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
    /// living space; a closet, shaft or chase named in the architect's drawing is preferred. The best spot is then checked
    /// once more with the same <see cref="PlacementGuard"/> the click-placed openings use.
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
            public PlacementGuard Guard;
        }

        private readonly LinkedModels _links;
        private readonly List<(Element Element, BoundingBoxXYZ Box, LinkedModels.Source Source)> _columns, _beams;
        private readonly List<OpeningRecord> _openings;
        private readonly ElectricalSurvey _survey;

        /// <param name="levels">The riser levels, ascending.</param>
        /// <param name="replacing">Openings about to be deleted (the riser being replaced): not in the way.</param>
        public ElectricalLocator(Document doc, RuleSet rules, IList<Level> levels, ElectricalSurvey survey, ICollection<ElementId> replacing, Action<string> log)
        {
            _doc = doc; _rules = rules; _e = rules.Systems.Electrical; _log = log ?? (_ => { }); _survey = survey;
            _links = new LinkedModels(doc, rules);
            _columns = _links.Collect(BuiltInCategory.OST_StructuralColumns).Concat(_links.Collect(BuiltInCategory.OST_Columns)).ToList();
            _beams = _links.Collect(BuiltInCategory.OST_StructuralFraming).ToList();
            _openings = RiserIndex.AllOpenings(doc).Where(o => !replacing.Contains(o.Instance.Id)).ToList();
            _floors = levels.Select(Load).ToList();
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
            if (read != null)
            {
                // a drawn wall is its two face lines: each a wall of no thickness
                foreach (var s in read.Walls) f.Walls.Add(new WallData { Segs = { new Seg { X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1 } } });
                f.DoorLines = read.DoorLines.Select(s => new Seg { X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1 }).ToList();
                f.Doors = read.Doors.Select(d => new Box { X0 = d.X0, Y0 = d.Y0, X1 = d.X1, Y1 = d.Y1, Label = "door" }).ToList();
                f.Spaces = read.Spaces;
            }
            f.Columns = _columns.Where(c => c.Box.Min.Z - 1 <= z && c.Box.Max.Z + 1 >= z).Select(c => ToBox(c.Box, c.Source.Describe(c.Element.Id))).ToList();
            f.Beams = _beams.Where(c => c.Box.Min.Z - 1 <= z && c.Box.Max.Z + 1 >= z).Select(c => ToBox(c.Box, c.Source.Describe(c.Element.Id))).ToList();
            f.Openings = _openings.Where(o => o.Level.Id == level.Id).Select(Footprint).ToList();
            f.Guard = new PlacementGuard(_doc, _rules, level, f.WallGeometry);
            _log($"SEARCH level '{level.Name}': {revitWalls} Revit wall(s) + {f.Walls.Count - revitWalls} drawn wall line(s) from {read?.File ?? "no drawing"}, " +
                 $"{f.Doors.Count} door(s), slab outline {(f.Guard.HasSlab ? "yes" : "NONE")}, {f.Columns.Count} column(s),{f.Beams.Count} beam(s), {f.Openings.Count} other opening(s), " +
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
            return runs;
        }

        /// <summary>Only spots in a room the drawing names count (set while searching; off for the fallback and for a clicked spot).</summary>
        private bool _named;

        private RiserRun Best(int from, XYZ anchor, double widthIn, double lengthIn)
        {
            _named = true;
            try
            {
                var run = Search(from, anchor, widthIn, lengthIn);
                if (run.Point != null) return run;
                _log($"SEARCH   nothing in a named closet or shaft from '{_floors[from].Level.Name}': trying spots the drawing names no room for");
                _named = false;
                run = Search(from, anchor, widthIn, lengthIn);
                if (run.Point != null) run.Unnamed = true;
                return run;
            }
            finally { _named = false; }
        }

        private RiserRun Search(int from, XYZ anchor, double widthIn, double lengthIn)
        {
            double hw = Units.InchesToFeet(widthIn) / 2, hl = Units.InchesToFeet(lengthIn) / 2, r = Math.Max(hw, hl);
            double radius = Units.InchesToFeet(_e.SearchRadius), step = Math.Max(Units.InchesToFeet(_e.SearchStep), 1.0 / 12);
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
                return new RiserRun { From = from, To = from, Why = $"no allowed spot within {Units.FormatInches(_e.SearchRadius)} on {bottom.Level.Name}" };

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
        private int Grade(FloorData f, double x, double y, double hw, double hl, double r, out string room)
        {
            room = null;
            double minCol = Units.InchesToFeet(_rules.Clearances.MinFromColumn), clear = Units.InchesToFeet(_e.OpeningClearance);

            // through the slab, never beyond its edge (general rule): outside the building nothing is in the way, so
            // without this the "best" spot is out past the exterior wall
            if (!f.Guard.WithinSlab(new XYZ(x, y, 0), hw, hl)) return 0;
            foreach (var c in f.Columns) if (Distance(c, x, y) - r < minCol) return 0;                         // rule 26
            foreach (var b in f.Beams) if (Overlaps(b, x, y, hw, hl, 0)) return 0;                             // rule 28
            foreach (var o in f.Openings) if (Overlaps(o, x, y, hw, hl, clear)) return 0;                      // other openings
            if (_e.AvoidDoorways)
                foreach (var o in f.Doors) if (Overlaps(o, x, y, hw, hl, clear)) return 0;                     // door swings, and just inside doorways

            double face = double.MaxValue;
            foreach (var w in f.Walls)
            {
                double d = double.MaxValue;
                foreach (var s in w.Segs) d = Math.Min(d, SegDistance(s, x, y));
                if (d - r < w.Half) return 0;                   // inside a wall, on its edge (general rule), or in a shear wall (27)
                face = Math.Min(face, d - w.Half);
            }
            double gap = face - r;                              // from the opening's side to the nearest wall face
            if (gap < Units.InchesToFeet(_e.WallGapMin) || gap > Units.InchesToFeet(_e.WallGapMax)) return 0;   // rules 20-21
            if (InsideDrawnWall(f, x, y)) return 0;             // between a drawn wall's two faces, not on the floor

            var label = Visible(f, x, y);
            // with no slab to say where the building ends, an unnamed spot may be outside it
            if (label == null && !f.Guard.HasSlab) return 0;
            // where the drawing names its rooms, a spot no name reaches is a corridor, a terrace or past the building:
            // closets and shafts first (rules 20-21: "whenever possible"), unnamed spots only when none will do
            if (label == null && _named && f.Spaces.Count > 0) return 0;
            if (label == null) { room = "?"; return 1; }
            room = label.Text;
            return label.RiserSpace ? 2 : 0;                    // never through a bedroom, kitchen, corridor… (rules 17, 20-21)
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

        /// <summary>The nearest room name in the drawing that no wall separates from the point: the room the point is in.</summary>
        private static SpaceLabel Visible(FloorData f, double x, double y)
        {
            foreach (var s in f.Spaces.Select(s => (S: s, D: (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y))).Where(t => t.D < 25 * 25).OrderBy(t => t.D))
                if (!f.Walls.Any(w => w.Segs.Any(seg => Crosses(seg, x, y, s.S.X, s.S.Y))) && !f.DoorLines.Any(seg => Crosses(seg, x, y, s.S.X, s.S.Y))) return s.S;
            return null;
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

        private static bool Crosses(Seg s, double ax, double ay, double bx, double by)
        {
            double d1 = Cross(s.X0, s.Y0, s.X1, s.Y1, ax, ay), d2 = Cross(s.X0, s.Y0, s.X1, s.Y1, bx, by);
            double d3 = Cross(ax, ay, bx, by, s.X0, s.Y0), d4 = Cross(ax, ay, bx, by, s.X1, s.Y1);
            return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
        }

        private static double Cross(double ax, double ay, double bx, double by, double px, double py) => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

        /// <summary>
        /// The roof sleeve (rules 16-17): the nearest spot to the riser below that is on the roof slab, clear of structure, and
        /// at least clearances.roofMinFromWallOrCurb from walls and curbs (rule 58). Null when there is none within reach.
        /// </summary>
        public XYZ Roof(Level roof, XYZ below, double diameterIn, List<string> notes)
        {
            var f = Load(roof);
            double r = Units.InchesToFeet(diameterIn) / 2, radius = Units.InchesToFeet(_e.SearchRadius), step = Math.Max(Units.InchesToFeet(_e.SearchStep), 1.0 / 12);
            double minWall = Units.InchesToFeet(_rules.Clearances.RoofMinFromWallOrCurb), minCol = Units.InchesToFeet(_rules.Clearances.MinFromColumn);
            int n = (int)Math.Ceiling(radius / step);
            var spots = new List<(double X, double Y, double D)>();
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    double x = below.X + i * step, y = below.Y + j * step, d = Math.Sqrt((x - below.X) * (x - below.X) + (y - below.Y) * (y - below.Y));
                    if (d <= radius) spots.Add((x, y, d));
                }
            foreach (var s in spots.OrderBy(s => s.D))
            {
                if (f.Columns.Any(c => Distance(c, s.X, s.Y) - r < minCol) || f.Beams.Any(b => Overlaps(b, s.X, s.Y, r, r, 0)) ||
                    f.Openings.Any(o => Overlaps(o, s.X, s.Y, r, r, Units.InchesToFeet(_e.OpeningClearance))) || (_e.AvoidDoorways && f.Doors.Any(o => Overlaps(o, s.X, s.Y, r, r, 0))))
                    continue;
                if (f.Walls.Any(w => w.Segs.Any(seg => SegDistance(seg, s.X, s.Y) - r - w.Half < minWall))) continue;
                var p = new XYZ(s.X, s.Y, 0);
                var problems = f.Guard.Check(p, diameterIn / 2, diameterIn / 2, SystemKind.Electrical).Where(w => !w.Contains("rules 20-21")).ToList();
                if (problems.Count > 0) continue;
                _log($"SEARCH ROOF '{roof.Name}': sleeve at ({s.X:0.##}, {s.Y:0.##}), {s.D:0.#} ft from the riser below");
                if (s.D > 0.01) notes.Add($"moved {s.D:0.#} ft from the riser below to sit on the roof slab, clear of walls and curbs (rules 17, 58)");
                return p;
            }
            _log($"SEARCH ROOF '{roof.Name}': no allowed spot within {Units.FormatInches(_e.SearchRadius)} of ({below.X:0.##}, {below.Y:0.##})");
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
                    var label = Visible(f, p.X, p.Y);
                    if (label != null && !label.RiserSpace) notes.Add($"{f.Level.Name}: in '{label.Text}' (rules 17, 20-21: not through living spaces)");
                    else notes.Add($"{f.Level.Name}: not against a wall, or too close to a column, beam or other opening (rules 20-21, 26, 28)");
                }
            }
            return notes;
        }
    }
}
