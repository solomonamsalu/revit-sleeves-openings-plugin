using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Risers
{
    /// <summary>What to propagate: one source opening and the last level it should reach.</summary>
    public class PropagateItem
    {
        public OpeningRecord Source;
        public Level StopLevel;
    }

    public class PropagateReport
    {
        public int Created, Skipped;
        public List<string> Lines = new List<string>();
        public override string ToString() => string.Join("\n", Lines);
    }

    /// <summary>
    /// Manual rules 45-51: copy openings floor by floor, same location, same size, until the riser terminates.
    /// Uses view-to-view copy (the API equivalent of "Paste Aligned to Selected Levels") so hosted and
    /// level-based families both land on the target level. Runs inside the caller's transaction.
    /// </summary>
    public class Propagator
    {
        private readonly Document _doc;
        private readonly LevelMap _levels;
        private readonly List<OpeningRecord> _existing;
        private readonly List<ElementId> _tempViews = new List<ElementId>();

        public Propagator(Document doc, LevelMap levels)
        {
            _doc = doc; _levels = levels;
            _existing = RiserIndex.AllOpenings(doc);
        }

        public PropagateReport Run(View sourceView, IList<PropagateItem> items, bool skipExisting = true)
        {
            var report = new PropagateReport();

            // Group by target level so each level is one copy call.
            var byLevel = new Dictionary<ElementId, List<PropagateItem>>();
            foreach (var item in items)
            {
                foreach (var target in LevelsBetween(item.Source.Level, item.StopLevel))
                {
                    if (skipExisting && AlreadyThere(item.Source, target))
                    {
                        report.Skipped++;
                        report.Lines.Add($"  skip {item.Source.Riser} on {target.Name} (already present)");
                        continue;
                    }
                    if (!byLevel.TryGetValue(target.Id, out var list)) byLevel[target.Id] = list = new List<PropagateItem>();
                    list.Add(item);
                }
            }

            foreach (var kv in byLevel)
            {
                var target = _doc.GetElement(kv.Key) as Level;
                var destView = PlanViewFor(target);
                var ids = kv.Value.Select(i => i.Source.Instance.Id).Distinct().ToList();

                var copied = ElementTransformUtils.CopyElements(sourceView, ids, destView, Transform.Identity, new CopyPasteOptions());

                // Re-stamp copies with their new level (riser/system/size carry over with the entity).
                foreach (var id in copied)
                {
                    if (!(_doc.GetElement(id) is FamilyInstance inst)) continue;
                    var data = OpeningData.Read(inst);
                    if (data == null) continue;
                    data.Level = target.Name;
                    data.Placed = DateTime.Now;
                    data.PlacedBy = Environment.UserName;
                    data.WriteTo(inst);
                    SharedParams.Write(inst, data);
                }
                report.Created += copied.Count;
                report.Lines.Add($"{target.Name}: {copied.Count} copied");
            }

            foreach (var v in _tempViews) _doc.Delete(v);
            return report;
        }

        /// <summary>Copies one opening to one level (used by Final Check "add missing floor").</summary>
        public FamilyInstance CopyOne(OpeningRecord src, Level target)
        {
            var srcView = PlanViewFor(src.Level);
            var destView = PlanViewFor(target);
            var copied = ElementTransformUtils.CopyElements(srcView, new[] { src.Instance.Id }, destView, Transform.Identity, new CopyPasteOptions());
            FamilyInstance result = null;
            foreach (var id in copied)
            {
                if (!(_doc.GetElement(id) is FamilyInstance inst)) continue;
                var data = OpeningData.Read(inst);
                if (data != null) { data.Level = target.Name; data.Placed = DateTime.Now; data.PlacedBy = Environment.UserName; data.WriteTo(inst); SharedParams.Write(inst, data); }
                result = inst;
            }
            foreach (var v in _tempViews) _doc.Delete(v);
            _tempViews.Clear();
            return result;
        }

        /// <summary>Levels strictly between source and stop (inclusive of stop), nearest first.</summary>
        private IEnumerable<Level> LevelsBetween(Level source, Level stop)
        {
            double a = source.Elevation, b = stop.Elevation;
            if (Math.Abs(a - b) < 1e-9) yield break;
            var range = b < a
                ? _levels.All.Where(l => l.Elevation < a && l.Elevation >= b).OrderByDescending(l => l.Elevation)
                : _levels.All.Where(l => l.Elevation > a && l.Elevation <= b).OrderBy(l => l.Elevation);
            foreach (var l in range) yield return l.Level;
        }

        private bool AlreadyThere(OpeningRecord src, Level target) =>
            _existing.Any(o => o.Level.Id == target.Id && o.Riser == src.Riser
                               && RiserRecord.Dist(o.Point, src.Point) < Units.InchesToFeet(1));

        /// <summary>A floor plan on the level, or a temporary one created (and deleted after the copy).</summary>
        private View PlanViewFor(Level level)
        {
            var existing = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id)
                .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : 1)
                .FirstOrDefault();
            if (existing != null) return existing;

            var vft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(t => t.ViewFamily == ViewFamily.FloorPlan);
            var temp = ViewPlan.Create(_doc, vft.Id, level.Id);
            temp.Name = "SO temp " + Guid.NewGuid().ToString("N").Substring(0, 6);
            _tempViews.Add(temp.Id);
            return temp;
        }
    }
}
