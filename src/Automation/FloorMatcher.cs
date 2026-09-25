using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Drawing floor (FloorKey) -> Revit level. Pass 1: the user's saved choice, the level's role (Cellar, Roof,
    /// Bulkhead), the floor named in the level name ("5TH FLOOR", "03.3-TH FLOOR", "Level 5", "FIFTH FLR").
    /// Pass 2, only for floors still unmatched: count up from the nearest matched floor below. A level is never
    /// given to two floors; a clash is left unmatched and shown.
    /// </summary>
    public static class FloorMatcher
    {
        public class Match
        {
            public string Floor;
            public Level Level;
            public string How;      // "saved", "role", "name", "order", "not used", "clash", or null when unmatched
            /// <summary>Several levels carry this floor's name ("06.6-TH FLOOR" and "06.6 TH FLOOR NEW"); treated as the same floor.</summary>
            public List<string> Candidates;
            public bool NeedsCheck => Level == null || How == "clash";
        }

        public static List<Match> Run(IEnumerable<string> floors, LevelMap levels, AutomationInputs inputs)
        {
            var real = levels.All;                                   // ascending, reference levels excluded
            var result = floors.Distinct().OrderBy(FloorKey.Order).Select(k => new Match { Floor = k }).ToList();

            // ---- pass 1: saved, role, name
            foreach (var m in result)
            {
                inputs.FloorLevels.TryGetValue(m.Floor, out var saved);
                if (saved == AutomationInputs.NotUsed) { m.How = "not used"; continue; }
                if (saved != null && real.FirstOrDefault(l => l.Name == saved) is ClassifiedLevel s) { Set(m, s, "saved"); continue; }
                if (ByRole(m.Floor, levels) is ClassifiedLevel r) { Set(m, r, "role"); continue; }
                var named = real.Where(l => l.Role != LevelRole.Roof && l.Role != LevelRole.Bulkhead && FloorKey.FromLevelName(l.Name) == m.Floor).ToList();
                if (named.Count == 1) Set(m, named[0], "name");
                else if (named.Count > 1) m.Candidates = named.Select(l => l.Name).ToList();
            }

            // ---- pass 2: count up from the nearest matched floor below (levels between roles are floors in order)
            var floorsOnly = real.Where(l => l.Role != LevelRole.Roof && l.Role != LevelRole.Bulkhead).ToList();
            foreach (var m in result.Where(x => x.Level == null && x.How == null && Number(x.Floor) > 0))
            {
                int n = Number(m.Floor);
                var anchor = result.Where(x => x.Level != null && !x.How.StartsWith("order") && Number(x.Floor) >= 0 && Number(x.Floor) < n)
                                   .OrderByDescending(x => Number(x.Floor)).FirstOrDefault();
                int from = anchor == null ? -1 : floorsOnly.FindIndex(l => l.Level.Id == anchor.Level.Id);
                int steps = anchor == null ? n : n - Number(anchor.Floor);
                // with no anchor, floor 1 is the first level above the cellar(s)
                if (anchor == null) from = floorsOnly.FindLastIndex(l => l.Role == LevelRole.Cellar);
                int i = from + steps;
                if (i >= 0 && i < floorsOnly.Count)
                {
                    // Several levels with this floor's name count as the same floor: take the one at the expected position.
                    if (m.Candidates == null) Set(m, floorsOnly[i], "order");
                    else if (m.Candidates.Contains(floorsOnly[i].Name)) Set(m, floorsOnly[i], "name");
                }
                if (m.Level == null && m.Candidates != null)
                    Set(m, floorsOnly.First(l => l.Name == m.Candidates[0]), "name");     // lowest of the same-named levels
            }

            // ---- a level may serve one floor only
            foreach (var g in result.Where(x => x.Level != null).GroupBy(x => x.Level.Id).Where(g => g.Count() > 1))
                foreach (var m in g.Where(x => x.How.StartsWith("order") || x.How == "name").ToList())
                    if (g.Count(x => x.Level != null) > 1) { m.Level = null; m.How = "clash"; }
            return result;
        }

        private static void Set(Match m, ClassifiedLevel l, string how) { m.Level = l.Level; m.How = how; }

        /// <summary>CELLAR = 0, F1.. = n, ROOF/BULKHEAD = -1 (not counted).</summary>
        private static int Number(string key) =>
            key == FloorKey.Cellar ? 0 : key.StartsWith("F") && int.TryParse(key.Substring(1), out int n) ? n : -1;

        private static ClassifiedLevel ByRole(string key, LevelMap levels)
        {
            switch (key)
            {
                case FloorKey.Roof: return levels.MainRoof;
                case FloorKey.Bulkhead: return levels.Bulkhead;
                case FloorKey.Cellar:
                    var cellars = levels.All.Where(l => l.Role == LevelRole.Cellar).ToList();
                    return cellars.FirstOrDefault(l => FloorKey.FromLevelName(l.Name) == FloorKey.Cellar)
                        ?? (cellars.Count == 1 ? cellars[0] : cellars.OrderByDescending(l => l.Elevation).FirstOrDefault());
                default: return null;
            }
        }
    }
}
