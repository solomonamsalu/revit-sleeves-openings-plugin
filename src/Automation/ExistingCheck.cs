using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using SleevesOpenings.Risers;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>One sleeve/opening already in the model.</summary>
    public class ExistingItem
    {
        public ElementId Id;
        public string System;      // "?" when the family matched but no system could be decided
        public string Level;
        public double Elevation;
        public string Family;
        public string Source;      // "add-in" (stamped), "hand-placed" (matches rules.json adopt), "native opening"
        public XYZ Point;          // plan centre (feet); null when unknown
    }

    public class ExistingReport
    {
        public List<ExistingItem> Items = new List<ExistingItem>();
        /// <summary>Families that look like sleeves/openings but match nothing in rules.json "adopt" (name -> count).</summary>
        public Dictionary<string, int> UnknownFamilies = new Dictionary<string, int>();

        public bool Any => Items.Count > 0;

        public string Summary()
        {
            if (!Any && UnknownFamilies.Count == 0) return "No sleeves or openings in this model.";
            var sb = new StringBuilder();
            sb.AppendLine($"{Items.Count} sleeve(s)/opening(s) on {Items.Select(i => i.Level).Distinct().Count()} level(s):");
            foreach (var g in Items.GroupBy(i => i.System).OrderByDescending(g => g.Count()))
                sb.AppendLine($"  {g.Key}: {g.Count()}  ({string.Join(", ", g.GroupBy(i => i.Source).Select(s => $"{s.Count()} {s.Key}"))})");
            if (UnknownFamilies.Count > 0)
                sb.AppendLine($"Not recognised (family not in rules.json 'adopt'): " +
                              string.Join(", ", UnknownFamilies.Select(kv => $"{kv.Key} ×{kv.Value}")));
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Step 1 of Auto Run: what is already in the model. Uses the Adopt rules so hand-placed office families count,
    /// plus add-in stamps and native shaft/floor openings. Read-only; run outside a transaction.
    /// </summary>
    public static class ExistingCheck
    {
        public static ExistingReport Run(Document doc, RuleSet rules, ProjectState state)
        {
            var report = new ExistingReport();
            var seen = new HashSet<ElementId>();

            foreach (var o in RiserIndex.AllOpenings(doc))
            {
                seen.Add(o.Instance.Id);
                report.Items.Add(new ExistingItem
                {
                    Id = o.Instance.Id, System = o.Data.System ?? "?", Level = o.Level.Name, Elevation = o.Level.Elevation,
                    Family = o.Instance.Symbol.Family.Name, Source = o.Data.Adopted ? "adopted" : "add-in", Point = Centre(o.Instance)
                });
            }

            var scan = new Adopter(doc, rules, state).Scan();          // unstamped office families only
            foreach (var c in scan.Candidates.Where(c => seen.Add(c.Instance.Id)))
                report.Items.Add(new ExistingItem
                {
                    Id = c.Instance.Id, System = c.System, Level = c.Level.Name, Elevation = c.Level.Elevation,
                    Family = c.Instance.Symbol.Family.Name, Source = "hand-placed", Point = Centre(c.Instance)
                });
            foreach (var kv in scan.UnknownSystem) report.UnknownFamilies[kv.Key] = kv.Value;
            foreach (var kv in scan.UnmatchedFamilies) report.UnknownFamilies[kv.Key] = kv.Value;

            foreach (var op in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
            {
                var level = doc.GetElement(op.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level
                         ?? doc.GetElement(op.LevelId) as Level;
                report.Items.Add(new ExistingItem
                {
                    Id = op.Id, System = "Shaft/floor opening", Level = level?.Name ?? "?", Elevation = level?.Elevation ?? 0,
                    Family = op.Category?.Name ?? "Opening", Source = "native opening", Point = Centre(op)
                });
            }
            return report;
        }

        private static XYZ Centre(Element e)
        {
            if (e.Location is LocationPoint lp) return lp.Point;
            var box = e.get_BoundingBox(null);
            return box == null ? null : (box.Min + box.Max) / 2;
        }
    }
}
