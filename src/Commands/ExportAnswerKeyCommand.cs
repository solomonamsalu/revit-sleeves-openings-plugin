using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Read-only: writes every sleeve/opening in a finished model to CSV (plus levels, grids and CAD/Revit links with
    /// their transforms) so automatic placement from the engineer's DWG/PDF can be scored against what the drafter did.
    /// System and size come from the Adopt rules; anything that looks like a sleeve/opening but is not matched is
    /// still written with its raw size parameters. Nothing in the model is changed.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportAnswerKeyCommand : IExternalCommand
    {
        private static readonly Regex LooksLikeOpening = new Regex("sleeve|opening|penetrat", RegexOptions.IgnoreCase);
        private static readonly Regex SizeParamName = new Regex("width|length|diameter|radius|size|depth|height", RegexOptions.IgnoreCase);

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, rules, state);

                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SleevesOpenings", "answer-keys", Safe(doc.Title) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm"));
                Directory.CreateDirectory(dir);

                var toShared = doc.ActiveProjectLocation.GetTotalTransform().Inverse;
                int sleeves = WriteSleeves(doc, rules, state, toShared, Path.Combine(dir, "sleeves.csv"));
                WriteLevels(levels, Path.Combine(dir, "levels.csv"));
                WriteGrids(doc, toShared, Path.Combine(dir, "grids.csv"));
                int links = WriteLinks(doc, Path.Combine(dir, "links.csv"));

                App.Log($"Answer key: {sleeves} sleeves/openings, {links} links -> {dir}");
                var td = new TaskDialog("Export Answer Key")
                {
                    MainInstruction = $"{sleeves} sleeve(s)/opening(s) exported",
                    MainContent = $"Folder:\n{dir}\n\nsleeves.csv, levels.csv, grids.csv, links.csv. The model was not changed.",
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open folder");
                if (td.Show() == TaskDialogResult.CommandLink1) System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Export answer key failed: " + ex);
                return Result.Failed;
            }
        }

        // ------------------------------------------------------------------ sleeves / openings

        private static int WriteSleeves(Document doc, Rules.RuleSet rules, ProjectState state, Transform toShared, string path)
        {
            // Adopt decides system/size/riser for matched families, including ones already stamped.
            var adopted = new Adopter(doc, rules, state).Scan(includeStamped: true).Candidates
                .ToDictionary(c => c.Instance.Id, c => c);
            var toggleMap = new FamilyMapEntry { SizeTogglePattern = rules.Families.Values.FirstOrDefault(f => f.SizeToggles != null)?.SizeTogglePattern };

            var sb = new StringBuilder();
            sb.AppendLine("ElementId,Source,Category,Family,Type,Level,LevelElevFt,Label,Comments,Mark,System,SystemSource,Riser," +
                          "DiameterIn,WidthIn,LengthIn,XFt,YFt,ZFt,SharedXFt,SharedYFt,RotationDeg,Host,TogglesOn,Stamped,SizeParams");
            int n = 0;

            foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                var sym = inst.Symbol;
                if (sym?.Family == null) continue;
                adopted.TryGetValue(inst.Id, out var c);
                var stamp = OpeningData.Read(inst);
                if (c == null && stamp == null && !LooksLikeOpening.IsMatch(sym.Family.Name) && !LooksLikeOpening.IsMatch(sym.Name)) continue;

                var lp = inst.Location as LocationPoint;
                var pt = lp?.Point ?? Center(inst.get_BoundingBox(null));
                if (pt == null) continue;
                var level = c?.Level ?? RiserIndex.LevelOf(doc, inst);
                var shared = toShared.OfPoint(pt);

                string toggles = "";
                try { toggles = string.Join(" ", SizeToggles.Find(inst, toggleMap).Where(t => t.Param.AsInteger() == 1).Select(t => $"{t.Prefix} {t.Size}")); } catch { }

                sb.AppendLine(Row(
                    inst.Id.ToString(), "FamilyInstance", inst.Category?.Name, sym.Family.Name, sym.Name,
                    level?.Name, F(level?.Elevation),
                    c?.Label ?? stamp?.Label, Str(inst, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), Str(inst, BuiltInParameter.ALL_MODEL_MARK),
                    c?.System ?? stamp?.System, c != null ? c.SystemSource : stamp != null ? "stamp" : "", c?.Riser ?? stamp?.Riser,
                    F(c?.Diameter ?? stamp?.Diameter), F(c?.Width ?? stamp?.Width), F(c?.Length ?? stamp?.Length),
                    F(pt.X), F(pt.Y), F(pt.Z), F(shared.X), F(shared.Y), F(lp != null ? lp.Rotation * 180 / Math.PI : (double?)null),
                    inst.Host?.Category?.Name, toggles, stamp != null ? "yes" : "", SizeParams(inst)));
                n++;
            }

            // Native openings (shaft, floor, wall openings) — chutes and duct shafts are often drawn this way.
            foreach (var op in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
            {
                var bb = op.get_BoundingBox(null);
                var pt = Center(bb);
                if (pt == null) continue;
                var level = doc.GetElement(op.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level
                         ?? doc.GetElement(op.LevelId) as Level;
                var shared = toShared.OfPoint(pt);
                sb.AppendLine(Row(
                    op.Id.ToString(), "Opening", op.Category?.Name, "(native opening)", op.Name, level?.Name, F(level?.Elevation),
                    "", Str(op, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), Str(op, BuiltInParameter.ALL_MODEL_MARK),
                    "", "", "", "", F(Units.FeetToInches(bb.Max.X - bb.Min.X)), F(Units.FeetToInches(bb.Max.Y - bb.Min.Y)),
                    F(pt.X), F(pt.Y), F(pt.Z), F(shared.X), F(shared.Y), "", op.Host?.Category?.Name, "", "", ""));
                n++;
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return n;
        }

        /// <summary>Every length parameter whose name looks like a size, in inches: "Width=12;Sleeve Length=10".</summary>
        private static string SizeParams(FamilyInstance inst)
        {
            var parts = new List<string>();
            foreach (var e in new Element[] { inst, inst.Symbol })
                foreach (Parameter p in e.Parameters)
                {
                    if (p.StorageType != StorageType.Double || !p.HasValue || !SizeParamName.IsMatch(p.Definition.Name)) continue;
                    try
                    {
                        if (p.Definition.GetDataType() != SpecTypeId.Length) continue;
                    }
                    catch { continue; }
                    parts.Add($"{p.Definition.Name}={F(Units.FeetToInches(p.AsDouble()))}");
                }
            return string.Join(";", parts.Distinct());
        }

        // ------------------------------------------------------------------ context for alignment

        private static void WriteLevels(LevelMap levels, string path)
        {
            var sb = new StringBuilder("Name,ElevationFt,Role\n");
            foreach (var l in levels.Everything) sb.AppendLine(Row(l.Name, F(l.Elevation), l.Role.ToString()));
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void WriteGrids(Document doc, Transform toShared, string path)
        {
            var sb = new StringBuilder("Name,X1Ft,Y1Ft,X2Ft,Y2Ft,SharedX1Ft,SharedY1Ft,SharedX2Ft,SharedY2Ft\n");
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(g.Curve is Line ln)) continue;
                XYZ a = ln.GetEndPoint(0), b = ln.GetEndPoint(1), sa = toShared.OfPoint(a), sbp = toShared.OfPoint(b);
                sb.AppendLine(Row(g.Name, F(a.X), F(a.Y), F(b.X), F(b.Y), F(sa.X), F(sa.Y), F(sbp.X), F(sbp.Y)));
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>CAD and Revit links with the transform that takes link coordinates into this model (DWG units → feet is the link's own scale).</summary>
        private static int WriteLinks(Document doc, string path)
        {
            var sb = new StringBuilder("Kind,Name,Path,View,OriginXFt,OriginYFt,OriginZFt,RotationDeg,Scale\n");
            int n = 0;
            foreach (var imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var t = imp.GetTotalTransform();
                string file = "";
                try
                {
                    var type = doc.GetElement(imp.GetTypeId());
                    if (type != null && type.IsExternalFileReference())
                        file = ModelPathUtils.ConvertModelPathToUserVisiblePath(type.GetExternalFileReference().GetAbsolutePath());
                }
                catch { }
                string view = imp.OwnerViewId != ElementId.InvalidElementId ? doc.GetElement(imp.OwnerViewId)?.Name : "";
                sb.AppendLine(Row(imp.IsLinked ? "CAD link" : "CAD import", imp.Category?.Name ?? imp.Name, file, view,
                    F(t.Origin.X), F(t.Origin.Y), F(t.Origin.Z), F(Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI), F(t.Scale)));
                n++;
            }
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var t = li.GetTotalTransform();
                sb.AppendLine(Row("Revit link", li.Name, "", "", F(t.Origin.X), F(t.Origin.Y), F(t.Origin.Z),
                    F(Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI), F(t.Scale)));
                n++;
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return n;
        }

        // ------------------------------------------------------------------ helpers

        private static XYZ Center(BoundingBoxXYZ bb) => bb == null ? null : (bb.Min + bb.Max) / 2;

        private static string Str(Element e, BuiltInParameter bip) => e.get_Parameter(bip)?.AsString();

        private static string F(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";

        private static string Row(params string[] cells) =>
            string.Join(",", cells.Select(s => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\""));

        private static string Safe(string s) => Regex.Replace(s ?? "model", @"[^\w\- ]+", "_");
    }
}
