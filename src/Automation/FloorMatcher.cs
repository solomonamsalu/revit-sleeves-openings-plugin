using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Drawing floor (FloorKey) -> Revit level. The user's saved choices are kept; every other floor is placed by
    /// FloorSequence: the floors in order on the levels in order, scored on the floor named in the level name, what the
    /// model holds on each level (LevelEvidence), the level's role (Cellar, Roof, Bulkhead), the elevation printed on the
    /// plan, how the plan's columns fit each level's columns and the storey heights between them.
    /// A level never serves two floors. Each match carries a confidence; a low one is shown for checking.
    /// </summary>
    public static class FloorMatcher
    {
        public class Match
        {
            public string Floor;
            public Level Level;
            public string How;      // "saved", "elevation", "name", "role", "order", "not used", or null when unmatched
            public FloorSequence.Confidence Confidence = FloorSequence.Confidence.High;
            /// <summary>The evidence, one line per reason (shown as the row's tooltip).</summary>
            public string Why = "";
            /// <summary>Unmatched, or matched with a close alternative (medium / low confidence): shown for the user to check.</summary>
            public bool NeedsCheck => Level == null || Confidence != FloorSequence.Confidence.High;
        }

        /// <summary>What the drawings and the model say beyond names; every part may be null.</summary>
        public class Evidence
        {
            /// <summary>Floor -> elevations printed on its plan (PdfSheet.Elevations).</summary>
            public IDictionary<string, List<double>> Printed;
            /// <summary>Floor -> level name -> how its plan's columns fit the level's columns (ColumnAligner.LevelFits).</summary>
            public IDictionary<string, Dictionary<string, double>> ColumnFits;
            /// <summary>Level name -> the floor the model's contents say (LevelEvidence).</summary>
            public IDictionary<string, FloorSequence.LevelHint> LevelHints;
            /// <summary>Floor -> its plan's title (a word it shares with a level name, e.g. STAIR, picks that level).</summary>
            public IDictionary<string, string> Titles;
        }

        public static List<Match> Run(IEnumerable<string> floors, LevelMap levels, AutomationInputs inputs, Evidence evidence = null)
        {
            evidence = evidence ?? new Evidence();
            var real = levels.All;                                   // ascending, reference levels excluded
            var result = floors.Distinct().OrderBy(FloorKey.Order).Select(k => new Match { Floor = k }).ToList();

            var input = new List<FloorSequence.FloorInfo>();
            foreach (var m in result)
            {
                inputs.FloorLevels.TryGetValue(m.Floor, out var saved);
                if (saved == AutomationInputs.NotUsed) { m.How = "not used"; continue; }
                List<double> values = null;
                evidence.Printed?.TryGetValue(m.Floor, out values);
                Dictionary<string, double> fits = null;
                evidence.ColumnFits?.TryGetValue(m.Floor, out fits);
                string title = null;
                evidence.Titles?.TryGetValue(m.Floor, out title);
                input.Add(new FloorSequence.FloorInfo { Key = m.Floor, Saved = saved, Printed = values ?? new List<double>(), ColumnFit = fits, Title = title });
            }

            FloorSequence.LevelHint Hint(string level) => evidence.LevelHints != null && evidence.LevelHints.TryGetValue(level, out var h) ? h : null;
            var info = real.Select(l => new FloorSequence.LevelInfo { Name = l.Name, Role = l.Role, Elevation = l.Elevation, Displayed = l.Level.Elevation, Hint = Hint(l.Name) }).ToList();
            foreach (var r in FloorSequence.Run(input, info))
            {
                var m = result.First(x => x.Floor == r.Floor);
                m.Level = r.Level >= 0 ? real[r.Level].Level : null;
                m.How = r.How;
                m.Confidence = r.Confidence;
                m.Why = string.Join("\n", r.Why);
            }
            return result;
        }
    }
}
