using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Roof
{
    public class RoofReport
    {
        public int Created, Skipped, Moved;
        public List<string> Lines = new List<string>();
        public override string ToString() => string.Join("\n", Lines);
    }

    /// <summary>
    /// Manual rules 52-67 (+78, 87, refrigeration 23, electrical 16): copy the top apartment floor's
    /// mechanical/electrical openings to a roof level applying the roof sizes, then push them apart to
    /// meet the roof spacing rules while staying as close to their riser as possible.
    /// Runs inside the caller's transaction.
    /// </summary>
    public class RoofGenerator
    {
        private readonly Document _doc;
        private readonly RuleSet _rules;
        private readonly ProjectState _state;
        private readonly LevelMap _levels;

        public RoofGenerator(Document doc, RuleSet rules, ProjectState state, LevelMap levels)
        {
            _doc = doc; _rules = rules; _state = state; _levels = levels;
        }

        /// <summary>The roof version of a floor opening, or null if this system does not continue to the roof from below.</summary>
        public OpeningSpec RoofSpec(OpeningRecord src)
        {
            var d = src.Data;
            var s = _rules.Systems;
            OpeningSpec spec;
            switch (d.System)
            {
                case "Exhaust":            // rule 56: +4" total
                case "ERV":                // rules 54-55: grouped, exactly 2' apart (spacing pass)
                case "MotorizedDamper":    // rule 87: same as roof exhaust
                    spec = OpeningSpec.Rect(Enum(d.System), FamilyRole.RegularOpening,
                        (d.Width ?? 0) + s.Exhaust.RoofIncreaseTotal, (d.Length ?? 0) + s.Exhaust.RoofIncreaseTotal, d.Label);
                    break;
                case "GarbageChute":       // rule 44: no clearance added
                    spec = OpeningSpec.Rect(SystemKind.GarbageChute, FamilyRole.RegularOpening,
                        s.GarbageChute.FixedWidth + s.GarbageChute.RoofClearance, s.GarbageChute.FixedLength + s.GarbageChute.RoofClearance, d.Label);
                    break;
                case "DryerExhaust":       // rule 78: 6" x 6" per 4" dryer exhaust
                    spec = OpeningSpec.Rect(SystemKind.DryerExhaust, FamilyRole.RegularOpening,
                        s.DryerExhaust.RoofOpeningWidth, s.DryerExhaust.RoofOpeningLength, _rules.Naming.DryerExhaust);
                    break;
                case "Refrigeration":      // rule 23: +3" W and L for condenser conduits
                    spec = OpeningSpec.Rect(SystemKind.Refrigeration, FamilyRole.PipeReferenceOpening,
                        (d.Width ?? 0) + s.Refrigeration.RoofExtraWidth, (d.Length ?? 0) + s.Refrigeration.RoofExtraLength, d.Label);
                    spec.DownHeight = s.Refrigeration.DownHeight;
                    break;
                case "Electrical":         // rule 16: one 2" ELECTRIC sleeve
                    spec = OpeningSpec.Round(SystemKind.Electrical, s.Electrical.RoofSleeveDiameter, _rules.Naming.Electrical);
                    break;
                default:
                    return null;           // storm/AD/condensate/standpipe/bathtub start or end elsewhere
            }
            spec.Riser = d.Riser;
            return spec;
        }

        private static SystemKind Enum(string s) => (SystemKind)System.Enum.Parse(typeof(SystemKind), s);

        public RoofReport Run(View placementView, Level source, Level roof, bool autoSpace, bool skipExisting)
        {
            var report = new RoofReport();
            var all = RiserIndex.AllOpenings(_doc);
            var sources = all.Where(o => o.Level.Id == source.Id).ToList();
            var onRoof = all.Where(o => o.Level.Id == roof.Id).ToList();
            var placer = new Placer(_doc, placementView);
            var created = new List<OpeningRecord>();

            foreach (var src in sources)
            {
                var spec = RoofSpec(src);
                if (spec == null) { report.Lines.Add($"  skip {src.Riser ?? src.Data.Label} ({src.Data.System}: not a roof penetration from below)"); continue; }

                if (skipExisting && onRoof.Any(o => o.Riser != null && o.Riser == src.Riser))
                { report.Skipped++; report.Lines.Add($"  skip {src.Riser} (already on {roof.Name})"); continue; }

                var map = FamilyMapping.Get(_rules, _state, spec.Role);
                var symbol = FamilyMapping.FindSymbol(_doc, map);
                if (symbol == null) { report.Lines.Add($"! no family mapped for {FamilyRole.Describe(spec.Role)} — {src.Riser} not created"); continue; }

                var inst = placer.Place(spec, symbol, map, roof, src.Point);
                var rec = new OpeningRecord { Instance = inst, Data = OpeningData.Read(inst), Level = roof, Point = src.Point };
                created.Add(rec);
                report.Created++;
                report.Lines.Add($"{roof.Name}: {spec.Label} {spec.SizeText} from {src.Riser ?? "-"}");
            }

            if (autoSpace && created.Count > 0)
                report.Moved = SpaceOut(roof, onRoof.Concat(created).ToList(), created, report);

            return report;
        }

        // ------------------------------------------------------------------ layout (rules 55, 58, 59, 79)

        /// <summary>
        /// Iteratively pushes openings apart until every pair meets its minimum gap and every opening is
        /// clear of walls/curbs. Only newly created openings move; existing roof openings act as anchors.
        /// </summary>
        private int SpaceOut(Level roof, List<OpeningRecord> all, List<OpeningRecord> movable, RoofReport report)
        {
            var c = _rules.Clearances;
            var s = _rules.Systems;
            double wallMin = Units.InchesToFeet(c.RoofMinFromWallOrCurb);
            var walls = new WallGeometry(_doc, roof);

            var pos = all.ToDictionary(o => o, o => o.Point);
            var canMove = new HashSet<OpeningRecord>(movable);
            double HW(OpeningRecord o) => Units.InchesToFeet((o.Data.Width ?? o.Data.Diameter ?? 0) / 2);
            double HL(OpeningRecord o) => Units.InchesToFeet((o.Data.Length ?? o.Data.Diameter ?? 0) / 2);

            double MinGap(OpeningRecord a, OpeningRecord b)
            {
                bool dryer = a.Data.System == "DryerExhaust" && b.Data.System == "DryerExhaust";
                bool ad = a.Data.System == "AreaDrain" && b.Data.System == "AreaDrain";
                bool erv = a.Data.System == "ERV" && b.Data.System == "ERV";
                if (ad) return 0;                                                   // pairs are placed deliberately
                if (erv) return Units.InchesToFeet(c.ErvSpacingExact);              // rule 55
                return Units.InchesToFeet(dryer ? s.DryerExhaust.MinSpacing : c.RoofMinBetweenOpenings);
            }

            for (int iter = 0; iter < 40; iter++)
            {
                bool changed = false;

                for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                {
                    var a = all[i]; var b = all[j];
                    double need = MinGap(a, b);
                    var pa = pos[a]; var pb = pos[b];
                    double dx = Math.Abs(pa.X - pb.X) - (HW(a) + HW(b));
                    double dy = Math.Abs(pa.Y - pb.Y) - (HL(a) + HL(b));
                    double gap = Math.Max(dx, dy);
                    if (gap >= need - 1e-6) continue;

                    // Push along the axis that already separates them most (keeps rows aligned, rule 80).
                    XYZ dir = dx >= dy ? new XYZ(Math.Sign(pb.X - pa.X), 0, 0) : new XYZ(0, Math.Sign(pb.Y - pa.Y), 0);
                    if (dir.IsZeroLength()) dir = XYZ.BasisX;
                    double push = (need - gap) + 0.01;
                    bool ma = canMove.Contains(a), mb = canMove.Contains(b);
                    if (ma && mb) { pos[a] = pa - dir * (push / 2); pos[b] = pb + dir * (push / 2); }
                    else if (ma) pos[a] = pa - dir * push;
                    else if (mb) pos[b] = pb + dir * push;
                    else continue;
                    changed = true;
                }

                foreach (var o in movable)
                {
                    var p = pos[o];
                    double r = Math.Max(HW(o), HL(o));
                    var near = walls.Nearest(p, out double face);
                    if (near == null) continue;
                    double gap = face - r;                                          // edge of opening to wall face
                    if (gap >= wallMin) continue;
                    if (face < 0) continue;                                         // centre inside a wall: leave to the auditor
                    // Move straight away from the wall along the perpendicular from its centerline.
                    var flat = new XYZ(p.X, p.Y, near.Centerline.GetEndPoint(0).Z);
                    var foot = near.Centerline.Project(flat)?.XYZPoint ?? flat;
                    var away = new XYZ(p.X - foot.X, p.Y - foot.Y, 0);
                    if (away.IsZeroLength()) away = XYZ.BasisX; else away = away.Normalize();
                    pos[o] = p + away * (wallMin - gap + 0.01);
                    changed = true;
                }

                if (!changed) break;
            }

            int moved = 0;
            foreach (var o in movable)
            {
                var delta = pos[o] - o.Point;
                if (delta.GetLength() < 1e-6) continue;
                ElementTransformUtils.MoveElement(_doc, o.Instance.Id, new XYZ(delta.X, delta.Y, 0));
                o.Point = pos[o];
                moved++;
                report.Lines.Add($"  moved {o.Riser ?? o.Data.Label} {Units.FormatInches(Units.FeetToInches(delta.GetLength()))} to meet roof spacing");
            }
            return moved;
        }
    }
}
