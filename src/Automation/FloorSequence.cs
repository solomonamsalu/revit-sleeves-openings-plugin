using System;
using System.Collections.Generic;
using System.Linq;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Drawing floors -> Revit levels as one ordered assignment, free of the Revit API (FloorMatcher feeds it).
    /// Floors go up as levels go up, so the drawing floors (cellar, 1st..nth, roof, bulkhead) are laid on the levels in
    /// that order. Each pairing is scored on its evidence: the floor named in the level name, what the model holds on the
    /// level (room numbers, linked models' levels, xrefs, views: LevelEvidence), the level's role, the elevation printed on
    /// the plan, and how well the plan's columns fit the columns standing on the level (ColumnAligner.LevelFits).
    /// Each step between two numbered floors is scored on the levels between them: one storey of the
    /// building's usual height per floor number. A short gap (mezzanine, landing) is passed over, a missing storey is not.
    /// The best-scoring order wins (dynamic programming), so typical floors with nothing in their names are placed
    /// between the floors that do name themselves ("sandwich"). A floor's confidence is how much worse the best order
    /// gets without its level: high when the floors around it leave no choice, low when an extra level between does.
    /// Printed elevations are compared after finding the datum: the drawings' datum (city / +0'-0" at the 1st floor) is
    /// rarely Revit's, so offsets where several floors agree are tried and the best order over all of them is kept.
    /// </summary>
    public static class FloorSequence
    {
        public enum Confidence { High, Medium, Low }

        public class LevelInfo
        {
            public string Name;
            public LevelRole Role;
            /// <summary>Model elevation (feet), the levels' order.</summary>
            public double Elevation;
            /// <summary>The elevation the level head shows (feet): the project's datum, the one most likely printed on the drawings.</summary>
            public double Displayed;
            /// <summary>The floor the model's contents on this level say it is (LevelEvidence); null when none or they disagree.</summary>
            public LevelHint Hint;
        }

        /// <summary>The floor a level holds, from what the model has on it rather than its name.</summary>
        public class LevelHint
        {
            public string Floor;
            /// <summary>Independent sources that agree (rooms, linked levels, xrefs, views).</summary>
            public int Sources;
            public List<string> Why = new List<string>();
        }

        public class FloorInfo
        {
            /// <summary>FloorKey: CELLAR, F1.., ROOF, BULKHEAD.</summary>
            public string Key;
            /// <summary>Elevations printed on the floor's plan (feet), every datum shown; empty when none.</summary>
            public IList<double> Printed = new List<double>();
            /// <summary>Level name the user chose for this floor before; null for none.</summary>
            public string Saved;
            /// <summary>Level name -> how well the plan's columns fit the columns standing on that level (0..1); null when not read.</summary>
            public IDictionary<string, double> ColumnFit;
            /// <summary>The plan's title ("PLUMBING - STAIR BULKHEAD PLAN"): a word it shares with a level name settles which of two alike levels it is.</summary>
            public string Title;
        }

        public class Result
        {
            public string Floor;
            /// <summary>Index into the levels given; -1 when no level fits.</summary>
            public int Level = -1;
            /// <summary>The strongest evidence: "saved", "elevation", "name", "model", "columns", "role", "order"; null when unmatched.</summary>
            public string How;
            public Confidence Confidence;
            public List<string> Why = new List<string>();
        }

        // ---- scores (points). A floor left unmatched costs Miss: worse than a level with no evidence, better than one named for another floor.
        private const double NameScore = 10, ElevExact = 12, ElevNear = 6, ElevWrong = -12, Miss = -8;
        private const double RoleMain = 8, RoleOther = 3, RoleWrong = -4, CellarForFloor = -8, Setback = -1, HighestCellar = 1;
        private const double SkipCost = 1, HeightCost = 6, LeadCost = 1;
        private const double HintScore = 8, HintExtra = 2, HintMax = 12, WordScore = 6;
        private const double ColMatch = 3, ColMiss = 6, ColMinBest = 0.5, ColSame = 0.05, ColSlope = 30;
        private const double High = 6, Medium = 2;
        private const double ExactTol = 0.25, NearTol = 0.75, WrongTol = 2.0, SameLevelTol = 1.0, SameHeightTol = 0.1;
        /// <summary>At most this many floors in a row stay unmatched between two matched ones (keeps a 40-storey tower fast).</summary>
        private const int MaxGap = 6;
        private const int MaxOffsets = 4;

        private class Datum
        {
            public string Describe;
            public Func<int, double> Value;     // level index -> the value the drawings would print for it
            public double[,] Scores;            // Elev() per floor and level, computed once
        }

        private class Model
        {
            public List<FloorInfo> Floors;
            public List<LevelInfo> Levels;
            public string[] LevelKeys;
            public int[] Numbers;                // Number() of each floor
            public double[] Z;                   // level elevations
            public double[,] Col;                // column-fit score of floor i on level k
            public bool[] ColDecides;            // the floor's columns tell levels apart
            public string[,] Word;               // a word the floor's title and the level's name share ("STAIR"), null for none
            public bool[] Worded;                // the title names one of the levels of its role: no "main roof / top bulkhead" default
            public double? Storey;
            public int MainRoof = -1, TopBulkhead = -1, HighestCellarIdx = -1;
            public int[] Saved;                  // level index per floor, -1 none
            public bool SavedInside = true;      // saved floors take part in the order (false when the user's choices are out of order)
        }

        /// <summary>Matches <paramref name="floors"/> (any order) to <paramref name="levels"/> (ascending, reference levels left out).</summary>
        public static List<Result> Run(IEnumerable<FloorInfo> floors, IList<LevelInfo> levels)
        {
            var m = new Model
            {
                Floors = floors.OrderBy(f => FloorKey.Order(f.Key)).ToList(),
                Levels = levels.ToList(),
            };
            // "Level 0" names no floor a drawing can have: no evidence (not "another floor")
            m.LevelKeys = m.Levels.Select(l => FloorKey.FromLevelName(l.Name)).Select(k => k == "F0" ? null : k).ToArray();
            m.Numbers = m.Floors.Select(f => Number(f.Key)).ToArray();
            m.Z = m.Levels.Select(l => l.Elevation).ToArray();
            m.MainRoof = m.Levels.FindLastIndex(l => l.Role == LevelRole.Roof);
            m.TopBulkhead = m.Levels.FindLastIndex(l => l.Role == LevelRole.Bulkhead);
            m.HighestCellarIdx = m.Levels.FindLastIndex(l => l.Role == LevelRole.Cellar);
            m.Storey = StoreyHeight(m.Levels);
            m.Saved = m.Floors.Select(f => f.Saved == null ? -1 : m.Levels.FindIndex(l => l.Name == f.Saved)).ToArray();
            ColumnScores(m);
            TitleWords(m);

            var results = m.Floors.Select(f => new Result { Floor = f.Key }).ToList();
            if (m.Levels.Count == 0 || m.Floors.Count == 0)
            {
                foreach (var r in results) { r.Confidence = Confidence.Low; r.Why.Add("the model has no levels to match"); }
                return results;
            }

            var datums = Datums(m);
            var (best, bestDatum) = BestOver(m, datums, null);
            if (double.IsNegativeInfinity(best.Score))
            {
                // the saved choices are out of order with each other: keep them as chosen, order the rest without them
                m.SavedInside = false;
                (best, bestDatum) = BestOver(m, datums, null);
            }

            for (int i = 0; i < m.Floors.Count; i++)
            {
                var r = results[i];
                int k = best.Assign[i];
                if (m.Saved[i] >= 0) { r.Level = m.Saved[i]; r.How = "saved"; r.Confidence = Confidence.High; r.Why.Add("your choice from an earlier run"); continue; }
                if (k < 0)
                {
                    r.Confidence = Confidence.Low;
                    r.Why.Add(m.Floors[i].Printed.Count > 0 ? "no level fits the floor's place in the order or its printed elevation"
                                                            : "no level fits the floor's place in the order");
                    continue;
                }
                r.Level = k;

                // confidence: the best order without this level (and its duplicates) under any datum
                var forbid = Same(m, k);
                var (alt, _) = BestOver(m, datums, (i, forbid));
                double margin = best.Score - alt.Score;
                r.Confidence = margin >= High ? Confidence.High : margin >= Medium ? Confidence.Medium : Confidence.Low;

                Explain(m, bestDatum, best.Assign, i, k, r);
                if (alt.Assign != null && alt.Assign[i] >= 0 && r.Confidence != Confidence.High)
                    r.Why.Add($"{m.Levels[alt.Assign[i]].Name} would fit almost as well");
            }
            return results;
        }

        // ------------------------------------------------------------------ evidence

        /// <summary>Score of floor i on level k (without the elevation); -inf when impossible.</summary>
        private static double Node(Model m, int i, int k)
        {
            if (m.Saved[i] >= 0 && m.SavedInside) return k == m.Saved[i] ? 0 : double.NegativeInfinity;
            if (!m.SavedInside && m.Saved.Contains(k)) return double.NegativeInfinity;

            string d = m.Floors[i].Key;
            var l = m.Levels[k];
            bool upper = d == FloorKey.Roof || d == FloorKey.Bulkhead;
            bool topRole = l.Role == LevelRole.Roof || l.Role == LevelRole.Bulkhead;
            if (!upper && topRole) return double.NegativeInfinity;                         // a floor plan is never on the roof level
            if (upper && l.Role == LevelRole.Cellar) return double.NegativeInfinity;
            if (d == FloorKey.Roof && l.Role == LevelRole.Bulkhead) return double.NegativeInfinity;

            double s = m.Col[i, k];
            if (m.LevelKeys[k] != null) s += m.LevelKeys[k] == d ? NameScore : -NameScore;
            if (l.Hint?.Floor != null)
            {
                double hint = Math.Min(HintMax, HintScore + HintExtra * (l.Hint.Sources - 1));
                s += l.Hint.Floor == d ? hint : -hint;
            }
            if (d == FloorKey.Cellar) s += l.Role == LevelRole.Cellar ? RoleOther + (k == m.HighestCellarIdx ? HighestCellar : 0) : RoleWrong;
            else if (d == FloorKey.Roof) s += l.Role == LevelRole.Roof ? (k == m.MainRoof && !m.Worded[i] ? RoleMain : RoleOther) : RoleWrong;
            else if (d == FloorKey.Bulkhead) s += l.Role == LevelRole.Bulkhead ? (k == m.TopBulkhead && !m.Worded[i] ? RoleMain : RoleOther) : RoleWrong;
            if (m.Word[i, k] != null) s += WordScore;
            else if (l.Role == LevelRole.Cellar) s += CellarForFloor;
            else if (l.Role == LevelRole.Setback) s += Setback;
            return s;
        }

        /// <summary>
        /// The printed elevation on level k under the datum: on it (+), near it, or pointing at another level (-). A value
        /// that lands on no level is another datum's ("+16'-7\"" next to "37.78'") and says nothing.
        /// </summary>
        private static double Elev(Model m, Datum datum, int i, int k)
        {
            if (datum?.Value == null) return 0;
            if (datum.Scores == null)
            {
                datum.Scores = new double[m.Floors.Count, m.Levels.Count];
                for (int f = 0; f < m.Floors.Count; f++)
                {
                    var printed = m.Floors[f].Printed;
                    if (printed.Count == 0) continue;
                    var lands = Enumerable.Range(0, m.Levels.Count).Where(x => Off(datum, printed, x) <= ExactTol).ToList();
                    for (int x = 0; x < m.Levels.Count; x++)
                    {
                        double off = Off(datum, printed, x);
                        datum.Scores[f, x] = off <= ExactTol ? ElevExact : off <= NearTol ? ElevNear
                                           : off > WrongTol && lands.Count > 0 ? ElevWrong : 0;
                    }
                }
            }
            return datum.Scores[i, k];
        }

        private static double Off(Datum datum, IList<double> printed, int k) => printed.Min(p => Math.Abs(datum.Value(k) - p));

        /// <summary>Step from floor p on level j to floor i on level k (p below i, nothing matched between).</summary>
        private static double Step(Model m, int p, int j, int i, int k)
        {
            int np = m.Numbers[p], ni = m.Numbers[i];
            if (np < 0 || ni < 0) return 0;                                   // to the roof / bulkhead: setbacks may lie between
            int g = ni - np;
            double cost = SkipCost * Math.Abs((k - j) - g);
            if (m.Storey is double H)
            {
                double h = m.Z[k] - m.Z[j];
                // short storeys are suspicious (a mezzanine or landing taken for a floor); tall ones are common (lobby, cellar)
                double dev = h < g * H ? 2 * (g * H - h) / H : 0.5 * (h - g * H) / H;
                cost += HeightCost * Math.Min(dev, 2);
            }
            return -cost;
        }

        /// <summary>First matched floor: floor n is expected n levels above the cellar(s), unless something says otherwise.</summary>
        private static double Lead(Model m, int i, int k)
        {
            int n = m.Numbers[i];
            if (n < 1) return 0;
            return -LeadCost * Math.Abs(k - (m.HighestCellarIdx + n));
        }

        // ------------------------------------------------------------------ search

        private class Solution { public double Score = double.NegativeInfinity; public int[] Assign; }

        private static (Solution, Datum) BestOver(Model m, List<Datum> datums, (int Floor, HashSet<int> Levels)? forbid)
        {
            Solution best = new Solution(); Datum at = null;
            foreach (var d in datums)
            {
                var s = Solve(m, d, forbid);
                if (s.Score > best.Score + 1e-9) { best = s; at = d; }
            }
            return (best, at);
        }

        /// <summary>
        /// Best ordered assignment: best[i,k] = floor i on level k with the floors below placed as well as possible;
        /// floors may stay unmatched (Miss) and levels unused. O(floors × MaxGap × levels²).
        /// </summary>
        private static Solution Solve(Model m, Datum datum, (int Floor, HashSet<int> Levels)? forbid)
        {
            int F = m.Floors.Count, L = m.Levels.Count;
            // misses of floors 0..i-1; a saved floor in the order cannot be missed (counted apart: -inf in a sum gives NaN)
            var before = new double[F + 1];
            var must = new int[F + 1];
            for (int i = 0; i < F; i++)
            {
                bool saved = m.Saved[i] >= 0;
                before[i + 1] = before[i] + (saved ? 0 : Miss);
                must[i + 1] = must[i] + (saved && m.SavedInside ? 1 : 0);
            }
            double Missed(int from, int to) => must[to] - must[from] > 0 ? double.NegativeInfinity : before[to] - before[from];   // floors from..to-1

            var best = new double[F, L];
            var back = new (int P, int J)[F, L];
            for (int i = 0; i < F; i++)
                for (int k = 0; k < L; k++)
                {
                    best[i, k] = double.NegativeInfinity;
                    back[i, k] = (-1, -1);
                    bool outside = !m.SavedInside && m.Saved[i] >= 0;          // kept as the user chose, not in the order
                    if (outside || (forbid.HasValue && forbid.Value.Floor == i && forbid.Value.Levels.Contains(k))) continue;
                    double node = Node(m, i, k);
                    if (double.IsNegativeInfinity(node)) continue;
                    node += Elev(m, datum, i, k);

                    double from = Missed(0, i) + Lead(m, i, k);
                    for (int p = Math.Max(0, i - 1 - MaxGap); p < i; p++)
                    {
                        if (!m.SavedInside && m.Saved[p] >= 0) continue;
                        double gap = Missed(p + 1, i);
                        if (double.IsNegativeInfinity(gap)) continue;
                        for (int j = 0; j < k; j++)
                        {
                            if (double.IsNegativeInfinity(best[p, j])) continue;
                            double v = best[p, j] + gap + Step(m, p, j, i, k);
                            if (v > from) { from = v; back[i, k] = (p, j); }
                        }
                    }
                    best[i, k] = node + from;
                }

            var sol = new Solution { Score = Missed(0, F), Assign = Enumerable.Repeat(-1, F).ToArray() };
            int bi = -1, bk = -1;
            for (int i = 0; i < F; i++)
                for (int k = 0; k < L; k++)
                {
                    double v = best[i, k] + Missed(i + 1, F);
                    if (v > sol.Score) { sol.Score = v; bi = i; bk = k; }
                }
            while (bi >= 0)
            {
                sol.Assign[bi] = bk;
                (bi, bk) = back[bi, bk];
            }
            return sol;
        }

        // ------------------------------------------------------------------ datums

        /// <summary>
        /// "none" always (printed values may be wrong) and the levels' displayed elevations. When the displayed elevations
        /// agree with the drawings (on half the floors that print one), that is the datum: nothing else is tried. Otherwise
        /// every offset from the model elevations on which at least two floors' printed values land on levels. A uniform
        /// building also agrees one storey up or down, so the offsets alone cannot pin typical floors: the names, roles
        /// and the floors around them decide, and the confidence says so.
        /// </summary>
        private static List<Datum> Datums(Model m)
        {
            var list = new List<Datum> { new Datum { Describe = null, Value = null } };
            var withPrinted = Enumerable.Range(0, m.Floors.Count).Where(i => m.Floors[i].Printed.Count > 0).ToList();
            if (withPrinted.Count == 0) return list;

            list.Add(new Datum { Describe = "the elevation shown on the Revit level", Value = k => m.Levels[k].Displayed });
            int shown = withPrinted.Count(f => m.Floors[f].Printed.Any(q => m.Levels.Any(x => Math.Abs(x.Displayed - q) <= ExactTol)));
            if (shown >= Math.Max(1, (withPrinted.Count + 1) / 2)) return list;

            var offsets = new List<(double Off, int Support)>();
            foreach (int i in withPrinted)
                foreach (double p in m.Floors[i].Printed)
                    foreach (var l in m.Levels)
                    {
                        double off = l.Elevation - p;
                        if (offsets.Any(o => Math.Abs(o.Off - off) < ExactTol)) continue;
                        int support = withPrinted.Count(f => m.Floors[f].Printed.Any(q => m.Levels.Any(x => Math.Abs(x.Elevation - q - off) <= ExactTol)));
                        if (support >= 2) offsets.Add((off, support));
                    }
            foreach (var o in offsets.OrderByDescending(o => o.Support).Take(MaxOffsets))
            {
                double off = o.Off;
                list.Add(new Datum { Describe = $"model elevation {(off >= 0 ? "-" : "+")} {Math.Abs(off):0.##}' (agrees on {o.Support} floors)", Value = k => m.Levels[k].Elevation - off });
            }
            return list;
        }

        // ------------------------------------------------------------------ helpers

        private static void Explain(Model m, Datum datum, int[] assign, int i, int k, Result r)
        {
            string d = m.Floors[i].Key;
            var l = m.Levels[k];
            bool elev = datum?.Value != null && m.Floors[i].Printed.Count > 0 && Off(datum, m.Floors[i].Printed, k) <= ExactTol;
            bool name = m.LevelKeys[k] == d;
            bool model = l.Hint?.Floor == d;
            bool columns = m.ColDecides[i] && m.Col[i, k] > 0;
            bool role = (d == FloorKey.Cellar && l.Role == LevelRole.Cellar) || (d == FloorKey.Roof && l.Role == LevelRole.Roof) || (d == FloorKey.Bulkhead && l.Role == LevelRole.Bulkhead);
            r.How = elev ? "elevation" : name ? "name" : model ? "model" : columns ? "columns" : role ? "role" : "order";

            if (elev)
            {
                double printed = m.Floors[i].Printed.OrderBy(p => Math.Abs(datum.Value(k) - p)).First();
                r.Why.Add($"printed elevation {printed:0.##}' = {datum.Describe}");
            }
            if (name) r.Why.Add($"the level name says {FloorKey.Describe(d)}");
            else if (m.LevelKeys[k] != null) r.Why.Add($"the level name says {FloorKey.Describe(m.LevelKeys[k])}");
            if (l.Hint?.Floor != null)
                r.Why.Add($"the model says {FloorKey.Describe(l.Hint.Floor)}: " + string.Join("; ", l.Hint.Why));
            if (columns && m.Floors[i].ColumnFit.TryGetValue(l.Name, out double fit))
                r.Why.Add($"its columns fit the columns on this level ({fit:P0}), not those of the levels that differ");
            if (role) r.Why.Add($"the level's role is {l.Role}");
            if (m.Word[i, k] != null) r.Why.Add($"the plan title and the level name both say {m.Word[i, k]}");

            int below = -1, above = -1;
            for (int p = i - 1; p >= 0 && below < 0; p--) if (assign[p] >= 0 || m.Saved[p] >= 0) below = p;
            for (int p = i + 1; p < m.Floors.Count && above < 0; p++) if (assign[p] >= 0 || m.Saved[p] >= 0) above = p;
            if (!name && !elev && !model)
                r.Why.Add("in order" + (below >= 0 ? $" above {FloorKey.Describe(m.Floors[below].Key)}" : "") +
                          (above >= 0 ? (below >= 0 ? " and" : "") + $" below {FloorKey.Describe(m.Floors[above].Key)}" : ""));
        }

        // words of a plan title or level name that say nothing about which level it is
        private static readonly HashSet<string> Plain = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PLAN", "PLANS", "FLOOR", "FLOORS", "FLR", "LEVEL", "PLUMBING", "MECHANICAL", "SPRINKLER", "FIRE", "PROTECTION", "HVAC",
            "ELECTRICAL", "ARCHITECTURAL", "STRUCTURAL", "FRAMING", "PARTIAL", "ENLARGED", "TYPICAL", "THRU", "THROUGH", "AND", "THE",
            "NEW", "EXISTING", "PROPOSED", "SLEEVES", "OPENINGS", "SCALE", "NOTES", "RISER", "DUCT", "PIPING", "CEILING", "REFLECTED"
        };

        private static List<string> Qualifiers(string text) =>
            System.Text.RegularExpressions.Regex.Matches((text ?? "").ToUpperInvariant(), @"[A-Z]{3,}").Cast<System.Text.RegularExpressions.Match>()
                .Select(x => x.Value).Where(w => !Plain.Contains(w) && FloorKey.Find(w).Count == 0).Distinct().ToList();

        /// <summary>
        /// "STAIR BULKHEAD PLAN" and the level "10.STAIR BULKHEAD": the shared word picks that one of the bulkheads (or
        /// roofs, cellars) instead of the default top one. A word matches as itself or by its first 4 letters (ELEV / ELEVATOR).
        /// </summary>
        private static void TitleWords(Model m)
        {
            m.Word = new string[m.Floors.Count, m.Levels.Count];
            m.Worded = new bool[m.Floors.Count];
            var levelWords = m.Levels.Select(l => Qualifiers(l.Name)).ToList();
            for (int i = 0; i < m.Floors.Count; i++)
            {
                var title = Qualifiers(m.Floors[i].Title);
                if (title.Count == 0) continue;
                for (int k = 0; k < m.Levels.Count; k++)
                    m.Word[i, k] = title.FirstOrDefault(t => levelWords[k].Any(w => w == t || (t.Length >= 4 && w.Length >= 4 && (t.StartsWith(w) || w.StartsWith(t)))));
                m.Worded[i] = Enumerable.Range(0, m.Levels.Count).Any(k => m.Word[i, k] != null);
            }
        }

        /// <summary>
        /// Column fit as score: a floor whose columns clearly fit some level (ColMinBest) gains on the levels that fit as
        /// well and loses on those that fit worse, more the worse they fit (a cellar plan on an upper floor, a setback floor
        /// on a full one). Identical typical floors fit their levels alike: no effect there, as it should be. Levels with no
        /// column standing on them (roof) lose too but do not count as the columns telling levels apart.
        /// </summary>
        private static void ColumnScores(Model m)
        {
            m.Col = new double[m.Floors.Count, m.Levels.Count];
            m.ColDecides = new bool[m.Floors.Count];
            for (int i = 0; i < m.Floors.Count; i++)
            {
                var fits = m.Floors[i].ColumnFit;
                if (fits == null || fits.Count == 0) continue;
                double Fit(int k) => fits.TryGetValue(m.Levels[k].Name, out double v) ? v : 0;
                double best = Enumerable.Range(0, m.Levels.Count).Max(Fit);
                if (best < ColMinBest) continue;
                for (int k = 0; k < m.Levels.Count; k++)
                {
                    double v = Fit(k), gap = best - v;
                    m.Col[i, k] = gap <= ColSame ? ColMatch : -Math.Min(ColMiss, ColSlope * (gap - ColSame));
                    if (gap > ColSame && v > 0) m.ColDecides[i] = true;
                }
                if (!m.ColDecides[i])                                  // fits every level alike: no evidence
                    for (int k = 0; k < m.Levels.Count; k++) m.Col[i, k] = 0;
            }
        }

        /// <summary>
        /// Level k and the levels that are the same floor: at the same height whatever their names, or the same floor in
        /// the name and almost the same height ("06.6-TH FLOOR" / "06.6 TH FLOOR NEW").
        /// </summary>
        private static HashSet<int> Same(Model m, int k)
        {
            var set = new HashSet<int> { k };
            for (int x = 0; x < m.Levels.Count; x++)
            {
                double dz = Math.Abs(m.Levels[x].Elevation - m.Levels[k].Elevation);
                if (dz < SameHeightTol || (m.LevelKeys[k] != null && m.LevelKeys[x] == m.LevelKeys[k] && dz < SameLevelTol)) set.Add(x);
            }
            return set;
        }

        /// <summary>The building's usual storey: the median gap between consecutive floors (apartment / setback), ignoring gaps under 6'.</summary>
        private static double? StoreyHeight(List<LevelInfo> levels)
        {
            var gaps = new List<double>();
            for (int k = 1; k < levels.Count; k++)
                if (IsStorey(levels[k - 1].Role) && IsStorey(levels[k].Role) && levels[k].Elevation - levels[k - 1].Elevation >= 6)
                    gaps.Add(levels[k].Elevation - levels[k - 1].Elevation);
            if (gaps.Count == 0) return null;
            gaps.Sort();
            return gaps[gaps.Count / 2];
        }

        private static bool IsStorey(LevelRole r) => r == LevelRole.Apartment || r == LevelRole.Setback;

        /// <summary>CELLAR = 0, Fn = n, ROOF / BULKHEAD = -1 (not counted).</summary>
        public static int Number(string key) =>
            key == FloorKey.Cellar ? 0 : key != null && key.StartsWith("F") && int.TryParse(key.Substring(1), out int n) ? n : -1;
    }
}
