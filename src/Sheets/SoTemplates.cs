using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Sheets
{
    /// <summary>One S&amp;O template: an .rvt holding one S&amp;O sheet (title block, a Sleeves plan, legend, schedule, floor label).</summary>
    public class SoTemplate
    {
        public const string BuiltIn = "Built-in", Office = "Office", Project = "This project";

        public string Name, Path, Where;

        public override string ToString() => $"{Name} ({Where})";
    }

    /// <summary>
    /// S&amp;O templates: what the set starts from when the model has no S&amp;O sheet to copy. Kept in three folders, looked in
    /// this order by name: the project's "SO Templates" folder (next to the .rvt: everyone opening the project has it),
    /// the office's (%APPDATA%\SleevesOpenings\Templates) and the add-in's own (Templates next to the DLL: installed with it).
    /// The first sheet is built from the template (its title block, legend and schedule copied into the model); the
    /// others are then copied from that sheet as usual.
    /// </summary>
    public static class SoTemplates
    {
        /// <summary>The add-in's own template, used when no template is chosen.</summary>
        public const string DefaultName = "SO Template";

        public static string BuiltInFolder => Path.Combine(RuleLoader.AddinDir, "Templates");

        public static string OfficeFolder => Path.Combine(Path.GetDirectoryName(RuleLoader.UserRulesPath), "Templates");

        public static string ProjectFolder(string modelPath) =>
            string.IsNullOrEmpty(modelPath) || !Path.IsPathRooted(modelPath) ? null : Path.Combine(Path.GetDirectoryName(modelPath), "SO Templates");

        /// <summary>Every template found: this project's, the office's, the add-in's.</summary>
        public static List<SoTemplate> List(string modelPath)
        {
            var list = new List<SoTemplate>();
            void From(string folder, string where)
            {
                try
                {
                    if (folder == null || !Directory.Exists(folder)) return;
                    foreach (var f in Directory.GetFiles(folder, "*.rvt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        if (!IsBackup(f)) list.Add(new SoTemplate { Name = Path.GetFileNameWithoutExtension(f), Path = f, Where = where });
                }
                catch (Exception ex) { App.Log($"S&O templates: {folder}: {ex.Message}"); }
            }
            From(ProjectFolder(modelPath), SoTemplate.Project);
            From(OfficeFolder, SoTemplate.Office);
            From(BuiltInFolder, SoTemplate.BuiltIn);
            return list;
        }

        /// <summary>Revit's backups: "name.0001.rvt".</summary>
        private static bool IsBackup(string file) => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"\.\d{4}\.rvt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// The template a setting names: a path (relative = from the model's folder) or a name (project, office, add-in
        /// folders in that order). Null setting = the add-in's "SO Template", else the only office template there is.
        /// </summary>
        public static SoTemplate Resolve(string setting, string modelPath, out string problem)
        {
            problem = null;
            var all = List(modelPath);
            if (!string.IsNullOrWhiteSpace(setting))
            {
                var s = setting.Trim();
                if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) || s.Contains("\\") || s.Contains("/"))
                {
                    var path = Path.IsPathRooted(s) ? s : (ProjectFolder(modelPath) != null ? Path.Combine(Path.GetDirectoryName(modelPath), s) : null);
                    if (path != null && File.Exists(path)) return new SoTemplate { Name = Path.GetFileNameWithoutExtension(path), Path = path, Where = "File" };
                    problem = $"No S&O sheet in this model, and the S&O template '{s}' was not found.";
                    return null;
                }
                var named = all.FirstOrDefault(t => t.Name.Equals(s, StringComparison.OrdinalIgnoreCase));
                if (named != null) return named;
                problem = $"No S&O sheet in this model, and the S&O template '{s}' is in none of the template folders (S&O Set > Templates).";
                return null;
            }
            var found = all.FirstOrDefault(t => t.Where == SoTemplate.BuiltIn && t.Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
                        ?? (all.Count(t => t.Where == SoTemplate.Office) == 1 ? all.First(t => t.Where == SoTemplate.Office) : null);
            if (found != null) return found;
            problem = all.Count == 0
                ? "No S&O sheet in this model and no S&O template. Make one sheet by hand once (as the office's SL101), or add a template in the Templates tab."
                : "No S&O sheet in this model and no template chosen: pick one in the Templates tab.";
            return null;
        }

        // ---------------------------------------------------------------- the first sheet from the template

        /// <summary>
        /// Builds the first new floor's sheet from the plan's template, then plans again: that sheet is now the pattern
        /// the other floors copy. Opens its own transaction. Problems go to <paramref name="problems"/>.
        /// </summary>
        public static SoSetPlan Seed(Document doc, RuleSet rules, SoSheetRules cfg, LevelMap levels, SoSetPlan plan, List<string> problems)
        {
            var floor = plan.Floors.FirstOrDefault(f => f.FromTemplate && f.Sheet == null);
            if (plan.Pattern != null || plan.Template == null || floor == null) return plan;
            string suffix = rules.SleeveViews?.NameSuffix ?? " Sleeves";
            Document src = null;
            bool opened = false;
            try
            {
                src = Open(doc.Application, plan.Template.Path, out opened);
                var templateProblems = new List<string>();
                var sp = SoSheets.FindPattern(src, AnySheet(cfg), suffix, templateProblems);
                if (sp == null)
                {
                    problems.Add($"S&O template '{plan.Template.Name}' holds no sheet with a '<level>{suffix}' view: nothing was made.");
                    return plan;
                }
                problems.AddRange(templateProblems.Select(p => $"Template '{plan.Template.Name}': {p}"));
                using (var t = new Transaction(doc, "Sleeves & Openings: S&O sheet from the template"))
                {
                    t.Start();
                    var sheet = BuildSheet(src, sp, doc, floor.View, floor.Level.Level, floor.Number, floor.Name, floor.ScheduleName, floor.Label, problems);
                    t.Commit();
                    App.Log($"S&O set: {sheet.SheetNumber} made from template {plan.Template.Path} ({sp.Describe()})");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"S&O template '{plan.Template.Name}' could not be used: {ex.Message}");
                App.Log("S&O set: template failed: " + ex);
                return plan;
            }
            finally
            {
                if (opened && src != null) try { src.Close(false); } catch (Exception ex) { App.Log("S&O set: template not closed: " + ex.Message); }
            }
            return SoSheets.Plan(doc, rules, cfg, levels);
        }

        /// <summary>The template's own sheet number does not matter: any sheet holding a Sleeves view is its pattern.</summary>
        private static SoSheetRules AnySheet(SoSheetRules cfg)
        {
            var c = SoProjectSettings.Apply(cfg, null);
            c.NumberPrefix = "";
            c.PatternSheet = null;
            return c;
        }

        /// <summary>The template, opened in the background (a central file detached); one already open is used as it is.</summary>
        private static Document Open(Application app, string path, out bool opened)
        {
            opened = false;
            var already = app.Documents.Cast<Document>().FirstOrDefault(d => !d.IsLinked && string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
            if (already != null) return already;
            if (!File.Exists(path)) throw new FileNotFoundException("The template file is missing", path);
            var options = new OpenOptions { Audit = false };
            try
            {
                var info = BasicFileInfo.Extract(path);
                if (int.TryParse(info.Format, out int year) && int.TryParse(app.VersionNumber, out int mine) && year > mine)
                    throw new InvalidOperationException($"the template was saved in Revit {year}; this is Revit {mine}. Save templates in the oldest Revit the office uses.");
                if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { App.Log("S&O template: file info not read: " + ex.Message); }
            var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options);
            opened = true;
            return doc;
        }

        /// <summary>
        /// One S&amp;O sheet in <paramref name="dst"/> built like the pattern sheet <paramref name="sp"/> of another model:
        /// its title block type (copied in when missing), the plan at the same spot, the legend, the schedule (copied, its
        /// Level filter set to this floor) and the floor label at the same place relative to the plan's crop region.
        /// Must run inside a transaction of <paramref name="dst"/>.
        /// </summary>
        internal static ViewSheet BuildSheet(Document src, SheetPattern sp, Document dst, ViewPlan view, Level level,
                                             string number, string name, string scheduleName, string label, List<string> problems)
        {
            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());

            // the title block type
            var tbType = ElementId.InvalidElementId;
            if (sp.TitleBlock != null)
            {
                tbType = SameType(dst, sp.TitleBlock.Symbol, BuiltInCategory.OST_TitleBlocks) ?? CopyIn(src, sp.TitleBlockType, dst, options, "title block", problems);
                if (dst.GetElement(tbType) is FamilySymbol sym && !sym.IsActive) sym.Activate();
            }
            var sheet = ViewSheet.Create(dst, tbType);
            sheet.SheetNumber = number;
            sheet.Name = name;
            foreach (var bip in new[] { BuiltInParameter.SHEET_DRAWN_BY, BuiltInParameter.SHEET_CHECKED_BY, BuiltInParameter.SHEET_DESIGNED_BY, BuiltInParameter.SHEET_APPROVED_BY, BuiltInParameter.SHEET_ISSUE_DATE })
                CopyValue(sp.Sheet.get_Parameter(bip), sheet.get_Parameter(bip));

            // the title block where the template's is, with its switches
            var tb = new FilteredElementCollector(dst, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault();
            if (tb != null && sp.TitleBlock != null)
            {
                var at = (tb.Location as LocationPoint)?.Point ?? XYZ.Zero;
                if (!at.IsAlmostEqualTo(sp.TitleBlockPoint)) ElementTransformUtils.MoveElement(dst, tb.Id, sp.TitleBlockPoint - at);
                foreach (Parameter p in sp.TitleBlock.Parameters)
                {
                    if (p.IsReadOnly || !(p.Definition is InternalDefinition def) || def.BuiltInParameter != BuiltInParameter.INVALID) continue;
                    CopyValue(p, tb.LookupParameter(p.Definition.Name));
                }
            }

            // the plan, set up like the template's (scale, detail, crop) unless a view template rules it
            if (view.ViewTemplateId == ElementId.InvalidElementId)
            {
                if (view.Scale != sp.Plan.Scale) view.Scale = sp.Plan.Scale;
                view.DetailLevel = sp.Plan.DetailLevel;
                view.CropBoxActive = sp.Plan.CropBoxActive;
                view.CropBoxVisible = sp.Plan.CropBoxVisible;
            }
            // the floor label: the same spot relative to the crop region (the building is another one); before the plan goes on
            // the sheet, so a template's empty plan is not empty
            if (sp.FloorLabel != null)
                try
                {
                    var target = SamePlace(sp.Plan.CropBox, view.CropBox, sp.FloorLabel.Coord);
                    var ids = ElementTransformUtils.CopyElements(sp.Plan, new List<ElementId> { sp.FloorLabel.Id }, view, Transform.Identity, options);
                    foreach (var id in ids)
                    {
                        if (!(dst.GetElement(id) is TextNote note)) continue;
                        if (!string.IsNullOrEmpty(label)) note.Text = label;
                        var move = target - note.Coord;
                        if (move.GetLength() > 1e-6) ElementTransformUtils.MoveElement(dst, id, new XYZ(move.X, move.Y, 0));
                    }
                }
                catch (Exception ex) { problems.Add($"{number}: the floor label was not copied ({ex.Message})"); }
            if (!Viewport.CanAddViewToSheet(dst, sheet.Id, view.Id)) throw new InvalidOperationException($"'{view.Name}' cannot go on a sheet (already placed elsewhere?)");
            var vp = Viewport.Create(dst, sheet.Id, view.Id, sp.PlanCenter);
            SetViewportType(src, sp.PlanViewportType, dst, vp, options, problems);
            vp.SetBoxCenter(sp.PlanCenter);

            // the legend
            if (sp.Legend != null)
            {
                var legend = Legend(src, sp.Legend, dst, options, problems);
                if (legend != null && Viewport.CanAddViewToSheet(dst, sheet.Id, legend.Id))
                {
                    var lvp = Viewport.Create(dst, sheet.Id, legend.Id, sp.LegendCenter);
                    SetViewportType(src, sp.LegendViewportType, dst, lvp, options, problems);
                    lvp.SetBoxCenter(sp.LegendCenter);
                }
            }

            // the schedule: this floor's copy of the template's
            if (sp.Schedule != null)
            {
                var schedule = new FilteredElementCollector(dst).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(s => s.Name == scheduleName);
                if (schedule == null)
                {
                    var id = CopyIn(src, sp.Schedule.Id, dst, options, "schedule", problems);
                    schedule = dst.GetElement(id) as ViewSchedule;
                    if (schedule != null)
                    {
                        if (!string.IsNullOrEmpty(scheduleName) && schedule.Name != scheduleName) schedule.Name = scheduleName;
                        if (!SoSheets.SetLevelFilter(schedule, level)) problems.Add($"{schedule.Name}: the template's schedule has no Level filter, so it lists every floor");
                    }
                }
                if (schedule != null) ScheduleSheetInstance.Create(dst, sheet.Id, schedule.Id, sp.SchedulePoint);
            }

            return sheet;
        }

        /// <summary>The point at the same place relative to the other crop box (fractions of its width and height).</summary>
        private static XYZ SamePlace(BoundingBoxXYZ from, BoundingBoxXYZ to, XYZ point)
        {
            var local = from.Transform.Inverse.OfPoint(point);
            double w = from.Max.X - from.Min.X, h = from.Max.Y - from.Min.Y;
            double u = w > 1e-9 ? (local.X - from.Min.X) / w : 0.5, v = h > 1e-9 ? (local.Y - from.Min.Y) / h : 0.5;
            var there = new XYZ(to.Min.X + u * (to.Max.X - to.Min.X), to.Min.Y + v * (to.Max.Y - to.Min.Y), 0);
            return to.Transform.OfPoint(there);
        }

        /// <summary>A type of the same family and name already in the model (the office's title block loaded before).</summary>
        private static ElementId SameType(Document dst, ElementType type, BuiltInCategory? category)
        {
            var collector = new FilteredElementCollector(dst).WhereElementIsElementType();
            if (category.HasValue) collector = collector.OfCategory(category.Value);
            return collector.Cast<ElementType>().FirstOrDefault(t => t.FamilyName == type.FamilyName && t.Name == type.Name)?.Id;
        }

        private static ElementId CopyIn(Document src, ElementId id, Document dst, CopyPasteOptions options, string what, List<string> problems)
        {
            try
            {
                var ids = ElementTransformUtils.CopyElements(src, new List<ElementId> { id }, dst, Transform.Identity, options);
                var srcElement = src.GetElement(id);
                // several come over (a family with its types): the one of the same kind and name
                var hit = ids.Select(dst.GetElement).FirstOrDefault(e => e != null && e.GetType() == srcElement.GetType() && e.Name == srcElement.Name)
                          ?? ids.Select(dst.GetElement).FirstOrDefault(e => e != null && e.GetType() == srcElement.GetType());
                if (hit != null) return hit.Id;
                if (srcElement is ElementType et && SameType(dst, et, null) is ElementId same) return same;
                problems.Add($"The template's {what} was not copied into this model");
            }
            catch (Exception ex) { problems.Add($"The template's {what} could not be copied: {ex.Message}"); }
            return ElementId.InvalidElementId;
        }

        private static void SetViewportType(Document src, ElementId srcType, Document dst, Viewport vp, CopyPasteOptions options, List<string> problems)
        {
            if (srcType == null || srcType == ElementId.InvalidElementId || !(src.GetElement(srcType) is ElementType type)) return;
            var id = SameType(dst, type, null) ?? CopyIn(src, srcType, dst, options, "viewport type", problems);
            if (id != ElementId.InvalidElementId && vp.GetTypeId() != id)
                try { vp.ChangeTypeId(id); } catch (Exception ex) { App.Log("S&O template: viewport type: " + ex.Message); }
        }

        /// <summary>
        /// The legend in <paramref name="dst"/>: one of the same name already there, else the template's copied in; when Revit
        /// will not copy the view, a legend of the model is duplicated and the template legend's contents copied into it.
        /// </summary>
        private static View Legend(Document src, View legend, Document dst, CopyPasteOptions options, List<string> problems)
        {
            var legends = new FilteredElementCollector(dst).OfClass(typeof(View)).Cast<View>().Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();
            var same = legends.FirstOrDefault(v => v.Name == legend.Name);
            if (same != null) return same;
            try
            {
                var ids = ElementTransformUtils.CopyElements(src, new List<ElementId> { legend.Id }, dst, Transform.Identity, options);
                var copied = ids.Select(dst.GetElement).OfType<View>().FirstOrDefault(v => v.ViewType == ViewType.Legend);
                if (copied != null) return copied;
            }
            catch (Exception ex) { App.Log($"S&O template: legend view not copied ({ex.Message}); copying its contents instead"); }

            var host = legends.FirstOrDefault();
            if (host == null)
            {
                problems.Add($"Legend '{legend.Name}' not copied: Revit cannot copy a legend into a model with no legend. Make any legend once (View > Legends), then run again.");
                return null;
            }
            try
            {
                var option = host.CanViewBeDuplicated(ViewDuplicateOption.Duplicate) ? ViewDuplicateOption.Duplicate : ViewDuplicateOption.WithDetailing;
                var made = dst.GetElement(host.Duplicate(option)) as View;
                var old = new FilteredElementCollector(dst, made.Id).WhereElementIsNotElementType().Where(e => e.OwnerViewId == made.Id).Select(e => e.Id).ToList();
                if (old.Count > 0) dst.Delete(old);
                made.Name = legend.Name;
                made.Scale = legend.Scale;
                var contents = new FilteredElementCollector(src, legend.Id).WhereElementIsNotElementType().Where(e => e.OwnerViewId == legend.Id && e.Category != null).Select(e => e.Id).ToList();
                if (contents.Count > 0) ElementTransformUtils.CopyElements(legend, contents, made, Transform.Identity, options);
                return made;
            }
            catch (Exception ex)
            {
                problems.Add($"Legend '{legend.Name}' not copied: {ex.Message}");
                return null;
            }
        }

        /// <summary>A text / number / yes-no value; element references mean nothing in another model.</summary>
        private static void CopyValue(Parameter from, Parameter to)
        {
            if (from == null || to == null || from.StorageType == StorageType.ElementId) return;
            SoSheets.CopyParam(from, to);
        }

        // ---------------------------------------------------------------- saving a template

        /// <summary>
        /// Saves the model's pattern sheet as a template: a new, empty Revit project holding that sheet (title block, an
        /// empty '<level> Sleeves' plan with the same crop size, the legend, the schedule, the floor label).
        /// Returns the problems (empty = everything came over).
        /// </summary>
        public static List<string> Save(Document doc, SheetPattern sp, string suffix, string path)
        {
            var problems = new List<string>();
            var app = doc.Application;
            Document tdoc = null;
            try
            {
                tdoc = app.NewProjectDocument(doc.DisplayUnitSystem == DisplayUnit.METRIC ? UnitSystem.Metric : UnitSystem.Imperial);
                using (var t = new Transaction(tdoc, "S&O template"))
                {
                    t.Start();
                    var level = new FilteredElementCollector(tdoc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault() ?? Level.Create(tdoc, 0);
                    var name = sp.Plan.GenLevel?.Name ?? "1st Floor";
                    try { level.Name = name; } catch { }
                    var vft = new FilteredElementCollector(tdoc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan)
                              ?? throw new InvalidOperationException("Revit's empty project has no floor plan type");
                    var view = ViewPlan.Create(tdoc, vft.Id, level.Id);
                    view.Name = level.Name + suffix;
                    view.Scale = sp.Plan.Scale;

                    // the crop region the same size as the model's, so the viewport and the label sit as on its sheet
                    var from = sp.Plan.CropBox;
                    double w = from.Max.X - from.Min.X, h = from.Max.Y - from.Min.Y;
                    var box = view.CropBox;
                    box.Min = new XYZ(-w / 2, -h / 2, box.Min.Z);
                    box.Max = new XYZ(w / 2, h / 2, box.Max.Z);
                    view.CropBox = box;
                    view.CropBoxActive = true;

                    BuildSheet(doc, sp, tdoc, view, level, sp.Sheet.SheetNumber, sp.Sheet.Name, sp.Schedule?.Name, sp.FloorLabel?.Text.Trim(), problems);
                    t.Commit();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                tdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, Compact = true });
                App.Log($"S&O template saved: {path} from {sp.Describe()}" + (problems.Count > 0 ? "; " + string.Join("; ", problems) : ""));
            }
            finally
            {
                if (tdoc != null) try { tdoc.Close(false); } catch (Exception ex) { App.Log("S&O template: not closed: " + ex.Message); }
            }
            return problems;
        }
    }

    /// <summary>A type already in the model wins over the template's (the office's title block stays the model's).</summary>
    internal class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }
}
