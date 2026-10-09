using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Settles the fixture labels read from a plan (WC, LAV, BT...) against the model, in this order:
    /// 1. a sleeve already in the model under the fixture -> already in the model (nothing placed);
    /// 2. the matching Revit fixture (one label, one fixture, nearest pairs first, up to <see cref="MismatchRadiusFeet"/>
    ///    away: the model wins when the plans show the fixture somewhere else) -> sleeve from that fixture: its sanitary
    ///    connector, else its shape (toilet 1'-1" from its back, sink in the wall behind it, tub at its piped end); the
    ///    fixture drawn on the plan is used instead when it sits on the modelled one and the model has no connector;
    /// 2b. no Revit fixture, but the architect's DWG in the model draws it (plumbing.dwgFixtures) -> its drawing's drain spot;
    /// 3. no sleeve, no Revit fixture, no DWG drawing: the model decides. On a floor where the model has fixtures (Revit
    ///    families or the architect's DWG) -> a model/PDF gap, reported and not placed. Only on a floor with no fixtures in
    ///    the model at all, plumbing.fixtureGaps "place" -> the same fixture's sleeve on the floor above/below at that spot,
    ///    else the fixture drawn on the plan, else review; flagged for a check ("report" = reported, not placed);
    /// 4. a matched fixture whose shape gives no spot -> the fixture as drawn on the PDF, else the fixture's insertion
    ///    point, flagged;
    /// 5. a fixture in the model no plan label names, or one the label disagrees with (plumbing.modelFixtures): Revit
    ///    families and the architect's DWG fixtures (Extract Fixtures' reading) -> its sleeve from the model all the same,
    ///    flagged for a check; no spot -> its insertion point. A floor whose modelled fixtures mostly disagree with the plan
    ///    is reported once as out of sync.
    /// Plan text is deliberately used only to select nearby elements; it is never used as the sleeve centre.
    /// </summary>
    public static class FixtureSleeveLocator
    {
        private const double MatchRadiusFeet = 6.0;
        /// <summary>A modelled fixture this far from its plan label is still the label's fixture (the plans are out of date there).</summary>
        private const double MismatchRadiusFeet = 15.0;
        /// <summary>The fixture drawn on the plan is the modelled one when their sleeve points are this close.</summary>
        private const double SameSpotFeet = 1.5;
        /// <summary>How far from the plan label a sleeve already in the model can be and still be the fixture's sleeve.</summary>
        private const double ExistingRadiusFeet = 4.0;
        /// <summary>The same, from the drain point of the fixture drawn on the plan (a closer, surer spot than the label).</summary>
        private const double ExistingFromDrainFeet = 2.5;
        /// <summary>The same, from a modelled fixture's sleeve point (no plan label).</summary>
        private const double ExistingFromModelFeet = 1.5;
        /// <summary>A label with no Revit fixture takes the sleeve of the same fixture this close on another floor (the stack).</summary>
        private const double StackCopyFeet = 3.0;
        /// <summary>Two fixture sleeves this close are one (a shower base and its drain modelled as two families).</summary>
        private const double DuplicateFeet = 1.0;
        /// <summary>A sleeve this close to a riser crossing belongs to the riser, not to a fixture.</summary>
        private const double RiserSleeveFeet = 1.0;
        /// <summary>Fixture labels on different floors this close (plan position) are one stack.</summary>
        private const double StackFeet = 1.5;
        /// <summary>Share of a floor's fixture labels with no modelled fixture, on a floor that has fixtures, that makes it out of sync.</summary>
        private const double OutOfSyncShare = 0.6;
        /// <summary>Toilet sleeve centre from the back of the toilet (manual: 1'-1" from the wall).</summary>
        private const double ToiletFromWallFeet = 13.0 / 12;
        /// <summary>Sink, lavatory, washer and wall-hung toilet sleeves: this far into the wall behind the fixture (half a stud wall).</summary>
        private const double IntoWallFeet = 2.5 / 12;
        /// <summary>A tub without a drain connector: its drain this far in from the piped end, on its centre line.</summary>
        private const double TubDrainFromEndFeet = 1.0;

        /// <summary>Sleeve and opening families are never fixtures (the office tub sleeve "MPI - Double Sleeve for Tub" matched "tub"), nor fixture accessories.</summary>
        private static readonly Regex NotFixture = new Regex(@"sleeve|opening|penetration|dish.?washer|grab.?bar|curtain|shower.?rod|accessor|mirror|faucet|\bvalve\b", RegexOptions.IgnoreCase);
        private static readonly Regex WallHung = new Regex(@"wall.?(hung|mount)", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> IntoWall = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LAV", "KS", "LS", "W/D", "WD" };

        private class Fixture
        {
            public string Name, Source, Level;
            public XYZ Point;
            public List<XYZ> Drains = new List<XYZ>();
            /// <summary>The other piping connectors (water): a tub with no drain connector drains at the end they are on.</summary>
            public List<XYZ> Pipes = new List<XYZ>();
            public XYZ Min, Max;                 // plan box, host coordinates
            /// <summary>Out of the wall the fixture stands against (host plan); null when the family has no facing.</summary>
            public XYZ Facing;
            public bool WallHung;
        }

        /// <summary>Resolve fixture-review crossings in place. Returns reader-friendly results for the check summary.</summary>
        /// <param name="onlyLevels">Auto Run's chosen floors (Revit level names): the architect's DWG is read on those only; null = all.</param>
        public static List<string> Apply(Document host, RiserAssembly assembly, PlumbingRules rules, ExistingReport existing = null, ICollection<string> onlyLevels = null)
        {
            var messages = new List<string>();
            if (host == null || assembly == null || rules == null ||
                !string.Equals(rules.FixtureSleeves, "model", StringComparison.OrdinalIgnoreCase)) return messages;

            var labels = assembly.Crossings.Where(IsFixtureReview).Where(c => rules.Fixtures.ContainsKey(c.Tag)).ToList();
            if (labels.Count == 0 && !rules.ModelFixtures) return messages;

            int inModel = ClaimExisting(labels, assembly, existing);

            var fixtures = ReadFixtures(host, existing, rules, messages);
            int connector = 0, shape = 0, drawn = 0, moved = 0, noSpot = 0;
            var gaps = new List<Crossing>();

            // one label, one modelled fixture, nearest pairs first: two toilets side by side each keep their own, and a
            // fixture the plans show somewhere else is still the label's fixture (the model is what gets built)
            var open = labels.Where(c => c.ExistingIds.Count == 0).ToList();
            var pairs = (from c in open
                         where c.HasPosition
                         from f in fixtures
                         where AutoPlacer.SameFloor(f.Level, c.Level) && Matches(c.Tag, f.Name, true)
                         let d = Dist(c.X, c.Y, f.Point)
                         where d <= MismatchRadiusFeet
                         orderby d
                         select (C: c, F: f, D: d)).ToList();
            var match = new Dictionary<Crossing, (Fixture F, double D)>();
            var used = new HashSet<Fixture>();
            foreach (var p in pairs)
                if (!match.ContainsKey(p.C) && !used.Contains(p.F)) { match[p.C] = (p.F, p.D); used.Add(p.F); }

            foreach (var c in open)
            {
                // the plan shows a fixture the model does not have (architecture only in DWG links): step 3
                if (!match.TryGetValue(c, out var m)) { gaps.Add(c); continue; }
                ModelKind(c, Classify(m.F.Name, rules), rules);
                var sleeve = rules.Fixtures[c.Tag];
                var (points, how, sure) = SleevePoints(m.F, c.Tag, sleeve);
                string away = m.D > MatchRadiusFeet ? $"{m.D:0.#} ft from where the plan shows it (the plan looks out of date here)" : $"{m.D * 12:0.#}\" from the plan label";
                if (points == null)
                {
                    if (c.DrawnDrain != null) { FromDrawing(c, $"matched Revit fixture '{m.F.Name}' ({m.F.Source}): {how}"); drawn++; }
                    else
                    {
                        // the fixture is in the model: it gets its sleeve, at the fixture itself, flagged
                        SetPoints(c, new List<XYZ> { Flat(m.F.Point) });
                        c.Back = BackOf(m.F) ?? c.Back;
                        c.Status = Crossing.Place; c.Confidence = "low"; c.FromModel = true; c.Check = true;
                        c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
                        c.Notes.Add($"matched Revit fixture '{m.F.Name}' ({m.F.Source}), {away}: {how}; placed at the fixture's insertion point: check and move it to the drain");
                        noSpot++;
                    }
                    continue;
                }
                // the fixture drawn on the plan is laid out by the manual: better than the model's shape when both are at one spot
                if (!sure && c.DrawnDrain != null && Dist(c.X, c.Y, Mean(points)) <= SameSpotFeet)
                {
                    FromDrawing(c, $"matched Revit fixture '{m.F.Name}' ({m.F.Source}) is at the drawn fixture");
                    c.Back = BackOf(m.F) ?? c.Back;
                    drawn++; continue;
                }

                SetPoints(c, points);
                c.Back = BackOf(m.F) ?? c.Back;
                c.Status = Crossing.Place;
                c.Confidence = sure ? "high" : "medium";
                c.FromModel = true;
                c.Check = !sure || m.D > MatchRadiusFeet;
                c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
                c.Notes.Add($"position from {how} of Revit fixture '{m.F.Name}' ({m.F.Source}), {away}");
                if (sure) connector++; else shape++;
                if (m.D > MatchRadiusFeet) moved++;
            }

            // step 2b: no Revit fixture, but the architect's DWG in the model draws it
            var dwgShapes = new List<(DwgFixtureReader.Plan Plan, FixtureDrains.Shape Shape)>();
            var dwgLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fromDwg = 0;
            if (rules.DwgFixtures && (gaps.Count > 0 || rules.ModelFixtures))
            {
                try { fromDwg = FromDwg(DwgFixtureReader.Read(host, rules, onlyLevels: onlyLevels), gaps, labels, rules, dwgShapes, dwgLevels, messages); }
                catch (Exception ex) { App.Log("AutoRun DWG fixtures failed: " + ex); messages.Add("Fixtures in the architect's DWGs not read: " + ex.Message); }
            }

            // step 3: no Revit fixture and no DWG drawing for the label. The model decides: on a floor where the model has
            // fixtures (Revit families or the architect's DWG), a label it has nothing for is a model/PDF mismatch, reported
            // and not placed. Only a floor with no fixtures in the model at all (nothing to compare with) follows fixtureGaps.
            var modelFloors = fixtures.Where(f => f.Level != null && Classify(f.Name, rules) != null).Select(f => f.Level).Concat(dwgLevels)
                                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var mismatch = gaps.Where(c => modelFloors.Any(l => AutoPlacer.SameFloor(l, c.Level))).ToList();
            gaps.RemoveAll(mismatch.Contains);
            int stacked = 0, fromPdf = 0, textOnly = 0;
            if (string.Equals(rules.FixtureGaps, "place", StringComparison.OrdinalIgnoreCase))
            {
                (stacked, fromPdf, textOnly) = PlaceWithoutFixture(gaps, labels, fixtures, existing, rules, dwgLevels);
                gaps.Clear();
                drawn += fromPdf;
            }
            gaps.AddRange(mismatch);
            int duplicates = Duplicates(labels.Where(c => c.Status == Crossing.Place && c.ExistingIds.Count == 0).ToList(), assembly);
            int served = Served(labels.Where(c => c.Status == Crossing.Place && c.ExistingIds.Count == 0).ToList(), assembly, rules);

            var outOfSync = OutOfSync(labels, fixtures);
            ReportGaps(gaps, assembly);
            labels.RemoveAll(gaps.Contains);
            var left = labels.Where(c => c.Status == Crossing.Review).ToList();
            int stacks = GroupStacks(left);

            messages.Add($"Fixture sleeves: {inModel} already in the model, {connector} from Revit sanitary connectors, {shape} from the modelled fixture's shape, " +
                         $"{fromDwg} from the fixture drawn in the architect's DWG, {stacked} from the same fixture's sleeve on the floor above/below, {drawn} from the fixtures drawn on the plans " +
                         $"({served} served by a sleeve already there, {duplicates} on another label's sleeve)" +
                         (moved > 0 ? $"; {moved} modelled more than {MatchRadiusFeet:0} ft from the plan (model position used, flagged for a check)" : "") +
                         (gaps.Count > 0 ? $"; {gaps.Count} on the plans but not in the model (reported as model/PDF gaps, not placed)" : "") +
                         (noSpot > 0 ? $"; {noSpot} at the modelled fixture's insertion point (no drain spot on it: check)" : "") +
                         $"; {left.Count} left for review in {stacks} stack(s) ({textOnly} not in the architect's DWG or with only the label text).");
            if (rules.ModelFixtures)
                messages.Add(ModelOnly(fixtures.Where(f => !used.Contains(f)).ToList(), dwgShapes, assembly, rules, existing));
            messages.Add(WetWall(assembly, rules, existing));
            foreach (var level in outOfSync)
                messages.Add($"  ! {level}: most fixture labels on the plan have no modelled fixture nearby — the drawings and the model look out of sync on this floor (which is newer?).");
            foreach (var line in messages) App.Log("AutoRun " + line.Trim());
            return messages;
        }

        /// <summary>
        /// Step 3 (plumbing.fixtureGaps "place"): a label no Revit fixture answers (the architecture is only in DWG links)
        /// takes, in order, the position of the same fixture's sleeve already in the model on the nearest floor at that spot
        /// (bathrooms stack: the office's own sleeve), else the fixture drawn on the plan, else stays for review. Placed
        /// ones are flagged for a check. Returns (from the stack, from the drawing, label text only).
        /// </summary>
        private static (int Stacked, int Drawn, int TextOnly) PlaceWithoutFixture(List<Crossing> gaps, List<Crossing> labels, List<Fixture> fixtures,
                                                                                ExistingReport existing, PlumbingRules rules, HashSet<string> dwgLevels)
        {
            // a floor whose architect's DWG shows its fixtures: a label the DWG does not confirm is not guessed (the floor
            // above may be laid out differently, the PDF's background may be older): review, with the guesses as notes
            var onDwg = gaps.Where(c => dwgLevels.Any(l => AutoPlacer.SameFloor(l, c.Level))).ToList();
            foreach (var c in onDwg)
            {
                c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
                c.Notes.Add($"not found in the architect's DWG within {DwgSearchInches / 12:0} ft of where the plan shows it" +
                            (c.DrawnDrain != null ? $" (the plumbing PDF draws it: {c.DrawnDrain})" : "") + "; review required");
            }
            int review = onDwg.Count;
            gaps = gaps.Except(onDwg).ToList();

            var sleeveAt = existing?.Items.Where(e => e.Point != null).GroupBy(e => e.Id.Value).ToDictionary(g => g.Key, g => g.First().Point)
                           ?? new Dictionary<long, XYZ>();
            int stacked = 0, drawn = 0, textOnly = 0;
            foreach (var c in gaps)
            {
                bool modelled = fixtures.Any(f => AutoPlacer.SameFloor(f.Level, c.Level) && Classify(f.Name, rules) != null);
                string why = modelled ? $"no {c.Tag} in the model within {MismatchRadiusFeet:0} ft" : "no Revit fixtures on this floor (the architecture is only in the DWG)";
                var stack = !c.HasPosition ? null : labels
                    .Where(o => o != c && o.Tag == c.Tag && o.Floor != c.Floor && o.ExistingIds.Count > 0 && o.ExistingIds.All(sleeveAt.ContainsKey) &&
                                Dist(o.X, o.Y, c.X, c.Y) <= StackCopyFeet)
                    .OrderBy(o => Math.Abs(FloorKey.Order(o.Floor) - FloorKey.Order(c.Floor))).ThenBy(o => Dist(o.X, o.Y, c.X, c.Y))
                    .FirstOrDefault();
                c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
                if (stack != null)
                {
                    SetPoints(c, stack.ExistingIds.Select(id => Flat(sleeveAt[id])).ToList());
                    c.Back = stack.Back ?? c.Back;
                    c.Status = Crossing.Place;
                    c.Confidence = "medium";
                    c.FromModel = true;
                    c.Check = true;
                    c.Notes.Add($"{why}; position of the {c.Tag} sleeve already in the model on the {FloorKey.Describe(stack.Floor)} at this spot (the bathrooms stack)");
                    stacked++;
                }
                else if (c.DrawnDrain != null)
                {
                    FromDrawing(c, why);
                    c.Check = true;
                    drawn++;
                }
                else
                {
                    c.Notes.Add($"{why}; the plan gives only the label text (no fixture drawn, no sleeve on the floors above/below at this spot); review required");
                    textOnly++;
                }
            }
            return (stacked, drawn, textOnly + review);
        }

        /// <summary>Toilet vent sleeve: this far (feet) along the wall behind the toilet from its centre line (manual, toilet sleeves page: 5 1/4").</summary>
        private const double ToiletVentAlongFeet = 5.25 / 12;
        /// <summary>A lavatory's vent sleeve goes on the side of a vent stack this close (feet) in the same wall.</summary>
        private const double VentStackFeet = 6.0;
        /// <summary>How far (feet) a vent sleeve may slide along the wall to clear the sleeves already there.</summary>
        private const double SlideFeet = 1.0;

        /// <summary>
        /// The sleeves beside the fixtures' drain sleeves (24 Skillman S&amp;O set, manual toilet sleeves page). Only the stacks
        /// and the fixtures' drains go through the slab; the rest of a fixture's pipes run in the wall to the stack:
        /// - a sink with a sanitary or vent stack sleeve within its stackReach: the stack serves it, no sleeve of its own;
        /// - a toilet: a vent sleeve in the wall behind it, 5 1/4" off its centre line (away from a stack there);
        /// - a lavatory: a vent sleeve beside its drain sleeve in the wall (on the vent stack's side when one is in that wall);
        /// - a sink with no stack near (an island, a sink on its own wall): its own row behind it, vent - hot - drain - cold,
        ///   hot on the left facing it (IRC P2722.2).
        /// The sleeves touch (plumbing.sleeveGap clear), a vent slides along the wall off the sleeves already there, and every
        /// one is flagged for a check. A fixture whose wall is not known gets a note instead.
        /// </summary>
        private static string WetWall(RiserAssembly assembly, PlumbingRules rules, ExistingReport existing)
        {
            double gap = rules.SleeveGap / 12;
            double R(double pipe) => rules.SleeveFor(pipe) / 2 / 12;
            double Rc(Crossing o) => R(o.Size?.Diameter ?? 2);
            int served = 0, vents = 0, water = 0, noWall = 0, already = 0, clash = 0;

            IEnumerable<(double X, double Y, double R)> Taken(string floor) =>
                assembly.Crossings.Where(o => o.Floor == floor && o.Status == Crossing.Place && o.MergedInto == null && o.HasPosition)
                    .SelectMany(o => o.EachPoint && o.Points.Count > 0 ? o.Points.Select(p => (p[0], p[1], Rc(o))) : new[] { (o.X, o.Y, Rc(o)) });
            bool Clear(string floor, double x, double y, double r) => Taken(floor).All(t => Dist(t.X, t.Y, x, y) >= t.R + r + gap - 1e-6);
            // a sleeve already in the model there (the office's own, an earlier run's): nothing more
            bool InModel(Crossing fx, double x, double y, double r) => existing != null && existing.Items.Any(e => e.Point != null && e.Source != "native opening" &&
                AutoPlacer.SameFloor(e.Level, fx.Level) && Dist(x, y, e.Point) <= r + 1.0 / 12);

            void Add(Crossing fx, string service, string system, double pipe, double x, double y, string where, bool clear)
            {
                if (InModel(fx, x, y, R(pipe))) { already++; return; }
                var c = new Crossing
                {
                    Floor = fx.Floor, Level = fx.Level, Tag = $"{service}-{fx.Tag}", System = system, Size = new DuctSize { Diameter = pipe },
                    X = x, Y = y, HasPosition = true, Status = Crossing.Place, Confidence = "medium", Check = true, FromModel = fx.FromModel,
                    Pdf = "not in the PDF", Back = fx.Back
                };
                c.From.Add($"{FloorKey.Describe(fx.Floor)}: fixture '{fx.Tag}' ({rules.Fixtures[fx.Tag].Name}) {service} sleeve");
                c.Notes.Add($"{Units.FormatInches(rules.SleeveFor(pipe))} {service} sleeve {where}: by the office rule (the plans do not draw it), check");
                if (!clear) { c.Notes.Add("overlaps a sleeve already there: move it by hand"); clash++; }
                assembly.Crossings.Add(c);
                if (system == "Vent") vents++; else water++;
            }

            var stacks = assembly.Crossings.Where(o => !o.IsFixture && o.HasPosition && o.Status != Crossing.Skip && o.MergedInto == null &&
                                                       (o.System == "Sanitary" || o.System == "Vent")).ToList();
            var fixtures = assembly.Crossings.Where(c => c.IsFixture && c.Status == Crossing.Place && c.MergedInto == null && c.HasPosition && !c.EachPoint &&
                                                         c.ExistingIds.Count == 0 && c.Tag != null && rules.Fixtures.ContainsKey(c.Tag)).ToList();
            foreach (var fx in fixtures)
            {
                var fs = rules.Fixtures[fx.Tag];
                if (fs.StackReach > 0)
                {
                    var stack = stacks.Where(o => o.Floor == fx.Floor).Select(o => (O: o, D: Dist(o, fx))).Where(t => t.D <= fs.StackReach / 12)
                                      .OrderBy(t => t.D).FirstOrDefault();
                    if (stack.O != null)
                    {
                        fx.Status = Crossing.Skip; fx.MergedInto = stack.O;
                        fx.Notes.Add($"drains in the wall to the {stack.O.Tag} stack {stack.D * 12:0}\" away: only the stack goes through the slab, " +
                                     $"its sleeves serve the {fs.Name} (plumbing.fixtures stackReach)");
                        served++;
                        continue;
                    }
                }
                if (fs.Vent <= 0 && fs.Water <= 0) continue;
                if (fx.Back == null)
                {
                    fx.Notes.Add("the wall behind the fixture is not known: its vent" + (fs.Water > 0 ? " and hot/cold water" : "") + " sleeves are not laid out, add them by hand");
                    fx.Check = true; noWall++;
                    continue;
                }
                double bx = fx.Back[0], by = fx.Back[1];
                double ax = -by, ay = bx;                 // along the wall, to the left of someone facing it
                double rw = Rc(fx);

                if (fs.Water > 0)
                {
                    // its own row behind it: vent - hot - drain - cold, hot on the left
                    double rh = R(fs.Water), rv = R(fs.Vent);
                    double hot = rw + rh + gap, cold = -(rw + rh + gap), vent = hot + rh + rv + gap;
                    foreach (var (service, system, pipe, along) in new[] { ("HW", "HotWater", fs.Water, hot), ("CW", "ColdWater", fs.Water, cold), ("V", "Vent", fs.Vent, vent) })
                    {
                        if (pipe <= 0) continue;
                        double x = fx.X + ax * along, y = fx.Y + ay * along;
                        Add(fx, service, system, pipe, x, y, $"in the {fs.Name}'s own row behind it ({(along > 0 ? "left" : "right")} of its drain, facing it): no stack within " +
                            $"{Units.FormatInches(fs.StackReach)}", Clear(fx.Floor, x, y, R(pipe)));
                    }
                    continue;
                }

                // a vent beside the drain, in the wall behind (a toilet's drain is 1'-1" out from that wall)
                double r = R(fs.Vent);
                bool toilet = string.Equals(fx.Tag, "WC", StringComparison.OrdinalIgnoreCase);
                bool inWall = !toilet || fx.Notes.Any(n => n.Contains("wall-hung"));
                double wx = fx.X, wy = fx.Y;
                if (!inWall) { wx += bx * (ToiletFromWallFeet + IntoWallFeet); wy += by * (ToiletFromWallFeet + IntoWallFeet); }
                double first = toilet ? ToiletVentAlongFeet : rw + r + gap;
                // which side: a toilet's vent away from a stack in its wall (the stack sits on the other side, manual); a
                // lavatory's toward the vent stack in its wall
                int side = 1;
                var near = stacks.Where(o => o.Floor == fx.Floor && (toilet ? o.System == "Sanitary" : o.System == "Vent"))
                                 .Where(o => Math.Abs((o.X - wx) * bx + (o.Y - wy) * by) <= 1.0)
                                 .Select(o => (O: o, D: Dist(o.X, o.Y, wx, wy))).Where(t => t.D <= (toilet ? 2.0 : VentStackFeet)).OrderBy(t => t.D).FirstOrDefault();
                if (near.O != null)
                {
                    int toward = (near.O.X - wx) * ax + (near.O.Y - wy) * ay >= 0 ? 1 : -1;
                    side = toilet ? -toward : toward;
                }
                (double X, double Y)? spot = null;
                for (double extra = 0; extra <= SlideFeet + 1e-9 && spot == null; extra += 1.0 / 12)
                    foreach (int s in new[] { side, -side })
                    {
                        double x = wx + ax * s * (first + extra), y = wy + ay * s * (first + extra);
                        if (Clear(fx.Floor, x, y, r)) { spot = (x, y); break; }
                    }
                bool clear = spot != null;
                var at = spot ?? (wx + ax * side * first, wy + ay * side * first);
                Add(fx, "V", "Vent", fs.Vent, at.X, at.Y, toilet && !inWall ? "in the wall behind the toilet, beside its centre line" : "beside its drain sleeve, in the wall behind it", clear);
            }
            return $"Sleeves beside the fixtures: {served} sink(s) served by the stack in their wall (no sleeve of their own), {vents} vent and {water} hot/cold water " +
                   $"sleeve(s) placed by the office rule (flagged for a check)" +
                   (already > 0 ? $", {already} already in the model" : "") +
                   (clash > 0 ? $", {clash} overlapping another sleeve (move by hand)" : "") +
                   (noWall > 0 ? $", {noWall} fixture(s) whose wall is not known (add them by hand)" : "") + ".";
        }

        /// <summary>The wall behind a modelled fixture (against its facing), Revit plan; null when the family has no facing.</summary>
        private static double[] BackOf(Fixture f)
        {
            if (f.Facing == null) return null;
            double len = Math.Sqrt(f.Facing.X * f.Facing.X + f.Facing.Y * f.Facing.Y);
            return len < 1e-6 ? null : new[] { -f.Facing.X / len, -f.Facing.Y / len };
        }

        private static double[] Dir((double X, double Y)? d) => d.HasValue ? new[] { d.Value.X, d.Value.Y } : null;

        /// <summary>
        /// Two labels of one fixture (a double sink read twice, a label repeated) that end on the same spot: the second is
        /// served by the first. Returns how many.
        /// </summary>
        private static int Duplicates(List<Crossing> placed, RiserAssembly assembly)
        {
            int n = 0;
            foreach (var c in placed.Where(c => c.Status == Crossing.Place))
            {
                var other = assembly.Crossings.FirstOrDefault(o => o != c && o.IsFixture && o.Tag == c.Tag && o.Floor == c.Floor && o.Status == Crossing.Place &&
                                                                   o.MergedInto == null && o.HasPosition && Dist(o.X, o.Y, c.X, c.Y) <= DuplicateFeet);
                if (other == null) continue;
                c.MergedInto = other;
                c.Status = Crossing.Skip;
                c.Notes.Add($"lands on the {other.Tag} sleeve {Dist(other, c) * 12:0.#}\" away: the same fixture, one sleeve");
                n++;
            }
            return n;
        }

        private static double Dist(Crossing o, Crossing c) => Dist(o.X, o.Y, c.X, c.Y);

        /// <summary>A labelled fixture's drawing this far (inches) from where the plan puts it, in the architect's DWG.</summary>
        private const double DwgSearchInches = 36;

        /// <summary>
        /// Step 2b: the architect's DWGs in the model, level by level. Every label of the level claims its drawing (so a
        /// fixture already sleeved is never guessed again); the labels with no Revit fixture take their drawing's drain
        /// spot. The drawings no label claims that are a toilet, tub or shower by shape go to <paramref name="shapes"/>.
        /// Returns how many labels were placed.
        /// </summary>
        private static int FromDwg(List<DwgFixtureReader.Plan> plans, List<Crossing> gaps, List<Crossing> labels, PlumbingRules rules,
                                   List<(DwgFixtureReader.Plan, FixtureDrains.Shape)> shapes, HashSet<string> dwgLevels, List<string> messages = null)
        {
            int n = 0;
            (int, double) Sleeves(string code) => rules.Fixtures.TryGetValue(code, out var fs) ? (fs.Count, fs.Spacing) : (1, 0);

            // the architect's blocks, read from the files: each kind identified once over every floor, from the plumbing
            // labels next to its copies, its drawing and its name (no one names anything), then located
            var map = FixtureBlockMap.Load();
            var cats = plans.Where(p => p.Strokes.Count > 0).ToDictionary(p => p, p => DwgFixtureCatalog.Read(p, rules, map));
            var votes = labels.Where(c => c.HasPosition && c.Level != null)
                              .Select(c => new DwgFixtureCatalog.Label { Level = c.Level, Tag = c.Tag, X = c.X, Y = c.Y }).ToList();
            var unclear = DwgFixtureCatalog.Identify(cats.Values.ToList(), votes, map);
            foreach (var c in cats.Values.Where(c => c.Used)) DwgFixtureCatalog.Locate(c, rules);
            foreach (var note in unclear.Distinct()) { App.Log("AutoRun fixtures: " + note); messages?.Add("  ! " + note); }
            var kinds = cats.Values.Where(c => c.Used).SelectMany(c => c.Items).Where(i => i.Code != null).GroupBy(i => i.Name)
                            .Select(g => $"{g.Key} = {g.First().Code}{(g.First().Check ? " (check)" : "")}").ToList();
            if (kinds.Count > 0) messages?.Add("Fixtures in the architect's DWG blocks: " + string.Join(", ", kinds) + ".");

            foreach (var plan in plans.Where(p => p.Strokes.Count > 0))
            {
                var cat = cats[plan];
                if (cat.Note != null) App.Log("AutoRun fixtures: " + cat.Note);
                if (cat.Used && cat.Items.Any(i => i.Code != null && i.Points != null))
                {
                    n += FromBlocks(plan, cat, gaps, labels, rules, shapes, dwgLevels, Sleeves);
                    continue;
                }
                var here = labels.Where(c => c.HasPosition && AutoPlacer.SameFloor(plan.Level, c.Level)).ToList();
                var marks = here.Select(c => new FixtureMark { Code = c.Tag, X = c.X * 12, Y = c.Y * 12 }).ToList();
                FixtureDrains.Recognise(marks, plan.Strokes, plan.Walls, Sleeves, DwgSearchInches, ToiletFromWallFeet * 12, 72, false, plan.Edges);

                // every fixture the drawing shows (Extract Fixtures): those no label is on are the fixtures the plans do not label
                var all = FixtureDrains.Extract(plan.Strokes, plan.Walls, Sleeves, ToiletFromWallFeet * 12, 72, plan.Edges);
                if (all.Count > 0) dwgLevels.Add(plan.Level);
                bool Labelled(FixtureDrains.Shape s) =>
                    here.Any(c => Dist(c.X * 12, c.Y * 12, s.Cx, s.Cy) <= DwgSearchInches || s.Points.Any(p => Dist(c.X * 12, c.Y * 12, p.X, p.Y) <= DwgSearchInches)) ||
                    marks.Any(m => m.Drains.Any(d => s.Points.Any(p => Dist(d.X, d.Y, p.X, p.Y) <= 12)));
                var found = all.Where(s => !Labelled(s)).Select(s => { if (s.Code == "LAV/SINK") s.Code = "LAV"; return s; }).ToList();
                for (int i = 0; i < here.Count; i++)
                {
                    var c = here[i]; var m = marks[i];
                    if (!gaps.Contains(c) || m.Drains.Count == 0) continue;
                    SetPoints(c, m.Drains.Select(d => new XYZ(d.X / 12, d.Y / 12, 0)).ToList());
                    c.Back = Dir(m.Back);
                    c.Status = Crossing.Place;
                    c.Confidence = "high";
                    c.FromModel = true;
                    c.Notes.RemoveAll(t => t.Contains("position is the engineer's fixture text"));
                    c.Notes.Add($"position from the {c.Tag} drawn in the architect's DWG in the model ({plan.Source}): {m.DrainHow}");
                    gaps.Remove(c);
                    n++;
                }
                shapes.AddRange(found.Select(s => (plan, s)));
            }
            return n;
        }

        /// <summary>
        /// Step 2b from the DWG's named blocks (<see cref="DwgFixtureCatalog"/>), plus tubs and showers drawn line by line
        /// (by shape). Each plan label takes the nearest fixture of its kind within <see cref="MatchRadiusFeet"/> (beyond
        /// <see cref="DwgSearchInches"/> flagged), nearest pairs first; a label with no Revit fixture takes that fixture's sleeve point(s). The fixtures no label takes go to
        /// <paramref name="shapes"/> (step 5: placed and flagged). Returns how many labels were placed.
        /// </summary>
        private static int FromBlocks(DwgFixtureReader.Plan plan, DwgFixtureCatalog.Result cat, List<Crossing> gaps, List<Crossing> labels, PlumbingRules rules,
                                      List<(DwgFixtureReader.Plan, FixtureDrains.Shape)> shapes, HashSet<string> dwgLevels, Func<string, (int, double)> sleeves)
        {
            var fixtures = cat.Items.Where(i => i.Code != null && i.Points != null && rules.Fixtures.ContainsKey(i.Code))
                .Select(i => new FixtureDrains.Shape { Code = i.Code, Cx = i.Cx, Cy = i.Cy, X0 = i.X0, Y0 = i.Y0, X1 = i.X1, Y1 = i.Y1, Points = i.Points, Back = i.Back,
                                                       How = $"block '{i.Name}'{(i.Check ? " (check)" : "")}: {i.How} [{i.Why}]" })
                .ToList();
            fixtures.AddRange(FixtureDrains.Extract(DwgFixtureCatalog.Loose(plan, cat), plan.Walls, sleeves, ToiletFromWallFeet * 12, 72, plan.Edges)
                                           .Where(s => s.Code == "BT" || s.Code == "SH")
                                           .Select(s => { s.How = "by shape: " + s.How; return s; }));
            if (fixtures.Count > 0) dwgLevels.Add(plan.Level);

            double Gap(FixtureDrains.Shape s, double x, double y) =>
                Math.Sqrt(Math.Pow(Math.Max(Math.Max(s.X0 - x, 0), x - s.X1), 2) + Math.Pow(Math.Max(Math.Max(s.Y0 - y, 0), y - s.Y1), 2));
            var here = labels.Where(c => c.HasPosition && AutoPlacer.SameFloor(plan.Level, c.Level)).ToList();
            // nearest pairs first; a label with no fixture yet takes one further than DwgSearchInches (up to MatchRadiusFeet):
            // the model is what gets built, the plan looks out of date there (flagged). A label already settled (a sleeve
            // there, a Revit fixture) claims only the drawing right at it: a fixture further off keeps its own sleeve.
            // Past DwgSearchInches only the very same kind: a KS label 5 ft from a lavatory is not that lavatory.
            var pairs = (from c in here
                         from s in fixtures
                         where SameKind(c.Tag, s.Code)
                         let d = Gap(s, c.X * 12, c.Y * 12)
                         where d <= DwgSearchInches ||
                               (gaps.Contains(c) && d <= MatchRadiusFeet * 12 && string.Equals(c.Tag, s.Code, StringComparison.OrdinalIgnoreCase))
                         orderby d
                         select (C: c, S: s, D: d)).ToList();
            var claimed = new HashSet<FixtureDrains.Shape>();
            var done = new HashSet<Crossing>();
            int n = 0;
            foreach (var (c, s, d) in pairs)
            {
                if (done.Contains(c) || claimed.Contains(s)) continue;
                done.Add(c); claimed.Add(s);
                if (!gaps.Contains(c)) continue;                     // already sleeved or a Revit fixture: the drawing only claims it
                bool far = d > DwgSearchInches;
                ModelKind(c, s.Code, rules);                       // the drawn fixture's kind, not the label's
                SetPoints(c, s.Points.Select(p => new XYZ(p.X / 12, p.Y / 12, 0)).ToList());
                c.Back = Dir(s.Back);
                c.Status = Crossing.Place;
                c.Confidence = s.How.StartsWith("block") && !far ? "high" : "medium";
                c.FromModel = true;
                c.Check = !s.How.StartsWith("block") || s.How.Contains("(check)") || far;
                c.Notes.RemoveAll(t => t.Contains("position is the engineer's fixture text"));
                c.Notes.Add($"position from the {s.Code} in the architect's DWG in the model ({plan.Source}): {s.How}" +
                            (far ? $"; {d / 12:0.#} ft from where the plan shows it (the plan looks out of date here: model position used)" : ""));
                gaps.Remove(c);
                n++;
            }
            shapes.AddRange(fixtures.Where(s => !claimed.Contains(s)).Select(s => (plan, s)));
            int unnamed = cat.Items.Count(i => i.Code == null);
            App.Log($"AutoRun fixtures from blocks: {plan.Source} on {plan.Level}: {fixtures.Count} fixture(s) " +
                    $"({string.Join(", ", fixtures.GroupBy(s => s.Code).Select(g => $"{g.Count()} {g.Key}"))}), {n} plan label(s) placed from them" +
                    (unnamed > 0 ? $"; {unnamed} block(s) not named yet, not placed" : ""));
            return n;
        }

        /// <summary>A plan label and a drawn fixture are the same kind: the same code, any sink for a sink label, tub/shower, washer.</summary>
        private static bool SameKind(string label, string code)
        {
            if (string.Equals(label, code, StringComparison.OrdinalIgnoreCase)) return true;
            var sinks = new[] { "LAV", "KS", "LS" };
            var wet = new[] { "BT", "SH" };
            var washers = new[] { "W/D", "WD" };
            return (sinks.Contains(label) && sinks.Contains(code)) || (wet.Contains(label) && wet.Contains(code)) || (washers.Contains(label) && washers.Contains(code));
        }

        /// <summary>
        /// Step 5: fixtures no plan label names, modelled (Revit families) or drawn in the architect's DWG (toilets, tubs,
        /// showers by shape), get their sleeve all the same, unless a sleeve is already there, another fixture's sleeve is
        /// on the same spot, or a riser in the wall behind serves the sink. Flagged for a check.
        /// </summary>
        private static string ModelOnly(List<Fixture> fixtures, List<(DwgFixtureReader.Plan Plan, FixtureDrains.Shape Shape)> shapes,
                                        RiserAssembly assembly, PlumbingRules rules, ExistingReport existing)
        {
            if (assembly.FloorLevels == null) return "Fixtures not on the plans: not checked (no floor list).";
            int placed = 0, fromDwg = 0, already = 0, duplicate = 0, review = 0, lowest = 0, noFloor = 0;
            var made = new List<Crossing>();

            var candidates = new List<(string Code, string Level, List<XYZ> Points, string How, bool Sure, XYZ At, string Name, bool Dwg, double[] Back)>();
            foreach (var f in fixtures)
            {
                string code = Classify(f.Name, rules);
                if (code == null) continue;                                  // not a sleeved fixture (cabinet, counter...)
                var (points, how, sure) = SleevePoints(f, code, rules.Fixtures[code]);
                candidates.Add((code, f.Level, points, how + " of the Revit fixture", sure, points != null ? Mean(points) : f.Point, $"modelled as '{f.Name}' ({f.Source})", false, BackOf(f)));
            }
            foreach (var (plan, s) in shapes.Where(x => rules.Fixtures.ContainsKey(x.Shape.Code)))
            {
                var points = s.Points.Select(p => new XYZ(p.X / 12, p.Y / 12, 0)).ToList();
                candidates.Add((s.Code, plan.Level, points, s.How + " of the fixture drawn in the DWG", false, Mean(points), $"drawn in {plan.Source} (found by its shape)", true, Dir(s.Back)));
            }

            foreach (var (code, level, points, how, sure, at, name, dwg, back) in candidates)
            {
                var floor = assembly.FloorLevels.FirstOrDefault(kv => AutoPlacer.SameFloor(kv.Value, level));
                if (floor.Key == null) { noFloor++; continue; }
                if (rules.SkipLowestSlab && floor.Key == assembly.Lowest) { lowest++; continue; }

                var sleeve = rules.Fixtures[code];
                if (existing != null && existing.Items.Any(e => e.Point != null && e.Source != "native opening" && AutoPlacer.SameFloor(e.Level, floor.Value) &&
                                                               SystemRank(code, e.System) >= 0 && Dist(at.X, at.Y, e.Point) <= ExistingFromModelFeet))
                { already++; continue; }
                if (assembly.Crossings.Any(o => o.IsFixture && o.Floor == floor.Key && o.Status != Crossing.Skip && o.HasPosition && Dist(o.X, o.Y, at) <= DuplicateFeet))
                { duplicate++; continue; }

                var c = new Crossing
                {
                    Floor = floor.Key, Level = floor.Value, Tag = code, System = sleeve.System, Size = new DuctSize { Diameter = sleeve.Pipe },
                    HasPosition = true, Pdf = "not in the PDF", FromModel = true, Check = true, Back = back
                };
                c.From.Add($"{FloorKey.Describe(floor.Key)}: fixture '{code}' ({sleeve.Name}) {name}");
                c.Notes.Add($"{sleeve.Name}: in the model but not labelled on the plan");
                if (points == null)
                {
                    // every fixture in the model gets its sleeve: no spot from its connectors or shape = at the fixture itself
                    SetPoints(c, new List<XYZ> { Flat(at) });
                    c.Status = Crossing.Place; c.Confidence = "low";
                    c.Notes.Add($"{how}; placed at the fixture's insertion point: check and move it to the drain");
                    review++;
                    placed++;
                }
                else
                {
                    SetPoints(c, points);
                    c.Status = Crossing.Place; c.Confidence = sure ? "high" : "medium";
                    c.Notes.Add($"position from {how}");
                    placed++;
                    if (dwg) fromDwg++;
                }
                assembly.Crossings.Add(c);
                made.Add(c);
            }
            int served = Served(made.Where(c => c.Status == Crossing.Place).ToList(), assembly, rules);
            return $"Fixtures not on the plans: {placed - served} sleeve(s) placed ({fromDwg} of the {placed} found by shape in the architect's DWG; flagged for a check), {served} served by a sleeve next to them, " +
                   $"{already} already sleeved, {duplicate} on another fixture's sleeve, {review} of them at the fixture's insertion point (no drain spot: check)" +
                   (lowest > 0 ? $", {lowest} on the lowest level (slab on grade)" : "") +
                   (noFloor > 0 ? $", {noFloor} on levels the run does not cover" : "") + ".";
        }

        /// <summary>
        /// Where a modelled fixture's sleeve(s) go: its sanitary connector(s) when it has them (sure), else from its shape.
        /// Null points with the reason when neither gives a spot.
        /// </summary>
        private static (List<XYZ> Points, string How, bool Sure) SleevePoints(Fixture f, string code, FixtureSleeve sleeve)
        {
            int n = Math.Max(1, sleeve.Count);
            var centre = new XYZ((f.Min.X + f.Max.X) / 2, (f.Min.Y + f.Max.Y) / 2, 0);
            // the back of the fixture: the side of its box against the wall, on the fixture's centre line
            XYZ Back() => f.Facing == null ? null
                : centre - f.Facing * (Math.Abs(f.Facing.X) * (f.Max.X - f.Min.X) / 2 + Math.Abs(f.Facing.Y) * (f.Max.Y - f.Min.Y) / 2);

            switch (code.ToUpperInvariant())
            {
                case "BT":
                    {
                        bool alongX = f.Max.X - f.Min.X >= f.Max.Y - f.Min.Y;
                        var along = alongX ? XYZ.BasisX : XYZ.BasisY;
                        XYZ drain; string how; bool sure;
                        if (f.Drains.Count > 0) { drain = Flat(f.Drains[0]); how = "the sanitary connector"; sure = true; }
                        else if (f.Pipes.Count > 0)
                        {
                            // the drain is at the end the faucet (water connectors) is on
                            double half = (alongX ? f.Max.X - f.Min.X : f.Max.Y - f.Min.Y) / 2;
                            int end = (Mean(f.Pipes) - centre).DotProduct(along) >= 0 ? 1 : -1;
                            drain = centre + along * (end * Math.Max(0, half - TubDrainFromEndFeet));
                            how = "the piped end of the tub (1'-0\" in, on its centre line)"; sure = false;
                        }
                        else return (null, "the tub has no piping connectors to tell its drain end", false);
                        var pts = Enumerable.Range(0, n).Select(i => drain + along * ((i - (n - 1) / 2.0) * sleeve.Spacing / 12)).ToList();
                        return (pts, how + (n > 1 ? $" ({n} sleeves {sleeve.Spacing:0.#}\" c-c along the tub)" : ""), sure);
                    }
                case "SH":
                case "FD":
                    if (f.Drains.Count > 0) return (new List<XYZ> { Flat(f.Drains[0]) }, "the sanitary connector", true);
                    return code == "FD" ? (new List<XYZ> { Flat(f.Point) }, "the drain's insertion point", false)
                                        : (new List<XYZ> { centre }, "the shower's centre", false);
                case "WC":
                    {
                        if (f.Drains.Count > 0) return (new List<XYZ> { Flat(f.Drains.OrderBy(d => Dist(d.X, d.Y, centre)).First()) }, "the sanitary connector", true);
                        var back = Back();
                        if (back == null) return (null, "the toilet has no sanitary connector and no facing to find its back", false);
                        return f.WallHung
                            ? (new List<XYZ> { back - f.Facing * IntoWallFeet }, "the wall behind the wall-hung toilet (carrier)", false)
                            : (new List<XYZ> { back + f.Facing * ToiletFromWallFeet }, "1'-1\" from the back of the toilet (manual)", false);
                    }
                default:
                    {
                        // sinks, lavatories, washers: into the wall behind; a double sink's connectors share one sleeve
                        var back = Back();
                        if (f.Drains.Count > 0)
                        {
                            var p = Flat(Mean(f.Drains));
                            string how = f.Drains.Count > 1 ? "the sanitary connectors (one sleeve)" : "the sanitary connector";
                            // a connector inside the fixture (at its faucet) would put the sleeve on the sink: behind its back edge
                            double depth = back == null ? IntoWallFeet : (back - p).DotProduct(f.Facing);
                            if (depth < IntoWallFeet) { p -= f.Facing * (IntoWallFeet - depth); how += $", moved {IntoWallFeet * 12:0.#}\" behind the back of the fixture"; }
                            return (new List<XYZ> { p }, how, true);
                        }
                        if (back == null) return (null, "the fixture has no sanitary connector and no facing to find the wall behind it", false);
                        return (new List<XYZ> { back - f.Facing * IntoWallFeet }, "the wall behind the fixture, on its centre line", false);
                    }
            }
        }

        /// <summary>
        /// The model decides what the fixture is: a label that took a fixture of another kind (a KS label on a lavatory, BT on
        /// a shower) becomes that kind's sleeve (name, pipe and sleeve size), flagged. Kitchen and laundry sinks are one
        /// block in many sets, so there the plan's KS / LS is kept.
        /// </summary>
        private static void ModelKind(Crossing c, string code, PlumbingRules rules)
        {
            if (code == null || string.Equals(c.Tag, code, StringComparison.OrdinalIgnoreCase) || !rules.Fixtures.TryGetValue(code, out var fs)) return;
            var sinks = new[] { "KS", "LS" };
            if (sinks.Contains(c.Tag) && sinks.Contains(code)) return;
            string was = c.Tag;
            c.Tag = code; c.System = fs.System; c.Size = new DuctSize { Diameter = fs.Pipe };
            c.Check = true;
            c.Notes.Add($"the plan labels it {was}, the model has a {fs.Name} here: the model's {code} sleeve is used ({Units.FormatInches(rules.SleeveFor(fs.Pipe))}{(fs.Count > 1 ? $" x {fs.Count}" : "")}); check");
        }

        private static void SetPoints(Crossing c, List<XYZ> points)
        {
            var mid = Mean(points);
            c.X = mid.X; c.Y = mid.Y;
            c.Points.Clear();
            c.EachPoint = points.Count > 1;
            if (c.EachPoint) c.Points.AddRange(points.Select(p => new[] { p.X, p.Y }));
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
        /// Sinks, lavatories and washers drain into the wall behind them. A sleeve that lands on another sleeve there (a
        /// riser stack in that wall, a sleeve already in the model, the other sink of a double sink) is served by it:
        /// nothing of its own is placed. Returns how many.
        /// </summary>
        private static int Served(List<Crossing> fixtures, RiserAssembly assembly, PlumbingRules rules)
        {
            double Radius(Crossing o) => rules.SleeveFor(o.Size?.Diameter ?? 2) / 2 / 12;
            int n = 0;
            foreach (var c in fixtures.Where(c => IntoWall.Contains(c.Tag) && !c.EachPoint))
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
                    Detail = $"{c.Tag} is on the {FloorKey.Describe(c.Floor)} plan but not in the model: no sleeve, no Revit fixture within {MismatchRadiusFeet:0} ft " +
                             $"and no {c.Tag} in the architect's DWG within {MatchRadiusFeet:0} ft" +
                             (c.DrawnDrain != null ? $" (the plan draws it: {c.DrawnDrain})" : "") +
                             (floors > 0 ? $"; the same gap on {floors} other floor(s) at this spot" : "") +
                             ". Not placed (the model decides): add the fixture to the model or place the sleeve by hand."
                });
            }
            assembly.Crossings.RemoveAll(gaps.Contains);
        }

        /// <summary>Step 4: the sleeve point(s) from the fixture drawn on the plan (set by the plan reader), placed.</summary>
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

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static XYZ Mean(IList<XYZ> points) =>
            new XYZ(points.Average(p => p.X), points.Average(p => p.Y), 0);

        /// <summary>
        /// The fixture code a modelled family is (WC, LAV, BT...), the specific names first; a plain "sink" is a
        /// lavatory. Null when it is no fixture the rules sleeve.
        /// </summary>
        internal static string Classify(string name, PlumbingRules rules)
        {
            return rules.Fixtures.Keys.FirstOrDefault(code => Matches(code, name, false))
                ?? rules.Fixtures.Keys.FirstOrDefault(code => Matches(code, name, true));
        }

        /// <summary>A modelled family's name fits the fixture code; <paramref name="anySink"/> = a plain "sink" fits any sink code.</summary>
        private static bool Matches(string code, string name, bool anySink)
        {
            string pattern = code.ToUpperInvariant() switch
            {
                "WC" => @"toilet|water.?closet|\bwc\b",
                "LAV" => @"lavatory|\blav\b|vanity|basin",
                "BT" => @"bathtub|\btub\b",
                "KS" => @"kitchen.*sink|sink.*kitchen|\bks\b",
                "LS" => @"laundry.*sink|sink.*laundry|utility.*sink|\bls\b",
                "W/D" => @"washer|washing.?machine|\bw/d\b|\bwd\b",
                "WD" => @"washer|washing.?machine|\bwd\b",
                "SH" => @"shower",
                "FD" => @"floor.?drain|\bfd\b",
                _ => Regex.Escape(code)
            };
            if (anySink && (code == "LAV" || code == "KS" || code == "LS")) pattern += @"|\bsinks?\b";
            return Regex.IsMatch(name ?? "", pattern, RegexOptions.IgnoreCase);
        }

        private static List<Fixture> ReadFixtures(Document host, ExistingReport existing, PlumbingRules rules, List<string> messages)
        {
            // the floor of a fixture is the host level it stands on (by height): a linked architect's model names its
            // levels its own way ("Level 2", "L02"), which never matches "02.SECOND FLOOR"
            var levels = new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            string HostLevel(double z, string own) =>
                levels.LastOrDefault(l => l.ProjectElevation <= z + LevelToleranceFeet)?.Name ?? own;

            var sleeves = new HashSet<ElementId>(existing?.Items.Select(i => i.Id) ?? Enumerable.Empty<ElementId>());
            var result = new List<Fixture>();
            void Add(string source, List<Fixture> read)
            {
                result.AddRange(read);
                var known = read.Select(f => Classify(f.Name, rules)).Where(c => c != null).GroupBy(c => c).Select(g => $"{g.Count()} {g.Key}");
                var unknown = read.Where(f => Classify(f.Name, rules) == null).Select(f => f.Name.Trim()).Distinct().Take(15).ToList();
                App.Log($"AutoRun fixtures in {source}: {read.Count} read, {string.Join(", ", known)}" +
                        (unknown.Count > 0 ? $"; not fixtures by name: {string.Join(" | ", unknown)}" : ""));
                foreach (var g in read.Where(f => Classify(f.Name, rules) != null).GroupBy(f => f.Level ?? "?"))
                    App.Log($"AutoRun fixtures in {source} on {g.Key}: {g.Count()}");
            }
            Add("this model", Read(host, Transform.Identity, "this model", sleeves, HostLevel));
            int links = 0, unloaded = 0;
            foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = link.GetLinkDocument();
                if (linked == null) { unloaded++; App.Log($"AutoRun fixtures: link '{link.Name}' is not loaded"); continue; }
                links++;
                Add($"link '{link.Name}'", Read(linked, link.GetTotalTransform(), $"link '{link.Name}'", null, HostLevel));
            }
            int fixtures = result.Count(f => Classify(f.Name, rules) != null);
            messages.Add($"Fixtures found in the model: {fixtures} (this model and {links} Revit link(s)" + (unloaded > 0 ? $", {unloaded} link(s) not loaded" : "") + ")" +
                         (fixtures == 0 ? " — none: fixtures drawn only in linked DWGs are not read yet; see the log for the families seen" : "") + ".");
            return result;
        }

        /// <summary>A fixture this far below a level (feet) still stands on it (floor drains, tubs set into the slab).</summary>
        private const double LevelToleranceFeet = 1.0;

        private static List<Fixture> Read(Document doc, Transform transform, string source, HashSet<ElementId> sleeves, Func<double, string, string> hostLevel)
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
                var point = transform.OfPoint(local);
                var fixture = new Fixture
                {
                    Name = name,
                    Source = source,
                    Level = hostLevel(point.Z, RiserIndex.LevelOf(doc, instance)?.Name),
                    Point = point,
                    Min = point, Max = point,
                    WallHung = instance.Host is Wall || WallHung.IsMatch(name)
                };
                if (box != null)
                {
                    var corners = new[] { box.Min, new XYZ(box.Max.X, box.Min.Y, box.Min.Z), new XYZ(box.Min.X, box.Max.Y, box.Min.Z), box.Max }
                                  .Select(transform.OfPoint).ToList();
                    fixture.Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), 0);
                    fixture.Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), 0);
                }
                try
                {
                    var facing = transform.OfVector(instance.FacingOrientation);
                    facing = new XYZ(facing.X, facing.Y, 0);
                    if (facing.GetLength() > 1e-6) fixture.Facing = facing.Normalize();
                }
                catch (Exception) { }                                         // families without a facing
                var manager = instance.MEPModel?.ConnectorManager;
                if (manager != null)
                    foreach (Connector connector in manager.Connectors)
                    {
                        if (connector.Domain != Domain.DomainPiping || connector.ConnectorType != ConnectorType.End) continue;
                        bool sanitary = connector.PipeSystemType.ToString().IndexOf("Sanitary", StringComparison.OrdinalIgnoreCase) >= 0;
                        (sanitary ? fixture.Drains : fixture.Pipes).Add(transform.OfPoint(connector.Origin));
                    }
                result.Add(fixture);
            }
            return result;
        }
    }
}
