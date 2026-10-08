using System;
using System.Collections.Generic;
using System.Linq;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// Where a fixture's sleeve goes, from the fixture as the architect draws it on the plumbing PDF (its fixture layers) and
    /// the walls around it (manual, toilet sleeves page): a tub's sleeves on its drawn drain circle, along the tub; a shower's
    /// at its centre (point drain: the corner-to-corner X); a toilet's on its centre line 1'-1" from the wall behind it;
    /// sinks, lavatories and washers drain into the wall behind them: the sleeve in that wall on the fixture's centre line
    /// (the wall a drawn faucet sits on, else the nearest). Real inches, the PDF's own axes. Free of the Revit API.
    /// A lavatory or sink by shape alone needs a closed curved basin or a faucet/drain circle: an open arc with a line (a door
    /// swing on an alteration drawing's new-work layer) is not one.
    /// </summary>
    public static class FixtureDrains
    {
        /// <summary>
        /// One stroke of a fixture layer: its box; Circle = a closed round stroke (a tub drain, faucet handles); Curved = an
        /// arc, ellipse or spline (a toilet bowl; set by the DWG reader, the PDF reader leaves it false).
        /// </summary>
        public class Stroke
        {
            public double X0, Y0, X1, Y1;
            public bool Circle, Curved;
            /// <summary>A closed outline (a basin, a tub, a tank): set by the DWG reader.</summary>
            public bool Closed;
            /// <summary>A straight line at an angle (a shower's corner-to-corner X): set by the DWG reader.</summary>
            public bool Diagonal;
            /// <summary>
            /// The block the stroke belongs to (DWG reader; 0 = loose). A block is one drawing: a tub and the vanity touching
            /// it stay two fixtures. The reader leaves blocks bigger than a fixture (a counter, a bound floor plan) loose.
            /// </summary>
            public int Group;
            /// <summary>On a kitchen layer (DWG reader): a sink drawn there is a kitchen sink, not a lavatory.</summary>
            public bool Kitchen;
        }

        /// <summary>A fixture recognised by its drawing alone, with no label: its code, sleeve point(s) and how they were found.</summary>
        public class Shape
        {
            public string Code, How;
            public double Cx, Cy;
            /// <summary>The drawing's box (inches).</summary>
            public double X0, Y0, X1, Y1;
            public List<(double X, double Y)> Points;
        }

        /// <summary>
        /// Every fixture the drawing shows, by shape alone (Extract Fixtures): toilets, tubs and showers as in
        /// <see cref="Recognise"/>, plus basins (lavatory or sink: a curved or round stroke, sink-sized) and washers/dryers
        /// (22-34" square with a round drum). A shape with no wall behind it keeps its box centre (How says so).
        /// </summary>
        public static List<Shape> Extract(IList<Stroke> strokes, IList<((double X, double Y) A, (double X, double Y) B)> walls,
                                          Func<string, (int Count, double Spacing)> sleeves, double toiletFromWall = 13, double maxObject = 72)
        {
            var shapes = new List<Shape>();
            var objects = Objects(strokes, maxObject);
            var basins = Basins(strokes, objects);
            // a vanity or counter with its basins drawn: one sleeve per basin (a double vanity has two), not one for the counter
            foreach (var o in objects.Where(o => !basins.Any(b => Inside(b, o))).Concat(basins))
            {
                string code = Kind(o);
                if (code == null || LooseShower(code, o, walls)) continue;
                var (points, how) = Drain(code, o, walls, sleeves(code), toiletFromWall);
                if (points == null) { points = new List<(double X, double Y)> { (o.Cx, o.Cy) }; how = "no wall found behind it: its centre"; }
                shapes.Add(new Shape { Code = code == "LAV" ? "LAV/SINK" : code, Cx = o.Cx, Cy = o.Cy, X0 = o.X0, Y0 = o.Y0, X1 = o.X1, Y1 = o.Y1, Points = points, How = how });
            }
            return shapes;
        }

        private class Obj
        {
            public double X0, Y0, X1, Y1;
            public int Curved;
            /// <summary>Closed curved outlines (an oval basin, a tub's inner rim): never a door swing (an open arc).</summary>
            public int ClosedCurved;
            /// <summary>Small curved strokes (up to 6"): faucet handles and spouts, a drain symbol. A door swing has none.</summary>
            public int Fittings;
            /// <summary>The block the object is (see <see cref="Stroke.Group"/>); 0 = strokes merged by touching.</summary>
            public int Group;
            /// <summary>Straight lines at an angle (a shower's X).</summary>
            public int Diagonals;
            /// <summary>The biggest curved stroke's box (a freestanding tub's oval outline spans the whole tub).</summary>
            public double MaxCurvedW, MaxCurvedL;
            /// <summary>One basin of a sink, lavatory or vanity (see <see cref="Basins"/>), not a whole drawing.</summary>
            public bool Basin;
            /// <summary>Centre of the curved strokes, weighted by their size (a toilet's bowl, not its tank); valid when CurvedW &gt; 0.</summary>
            public double CurvedSx, CurvedSy, CurvedW;
            public List<Stroke> Circles = new List<Stroke>();
            /// <summary>Every stroke of the drawing (for a basin: the small ones around it too, its faucet).</summary>
            public List<Stroke> Strokes = new List<Stroke>();
            /// <summary>Drawn on a kitchen layer.</summary>
            public bool Kitchen;
            public double W => X1 - X0; public double L => Y1 - Y0;
            public double Cx => (X0 + X1) / 2; public double Cy => (Y0 + Y1) / 2;
        }

        /// <summary>Short side min/max, long side min/max (inches) of each fixture's drawing.</summary>
        private static readonly Dictionary<string, double[]> Sizes = new Dictionary<string, double[]>
        {
            ["WC"] = new double[] { 14, 24, 24, 34 }, ["LAV"] = new double[] { 12, 26, 14, 40 }, ["BT"] = new double[] { 26, 40, 48, 78 },
            ["SH"] = new double[] { 28, 60, 28, 72 }, ["KS"] = new double[] { 12, 26, 14, 40 }, ["LS"] = new double[] { 12, 28, 14, 34 },
            ["W/D"] = new double[] { 22, 34, 22, 34 }, ["WD"] = new double[] { 22, 34, 22, 34 }, ["FD"] = new double[] { 2, 12, 2, 12 }
        };

        /// <param name="fixtures">The page's fixture labels; each one found gets its sleeve point(s) and how they were found.</param>
        /// <param name="sleeves">Sleeves per fixture code (tub: 2) and their spacing (inches c-c).</param>
        /// <param name="search">How far (inches) from its label a fixture's drawing may be.</param>
        /// <param name="toiletFromWall">Toilet sleeve centre from the wall behind it (manual: 1'-1").</param>
        public static void Locate(IList<FixtureMark> fixtures, IList<Stroke> strokes, IList<((double X, double Y) A, (double X, double Y) B)> walls,
                                  Func<string, (int Count, double Spacing)> sleeves, double search = 24, double toiletFromWall = 13, double maxObject = 72) =>
            Recognise(fixtures, strokes, walls, sleeves, search, toiletFromWall, maxObject, false);

        /// <summary>
        /// <see cref="Locate"/>, then (<paramref name="unlabelled"/>) the drawings no label took that are a fixture by their
        /// shape alone: a toilet (a bowl and tank: curved strokes, 14-24" x 24-34", longer than wide), a tub (26-40" x
        /// 48-78" with its drain circle), a shower (28-60" square-ish with a drain circle near its centre). Sinks and
        /// lavatories are not guessed: a counter's basins look like too many other things.
        /// </summary>
        public static List<Shape> Recognise(IList<FixtureMark> fixtures, IList<Stroke> strokes, IList<((double X, double Y) A, (double X, double Y) B)> walls,
                                            Func<string, (int Count, double Spacing)> sleeves, double search, double toiletFromWall, double maxObject, bool unlabelled)
        {
            var objects = Objects(strokes, maxObject);
            var used = new HashSet<Obj>();
            // the kind of a drawing is known only where the strokes say which are curves (the DWG reader; not the PDF's)
            bool shaped = strokes.Any(s => s.Curved);
            // a sink label takes one basin of a vanity (a double vanity holds two fixtures), before the whole drawing
            if (shaped) objects.AddRange(Basins(strokes, objects));
            // nearest label first, so two labels never take the same fixture
            var pairs = (from f in fixtures
                         where Sizes.ContainsKey(f.Code)
                         from o in objects
                         let s = Sizes[f.Code]
                         let shortSide = Math.Min(o.W, o.L)
                         let longSide = Math.Max(o.W, o.L)
                         where shortSide >= s[0] && shortSide <= s[1] && longSide >= s[2] && longSide <= s[3]
                         where !shaped || Fits(f.Code, Kind(o))      // a drawing that is clearly a toilet is never a sink label's
                         let g = Gap(o, f.X, f.Y)
                         where g <= search
                         orderby g, o.Basin ? 0 : 1
                         select (F: f, O: o)).ToList();
            var done = new HashSet<FixtureMark>();
            foreach (var (f, o) in pairs)
            {
                if (done.Contains(f) || used.Contains(o)) continue;
                var (points, how) = Drain(f.Code, o, walls, sleeves(f.Code), toiletFromWall);
                if (points == null) continue;
                done.Add(f); used.Add(o);
                f.Drains.AddRange(points);
                f.DrainHow = how;
            }
            // a label near a drawing it could not use still owns it (never guessed again as an unlabelled fixture)
            foreach (var f in fixtures)
                foreach (var o in objects.Where(o => Gap(o, f.X, f.Y) <= 6)) used.Add(o);

            var shapes = new List<Shape>();
            if (!unlabelled) return shapes;
            foreach (var o in objects.Where(o => !used.Contains(o)))
            {
                string code = Kind(o);
                if (code != "WC" && code != "BT" && code != "SH") continue;
                if (LooseShower(code, o, walls)) continue;
                var (points, how) = Drain(code, o, walls, sleeves(code), toiletFromWall);
                if (points == null) continue;
                shapes.Add(new Shape { Code = code, Cx = o.Cx, Cy = o.Cy, Points = points, How = how });
            }
            return shapes;
        }

        /// <summary>
        /// What a drawing is by its shape alone; null = nothing recognisable. A toilet: a bowl and tank (curved strokes,
        /// 14-24" x 24-34", longer than wide). A shower: a drain circle near its centre (a 60x32 shower pan is tub-sized). A
        /// tub: tub-sized with a drain circle. A washer/dryer: square with a round drum. A basin (lavatory or sink): sink-sized
        /// with a curved or round stroke.
        /// </summary>
        private static string Kind(Obj o)
        {
            double shortSide = Math.Min(o.W, o.L), longSide = Math.Max(o.W, o.L);
            bool In(string code) { var s = Sizes[code]; return shortSide >= s[0] && shortSide <= s[1] && longSide >= s[2] && longSide <= s[3]; }
            bool drain = o.Circles.Any(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 6);
            bool centred = o.Circles.Any(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 6 &&
                                              Math.Abs((c.X0 + c.X1) / 2 - o.Cx) <= 8 && Math.Abs((c.Y0 + c.Y1) / 2 - o.Cy) <= 8);
            bool drum = o.Circles.Any(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) >= 12);
            string sink = o.Kitchen ? "KS" : "LAV";
            if (o.Basin) return sink;
            // a cooktop or range: round burners (5-11"), no basin; never a sink
            if (Burners(o.Circles) >= 2 && o.ClosedCurved == 0) return null;
            // a vanity or counter: a straight closed outline around the whole drawing (a toilet has none), so its
            // oval basin never makes it a toilet
            bool counter = o.Strokes.Any(s => s.Closed && !s.Curved && s.X0 <= o.X0 + 2 && s.Y0 <= o.Y0 + 2 && s.X1 >= o.X1 - 2 && s.Y1 >= o.Y1 - 2);
            return In("WC") && !counter && o.Curved > 0 && longSide >= 1.25 * shortSide ? "WC"
                 : (In("SH") || In("BT")) && (centred || o.Diagonals >= 2) ? "SH"     // a centre drain or the corner-to-corner X
                 : In("BT") && drain ? "BT"
                 : In("W/D") && longSide <= 1.2 * shortSide && drum ? "W/D"
                 : In("LAV") && (o.ClosedCurved > 0 || o.Circles.Count > 0 || o.Fittings > 0 || (counter && Faucet(o) != null)) ? sink   // a door swing (one open arc, no faucet) is not a basin
                 : null;
        }

        /// <summary>A label can take a drawing of this kind: the same fixture, or a drawing the shape does not decide.</summary>
        private static bool Fits(string label, string kind)
        {
            if (kind == null) return true;
            switch (label)
            {
                case "WC": return kind == "WC";
                case "BT": case "SH": return kind == "BT" || kind == "SH";
                case "W/D": case "WD": return kind == "W/D" || kind == "LAV";
                case "LAV": case "KS": case "LS": return kind == "LAV" || kind == "KS";
                default: return kind != "WC";
            }
        }

        private static (List<(double X, double Y)> Points, string How) Drain(string code, Obj o, IList<((double X, double Y) A, (double X, double Y) B)> walls,
                                                                            (int Count, double Spacing) sleeve, double toiletFromWall)
        {
            switch (code)
            {
                case "BT":
                    {
                        var drain = o.Circles.Where(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 5).OrderBy(c => c.X1 - c.X0).FirstOrDefault();
                        if (drain == null) return (null, null);
                        double dx = (drain.X0 + drain.X1) / 2, dy = (drain.Y0 + drain.Y1) / 2;
                        bool alongX = o.W >= o.L;
                        // a freestanding tub (its oval outline spans the whole drawing) drains through one sleeve (office sets)
                        bool freestanding = o.MaxCurvedW >= 0.85 * o.W && o.MaxCurvedL >= 0.85 * o.L;
                        int n = freestanding ? 1 : Math.Max(1, sleeve.Count);
                        var pts = Enumerable.Range(0, n).Select(i => (i - (n - 1) / 2.0) * sleeve.Spacing)
                                            .Select(a => alongX ? (dx + a, dy) : (dx, dy + a)).ToList();
                        return (pts, freestanding ? "freestanding tub's drawn drain circle, one sleeve"
                                                  : $"tub's drawn drain circle{(n > 1 ? $", {n} sleeves {sleeve.Spacing:0.#}\" c-c along the tub" : "")}");
                    }
                case "SH":
                case "FD":
                    {
                        // the drawn drain circle near the centre when there is one, else the centre (point drain)
                        var circle = o.Circles.Where(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 6)
                                              .OrderBy(c => Math.Abs((c.X0 + c.X1) / 2 - o.Cx) + Math.Abs((c.Y0 + c.Y1) / 2 - o.Cy)).FirstOrDefault();
                        if (circle != null && Math.Abs((circle.X0 + circle.X1) / 2 - o.Cx) <= 8 && Math.Abs((circle.Y0 + circle.Y1) / 2 - o.Cy) <= 8)
                            return (new List<(double X, double Y)> { ((circle.X0 + circle.X1) / 2, (circle.Y0 + circle.Y1) / 2) }, code == "SH" ? "shower's drawn drain" : "drain's drawn circle");
                        return (new List<(double X, double Y)> { (o.Cx, o.Cy) }, code == "SH" ? "shower's centre (point drain)" : "drain's centre");
                    }
                case "WC":
                    {
                        // the toilet's back is its tank end, the far end from its bowl (the curved strokes); the sleeve on its
                        // centre line, 1'-1" from the wall behind the tank (the tank stands about 1" off it)
                        bool alongX = o.W >= o.L;
                        double half = (alongX ? o.W : o.L) / 2, mid = alongX ? o.Cx : o.Cy;
                        int front = 0;
                        if (o.CurvedW > 0)
                        {
                            double bowl = (alongX ? o.CurvedSx : o.CurvedSy) / o.CurvedW - mid;
                            if (Math.Abs(bowl) >= 1) front = bowl > 0 ? 1 : -1;
                        }
                        if (front != 0)
                        {
                            string backSide = (alongX ? "X" : "Y") + (front > 0 ? "0" : "1");
                            double back = mid - front * half;
                            var behind = WallBehind(o, walls, backSide);
                            bool onWall = behind != null && behind.Value.Side == backSide && Math.Abs(behind.Value.Face - back) <= 4;
                            double along = onWall ? behind.Value.Face + front * toiletFromWall : back + front * (toiletFromWall - 1);
                            return (new List<(double X, double Y)> { alongX ? (along, o.Cy) : (o.Cx, along) },
                                    onWall ? $"toilet's centre line, {toiletFromWall:0.#}\" from the wall behind its tank"
                                           : $"toilet's centre line, {toiletFromWall - 1:0.#}\" from the back of its tank");
                        }
                        // no bowl to tell the front (wall-hung, a simplified block): the nearer wall at an end of its long side
                        var ends = alongX ? new[] { "X0", "X1" } : new[] { "Y0", "Y1" };
                        var w = ends.Select(e => WallBehind(o, walls, e)).Where(x => x != null && ends.Contains(x.Value.Side))
                                    .OrderBy(x => Math.Abs(x.Value.Face - (x.Value.Side == "X0" ? o.X0 : x.Value.Side == "X1" ? o.X1 : x.Value.Side == "Y0" ? o.Y0 : o.Y1)))
                                    .FirstOrDefault();
                        if (w == null) return (null, null);
                        var (side, face, _) = w.Value;
                        int outward = side == "X0" || side == "Y0" ? -1 : 1;
                        var p = side[0] == 'X' ? (face - outward * toiletFromWall, o.Cy) : (o.Cx, face - outward * toiletFromWall);
                        return (new List<(double X, double Y)> { p }, $"toilet's centre line, {toiletFromWall:0.#}\" from the wall behind it");
                    }
                default:
                    {
                        // sinks, lavatories, washers: into the wall behind; the faucet (handles and spout drawn as small
                        // circles, hexagons or arcs, off the basin's middle) is on that wall's side
                        string prefer = Faucet(o);
                        var w = WallBehind(o, walls, prefer);
                        if (w == null && prefer != null)
                        {
                            // an island sink (no wall behind it): the sleeve at its faucet side, on its centre line (office sets)
                            var edge = prefer == "X0" ? (o.X0, o.Cy) : prefer == "X1" ? (o.X1, o.Cy) : prefer == "Y0" ? (o.Cx, o.Y0) : (o.Cx, o.Y1);
                            return (new List<(double X, double Y)> { edge }, "no wall behind it (island): at its faucet side, on its centre line");
                        }
                        if (w == null) return (null, null);
                        var (side, _, centre) = w.Value;
                        var p = side[0] == 'X' ? (centre, o.Cy) : (o.Cx, centre);
                        return (new List<(double X, double Y)> { p }, "wall behind the fixture, on its centre line" + (prefer != null ? " (faucet side)" : ""));
                    }
            }
        }

        /// <summary>
        /// The side of the object against a wall: the side's name (X0 = low X edge...), the wall face and the wall's centre
        /// line (between its two faces, else 2.5" in). <paramref name="prefer"/> = only that side (a faucet's).
        /// </summary>
        private static (string Side, double Face, double Centre)? WallBehind(Obj o, IList<((double X, double Y) A, (double X, double Y) B)> walls, string prefer)
        {
            (double D, string Side, double Face, double Centre)? best = null;
            foreach (var side in prefer != null ? new[] { prefer } : new[] { "X0", "X1", "Y0", "Y1" })
            {
                bool horizontal = side[0] == 'Y';                       // a Y side is a horizontal edge: horizontal walls
                double edge = side == "X0" ? o.X0 : side == "X1" ? o.X1 : side == "Y0" ? o.Y0 : o.Y1;
                int outward = side.EndsWith("0") ? -1 : 1;
                double lo = horizontal ? o.X0 : o.Y0, hi = horizontal ? o.X1 : o.Y1;
                var faces = new List<(double D, double C)>();
                foreach (var (a, b) in walls)
                {
                    double c, s0, s1;
                    if (horizontal && Math.Abs(a.Y - b.Y) < 0.3) { c = a.Y; s0 = Math.Min(a.X, b.X); s1 = Math.Max(a.X, b.X); }
                    else if (!horizontal && Math.Abs(a.X - b.X) < 0.3) { c = a.X; s0 = Math.Min(a.Y, b.Y); s1 = Math.Max(a.Y, b.Y); }
                    else continue;
                    if (s1 < lo + 2 || s0 > hi - 2) continue;              // the wall runs along this side
                    double d = (c - edge) * outward;
                    if (d >= -1 && d <= 12) faces.Add((d, c));
                }
                if (faces.Count == 0) continue;
                var near = faces.OrderBy(t => t.D).First();
                var far = faces.Where(t => (t.C - near.C) * outward >= 2 && (t.C - near.C) * outward <= 12).OrderBy(t => t.D).Select(t => (double?)t.C).FirstOrDefault();
                double centre = far.HasValue ? (near.C + far.Value) / 2 : near.C + outward * 2.5;
                if (best == null || near.D < best.Value.D) best = (near.D, side, near.C, centre);
            }
            if (best == null && prefer != null) return WallBehind(o, walls, null);
            return best == null ? ((string, double, double)?)null : (best.Value.Side, best.Value.Face, best.Value.Centre);
        }

        /// <summary>
        /// Fixture drawings: a block's strokes are one drawing; loose strokes join the drawing they touch (or the block
        /// they lie in), grown biggest first but never past maxObject inches (a kitchen's counter lines stay apart from
        /// its sink basins). Strokes bigger than that (counters, room outlines) are left out.
        /// </summary>
        private static List<Obj> Objects(IList<Stroke> strokes, double maxObject)
        {
            var objects = new List<Obj>();
            foreach (var s in strokes.Where(s => Math.Max(s.X1 - s.X0, s.Y1 - s.Y0) <= maxObject).OrderByDescending(s => (s.X1 - s.X0) * (s.Y1 - s.Y0)))
            {
                var host = s.Group != 0
                    ? objects.FirstOrDefault(o => o.Group == s.Group)
                    : objects.FirstOrDefault(o => (o.Group == 0 ? Touch(o, s) : Within(o, s)) &&
                        Math.Max(Math.Max(o.X1, s.X1) - Math.Min(o.X0, s.X0), Math.Max(o.Y1, s.Y1) - Math.Min(o.Y0, s.Y0)) <= maxObject);
                if (host == null) objects.Add(host = new Obj { X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1, Group = s.Group });
                else { host.X0 = Math.Min(host.X0, s.X0); host.Y0 = Math.Min(host.Y0, s.Y0); host.X1 = Math.Max(host.X1, s.X1); host.Y1 = Math.Max(host.Y1, s.Y1); }
                if (s.Circle) host.Circles.Add(s);
                host.Strokes.Add(s);
                if (s.Kitchen) host.Kitchen = true;
                if (s.Curved && !s.Circle)              // a bowl or a tub's outline; round circles are drains and faucet handles
                {
                    double a = Math.Max(1, (s.X1 - s.X0) * (s.Y1 - s.Y0));
                    host.Curved++; host.CurvedW += a; host.CurvedSx += a * (s.X0 + s.X1) / 2; host.CurvedSy += a * (s.Y0 + s.Y1) / 2;
                    if (s.Closed) host.ClosedCurved++;
                    if (Math.Max(s.X1 - s.X0, s.Y1 - s.Y0) <= 6) host.Fittings++;
                    if ((s.X1 - s.X0) * (s.Y1 - s.Y0) > host.MaxCurvedW * host.MaxCurvedL) { host.MaxCurvedW = s.X1 - s.X0; host.MaxCurvedL = s.Y1 - s.Y0; }
                }
                if (s.Diagonal) host.Diagonals++;
            }
            return objects;
        }

        /// <summary>
        /// The basins drawn in vanities, counters and sinks: closed outlines 10-24" x 11-30" that are curved (an oval or
        /// round basin), hold a drain circle (a square sink) or are a rectangle drawn inside a vanity's closed outline
        /// (a rectangular basin), each on its own (the outer rim of a basin drawn twice is one). Never a toilet's bowl,
        /// nor anything inside a tub or shower.
        /// </summary>
        private static List<Obj> Basins(IList<Stroke> strokes, List<Obj> objects)
        {
            var taken = objects.Where(o => { var k = Kind(o); return k == "WC" || k == "BT" || k == "SH"; }).ToList();
            var small = strokes.Where(s => s.Circle && Math.Max(s.X1 - s.X0, s.Y1 - s.Y0) <= 3).ToList();
            // the vanity tops and counters a rectangular basin can sit in: closed, straight, up to 30" deep and 6 ft long
            var tops = strokes.Where(r => r.Closed && !r.Curved && Math.Min(r.X1 - r.X0, r.Y1 - r.Y0) >= 14 && Math.Min(r.X1 - r.X0, r.Y1 - r.Y0) <= 30 &&
                                          Math.Max(r.X1 - r.X0, r.Y1 - r.Y0) <= 72).ToList();
            bool Rimmed(Stroke s) => !s.Curved && tops.Any(r => r != s && (r.Group == 0 || r.Group == s.Group) &&
                                                                r.X0 <= s.X0 - 1 && r.Y0 <= s.Y0 - 1 && r.X1 >= s.X1 + 1 && r.Y1 >= s.Y1 + 1);
            var found = strokes
                .Where(s => s.Closed)
                .Where(s =>
                {
                    double a = Math.Min(s.X1 - s.X0, s.Y1 - s.Y0), b = Math.Max(s.X1 - s.X0, s.Y1 - s.Y0);
                    return a >= 10 && a <= 24 && b >= 11 && b <= 30;
                })
                .Where(s => s.Curved || small.Any(c => (c.X0 + c.X1) / 2 > s.X0 && (c.X0 + c.X1) / 2 < s.X1 && (c.Y0 + c.Y1) / 2 > s.Y0 && (c.Y0 + c.Y1) / 2 < s.Y1) || Rimmed(s))
                // a cooktop's outline holds its burners, a basin never does
                .Where(s => Burners(strokes.Where(c => c.Circle && (c.X0 + c.X1) / 2 > s.X0 && (c.X0 + c.X1) / 2 < s.X1 && (c.Y0 + c.Y1) / 2 > s.Y0 && (c.Y0 + c.Y1) / 2 < s.Y1)) < 2)
                .Where(s => !taken.Any(o => (s.X0 + s.X1) / 2 >= o.X0 && (s.X0 + s.X1) / 2 <= o.X1 && (s.Y0 + s.Y1) / 2 >= o.Y0 && (s.Y0 + s.Y1) / 2 <= o.Y1))
                .OrderByDescending(s => (s.X1 - s.X0) * (s.Y1 - s.Y0)).ToList();
            var basins = new List<Obj>();
            foreach (var s in found)
            {
                if (basins.Any(b => (s.X0 + s.X1) / 2 >= b.X0 && (s.X0 + s.X1) / 2 <= b.X1 && (s.Y0 + s.Y1) / 2 >= b.Y0 && (s.Y0 + s.Y1) / 2 <= b.Y1)) continue;
                var basin = new Obj { X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1, Basin = true, Curved = s.Curved ? 1 : 0, Kitchen = s.Kitchen };
                // the small strokes around it: its faucet's handles and spout (they tell the wall it drains into)
                basin.Strokes.AddRange(strokes.Where(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 4 &&
                                                          (c.X0 + c.X1) / 2 >= s.X0 - 6 && (c.X0 + c.X1) / 2 <= s.X1 + 6 &&
                                                          (c.Y0 + c.Y1) / 2 >= s.Y0 - 6 && (c.Y0 + c.Y1) / 2 <= s.Y1 + 6));
                // its drain and the faucet behind it (the faucet tells which wall it drains into)
                basin.Circles.AddRange(strokes.Where(c => c.Circle && Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 3 &&
                                                          (c.X0 + c.X1) / 2 >= s.X0 - 6 && (c.X0 + c.X1) / 2 <= s.X1 + 6 &&
                                                          (c.Y0 + c.Y1) / 2 >= s.Y0 - 6 && (c.Y0 + c.Y1) / 2 <= s.Y1 + 6));
                basins.Add(basin);
            }
            return basins;
        }

        /// <summary>Round burners of a cooktop or range: circles 5-11" across.</summary>
        private static int Burners(IEnumerable<Stroke> circles) =>
            circles.Count(c => { double d = Math.Max(c.X1 - c.X0, c.Y1 - c.Y0); return d >= 5 && d <= 11; });

        /// <summary>
        /// The side of the drawing its faucet is on (X0 = low X edge...): the small strokes (up to 4": handles, spout,
        /// drawn as circles, hexagons or arcs) away from the basin's middle, where the drain is. Null when there are none
        /// or they sit in the middle.
        /// </summary>
        private static string Faucet(Obj o)
        {
            var bits = o.Strokes.Where(s => Math.Max(s.X1 - s.X0, s.Y1 - s.Y0) <= 4 &&
                                            !(Math.Abs((s.X0 + s.X1) / 2 - o.Cx) < 0.2 * o.W && Math.Abs((s.Y0 + s.Y1) / 2 - o.Cy) < 0.2 * o.L)).ToList();
            if (bits.Count == 0) return null;
            double fx = bits.Average(c => (c.X0 + c.X1) / 2), fy = bits.Average(c => (c.Y0 + c.Y1) / 2);
            var (side, gap) = new[] { ("X0", (fx - o.X0) / Math.Max(1, o.W)), ("X1", (o.X1 - fx) / Math.Max(1, o.W)),
                                      ("Y0", (fy - o.Y0) / Math.Max(1, o.L)), ("Y1", (o.Y1 - fy) / Math.Max(1, o.L)) }.OrderBy(t => t.Item2).First();
            return gap <= 0.35 ? side : null;
        }

        /// <summary>
        /// A "shower" known only by its corner-to-corner X (no drain drawn) with no wall along any side: some other box with
        /// an X (a skylight, a hatch, an equipment pad), not a shower.
        /// </summary>
        private static bool LooseShower(string code, Obj o, IList<((double X, double Y) A, (double X, double Y) B)> walls)
        {
            if (code != "SH" || walls.Count == 0) return false;
            bool drain = o.Circles.Any(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 6 &&
                                            Math.Abs((c.X0 + c.X1) / 2 - o.Cx) <= 8 && Math.Abs((c.Y0 + c.Y1) / 2 - o.Cy) <= 8);
            return !drain && WallBehind(o, walls, null) == null;
        }

        /// <summary>The middle of <paramref name="inner"/> is in <paramref name="outer"/>'s box (and they are not the same drawing).</summary>
        private static bool Inside(Obj inner, Obj outer) =>
            inner != outer && inner.Cx >= outer.X0 && inner.Cx <= outer.X1 && inner.Cy >= outer.Y0 && inner.Cy <= outer.Y1;

        private static bool Touch(Obj o, Stroke s, double g = 0.6) => o.X0 - g <= s.X1 && s.X0 - g <= o.X1 && o.Y0 - g <= s.Y1 && s.Y0 - g <= o.Y1;

        /// <summary>The stroke lies in the object's box (a drain circle drawn over a tub block).</summary>
        private static bool Within(Obj o, Stroke s, double g = 0.6) => o.X0 - g <= s.X0 && s.X1 <= o.X1 + g && o.Y0 - g <= s.Y0 && s.Y1 <= o.Y1 + g;

        private static double Gap(Obj o, double x, double y) =>
            Math.Sqrt(Math.Pow(Math.Max(Math.Max(o.X0 - x, 0), x - o.X1), 2) + Math.Pow(Math.Max(Math.Max(o.Y0 - y, 0), y - o.Y1), 2));
    }
}
