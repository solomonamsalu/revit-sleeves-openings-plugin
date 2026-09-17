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

        public Auditor(Document doc, RuleSet rules, ProjectState state, LevelMap levels)
        {
            _doc = doc; _rules = rules; _state = state; _levels = levels;
            _openings = RiserIndex.AllOpenings(doc);
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
                    string rule = text.Contains("shear wall") ? "Rule 27" : text.Contains("beam") ? "Rule 28" : text.Contains("column") ? "Rule 26"
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
                            Add(Severity.Warning, "Rule 58", $"Roof opening {Units.FormatInches(Math.Max(0, gapIn))} from wall/curb {near.Wall.Id} (min {Units.FormatInches(c.RoofMinFromWallOrCurb)})", a);
                    }

                    for (int j = i + 1; j < list.Count; j++)
                    {
                        var b = list[j];
                        double gap = Gap(a, b);
                        double cc = CenterDist(a, b);

                        if (gap < 0)
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
                        else if (gap >= 0 && gap < c.RoofMinBetweenOpenings)
                            Add(Severity.Warning, "Rule 59", $"Roof openings {Units.FormatInches(gap)} apart (min {Units.FormatInches(c.RoofMinBetweenOpenings)})", a, b);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ 4. riser continuity

        private void CheckRisers()
        {
            var lowest = _levels.Lowest?.Level;
            var roof = _levels.MainRoof?.Level;

            foreach (var r in RiserIndex.Group(_openings))
            {
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

                int offsets = r.OffsetCount;
                if (offsets > 0)
                    Add(r.System == "GarbageChute" ? Severity.Error : Severity.Warning,
                        r.System == "GarbageChute" ? "Rule 42-43" : "Rule 22-23",
                        $"Riser {r.Riser} offsets {offsets} time(s) between floors", top, others);

                if (r.Sizes.Count() > 1 && r.System != "DryerExhaust" && r.System != "Electrical" && r.System != "Refrigeration"
                    && !(IsRoofLevel(top.Level) && r.Openings.Count(o => !IsRoofLevel(o.Level)) > 0 && r.Openings.Where(o => !IsRoofLevel(o.Level)).Select(o => o.SizeText).Distinct().Count() == 1))
                    Add(r.System == "GarbageChute" ? Severity.Error : Severity.Warning, "Rule 24 / 49",
                        $"Riser {r.Riser} changes size: {string.Join(", ", r.Sizes)}", top, others);

                // Where each system is expected to end, unless the drafter declared otherwise (tap-out, bulkhead...)
                switch (r.System)
                {
                    case "Storm": case "AreaDrain": case "Condensate": case "Standpipe":
                        if (lowest != null && r.Bottom.Level.Id != lowest.Id && declared?.Bottom != r.Bottom.Level.Name)
                            Add(Severity.Warning, r.System == "Standpipe" ? "Standpipe" : r.System == "Condensate" ? "Condensate 7" : "Storm 12",
                                $"Riser {r.Riser} stops at {r.Bottom.Level.Name}; expected to continue to the lowest level ({lowest.Name}). If this is intended, set it in Riser Manager.", r.Bottom);
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
