using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Sheets
{
    /// <summary>
    /// The pattern sheet (SL101 on 24 Skillman): what every S&amp;O sheet is built from. All of it is read from the
    /// model, nothing about the office's layout is in code.
    /// </summary>
    public class SheetPattern
    {
        public ViewSheet Sheet;
        public ElementId TitleBlockType = ElementId.InvalidElementId;
        public XYZ TitleBlockPoint = XYZ.Zero;
        public FamilyInstance TitleBlock;
        public ViewPlan Plan;
        public XYZ PlanCenter;
        public ElementId PlanViewportType = ElementId.InvalidElementId;
        public View Legend;
        public XYZ LegendCenter;
        public ElementId LegendViewportType = ElementId.InvalidElementId;
        public ViewSchedule Schedule;
        public XYZ SchedulePoint;
        public TextNote FloorLabel;
        public List<TextNote> CopyTexts = new List<TextNote>();

        public string Describe() =>
            $"{Sheet.SheetNumber} - {Sheet.Name}: title block {(TitleBlock != null ? TitleBlock.Symbol.FamilyName + " : " + TitleBlock.Symbol.Name : "none")}, " +
            $"plan '{Plan?.Name}', legend '{Legend?.Name ?? "none"}', schedule '{Schedule?.Name ?? "none"}', " +
            $"floor label {(FloorLabel != null ? "'" + FloorLabel.Text.Trim() + "'" : "none")}" +
            (CopyTexts.Count > 0 ? $", copied text: {string.Join(", ", CopyTexts.Select(t => "'" + t.Text.Trim() + "'"))}" : "");
    }

    /// <summary>One floor's sheet: what it is called and what it still needs.</summary>
    public class SoFloor
    {
        public ClassifiedLevel Level;
        public ViewPlan View;
        public ViewSheet Sheet;                  // null = to be made
        public string Number, Name, ScheduleName, Label;
        public List<string> Missing = new List<string>();
        public bool IsPattern;
        /// <summary>No S&amp;O sheet in the model yet: the first sheet made is built from the template.</summary>
        public bool FromTemplate;

        public string Action => Sheet == null ? (FromTemplate ? "create from the template" : "create") : Missing.Count == 0 ? "up to date" : "add " + string.Join(", ", Missing);
    }

    public class SoSetPlan
    {
        public SheetPattern Pattern;
        /// <summary>The model has no S&amp;O sheet to copy: the set starts from this template (null = none found).</summary>
        public SoTemplate Template;
        public List<SoFloor> Floors = new List<SoFloor>();
        public List<string> Problems = new List<string>();
        public string ProjectName;
    }

    public class SoSetResult
    {
        public List<string> Created = new List<string>(), Updated = new List<string>(), Problems = new List<string>();
        public int Notes, Fields;
        public string Pdf;
        public List<ElementId> SheetIds = new List<ElementId>();

        public string Summary()
        {
            var lines = new List<string>();
            if (Created.Count > 0) lines.Add($"S&O sheets made: {string.Join(", ", Created)}");
            if (Updated.Count > 0) lines.Add($"S&O sheets updated: {string.Join("; ", Updated)}");
            if (Created.Count == 0 && Updated.Count == 0) lines.Add($"S&O sheets: all {SheetIds.Count} up to date");
            if (Fields > 0) lines.Add($"Title block / project fields written: {Fields}");
            if (Notes > 0) lines.Add($"Notes on the sheets: {Notes}");
            if (Pdf != null) lines.Add($"PDF: {Pdf}");
            lines.AddRange(Problems.Select(p => "! " + p));
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// The S&amp;O set (SO_SHEETS_PLAN.md): one sheet per floor that has a Sleeves view, built like the pattern sheet:
    /// title block, the floor's Sleeves view, the legend, the floor's copy of the pattern schedule, the floor label in the
    /// view. Existing sheets are only completed, never rebuilt or deleted. Notes go in the title block's revision table
    /// as revisions. Then the set is printed to one PDF.
    /// </summary>
    public static class SoSheets
    {
        // ---------------------------------------------------------------- what to do

        public static SoSetPlan Plan(Document doc, RuleSet rules, SoSheetRules cfg, LevelMap levels)
        {
            string suffix = rules.SleeveViews?.NameSuffix ?? " Sleeves";
            var plan = new SoSetPlan { ProjectName = ProjectName(doc, cfg) };
            var patternProblems = new List<string>();
            plan.Pattern = FindPattern(doc, cfg, suffix, patternProblems);
            if (plan.Pattern == null)
            {
                // no S&O sheet in the model: the office's template, if there is one
                plan.Template = SoTemplates.Resolve(cfg.Template, doc.PathName, out string why);
                if (plan.Template == null) { plan.Problems.AddRange(patternProblems); plan.Problems.Add(why); return plan; }
            }
            else plan.Problems.AddRange(patternProblems);

            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate).ToList();
            var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            var taken = new HashSet<string>(sheets.Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);

            ViewSheet SheetOf(ViewPlan v) { var vp = viewports.FirstOrDefault(x => x.ViewId == v.Id); return vp != null ? doc.GetElement(vp.SheetId) as ViewSheet : null; }

            // one sheet per floor: two levels with the same floor name ("06.6-TH FLOOR" and "06.6 TH FLOOR NEW") keep the one
            // that holds the openings; when the other already has the floor's sheet, that sheet takes this level's view
            var withViews = levels.All.OrderBy(l => l.Elevation)
                                  .Select(cl => (Cl: cl, View: plans.FirstOrDefault(v => v.Name == cl.Name + suffix))).Where(x => x.View != null).ToList();
            var openingsOn = SleevesOpenings.Risers.RiserIndex.AllOpenings(doc).GroupBy(o => o.Level.Id).ToDictionary(g => g.Key, g => g.Count());
            int Openings(ClassifiedLevel cl) => openingsOn.TryGetValue(cl.Level.Id, out var n) ? n : 0;
            var kept = new List<(ClassifiedLevel Cl, ViewPlan View, ViewSheet Borrowed)>();
            foreach (var same in withViews.GroupBy(x => cl2Key(x.Cl)))
            {
                var list = same.ToList();
                var keep = list.OrderByDescending(x => Openings(x.Cl)).ThenByDescending(x => SheetOf(x.View) != null).First();
                ViewSheet borrowed = null;
                foreach (var other in list.Where(x => x.Cl != keep.Cl))
                {
                    var theirs = SheetOf(other.View);
                    if (theirs != null && SheetOf(keep.View) == null && borrowed == null) borrowed = theirs;
                    plan.Problems.Add($"{other.Cl.Name}: same floor as {keep.Cl.Name}; one sheet per floor, for the level with the openings ({keep.Cl.Name}, {Openings(keep.Cl)} opening(s))" +
                                      (theirs != null && borrowed == theirs ? $"; sheet {theirs.SheetNumber} now shows '{keep.View.Name}'" : ""));
                }
                kept.Add((keep.Cl, keep.View, borrowed));
            }
            string cl2Key(ClassifiedLevel cl) => cl.Role == LevelRole.Roof || cl.Role == LevelRole.Bulkhead ? cl.Name : FloorKey.FromLevelName(cl.Name) ?? cl.Name;

            int index = 0;
            foreach (var (cl, view, borrowed) in kept.OrderBy(x => x.Cl.Elevation))
            {
                index++;
                var floor = new SoFloor { Level = cl, View = view, IsPattern = plan.Pattern != null && view.Id == plan.Pattern.Plan.Id };
                Names(cl, index, cfg, floor);
                floor.Sheet = SheetOf(view) ?? borrowed;
                if (floor.Sheet != null)
                {
                    floor.Number = floor.Sheet.SheetNumber;
                    if (plan.Pattern != null) floor.Missing = Missing(doc, plan.Pattern, floor, cfg);
                }
                else
                {
                    // the next number in the set; one taken by another sheet moves this one on
                    int n = cfg.FirstNumber + index - 1;
                    while (taken.Contains(cfg.NumberPrefix + n)) n++;
                    if (n != cfg.FirstNumber + index - 1)
                        plan.Problems.Add($"{cl.Name}: {cfg.NumberPrefix}{cfg.FirstNumber + index - 1} is already used by another sheet, so this floor's sheet is {cfg.NumberPrefix}{n}");
                    floor.Number = cfg.NumberPrefix + n;
                    floor.FromTemplate = plan.Pattern == null && !plan.Floors.Any(x => x.FromTemplate);
                    taken.Add(floor.Number);
                }
                plan.Floors.Add(floor);
            }
            if (plan.Floors.Count == 0) plan.Problems.Add($"No '<level>{suffix}' views yet: run Auto Run (it makes them) before the S&O sheets.");
            return plan;
        }

        internal static SheetPattern FindPattern(Document doc, SoSheetRules cfg, string suffix, List<string> problems)
        {
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            IEnumerable<ViewSheet> candidates = !string.IsNullOrEmpty(cfg.PatternSheet)
                ? sheets.Where(s => s.SheetNumber.Equals(cfg.PatternSheet, StringComparison.OrdinalIgnoreCase))
                : sheets.Where(s => s.SheetNumber.StartsWith(cfg.NumberPrefix ?? "", StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in candidates)
            {
                var p = new SheetPattern { Sheet = sheet };
                foreach (var id in sheet.GetAllViewports())
                {
                    if (!(doc.GetElement(id) is Viewport vp)) continue;
                    var view = doc.GetElement(vp.ViewId) as View;
                    if (view is ViewPlan vplan && view.Name.EndsWith(suffix) && p.Plan == null) { p.Plan = vplan; p.PlanCenter = vp.GetBoxCenter(); p.PlanViewportType = vp.GetTypeId(); }
                    else if (view?.ViewType == ViewType.Legend && p.Legend == null) { p.Legend = view; p.LegendCenter = vp.GetBoxCenter(); p.LegendViewportType = vp.GetTypeId(); }
                }
                if (p.Plan == null) continue;
                var onSheet = new FilteredElementCollector(doc, sheet.Id).WhereElementIsNotElementType().ToElements();
                p.TitleBlock = onSheet.OfType<FamilyInstance>().FirstOrDefault(f => f.Category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks);
                if (p.TitleBlock != null) { p.TitleBlockType = p.TitleBlock.GetTypeId(); p.TitleBlockPoint = (p.TitleBlock.Location as LocationPoint)?.Point ?? XYZ.Zero; }
                var ss = onSheet.OfType<ScheduleSheetInstance>().FirstOrDefault(s => !s.IsTitleblockRevisionSchedule);
                if (ss != null) { p.Schedule = doc.GetElement(ss.ScheduleId) as ViewSchedule; p.SchedulePoint = ss.Point; }
                var texts = new FilteredElementCollector(doc, p.Plan.Id).OfClass(typeof(TextNote)).Cast<TextNote>().Where(t => t.OwnerViewId == p.Plan.Id).ToList();
                p.FloorLabel = texts.FirstOrDefault(t => TypeName(doc, t).Equals(cfg.FloorLabelType ?? "", StringComparison.OrdinalIgnoreCase));
                p.CopyTexts = texts.Where(t => t != p.FloorLabel && (cfg.CopyTextTypes ?? new List<string>()).Any(n => TypeName(doc, t).Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();

                if (p.TitleBlock == null) problems.Add($"Pattern sheet {sheet.SheetNumber} has no title block: new sheets get none.");
                if (p.Legend == null) problems.Add($"Pattern sheet {sheet.SheetNumber} has no legend.");
                if (p.Schedule == null) problems.Add($"Pattern sheet {sheet.SheetNumber} has no schedule (only the title block's notes table).");
                if (p.FloorLabel == null) problems.Add($"Pattern view '{p.Plan.Name}' has no text of type '{cfg.FloorLabelType}' (the floor label).");
                return p;
            }
            problems.Add(!string.IsNullOrEmpty(cfg.PatternSheet)
                ? $"Pattern sheet {cfg.PatternSheet} (rules.json soSheets.patternSheet) not found, or it holds no '<level>{suffix}' view."
                : $"No {cfg.NumberPrefix} sheet with a '<level>{suffix}' view to copy. Make one sheet by hand once (as the office's SL101), or copy one from another project.");
            return null;
        }

        /// <summary>"1st Floor" / "Roof"; "SL- 8th roof"; "1ST FLOOR".</summary>
        private static void Names(ClassifiedLevel cl, int index, SoSheetRules cfg, SoFloor f)
        {
            string kind = cl.Role == LevelRole.Roof ? "roof" : cl.Role == LevelRole.Bulkhead ? "bulkhead" : cl.Role == LevelRole.Cellar ? "cellar" : "floor";
            string key = FloorKey.FromLevelName(cl.Name);
            int n = key != null && key.StartsWith("F") && int.TryParse(key.Substring(1), out int k) ? k : 0;
            string floor = kind == "roof" ? "Roof" : kind == "bulkhead" ? "Bulkhead" : kind == "cellar" ? "Cellar" : n > 0 ? $"{Ord(n)} Floor" : cl.Name;
            string Fill(string pattern) => (pattern ?? "")
                .Replace("{index}", Ord(index)).Replace("{ord}", n > 0 ? Ord(n) : Ord(index)).Replace("{kind}", kind)
                .Replace("{Floor}", floor).Replace("{FLOOR}", floor.ToUpperInvariant()).Replace("{level}", cl.Name);
            f.Name = Fill(cfg.SheetName);
            f.ScheduleName = Fill(cfg.ScheduleName);
            f.Label = Fill(cfg.FloorLabel);
        }

        private static string Ord(int n) =>
            n + (n % 100 >= 11 && n % 100 <= 13 ? "th" : (n % 10) == 1 ? "st" : (n % 10) == 2 ? "nd" : (n % 10) == 3 ? "rd" : "th");

        private static List<string> Missing(Document doc, SheetPattern p, SoFloor f, SoSheetRules cfg)
        {
            var sheet = f.Sheet; var view = f.View;
            var missing = new List<string>();
            if (cfg.RenameExisting && !string.IsNullOrEmpty(f.Name) && sheet.Name != f.Name) missing.Add("name");
            if (!sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Any(v => v?.ViewId == view.Id)) missing.Add("plan view");
            var onSheet = new FilteredElementCollector(doc, sheet.Id).WhereElementIsNotElementType().ToElements();
            if (p.TitleBlock != null && !onSheet.OfType<FamilyInstance>().Any(f => f.Category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks)) missing.Add("title block");
            if (p.Legend != null && !sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Any(v => v?.ViewId == p.Legend.Id)) missing.Add("legend");
            if (p.Schedule != null && !onSheet.OfType<ScheduleSheetInstance>().Any(s => !s.IsTitleblockRevisionSchedule)) missing.Add("schedule");
            missing.AddRange(MissingTexts(doc, p, view));
            if (Math.Abs(LegendShift(doc, sheet).Shift) > 1e-6) missing.Add(LegendBelow);
            return missing;
        }

        /// <summary>The floor label and the copied texts the view has none of (by text type: a view keeps its own label where it is).</summary>
        private static List<string> MissingTexts(Document doc, SheetPattern p, View view)
        {
            var missing = new List<string>();
            var texts = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().Where(t => t.OwnerViewId == view.Id).Select(t => TypeName(doc, t)).ToList();
            if (p.FloorLabel != null && !texts.Contains(TypeName(doc, p.FloorLabel))) missing.Add("floor label");
            foreach (var t in p.CopyTexts)
                if (!texts.Contains(TypeName(doc, t))) missing.Add($"'{t.Text.Trim()}'");
            return missing;
        }

        /// <summary>
        /// The same floor label / copied text twice in a view (the view's own and the add-in's copy, laid over each other):
        /// the oldest stays, the others go. Only texts of those types with the very same words. Returns how many went.
        /// </summary>
        private static int RemoveDoubleTexts(Document doc, SheetPattern p, View view)
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.FloorLabel != null) types.Add(TypeName(doc, p.FloorLabel));
            foreach (var t in p.CopyTexts) types.Add(TypeName(doc, t));
            if (types.Count == 0) return 0;
            var doubles = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
                .Where(t => t.OwnerViewId == view.Id && types.Contains(TypeName(doc, t)))
                .GroupBy(t => TypeName(doc, t).ToUpperInvariant() + "|" + System.Text.RegularExpressions.Regex.Replace(t.Text ?? "", @"\s+", " ").Trim().ToUpperInvariant())
                .SelectMany(g => g.OrderBy(t => t.Id.Value).Skip(1))
                .Select(t => t.Id).ToList();
            if (doubles.Count > 0) doc.Delete(doubles);
            return doubles.Count;
        }

        // ---------------------------------------------------------------- doing it

        /// <summary>Makes / completes the sheets and writes the notes. Opens its own transaction.</summary>
        public static SoSetResult Apply(Document doc, SoSheetRules cfg, SoSetPlan plan, IList<SoNote> notes, IList<SoField> fields = null)
        {
            var result = new SoSetResult();
            result.Problems.AddRange(plan.Problems);
            var p = plan.Pattern;
            if (p == null) return result;

            using (var t = new Transaction(doc, "Sleeves & Openings: S&O sheets"))
            {
                t.Start();
                var sizedOnly = new List<string>();
                var doubled = new List<string>();
                foreach (var f in plan.Floors)
                {
                    try
                    {
                        // a label the add-in laid over the view's own (before it looked): the copy goes
                        int gone = RemoveDoubleTexts(doc, p, f.View);
                        if (gone > 0) doubled.Add($"{f.View.Name} ({gone})");
                        if (f.Sheet == null)
                        {
                            f.Sheet = Create(doc, p, f, cfg);
                            result.Created.Add($"{f.Sheet.SheetNumber} {f.Sheet.Name}");
                            // the view may have its label already (a set made before, its sheets deleted): only what it lacks
                            Complete(doc, p, f, new List<string> { "legend", "schedule" }.Concat(MissingTexts(doc, p, f.View)).ToList(), result);
                            SizedOnly(doc, cfg, f.Sheet, sizedOnly, result.Problems);
                            Complete(doc, p, f, new List<string> { LegendBelow }, result);
                        }
                        else
                        {
                            SizedOnly(doc, cfg, f.Sheet, sizedOnly, result.Problems);
                            if (f.Missing.Count > 0)
                            {
                                Complete(doc, p, f, f.Missing, result);
                                result.Updated.Add($"{f.Sheet.SheetNumber}: added {string.Join(", ", f.Missing)}");
                            }
                        }
                        result.SheetIds.Add(f.Sheet.Id);
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add($"{f.Level.Name}: {ex.Message}");
                        App.Log($"S&O sheets: {f.Level.Name} failed: {ex}");
                    }
                }
                if (doubled.Count > 0)
                    result.Problems.Add($"Floor label / street name was in the view twice (one over the other); the copy was removed: {string.Join(", ", doubled)}");
                if (sizedOnly.Count > 0)
                    result.Problems.Add($"Schedules now list sized sleeves only (rows with no size left out, e.g. rectangular openings): {string.Join(", ", sizedOnly)}");
                if (cfg.UnstackGrids)
                    try { SoGrids.Unstack(doc, plan.Floors.Where(f => f.Sheet != null).Select(f => f.View).ToList(), result.Problems); }
                    catch (Exception ex) { result.Problems.Add("Stacked grid bubbles not checked: " + ex.Message); App.Log("S&O sheets: grids failed: " + ex); }
                var setSheets = plan.Floors.Where(f => f.Sheet != null).Select(f => f.Sheet).ToList();
                if (fields != null && fields.Any(x => x.Write))
                    try { result.Fields = WriteFields(doc, setSheets.Concat(new[] { p.Sheet }).Distinct().ToList(), fields, result.Problems); }
                    catch (Exception ex) { result.Problems.Add("Title block fields not written: " + ex.Message); App.Log("S&O sheets: fields failed: " + ex); }
                try { result.Notes = WriteNotes(doc, cfg, setSheets, notes ?? new List<SoNote>(), result.Problems); }
                catch (Exception ex) { result.Problems.Add("Notes not written: " + ex.Message); App.Log("S&O sheets: notes failed: " + ex); }
                t.Commit();
            }
            return result;
        }

        private static ViewSheet Create(Document doc, SheetPattern p, SoFloor f, SoSheetRules cfg)
        {
            var sheet = ViewSheet.Create(doc, p.TitleBlockType);
            sheet.SheetNumber = f.Number;
            sheet.Name = f.Name;
            // the sheet's own fields as on the pattern (drawn / checked / designed / approved by), today's issue date
            foreach (var bip in new[] { BuiltInParameter.SHEET_DRAWN_BY, BuiltInParameter.SHEET_CHECKED_BY, BuiltInParameter.SHEET_DESIGNED_BY, BuiltInParameter.SHEET_APPROVED_BY })
                CopyParam(p.Sheet.get_Parameter(bip), sheet.get_Parameter(bip));
            if (!string.IsNullOrEmpty(cfg.IssueDateFormat))
                sheet.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE)?.Set(DateTime.Now.ToString(cfg.IssueDateFormat));

            // the title block where the pattern's is, with the pattern's own switches (A-J, NOT FOR CONSTRUCTION…)
            var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault();
            if (tb != null && p.TitleBlock != null)
            {
                var at = (tb.Location as LocationPoint)?.Point ?? XYZ.Zero;
                if (!at.IsAlmostEqualTo(p.TitleBlockPoint)) ElementTransformUtils.MoveElement(doc, tb.Id, p.TitleBlockPoint - at);
                foreach (Parameter src in p.TitleBlock.Parameters)
                {
                    if (src.IsReadOnly || !(src.Definition is InternalDefinition def) || def.BuiltInParameter != BuiltInParameter.INVALID) continue;
                    CopyParam(src, tb.LookupParameter(src.Definition.Name));
                }
            }

            // the floor's Sleeves view, set up like the pattern's (scope box, scale, detail) unless a template rules it
            if (f.View.ViewTemplateId == ElementId.InvalidElementId)
            {
                var box = p.Plan.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId();
                var mine = f.View.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                if (box != null && box != ElementId.InvalidElementId && mine != null && !mine.IsReadOnly && mine.AsElementId() != box) mine.Set(box);
                if (f.View.Scale != p.Plan.Scale) f.View.Scale = p.Plan.Scale;
                f.View.DetailLevel = p.Plan.DetailLevel;
                f.View.CropBoxActive = p.Plan.CropBoxActive;
                f.View.CropBoxVisible = p.Plan.CropBoxVisible;
            }
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, f.View.Id)) throw new InvalidOperationException($"'{f.View.Name}' cannot go on a sheet (already placed elsewhere?)");
            var vp = Viewport.Create(doc, sheet.Id, f.View.Id, p.PlanCenter);
            if (p.PlanViewportType != ElementId.InvalidElementId && vp.GetTypeId() != p.PlanViewportType) vp.ChangeTypeId(p.PlanViewportType);
            vp.SetBoxCenter(p.PlanCenter);
            return sheet;
        }

        private static void Complete(Document doc, SheetPattern p, SoFloor f, List<string> missing, SoSetResult result)
        {
            if (missing.Contains("name")) f.Sheet.Name = f.Name;

            if (missing.Contains("plan view"))
            {
                // the sheet shows the other level of the same floor: its plan viewport is replaced by this level's, in place
                var old = f.Sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport)
                                 .FirstOrDefault(v => v != null && doc.GetElement(v.ViewId) is ViewPlan vp && vp.ViewType == ViewType.FloorPlan);
                var center = old?.GetBoxCenter() ?? p.Sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).FirstOrDefault(v => v?.ViewId == p.Plan.Id)?.GetBoxCenter();
                var type = old?.GetTypeId();
                if (old != null) doc.Delete(old.Id);
                if (center != null)
                {
                    var vp = Viewport.Create(doc, f.Sheet.Id, f.View.Id, center);
                    if (type != null && type != ElementId.InvalidElementId && vp.GetTypeId() != type) vp.ChangeTypeId(type);
                    vp.SetBoxCenter(center);
                }
            }

            if (missing.Contains("title block") && p.TitleBlockType != ElementId.InvalidElementId)
                doc.Create.NewFamilyInstance(p.TitleBlockPoint, doc.GetElement(p.TitleBlockType) as FamilySymbol, f.Sheet);

            if (missing.Contains("legend") && p.Legend != null)
            {
                var vp = Viewport.Create(doc, f.Sheet.Id, p.Legend.Id, p.LegendCenter);
                if (p.LegendViewportType != ElementId.InvalidElementId && vp.GetTypeId() != p.LegendViewportType) vp.ChangeTypeId(p.LegendViewportType);
                vp.SetBoxCenter(p.LegendCenter);
            }

            if (missing.Contains("schedule") && p.Schedule != null)
            {
                var schedule = FloorSchedule(doc, p, f, result.Problems);
                if (schedule != null) ScheduleSheetInstance.Create(doc, f.Sheet.Id, schedule.Id, p.SchedulePoint);
            }

            // the floor label and the copied text: the pattern's notes copied into this floor's view (same building, same spot)
            var copy = new List<(TextNote Note, string Text)>();
            if (missing.Contains("floor label") && p.FloorLabel != null) copy.Add((p.FloorLabel, f.Label));
            foreach (var t in p.CopyTexts)
                if (missing.Contains($"'{t.Text.Trim()}'")) copy.Add((t, null));
            foreach (var (note, text) in copy)
            {
                if (f.View.Id == p.Plan.Id) continue;
                var ids = ElementTransformUtils.CopyElements(p.Plan, new List<ElementId> { note.Id }, f.View, Transform.Identity, new CopyPasteOptions());
                if (text != null)
                    foreach (var id in ids)
                        if (doc.GetElement(id) is TextNote copied) copied.Text = text;
            }

            if (missing.Contains(LegendBelow))
            {
                doc.Regenerate();
                var (shift, squeezed) = LegendShift(doc, f.Sheet);
                var legend = LegendViewport(doc, f.Sheet);
                if (Math.Abs(shift) > 1e-6 && legend != null)
                {
                    legend.SetBoxCenter(legend.GetBoxCenter() + new XYZ(0, shift, 0));
                    result.Problems.Add(squeezed
                        ? $"{f.Sheet.SheetNumber}: the schedule and the legend only just fit above the notes table: the legend sits right on it, close under the schedule; check they don't touch"
                        : shift < 0
                            ? $"{f.Sheet.SheetNumber}: the schedule is longer than on the pattern sheet, so the legend moved down {Math.Abs(shift) * 12:0.##}\" under it (it still clears the notes table)"
                            : $"{f.Sheet.SheetNumber}: the legend ran into the notes table; moved up {shift * 12:0.##}\"");
                }
            }
        }

        private const string LegendBelow = "legend below the schedule";

        private static Viewport LegendViewport(Document doc, ViewSheet sheet) =>
            sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).FirstOrDefault(v => v != null && doc.GetElement(v.ViewId) is View lv && lv.ViewType == ViewType.Legend);

        /// <summary>
        /// The floor's schedule grows down from where the pattern's starts; one with more sizes than the pattern's runs into
        /// the legend under it. Returns how far the legend must move (feet, negative = down) to clear it, but never down
        /// past the title block line under it (the top of the notes table): a legend already over that line goes back up.
        /// Squeezed = it would have to go lower to clear the schedule (the two only just fit; the outlines carry some margin).
        /// </summary>
        private static (double Shift, bool Squeezed) LegendShift(Document doc, ViewSheet sheet)
        {
            var legend = LegendViewport(doc, sheet);
            var o = legend?.GetBoxOutline();
            if (o == null) return (0, false);
            const double gap = 0.125 / 12;                                                    // 1/8" between them
            double shift = 0;
            var ss = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().FirstOrDefault(x => !x.IsTitleblockRevisionSchedule);
            var box = ss?.get_BoundingBox(sheet);
            if (box != null)
            {
                bool side = box.Max.X <= o.MinimumPoint.X || box.Min.X >= o.MaximumPoint.X;
                bool clear = box.Min.Y >= o.MaximumPoint.Y;                                      // schedule wholly above the legend
                bool under = box.Max.Y <= o.MinimumPoint.Y || box.Max.Y < o.MaximumPoint.Y;      // schedule under / starting inside: not this case
                if (!(side || clear || under)) shift = Math.Min(0, box.Min.Y - gap - o.MaximumPoint.Y);
            }
            var floor = LineUnder(doc, sheet, o);
            if (floor == null) return (shift, false);
            double lowest = floor.Value + 0.0625 / 12 - o.MinimumPoint.Y;                       // legend bottom 1/16" above the line
            if (shift >= lowest) return (shift, false);
            return (lowest, shift < 0 && lowest > shift);
        }

        /// <summary>
        /// The highest horizontal title block line under the legend's top that runs across it (the top of the notes table):
        /// the legend must stay above it. Null when the title block has none.
        /// </summary>
        private static double? LineUnder(Document doc, ViewSheet sheet, Outline legend)
        {
            var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance)).FirstOrDefault();
            if (tb == null) return null;
            double minX = legend.MinimumPoint.X, maxX = legend.MaximumPoint.X, mid = (minX + maxX) / 2, top = legend.MaximumPoint.Y;
            double? best = null;
            void Walk(GeometryElement ge)
            {
                if (ge == null) return;
                foreach (var g in ge)
                {
                    if (g is GeometryInstance gi) { Walk(gi.GetInstanceGeometry()); continue; }
                    if (!(g is Line line)) continue;
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                    if (Math.Abs(a.Y - b.Y) > 1e-4) continue;
                    double lo = Math.Min(a.X, b.X), hi = Math.Max(a.X, b.X);
                    // across the legend: under its middle and at least half as long as it is wide
                    if (lo > mid || hi < mid || Math.Min(hi, maxX) - Math.Max(lo, minX) < (maxX - minX) / 2) continue;
                    if (a.Y < top - 1e-4 && (best == null || a.Y > best)) best = a.Y;
                }
            }
            try { Walk(tb.get_Geometry(new Options { View = sheet })); }
            catch (Exception ex) { App.Log($"S&O sheets: {sheet.SheetNumber} title block lines not read: {ex.Message}"); }
            return best;
        }

        /// <summary>This floor's copy of the pattern schedule ("SL- 2nd floor"), its level filter set to the floor; an existing one by that name is reused.</summary>
        private static ViewSchedule FloorSchedule(Document doc, SheetPattern p, SoFloor f, List<string> problems)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(s => s.Name == f.ScheduleName);
            if (existing != null) return existing;
            if (f.IsPattern) return p.Schedule;
            var schedule = doc.GetElement(p.Schedule.Duplicate(ViewDuplicateOption.Duplicate)) as ViewSchedule;
            schedule.Name = f.ScheduleName;
            if (!SetLevelFilter(schedule, f.Level.Level)) problems.Add($"{f.ScheduleName}: the pattern schedule has no Level filter, so it lists every floor");
            return schedule;
        }

        /// <summary>
        /// rules.json soSheets.scheduleSizedOnly: the sheet's schedule lists sleeves only. Its size column (the first field it
        /// sorts / groups by that is not the Level or the Count) gets a "has a value" filter, so what has no size (rectangular
        /// openings) is no longer a blank row. Once per schedule; the names of the schedules changed go to <paramref name="changed"/>.
        /// </summary>
        private static void SizedOnly(Document doc, SoSheetRules cfg, ViewSheet sheet, List<string> changed, List<string> problems)
        {
            if (!cfg.ScheduleSizedOnly) return;
            var ss = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().FirstOrDefault(x => !x.IsTitleblockRevisionSchedule);
            if (ss == null || !(doc.GetElement(ss.ScheduleId) is ViewSchedule schedule)) return;
            var def = schedule.Definition;
            ScheduleField size = null;
            for (int i = 0; i < def.GetSortGroupFieldCount() && size == null; i++)
            {
                var field = def.GetField(def.GetSortGroupField(i).FieldId);
                if (field.FieldType != ScheduleFieldType.Count && !IsLevelField(field)) size = field;
            }
            if (size == null) return;
            for (int i = 0; i < def.GetFilterCount(); i++)
            {
                var filter = def.GetFilter(i);
                if (filter.FieldId == size.FieldId && filter.FilterType == ScheduleFilterType.HasValue) return;
            }
            try
            {
                def.AddFilter(new ScheduleFilter(size.FieldId, ScheduleFilterType.HasValue));
                changed.Add(schedule.Name);
            }
            catch (Exception ex)
            {
                problems.Add($"{schedule.Name}: rows with no {size.GetName()} not left out ({ex.Message})");
                App.Log($"S&O sheets: '{schedule.Name}' has-value filter on '{size.GetName()}': {ex}");
            }
        }

        private static bool IsLevelField(ScheduleField f) =>
            f.ParameterId == new ElementId(BuiltInParameter.SCHEDULE_LEVEL_PARAM) || f.GetName().Equals("Level", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The schedule's Level filter set to this level. A schedule without one (a template's, whose filter Revit drops when
        /// the level it named is not in the model) gets one, on its Level field (added hidden when missing). False = no Level
        /// field can be scheduled.
        /// </summary>
        internal static bool SetLevelFilter(ViewSchedule schedule, Level level)
        {
            var def = schedule.Definition;
            var levelParam = new ElementId(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            bool IsLevel(ScheduleField f) => IsLevelField(f);
            bool set = false;
            for (int i = 0; i < def.GetFilterCount(); i++)
            {
                var filter = def.GetFilter(i);
                if (!IsLevel(def.GetField(filter.FieldId))) continue;
                if (filter.IsElementIdValue) filter.SetValue(level.Id);
                else if (filter.IsStringValue) filter.SetValue(level.Name);
                else continue;
                def.SetFilter(i, filter);
                set = true;
            }
            if (set) return true;

            ScheduleField field = null;
            for (int i = 0; i < def.GetFieldCount() && field == null; i++)
                if (IsLevel(def.GetField(i))) field = def.GetField(i);
            if (field == null)
            {
                var doc = schedule.Document;
                var schedulable = def.GetSchedulableFields().FirstOrDefault(s => s.ParameterId == levelParam)
                                  ?? def.GetSchedulableFields().FirstOrDefault(s => s.GetName(doc).Equals("Level", StringComparison.OrdinalIgnoreCase));
                if (schedulable == null) return false;
                field = def.AddField(schedulable);
                field.IsHidden = true;
            }
            try { def.AddFilter(new ScheduleFilter(field.FieldId, ScheduleFilterType.Equal, level.Id)); }
            catch (Exception)
            {
                try { def.AddFilter(new ScheduleFilter(field.FieldId, ScheduleFilterType.Equal, level.Name)); }
                catch (Exception ex) { App.Log($"S&O sheets: Level filter not added to '{schedule.Name}': {ex.Message}"); return false; }
            }
            return true;
        }

        /// <summary>
        /// The notes table is the title block's revision schedule: each note is a revision (Description = the note,
        /// Issued By = notesTag) set on its sheets. Revisions without the tag (the office's own) are never touched.
        /// </summary>
        private static int WriteNotes(Document doc, SoSheetRules cfg, List<ViewSheet> sheets, IList<SoNote> notes, List<string> problems)
        {
            string tag = cfg.NotesTag ?? "S&O notes";
            var ours = Revision.GetAllRevisionIds(doc).Select(id => doc.GetElement(id) as Revision)
                .Where(r => r != null && r.IssuedBy == tag).ToList();
            var wanted = notes.Where(n => !string.IsNullOrWhiteSpace(n.Text)).ToList();
            var byNote = new Dictionary<SoNote, Revision>();
            foreach (var n in wanted)
            {
                var r = ours.FirstOrDefault(x => x.Description == n.Text.Trim() && !byNote.ContainsValue(x));
                if (r == null)
                {
                    r = Revision.Create(doc);
                    r.Description = n.Text.Trim();
                    r.IssuedBy = tag;
                    r.RevisionDate = DateTime.Now.ToString(cfg.IssueDateFormat ?? "MM/dd/yy");
                }
                if (!string.IsNullOrWhiteSpace(n.Date) && r.RevisionDate != n.Date.Trim()) r.RevisionDate = n.Date.Trim();
                byNote[n] = r;
            }
            var used = new HashSet<ElementId>(byNote.Values.Select(r => r.Id));
            var oursIds = new HashSet<ElementId>(ours.Select(r => r.Id).Concat(used));
            foreach (var sheet in sheets)
            {
                var keep = sheet.GetAdditionalRevisionIds().Where(id => !oursIds.Contains(id)).ToList();
                foreach (var kv in byNote)
                {
                    bool here = kv.Key.Sheets == null || kv.Key.Sheets.Count == 0 ||
                                kv.Key.Sheets.Any(s => s.Trim().Equals(sheet.SheetNumber, StringComparison.OrdinalIgnoreCase));
                    if (here) keep.Add(kv.Value.Id);
                }
                sheet.SetAdditionalRevisionIds(keep);
            }
            // notes taken off the list: their revisions go (Revit keeps at least one revision; then it just stays off the sheets)
            foreach (var r in ours.Where(r => !used.Contains(r.Id)))
                try { doc.Delete(r.Id); } catch (Exception ex) { App.Log($"S&O sheets: revision '{r.Description}' kept: {ex.Message}"); }
            var missingSheets = wanted.SelectMany(n => n.Sheets ?? new List<string>()).Select(s => s.Trim()).Where(s => s.Length > 0)
                .Where(s => !sheets.Any(x => x.SheetNumber.Equals(s, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            if (missingSheets.Count > 0) problems.Add($"Notes name sheets that are not in the set: {string.Join(", ", missingSheets)}");
            return wanted.Count;
        }

        // ---------------------------------------------------------------- title block and project fields

        private static readonly BuiltInParameter[] SheetFields =
            { BuiltInParameter.SHEET_DRAWN_BY, BuiltInParameter.SHEET_CHECKED_BY, BuiltInParameter.SHEET_DESIGNED_BY, BuiltInParameter.SHEET_APPROVED_BY, BuiltInParameter.SHEET_ISSUE_DATE };

        private static readonly BuiltInParameter[] ProjectFields =
        {
            BuiltInParameter.PROJECT_NAME, BuiltInParameter.PROJECT_ADDRESS, BuiltInParameter.CLIENT_NAME, BuiltInParameter.PROJECT_NUMBER,
            BuiltInParameter.PROJECT_ISSUE_DATE, BuiltInParameter.PROJECT_STATUS, BuiltInParameter.PROJECT_AUTHOR,
            BuiltInParameter.PROJECT_ORGANIZATION_NAME, BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION, BuiltInParameter.PROJECT_BUILDING_NAME
        };

        /// <summary>
        /// What the S&amp;O Set window lets the drafter change, read from the pattern sheet: the project's information
        /// (name, address, client…), the sheet's fields (drawn / checked / designed by, issue date), and every text, number and
        /// yes/no field of the title block and its type (contractor, revision lines… whatever the office's family has).
        /// </summary>
        public static List<SoField> ReadFields(Document doc, SheetPattern p)
        {
            var list = new List<SoField>();
            if (p == null) return list;
            void Add(string owner, Parameter prm, bool builtInOk)
            {
                if (prm?.Definition == null || prm.IsReadOnly) return;
                if (prm.StorageType != StorageType.String && prm.StorageType != StorageType.Integer) return;
                var bip = (prm.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
                if (bip != BuiltInParameter.INVALID && !builtInOk) return;
                bool yesNo = false;
                try { yesNo = prm.StorageType == StorageType.Integer && prm.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; } catch { }
                if (list.Any(x => x.Owner == owner && x.Name == prm.Definition.Name)) return;
                string value = ValueOf(prm, yesNo);
                list.Add(new SoField { Owner = owner, Name = prm.Definition.Name, BuiltIn = (int)bip, YesNo = yesNo, Integer = prm.StorageType == StorageType.Integer && !yesNo, Value = value, Original = value });
            }

            var info = doc.ProjectInformation;
            foreach (var bip in ProjectFields) Add(SoField.Project, info.get_Parameter(bip), true);
            foreach (var prm in Sorted(info.Parameters)) Add(SoField.Project, prm, false);

            foreach (var bip in SheetFields) Add(SoField.Sheet, p.Sheet.get_Parameter(bip), true);
            foreach (var prm in Sorted(p.Sheet.Parameters)) Add(SoField.Sheet, prm, false);

            if (p.TitleBlock != null)
            {
                foreach (var prm in Sorted(p.TitleBlock.Parameters)) Add(SoField.TitleBlock, prm, false);
                foreach (var prm in Sorted(p.TitleBlock.Symbol.Parameters)) Add(SoField.TitleBlockType, prm, false);
            }
            return list;
        }

        private static IEnumerable<Parameter> Sorted(ParameterSet set) =>
            set.Cast<Parameter>().Where(x => x?.Definition != null).OrderBy(x => x.Definition.Name, StringComparer.OrdinalIgnoreCase);

        private static string ValueOf(Parameter prm, bool yesNo) =>
            prm.StorageType == StorageType.String ? prm.AsString() ?? ""
            : yesNo ? (prm.AsInteger() != 0 ? "Yes" : "No")
            : prm.AsInteger().ToString();

        /// <summary>Writes the ticked fields: project ones once, sheet and title block ones on every S&amp;O sheet, type ones on its type(s).</summary>
        private static int WriteFields(Document doc, List<ViewSheet> sheets, IList<SoField> fields, List<string> problems)
        {
            var titleBlocks = sheets.SelectMany(s => new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()).ToList();
            var types = titleBlocks.Select(t => t.Symbol).GroupBy(t => t.Id).Select(g => g.First()).ToList();
            int written = 0;
            foreach (var f in fields.Where(x => x.Write))
            {
                IEnumerable<Element> owners =
                    f.Owner == SoField.Project ? new Element[] { doc.ProjectInformation }
                    : f.Owner == SoField.Sheet ? sheets
                    : f.Owner == SoField.TitleBlock ? titleBlocks
                    : (IEnumerable<Element>)types;
                bool ok = false;
                foreach (var e in owners)
                {
                    var prm = f.BuiltIn != (int)BuiltInParameter.INVALID ? e.get_Parameter((BuiltInParameter)f.BuiltIn) : e.LookupParameter(f.Name);
                    if (prm == null || prm.IsReadOnly) continue;
                    try
                    {
                        string v = f.Value ?? "";
                        if (prm.StorageType == StorageType.String) { if ((prm.AsString() ?? "") != v) prm.Set(v); ok = true; }
                        else if (prm.StorageType == StorageType.Integer)
                        {
                            int n = f.YesNo ? (Regex.IsMatch(v.Trim(), "^(yes|y|true|1|on|x)$", RegexOptions.IgnoreCase) ? 1 : 0)
                                  : int.TryParse(v.Trim(), out int k) ? k : prm.AsInteger();
                            if (prm.AsInteger() != n) prm.Set(n);
                            ok = true;
                        }
                    }
                    catch (Exception ex) { App.Log($"S&O sheets: {f.Owner} '{f.Name}' on {e.Id}: {ex.Message}"); }
                }
                if (ok) written++;
                else problems.Add($"{f.Owner} field '{f.Name}' could not be written");
            }
            return written;
        }

        // ---------------------------------------------------------------- PDF

        private static readonly (ExportPaperFormat Format, double W, double H, string Name)[] Papers =
        {
            (ExportPaperFormat.ANSI_A, 11, 8.5, "ANSI A 8.5 x 11"), (ExportPaperFormat.ANSI_B, 17, 11, "ANSI B 11 x 17"),
            (ExportPaperFormat.ANSI_C, 22, 17, "ANSI C 17 x 22"), (ExportPaperFormat.ARCH_C, 24, 18, "ARCH C 18 x 24"),
            (ExportPaperFormat.ANSI_D, 34, 22, "ANSI D 22 x 34"), (ExportPaperFormat.ARCH_D, 36, 24, "ARCH D 24 x 36"),
            (ExportPaperFormat.ARCH_E2, 38, 26, "ARCH E2 26 x 38"), (ExportPaperFormat.ARCH_E3, 39, 27, "ARCH E3 27 x 39"),
            (ExportPaperFormat.ARCH_E1, 42, 30, "ARCH E1 30 x 42"), (ExportPaperFormat.ANSI_E, 44, 34, "ANSI E 34 x 44"),
            (ExportPaperFormat.ARCH_E, 48, 36, "ARCH E 36 x 48"),
            (ExportPaperFormat.ISO_A3, 16.54, 11.69, "ISO A3"), (ExportPaperFormat.ISO_A2, 23.39, 16.54, "ISO A2"),
            (ExportPaperFormat.ISO_A1, 33.11, 23.39, "ISO A1"), (ExportPaperFormat.ISO_A0, 46.81, 33.11, "ISO A0"),
        };

        /// <summary>The PDF tab's paper list: (value saved, text shown).</summary>
        public static List<(string Value, string Text)> PaperChoices() =>
            new[] { ("auto", "Auto: the standard paper the title block fits on (100%, centred)"), ("sheet", "Revit's 'use sheet size' (page cut to the title block)") }
            .Concat(Papers.Select(p => (p.Format.ToString(), p.Name + " in"))).ToList();

        /// <summary>The title block's size on the sheets, inches (largest over the set); null when there is none.</summary>
        private static (double W, double H)? SheetSize(Document doc, IList<ElementId> sheetIds)
        {
            double w = 0, h = 0;
            foreach (var id in sheetIds)
            {
                if (!(doc.GetElement(id) is ViewSheet sheet)) continue;
                foreach (var tb in new FilteredElementCollector(doc, id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
                {
                    var box = tb.get_BoundingBox(sheet);
                    if (box == null) continue;
                    w = Math.Max(w, (box.Max.X - box.Min.X) * 12);
                    h = Math.Max(h, (box.Max.Y - box.Min.Y) * 12);
                }
            }
            return w > 0 && h > 0 ? (w, h) : ((double, double)?)null;
        }

        /// <summary>
        /// Revit's "use sheet size" cuts the page to the title block's lines (35.5" x 23.2" on 24 Skillman, the edge lines
        /// half off the page). The office's sets are on a whole sheet of paper: the smallest standard size the title block fits
        /// on, at 100% and centred; when it is a little too big for every size, the closest one, fitted to the page.
        /// </summary>
        private static void SetPaper(SoSheetRules cfg, (double W, double H)? size, PDFExportOptions options)
        {
            string want = (cfg.PaperSize ?? "auto").Trim();
            if (want.Equals("sheet", StringComparison.OrdinalIgnoreCase) || want.Equals("default", StringComparison.OrdinalIgnoreCase)) return;
            bool portrait = size.HasValue && size.Value.H > size.Value.W;
            double w = size.HasValue ? Math.Max(size.Value.W, size.Value.H) : 0, h = size.HasValue ? Math.Min(size.Value.W, size.Value.H) : 0;

            int pick = -1;
            if (!want.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                pick = Array.FindIndex(Papers, p => p.Format.ToString().Equals(want.Replace(" ", "_"), StringComparison.OrdinalIgnoreCase));
                if (pick < 0) App.Log($"S&O PDF: paper '{want}' unknown, auto instead");
            }
            if (pick < 0)
            {
                if (!size.HasValue) return;                                                      // no title block: Revit's own
                var order = Enumerable.Range(0, Papers.Length).OrderBy(i => Papers[i].W * Papers[i].H).ToList();
                pick = order.Where(i => Papers[i].W + 0.01 >= w && Papers[i].H + 0.01 >= h).DefaultIfEmpty(-1).First();
                if (pick < 0) pick = order.Where(i => Papers[i].W >= w * 0.9 && Papers[i].H >= h * 0.9).DefaultIfEmpty(-1).First();
                if (pick < 0) return;
            }
            var paper = Papers[pick];
            bool fit = !size.HasValue || w > paper.W + 0.01 || h > paper.H + 0.01;
            options.PaperFormat = paper.Format;
            options.PaperOrientation = portrait ? PageOrientationType.Portrait : PageOrientationType.Landscape;
            options.PaperPlacement = PaperPlacementType.Center;
            options.ZoomType = fit ? ZoomType.FitToPage : ZoomType.Zoom;
            options.ZoomPercentage = 100;
            App.Log($"S&O PDF: title block {(size.HasValue ? $"{size.Value.W:0.##} x {size.Value.H:0.##} in" : "unknown")} -> {paper.Name}, {(fit ? "fit to page" : "100%")}");
        }

        /// <summary>Prints the sheets (in set order) to one PDF. Outside any transaction.</summary>
        public static string ExportPdf(Document doc, SoSheetRules cfg, IList<ElementId> sheetIds, string folder, string name)
        {
            Directory.CreateDirectory(folder);
            string file = name;
            if (File.Exists(Path.Combine(folder, file + ".pdf")))
                try { File.Delete(Path.Combine(folder, file + ".pdf")); }
                catch { file = $"{name} {DateTime.Now:HHmm}"; }                 // open in a viewer: a new name instead
            var options = new PDFExportOptions
            {
                Combine = true, FileName = file, PaperFormat = ExportPaperFormat.Default, ZoomType = ZoomType.Zoom, ZoomPercentage = 100,
                PaperPlacement = PaperPlacementType.Center, ColorDepth = ColorDepthType.Color, RasterQuality = RasterQualityType.High,
                HideCropBoundaries = true, HideScopeBoxes = true, HideReferencePlane = true, HideUnreferencedViewTags = true
            };
            SetPaper(cfg, SheetSize(doc, sheetIds), options);
            if (!doc.Export(folder, sheetIds, options)) throw new InvalidOperationException("Revit did not print the PDF");
            return Path.Combine(folder, file + ".pdf");
        }

        public static string PdfName(SoSheetRules cfg, string project) =>
            string.Join("_", (cfg.PdfName ?? "{project} Sleeves & Openings {date}")
                .Replace("{project}", project).Replace("{date}", DateTime.Now.ToString(cfg.PdfDateFormat ?? "M-d-yy"))
                .Split(Path.GetInvalidFileNameChars()));

        /// <summary>
        /// The project's Structural folder (pdfFolder): looked for next to the model and next to the drawings Auto Run
        /// read (a detached model has no folder), up to three levels up. Null when there is none.
        /// </summary>
        public static string PdfFolder(Document doc, SoSheetRules cfg, ProjectState state)
        {
            var starts = new List<string>();
            if (!string.IsNullOrEmpty(doc.PathName) && Path.IsPathRooted(doc.PathName)) starts.Add(Path.GetDirectoryName(doc.PathName));
            foreach (var f in state?.Automation?.Files?.Values ?? Enumerable.Empty<Automation.DisciplineFiles>())
                foreach (var path in new[] { f.Pdf, f.Dwg, f.Xrefs })
                    if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path)) starts.Add(Directory.Exists(path) ? path : Path.GetDirectoryName(path));
            Regex rx;
            try { rx = new Regex(cfg.PdfFolder ?? "^Structural$", RegexOptions.IgnoreCase); } catch { return null; }
            foreach (var start in starts.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dir = new DirectoryInfo(start);
                for (int up = 0; up < 4 && dir != null; up++, dir = dir.Parent)
                {
                    try
                    {
                        if (!dir.Exists) continue;
                        if (rx.IsMatch(dir.Name)) return dir.FullName;
                        var hit = dir.GetDirectories().FirstOrDefault(d => rx.IsMatch(d.Name));
                        if (hit != null) return hit.FullName;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>"24 Skillman PL_detached" → "24 Skillman".</summary>
        public static string ProjectName(Document doc, SoSheetRules cfg)
        {
            string name = Path.GetFileNameWithoutExtension(doc.PathName ?? "");
            if (string.IsNullOrEmpty(name)) name = doc.Title ?? "Project";
            try
            {
                for (int i = 0; i < 3 && !string.IsNullOrEmpty(cfg.ProjectNameStrip); i++)
                    name = Regex.Replace(name, cfg.ProjectNameStrip, "", RegexOptions.IgnoreCase).Trim();
            }
            catch { }
            return name.Length > 0 ? name : doc.Title;
        }

        internal static string TypeName(Document doc, Element e) => doc.GetElement(e.GetTypeId())?.Name ?? "";

        internal static void CopyParam(Parameter src, Parameter dst)
        {
            if (src == null || dst == null || dst.IsReadOnly || src.StorageType != dst.StorageType) return;
            try
            {
                switch (src.StorageType)
                {
                    case StorageType.String: if (dst.AsString() != src.AsString()) dst.Set(src.AsString() ?? ""); break;
                    case StorageType.Integer: if (dst.AsInteger() != src.AsInteger()) dst.Set(src.AsInteger()); break;
                    case StorageType.Double: if (Math.Abs(dst.AsDouble() - src.AsDouble()) > 1e-9) dst.Set(src.AsDouble()); break;
                    case StorageType.ElementId: if (dst.AsElementId() != src.AsElementId()) dst.Set(src.AsElementId()); break;
                }
            }
            catch { }
        }
    }
}
