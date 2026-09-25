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
            using (var group = new TransactionGroup(_doc, "Sleeves & Openings: Auto Run - place openings"))
            {
                group.Start();
                foreach (var c in list)
                {
                    var outcome = new PlacementOutcome { Crossing = c };
                    outcomes.Add(outcome);
                    var level = levels.FirstOrDefault(l => l.Name == c.Level);
                    if (level == null) { outcome.Result = PlacementOutcome.Skipped; outcome.Detail = $"level '{c.Level}' not found"; continue; }

                    // one point per opening; a dryer shaft that passed the spacing check gets one sleeve per duct
                    var points = c.System == "DryerExhaust" && c.Points.Count > 1
                        ? c.Points.Select(p => new XYZ(p[0], p[1], 0)).ToList()
                        : new List<XYZ> { new XYZ(c.X, c.Y, 0) };
                    var spec0 = Spec(c, points.Count);
                    outcome.Size = spec0.SizeText;
                    var (symbol, map) = Family(spec0.Role);
                    if (symbol == null) { outcome.Result = PlacementOutcome.Skipped; outcome.Detail = $"no family loaded for {FamilyRole.Describe(spec0.Role)}"; continue; }

                    // already there?
                    var here = _existing.Items.Where(e => e.Level == c.Level && e.Point != null && Dist(e.Point, points[0]) <= Units.InchesToFeet(6) &&
                                                          (e.System == c.System || e.System == "?" || e.Source == "native opening")).ToList();
                    if (here.Count > 0)
                    {
                        outcome.Result = PlacementOutcome.Existing;
                        outcome.Ids.AddRange(here.Select(e => e.Id));
                        outcome.Detail = $"{here[0].Family} ({here[0].Source}) {Units.FormatInches(Dist(here[0].Point, points[0]) * 12)} away";
                        Drop(outcome, here, level);
                        if (_policy == ExistingPolicy.Update) Update(outcome, here, spec0, map);
                        continue;
                    }

                    // rules check at the spot (structure from links too)
                    var guard = Guard(level);
                    double hw = (spec0.Width ?? spec0.Diameter ?? 0) / 2, hl = (spec0.Length ?? spec0.Diameter ?? 0) / 2;
                    if (Math.Abs(Math.Sin(spec0.RotationRadians)) > 0.7) (hw, hl) = (hl, hw);
                    var warnings = points.SelectMany(p => guard.Check(p, hw, hl, spec0.System)).Distinct().ToList();
                    var hard = warnings.Where(PlacementGuard.IsHard).ToList();
                    if (hard.Count > 0)
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
                            foreach (var p in points)
                                outcome.Ids.Add(placer.Place(Spec(c, points.Count), symbol, map, level, p).Id);
                            if (t.Commit() == TransactionStatus.Committed) outcome.Result = PlacementOutcome.Placed;
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
        /// The opening placed for a crossing (inches, with clearances), across (Revit X) and along (Revit Y) once turned;
        /// null when no size is known. Used to compare with other sets before anything is placed.
        /// </summary>
        public static (double W, double L)? OpeningSize(RuleSet rules, Crossing c)
        {
            if (c.Size == null && c.System != "DryerExhaust") return null;
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
            if (c.System == "DryerExhaust")
            {
                spec = c.Roof
                    ? OpeningSpec.Rect(SystemKind.DryerExhaust, FamilyRole.RegularOpening, s.DryerExhaust.RoofOpeningWidth, s.DryerExhaust.RoofOpeningLength, _rules.Naming.DryerExhaust)
                    : OpeningSpec.Round(SystemKind.DryerExhaust, s.DryerExhaust.Diameter, _rules.Naming.DryerExhaust);
                return spec;
            }
            var kind = Enum.TryParse(c.System, out SystemKind k) ? k : SystemKind.Exhaust;
            double w, l;
            if (c.Size?.Diameter != null) w = l = c.Size.Diameter.Value;                      // round duct: square opening around it
            else { w = c.Size?.Width ?? 0; l = c.Size?.Length ?? 0; }
            var clear = kind == SystemKind.MotorizedDamper ? s.MotorizedDamper.ClearanceEachSide : s.Exhaust.ClearanceEachSide;
            string label = c.Tag == null ? c.System.ToUpperInvariant() : _rules.Naming.ExhaustPattern.Replace("{riser}", c.Tag);
            spec = OpeningSpec.Rect(kind, FamilyRole.RegularOpening, w + 2 * clear + roof, l + 2 * clear + roof, label);
            spec.Riser = c.Tag;
            spec.RotationRadians = c.Rotation ?? 0;
            return spec;
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
