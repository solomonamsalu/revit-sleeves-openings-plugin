using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Electrical
{
    /// <summary>One straight run of the riser the search found: floors <see cref="From"/>..<see cref="To"/> (indexes into the riser levels) at one spot.</summary>
    public class RiserRun
    {
        public int From, To;
        public XYZ Point;                               // null = no allowed spot: the drafter clicks these floors
        public double Distance;                         // feet from where the search started (the room, or the run below)
        public List<string> Rooms = new List<string>(); // the room the spot is in, per floor ("?" = no name in the drawing)
        public string Why;                              // why nothing was found, when Point is null
        public bool Unnamed;                            // no named closet or shaft would do: the spot is in rooms the drawing does not name
    }

    /// <summary>
    /// Finds where the electrical riser goes, by the manual's rules alone: as close to the electrical room as a clear path
    /// allows (rule 3), straight up as far as it can (rules 10-11), and where that spot stops being allowed, an offset to the
    /// nearest spot that is (rules 12-15). A spot is allowed on a floor when it is against a wall but not on its edge and not
    /// mid-room (general rule, 20-21), clear of columns (26), shear walls (27), beams (28) and other openings, and not in a
    /// living space; a closet, shaft or chase named in the architect's drawing is preferred (a room a typical floor's sheet
    /// leaves unnamed takes the name the same room carries on another floor). The walls, doors and windows are the
    /// architect's DWG as Revit draws it; the room a spot is in is the area they close around it. With nothing allowed
    /// within electrical.searchRadius the search widens until it covers the floor. The best spot is then checked once
    /// more with the same <see cref="PlacementGuard"/> the click-placed openings use.
    /// </summary>
    public class ElectricalLocator
    {
        private readonly Document _doc;
        private readonly RuleSet _rules;
        private readonly ElectricalRules _e;
        private readonly List<FloorData> _floors;
        private readonly Action<string> _log;

        private class Seg { public double X0, Y0, X1, Y1; }
        private class WallData { public List<Seg> Segs = new List<Seg>(); public double Half; }
        private class Box { public double X0, Y0, X1, Y1; public string Label; }

        /// <summary>Everything the search needs about one riser level, read once.</summary>
        private class FloorData
        {
            public Level Level;
            public WallGeometry WallGeometry;
            public List<WallData> Walls = new List<WallData>();
            public List<Box> Columns = new List<Box>(), Beams = new List<Box>(), Openings = new List<Box>(), Doors = new List<Box>();
            public List<SpaceLabel> Spaces = new List<SpaceLabel>();
            /// <summary>Door leaves and swings: a room ends at its door, so a room name past one is another room.</summary>
            public List<Seg> DoorLines = new List<Seg>();
            /// <summary>Window lines: a room does not end at the gap its window leaves in the wall.</summary>
            public List<Seg> Windows = new List<Seg>();
            public PlacementGuard Guard;
            /// <summary>The floor cut into rooms by its walls, doors and windows (made when first asked for).</summary>
            public RoomGrid Rooms;
            /// <summary>Unnamed rooms -> the name the same room carries on another floor (null = none).</summary>
            public Dictionary<RoomGrid.Room, SpaceLabel> Borrowed = new Dictionary<RoomGrid.Room, SpaceLabel>();
        }

        private readonly LinkedModels _links;
        private readonly List<(Element Element, BoundingBoxXYZ Box, LinkedModels.Source Source)> _columns, _beams;
        private readonly List<OpeningRecord> _openings;
        /// <summary>Openings the add-in's own list misses: in linked models (the HV model's duct openings), placed by hand, or Revit's floor / shaft openings.</summary>
        private readonly List<(BoundingBoxXYZ Box, string Label, bool Linked)> _others;
        private readonly ElectricalSurvey _survey;

        private static readonly System.Text.RegularExpressions.Regex OpeningFamily =
            new System.Text.RegularExpressions.Regex("sleeve|opening", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <param name="levels">The riser levels, ascending.</param>
        /// <param name="replacing">Openings about to be deleted (the riser being replaced): not in the way.</param>
        public ElectricalLocator(Document doc, RuleSet rules, IList<Level> levels, ElectricalSurvey survey, ICollection<ElementId> replacing, Action<string> log)
        {
            _doc = doc; _rules = rules; _e = rules.Systems.Electrical; _log = log ?? (_ => { }); _survey = survey;
            _links = new LinkedModels(doc, rules);
            _columns = _links.Collect(BuiltInCategory.OST_StructuralColumns).Concat(_links.Collect(BuiltInCategory.OST_Columns)).ToList();
            _beams = _links.Collect(BuiltInCategory.OST_StructuralFraming).ToList();
            _openings = RiserIndex.AllOpenings(doc).Where(o => !replacing.Contains(o.Instance.Id)).ToList();
            _others = OtherOpenings(replacing).ToList();
            _floors = levels.Select(Load).ToList();
        }

        /// <summary>
        /// Every opening the riser must keep clear of (electrical.openingClearance) that <see cref="RiserIndex.AllOpenings"/>
        /// does not give: in each linked model, the add-in's stamped openings and sleeves; in every model, a family named
        /// sleeve or opening not stamped (placed by hand), and Revit's own floor and shaft openings. Doors, windows and
        /// openings cut in walls are not slab openings. The riser being replaced is left out.
        /// </summary>
        private IEnumerable<(BoundingBoxXYZ Box, string Label, bool Linked)> OtherOpenings(ICollection<ElementId> replacing)
        {
            foreach (var s in _links.Sources)
            {
                foreach (var fi in new FilteredElementCollector(s.Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    if (s.IsHost && replacing.Contains(fi.Id)) continue;
                    var cat = fi.Category?.BuiltInCategory;
                    if (cat == BuiltInCategory.OST_Doors || cat == BuiltInCategory.OST_Windows) continue;
                    bool stamped = OpeningData.Read(fi) != null;
                    if (s.IsHost && stamped) continue;                     // already in the add-in's own list
                    if (!stamped && !OpeningFamily.IsMatch(fi.Symbol?.Family?.Name ?? "")) continue;
                    var bb = fi.get_BoundingBox(null);
                    if (bb == null) continue;
                    yield return (LinkedModels.TransformBox(bb, s.Transform), $"{s.Describe(fi.Id)} '{fi.Symbol?.Family?.Name}'", !s.IsHost);
                }
                foreach (var op in new FilteredElementCollector(s.Doc).OfClass(typeof(Opening)).Cast<Opening>())
                {
                    if (op.Host is Wall) continue;
                    var bb = op.get_BoundingBox(null);
                    if (bb == null) continue;
                    yield return (LinkedModels.TransformBox(bb, s.Transform), $"{s.Describe(op.Id)} '{op.Category?.Name ?? "Opening"}'", !s.IsHost);
                }
            }
        }

        /// <summary>
        /// One level's walls (the Revit walls, and the architect's wall lines: on many projects the partitions are only in the
        /// drawing), doors, structure, other openings and room names.
        /// </summary>
        private FloorData Load(Level level)
        {
            double z = level.ProjectElevation;
            var f = new FloorData { Level = level, WallGeometry = new WallGeometry(_doc, level, _links) };
            foreach (var w in f.WallGeometry.Walls)
            {
                var wall = new WallData { Half = w.HalfWidth };
                var pts = w.Centerline.Tessellate();
                for (int i = 1; i < pts.Count; i++) wall.Segs.Add(new Seg { X0 = pts[i - 1].X, Y0 = pts[i - 1].Y, X1 = pts[i].X, Y1 = pts[i].Y });
                f.Walls.Add(wall);
            }
            int revitWalls = f.Walls.Count;
            var read = _survey?.For(level);
            Seg ToSeg((double X0, double Y0, double X1, double Y1) s) => new Seg { X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1 };
            Box ToDoor((double X0, double Y0, double X1, double Y1) d) => new Box { X0 = d.X0, Y0 = d.Y0, X1 = d.X1, Y1 = d.Y1, Label = "door" };
            // the architect's DWG as Revit draws it: AEC walls and doors (AutoCAD Architecture) come as lines there, while the
            // file read sees none of them; the file's own lines only when the level has no DWG in the model
            var drawn = LinkedPlanReader.Read(_doc, level, _rules.DwgProfile ?? new Automation.Drawings.DwgProfile(), read?.Floor);
            string from;
            if (drawn.Walls.Count > 0)
            {
                from = string.Join(", ", drawn.Sources) + " (in the model)" +
                       (drawn.Skipped.Count > 0 ? $"; not read, another floor's plan: {string.Join(", ", drawn.Skipped)}" : "");
                // a drawn wall is its two face lines: each a wall of no thickness
                foreach (var s in drawn.Walls) f.Walls.Add(new WallData { Segs = { ToSeg(s) } });
                f.DoorLines = drawn.DoorLines.Select(ToSeg).ToList();
                f.Windows = drawn.Windows.Select(ToSeg).ToList();
                f.Doors = drawn.Swings.Select(sw => ToDoor(ElectricalReader.Doorway(sw.Hinge, sw.A, sw.B, drawn.DoorLines, 2.0 / 12))).ToList();
            }
            else if (read != null)
            {
                from = read.File;
                foreach (var s in read.Walls) f.Walls.Add(new WallData { Segs = { ToSeg(s) } });
                f.DoorLines = read.DoorLines.Select(ToSeg).ToList();
                f.Doors = read.Doors.Select(ToDoor).ToList();
            }
            else from = "no drawing";
            if (read != null) f.Spaces = read.Spaces;
            f.Columns = _columns.Where(c => c.Box.Min.Z - 1 <= z && c.Box.Max.Z + 1 >= z).Select(c => ToBox(c.Box, c.Source.Describe(c.Element.Id))).ToList();
            f.Beams = _beams.Where(c => c.Box.Min.Z - 1 <= z && c.Box.Max.Z + 1 >= z).Select(c => ToBox(c.Box, c.Source.Describe(c.Element.Id))).ToList();
            f.Openings = _openings.Where(o => o.Level.Id == level.Id).Select(Footprint).ToList();
            var others = _others.Where(o => o.Box.Min.Z - 1 <= z && o.Box.Max.Z + 1 >= z).ToList();
            f.Openings.AddRange(others.Select(o => ToBox(o.Box, o.Label)));
            f.Guard = new PlacementGuard(_doc, _rules, level, f.WallGeometry);
            _log($"SEARCH level '{level.Name}': {revitWalls} Revit wall(s) + {f.Walls.Count - revitWalls} drawn wall line(s) from {from}, " +
                 $"{f.DoorLines.Count} door line(s), {f.Doors.Count} door swing(s), {f.Windows.Count} window line(s), slab outline {(f.Guard.HasSlab ? "yes" : "NONE")}, {f.Columns.Count} column(s),{f.Beams.Count} beam(s), {f.Openings.Count} other opening(s) ({others.Count(o => o.Linked)} in linked models, {others.Count(o => !o.Linked)} not the add-in's), " +
                 $"{f.Spaces.Count(s => s.RiserSpace)} riser space name(s), {f.Spaces.Count(s => !s.RiserSpace)} living space name(s)");
            return f;
        }

        /// <summary>
        /// The riser as straight runs from the bottom riser level up. <paramref name="sizeAt"/> gives the opening (inches) of a
        /// run that starts at a floor index, since an offset reduces the circles (rule 14).
        /// </summary>
        public List<RiserRun> Plan(XYZ start, Func<int, (double Width, double Length)> sizeAt)
        {
            var runs = new List<RiserRun>();
            var anchor = start;
            int from = 0;
            while (from < _floors.Count)
            {
                var (w, l) = sizeAt(from);
                var run = Best(from, anchor, w, l);
                runs.Add(run);
                if (run.Point == null)
                {
                    // nothing allowed from this floor up: the drafter clicks the rest as one run
                    run.To = _floors.Count - 1;
                    break;
                }
                anchor = run.Point;
                from = run.To + 1;
            }
            ExplainOffsets(runs, sizeAt);
            return runs;
        }

        /// <summary>Only spots in a room the drawing names count (set while searching; off for the fallback and for a clicked spot).</summary>
        private bool _named;

        private RiserRun Best(int from, XYZ anchor, double widthIn, double lengthIn)
        {
            _named = true;
            try
            {
                var run = Widening(from, anchor, widthIn, lengthIn);
                if (run.Point != null) return run;
                _log($"SEARCH   nothing in a named closet or shaft from '{_floors[from].Level.Name}': trying spots the drawing names no room for");
                _named = false;
                run = Widening(from, anchor, widthIn, lengthIn);
                if (run.Point != null) run.Unnamed = true;
                return run;
            }
            finally { _named = false; }
        }

        /// <summary>
        /// The search within the rules' radius, and when nothing there is allowed, again at twice the radius, and twice
        /// that, until it reaches past the floor's drawing: a riser far from the room beats none at all.
        /// </summary>
        private RiserRun Widening(int from, XYZ anchor, double widthIn, double lengthIn)
        {
            double radius = Units.InchesToFeet(_e.SearchRadius), reach = Reach(_floors[from], anchor);
            while (true)
            {
                var run = Search(from, anchor, widthIn, lengthIn, radius);
                if (run.Point != null || radius >= reach) return run;
                double wider = Math.Min(radius * 2, reach);
                _log($"SEARCH   nothing within {Units.FormatInches(radius * 12)} of ({anchor.X:0.##}, {anchor.Y:0.##}): widening to {Units.FormatInches(wider * 12)}");
                radius = wider;
            }
        }

        /// <summary>The farthest the floor's walls go from the point (feet): past that a wider search finds nothing new.</summary>
        private static double Reach(FloorData f, XYZ a)
        {
            double far = 0;
            foreach (var w in f.Walls)
                foreach (var s in w.Segs)
                    far = Math.Max(far, Math.Max(Math.Sqrt((s.X0 - a.X) * (s.X0 - a.X) + (s.Y0 - a.Y) * (s.Y0 - a.Y)),
                                                 Math.Sqrt((s.X1 - a.X) * (s.X1 - a.X) + (s.Y1 - a.Y) * (s.Y1 - a.Y))));
            return far;
        }

        private RiserRun Search(int from, XYZ anchor, double widthIn, double lengthIn, double radius)
        {
            double hw = Units.InchesToFeet(widthIn) / 2, hl = Units.InchesToFeet(lengthIn) / 2, r = Math.Max(hw, hl);
            double step = Math.Max(Units.InchesToFeet(_e.SearchStep), 1.0 / 12);
            var bottom = _floors[from];
            if (bottom.Walls.Count == 0)
                return new RiserRun { From = from, To = from, Why = $"no walls on {bottom.Level.Name} to place it against (is the architectural model linked?)" };

            // every spot within the radius, scored by how many floors it runs straight up, then how many of those are a
            // named riser space (closet, shaft…), then how close it is to where the search started
            var scored = new List<(XYZ P, int Floors, int Named, double Dist, List<string> Rooms)>();
            int n = (int)Math.Ceiling(radius / step), tested = 0, firstFloorOk = 0;
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    double x = anchor.X + i * step, y = anchor.Y + j * step, d = Math.Sqrt((x - anchor.X) * (x - anchor.X) + (y - anchor.Y) * (y - anchor.Y));
                    if (d > radius) continue;
                    tested++;
                    int floors = 0, named = 0;
                    var rooms = new List<string>();
                    for (int k = from; k < _floors.Count; k++)
                    {
                        int g = Grade(_floors[k], x, y, hw, hl, r, out string room);
                        if (g == 0) break;
                        floors++; if (g == 2) named++; rooms.Add(room);
                    }
                    if (floors == 0) continue;
                    firstFloorOk++;
                    scored.Add((new XYZ(x, y, 0), floors, named, d, rooms));
                }
            _log($"SEARCH floors from '{bottom.Level.Name}', opening {Units.FormatInches(widthIn)} x {Units.FormatInches(lengthIn)}, around ({anchor.X:0.##}, {anchor.Y:0.##}): " +
                 $"{tested} spot(s) tested, {firstFloorOk} allowed on '{bottom.Level.Name}'");
            if (scored.Count == 0)
                return new RiserRun { From = from, To = from, Why = $"no allowed spot within {Units.FormatInches(radius * 12)} on {bottom.Level.Name}" };

            var ranked = scored.OrderByDescending(s => s.Floors).ThenByDescending(s => s.Named).ThenBy(s => s.Dist).ToList();
            foreach (var s in ranked.Take(5))
                _log($"SEARCH   candidate ({s.P.X:0.##}, {s.P.Y:0.##}) {s.Dist:0.#} ft away: straight up {s.Floors} floor(s), {s.Named} in a named riser space; rooms [{string.Join(" | ", s.Rooms)}]");

            // the fast checks above approximate the guard; the spot that goes out is the first the guard itself also clears
            int rejected = 0;
            foreach (var s in ranked.Take(500))
            {
                var problems = new List<string>();
                for (int k = from; k < from + s.Floors; k++)
                    foreach (var w in _floors[k].Guard.Check(s.P, widthIn / 2, lengthIn / 2, SystemKind.Electrical))
                        if (!w.Contains("rules 20-21")) problems.Add(_floors[k].Level.Name + ": " + PlacementGuard.Clean(w));
                if (problems.Count > 0)
                {
                    if (rejected++ < 10) _log($"SEARCH   ({s.P.X:0.##}, {s.P.Y:0.##}) rejected by the placement check: {string.Join("; ", problems.Take(3))}");
                    continue;
                }
                if (rejected > 0) _log($"SEARCH   {rejected} better-ranked spot(s) were rejected by the placement check first");
                _log($"SEARCH   CHOSEN ({s.P.X:0.##}, {s.P.Y:0.##}) for {bottom.Level.Name} – {_floors[from + s.Floors - 1].Level.Name}");
                return new RiserRun { From = from, To = from + s.Floors - 1, Point = s.P, Distance = s.Dist, Rooms = s.Rooms };
            }
            return new RiserRun { From = from, To = from, Why = $"every allowed spot near {bottom.Level.Name} failed the placement check" };
        }

        /// <summary>
        /// 0 = not allowed on this floor; 1 = allowed, in a room the drawing does not name; 2 = allowed, in a named riser space.
        /// </summary>
        private int Grade(FloorData f, double x, double y, double hw, double hl, double r, out string room) => Grade(f, x, y, hw, hl, r, out room, out _);

        /// <param name="why">Set to the first check the spot fails (for the log); null when it passes.</param>
        private int Grade(FloorData f, double x, double y, double hw, double hl, double r, out string room, out string why)
        {
            room = null; why = null;
            double minCol = Units.InchesToFeet(_rules.Clearances.MinFromColumn), clear = Units.InchesToFeet(_e.OpeningClearance);
            string inches(double ft) => Units.FormatInches(Math.Max(0, Units.FeetToInches(ft)));

            // through the slab, never beyond its edge (general rule): outside the building nothing is in the way, so
            // without this the "best" spot is out past the exterior wall
            if (!f.Guard.WithinSlab(new XYZ(x, y, 0), hw, hl)) { why = "off the slab"; return 0; }
            foreach (var c in f.Columns)                                                                       // rule 26
                if (Distance(c, x, y) - r < minCol) { why = $"{inches(Distance(c, x, y) - r)} from column {c.Label} (rule 26)"; return 0; }
            foreach (var b in f.Beams)                                                                         // rule 28
                if (Overlaps(b, x, y, hw, hl, 0)) { why = $"in beam {b.Label} (rule 28)"; return 0; }
            foreach (var o in f.Openings)                                                                      // other openings
                if (Overlaps(o, x, y, hw, hl, clear)) { why = $"too close to opening {o.Label}"; return 0; }
            if (_e.AvoidDoorways)
                foreach (var o in f.Doors)                                                                     // door swings, and just inside doorways
                    if (Overlaps(o, x, y, hw, hl, clear))
                    { why = $"in a doorway / door swing ({o.X0:0.#},{o.Y0:0.#})-({o.X1:0.#},{o.Y1:0.#})"; return 0; }

            double face = double.MaxValue;
            foreach (var w in f.Walls)
            {
                double d = double.MaxValue;
                foreach (var s in w.Segs) d = Math.Min(d, SegDistance(s, x, y));
                if (d - r < w.Half)                             // inside a wall, on its edge (general rule), or in a shear wall (27)
                { why = w.Half > 0 ? "on a Revit wall" : $"a drawn wall line crosses the opening ({inches(d)} from its centre)"; return 0; }
                face = Math.Min(face, d - w.Half);
            }
            double gap = face - r;                              // from the opening's side to the nearest wall face
            if (gap < Units.InchesToFeet(_e.WallGapMin) || gap > Units.InchesToFeet(_e.WallGapMax))         // rules 20-21
            { why = $"{inches(gap)} from the nearest wall face (allowed {Units.FormatInches(_e.WallGapMin)}-{Units.FormatInches(_e.WallGapMax)}, rules 20-21)"; return 0; }
            if (InsideDrawnWall(f, x, y)) { why = "between a drawn wall's two faces"; return 0; }

            var label = RoomOf(f, x, y, out var area);
            // a typical floor's sheet often leaves the names off: the same room on another floor carries it
            if (label == null && area != null && !area.Open && area.Area <= MaxRoom) label = Borrowed(f, area, x, y);
            // with no slab to say where the building ends, an unnamed spot may be outside it
            if (label == null && !f.Guard.HasSlab) { why = "no room name and no slab outline"; return 0; }
            // where the drawing names its rooms, a spot no name reaches is a corridor, a terrace or past the building:
            // closets and shafts first (rules 20-21: "whenever possible"), unnamed spots only when none will do
            if (label == null && _named && f.Spaces.Count > 0) { why = "in a room the drawing names nothing (or open to outside)"; return 0; }
            if (label == null) { room = "?"; return 1; }
            room = label.Text;
            if (!label.RiserSpace) why = $"in '{label.Text}' (rules 17, 20-21)";
            return label.RiserSpace ? 2 : 0;                    // never through a bedroom, kitchen, corridor… (rules 17, 20-21)
        }

        /// <summary>
        /// For the log, what to check when a riser offsets: why each run's spot does not go on to the floor above it, and
        /// why each offset run's spot is not used on the floors below it (were it allowed there, the riser could have run
        /// straight from lower down).
        /// </summary>
        private void ExplainOffsets(List<RiserRun> runs, Func<int, (double Width, double Length)> sizeAt)
        {
            bool named = _named;
            _named = true;
            try
            {
                // why each run stops: its spot on the floor just above it, at the size the next run needs
                foreach (var run in runs.Where(r => r.Point != null && r.To + 1 < _floors.Count))
                {
                    var (w, l) = sizeAt(run.To + 1);
                    double hw = Units.InchesToFeet(w) / 2, hl = Units.InchesToFeet(l) / 2;
                    var f = _floors[run.To + 1];
                    int g = Grade(f, run.Point.X, run.Point.Y, hw, hl, Math.Max(hw, hl), out string room, out string why);
                    _log($"SEARCH   WHY STOP ({run.Point.X:0.##}, {run.Point.Y:0.##}) does not go on to '{f.Level.Name}' at {Units.FormatInches(w)} x {Units.FormatInches(l)}: " +
                         (g > 0 ? $"allowed there (in {room}) — the placement check or the search area stopped it" : why));
                }
                foreach (var run in runs.Skip(1).Where(r => r.Point != null))
                    foreach (var below in runs.TakeWhile(r => r != run))
                    {
                        var (w, l) = sizeAt(below.From);
                        double hw = Units.InchesToFeet(w) / 2, hl = Units.InchesToFeet(l) / 2;
                        for (int k = below.From; k <= below.To; k++)
                        {
                            int g = Grade(_floors[k], run.Point.X, run.Point.Y, hw, hl, Math.Max(hw, hl), out string room, out string why);
                            _log($"SEARCH   WHY NOT ({run.Point.X:0.##}, {run.Point.Y:0.##}) on '{_floors[k].Level.Name}' at {Units.FormatInches(w)} x {Units.FormatInches(l)}: " +
                                 (g > 0 ? $"allowed (in {room}) — lost on ranking" : why));
                        }
                    }
            }
            finally { _named = named; }
        }

        /// <summary>A drawn wall is at most this thick (feet): face lines closer than this on both sides are one wall.</summary>
        private const double MaxWall = 14.0 / 12;

        /// <summary>
        /// The point is inside a wall the drawing draws as its two face lines: lines on both sides of it, across or along,
        /// less than a wall's thickness apart. A small opening fits between the faces of a thick wall without touching
        /// either, so the line checks alone take the wall's inside for floor.
        /// </summary>
        private static bool InsideDrawnWall(FloorData f, double x, double y)
        {
            double plusX = double.MaxValue, minusX = double.MaxValue, plusY = double.MaxValue, minusY = double.MaxValue;
            foreach (var w in f.Walls)
            {
                if (w.Half > 0) continue;                      // a Revit wall: its thickness is checked as a wall
                foreach (var s in w.Segs)
                {
                    // where the segment crosses the horizontal and the vertical line through the point
                    if ((s.Y0 - y) * (s.Y1 - y) <= 0 && Math.Abs(s.Y1 - s.Y0) > 1e-9)
                    {
                        double cx = s.X0 + (y - s.Y0) * (s.X1 - s.X0) / (s.Y1 - s.Y0) - x;
                        if (cx >= 0) plusX = Math.Min(plusX, cx); else minusX = Math.Min(minusX, -cx);
                    }
                    if ((s.X0 - x) * (s.X1 - x) <= 0 && Math.Abs(s.X1 - s.X0) > 1e-9)
                    {
                        double cy = s.Y0 + (x - s.X0) * (s.Y1 - s.Y0) / (s.X1 - s.X0) - y;
                        if (cy >= 0) plusY = Math.Min(plusY, cy); else minusY = Math.Min(minusY, -cy);
                    }
                }
            }
            return plusX + minusX <= MaxWall || plusY + minusY <= MaxWall;
        }

        /// <summary>A closed area bigger than this (square feet) is no room: the walls leak (a doorway with no door drawn).</summary>
        private const double MaxRoom = 2500;

        /// <summary>Two rooms on different floors are the same room when their outlines are this close on every side (feet)…</summary>
        private const double SameEdge = 4.0 / 12;
        /// <summary>…and their areas differ by no more than this share.</summary>
        private const double SameArea = 0.15;

        /// <summary>
        /// The name of an unnamed room, read off the same room on another floor: the nearest floor whose room at this
        /// point has the same outline (every side within <see cref="SameEdge"/>, area within <see cref="SameArea"/>) and
        /// a name. The drawings share Revit's 0,0, so the same room sits at the same place on every floor. Null when no
        /// floor has it named. Kept per room, so each is worked out once.
        /// </summary>
        private SpaceLabel Borrowed(FloorData f, RoomGrid.Room room, double x, double y)
        {
            if (f.Borrowed.TryGetValue(room, out var known)) return known;
            SpaceLabel found = null;
            int at = _floors.IndexOf(f);
            foreach (var g in _floors.Where(g => g != f).OrderBy(g => Math.Abs(_floors.IndexOf(g) - at)))
            {
                var label = RoomOf(g, x, y, out var other);
                if (label == null || other == null || other.Open) continue;
                bool same = Math.Abs(room.X0 - other.X0) <= SameEdge && Math.Abs(room.X1 - other.X1) <= SameEdge &&
                            Math.Abs(room.Y0 - other.Y0) <= SameEdge && Math.Abs(room.Y1 - other.Y1) <= SameEdge &&
                            Math.Abs(room.Area - other.Area) <= SameArea * Math.Max(room.Area, other.Area);
                if (!same) continue;
                found = new SpaceLabel { X = label.X, Y = label.Y, Text = $"{label.Text} (as on {g.Level.Name})", RiserSpace = label.RiserSpace };
                break;
            }
            f.Borrowed[room] = found;
            return found;
        }

        /// <summary>
        /// The name of the room the point is in: the names inside the area its walls, doors and windows close around it.
        /// A living space wins over a closet in the same area (a closet with no door drawn is part of the bedroom). Null
        /// when the area has no name, is open to the outside, or is too big to be one room.
        /// </summary>
        private static SpaceLabel RoomOf(FloorData f, double x, double y) => RoomOf(f, x, y, out _);

        /// <param name="room">The closed area the point is in (named or not); null when the floor has no lines.</param>
        private static SpaceLabel RoomOf(FloorData f, double x, double y, out RoomGrid.Room room)
        {
            f.Rooms ??= RoomGrid.Build(f.Walls.SelectMany(w => w.Segs).Concat(f.DoorLines).Concat(f.Windows).ToList(), f.Spaces);
            room = f.Rooms?.At(x, y);
            if (room == null || room.Open || room.Area > MaxRoom) return null;
            return room.Labels.FirstOrDefault(l => !l.RiserSpace) ?? room.Labels.FirstOrDefault();
        }

        /// <summary>
        /// A floor in rooms: its wall, door and window lines drawn on a 2" grid, and every patch of floor they close off
        /// filled out from a cell. A door leaf and its swing close the doorway. The room a point is in is its cell's patch,
        /// whatever its shape and however far its name is written from the point.
        /// </summary>
        private class RoomGrid
        {
            public class Room
            {
                public int Cells; public double Area; public bool Open; public List<SpaceLabel> Labels = new List<SpaceLabel>();
                /// <summary>The room's outline box (feet).</summary>
                public double X0, Y0, X1, Y1;
            }

            private double _x0, _y0, _cell;
            private int _w, _h;
            private int[] _id;                                   // -1 = a line, else the room's index
            private readonly List<Room> _rooms = new List<Room>();

            public static RoomGrid Build(List<Seg> lines, List<SpaceLabel> labels)
            {
                if (lines.Count == 0) return null;
                var g = new RoomGrid { _cell = 2.0 / 12 };
                g._x0 = lines.Min(s => Math.Min(s.X0, s.X1)) - 3;
                g._y0 = lines.Min(s => Math.Min(s.Y0, s.Y1)) - 3;
                double x1 = lines.Max(s => Math.Max(s.X0, s.X1)) + 3, y1 = lines.Max(s => Math.Max(s.Y0, s.Y1)) + 3;
                while ((x1 - g._x0) * (y1 - g._y0) / (g._cell * g._cell) > 8e6) g._cell *= 1.5;      // a site-wide drawing
                g._w = (int)Math.Ceiling((x1 - g._x0) / g._cell) + 1;
                g._h = (int)Math.Ceiling((y1 - g._y0) / g._cell) + 1;
                g._id = new int[g._w * g._h];
                for (int i = 0; i < g._id.Length; i++) g._id[i] = int.MaxValue;   // not filled yet

                // every line as the cells it passes through, sampled at half a cell: no gap a fill could slip through
                foreach (var s in lines)
                {
                    double len = Math.Sqrt((s.X1 - s.X0) * (s.X1 - s.X0) + (s.Y1 - s.Y0) * (s.Y1 - s.Y0));
                    int n = Math.Max(1, (int)Math.Ceiling(len / (g._cell / 2)));
                    for (int k = 0; k <= n; k++)
                    {
                        int c = g.Cell(s.X0 + (s.X1 - s.X0) * k / n, s.Y0 + (s.Y1 - s.Y0) * k / n);
                        if (c >= 0) g._id[c] = -1;
                    }
                }

                // each patch of floor, filled cell to cell (up, down, left, right)
                var stack = new Stack<int>();
                for (int start = 0; start < g._id.Length; start++)
                {
                    if (g._id[start] != int.MaxValue) continue;
                    var room = new Room();
                    int id = g._rooms.Count;
                    g._rooms.Add(room);
                    g._id[start] = id;
                    stack.Push(start);
                    int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                    while (stack.Count > 0)
                    {
                        int c = stack.Pop(), cx = c % g._w, cy = c / g._w;
                        room.Cells++;
                        minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx); minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                        if (cx == 0 || cy == 0 || cx == g._w - 1 || cy == g._h - 1) room.Open = true;     // reaches past the drawing
                        foreach (int nb in new[] { cx > 0 ? c - 1 : -1, cx < g._w - 1 ? c + 1 : -1, cy > 0 ? c - g._w : -1, cy < g._h - 1 ? c + g._w : -1 })
                            if (nb >= 0 && g._id[nb] == int.MaxValue) { g._id[nb] = id; stack.Push(nb); }
                    }
                    room.Area = room.Cells * g._cell * g._cell;
                    room.X0 = g._x0 + minX * g._cell; room.X1 = g._x0 + (maxX + 1) * g._cell;
                    room.Y0 = g._y0 + minY * g._cell; room.Y1 = g._y0 + (maxY + 1) * g._cell;
                }
                foreach (var l in labels ?? new List<SpaceLabel>()) g.At(l.X, l.Y)?.Labels.Add(l);
                return g;
            }

            private int Cell(double x, double y)
            {
                int cx = (int)Math.Floor((x - _x0) / _cell), cy = (int)Math.Floor((y - _y0) / _cell);
                return cx < 0 || cy < 0 || cx >= _w || cy >= _h ? -1 : cy * _w + cx;
            }

            /// <summary>
            /// The room at a point. A point on a line's cell (a name written over a wall, a riser an inch or two off its
            /// face) takes the nearest cell around it that is floor.
            /// </summary>
            public Room At(double x, double y)
            {
                int c = Cell(x, y);
                if (c < 0) return null;
                if (_id[c] >= 0) return _rooms[_id[c]];
                int cx = c % _w, cy = c / _w, best = -1;
                double bestD = double.MaxValue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= _w || ny >= _h || _id[ny * _w + nx] < 0) continue;
                        double px = _x0 + (nx + 0.5) * _cell - x, py = _y0 + (ny + 0.5) * _cell - y, d = px * px + py * py;
                        if (d < bestD) { bestD = d; best = _id[ny * _w + nx]; }
                    }
                return best < 0 ? null : _rooms[best];
            }
        }

        private static Box ToBox(BoundingBoxXYZ b, string label) => new Box { X0 = b.Min.X, Y0 = b.Min.Y, X1 = b.Max.X, Y1 = b.Max.Y, Label = label };

        private static Box Footprint(OpeningRecord o)
        {
            double hw = Units.InchesToFeet((o.Data.Diameter ?? o.Data.Width ?? 0) / 2), hl = Units.InchesToFeet((o.Data.Diameter ?? o.Data.Length ?? 0) / 2);
            if (hw <= 0 || hl <= 0)
            {
                var bb = o.Instance.get_BoundingBox(null);
                if (bb != null) return ToBox(bb, o.Instance.Id.ToString());
            }
            return new Box { X0 = o.Point.X - hw, Y0 = o.Point.Y - hl, X1 = o.Point.X + hw, Y1 = o.Point.Y + hl, Label = o.Instance.Id.ToString() };
        }

        private static double Distance(Box b, double x, double y)
        {
            double dx = Math.Max(Math.Max(b.X0 - x, 0), x - b.X1), dy = Math.Max(Math.Max(b.Y0 - y, 0), y - b.Y1);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Overlaps(Box b, double x, double y, double hw, double hl, double clear) =>
            x + hw + clear > b.X0 && x - hw - clear < b.X1 && y + hl + clear > b.Y0 && y - hl - clear < b.Y1;

        private static double SegDistance(Seg s, double x, double y)
        {
            double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, len = dx * dx + dy * dy;
            double t = len < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - s.X0) * dx + (y - s.Y0) * dy) / len));
            double px = s.X0 + t * dx - x, py = s.Y0 + t * dy - y;
            return Math.Sqrt(px * px + py * py);
        }


        /// <summary>
        /// The roof sleeve (rules 16-17): the nearest spot to the riser below that is on the roof slab, clear of structure, and
        /// at least clearances.roofMinFromWallOrCurb from walls and curbs (rule 58). Null when there is none within reach.
        /// </summary>
        public XYZ Roof(Level roof, XYZ below, double diameterIn, List<string> notes)
        {
            var f = Load(roof);
            double r = Units.InchesToFeet(diameterIn) / 2, radius = Units.InchesToFeet(_e.SearchRadius), step = Math.Max(Units.InchesToFeet(_e.SearchStep), 1.0 / 12);
            double minWall = Units.InchesToFeet(_rules.Clearances.RoofMinFromWallOrCurb), minCol = Units.InchesToFeet(_rules.Clearances.MinFromColumn);
            double reach = Reach(f, below), inner = -1;
            var why = new Dictionary<string, int>();
            void Count(string reason) => why[reason] = why.TryGetValue(reason, out int c) ? c + 1 : 1;
            // the rules' radius first, then twice it and twice that, as the floors' search does, until past the roof's drawing
            while (true)
            {
                int n = (int)Math.Ceiling(radius / step);
                var spots = new List<(double X, double Y, double D)>();
                for (int i = -n; i <= n; i++)
                    for (int j = -n; j <= n; j++)
                    {
                        double x = below.X + i * step, y = below.Y + j * step, d = Math.Sqrt((x - below.X) * (x - below.X) + (y - below.Y) * (y - below.Y));
                        if (d <= radius && d > inner) spots.Add((x, y, d));
                    }
                foreach (var s in spots.OrderBy(s => s.D))
                {
                    if (f.Columns.Any(c => Distance(c, s.X, s.Y) - r < minCol)) { Count("near a column (rule 26)"); continue; }
                    if (f.Beams.Any(b => Overlaps(b, s.X, s.Y, r, r, 0))) { Count("in a beam (rule 28)"); continue; }
                    if (f.Openings.Any(o => Overlaps(o, s.X, s.Y, r, r, Units.InchesToFeet(_e.OpeningClearance)))) { Count("near another opening"); continue; }
                    if (_e.AvoidDoorways && f.Doors.Any(o => Overlaps(o, s.X, s.Y, r, r, 0))) { Count("in a doorway"); continue; }
                    if (f.Walls.Any(w => w.Segs.Any(seg => SegDistance(seg, s.X, s.Y) - r - w.Half < minWall))) { Count("too close to a wall or curb (rule 58)"); continue; }
                    var p = new XYZ(s.X, s.Y, 0);
                    var problems = f.Guard.Check(p, diameterIn / 2, diameterIn / 2, SystemKind.Electrical).Where(w => !w.Contains("rules 20-21")).ToList();
                    if (problems.Count > 0) { Count("placement check: " + PlacementGuard.Clean(problems[0]).Split('(')[0].Trim()); continue; }
                    _log($"SEARCH ROOF '{roof.Name}': sleeve at ({s.X:0.##}, {s.Y:0.##}), {s.D:0.#} ft from the riser below");
                    if (s.D > 0.01) notes.Add($"moved {s.D:0.#} ft from the riser below to sit on the roof slab, clear of walls and curbs (rules 17, 58)");
                    return p;
                }
                if (radius >= reach) break;
                inner = radius;
                radius = Math.Min(radius * 2, reach);
                _log($"SEARCH ROOF '{roof.Name}': nothing within {Units.FormatInches(inner * 12)}: widening to {Units.FormatInches(radius * 12)}");
            }
            _log($"SEARCH ROOF '{roof.Name}': no allowed spot within {Units.FormatInches(radius * 12)} of ({below.X:0.##}, {below.Y:0.##}); spots refused for: " +
                 string.Join("; ", why.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} x{kv.Value}")));
            notes.Add("no spot on the roof slab within reach clears walls, curbs and structure: placed over the riser below, check it");
            return null;
        }

        /// <summary>The placement check's findings for a clicked spot on every floor of a run, plus the room it is in.</summary>
        public List<string> Review(int from, int to, XYZ p, double widthIn, double lengthIn)
        {
            var notes = new List<string>();
            double hw = Units.InchesToFeet(widthIn) / 2, hl = Units.InchesToFeet(lengthIn) / 2;
            for (int k = from; k <= to && k < _floors.Count; k++)
            {
                var f = _floors[k];
                foreach (var w in f.Guard.Check(p, widthIn / 2, lengthIn / 2, SystemKind.Electrical))
                    if (!w.Contains("rules 20-21")) notes.Add(f.Level.Name + ": " + PlacementGuard.Clean(w));
                if (f.Walls.Count == 0) { notes.Add($"{f.Level.Name}: no walls in the model on this level, so the spot could not be checked"); continue; }
                if (Grade(f, p.X, p.Y, hw, hl, Math.Max(hw, hl), out string room) == 0)
                {
                    var label = RoomOf(f, p.X, p.Y);
                    if (label != null && !label.RiserSpace) notes.Add($"{f.Level.Name}: in '{label.Text}' (rules 17, 20-21: not through living spaces)");
                    else notes.Add($"{f.Level.Name}: not against a wall, or too close to a column, beam or other opening (rules 20-21, 26, 28)");
                }
            }
            return notes;
        }
    }
}
