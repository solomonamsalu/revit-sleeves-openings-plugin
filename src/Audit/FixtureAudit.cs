using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Audit
{
    /// <summary>A plumbing fixture / casework item the manual says to verify, found in the model.</summary>
    public class FixtureRecord
    {
        public FamilyInstance Instance;
        public Level Level;
        public XYZ Point;
        public BoundingBoxXYZ Box;
        public string Name;
        public bool WallHung;
    }

    /// <summary>
    /// Manual p.2 "Always verify" turned into checks: shower drains, toilets (wall-hung or not),
    /// medicine cabinets and niches are found by name and the sleeves are checked against them.
    /// </summary>
    public static class FixtureAudit
    {
        private static readonly BuiltInCategory[] Cats =
        {
            BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Casework, BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_Furniture, BuiltInCategory.OST_MechanicalEquipment
        };

        public static List<FixtureRecord> Find(Document doc, LevelMap levels, FixtureRule rule)
        {
            var list = new List<FixtureRecord>();
            if (rule == null || string.IsNullOrEmpty(rule.Match)) return list;
            var rx = new Regex(rule.Match, RegexOptions.IgnoreCase);
            var wall = string.IsNullOrEmpty(rule.WallHungMatch) ? null : new Regex(rule.WallHungMatch, RegexOptions.IgnoreCase);

            foreach (var cat in Cats)
            {
                foreach (var inst in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    if (OpeningData.Read(inst) != null) continue;               // our own openings
                    string name = $"{inst.Symbol?.Family?.Name} {inst.Symbol?.Name} {inst.Name}";
                    if (!rx.IsMatch(name)) continue;
                    var bb = inst.get_BoundingBox(null);
                    if (bb == null) continue;
                    var pt = (inst.Location as LocationPoint)?.Point ?? (bb.Min + bb.Max) / 2;
                    var level = RiserIndex.LevelOf(doc, inst) ?? LevelBelow(levels, bb.Min.Z + 0.5);
                    if (level == null) continue;
                    list.Add(new FixtureRecord
                    {
                        Instance = inst, Level = level, Point = pt, Box = bb, Name = (inst.Symbol?.Family?.Name ?? inst.Name).Trim(),
                        WallHung = wall != null && (wall.IsMatch(name) || inst.Host is Wall)
                    });
                }
            }
            return list;
        }

        private static Level LevelBelow(LevelMap levels, double z) =>
            levels.All.Where(l => l.Elevation <= z).OrderByDescending(l => l.Elevation).FirstOrDefault()?.Level;

        private static bool FootprintOverlaps(OpeningRecord o, BoundingBoxXYZ bb, double clearanceIn)
        {
            double hw = Units.InchesToFeet((o.Data.Width ?? o.Data.Diameter ?? 0) / 2 + clearanceIn);
            double hl = Units.InchesToFeet((o.Data.Length ?? o.Data.Diameter ?? 0) / 2 + clearanceIn);
            return o.Point.X + hw > bb.Min.X && o.Point.X - hw < bb.Max.X && o.Point.Y + hl > bb.Min.Y && o.Point.Y - hl < bb.Max.Y;
        }

        /// <summary>Runs all fixture checks and appends issues. Fixes are attached where the manual leaves one answer.</summary>
        public static void Run(Document doc, RuleSet rules, ProjectState state, LevelMap levels,
                               List<OpeningRecord> openings, Func<Severity, string, string, OpeningRecord, AuditIssue> add,
                               Func<Document, Placer> placerFor, Dictionary<ElementId, WallGeometry> walls)
        {
            var f = rules.Fixtures;
            var sleeves = openings.Where(o => o.Data.Diameter.HasValue || o.Data.System == "Bathtub" || o.Data.System == "Toilet").ToList();
            WallGeometry WallsOn(Level l) { if (!walls.TryGetValue(l.Id, out var w)) walls[l.Id] = w = new WallGeometry(doc, l); return w; }

            AuditIssue AddFixture(Severity sev, string rule, string msg, FixtureRecord fx, params OpeningRecord[] related)
            {
                var issue = add(sev, rule, msg, null);
                issue.Level = fx.Level.Name; issue.System = "Fixture";
                issue.Elements.Add(fx.Instance.Id);
                issue.Elements.AddRange(related.Select(r => r.Instance.Id));
                return issue;
            }

            void PlaceSleeveFix(AuditIssue issue, FixtureRecord fx, double pipeSize, SystemKind kind, string label)
            {
                double dia = rules.Systems.Storm.SleeveSize(pipeSize);      // sleeve = pipe + 2"
                issue.FixLabel = $"Add {Units.FormatInches(dia)} sleeve";
                issue.Fix = d =>
                {
                    var map = FamilyMapping.Get(rules, state, FamilyRole.RoundSleeve);
                    var sym = FamilyMapping.FindSymbol(d, map) ?? throw new InvalidOperationException("No Round Sleeve family mapped.");
                    var spec = OpeningSpec.Round(kind, dia, label);
                    spec.Riser = RiserIndex.NextRiserId(d, kind.ToString());
                    placerFor(d).Place(spec, sym, map, fx.Level, fx.Point);
                };
            }

            // ---- Shower drains: every drain needs a sleeve nearby (drain type decides where; point drains drop straight down)
            foreach (var fx in Find(doc, levels, f.ShowerDrain))
            {
                double r = Units.InchesToFeet(f.ShowerDrain.SleeveRadius);
                var near = sleeves.Where(o => o.Level.Id == fx.Level.Id && RiserRecord.Dist(o.Point, fx.Point) <= r).ToList();
                if (near.Count > 0) continue;
                var issue = AddFixture(Severity.Warning, "Verify: shower drain",
                    $"Shower drain '{fx.Name}' has no sleeve within {Units.FormatInches(f.ShowerDrain.SleeveRadius)} — check the drain type (linear vs point) and place its sleeve", fx);
                PlaceSleeveFix(issue, fx, f.ShowerDrain.PipeSize, SystemKind.Storm, "SHOWER DRAIN");
            }

            // ---- Toilets: floor-mounted drains through the slab; wall-hung drains through the wall into a carrier
            foreach (var fx in Find(doc, levels, f.Toilet))
            {
                double r = Units.InchesToFeet(f.Toilet.SleeveRadius);
                var near = sleeves.Where(o => o.Level.Id == fx.Level.Id && RiserRecord.Dist(o.Point, fx.Point) <= r).ToList();
                if (fx.WallHung)
                {
                    var wg = WallsOn(fx.Level);
                    foreach (var o in near)
                    {
                        double rad = Units.InchesToFeet((o.Data.Diameter ?? o.Data.Width ?? 0) / 2);
                        bool inWall = wg.Walls.Any(w => WallGeometry.Relate(w, o.Point, rad) == WallGeometry.Relation.Inside);
                        if (!inWall)
                            AddFixture(Severity.Error, "Verify: wall-hung toilet",
                                $"Wall-hung toilet '{fx.Name}': sleeve {o.Riser ?? o.Data.Label} is in the open slab — a wall-hung WC drains through the wall into a carrier; put the sleeve inside the wall/chase", fx, o);
                    }
                    if (near.Count == 0)
                        AddFixture(Severity.Info, "Verify: wall-hung toilet", $"Wall-hung toilet '{fx.Name}': no sleeve within {Units.FormatInches(f.Toilet.SleeveRadius)} — its drain goes through the wall behind; make sure the carrier riser has a sleeve", fx);
                }
                else if (near.Count == 0)
                {
                    var issue = AddFixture(Severity.Warning, "Verify: toilet", $"Toilet '{fx.Name}' has no sleeve within {Units.FormatInches(f.Toilet.SleeveRadius)}", fx);
                    PlaceSleeveFix(issue, fx, f.Toilet.PipeSize, SystemKind.Toilet, "TOILET");
                }
            }

            // ---- Medicine cabinets and niches: recessed into the wall, so no sleeve/riser behind them
            foreach (var (rule, what) in new[] { (f.MedicineCabinet, "medicine cabinet"), (f.Niche, "niche") })
            {
                foreach (var fx in Find(doc, levels, rule))
                {
                    foreach (var o in openings.Where(o => o.Level.Id == fx.Level.Id && FootprintOverlaps(o, fx.Box, rule.Clearance)))
                        AddFixture(Severity.Error, $"Verify: {what}",
                            $"Sleeve {o.Riser ?? o.Data.Label} sits behind {what} '{fx.Name}' — the wall cavity is taken; move the riser", fx, o);
                }
            }
        }
    }
}
