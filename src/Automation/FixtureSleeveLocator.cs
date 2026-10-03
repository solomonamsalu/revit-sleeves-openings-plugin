using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Placement;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Settles the fixture labels read from a plan (WC, LAV, BT...) against the model, in this order:
    /// 1. a sleeve already in the model under the fixture -> already in the model (nothing placed);
    /// 2. the matching Revit fixture with one sanitary connector -> sleeve on that connector;
    /// 3. no sleeve and no Revit fixture nearby -> a model/PDF gap, reported and not placed (the model lacks the fixture);
    /// 4. a Revit fixture without a usable connector -> the fixture as drawn on the PDF (tub drain circle, toilet 1'-1"
    ///    from its wall, sink in the wall behind it);
    /// 5. anything else stays review, grouped by stack (one decision covers every floor of a stacked bathroom/kitchen),
    ///    and a floor whose modelled fixtures mostly disagree with the plan is reported once as out of sync.
    /// Plan text is deliberately used only to select nearby elements; it is never used as the sleeve centre.
    /// </summary>
    public static class FixtureSleeveLocator
    {
        private const double MatchRadiusFeet = 6.0;
        private const double ClearNearestFeet = 1.0;
        /// <summary>How far from the plan label a sleeve already in the model can be and still be the fixture's sleeve.</summary>
        private const double ExistingRadiusFeet = 4.0;
        /// <summary>The same, from the drain point of the fixture drawn on the plan (a closer, surer spot than the label).</summary>
        private const double ExistingFromDrainFeet = 2.5;
        /// <summary>A sleeve this close to a riser crossing belongs to the riser, not to a fixture.</summary>
        private const double RiserSleeveFeet = 1.0;
        /// <summary>Fixture labels on different floors this close (plan position) are one stack.</summary>
        private const double StackFeet = 1.5;
        /// <summary>Share of a floor's fixture labels with no modelled fixture, on a floor that has fixtures, that makes it out of sync.</summary>
        private const double OutOfSyncShare = 0.6;

        /// <summary>Sleeve and opening families are never fixtures (the office tub sleeve "MPI - Double Sleeve for Tub" matched "tub").</summary>
        private static readonly Regex NotFixture = new Regex(@"sleeve|opening|penetration", RegexOptions.IgnoreCase);

        private class Fixture
        {
            public string Name, Source, Level;
            public XYZ Point;
            public List<XYZ> Drains = new List<XYZ>();
        }

        /// <summary>Resolve fixture-review crossings in place. Returns reader-friendly results for the check summary.</summary>
        public static List<string> Apply(Document host, RiserAssembly assembly, PlumbingRules rules, ExistingReport existing = null)
        {
            var messages = new List<string>();
            if (host == null || assembly == null || rules == null ||
                !string.Equals(rules.FixtureSleeves, "model", StringComparison.OrdinalIgnoreCase)) return messages;

            var labels = assembly.Crossings.Where(IsFixtureReview).Where(c => rules.Fixtures.ContainsKey(c.Tag)).ToList();
            if (labels.Count == 0) return messages;

            int inModel = ClaimExisting(labels, assembly, existing);

            var fixtures = ReadFixtures(host, existing);
            int placed = 0, drawn = 0, noMatch = 0, ambiguous = 0, noDrain = 0;
            var gaps = new List<Crossing>();
            // the model has no usable fixture here: the fixture drawn on the plan gives the spot, else review
            void Fallback(Crossing c, string why, ref int counter)
            {
                if (c.DrawnDrain != null) { FromDrawing(c, why); drawn++; }
                else { counter++; c.Notes.Add(why + "; review required"); }
            }
            foreach (var c in labels.Where(c => c.ExistingIds.Count == 0))
            {
                var sleeve = rules.Fixtures[c.Tag];
                var candidates = fixtures.Where(f => AutoPlacer.SameFloor(f.Level, c.Level) && Matches(c.Tag, sleeve, f.Name))
                                         .Select(f => new { Fixture = f, Distance = Dist(c.X, c.Y, f.Point) })
                                         .Where(x => x.Distance <= MatchRadiusFeet)
                                         .OrderBy(x => x.Distance).ToList();
                // the plan shows a fixture the model does not have: a gap between model and drawings, never a sleeve placed by guess
                if (candidates.Count == 0) { gaps.Add(c); noMatch++; continue; }
                if (candidates.Count > 1 && candidates[1].Distance - candidates[0].Distance < ClearNearestFeet)
                {
                    Fallback(c, "more than one matching Revit fixture is near the plan label", ref ambiguous); continue;
                }

                var found = candidates[0].Fixture;
                if (found.Drains.Count == 0)
                {
                    Fallback(c, $"matched Revit fixture '{found.Name}' ({found.Source}) has no sanitary connector", ref noDrain); continue;
                }

                int wanted = Math.Max(1, sleeve.Count);
                // Multiple sleeves below one fixture need a project-specific layout rule (for example a tub's two
                // sleeves).  A connector alone does not state which side/orientation the office expects, so retain
                // these for review instead of making a plausible-looking but wrong layout.
                if (wanted != 1)
                {
                    Fallback(c, $"matched Revit fixture '{found.Name}' needs {wanted} sleeve point(s); its connector does not give the multi-sleeve layout", ref noDrain); continue;
                }
                if (found.Drains.Count != wanted)
                {
                    Fallback(c, $"matched Revit fixture '{found.Name}' has {found.Drains.Count} sanitary connector(s); {wanted} sleeve point(s) are required", ref noDrain); continue;
                }

                c.X = found.Drains[0].X; c.Y = found.Drains[0].Y;
                c.Points.Clear(); c.EachPoint = false;
                c.Status = Crossing.Place;
                c.Confidence = "high";
                c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
                c.Notes.Add($"position from sanitary connector of Revit fixture '{found.Name}' ({found.Source}), {candidates[0].Distance * 12:0.#}\" from the plan label");
                placed++;
            }

            int served = Served(labels.Where(c => c.Status == Crossing.Place && c.ExistingIds.Count == 0 && c.DrawnDrain != null).ToList(), assembly, rules);

            var outOfSync = OutOfSync(labels, fixtures);
            ReportGaps(gaps, assembly);
            labels.RemoveAll(gaps.Contains);
            var left = labels.Where(c => c.Status == Crossing.Review).ToList();
            int stacks = GroupStacks(left);

            messages.Add($"Fixture sleeves: {inModel} already in the model, {placed} resolved from Revit sanitary connectors, {drawn} from the fixtures drawn on the plans ({served} of them served by a sleeve already there); " +
                         $"{noMatch} on the plans but not in the model (reported as model/PDF gaps, not placed); " +
                         $"{left.Count} left for review in {stacks} stack(s) ({ambiguous} ambiguous, {noDrain} without matching connector(s)).");
            foreach (var level in outOfSync)
                messages.Add($"  ! {level}: most fixture labels on the plan have no modelled fixture nearby — the drawings and the model look out of sync on this floor (which is newer?).");
            return messages;
        }

        /// <summary>
        /// Step 1: a sleeve already in the model near the label, of a system that can serve the fixture, is the fixture's
        /// sleeve. Pairs are taken nearest-first (the fixture's own system first: a Toilet sleeve for WC, Bathtub for BT) and
        /// each sleeve serves one label only; sleeves on a riser crossing belong to the riser.
        /// </summary>
        private static int ClaimExisting(List<Crossing> labels, RiserAssembly assembly, ExistingReport existing)
        {
            if (existing == null || existing.Items.Count == 0) return 0;
            var risers = assembly.Crossings.Where(c => !labels.Contains(c) && c.Status == Crossing.Place).ToList();
            var free = existing.Items.Where(e => e.Point != null && e.Source != "native opening" &&
                                                 !risers.Any(r => r.Level == e.Level && Dist(r.X, r.Y, e.Point) <= RiserSleeveFeet)).ToList();

            var pairs = (from c in labels
                         from e in free
                         where AutoPlacer.SameFloor(e.Level, c.Level)
                         let rank = SystemRank(c.Tag, e.System)
                         where rank >= 0
                         let d = Dist(c.X, c.Y, e.Point)
                         where d <= (c.DrawnDrain != null ? ExistingFromDrainFeet : ExistingRadiusFeet)
                         orderby rank, d
                         select new { Label = c, Sleeve = e, Distance = d }).ToList();

            // a tub is two sleeves side by side (or one double-sleeve family): it takes the sleeves of its pair
            var used = new HashSet<ElementId>();
            var first = new Dictionary<Crossing, ExistingItem>();
            int claimed = 0;
            foreach (var p in pairs)
            {
                if (used.Contains(p.Sleeve.Id)) continue;
                var c = p.Label;
                if (first.TryGetValue(c, out var had))
                {
                    bool tubPair = c.Tag == "BT" && c.ExistingIds.Count == 1 && had.System != "Bathtub" && p.Sleeve.System != "Bathtub" &&
                                   Dist(p.Sleeve.Point.X, p.Sleeve.Point.Y, had.Point) <= 1.5;
                    if (!tubPair) continue;
                }
                else { first[c] = p.Sleeve; claimed++; }
                used.Add(p.Sleeve.Id);
                c.ExistingIds.Add(p.Sleeve.Id.Value);
                c.Status = Crossing.Place;
                c.Confidence = "high";
                c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
                c.ExistingDetail = $"{p.Sleeve.Family} ({p.Sleeve.Source}{(p.Sleeve.System == "?" ? "" : ", " + p.Sleeve.System)}) {p.Distance * 12:0.#}\" from the " +
                                   (c.DrawnDrain != null ? "drawn fixture's drain point" : "plan label");
                c.Notes.RemoveAll(n => n.StartsWith("sleeve already in the model: "));
                c.Notes.Add("sleeve already in the model: " + c.ExistingDetail + (c.ExistingIds.Count > 1 ? $" (+{c.ExistingIds.Count - 1} more)" : ""));
                if (c.ExistingIds.Count == 1) { c.X = p.Sleeve.Point.X; c.Y = p.Sleeve.Point.Y; }
            }
            return claimed;
        }

        /// <summary>
        /// Sinks, lavatories and washers drain into the wall behind them. A sleeve from the drawing that lands on another
        /// sleeve there (a riser stack in that wall, a sleeve already in the model, the other sink of a double sink) is served
        /// by it: nothing of its own is placed. Returns how many.
        /// </summary>
        private static int Served(List<Crossing> drawn, RiserAssembly assembly, PlumbingRules rules)
        {
            var intoWall = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LAV", "KS", "LS", "W/D", "WD" };
            double Radius(Crossing o) => rules.SleeveFor(o.Size?.Diameter ?? 2) / 2 / 12;
            int n = 0;
            foreach (var c in drawn.Where(c => intoWall.Contains(c.Tag) && !c.EachPoint))
            {
                var host = assembly.Crossings
                    .Where(o => o != c && o.MergedInto == null && o.Status == Crossing.Place && o.Floor == c.Floor && !o.EachPoint && o.HasPosition)
                    .Select(o => (O: o, D: Dist(c.X, c.Y, o.X, o.Y), R: Radius(o)))
                    .Where(t => t.D < Radius(c) + t.R + 1.0 / 12).OrderBy(t => t.D).FirstOrDefault();
                if (host.O == null) continue;
                c.MergedInto = host.O;
                c.Status = Crossing.Skip;
                c.Notes.Add(host.O.ExistingIds.Count > 0 ? $"its sleeve would sit on the sleeve already in the model ({host.D * 12:0.#}\" away): that one serves it"
                          : host.O.IsFixture ? $"its sleeve would overlap the {host.O.Tag} sleeve next to it ({host.D * 12:0.#}\"): one sleeve serves both"
                          : $"drains into the {host.O.Tag} stack in the wall behind it ({host.D * 12:0.#}\" away): that riser's sleeve serves it");
                n++;
            }
            return n;
        }

        /// <summary>
        /// Fixture labels the model has nothing for (no sleeve, no Revit fixture nearby): taken out of the openings and
        /// reported once each as a model/PDF gap, so the drafter adds the fixture to the model or places the sleeve by hand.
        /// </summary>
        private static void ReportGaps(List<Crossing> gaps, RiserAssembly assembly)
        {
            foreach (var c in gaps)
            {
                int floors = gaps.Count(o => o.Tag == c.Tag && o.Floor != c.Floor && o.HasPosition && c.HasPosition && Dist(o.X, o.Y, c.X, c.Y) <= StackFeet);
                assembly.Issues.Add(new AssemblyIssue
                {
                    Floor = c.Floor, Tag = c.Tag, Level = c.Level, Type = AssemblyIssue.ModelGap,
                    X = c.HasPosition ? c.X : (double?)null, Y = c.HasPosition ? c.Y : (double?)null,
                    Detail = $"{c.Tag} is on the {FloorKey.Describe(c.Floor)} plan but not in the model: no sleeve and no Revit fixture within {MatchRadiusFeet:0} ft" +
                             (c.DrawnDrain != null ? $" (the plan draws it: {c.DrawnDrain})" : "") +
                             (floors > 0 ? $"; the same gap on {floors} other floor(s) at this spot" : "") +
                             ". Not placed: add the fixture to the model or place the sleeve by hand."
                });
            }
            assembly.Crossings.RemoveAll(gaps.Contains);
        }

        /// <summary>Step 3: the sleeve point(s) from the fixture drawn on the plan (set by the plan reader), placed.</summary>
        private static void FromDrawing(Crossing c, string why)
        {
            c.Status = Crossing.Place;
            c.Confidence = "medium";
            c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
            c.Notes.Add($"{why}; position from the fixture drawn on the plan: {c.DrawnDrain} (manual, toilet sleeves page)");
        }

        /// <summary>0 = the fixture's own sleeve system, 1 = a generic sanitary/unknown sleeve, -1 = cannot serve this fixture.</summary>
        private static int SystemRank(string code, string system)
        {
            switch (code?.ToUpperInvariant())
            {
                case "WC": if (system == "Toilet") return 0; break;
                case "BT": if (system == "Bathtub") return 0; break;
                case "SH": case "FD": if (system == "Storm") return 1; break;
            }
            return system == "Sanitary" || system == "?" ? 1 : -1;
        }

        /// <summary>Notes each review label with its stack (same fixture, same plan spot on other floors). Returns the number of stacks.</summary>
        private static int GroupStacks(List<Crossing> left)
        {
            var stacks = new List<List<Crossing>>();
            foreach (var c in left.Where(c => c.HasPosition))
            {
                var stack = stacks.FirstOrDefault(s => s[0].Tag == c.Tag && s.Any(o => o.Floor != c.Floor && Dist(o.X, o.Y, c.X, c.Y) <= StackFeet));
                if (stack == null) stacks.Add(stack = new List<Crossing>());
                stack.Add(c);
            }
            int n = 0;
            foreach (var s in stacks.OrderBy(s => s[0].Tag).ThenBy(s => s[0].X))
            {
                n++;
                if (s.Count < 2) continue;
                var floors = string.Join(", ", s.Select(c => FloorKey.Describe(c.Floor)).Distinct());
                foreach (var c in s) c.Notes.Add($"stack {s[0].Tag}#{n} ({s.Count} floors: {floors}): decide once for the whole stack");
            }
            return n + left.Count(c => !c.HasPosition);
        }

        /// <summary>Floors that have fixtures modelled but where most of the plan's fixture labels find none: drawings vs model out of sync.</summary>
        private static List<string> OutOfSync(List<Crossing> labels, List<Fixture> fixtures)
        {
            var result = new List<string>();
            foreach (var floor in labels.Where(c => c.Level != null).GroupBy(c => c.Level))
            {
                var here = fixtures.Where(f => string.Equals(f.Level, floor.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (here.Count == 0 || floor.Count() < 3) continue;          // nothing modelled: not a mismatch, the model just has no fixtures
                int missing = floor.Count(c => c.ExistingIds.Count == 0 && !here.Any(f => Dist(c.X, c.Y, f.Point) <= MatchRadiusFeet));
                if (missing >= OutOfSyncShare * floor.Count()) result.Add($"{floor.Key} ({missing} of {floor.Count()} labels)");
            }
            return result;
        }

        private static bool IsFixtureReview(Crossing c) => c.Status == Crossing.Review && c.Confidence == "low" && c.Tag != null &&
            c.From.Any(x => x.IndexOf("fixture '", StringComparison.OrdinalIgnoreCase) >= 0);

        private static double Dist(double x, double y, XYZ p) => Dist(x, y, p.X, p.Y);

        private static double Dist(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Matches(string code, FixtureSleeve sleeve, string name)
        {
            string pattern = code.ToUpperInvariant() switch
            {
                "WC" => @"toilet|water.?closet|\bwc\b",
                "LAV" => @"lavatory|\blav\b",
                "BT" => @"bathtub|\btub\b",
                "KS" => @"kitchen.?sink|\bks\b",
                "LS" => @"laundry.?sink|\bls\b",
                "W/D" => @"washer|washing.?machine|\bw/d\b|\bwd\b",
                "WD" => @"washer|washing.?machine|\bwd\b",
                "SH" => @"shower",
                "FD" => @"floor.?drain|\bfd\b",
                _ => Regex.Escape(sleeve?.Name ?? code)
            };
            return Regex.IsMatch(name ?? "", pattern, RegexOptions.IgnoreCase);
        }

        private static List<Fixture> ReadFixtures(Document host, ExistingReport existing)
        {
            var sleeves = new HashSet<ElementId>(existing?.Items.Select(i => i.Id) ?? Enumerable.Empty<ElementId>());
            var result = Read(host, Transform.Identity, "this model", sleeves);
            foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = link.GetLinkDocument();
                if (linked != null) result.AddRange(Read(linked, link.GetTotalTransform(), $"link '{link.Name}'", null));
            }
            return result;
        }

        private static List<Fixture> Read(Document doc, Transform transform, string source, HashSet<ElementId> sleeves)
        {
            var result = new List<Fixture>();
            foreach (var category in new[] { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Casework, BuiltInCategory.OST_GenericModel,
                                             BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_MechanicalEquipment })
            foreach (var instance in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                // sleeves and openings (ours, the office's hand-placed families, or any *sleeve*/*opening* family) are not fixtures
                if (sleeves != null && sleeves.Contains(instance.Id)) continue;
                if (OpeningData.Read(instance) != null) continue;
                string name = $"{instance.Symbol?.Family?.Name} {instance.Symbol?.Name} {instance.Name}";
                if (NotFixture.IsMatch(name)) continue;

                var box = instance.get_BoundingBox(null);
                var local = (instance.Location as LocationPoint)?.Point ?? (box == null ? null : (box.Min + box.Max) / 2);
                if (local == null) continue;
                var fixture = new Fixture
                {
                    Name = name,
                    Source = source,
                    Level = (doc.GetElement(instance.LevelId) as Level)?.Name,
                    Point = transform.OfPoint(local)
                };
                var manager = instance.MEPModel?.ConnectorManager;
                if (manager != null)
                    foreach (Connector connector in manager.Connectors)
                    {
                        if (connector.Domain != Domain.DomainPiping || connector.ConnectorType != ConnectorType.End) continue;
                        if (connector.PipeSystemType.ToString().IndexOf("Sanitary", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        fixture.Drains.Add(transform.OfPoint(connector.Origin));
                    }
                result.Add(fixture);
            }
            return result;
        }
    }
}
