using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>
    /// PDF-only mode: the model's columns (structural and architectural, from this model and its loaded links, as the
    /// placement guards read them) and the level elevations, for lining the PDF plans up by their columns.
    /// </summary>
    public static class RevitColumnCollector
    {
        public static RevitColumns Collect(Document doc, RuleSet rules, LevelMap levels)
        {
            var result = new RevitColumns();
            var links = new LinkedModels(doc, rules);
            var sources = new System.Collections.Generic.HashSet<string>();
            foreach (var (e, box, source) in links.Collect(BuiltInCategory.OST_StructuralColumns).Concat(links.Collect(BuiltInCategory.OST_Columns)))
            {
                string from = source.IsHost ? "this model" : source.Name;
                result.Columns.Add(new ColumnPoint
                {
                    X = (box.Min.X + box.Max.X) / 2, Y = (box.Min.Y + box.Max.Y) / 2, W = box.Max.X - box.Min.X, L = box.Max.Y - box.Min.Y,
                    MinZ = box.Min.Z, MaxZ = box.Max.Z, Source = from
                });
                sources.Add(from);
            }
            foreach (var l in levels.Everything) result.Levels[l.Name] = l.Elevation;
            result.Source = sources.Count == 0 ? "no columns" : string.Join(", ", sources.OrderBy(s => s));
            return result;
        }
    }
}
