using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Cad = ACadSharp.Entities;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using SleevesOpenings.Automation;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Placement;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Extract CW/HW (Extract panel): tick floors and list everything the model shows of the domestic water (cold, hot,
    /// hot water return), to learn where the water sleeves go: Revit pipes of this model and its links (through the slab,
    /// or on the floor), the water connectors of the plumbing fixtures, water heaters / meters / boosters, and the circles
    /// on the water layers of the floor's DWGs with the words written next to them ("HW DROP / CW DROP", 3/4" CW); and
    /// each wet row's hot + cold pair by the office rule (plumbing.water). From the list: zoom, mark in the plan, export
    /// CSV, place the office rule's sleeves.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExtractWaterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = data.Application.ActiveUIDocument;
                if (uidoc?.Document == null) { TaskDialog.Show("Extract CW/HW", "Open a model first."); return Result.Cancelled; }
                var start = (uidoc.Document.ActiveView as ViewPlan)?.GenLevel;
                using (var form = new UI.ExtractWaterForm(uidoc, start))
                    form.ShowDialog(UI.RevitWindow.Instance);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                App.Log("Extract CW/HW failed: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Reads the ticked floors' domestic water (no transaction); marks it and places the office rule's pairs (own transactions).</summary>
    public static class WaterExtractor
    {
        /// <summary>Something stands on a level from this far (feet) below it up to this far below the next level.</summary>
        private const double LevelTolerance = 1.0;
        /// <summary>A pipe steeper than this (direction's Z) is vertical.</summary>
        private const double Vertical = 0.9;
        /// <summary>A DWG file circle within this many feet of a circle Revit shows is that circle (fitting the file's scale).</summary>
        private const double FitFeet = 0.25;
        /// <summary>Words of the DWG file this close (feet) to a water circle are its label.</summary>
        private const double LabelFeet = 3.0;

        private static readonly Regex Heater = new Regex(@"WATER.?HEATER|HEATER|BOILER|\bW\.?H\.?\b|\bWH-?\d|STORAGE.?TANK|BOOSTER|WATER.?METER|\bWM\b|BACKFLOW|\bBFP\b|\bRPZ\b|MIXING.?VALVE|\bTMV\b|RECIRC",
                                                     RegexOptions.IgnoreCase);
        private static readonly Regex WaterWords = new Regex(@"\b(D?CW|D?HW|HWR|HWC|COLD|HOT|WATER|DROP|RISER?|UP|DN|DOWN)\b|""", RegexOptions.IgnoreCase);

        public class Item
        {
            public Floor Floor;
            /// <summary>Where it was read: "Revit pipe", "fixture connector", "equipment", "DWG circle".</summary>
            public string Source;
            /// <summary>What it is: "riser through the slab", "pipe on the floor", "cold supply"...</summary>
            public string Kind;
            /// <summary>ColdWater, HotWater, HotWaterReturn, "?" (water, not known which).</summary>
            public string System;
            /// <summary>Pipe or circle size, inches (0 = not known).</summary>
            public double Size;
            public double X, Y;
            /// <summary>The element, family, layer or file it comes from.</summary>
            public string From;
            /// <summary>The words next to it (DWG), its system type name (Revit).</summary>
            public string Label;
            public string Grid;
            public List<string> Notes = new List<string>();
            /// <summary>The element in this model (null for links and DWGs).</summary>
            public ElementId Id;
            /// <summary>Feet, a pipe on the floor: its other end (for Mark).</summary>
            public XYZ End;
            /// <summary>Laid out by the office rule (plumbing.water): what Place puts in; null = read from the model.</summary>
            public VentExtractor.Item Rule;
        }

        public class Floor
        {
            public Level Level;
            public List<string> Notes = new List<string>();
        }

        public class Run
        {
            public List<Floor> Floors = new List<Floor>();
            public List<Item> Items = new List<Item>();
        }

        public static string Short(string system)
        {
            switch (system)
            {
                case "ColdWater": return "CW";
                case "HotWater": return "HW";
                case "HotWaterReturn": return "HWR";
                default: return "W?";
            }
        }

        /// <summary>Which water a name says (a layer, a system type, a classification); null = no water.</summary>
        public static string SystemOf(string text)
        {
            string s = (text ?? "").ToUpperInvariant();
            if (Regex.IsMatch(s, @"RETURN|RECIRC|(^|[^A-Z])D?HW(R|C)([^A-Z]|$)")) return "HotWaterReturn";
            if (Regex.IsMatch(s, @"HOT|(^|[^A-Z])D?HWS?([^A-Z]|$)|HOTW")) return "HotWater";
            if (Regex.IsMatch(s, @"COLD|(^|[^A-Z])D?CWS?([^A-Z]|$)|COLW")) return "ColdWater";
            if (Regex.IsMatch(s, @"DOMESTIC|POTABLE|WATER")) return "?";
            return null;
        }

        public static Run Extract(Document doc, List<Level> levels, PlumbingRules rules)
        {
            var run = new Run();
            var all = FixtureExtractor.Levels(doc);
            var grids = FixtureExtractor.Grids(doc);
            var files = new Dictionary<string, FileWater>(StringComparer.OrdinalIgnoreCase);
            var waterLayers = string.IsNullOrWhiteSpace(rules.DwgWaterLayers) ? null : new Regex(rules.DwgWaterLayers, RegexOptions.IgnoreCase);

            // the office rule: each wet row's hot + cold pair, laid out with the rows' vents (Vent Placer's rows, top down)
            VentExtractor.Run laid = null;
            try { laid = VentExtractor.Extract(doc, levels, rules, water: true); }
            catch (Exception ex) { App.Log("Extract CW/HW rule: " + ex); }

            foreach (var level in levels.OrderByDescending(l => l.ProjectElevation))
            {
                var floor = new Floor { Level = level };
                run.Floors.Add(floor);
                var items = new List<Item>();
                var above = all.FirstOrDefault(l => l.ProjectElevation > level.ProjectElevation + 0.01);
                double z = level.ProjectElevation, lo = z - LevelTolerance, hi = (above?.ProjectElevation ?? double.MaxValue) - LevelTolerance;

                void Read(Document d, Transform t, string source, bool own)
                {
                    try { Pipes(d, t, source, own, floor, z, lo, hi, items); } catch (Exception ex) { floor.Notes.Add($"{source}: pipes not read ({ex.Message})"); }
                    try { Connectors(d, t, source, own, floor, lo, hi, items); } catch (Exception ex) { floor.Notes.Add($"{source}: fixtures not read ({ex.Message})"); }
                    try { Equipment(d, t, source, own, floor, lo, hi, items); } catch (Exception ex) { floor.Notes.Add($"{source}: equipment not read ({ex.Message})"); }
                }
                Read(doc, Transform.Identity, "this model", true);
                foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linked = link.GetLinkDocument();
                    if (linked != null) Read(linked, link.GetTotalTransform(), $"link '{linked.Title}'", false);
                    else floor.Notes.Add($"Revit link '{link.Name}' is not loaded: not read");
                }

                // the floor's DWGs: circles on the water layers, labelled from the file
                try
                {
                    var plans = DwgFixtureReader.Read(doc, rules, level.Name);
                    if (plans.Count == 0) floor.Notes.Add("no DWG placed on this level");
                    foreach (var p in plans)
                    {
                        var layers = waterLayers == null ? new List<KeyValuePair<string, int>>() : p.Layers.Where(kv => waterLayers.IsMatch(kv.Key)).ToList();
                        floor.Notes.Add($"DWG {p.Source}: " + (layers.Count == 0 ? "no water layers" :
                                        "water layers " + string.Join(", ", layers.Select(kv => $"{kv.Key} ({kv.Value} lines)")) + $", {p.Water.Count} circle(s)"));
                        if (p.Water.Count == 0) continue;
                        FileWater file = null;
                        if (p.FilePath != null && !files.TryGetValue(p.FilePath, out file))
                            files[p.FilePath] = file = FileWater.Read(p.FilePath, waterLayers);
                        var labels = file?.Labels(p);
                        if (file?.Error != null) floor.Notes.Add($"DWG {p.Source}: its file could not be read for the words ({file.Error})");
                        else if (p.FilePath == null) floor.Notes.Add($"DWG {p.Source}: its file was not found: circles without their words");
                        else if (labels == null) floor.Notes.Add($"DWG {p.Source}: the file does not line up with the drawing Revit shows: circles without their words");
                        foreach (var c in p.Water)
                        {
                            double x = c.X / 12, y = c.Y / 12;
                            var it = new Item
                            {
                                Floor = floor, Source = "DWG circle", Kind = "circle on a water layer", System = SystemOf(c.Layer) ?? "?",
                                Size = Math.Round(c.Diameter, 2), X = x, Y = y, From = $"{p.Source} / {c.Layer}"
                            };
                            if (labels != null)
                            {
                                var near = labels.Where(l => Math.Abs(l.X - x) <= LabelFeet && Math.Abs(l.Y - y) <= LabelFeet)
                                                 .OrderBy(l => Math.Pow(l.X - x, 2) + Math.Pow(l.Y - y, 2)).Select(l => l.Text).Distinct().Take(4).ToList();
                                it.Label = string.Join(" | ", near);
                                if (near.Any(n => Regex.IsMatch(n, @"\bDROP\b|\bDN\b|DOWN", RegexOptions.IgnoreCase))) it.Kind = "drop (from above)";
                                else if (near.Any(n => Regex.IsMatch(n, @"\bUP\b|RISE", RegexOptions.IgnoreCase))) it.Kind = "riser (up)";
                            }
                            items.Add(it);
                        }
                    }
                }
                catch (Exception ex) { floor.Notes.Add($"DWGs not read ({ex.Message})"); App.Log("Extract CW/HW DWG: " + ex); }

                if (laid == null) floor.Notes.Add("the office rule could not lay out the rows (see the log)");
                else
                {
                    var lf = laid.Floors.FirstOrDefault(f => f.Level.Id == level.Id);
                    if (lf != null)
                    {
                        if (lf.OnGrade) floor.Notes.Add($"{level.Name} is the lowest level: on grade, no water pairs (plumbing.skipLowestSlab)");
                        foreach (var r in lf.Rows)
                            foreach (var n in r.Notes.Where(n => n.StartsWith("hot / cold water")))
                                floor.Notes.Add($"{r.Id} ({r.Kind}): {n}");
                    }
                    foreach (var li in laid.Items.Where(i => i.Floor.Level.Id == level.Id && (i.Vent.System == "HotWater" || i.Vent.System == "ColdWater")))
                        items.Add(new Item
                        {
                            Floor = floor, Source = "office rule", Kind = li.Vent.Kind, System = li.Vent.System, Size = li.Vent.Size, X = li.Vent.X, Y = li.Vent.Y,
                            From = $"row {li.Row.Id} ({li.Row.Kind}): {li.Vent.Name}, serves {string.Join(", ", li.Row.Serves.Select(f => f.Code))}",
                            Label = li.Vent.How, Notes = li.Vent.Notes.ToList(), Rule = li
                        });
                }

                foreach (var it in items) it.Grid = FixtureExtractor.GridRef(grids, it.X, it.Y);
                run.Items.AddRange(items);
                string Count(string source) => $"{items.Count(i => i.Source == source)}";
                floor.Notes.Insert(0, $"{items.Count} water item(s): {Count("Revit pipe")} Revit pipe(s), {Count("fixture connector")} fixture connector(s), " +
                                      $"{Count("equipment")} equipment, {Count("DWG circle")} DWG circle(s); " +
                                      $"{Count("office rule")} sleeve(s) by the office rule");
                App.Log($"Extract CW/HW {level.Name}: " + string.Join("; ", floor.Notes));
                foreach (var g in items.GroupBy(i => i.Source + " / " + i.Kind + " / " + Short(i.System)))
                    App.Log($"  {g.Key}: {g.Count()}" + string.Concat(g.Take(5).Select(i => $"  ({i.X:0.00},{i.Y:0.00}) {i.Size:0.##}\" {i.Label}")));
            }
            return run;
        }

        /// <summary>Pipes of a water system: through this floor's slab (risers, drops) or on the floor (branches).</summary>
        private static void Pipes(Document d, Transform t, string source, bool own, Floor floor, double z, double lo, double hi, List<Item> items)
        {
            foreach (var pipe in new FilteredElementCollector(d).OfClass(typeof(Pipe)).Cast<Pipe>())
            {
                string cls = pipe.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
                string type = d.GetElement(pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId)?.Name ?? "";
                string system = SystemOf(cls + " " + type);
                if (system == null) continue;
                if (!(pipe.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                var a = t.OfPoint(line.GetEndPoint(0)); var b = t.OfPoint(line.GetEndPoint(1));
                var dir = b - a;
                if (dir.GetLength() < 1e-6) continue;
                double size = Math.Round(pipe.Diameter * 12, 2);
                string label = string.IsNullOrEmpty(type) ? cls : type;
                if (Math.Abs(dir.Normalize().Z) >= Vertical)
                {
                    if (Math.Min(a.Z, b.Z) < z - 0.1 && Math.Max(a.Z, b.Z) > z + 0.1)
                    {
                        double k = (z - a.Z) / (b.Z - a.Z);
                        items.Add(new Item
                        {
                            Floor = floor, Source = "Revit pipe", Kind = "riser through the slab", System = system, Size = size,
                            X = a.X + (b.X - a.X) * k, Y = a.Y + (b.Y - a.Y) * k, From = $"{source}, pipe {pipe.Id}", Label = label, Id = own ? pipe.Id : null
                        });
                    }
                    else if (Math.Min(a.Z, b.Z) >= lo && Math.Min(a.Z, b.Z) < hi)
                        items.Add(new Item
                        {
                            Floor = floor, Source = "Revit pipe", Kind = "vertical pipe on the floor (not through the slab)", System = system, Size = size,
                            X = a.X, Y = a.Y, From = $"{source}, pipe {pipe.Id}", Label = label, Id = own ? pipe.Id : null,
                            Notes = { $"from {FixtureExtractor.Ft(Math.Min(a.Z, b.Z) - z)} to {FixtureExtractor.Ft(Math.Max(a.Z, b.Z) - z)} above the level" }
                        });
                    continue;
                }
                double mz = (a.Z + b.Z) / 2;
                if (mz < lo || mz >= hi) continue;
                items.Add(new Item
                {
                    Floor = floor, Source = "Revit pipe", Kind = "pipe on the floor", System = system, Size = size,
                    X = a.X, Y = a.Y, End = new XYZ(b.X, b.Y, 0), From = $"{source}, pipe {pipe.Id}", Label = label, Id = own ? pipe.Id : null,
                    Notes = { $"{FixtureExtractor.Ft(dir.GetLength())} long, at {FixtureExtractor.Ft(mz - z)} above the level, to ({FixtureExtractor.Ft(b.X)}, {FixtureExtractor.Ft(b.Y)})" }
                });
            }
        }

        /// <summary>The cold, hot and return water connectors of the plumbing fixtures on the floor.</summary>
        private static void Connectors(Document d, Transform t, string source, bool own, Floor floor, double lo, double hi, List<Item> items)
        {
            foreach (var fi in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_PlumbingFixtures).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                string name = $"{fi.Symbol?.Family?.Name} : {fi.Symbol?.Name}".Trim();
                if (OpeningData.Read(fi) != null || name.IndexOf("sleeve", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var manager = fi.MEPModel?.ConnectorManager;
                if (manager == null) continue;
                foreach (Connector cn in manager.Connectors)
                {
                    if (cn.Domain != Domain.DomainPiping) continue;
                    string system;
                    switch (cn.PipeSystemType)
                    {
                        case PipeSystemType.DomesticColdWater: system = "ColdWater"; break;
                        case PipeSystemType.DomesticHotWater: system = "HotWater"; break;
                        default: system = SystemOf(cn.PipeSystemType.ToString()); break;
                    }
                    if (system == null) continue;
                    var pt = t.OfPoint(cn.Origin);
                    if (pt.Z < lo || pt.Z >= hi) continue;
                    double size = 0;
                    try { size = cn.Shape == ConnectorProfileType.Round ? Math.Round(cn.Radius * 24, 2) : 0; } catch (Exception) { }
                    items.Add(new Item
                    {
                        Floor = floor, Source = "fixture connector", Kind = system == "HotWater" ? "hot supply" : system == "ColdWater" ? "cold supply" : "water connector",
                        System = system, Size = size, X = pt.X, Y = pt.Y, From = $"{source}, {name} ({fi.Id})", Id = own ? fi.Id : null,
                        Label = cn.IsConnected ? "connected to a pipe" : "not connected",
                        Notes = { $"at {FixtureExtractor.Ft(pt.Z - floor.Level.ProjectElevation)} above the level" }
                    });
                }
            }
        }

        /// <summary>Water heaters, meters, boosters, backflow preventers... on the floor, by their family or type name.</summary>
        private static void Equipment(Document d, Transform t, string source, bool own, Floor floor, double lo, double hi, List<Item> items)
        {
            var cats = new List<BuiltInCategory> { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PipeAccessory };
            if (Enum.TryParse("OST_PlumbingEquipment", out BuiltInCategory plumbing)) cats.Add(plumbing);
            var seen = new HashSet<ElementId>();
            foreach (var cat in cats)
                foreach (var fi in new FilteredElementCollector(d).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    if (!seen.Add(fi.Id)) continue;
                    string name = $"{fi.Symbol?.Family?.Name} : {fi.Symbol?.Name}".Trim();
                    if (!Heater.IsMatch(name) || name.IndexOf("sleeve", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var box = fi.get_BoundingBox(null);
                    var local = (fi.Location as LocationPoint)?.Point ?? (box == null ? null : (box.Min + box.Max) / 2);
                    if (local == null) continue;
                    var pt = t.OfPoint(local);
                    if (pt.Z < lo || pt.Z >= hi) continue;
                    items.Add(new Item
                    {
                        Floor = floor, Source = "equipment", Kind = fi.Category?.Name ?? "equipment", System = SystemOf(name) ?? "?",
                        X = pt.X, Y = pt.Y, From = $"{source}, {name} ({fi.Id})", Label = name, Id = own ? fi.Id : null
                    });
                }
        }

        /// <summary>A DWG file's water circles and words, in its own units (read once per file).</summary>
        private class FileWater
        {
            public string Error;
            public double FeetPerUnit = 1.0 / 12;
            public List<(double X, double Y)> Circles = new List<(double X, double Y)>();
            public List<(double X, double Y, string Text)> Words = new List<(double X, double Y, string Text)>();

            public static FileWater Read(string path, Regex waterLayers)
            {
                var f = new FileWater();
                try
                {
                    var cad = DwgSheetIndex.Open(path);
                    f.FeetPerUnit = DwgFixtureBlocks.InchesPerUnit(cad.Header.InsUnits.ToString()) / 12;
                    foreach (var e in cad.Entities)
                    {
                        string layer = e.Layer?.Name ?? "";
                        switch (e)
                        {
                            case Cad.Circle c when waterLayers != null && waterLayers.IsMatch(layer): f.Circles.Add((c.Center.X, c.Center.Y)); break;
                            case Cad.TextEntity tx: f.Add(tx.InsertPoint.X, tx.InsertPoint.Y, tx.Value); break;
                            case Cad.MText mt: f.Add(mt.InsertPoint.X, mt.InsertPoint.Y, DwgRiserReader.MTextLines(mt.Value).Replace("\n", " / ")); break;
                        }
                    }
                    App.Log($"Extract CW/HW: {path}: {f.Circles.Count} water circle(s), {f.Words.Count} water word(s), units {cad.Header.InsUnits}");
                }
                catch (Exception ex) { f.Error = ex.Message; App.Log($"Extract CW/HW: {path} not read: {ex.Message}"); }
                return f;
            }

            private void Add(double x, double y, string text)
            {
                text = (text ?? "").Trim();
                if (text.Length > 0 && text.Length < 80 && WaterWords.IsMatch(text)) Words.Add((x, y, text));
            }

            /// <summary>The file's words in model feet, at the scale under which its water circles land on those Revit shows; null = it does not line up.</summary>
            public List<(double X, double Y, string Text)> Labels(DwgFixtureReader.Plan plan)
            {
                if (Error != null || plan.Transform == null || Circles.Count == 0) return null;
                var shown = plan.Water.Select(c => (X: c.X / 12, Y: c.Y / 12)).ToList();
                var scales = new[] { FeetPerUnit * plan.ImportScale, FeetPerUnit, plan.ImportScale, 1.0 / 12, 1.0 }.Where(s => s > 0).Distinct().ToList();
                double best = -1, scale = scales[0];
                var sample = Circles.Take(400).ToList();
                foreach (var s in scales)
                {
                    int hit = sample.Count(c =>
                    {
                        var q = plan.Transform.OfPoint(new XYZ(c.X * s, c.Y * s, 0));
                        return shown.Any(w => Math.Abs(w.X - q.X) <= FitFeet && Math.Abs(w.Y - q.Y) <= FitFeet);
                    });
                    double fit = (double)hit / sample.Count;
                    if (fit > best) { best = fit; scale = s; }
                }
                App.Log($"Extract CW/HW: {plan.Source}: {best:P0} of the file's water circles land on those Revit shows at {scale * 12:0.####} in/unit");
                if (best < 0.5) return null;
                return Words.Select(w => { var q = plan.Transform.OfPoint(new XYZ(w.X * scale, w.Y * scale, 0)); return (q.X, q.Y, w.Text); }).ToList();
            }
        }

        /// <summary>The office rule's water sleeves through the Vent Placer's placer (office families, structure checks), flagged for a check.</summary>
        public static string Place(Document doc, List<Item> items)
        {
            var rule = items.Where(i => i.Rule != null).Select(i => i.Rule).ToList();
            return rule.Count == 0 ? "Nothing to place: only the office rule's rows are placed (the rest is what the model already shows)." : VentExtractor.Place(doc, rule);
        }

        /// <summary>Detail lines: a circle at each item (its size, at least 3"), a line along each pipe on the floor, CW / HW / HWR as text.</summary>
        public static List<ElementId> Mark(Document doc, ViewPlan view, Level level, IEnumerable<Item> items)
        {
            var ids = new List<ElementId>();
            using (var t = new Transaction(doc, "Extract CW/HW: mark (undo to remove)"))
            {
                t.Start();
                double z = level.ProjectElevation;
                var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                foreach (var i in items)
                {
                    var c = new XYZ(i.X, i.Y, z);
                    ids.Add(doc.Create.NewDetailCurve(view, Arc.Create(c, Math.Max(i.Size, 3) / 24, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)).Id);
                    if (i.End != null && c.DistanceTo(new XYZ(i.End.X, i.End.Y, z)) > 0.05)
                        ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(c, new XYZ(i.End.X, i.End.Y, z))).Id);
                    if (textType != ElementId.InvalidElementId)
                        ids.Add(TextNote.Create(doc, view.Id, new XYZ(i.X + 0.2, i.Y + 0.2, z), Short(i.System), textType).Id);
                }
                t.Commit();
            }
            return ids;
        }

        public static void SaveCsv(string path, IEnumerable<Item> items, Run run)
        {
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("Level,Source,Kind,System,Size (in),Location (grids),X (ft),Y (ft),From,Label,Notes\n");
            foreach (var i in items)
                csv.AppendLine(string.Join(",", Q(i.Floor.Level.Name), Q(i.Source), Q(i.Kind), Q(Short(i.System)), i.Size.ToString("0.##"), Q(i.Grid),
                                           i.X.ToString("0.###"), i.Y.ToString("0.###"), Q(i.From), Q(i.Label), Q(string.Join("; ", i.Notes))));
            foreach (var f in run.Floors)
                foreach (var n in f.Notes)
                    csv.AppendLine(string.Join(",", Q(f.Level.Name), Q("note"), Q(""), Q(""), "", Q(""), "", "", Q(""), Q(""), Q(n)));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }

        public static string DefaultCsv(Document doc)
        {
            string Safe(string s) => string.Concat((s ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "water");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Safe(doc.Title) + ".csv");
        }
    }
}
