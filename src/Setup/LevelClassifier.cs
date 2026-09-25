using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Setup
{
    /// <summary>Ignore = reference level (top of steel, parapet, T.O. slab): never a floor, nothing is placed or propagated there.</summary>
    public enum LevelRole { Cellar, Apartment, Setback, Roof, Bulkhead, Ignore }

    public class ClassifiedLevel
    {
        public Level Level { get; set; }
        public LevelRole Role { get; set; }
        public string Name => Level.Name;
        /// <summary>Model Z (internal origin), for geometry and ordering; Level.Elevation follows the level type's base point.</summary>
        public double Elevation => Level.ProjectElevation;
    }

    /// <summary>
    /// The building as the manual sees it: lowest floor, apartment floors (top one is where
    /// placement starts), setbacks, main roof, bulkhead.
    /// </summary>
    public class LevelMap
    {
        /// <summary>Every level in the model, ascending, including ignored reference levels (for the setup grid).</summary>
        public List<ClassifiedLevel> Everything { get; } = new List<ClassifiedLevel>();

        /// <summary>The building's real levels, ascending: everything except Ignore. All placement/propagation/audit uses this.</summary>
        public List<ClassifiedLevel> All => Everything.Where(l => l.Role != LevelRole.Ignore).ToList();

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
            Everything.ToDictionary(l => l.Name, l => l.Role.ToString());
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
                var guess = GuessRole(lv.Name, rules.LevelClassification);
                if (saved != null && saved.LevelRoles.TryGetValue(lv.Name, out var savedRole)
                    && System.Enum.TryParse(savedRole, out LevelRole parsed))
                    // Apartment is the fallback guess, not a decision: a reference level saved as Apartment
                    // before the Ignore role existed still becomes Ignore. Any other saved role is respected.
                    role = parsed == LevelRole.Apartment && guess == LevelRole.Ignore ? guess : parsed;
                else
                    role = guess;

                map.Everything.Add(new ClassifiedLevel { Level = lv, Role = role });
            }
            return map;
        }

        public static LevelRole GuessRole(string name, LevelClassificationRules rx)
        {
            if (Match(name, rx.Ignore)) return LevelRole.Ignore;
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
