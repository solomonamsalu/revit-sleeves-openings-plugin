using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>Fills SO System / SO Riser / SO Size on every stamped opening (needed for openings placed before F10).</summary>
    [Transaction(TransactionMode.Manual)]
    public class SyncParametersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                int n = Sync(doc, rules, state);
                TaskDialog.Show("Sync Parameters", $"{n} opening(s) updated: SO System, SO Riser, SO Size now match the add-in stamps.");
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; App.Log("Sync failed: " + ex); return Result.Failed; }
        }

        public static int Sync(Document doc, Rules.RuleSet rules, ProjectState state)
        {
            int n = 0;
            using (var t = new Transaction(doc, "Sleeves & Openings: Sync parameters"))
            {
                t.Start();
                var cats = SharedParams.MappedCategories(doc, rules, state);
                foreach (var o in RiserIndex.AllOpenings(doc))
                    if (o.Instance.Category != null && cats.All(c => c.Id != o.Instance.Category.Id)) cats.Add(o.Instance.Category);
                SharedParams.EnsureBound(doc, cats);
                foreach (var o in RiserIndex.AllOpenings(doc)) { SharedParams.Write(o.Instance, o.Data); n++; }
                t.Commit();
            }
            return n;
        }
    }

    /// <summary>F10: one schedule per opening category listing system, riser, size, level and name.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                SyncParametersCommand.Sync(doc, rules, state);

                var openings = RiserIndex.AllOpenings(doc);
                var categories = openings.Select(o => o.Instance.Category).Where(c => c != null)
                    .GroupBy(c => c.Id.IntegerValue).Select(g => g.First()).ToList();
                if (categories.Count == 0)
                {
                    TaskDialog.Show("Schedule", "No add-in openings in the model yet.");
                    return Result.Cancelled;
                }

                var created = new List<string>();
                ViewSchedule first = null;
                using (var t = new Transaction(doc, "Sleeves & Openings: Schedule"))
                {
                    t.Start();
                    foreach (var cat in categories)
                    {
                        string name = "Sleeves & Openings - " + cat.Name;
                        var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => v.Name == name);
                        if (existing != null) { created.Add(name + " (already exists, left unchanged)"); first = first ?? existing; continue; }

                        var schedule = ViewSchedule.CreateSchedule(doc, cat.Id);
                        schedule.Name = name;
                        var def = schedule.Definition;
                        var fields = def.GetSchedulableFields();
                        ScheduleField Add(string fieldName)
                        {
                            var sf = fields.FirstOrDefault(f => f.GetName(doc) == fieldName);
                            return sf == null ? null : def.AddField(sf);
                        }
                        var level = Add("Level");
                        var riser = Add(SharedParams.Riser);
                        Add(SharedParams.System);
                        Add(SharedParams.Size);
                        Add("Family and Type");
                        Add("Comments");
                        var count = Add("Count");
                        if (level != null) def.AddSortGroupField(new ScheduleSortGroupField(level.FieldId));
                        if (riser != null) def.AddSortGroupField(new ScheduleSortGroupField(riser.FieldId));
                        def.IsItemized = true;
                        created.Add(name);
                        first = first ?? schedule;
                    }
                    t.Commit();
                }

                if (first != null) uidoc.ActiveView = first;
                TaskDialog.Show("Schedule", "Schedules:\n  " + string.Join("\n  ", created));
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; App.Log("Schedule failed: " + ex); return Result.Failed; }
        }
    }

    /// <summary>F10: tag every add-in opening in the active plan with the category's default tag (shows SO Size / SO Riser if the tag reads them).</summary>
    [Transaction(TransactionMode.Manual)]
    public class TagCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                if (!(doc.ActiveView is ViewPlan plan) || plan.GenLevel == null)
                {
                    TaskDialog.Show("Tag Openings", "Open a floor plan first — tags are placed in the active plan.");
                    return Result.Cancelled;
                }

                SyncParametersCommand.Sync(doc, App.Rules(doc), ProjectStore.Load(doc));

                var onLevel = RiserIndex.AllOpenings(doc).Where(o => o.Level.Id == plan.GenLevel.Id).ToList();
                var tagged = new HashSet<ElementId>(new FilteredElementCollector(doc, plan.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                    .SelectMany(t => t.GetTaggedLocalElementIds()));

                int n = 0, skipped = 0;
                var missing = new HashSet<string>();
                using (var t = new Transaction(doc, "Sleeves & Openings: Tag openings"))
                {
                    t.Start();
                    foreach (var o in onLevel)
                    {
                        if (tagged.Contains(o.Instance.Id)) { skipped++; continue; }
                        double hw = Units.InchesToFeet((o.Data.Width ?? o.Data.Diameter ?? 0) / 2);
                        var head = new XYZ(o.Point.X + hw + 1.0, o.Point.Y + 1.0, o.Point.Z);
                        try
                        {
                            IndependentTag.Create(doc, plan.Id, new Reference(o.Instance), true, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, head);
                            n++;
                        }
                        catch (Exception)
                        {
                            missing.Add(o.Instance.Category?.Name ?? "?");
                        }
                    }
                    t.Commit();
                }

                var msg = $"{n} tag(s) placed, {skipped} already tagged.";
                if (missing.Count > 0)
                    msg += "\n\nNo tag family loaded for: " + string.Join(", ", missing) +
                           ".\nLoad a tag for that category (Insert → Load Family → Annotations) whose label reads 'SO Size' or 'SO Riser', then run again.";
                TaskDialog.Show("Tag Openings", msg);
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; App.Log("Tag failed: " + ex); return Result.Failed; }
        }
    }
}
