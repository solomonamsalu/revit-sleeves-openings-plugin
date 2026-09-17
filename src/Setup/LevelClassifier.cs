using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Setup
{
    public enum LevelRole { Cellar, Apartment, Setback, Roof, Bulkhead }

    public class ClassifiedLevel
    {
        public Level Level { get; set; }
        public LevelRole Role { get; set; }
        public string Name => Level.Name;
        public double Elevation => Level.Elevation;
    }

    /// <summary>
    /// The building as the manual sees it: lowest floor, apartment floors (top one is where
    /// placement starts), setbacks, main roof, bulkhead.
    /// </summary>
    public class LevelMap
    {
        public List<ClassifiedLevel> All { get; } = new List<ClassifiedLevel>();   // ascending elevation

        public IEnumerable<ClassifiedLevel> Apartments => All.Where(l => l.Role == LevelRole.Apartment);
        public ClassifiedLevel Lowest => All.FirstOrDefault();
        public ClassifiedLevel HighestApartment => Apartments.LastOrDefault();
        public ClassifiedLevel MainRoof => All.Where(l => l.Role == LevelRole.Roof).OrderBy(l => l.Elevation).LastOrDefault();
        public IEnumerable<ClassifiedLevel> Setbacks => All.Where(l => l.Role == LevelRole.Setback);
        public ClassifiedLevel Bulkhead => All.LastOrDefault(l => l.Role == LevelRole.Bulkhead);

        public ClassifiedLevel Above(Level level)
        {
            int i = All.FindIndex(l => l.Level.Id == level.Id);
            return i >= 0 && i + 1 < All.Count ? All[i + 1] : null;
        }

        public ClassifiedLevel Below(Level level)
        {
            int i = All.FindIndex(l => l.Level.Id == level.Id);
            return i > 0 ? All[i - 1] : null;
        }

        public Dictionary<string, string> ToRoles() =>
            All.ToDictionary(l => l.Name, l => l.Role.ToString());
    }

    public static class LevelClassifier
    {
        /// <summary>
        /// Classifies by saved roles first, then by name regex from rules.json,
        /// otherwise Apartment. Levels are ordered by elevation.
        /// </summary>
        public static LevelMap Classify(Document doc, RuleSet rules, ProjectState saved = null)
        {
            var map = new LevelMap();
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation);

            foreach (var lv in levels)
            {
                LevelRole role;
                if (saved != null && saved.LevelRoles.TryGetValue(lv.Name, out var savedRole)
                    && System.Enum.TryParse(savedRole, out LevelRole parsed))
                    role = parsed;
                else
                    role = GuessRole(lv.Name, rules.LevelClassification);

                map.All.Add(new ClassifiedLevel { Level = lv, Role = role });
            }
            return map;
        }

        public static LevelRole GuessRole(string name, LevelClassificationRules rx)
        {
            if (Match(name, rx.Bulkhead)) return LevelRole.Bulkhead;
            if (Match(name, rx.Roof)) return LevelRole.Roof;
            if (Match(name, rx.Setback)) return LevelRole.Setback;
            if (Match(name, rx.Cellar)) return LevelRole.Cellar;
            return LevelRole.Apartment;
        }

        private static bool Match(string name, string pattern) =>
            !string.IsNullOrEmpty(pattern) && Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);
    }
}
