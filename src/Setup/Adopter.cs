using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Setup
{
    /// <summary>One existing element the adopter recognised and what it would stamp on it.</summary>
    public class AdoptCandidate
    {
        public FamilyInstance Instance;
        public AdoptMatcher Matcher;
        public FamilyMapEntry Map;
        public Level Level;
        public XYZ Point;
        public string Label;
        public string System;
        public string Riser;
        public double? Width, Length, Diameter;
        public string SystemSource;          // "name" | "toggle" | "matcher" — for the report
        public string TogglePrefix;          // prefix of the ON size toggle, when the family has them
    }

    public class AdoptReport
    {
        public List<AdoptCandidate> Candidates = new List<AdoptCandidate>();
        public int AlreadyStamped;
        public int NoLevel;
        public Dictionary<string, int> UnmatchedFamilies = new Dictionary<string, int>();   // family name -> count
        public Dictionary<string, int> UnknownSystem = new Dictionary<string, int>();       // family : type -> count

        public IEnumerable<IGrouping<string, AdoptCandidate>> BySystem =>
            Candidates.GroupBy(c => c.System).OrderByDescending(g => g.Count());
    }

    /// <summary>
    /// Scans the model for openings/sleeves placed by hand (per rules.json "adopt") and works out system, size,
    /// label and riser id for each, so they can be stamped and take part in every later feature.
    /// Nothing here knows a family or system by name — matchers, toggle prefixes and label patterns come from the rules.
    /// </summary>
    public class Adopter
    {
        private readonly Document _doc;
        private readonly RuleSet _rules;
        private readonly ProjectState _state;
        private readonly AdoptRules _cfg;
        private readonly Dictionary<ElementId, FamilyMapEntry> _maps = new Dictionary<ElementId, FamilyMapEntry>();

        public Adopter(Document doc, RuleSet rules, ProjectState state)
        {
            _doc = doc; _rules = rules; _state = state; _cfg = rules.Adopt ?? new AdoptRules();
        }

        // ------------------------------------------------------------------ scan

        /// <summary>Dry run: what would be adopted. Must run outside a transaction (parameter probing opens its own).</summary>
        public AdoptReport Scan(bool includeStamped = false)
        {
            var report = new AdoptReport();
            var matchers = _cfg.Matchers.Where(m => !string.IsNullOrEmpty(m.Family)).ToList();
            if (matchers.Count == 0) return report;

            foreach (var inst in new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                var sym = inst.Symbol;
                if (sym?.Family == null || sym.Family.IsInPlace) continue;
                string fam = sym.Family.Name, type = sym.Name;

                var m = matchers.FirstOrDefault(x => Rx(x.Family).IsMatch(fam) && (string.IsNullOrEmpty(x.Type) || Rx(x.Type).IsMatch(type)));
                if (m == null)
                {
                    // Only report families that look like ours at all, otherwise every door and desk shows up.
                    if (Regex.IsMatch(fam, "sleeve|opening", RegexOptions.IgnoreCase))
                        report.UnmatchedFamilies[fam] = report.UnmatchedFamilies.TryGetValue(fam, out var n) ? n + 1 : 1;
                    continue;
                }

                if (!includeStamped && OpeningData.Read(inst) != null) { report.AlreadyStamped++; continue; }

                var level = RiserIndex.LevelOf(_doc, inst);
                var pt = (inst.Location as LocationPoint)?.Point;
                if (level == null || pt == null) { report.NoLevel++; continue; }

                var map = MapFor(sym, m.Role);
                var c = new AdoptCandidate { Instance = inst, Matcher = m, Map = map, Level = level, Point = pt, Label = ReadLabel(inst, sym) };
                ReadSize(c);
                if (!DecideSystem(c))
                {
                    string key = $"{fam} : {type}";
                    report.UnknownSystem[key] = report.UnknownSystem.TryGetValue(key, out var n) ? n + 1 : 1;
                    continue;
                }
                report.Candidates.Add(c);
            }

            PairAreaDrains(report.Candidates);
            AssignRisers(report.Candidates);
            return report;
        }

        /// <summary>Inside a transaction: stamps every candidate. Returns how many were written.</summary>
        public int Apply(IEnumerable<AdoptCandidate> candidates)
        {
            int n = 0;
            foreach (var c in candidates)
            {
                var data = new OpeningData
                {
                    System = c.System, Riser = c.Riser, Label = c.Label,
                    Width = c.Width, Length = c.Length, Diameter = c.Diameter,
                    Level = c.Level.Name, PlacedBy = Environment.UserName, Placed = DateTime.Now, Adopted = true
                };
                data.WriteTo(c.Instance);
                try { SharedParams.Write(c.Instance, data); } catch { /* shared params not bound yet — Sync Parameters fills them later */ }
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ pieces

        private static readonly Dictionary<string, Regex> RxCache = new Dictionary<string, Regex>();
        private static Regex Rx(string pattern)
        {
            if (!RxCache.TryGetValue(pattern, out var rx)) RxCache[pattern] = rx = new Regex(pattern, RegexOptions.IgnoreCase);
            return rx;
        }

        /// <summary>Parameter names for this symbol: the project/rules mapping when it is the same family, else guessed.</summary>
        private FamilyMapEntry MapFor(FamilySymbol sym, string role)
        {
            if (_maps.TryGetValue(sym.Id, out var cached)) return cached;
            var mapped = FamilyMapping.Get(_rules, _state, role ?? FamilyRole.RoundSleeve);
            FamilyMapEntry map;
            if (mapped != null && string.Equals(mapped.FamilyName, sym.Family.Name, StringComparison.OrdinalIgnoreCase))
                map = new FamilyMapEntry
                {
                    FamilyName = mapped.FamilyName, TypeName = sym.Name, WidthParam = mapped.WidthParam, LengthParam = mapped.LengthParam,
                    DiameterParam = mapped.DiameterParam, NameParam = mapped.NameParam, DownHeightParam = mapped.DownHeightParam,
                    SizeToggles = mapped.SizeToggles, SizeTogglePattern = mapped.SizeTogglePattern
                };
            else
            {
                // Same family as the rules' round sleeve? carry its toggle setup even if the role differs.
                var toggled = _rules.Families.Values.FirstOrDefault(f => f.SizeToggles != null && string.Equals(f.Family, sym.Family.Name, StringComparison.OrdinalIgnoreCase));
                map = new FamilyMapEntry { FamilyName = sym.Family.Name, TypeName = sym.Name, SizeToggles = toggled?.SizeToggles, SizeTogglePattern = toggled?.SizeTogglePattern };
            }
            try { FamilyMapping.GuessParams(_doc, sym, map); } catch { /* keep what we have */ }
            _maps[sym.Id] = map;
            return map;
        }

        private string ReadLabel(FamilyInstance inst, FamilySymbol sym)
        {
            var names = new List<string>();
            if (!string.IsNullOrEmpty(_maps.TryGetValue(sym.Id, out var m) ? m.NameParam : null)) names.Add(m.NameParam);
            names.AddRange(_cfg.NameParams ?? new List<string>());
            foreach (var n in names.Distinct())
            {
                var p = inst.LookupParameter(n) ?? sym.LookupParameter(n);
                if (p == null || p.StorageType != StorageType.String) continue;
                var v = p.AsString()?.Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        /// <summary>Size from the ON toggle (checkbox family) or from the mapped length parameters.</summary>
        private void ReadSize(AdoptCandidate c)
        {
            var inst = c.Instance;
            // Checkbox family: the ON "<prefix> <size>" toggle is the size (any round sleeve is tried, the pattern defaults)
            if (c.Matcher.Role == FamilyRole.RoundSleeve || c.Map.UsesSizeToggles || !string.IsNullOrEmpty(c.Map.SizeTogglePattern))
            {
                var on = SizeToggles.Find(inst, c.Map).Where(t => t.Param.AsInteger() == 1).ToList();
                if (on.Count > 0)
                {
                    var biggest = on.OrderByDescending(t => t.Size).First();
                    c.Diameter = biggest.Size; c.TogglePrefix = biggest.Prefix;
                    return;
                }
            }
            double? Len(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                var p = inst.LookupParameter(name) ?? inst.Symbol.LookupParameter(name);
                if (p == null || p.StorageType != StorageType.Double) return null;
                double v = Units.FeetToInches(p.AsDouble());
                return name.IndexOf("radius", StringComparison.OrdinalIgnoreCase) >= 0 ? v * 2 : v;
            }
            if (c.Matcher.Role == FamilyRole.RoundSleeve) c.Diameter = Len(c.Map.DiameterParam);
            if (!c.Diameter.HasValue)
            {
                c.Width = Len(c.Map.WidthParam);
                c.Length = Len(c.Map.LengthParam);
                if (!c.Width.HasValue && !c.Length.HasValue) c.Diameter = Len(c.Map.DiameterParam);
            }
        }

        /// <summary>1. label / type name patterns, 2. ON toggle prefix, 3. matcher default. False = nothing decided.</summary>
        private bool DecideSystem(AdoptCandidate c)
        {
            foreach (var text in new[] { c.Label, c.Instance.Symbol.Name })
            {
                if (string.IsNullOrEmpty(text)) continue;
                var hit = (_cfg.NameToSystem ?? new List<NameMatcher>()).FirstOrDefault(n => !string.IsNullOrEmpty(n.Match) && Rx(n.Match).IsMatch(text));
                if (hit != null && !string.IsNullOrEmpty(hit.System)) { c.System = hit.System; c.SystemSource = "name"; return true; }
            }

            if (c.TogglePrefix != null && _cfg.TogglePrefixToSystem != null)
            {
                var kv = _cfg.TogglePrefixToSystem.FirstOrDefault(x => string.Equals(x.Key, c.TogglePrefix, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(kv.Value)) { c.System = kv.Value; c.SystemSource = "toggle"; return true; }
            }

            if (!string.IsNullOrEmpty(c.Matcher.System)) { c.System = c.Matcher.System; c.SystemSource = "matcher"; return true; }
            return false;
        }

        /// <summary>Two storm sleeves of the area-drain size at the manual's c-c spacing on one level are an AD pair.</summary>
        private void PairAreaDrains(List<AdoptCandidate> all)
        {
            var st = _rules.Systems.Storm;
            double tol = _cfg.AreaDrainPairTolerance;
            var storms = all.Where(c => c.System == SystemKind.Storm.ToString() && c.Diameter.HasValue && Math.Abs(c.Diameter.Value - st.AreaDrainDiameter) < 0.51).ToList();
            foreach (var byLevel in storms.GroupBy(c => c.Level.Id))
            {
                var list = byLevel.ToList();
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        double cc = Units.FeetToInches(RiserRecord.Dist(list[i].Point, list[j].Point));
                        if (Math.Abs(cc - st.AreaDrainSpacingCenterToCenter) <= tol)
                            list[i].System = list[j].System = SystemKind.AreaDrain.ToString();
                    }
            }
        }

        /// <summary>
        /// Riser ids: a label that already looks like one (KX2, MD-1) is the riser id wherever it appears, so an
        /// offset between floors is still one riser and Final Check reports the offset. Everything else is clustered
        /// by system + XY across floors and numbered after the ids already used in the model.
        /// </summary>
        private void AssignRisers(List<AdoptCandidate> all)
        {
            var rxId = string.IsNullOrEmpty(_cfg.RiserIdFromName) ? null : Rx(_cfg.RiserIdFromName);
            double tol = Units.InchesToFeet(_cfg.RiserTolerance);
            var used = new HashSet<string>(RiserIndex.AllOpenings(_doc).Select(o => o.Riser).Where(r => r != null), StringComparer.OrdinalIgnoreCase);

            var rest = new List<AdoptCandidate>();
            foreach (var c in all)
            {
                if (rxId != null && !string.IsNullOrEmpty(c.Label) && rxId.IsMatch(c.Label)) { c.Riser = c.Label.ToUpperInvariant(); used.Add(c.Riser); }
                else rest.Add(c);
            }

            foreach (var bySystem in rest.GroupBy(c => c.System))
            {
                var clusters = new List<List<AdoptCandidate>>();
                foreach (var c in bySystem.OrderByDescending(x => x.Level.Elevation))
                {
                    var cl = clusters.FirstOrDefault(k => k.All(o => o.Level.Id != c.Level.Id) && RiserRecord.Dist(k[0].Point, c.Point) <= tol);
                    if (cl == null) clusters.Add(cl = new List<AdoptCandidate>());
                    cl.Add(c);
                }

                string prefix = RiserIndex.Prefix(bySystem.Key);
                int n = 0;
                foreach (var cl in clusters)
                {
                    string id;
                    do { n++; id = $"{prefix}-{n}"; } while (used.Contains(id));
                    used.Add(id);
                    foreach (var c in cl) c.Riser = id;
                }
            }
        }
    }
}
