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

        public string Action => Sheet == null ? "create" : Missing.Count == 0 ? "up to date" : "add " + string.Join(", ", Missing);
    }

    public class SoSetPlan
    {
        public SheetPattern Pattern;
        public List<SoFloor> Floors = new List<SoFloor>();
        public List<string> Problems = new List<string>();
        public string ProjectName;
    }

    public class SoSetResult
    {
        public List<string> Created = new List<string>(), Updated = new List<string>(), Problems = new List<string>();
        public int Notes;
        public string Pdf;
        public List<ElementId> SheetIds = new List<ElementId>();

        public string Summary()
        {
            var lines = new List<string>();
            if (Created.Count > 0) lines.Add($"S&O sheets made: {string.Join(", ", Created)}");
            if (Updated.Count > 0) lines.Add($"S&O sheets updated: {string.Join("; ", Updated)}");
            if (Created.Count == 0 && Updated.Count == 0) lines.Add($"S&O sheets: all {SheetIds.Count} up to date");
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

        public static SoSetPlan Plan(Document doc, RuleSet rules, LevelMap levels)
        {
            var cfg = rules.SoSheets ?? new SoSheetRules();
            string suffix = rules.SleeveViews?.NameSuffix ?? " Sleeves";
            var plan = new SoSetPlan { ProjectName = ProjectName(doc, cfg) };
            plan.Pattern = FindPattern(doc, cfg, suffix, plan.Problems);
            if (plan.Pattern == null) return plan;

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
                var floor = new SoFloor { Level = cl, View = view, IsPattern = view.Id == plan.Pattern.Plan.Id };
                Names(cl, index, cfg, floor);
                floor.Sheet = SheetOf(view) ?? borrowed;
                if (floor.Sheet != null)
                {
                    floor.Number = floor.Sheet.SheetNumber;
                    floor.Missing = Missing(doc, plan.Pattern, floor.Sheet, view, cfg);
                }
                else
                {
                    // the next number in the set; one taken by another sheet moves this one on
                    int n = cfg.FirstNumber + index - 1;
                    while (taken.Contains(cfg.NumberPrefix + n)) n++;
                    if (n != cfg.FirstNumber + index - 1)
                        plan.Problems.Add($"{cl.Name}: {cfg.NumberPrefix}{cfg.FirstNumber + index - 1} is already used by another sheet, so this floor's sheet is {cfg.NumberPrefix}{n}");
                    floor.Number = cfg.NumberPrefix + n;
                    taken.Add(floor.Number);
                }
                plan.Floors.Add(floor);
            }
            if (plan.Floors.Count == 0) plan.Problems.Add($"No '<level>{suffix}' views yet: run Auto Run (it makes them) before the S&O sheets.");
            return plan;
        }

        private static SheetPattern FindPattern(Document doc, SoSheetRules cfg, string suffix, List<string> problems)
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

        private static List<string> Missing(Document doc, SheetPattern p, ViewSheet sheet, ViewPlan view, SoSheetRules cfg)
        {
            var missing = new List<string>();
            if (!sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Any(v => v?.ViewId == view.Id)) missing.Add("plan view");
            var onSheet = new FilteredElementCollector(doc, sheet.Id).WhereElementIsNotElementType().ToElements();
            if (p.TitleBlock != null && !onSheet.OfType<FamilyInstance>().Any(f => f.Category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks)) missing.Add("title block");
            if (p.Legend != null && !sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Any(v => v?.ViewId == p.Legend.Id)) missing.Add("legend");
            if (p.Schedule != null && !onSheet.OfType<ScheduleSheetInstance>().Any(s => !s.IsTitleblockRevisionSchedule)) missing.Add("schedule");
            var texts = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().Where(t => t.OwnerViewId == view.Id).Select(t => TypeName(doc, t)).ToList();
            if (p.FloorLabel != null && !texts.Contains(TypeName(doc, p.FloorLabel))) missing.Add("floor label");
            foreach (var t in p.CopyTexts)
                if (!texts.Contains(TypeName(doc, t))) missing.Add($"'{t.Text.Trim()}'");
            return missing;
        }

        // ---------------------------------------------------------------- doing it

        /// <summary>Makes / completes the sheets and writes the notes. Opens its own transaction.</summary>
        public static SoSetResult Apply(Document doc, RuleSet rules, SoSetPlan plan, IList<SoNote> notes)
        {
            var cfg = rules.SoSheets ?? new SoSheetRules();
            var result = new SoSetResult();
            result.Problems.AddRange(plan.Problems);
            var p = plan.Pattern;
            if (p == null) return result;

            using (var t = new Transaction(doc, "Sleeves & Openings: S&O sheets"))
            {
                t.Start();
                foreach (var f in plan.Floors)
                {
                    try
                    {
                        if (f.Sheet == null)
                        {
                            f.Sheet = Create(doc, p, f, cfg);
                            result.Created.Add($"{f.Sheet.SheetNumber} {f.Sheet.Name}");
                            Complete(doc, p, f, new List<string> { "legend", "schedule", "floor label" }.Concat(p.CopyTexts.Select(x => $"'{x.Text.Trim()}'")).ToList(), result);
                        }
                        else if (f.Missing.Count > 0)
                        {
                            Complete(doc, p, f, f.Missing, result);
                            result.Updated.Add($"{f.Sheet.SheetNumber}: added {string.Join(", ", f.Missing)}");
                        }
                        result.SheetIds.Add(f.Sheet.Id);
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add($"{f.Level.Name}: {ex.Message}");
                        App.Log($"S&O sheets: {f.Level.Name} failed: {ex}");
                    }
                }
                try { result.Notes = WriteNotes(doc, cfg, plan.Floors.Where(f => f.Sheet != null).Select(f => f.Sheet).ToList(), notes ?? new List<SoNote>(), result.Problems); }
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
        }

        /// <summary>This floor's copy of the pattern schedule ("SL- 2nd floor"), its level filter set to the floor; an existing one by that name is reused.</summary>
        private static ViewSchedule FloorSchedule(Document doc, SheetPattern p, SoFloor f, List<string> problems)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(s => s.Name == f.ScheduleName);
            if (existing != null) return existing;
            if (f.IsPattern) return p.Schedule;
            var schedule = doc.GetElement(p.Schedule.Duplicate(ViewDuplicateOption.Duplicate)) as ViewSchedule;
            schedule.Name = f.ScheduleName;
            var def = schedule.Definition;
            bool set = false;
            for (int i = 0; i < def.GetFilterCount(); i++)
            {
                var filter = def.GetFilter(i);
                var field = def.GetField(filter.FieldId);
                bool level = field.ParameterId == new ElementId(BuiltInParameter.SCHEDULE_LEVEL_PARAM) || field.GetName().Equals("Level", StringComparison.OrdinalIgnoreCase);
                if (!level) continue;
                if (filter.IsElementIdValue) filter.SetValue(f.Level.Level.Id);
                else if (filter.IsStringValue) filter.SetValue(f.Level.Name);
                else continue;
                def.SetFilter(i, filter);
                set = true;
            }
            if (!set) problems.Add($"{f.ScheduleName}: the pattern schedule has no Level filter, so it lists every floor");
            return schedule;
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

        // ---------------------------------------------------------------- PDF

        /// <summary>Prints the sheets (in set order) to one PDF. Outside any transaction.</summary>
        public static string ExportPdf(Document doc, RuleSet rules, IList<ElementId> sheetIds, string folder, string name)
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

        private static string TypeName(Document doc, Element e) => doc.GetElement(e.GetTypeId())?.Name ?? "";

        private static void CopyParam(Parameter src, Parameter dst)
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
