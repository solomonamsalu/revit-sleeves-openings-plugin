using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Audit
{
    public enum Severity { Error, Warning, Info }

    public class AuditIssue
    {
        public Severity Severity;
        public string Rule;          // short manual reference, e.g. "Rule 27"
        public string Message;
        public string Level;
        public string Riser;
        public string System;
        public List<ElementId> Elements = new List<ElementId>();

        /// <summary>Set when the fix is unambiguous. Runs inside a transaction opened by the caller.</summary>
        public Action<Document> Fix;
        public string FixLabel;
        public bool CanFix => Fix != null;
    }

    /// <summary>
    /// F8: checks every stamped opening against the manual — sizes, structure, wall edges, spacing,
    /// riser continuity, completeness and naming. Issues with exactly one right answer carry a Fix.
    /// </summary>
    public class Auditor
    {
        private readonly Document _doc;
        private readonly RuleSet _rules;
        private readonly ProjectState _state;
        private readonly LevelMap _levels;
        private readonly List<OpeningRecord> _openings;
        private readonly List<AuditIssue> _issues = new List<AuditIssue>();
        private readonly Dictionary<ElementId, WallGeometry> _walls = new Dictionary<ElementId, WallGeometry>();

        /// <param name="serving">Sleeves already in the model that an Auto Run used for its risers (element id -> riser name): they
        /// take part in the riser checks under that name, read only (hand-placed ones are described as Adopt Existing would).</param>
        public Auditor(Document doc, RuleSet rules, ProjectState state, LevelMap levels, IDictionary<long, string> serving = null)
        {
            _doc = doc; _rules = rules; _state = state; _levels = levels;
            _openings = RiserIndex.AllOpenings(doc);
            if (serving != null && serving.Count > 0)
            {
                foreach (var o in _openings)
                    if (serving.TryGetValue(o.Instance.Id.Value, out var riser)) o.RiserOverride = riser;
                var known = new HashSet<long>(_openings.Select(o => o.Instance.Id.Value));
                if (serving.Keys.Any(id => !known.Contains(id)))
                    foreach (var c in new SleevesOpenings.Setup.Adopter(doc, rules, state).Scan().Candidates)
                    {
                        if (c.Level == null || c.Point == null || known.Contains(c.Instance.Id.Value) || !serving.TryGetValue(c.Instance.Id.Value, out var riser)) continue;
                        _openings.Add(new OpeningRecord
                        {
                            Instance = c.Instance, Level = c.Level, Point = c.Point, RiserOverride = riser,
                            Data = new OpeningData { System = c.System, Riser = riser, Label = c.Label, Width = c.Width, Length = c.Length, Diameter = c.Diameter, Level = c.Level.Name, Adopted = true }
                        });
                    }
            }
        }

        public int OpeningCount => _openings.Count;

        public List<AuditIssue> Run()
        {
            _issues.Clear();
            CheckSizes();
            CheckStructure();
            CheckSpacing();
            CheckRisers();
            CheckCompleteness();
            CheckNaming();
            FixtureAudit.Run(_doc, _rules, _state, _levels, _openings, (sev, rule, msg, o) => Add(sev, rule, msg, o), PlacerFor, _walls);
            return _issues.OrderBy(i => i.Severity).ThenBy(i => i.Level).ThenBy(i => i.Riser).ToList();
        }

        // ------------------------------------------------------------------ helpers

        private AuditIssue Add(Severity sev, string rule, string msg, OpeningRecord o, params OpeningRecord[] more)
        {
            var issue = new AuditIssue
            {
                Severity = sev, Rule = rule, Message = msg,
                Level = o?.Level.Name, Riser = o?.Riser, System = o?.Data.System
            };
            if (o != null) issue.Elements.Add(o.Instance.Id);
            issue.Elements.AddRange(more.Select(m => m.Instance.Id));
            _issues.Add(issue);
            return issue;
        }

        private WallGeometry WallsOn(Level level)
        {
            if (!_walls.TryGetValue(level.Id, out var w)) _walls[level.Id] = w = new WallGeometry(_doc, level);
            return w;
        }

        private LevelRole RoleOf(Level l) => _levels.All.FirstOrDefault(x => x.Level.Id == l.Id)?.Role ?? LevelRole.Apartment;
        private bool IsRoofLevel(Level l) { var r = RoleOf(l); return r == LevelRole.Roof || r == LevelRole.Setback; }

        private static double HalfW(OpeningRecord o) => (o.Data.Width ?? o.Data.Diameter ?? 0) / 2;
        private static double HalfL(OpeningRecord o) => (o.Data.Length ?? o.Data.Diameter ?? 0) / 2;
        private static double RadiusFt(OpeningRecord o) => Units.InchesToFeet(Math.Max(HalfW(o), HalfL(o)));

        private static double Gap(OpeningRecord a, OpeningRecord b)
        {
            double dx = Units.FeetToInches(Math.Abs(a.Point.X - b.Point.X)) - (HalfW(a) + HalfW(b));
            double dy = Units.FeetToInches(Math.Abs(a.Point.Y - b.Point.Y)) - (HalfL(a) + HalfL(b));
            return Math.Max(dx, dy);
        }

        private static double CenterDist(OpeningRecord a, OpeningRecord b) => Units.FeetToInches(RiserRecord.Dist(a.Point, b.Point));
        private static bool Near(double a, double b, double tol = 0.51) => Math.Abs(a - b) <= tol;

        private FamilyMapEntry MapFor(OpeningRecord o) => FamilyMapping.Get(_rules, _state, RoleFor(o));

        private double? Actual(OpeningRecord o, Func<FamilyMapEntry, string> pick)
        {
            var map = MapFor(o);
            // Checkbox-sized sleeve: the diameter is whichever "<prefix> <size>" toggle is on
            if (map != null && map.UsesSizeToggles && pick(map) == map.DiameterParam && o.Data.Diameter.HasValue
                && Enum.TryParse(o.Data.System, out SystemKind sys))
                return SizeToggles.Current(o.Instance, map, sys);
            var name = map != null ? pick(map) : null;
            if (string.IsNullOrEmpty(name)) return null;
            var p = o.Instance.LookupParameter(name) ?? o.Instance.Symbol.LookupParameter(name);
            if (p == null || p.StorageType != StorageType.Double) return null;
            double v = Units.FeetToInches(p.AsDouble());
            return name.IndexOf("radius", StringComparison.OrdinalIgnoreCase) >= 0 ? v * 2 : v;
        }

        private static string RoleFor(OpeningRecord o)
        {
            switch (o.Data.System)
            {
                case "Refrigeration": return FamilyRole.PipeReferenceOpening;
                case "Electrical": return o.Data.Diameter.HasValue ? FamilyRole.RoundSleeve : FamilyRole.ElectricalOpening;
                default: return o.Data.Diameter.HasValue ? FamilyRole.RoundSleeve : FamilyRole.RegularOpening;
            }
        }

        private Placer PlacerFor(Document doc) =>
            new Placer(doc, new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate));

        private void FixSize(AuditIssue issue, OpeningRecord o, double? w, double? l, double? d)
        {
            issue.FixLabel = d.HasValue ? $"Set to {Units.FormatInches(d.Value)}" : $"Set to {Units.FormatInches(w ?? 0)} x {Units.FormatInches(l ?? 0)}";
            issue.Fix = doc => PlacerFor(doc).Resize(o.Instance, MapFor(o), o.Data, w, l, d);
        }

        // ------------------------------------------------------------------ 1. sizes

        private void CheckSizes()
        {
            var s = _rules.Systems;
            foreach (var o in _openings)
            {
                var d = o.Data;
                bool roof = IsRoofLevel(o.Level);

                // Model drifted from what the add-in placed? Fix = restore the placed size.
                var aw = Actual(o, m => m.WidthParam); var al = Actual(o, m => m.LengthParam); var ad = Actual(o, m => m.DiameterParam);
                if (d.Width.HasValue && aw.HasValue && !Near(aw.Value, d.Width.Value))
                    FixSize(Add(Severity.Warning, "Size", $"Width in model is {Units.FormatInches(aw.Value)}, placed as {Units.FormatInches(d.Width.Value)}", o), o, d.Width, d.Length, null);
                if (d.Length.HasValue && al.HasValue && !Near(al.Value, d.Length.Value))
                    FixSize(Add(Severity.Warning, "Size", $"Length in model is {Units.FormatInches(al.Value)}, placed as {Units.FormatInches(d.Length.Value)}", o), o, d.Width, d.Length, null);
                if (d.Diameter.HasValue && ad.HasValue && !Near(ad.Value, d.Diameter.Value))
                    FixSize(Add(Severity.Warning, "Size", $"Diameter in model is {Units.FormatInches(ad.Value)}, placed as {Units.FormatInches(d.Diameter.Value)}", o), o, null, null, d.Diameter);

                // Fixed sizes from the manual — exactly one right answer, so each is fixable.
                switch (d.System)
                {
                    case "GarbageChute":
                        if (!Near(d.Width ?? 0, s.GarbageChute.FixedWidth) || !Near(d.Length ?? 0, s.GarbageChute.FixedLength))
                            FixSize(Add(Severity.Error, "Rule 34-35", $"Garbage chute must be {Units.FormatInches(s.GarbageChute.FixedWidth)} x {Units.FormatInches(s.GarbageChute.FixedLength)}, is {o.SizeText}", o),
                                o, s.GarbageChute.FixedWidth, s.GarbageChute.FixedLength, null);
                        break;
                    case "Condensate":
                        if (!Near(d.Diameter ?? 0, s.Condensate.SleeveDiameter))
                            FixSize(Add(Severity.Error, "Condensate 5", $"Condensate sleeves are always {Units.FormatInches(s.Condensate.SleeveDiameter)}, is {o.SizeText}", o), o, null, null, s.Condensate.SleeveDiameter);
                        break;
                    case "AreaDrain":
                        if (!Near(d.Diameter ?? 0, s.Storm.AreaDrainDiameter))
                            FixSize(Add(Severity.Error, "Storm 8", $"Area drains are always {Units.FormatInches(s.Storm.AreaDrainDiameter)}, is {o.SizeText}", o), o, null, null, s.Storm.AreaDrainDiameter);
                        break;
                    case "DryerExhaust":
                        if (roof)
                        {
                            if (d.Diameter.HasValue)
                                Add(Severity.Error, "Rule 78", $"Dryer exhaust at roof must be a {Units.FormatInches(s.DryerExhaust.RoofOpeningWidth)} x {Units.FormatInches(s.DryerExhaust.RoofOpeningLength)} opening, is a {o.SizeText} round sleeve — re-place with Dryer Exhaust on the roof plan", o);
                            else if (!Near(d.Width ?? 0, s.DryerExhaust.RoofOpeningWidth) || !Near(d.Length ?? 0, s.DryerExhaust.RoofOpeningLength))
                                FixSize(Add(Severity.Error, "Rule 78", $"Dryer exhaust at roof must be {Units.FormatInches(s.DryerExhaust.RoofOpeningWidth)} x {Units.FormatInches(s.DryerExhaust.RoofOpeningLength)}, is {o.SizeText}", o),
                                    o, s.DryerExhaust.RoofOpeningWidth, s.DryerExhaust.RoofOpeningLength, null);
                        }
                        else if (d.Diameter.HasValue && !Near(d.Diameter.Value, s.DryerExhaust.Diameter))
                            FixSize(Add(Severity.Error, "Rule 72", $"Dryer exhaust must be {Units.FormatInches(s.DryerExhaust.Diameter)} round, is {o.SizeText}", o), o, null, null, s.DryerExhaust.Diameter);
                        break;
                    case "Electrical":
                        if (roof && d.Diameter.HasValue && !Near(d.Diameter.Value, s.Electrical.RoofSleeveDiameter))
                            FixSize(Add(Severity.Error, "Electrical 16", $"Roof electric sleeve must be {Units.FormatInches(s.Electrical.RoofSleeveDiameter)}, is {o.SizeText}", o), o, null, null, s.Electrical.RoofSleeveDiameter);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ 2. structure, wall edges, mid-room

        private void CheckStructure()
        {
            var guards = new Dictionary<ElementId, PlacementGuard>();
            foreach (var o in _openings)
            {
                if (!guards.TryGetValue(o.Level.Id, out var g))
                    guards[o.Level.Id] = g = new PlacementGuard(_doc, _rules, o.Level, WallsOn(o.Level));

                SystemKind? sys = Enum.TryParse(o.Data.System, out SystemKind k) ? k : (SystemKind?)null;
                foreach (var w in g.Check(o.Point, HalfW(o), HalfL(o), sys))
                {
                    string text = PlacementGuard.Clean(w);
                    string rule = text.Contains("shear wall") || text.Contains("foundation wall") ? "Rule 27" : text.Contains("beam") ? "Rule 28" : text.Contains("column") ? "Rule 26"
                                : text.Contains("edge of wall") ? "General" : text.Contains("Standpipe") ? "Standpipe" : "Rule 20-21";
                    Add(PlacementGuard.IsHard(w) ? Severity.Error : Severity.Warning, rule, text, o);
                }
            }
        }

        // ------------------------------------------------------------------ 3. spacing (roof, ERV, dryer, standpipe, overlaps)

        private void CheckSpacing()
        {
            var c = _rules.Clearances;
            var s = _rules.Systems;

            foreach (var byLevel in _openings.GroupBy(o => o.Level.Id))
            {
                var list = byLevel.ToList();
                var level = list[0].Level;
                bool roof = IsRoofLevel(level);
                var walls = roof ? WallsOn(level) : null;

                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];

                    if (roof)
                    {
                        // Rule 58: >= 1' from walls and curbs (exact wall faces)
                        var near = walls.Nearest(a.Point, out double face);
                        double gapIn = Units.FeetToInches(face - RadiusFt(a));
                        if (near != null && gapIn < c.RoofMinFromWallOrCurb)
                            Add(Severity.Warning, "Rule 58", $"Roof opening {Units.FormatInches(Math.Max(0, gapIn))} from wall/curb {near.Label} (min {Units.FormatInches(c.RoofMinFromWallOrCurb)})", a);
                    }

                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var b = list[j];
                        double gap = Gap(a, b);
                        double cc = CenterDist(a, b);

                        if (gap < 0 && !FixturePair(a, b, cc))
                            Add(Severity.Error, "Overlap", $"Overlaps {b.Riser ?? b.Data.Label} on {level.Name}", a, b);

                        bool bothDryer = a.Data.System == "DryerExhaust" && b.Data.System == "DryerExhaust";
                        bool bothAD = a.Data.System == "AreaDrain" && b.Data.System == "AreaDrain";
                        bool bothSP = a.Data.System == "Standpipe" && b.Data.System == "Standpipe";
                        bool bothERV = a.Data.System == "ERV" && b.Data.System == "ERV";

                        if (bothSP && cc < s.Standpipe.MinCenterToCenter)
                            Add(Severity.Error, "Standpipe", $"Standpipes {Units.FormatInches(cc)} c-c (min {Units.FormatInches(s.Standpipe.MinCenterToCenter)})", a, b);

                        if (!roof) continue;

                        if (bothERV)
                        {
                            // Rule 55: exactly 2' between ERV openings (checked for neighbouring ERVs only)
                            if (gap < c.ErvSpacingExact * 2 && !Near(gap, c.ErvSpacingExact, 1.0))
                                Add(Severity.Warning, "Rule 55", $"ERV openings {Units.FormatInches(gap)} apart — must be exactly {Units.FormatInches(c.ErvSpacingExact)}", a, b);
                        }
                        else if (bothDryer)
                        {
                            if (gap < s.DryerExhaust.MinSpacing)
                                Add(Severity.Warning, "Rule 79", $"Dryer exhausts {Units.FormatInches(gap)} apart (min {Units.FormatInches(s.DryerExhaust.MinSpacing)})", a, b);
                        }
                        else if (bothAD && Near(cc, s.Storm.AreaDrainSpacingCenterToCenter, 1.0))
                        {
                            // an area-drain pair: intentionally 2'-0" c-c, exempt from rule 59
                        }
                        else if (SamePipeGroup(a, b))
                        {
                            // the pipes of one plumbing group (S-6, V-6, ST-6, G-6): the engineer draws them as one row in the
                            // chase; their sleeves sit side by side, rule 59 is about separate roof openings
                        }
                        else if (gap >= 0 && gap < c.RoofMinBetweenOpenings)
                            Add(Severity.Warning, "Rule 59", $"Roof openings {Units.FormatInches(gap)} apart (min {Units.FormatInches(c.RoofMinBetweenOpenings)})", a, b);
                    }
                }
            }
        }

        /// <summary>Two pipe sleeves (plumbing services) of one group: "S-6" and "V-6", "ST-D1#1" and "ST-D1#2".</summary>
        private bool SamePipeGroup(OpeningRecord a, OpeningRecord b)
        {
            var pipes = _rules.Plumbing?.Services?.Values.Select(v => v.System).ToList();
            if (pipes == null || !pipes.Contains(a.Data.System) || !pipes.Contains(b.Data.System)) return false;
            string Group(string riser)
            {
                if (string.IsNullOrEmpty(riser)) return null;
                int i = riser.IndexOf('-'), hash = riser.IndexOf('#');
                string g = i >= 0 ? riser.Substring(i + 1) : riser;
                return hash > i ? g.Substring(0, hash - i - 1) : g;
            }
            string ga = Group(a.Riser);
            return ga != null && ga == Group(b.Riser);
        }

        /// <summary>A fixture code from rules.json plumbing.fixtures (WC, BT, KS...): the riser id of fixture sleeves.</summary>
        private bool IsFixture(string riser) => riser != null && (_rules.Plumbing?.Fixtures?.ContainsKey(riser) ?? false);

        /// <summary>The sleeves of one fixture laid out together (a tub's two 6" sleeves 6" c-c touch by design): not an overlap.</summary>
        private bool FixturePair(OpeningRecord a, OpeningRecord b, double cc)
        {
            if (a.Riser == null || a.Riser != b.Riser || !IsFixture(a.Riser)) return false;
            var f = _rules.Plumbing.Fixtures[a.Riser];
            return f.Count > 1 && f.Spacing > 0 && Near(cc, f.Spacing, 0.5);
        }

        // ------------------------------------------------------------------ 4. riser continuity

        private void CheckRisers()
        {
            var lowest = _levels.Lowest?.Level;
            var roof = _levels.MainRoof?.Level;
            // the lowest slab is on grade (rules.json plumbing.skipLowestSlab): pipes leave the building at the ceiling of the
            // lowest floor (storm main, detention tank), so a riser whose last sleeve is in the slab above it is complete
            bool onGrade = lowest != null && (_rules.Plumbing?.SkipLowestSlab ?? false);

            foreach (var r in RiserIndex.Group(_openings))
            {
                // fixture sleeves (WC, LAV, BT, KS...) share their fixture's code on every floor: they are not one riser
                if (IsFixture(r.Riser)) continue;
                var top = r.Top;
                var others = r.Openings.Except(new[] { top }).ToArray();
                _state.RiserEnds.TryGetValue(r.Riser, out var declared);

                var gaps = r.Gaps(_levels);
                if (gaps.Count > 0)
                {
                    var issue = Add(Severity.Error, "Rule 45-47", $"Riser {r.Riser} missing on: {string.Join(", ", gaps.Select(g => g.Name))}", top, others);
                    issue.FixLabel = $"Copy to {gaps.Count} missing floor(s)";
                    issue.Fix = doc =>
                    {
                        var prop = new Propagator(doc, _levels);
                        foreach (var gap in gaps)
                        {
                            // copy from the nearest opening above the gap
                            var src = r.Openings.Where(o => o.Level.Elevation > gap.Elevation).OrderBy(o => o.Level.Elevation).First();
                            prop.CopyOne(src, gap);
                        }
                    };
                }

                // offsets the plumbing plans draw (recorded by Auto Run, both sleeves still where it put them) are noted, not warned
                var steps = r.OffsetSteps;
                var drawn = r.System == "GarbageChute" ? new List<(OpeningRecord Below, OpeningRecord Here)>()
                    : steps.Where(s => _state.DrawnOffsets.Any(d => d.Riser == r.Riser && d.Level == s.Here.Level.Name && d.Matches(s.Below.Point, s.Here.Point))).ToList();
                int offsets = steps.Count - drawn.Count;
                if (offsets > 0)
                    Add(r.System == "GarbageChute" ? Severity.Error : Severity.Warning,
                        r.System == "GarbageChute" ? "Rule 42-43" : "Rule 22-23",
                        $"Riser {r.Riser} offsets {offsets} time(s) between floors (at {string.Join(", ", steps.Except(drawn).Select(s => s.Here.Level.Name))})", top, others);
                if (drawn.Count > 0)
                    Add(Severity.Info, "Rule 22-23",
                        $"Riser {r.Riser} offsets on {string.Join(", ", drawn.Select(s => s.Here.Level.Name))} as the plans draw it (check the riser diagram)", top, others);

                // exhaust risers start small and grow toward the roof fan (rule 3): only a size that gets smaller going up is flagged
                bool grows = (r.System == "Exhaust" || r.System == "ERV" || r.System == "MotorizedDamper") && r.GrowsUpward;
                if (r.Sizes.Count() > 1 && !grows && r.System != "DryerExhaust" && r.System != "Electrical" && r.System != "Refrigeration"
                    && !(IsRoofLevel(top.Level) && r.Openings.Count(o => !IsRoofLevel(o.Level)) > 0 && r.Openings.Where(o => !IsRoofLevel(o.Level)).Select(o => o.SizeText).Distinct().Count() == 1))
                    Add(r.System == "GarbageChute" ? Severity.Error : Severity.Warning, "Rule 24 / 49",
                        $"Riser {r.Riser} changes size: {string.Join(", ", r.Sizes)}", top, others);

                // Where each system is expected to end, unless the drafter declared otherwise (tap-out, bulkhead...)
                switch (r.System)
                {
                    case "Storm": case "AreaDrain": case "Condensate": case "Standpipe":
                        // a roof / terrace drain's leader (ST-D1#1, named by Auto Run) joins a stack's storm pipe on the way down
                        if (System.Text.RegularExpressions.Regex.IsMatch(r.Riser, @"-D\d+(#\d+)?$")) break;
                        // slab on grade: the system's lowest sleeved slab in the model is where its risers end
                        var lowestSleeved = onGrade ? _openings.Where(o => o.Data.System == r.System).Select(o => o.Level).OrderBy(l => l.Elevation).FirstOrDefault() ?? lowest : lowest;
                        if (lowest != null && r.Bottom.Level.Id != lowest.Id && r.Bottom.Level.Id != lowestSleeved.Id && declared?.Bottom != r.Bottom.Level.Name)
                            Add(Severity.Warning, r.System == "Standpipe" ? "Standpipe" : r.System == "Condensate" ? "Condensate 7" : "Storm 12",
                                $"Riser {r.Riser} stops at {r.Bottom.Level.Name}; expected to continue to {(lowestSleeved.Id == lowest.Id ? $"the lowest level ({lowest.Name})" : $"{lowestSleeved.Name} (the lowest slab with sleeves; the slab on grade below gets none)")}. If this is intended, set it in Riser Manager.", r.Bottom);
                        break;
                    case "GarbageChute": case "Exhaust": case "ERV":
                        if (roof != null && top.Level.Elevation < roof.Elevation && !IsRoofLevel(top.Level) && declared?.Top != top.Level.Name)
                            Add(Severity.Info, r.System == "GarbageChute" ? "Rule 38" : "Rule 4/32-34",
                                $"Riser {r.Riser} stops at {top.Level.Name}; most terminate at the roof — tap-out or bulkhead? Declare it in Riser Manager.", top);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ 5. completeness

        private void CheckCompleteness()
        {
            var s = _rules.Systems;

            // Area drains come in pairs at 2'-0" c-c (primary + overflow). Fix = add the overflow.
            foreach (var byLevel in _openings.Where(o => o.Data.System == "AreaDrain").GroupBy(o => o.Level.Id))
            {
                var ads = byLevel.ToList();
                foreach (var ad in ads)
                {
                    bool hasPartner = ads.Any(x => x != ad && Near(CenterDist(ad, x), s.Storm.AreaDrainSpacingCenterToCenter, 1.0));
                    if (hasPartner) continue;
                    var issue = Add(Severity.Error, "Storm note", $"Area drain has no partner at {Units.FormatInches(s.Storm.AreaDrainSpacingCenterToCenter)} c-c (always two: primary + overflow)", ad);
                    issue.FixLabel = "Add overflow drain";
                    issue.Fix = doc =>
                    {
                        var map = MapFor(ad); var sym = FamilyMapping.FindSymbol(doc, map);
                        if (sym == null) throw new InvalidOperationException("No Round Sleeve family mapped.");
                        var spec = OpeningSpec.Round(SystemKind.AreaDrain, s.Storm.AreaDrainDiameter, "AREA DRAIN (OVERFLOW)");
                        spec.Riser = ad.Riser;
                        PlacerFor(doc).Place(spec, sym, map, ad.Level, ad.Point + XYZ.BasisX * Units.InchesToFeet(s.Storm.AreaDrainSpacingCenterToCenter));
                    };
                }
            }

            int spRisers = RiserIndex.Group(_openings).Count(r => r.System == "Standpipe");
            if (_openings.Any(o => o.Data.System == "Standpipe") && spRisers < 2)
                _issues.Add(new AuditIssue { Severity = Severity.Info, Rule = "Standpipe", Message = $"Only {spRisers} standpipe riser(s) — most buildings have two (one per stairwell)" });

            if (_state.CondensateRequired == false && _openings.Any(o => o.Data.System == "Condensate"))
                _issues.Add(new AuditIssue { Severity = Severity.Warning, Rule = "Condensate 1", Message = "Condensate sleeves placed but Project Setup says condensate risers are not required" });

            if (_state.AcSystem == "PTAC" && _openings.Any(o => o.Data.System == "Refrigeration"))
                _issues.Add(new AuditIssue { Severity = Severity.Warning, Rule = "Refrigeration 2", Message = "Refrigeration openings placed but the AC system is PTAC (no refrigeration lines)" });

            foreach (var o in _openings.Where(o => string.IsNullOrEmpty(o.Riser)))
            {
                var issue = Add(Severity.Info, "Riser id", "No riser id (it will not be tracked floor to floor)", o);
                issue.FixLabel = "Assign id";
                issue.Fix = doc => { o.Data.Riser = RiserIndex.NextRiserId(doc, o.Data.System); o.Data.WriteTo(o.Instance); SharedParams.Write(o.Instance, o.Data); };
            }
        }

        // ------------------------------------------------------------------ 6. naming

        private void CheckNaming()
        {
            var n = _rules.Naming;
            foreach (var o in _openings)
            {
                var label = o.Instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? o.Data.Label ?? "";
                string required = o.Data.System == "Electrical" ? n.Electrical
                                : o.Data.System == "DryerExhaust" && !IsRoofLevel(o.Level) ? n.DryerExhaust : null;

                if (string.IsNullOrWhiteSpace(label))
                {
                    var issue = Add(Severity.Warning, "Rule 14/18", "Opening has no name", o);
                    string fallback = required ?? o.Data.Label ?? (o.Riser ?? o.Data.System.ToUpperInvariant());
                    issue.FixLabel = $"Name '{fallback}'";
                    issue.Fix = doc => PlacerFor(doc).Relabel(o.Instance, MapFor(o), o.Data, fallback);
                }
                else if (required != null && label.IndexOf(required, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var issue = Add(Severity.Error, o.Data.System == "Electrical" ? "Electrical 18" : "Rule 73",
                        $"{(o.Data.System == "Electrical" ? "Electrical opening" : "Dryer exhaust")} must be labelled '{required}', is '{label}'", o);
                    issue.FixLabel = $"Name '{required}'";
                    issue.Fix = doc => PlacerFor(doc).Relabel(o.Instance, MapFor(o), o.Data, required);
                }
            }
        }
    }
}
