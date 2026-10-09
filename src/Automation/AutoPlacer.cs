using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Placement;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>What happened to one opening from the drawings.</summary>
    public class PlacementOutcome
    {
        public const string Placed = "placed", Existing = "already in the model", Resized = "resized", Skipped = "skipped", Failed = "failed";
        public Crossing Crossing;
        public string Result;
        public string Detail;
        public string Size;                                   // opening size placed (with clearances)
        public List<ElementId> Ids = new List<ElementId>();
        public List<string> Warnings = new List<string>();     // soft rule warnings (placed anyway)
    }

    /// <summary>
    /// Phase 6: places the "place" crossings of a <see cref="RiserAssembly"/> with the add-in's own rules and families,
    /// no dialogs. Sizes: duct + clearance each side (rule 16), roof +4" total (rule 56), dryer 4" sleeve / 6"x6" at the
    /// roof (rule 78). Existing openings at the same spot are kept (or resized with the Update choice). A structural
    /// conflict (shear wall, beam) skips the opening; softer rule warnings are recorded. One undo step for the run.
    /// </summary>
    public class AutoPlacer
    {
        private readonly Document _doc;
        private readonly RuleSet _rules;
        private readonly ProjectState _state;
        private readonly ExistingReport _existing;
        private readonly string _policy;
        private readonly Dictionary<ElementId, PlacementGuard> _guards = new Dictionary<ElementId, PlacementGuard>();
        /// <summary>How far (inches) a sleeve already in the model can be from a stack pipe's spot and still be its sleeve.</summary>
        private const double PipeChaseInches = 18;

        public AutoPlacer(Document doc, RuleSet rules, ProjectState state, ExistingReport existing, string policy)
        {
            _doc = doc; _rules = rules; _state = state; _existing = existing; _policy = policy;
        }

        private readonly Dictionary<string, FamilyMapEntry> _maps = new Dictionary<string, FamilyMapEntry>();

        /// <summary>The family for a role: the project's mapping, else rules.json's (e.g. after it was loaded just now).</summary>
        private FamilyMapEntry MapFor(string role)
        {
            if (_maps.TryGetValue(role, out var m)) return m;
            m = FamilyMapping.Get(_rules, _state, role);
            if (FamilyMapping.FindSymbol(_doc, m) == null)
            {
                var fromRules = FamilyMapEntry.FromRule(_rules.Family(role));
                if (FamilyMapping.FindSymbol(_doc, fromRules) != null) m = fromRules;
            }
            return _maps[role] = m;
        }

        /// <summary>
        /// Loads the office families the crossings need from the add-in's Families folder (rules.json "file") when they
        /// are not in the project. Returns what was loaded. Outside any transaction.
        /// </summary>
        public List<string> LoadMissingFamilies(IEnumerable<Crossing> crossings)
        {
            var loaded = new List<string>();
            var roles = crossings.Select(c => Spec(c, 0).Role).Distinct().Where(r => FamilyMapping.FindSymbol(_doc, MapFor(r)) == null).ToList();
            if (roles.Count == 0) return loaded;
            using (var t = new Transaction(_doc, "Sleeves & Openings: load families"))
            {
                t.Start();
                foreach (var role in roles)
                {
                    var rule = _rules.Family(role);
                    if (rule == null || string.IsNullOrEmpty(rule.Family) || SleevesOpenings.Setup.ProjectSetup.FindFamily(_doc, rule.Family) != null) continue;
                    var path = Rules.RuleLoader.ResolveFamilyFile(rule);
                    if (path == null || !System.IO.File.Exists(path)) { App.Log($"AutoRun: no file to load for {rule.Family} ({path})"); continue; }
                    try { if (_doc.LoadFamily(path, out Family f)) loaded.Add(f.Name); }
                    catch (Exception ex) { App.Log($"AutoRun: loading {path} failed: {ex.Message}"); }
                }
                t.Commit();
            }
            _maps.Clear();
            return loaded;
        }

        /// <summary>Roles the crossings need that still have no loaded family.</summary>
        public List<string> MissingFamilies(IEnumerable<Crossing> crossings) =>
            crossings.Select(c => Spec(c, 0).Role).Distinct()
                     .Where(role => FamilyMapping.FindSymbol(_doc, MapFor(role)) == null).ToList();

        public List<PlacementOutcome> Run(IEnumerable<Crossing> crossings)
        {
            var outcomes = new List<PlacementOutcome>();
            var families = new Dictionary<string, (FamilySymbol Symbol, FamilyMapEntry Map)>();
            (FamilySymbol, FamilyMapEntry) Family(string role)
            {
                if (families.TryGetValue(role, out var f)) return f;
                var map = MapFor(role);
                var symbol = FamilyMapping.FindSymbol(_doc, map);
                if (symbol != null && (map.WidthParam == null || map.LengthParam == null || map.DiameterParam == null || map.NameParam == null))
                    FamilyMapping.GuessParams(_doc, symbol, map);      // may probe in a rolled-back transaction: before the group
                return families[role] = (symbol, map);
            }
            var list = crossings.ToList();
            foreach (var role in list.Select(c => Spec(c, 0).Role).Distinct()) Family(role);

            var levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            // sleeves a fixture already uses (matched before placing) serve no riser: a stack pipe must not take the same one
            var claimed = new HashSet<ElementId>(list.SelectMany(c => c.ExistingIds).Select(id => new ElementId(id)));
            // per pipe (S-3), its sleeve on the last floor done: the list goes up floor by floor
            var below = new Dictionary<string, (double X, double Y, XYZ At, bool Office)>();
            var pipeSystems = new HashSet<string>(_rules.Plumbing?.Services?.Values.Select(v => v.System) ?? Enumerable.Empty<string>());
            MoveStacks(list, levels);
            using (var group = new TransactionGroup(_doc, "Sleeves & Openings: Auto Run - place openings"))
            {
                group.Start();
                foreach (var c in list)
                {
                    _done.Add(c);
                    var outcome = new PlacementOutcome { Crossing = c };
                    outcomes.Add(outcome);
                    bool stackPipe = c.Tag != null && GroupOf(c) != null && !c.IsFixture;
                    double ox = c.X, oy = c.Y;
                    var under = stackPipe && below.TryGetValue(c.Tag, out var u) ? u : ((double X, double Y, XYZ At, bool Office)?)null;
                    if (stackPipe) below[c.Tag] = (ox, oy, null, false);
                    var level = levels.FirstOrDefault(l => l.Name == c.Level);
                    if (level == null) { outcome.Result = PlacementOutcome.Skipped; outcome.Detail = $"level '{c.Level}' not found"; continue; }

                    // one point per opening; dryer ducts that passed the spacing check get one sleeve each, a shaft one box
                    var points = (c.System == "DryerExhaust" && !c.Shaft || c.EachPoint) && c.Points.Count > 1
                        ? c.Points.Select(p => new XYZ(p[0], p[1], 0)).ToList()
                        : new List<XYZ> { new XYZ(c.X, c.Y, 0) };
                    var spec0 = Spec(c, points.Count);
                    outcome.Size = spec0.SizeText;
                    var (symbol, map) = Family(spec0.Role);
                    if (symbol == null) { outcome.Result = PlacementOutcome.Skipped; outcome.Detail = $"no family loaded for {FamilyRole.Describe(spec0.Role)}"; continue; }

                    // already there? (fixture sleeves: matched to the model's sleeves before placing, see FixtureSleeveLocator)
                    if (c.ExistingIds.Count > 0)
                    {
                        outcome.Result = PlacementOutcome.Existing;
                        outcome.Ids.AddRange(c.ExistingIds.Select(id => new ElementId(id)));
                        continue;                                  // what they are is already in the crossing's notes
                    }
                    var here =_existing.Items.Where(e => !claimed.Contains(e.Id) && SameFloor(e.Level, c.Level) && e.Point != null && Dist(e.Point, points[0]) <= Units.InchesToFeet(6) &&
                                                          (e.System == c.System || e.System == "?" || e.Source == "native opening")).ToList();
                    // a pipe of a stack: the office's sleeve for it may sit a little off the engineer's circle (the row spread
                    // differently, moved off a wall): the nearest unused sleeve of the same system in the chase serves it
                    if (here.Count == 0 && pipeSystems.Contains(c.System) && !c.IsFixture)
                        here = _existing.Items.Where(e => !claimed.Contains(e.Id) && SameFloor(e.Level, c.Level) && e.Point != null && e.System == c.System &&
                                                          Dist(e.Point, points[0]) <= Units.InchesToFeet(PipeChaseInches))
                                              .OrderBy(e => Dist(e.Point, points[0])).Take(1).ToList();
                    foreach (var e in here) claimed.Add(e.Id);
                    if (here.Count > 0)
                    {
                        if (stackPipe) below[c.Tag] = (ox, oy, here[0].Point, true);
                        outcome.Result = PlacementOutcome.Existing;
                        outcome.Ids.AddRange(here.Select(e => e.Id));
                        outcome.Detail = $"{here[0].Family} ({here[0].Source}) {Units.FormatInches(Dist(here[0].Point, points[0]) * 12)} away";
                        Drop(outcome, here, level);
                        if (_policy == ExistingPolicy.Update) Update(outcome, here, spec0, map);
                        continue;
                    }

                    // the floor below kept the office's own sleeve for this pipe, a little off the plans' spot, and the plans run the
                    // pipe straight up from there: this sleeve goes straight above that one (manual 22-23)
                    bool followed = false;
                    if (under?.Office == true && under.Value.At != null && points.Count == 1 &&
                        Math.Sqrt(Math.Pow(ox - under.Value.X, 2) + Math.Pow(oy - under.Value.Y, 2)) <= Units.InchesToFeet(1))
                    {
                        double off = Dist(under.Value.At, points[0]);
                        if (off > Units.InchesToFeet(1) && off <= Units.InchesToFeet(PipeChaseInches))
                        {
                            points[0] = new XYZ(under.Value.At.X, under.Value.At.Y, 0);
                            c.X = points[0].X; c.Y = points[0].Y;
                            c.Notes.Add($"placed straight above the sleeve already in the model for {c.Tag} on the floor below " +
                                        $"({Units.FormatInches(Math.Round(off * 12, 1))} from where the plans draw it; manual 22-23)");
                        }
                        followed = off <= Units.InchesToFeet(PipeChaseInches);
                    }

                    // rules check at the spot (structure from links too)
                    var guard = Guard(level);
                    double hw = (spec0.Width ?? spec0.Diameter ?? 0) / 2, hl = (spec0.Length ?? spec0.Diameter ?? 0) / 2;
                    if (Math.Abs(Math.Sin(spec0.RotationRadians)) > 0.7) (hw, hl) = (hl, hw);
                    var warnings = points.SelectMany(p => guard.Check(p, hw, hl, spec0.System)).Distinct().ToList();
                    // a fixture's sleeve stays where the model's fixture drains (the model decides): a column or structure there
                    // is flagged with the warning, never cleared by moving the sleeve off the fixture
                    if (c.IsFixture && warnings.Any(Avoidable))
                        c.Notes.Add("kept at the fixture (fixture sleeves are not moved to clear): " +
                                    string.Join("; ", warnings.Where(Avoidable).Select(PlacementGuard.Clean).Distinct()) + "; check");
                    else if (points.Count == 1 && (spec0.Role == FamilyRole.RegularOpening || spec0.Role == FamilyRole.RoundSleeve) && c.System != "GarbageChute" &&
                        _rules.Automation.Decisions?.AvoidStructure == true && warnings.Any(Avoidable))
                    {
                        var better = Avoid(c, spec0, points[0], guard, warnings, list);
                        if (better.HasValue)
                        {
                            points[0] = better.Value.Point;
                            warnings = better.Value.Warnings;
                            spec0 = Spec(c, 1);
                            outcome.Size = spec0.SizeText;
                        }
                    }
                    else if (points.Count == 1 && _rules.Automation.Decisions?.AvoidStructure == true && GroupOf(c) != null)
                    {
                        // this sleeve is clear but another of its pipe group still to place is not: the group moves now, together,
                        // before any of it is placed (moving the last one alone would push it into its neighbours)
                        var mateWarnings = MateWarnings(c, guard, list);
                        if (mateWarnings.Any(Avoidable))
                        {
                            var moved = AvoidAsGroup(c, spec0, guard, mateWarnings, list);
                            if (moved.HasValue) { points[0] = moved.Value.Point; warnings = moved.Value.Warnings; }
                        }
                    }
                    var hard = warnings.Where(PlacementGuard.IsHard).ToList();
                    if (hard.Count > 0 && !c.IsFixture)                 // a fixture's sleeve is placed all the same, the warning flags it
                    {
                        outcome.Result = PlacementOutcome.Skipped;
                        outcome.Detail = string.Join("; ", hard.Select(PlacementGuard.Clean));
                        continue;
                    }
                    outcome.Warnings.AddRange(warnings.Select(PlacementGuard.Clean));

                    using (var t = new Transaction(_doc, $"Auto Run: {c.Name} on {c.Level}"))
                    {
                        var options = t.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(new QuietWarnings());
                        options.SetClearAfterRollback(true);
                        t.SetFailureHandlingOptions(options);
                        t.Start();
                        try
                        {
                            var placer = new Placer(_doc, PlanOf(level));
                            // a fixture's sleeve(s) get an id of their own (not a floor-to-floor riser's), so Final Check has none to add
                            string fixtureId = c.IsFixture ? FixtureId(c.Tag) : null;
                            foreach (var p in points)
                            {
                                var spec = Spec(c, points.Count);
                                if (fixtureId != null && string.IsNullOrEmpty(spec.Riser)) spec.Riser = fixtureId;
                                outcome.Ids.Add(placer.Place(spec, symbol, map, level, p).Id);
                            }
                            if (t.Commit() == TransactionStatus.Committed)
                            {
                                outcome.Result = PlacementOutcome.Placed;
                                if (stackPipe) below[c.Tag] = (ox, oy, points[0], followed);     // the next floor keeps following the office's sleeve
                            }
                            else { outcome.Result = PlacementOutcome.Failed; outcome.Detail = "Revit did not accept it"; outcome.Ids.Clear(); }
                        }
                        catch (Exception ex)
                        {
                            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                            outcome.Result = PlacementOutcome.Failed; outcome.Detail = ex.Message; outcome.Ids.Clear();
                            App.Log($"AutoRun place {c.Floor} {c.Name}: {ex}");
                        }
                    }
                }
                if (outcomes.Any(o => o.Result == PlacementOutcome.Placed || o.Result == PlacementOutcome.Resized)) group.Assimilate();
                else group.RollBack();
            }
            return outcomes;
        }

        private OpeningSpec Spec(Crossing c, int ducts) => SpecFor(_rules, c);

        /// <summary>
        /// A fixture sleeve's id: "FX-WC-1"... unused in the model. Never a riser's id ("S-7"), so a riser of that name on
        /// another floor is not tracked through it.
        /// </summary>
        private string FixtureId(string tag)
        {
            var used = new HashSet<string>(SleevesOpenings.Risers.RiserIndex.AllOpenings(_doc).Select(o => o.Riser).Where(r => r != null), StringComparer.OrdinalIgnoreCase);
            string prefix = "FX-" + (tag ?? "FIX").Replace("/", "");
            for (int i = 1; ; i++)
                if (!used.Contains($"{prefix}-{i}")) return $"{prefix}-{i}";
        }

        /// <summary>Two level names of one floor ("06.6-TH FLOOR" and "06.6 TH FLOOR NEW"): the same floor name, or the same level.</summary>
        internal static bool SameFloor(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            (a != null && b != null && SleevesOpenings.Automation.Drawings.FloorKey.FromLevelName(a) is string ka && ka == SleevesOpenings.Automation.Drawings.FloorKey.FromLevelName(b));

        /// <summary>Warnings worth moving or reshaping an opening for: structure (rules 27-28), columns (rule 26), wall edges.</summary>
        private static bool Avoidable(string warning) =>
            PlacementGuard.IsHard(warning) || warning.Contains("(rule 26") || warning.Contains("edge of wall");

        /// <summary>
        /// An opening that hits structure, a column or a wall edge: first the same spot with the duct reshaped (same area,
        /// up to maxAspect:1, the engineer's general note), then moved shiftStep at a time up to maxShift (original shape,
        /// then the closest reshapes), never onto another opening of this run. The first candidate free of all of those wins;
        /// when none is and the spot is structurally impossible, the candidate with no structure and the fewest of the others.
        /// Updates the crossing (position, box, note) and returns the new point and its warnings; null = keep it as it is.
        /// </summary>
        private (XYZ Point, List<string> Warnings)? Avoid(Crossing c, OpeningSpec spec, XYZ at, PlacementGuard guard, List<string> now, List<Crossing> all)
        {
            var d = _rules.Automation.Decisions;
            // a pipe group's sleeves stay together: one move that clears every sleeve of the group still to place
            var asGroup = AvoidAsGroup(c, spec, guard, now, all);
            if (asGroup.HasValue) return asGroup;
            bool turned = Math.Abs(Math.Sin(spec.RotationRadians)) > 0.7;
            double w0 = spec.Width ?? spec.Diameter ?? 0, l0 = spec.Length ?? spec.Diameter ?? 0;
            if (turned) (w0, l0) = (l0, w0);

            // shapes: as drawn first, then the duct reshaped to the same area (rectangular ducts sized from a label only)
            var shapes = new List<(double W, double L, string Duct)> { (w0, l0, null) };
            if (c.Size?.Width != null && c.Size.Length != null && !c.BoxW.HasValue && !turned)
            {
                double dw0 = c.Size.Width.Value, dl0 = c.Size.Length.Value, area = dw0 * dl0;
                double extraW = w0 - dw0, extraL = l0 - dl0;          // clearances (+ roof increase)
                var reshaped = new List<(double W, double L, string Duct)>();
                for (double dw = 4; dw <= Math.Sqrt(area * d.MaxAspect) + 0.01; dw += 2)
                {
                    double dl = Math.Ceiling(area / dw / 2) * 2;           // whole even inches, at least the same area
                    if (Math.Max(dw, dl) / Math.Min(dw, dl) > d.MaxAspect + 1e-6 || (Math.Abs(dw - dw0) < 0.01 && Math.Abs(dl - dl0) < 0.01)) continue;
                    reshaped.Add((dw + extraW, dl + extraL, $"{dw:0}X{dl:0}"));
                }
                shapes.AddRange(reshaped.OrderBy(s => Math.Abs(s.W - w0) + Math.Abs(s.L - l0)));
            }

            var offsets = new List<(double X, double Y)> { (0, 0) };
            // the sleeves of one pipe group move together: the move a neighbour of this group already took is tried first
            var group = GroupOf(c);
            if (group != null && _groupShift.TryGetValue(group, out var taken)) offsets.Add(taken);
            for (double r = d.ShiftStep; r <= d.MaxShift + 1e-6 && d.ShiftStep > 0; r += d.ShiftStep)
                foreach (var (ux, uy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) })
                    offsets.Add((ux * r, uy * r));

            bool hardNow = now.Any(PlacementGuard.IsHard);
            (XYZ P, double W, double L, string Duct, (double X, double Y) Off, List<string> Warn)? fallback = null;
            for (int k = 0; k < offsets.Count; k++)
            {
                var off = offsets[k];
                // at the drawn spot every shape; moved, the drawn shape and the three closest reshapes
                foreach (var shape in k == 0 ? shapes.Skip(1) : shapes.Take(4))
                {
                    var p = new XYZ(at.X + off.X / 12, at.Y + off.Y / 12, at.Z);
                    if (OnAnother(c, p, shape.W, shape.L, all)) continue;
                    var warn = guard.Check(p, shape.W / 2, shape.L / 2, spec.System).Distinct().ToList();
                    int bad = warn.Count(Avoidable);
                    if (bad == 0) return Accept(c, p, shape.W, shape.L, shape.Duct, off, warn, now);
                    if (hardNow && !warn.Any(PlacementGuard.IsHard) && (fallback == null || bad < fallback.Value.Warn.Count(Avoidable)))
                        fallback = (p, shape.W, shape.L, shape.Duct, off, warn);
                }
            }
            if (fallback.HasValue)                                   // structurally impossible where drawn: the best free spot beats skipping it
                return Accept(c, fallback.Value.P, fallback.Value.W, fallback.Value.L, fallback.Value.Duct, fallback.Value.Off, fallback.Value.Warn, now);
            return null;
        }

        private (XYZ Point, List<string> Warnings) Accept(Crossing c, XYZ p, double w, double l, string duct, (double X, double Y) off, List<string> warn, List<string> now)
        {
            var cleared = now.Where(Avoidable).Where(x => !warn.Contains(x)).Select(PlacementGuard.Clean).ToList();
            var did = new List<string>();
            if (duct != null)
            {
                did.Add($"duct reshaped {c.Size} -> {duct} (same area; engineer's note: duct dimensions can be reconfigured up to {_rules.Automation.Decisions.MaxAspect:0}:1)");
                c.BoxW = w; c.BoxL = l; c.Rotation = 0;
            }
            if (off.X != 0 || off.Y != 0)
            {
                string dir = (off.Y > 0 ? "north" : off.Y < 0 ? "south" : "") + (off.X > 0 ? "east" : off.X < 0 ? "west" : "");
                did.Add($"moved {Math.Sqrt(off.X * off.X + off.Y * off.Y):0.#}\" {dir}");
                c.X = p.X; c.Y = p.Y;
                var group = GroupOf(c);
                if (group != null && !_groupShift.ContainsKey(group)) _groupShift[group] = off;
            }
            c.Notes.Add(string.Join(", ", did) + (cleared.Count > 0 ? " to clear: " + string.Join("; ", cleared) : ""));
            return (p, warn);
        }

        /// <summary>Crossings this run has already dealt with (placed, kept or skipped): they no longer move.</summary>
        private readonly HashSet<Crossing> _done = new HashSet<Crossing>();

        /// <summary>
        /// A plumbing sleeve whose group (S-3, V-3, ST-3... on one slab) still has sleeves to place: the smallest move (shiftStep
        /// rings up to maxShift) that leaves every one of them free of structure and, if possible, of columns and wall edges,
        /// applied to all of them. Null when the crossing has no such group or no move works.
        /// </summary>
        private (XYZ Point, List<string> Warnings)? AvoidAsGroup(Crossing c, OpeningSpec spec, PlacementGuard guard, List<string> now, List<Crossing> all)
        {
            var group = GroupOf(c);
            if (group == null) return null;
            var mates = all.Where(o => o != c && GroupOf(o) == group && o.MergedInto == null && o.ExistingIds.Count == 0 && o.HasPosition && !_done.Contains(o)).ToList();
            if (mates.Count == 0) return null;
            var members = new List<Crossing> { c }.Concat(mates).ToList();
            var moving = new HashSet<Crossing>(members);
            var d = _rules.Automation.Decisions;
            bool hardNow = now.Any(PlacementGuard.IsHard);
            (double X, double Y)? best = null; int bestBad = int.MaxValue;
            for (double r = d.ShiftStep; r <= d.MaxShift + 1e-6 && d.ShiftStep > 0 && bestBad > 0; r += d.ShiftStep)
                foreach (var (ux, uy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    double ox = ux * r, oy = uy * r;
                    int bad = 0; bool ok = true;
                    foreach (var m in members)
                    {
                        var s = OpeningSize(_rules, m);
                        if (!s.HasValue) continue;
                        var p = new XYZ(m.X + ox / 12, m.Y + oy / 12, 0);
                        if (OnAnother(m, p, s.Value.W, s.Value.L, all, moving)) { ok = false; break; }
                        var w = guard.Check(p, s.Value.W / 2, s.Value.L / 2, spec.System);
                        if (w.Any(PlacementGuard.IsHard)) { ok = false; break; }
                        bad += w.Count(Avoidable);
                    }
                    if (!ok || bad >= bestBad) continue;
                    best = (ox, oy); bestBad = bad;
                    if (bad == 0) break;
                }
            if (!best.HasValue || (bestBad > 0 && !hardNow)) return null;
            var off = best.Value;
            string dir = (off.Y > 0 ? "north" : off.Y < 0 ? "south" : "") + (off.X > 0 ? "east" : off.X < 0 ? "west" : "");
            double dist = Math.Sqrt(off.X * off.X + off.Y * off.Y);
            foreach (var m in mates)
            {
                m.X += off.X / 12; m.Y += off.Y / 12;
                m.Notes.Add($"moved {dist:0.#}\" {dir} with its pipe group (the group was in the way of: {string.Join("; ", now.Where(Avoidable).Select(PlacementGuard.Clean))})");
            }
            var pc = new XYZ(c.X + off.X / 12, c.Y + off.Y / 12, 0);
            var sc = OpeningSize(_rules, c) ?? (spec.Diameter ?? 0, spec.Diameter ?? 0);
            var warn = guard.Check(pc, sc.W / 2, sc.L / 2, spec.System).Distinct().ToList();
            var result = Accept(c, pc, sc.W, sc.L, null, off, warn, now);
            c.Notes[c.Notes.Count - 1] += " (the whole pipe group moved with it)";
            return result;
        }

        /// <summary>
        /// Before anything is placed: a pipe group that runs straight up several floors (each pipe within 1" floor to floor)
        /// and hits structure, a column or a wall edge on any of them moves as one stack, the same move on every floor, so the
        /// riser stays straight (manual 22-23). Only a move that clears every floor and is better than staying; otherwise each
        /// floor's group is moved on its own when it is placed (<see cref="AvoidAsGroup"/>) and Final Check reports the offset.
        /// </summary>
        private void MoveStacks(List<Crossing> all, List<Level> levels)
        {
            var d = _rules.Automation.Decisions;
            if (d?.AvoidStructure != true || d.ShiftStep <= 0) return;
            double tight = Units.InchesToFeet(1);
            var pipes = all.Where(c => GroupOf(c) != null && c.MergedInto == null && c.ExistingIds.Count == 0 && c.HasPosition &&
                                       !(c.EachPoint && c.Points.Count > 1) && levels.Any(l => l.Name == c.Level)).ToList();
            foreach (var g in pipes.GroupBy(c => c.Tag.Substring(c.Tag.IndexOf('-') + 1)))
            {
                var floors = g.GroupBy(c => c.Level).Select(f => f.ToList())
                              .OrderBy(f => levels.First(l => l.Name == f[0].Level).Elevation).ToList();
                var stacks = new List<List<List<Crossing>>>();
                foreach (var floor in floors)
                {
                    var below = stacks.LastOrDefault()?.Last();
                    bool straight = below != null && floor.Any(c => below.Any(o => o.Tag == c.Tag)) &&
                                    floor.All(c => below.All(o => o.Tag != c.Tag || Math.Sqrt(Math.Pow(o.X - c.X, 2) + Math.Pow(o.Y - c.Y, 2)) <= tight));
                    if (straight) stacks[stacks.Count - 1].Add(floor);
                    else stacks.Add(new List<List<Crossing>> { floor });
                }
                foreach (var stack in stacks.Where(s => s.Count > 1)) MoveStack(stack, levels, d, all);
            }
        }

        private void MoveStack(List<List<Crossing>> stack, List<Level> levels, DecisionRules d, List<Crossing> all)
        {
            var members = stack.SelectMany(f => f).ToList();
            var moving = new HashSet<Crossing>(members);
            var guards = stack.ToDictionary(f => f[0].Level, f => Guard(levels.First(l => l.Name == f[0].Level)));
            List<string> Warn(Crossing m, double ox, double oy)
            {
                var s = OpeningSize(_rules, m);
                return s.HasValue ? guards[m.Level].Check(new XYZ(m.X + ox / 12, m.Y + oy / 12, 0), s.Value.W / 2, s.Value.L / 2, Spec(m, 1).System) : new List<string>();
            }
            var now = members.SelectMany(m => Warn(m, 0, 0).Select(w => (Level: m.Level, W: w))).Distinct().ToList();
            int bad0 = now.Count(x => Avoidable(x.W));
            if (bad0 == 0) return;
            bool hardNow = now.Any(x => PlacementGuard.IsHard(x.W));

            (double X, double Y)? best = null; int bestBad = int.MaxValue;
            for (double r = d.ShiftStep; r <= d.MaxShift + 1e-6 && bestBad > 0; r += d.ShiftStep)
                foreach (var (ux, uy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    double ox = ux * r, oy = uy * r;
                    int bad = 0; bool ok = true;
                    foreach (var m in members)
                    {
                        var s = OpeningSize(_rules, m);
                        if (!s.HasValue) continue;
                        if (OnAnother(m, new XYZ(m.X + ox / 12, m.Y + oy / 12, 0), s.Value.W, s.Value.L, all, moving)) { ok = false; break; }
                        var w = Warn(m, ox, oy);
                        if (w.Any(PlacementGuard.IsHard)) { ok = false; break; }
                        bad += w.Count(Avoidable);
                        if (bad >= bestBad) { ok = false; break; }
                    }
                    if (!ok) continue;
                    best = (ox, oy); bestBad = bad;
                    if (bad == 0) break;
                }
            if (!best.HasValue || bestBad >= bad0 || (bestBad > 0 && !hardNow)) return;

            var off = best.Value;
            string dir = (off.Y > 0 ? "north" : off.Y < 0 ? "south" : "") + (off.X > 0 ? "east" : off.X < 0 ? "west" : "");
            string floorsText = $"{stack.First()[0].Level} to {stack.Last()[0].Level}";
            string why = string.Join("; ", now.Where(x => Avoidable(x.W)).Select(x => $"{PlacementGuard.Clean(x.W)} on {x.Level}").Distinct());
            foreach (var m in members)
            {
                m.X += off.X / 12; m.Y += off.Y / 12;
                m.Notes.Add($"moved {Math.Sqrt(off.X * off.X + off.Y * off.Y):0.#}\" {dir} with its whole stack ({floorsText}) so the riser stays straight " +
                            $"(manual 22-23; the stack was in the way of: {why})");
            }
        }

        /// <summary>The avoidable warnings (columns, wall edges, structure) of the sleeves of this crossing's group still to place.</summary>
        private List<string> MateWarnings(Crossing c, PlacementGuard guard, List<Crossing> all)
        {
            var group = GroupOf(c);
            var result = new List<string>();
            foreach (var m in all.Where(o => o != c && GroupOf(o) == group && o.MergedInto == null && o.ExistingIds.Count == 0 && o.HasPosition && !_done.Contains(o)))
            {
                var s = OpeningSize(_rules, m);
                if (s.HasValue) result.AddRange(guard.Check(new XYZ(m.X, m.Y, 0), s.Value.W / 2, s.Value.L / 2, Spec(m, 1).System));
            }
            return result.Distinct().ToList();
        }

        /// <summary>Moves taken by pipe groups on a level (level + group number, e.g. "06.6 TH FLOOR NEW|3"), inches.</summary>
        private readonly Dictionary<string, (double X, double Y)> _groupShift = new Dictionary<string, (double X, double Y)>();

        /// <summary>A plumbing / sprinkler sleeve's group on its level ("S-3" -> level|3); null for other openings.</summary>
        private string GroupOf(Crossing c)
        {
            bool pipe = (_rules.Plumbing?.IsPipeSystem(c.System) ?? false) || (_rules.Sprinkler?.IsPipeSystem(c.System) ?? false);
            int i = c.Tag?.IndexOf('-') ?? -1;
            return pipe && i >= 0 ? c.Level + "|" + c.Tag.Substring(i + 1) : null;
        }

        /// <summary>The proposed rectangle (inches, centre in feet) overlaps another opening of this run on the same level.</summary>
        private bool OnAnother(Crossing c, XYZ p, double w, double l, List<Crossing> all, HashSet<Crossing> except = null)
        {
            foreach (var o in all)
            {
                if (o == c || o.Level != c.Level || o.MergedInto != null || !o.HasPosition || (except != null && except.Contains(o))) continue;
                var s = OpeningSize(_rules, o);
                if (!s.HasValue) continue;
                // pipe sleeves keep the plumbing sleeve gap between them; touching is not clear
                double gap = GroupOf(c) != null && GroupOf(o) != null ? _rules.Plumbing?.SleeveGap ?? 0 : 0;
                if (Math.Abs(o.X - p.X) * 12 < (s.Value.W + w) / 2 + gap && Math.Abs(o.Y - p.Y) * 12 < (s.Value.L + l) / 2 + gap) return true;
            }
            return false;
        }

        /// <summary>
        /// The opening placed for a crossing (inches, with clearances), across (Revit X) and along (Revit Y) once turned;
        /// null when no size is known. Used to compare with other sets before anything is placed.
        /// </summary>
        public static (double W, double L)? OpeningSize(RuleSet rules, Crossing c)
        {
            if (c.Size == null && !c.SizedByRules) return null;
            var spec = SpecFor(rules, c);
            double w = spec.Width ?? spec.Diameter ?? 0, l = spec.Length ?? spec.Diameter ?? 0;
            return Math.Abs(Math.Sin(spec.RotationRadians)) > 0.7 ? (l, w) : (w, l);
        }

        /// <summary>The same opening a user would get from the Place buttons, sized from the drawings.</summary>
        internal static OpeningSpec SpecFor(RuleSet _rules, Crossing c)
        {
            var s = _rules.Systems;
            double roof = c.Roof ? s.Exhaust.RoofIncreaseTotal : 0;
            OpeningSpec spec;
            var pipes = _rules.Plumbing != null && _rules.Plumbing.IsPipeSystem(c.System) ? _rules.Plumbing
                      : _rules.Sprinkler != null && _rules.Sprinkler.IsPipeSystem(c.System) ? _rules.Sprinkler : null;
            if (pipes != null && c.Size?.Diameter != null)
            {
                // plumbing / sprinkler: a round MPI sleeve, pipe + 2" rounded up to the next sleeve size; named "S-P3", "SP-1"
                var pipeKind = Enum.TryParse(c.System, out SystemKind pk) ? pk : SystemKind.Sanitary;
                spec = OpeningSpec.Round(pipeKind, pipes.SleeveFor(c.Size.Diameter.Value), c.Tag);
                spec.Riser = c.IsFixture ? null : c.Tag;                // a fixture's sleeve is not a floor-to-floor riser
                return spec;
            }
            if (c.BoxW.HasValue && c.BoxL.HasValue)
            {
                // a box sized by the layout (shaft, combined or reshaped opening): exactly that size, not turned
                var boxKind = Enum.TryParse(c.System, out SystemKind bk) ? bk : SystemKind.Exhaust;
                string boxLabel = c.Label ?? (c.Tag == null ? c.System.ToUpperInvariant() : _rules.Naming.ExhaustPattern.Replace("{riser}", c.Tag));
                if (c.Label == null && c.Dampers.Count > 0) boxLabel = _rules.Naming.DamperPattern.Replace("{riser}", boxLabel).Replace("{damper}", string.Join("-", c.Dampers));
                spec = OpeningSpec.Rect(boxKind, FamilyRole.RegularOpening, c.BoxW.Value, c.BoxL.Value, boxLabel);
                spec.Riser = c.Tag;
                return spec;
            }
            if (c.System == "DryerExhaust" && c.Shaft && c.Points.Count > 1 && !c.Roof)
            {
                // dryer shaft (rule 74): one Regular Opening around every duct, clearance each side as for exhaust ducts
                var box = ShaftBox(c, s.DryerExhaust.Diameter + 2 * s.Exhaust.ClearanceEachSide);
                return OpeningSpec.Rect(SystemKind.DryerExhaust, FamilyRole.RegularOpening, box.W, box.L, _rules.Naming.DryerExhaust);
            }
            if (c.System == "DryerExhaust")
            {
                spec = c.Roof
                    ? OpeningSpec.Rect(SystemKind.DryerExhaust, FamilyRole.RegularOpening, s.DryerExhaust.RoofOpeningWidth, s.DryerExhaust.RoofOpeningLength, _rules.Naming.DryerExhaust)
                    : OpeningSpec.Round(SystemKind.DryerExhaust, s.DryerExhaust.Diameter, _rules.Naming.DryerExhaust);
                return spec;
            }
            if (c.System == "GarbageChute")      // rules 34-44: always the fixed size, whatever the drawing says; same as the Place button
            {
                double extra = c.Roof ? s.GarbageChute.RoofClearance : 0;
                spec = OpeningSpec.Rect(SystemKind.GarbageChute, FamilyRole.RegularOpening, s.GarbageChute.FixedWidth + extra, s.GarbageChute.FixedLength + extra, "GARBAGE CHUTE");
                spec.Riser = c.Tag;
                return spec;
            }
            var kind =Enum.TryParse(c.System, out SystemKind k) ? k : SystemKind.Exhaust;
            double w, l;
            if (c.Size?.Diameter != null) w = l = c.Size.Diameter.Value;                      // round duct: square opening around it
            else { w = c.Size?.Width ?? 0; l = c.Size?.Length ?? 0; }
            var clear = kind == SystemKind.MotorizedDamper ? s.MotorizedDamper.ClearanceEachSide : s.Exhaust.ClearanceEachSide;
            string label = c.Label ?? (c.Tag == null ? c.System.ToUpperInvariant() : _rules.Naming.ExhaustPattern.Replace("{riser}", c.Tag));
            if (c.Label == null && c.Dampers.Count > 0) label = _rules.Naming.DamperPattern.Replace("{riser}", label).Replace("{damper}", string.Join("-", c.Dampers));
            spec = OpeningSpec.Rect(kind, FamilyRole.RegularOpening, w + 2 * clear + roof, l + 2 * clear + roof, label);
            spec.Riser = c.Tag;
            spec.RotationRadians = c.Rotation ?? 0;
            return spec;
        }

        /// <summary>
        /// A dryer shaft's opening (inches across X, along Y): the ducts' centres plus <paramref name="extra"/> (a duct and
        /// its clearances), rounded up to whole even inches like the office's opening sizes.
        /// </summary>
        internal static (double W, double L) ShaftBox(Crossing c, double extra)
        {
            double Even(double v) => Math.Ceiling(Math.Round(v, 2) / 2) * 2;
            return (Even((c.Points.Max(p => p[0]) - c.Points.Min(p => p[0])) * 12 + extra),
                    Even((c.Points.Max(p => p[1]) - c.Points.Min(p => p[1])) * 12 + extra));
        }

        /// <summary>Update choice: an add-in opening whose size differs from the drawings is resized (never moved or deleted).</summary>
        private void Update(PlacementOutcome outcome, List<ExistingItem> here, OpeningSpec spec, FamilyMapEntry map)
        {
            var inst = here.Select(e => _doc.GetElement(e.Id) as FamilyInstance).FirstOrDefault(i => i != null && OpeningData.Read(i) != null);
            var data = inst == null ? null : OpeningData.Read(inst);
            if (data == null) { outcome.Detail += "; not an add-in opening, size not checked"; return; }
            bool same = spec.IsRound ? data.Diameter == spec.Diameter
                                     : (data.Width == spec.Width && data.Length == spec.Length) || (data.Width == spec.Length && data.Length == spec.Width);
            if (same) return;
            using (var t = new Transaction(_doc, "Auto Run: resize " + data.Label))
            {
                t.Start();
                try
                {
                    new Placer(_doc, null).Resize(inst, map, data, spec.Width, spec.Length, spec.Diameter);
                    t.Commit();
                    outcome.Result = PlacementOutcome.Resized;
                    outcome.Detail += $"; resized to {spec.SizeText}";
                }
                catch (Exception ex) { t.RollBack(); outcome.Detail += "; resize failed: " + ex.Message; }
            }
        }

        /// <summary>
        /// Add-in openings placed by an earlier version sat at the level's Elevation (measured from the base point the level
        /// type uses) instead of its model height: move them down onto the level. Hand-placed elements are left alone.
        /// </summary>
        private void Drop(PlacementOutcome outcome, List<ExistingItem> here, Level level)
        {
            foreach (var item in here)
            {
                if (!(_doc.GetElement(item.Id) is FamilyInstance inst) || OpeningData.Read(inst) == null || !(inst.Location is LocationPoint lp)) continue;
                double dz = level.ProjectElevation - lp.Point.Z;
                if (Math.Abs(dz) < 0.5) continue;
                using (var t = new Transaction(_doc, "Auto Run: move opening onto its level"))
                {
                    t.Start();
                    try
                    {
                        ElementTransformUtils.MoveElement(_doc, inst.Id, new XYZ(0, 0, dz));
                        t.Commit();
                        outcome.Result = PlacementOutcome.Resized;
                        outcome.Detail += $"; moved {Units.FormatInches(Math.Abs(dz) * 12)} {(dz < 0 ? "down" : "up")} onto the level";
                    }
                    catch (Exception ex) { t.RollBack(); outcome.Detail += "; could not move onto the level: " + ex.Message; }
                }
            }
        }

        private PlacementGuard Guard(Level level)
        {
            if (!_guards.TryGetValue(level.Id, out var g)) _guards[level.Id] = g = new PlacementGuard(_doc, _rules, level);
            return g;
        }

        private ViewPlan PlanOf(Level level) =>
            new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.GenLevel?.Id == level.Id);

        private static double Dist(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>No warning dialogs during an automatic run: warnings are dismissed, errors roll the opening back.</summary>
        private class QuietWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                    else return FailureProcessingResult.ProceedWithRollBack;
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
