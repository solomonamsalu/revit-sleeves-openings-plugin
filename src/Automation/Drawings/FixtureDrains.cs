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
    /// </summary>
    public static class FixtureDrains
    {
        /// <summary>One stroke of a fixture layer: its box; Circle = a closed round stroke (a tub drain, faucet handles).</summary>
        public class Stroke { public double X0, Y0, X1, Y1; public bool Circle; }

        private class Obj
        {
            public double X0, Y0, X1, Y1;
            public List<Stroke> Circles = new List<Stroke>();
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
                                  Func<string, (int Count, double Spacing)> sleeves, double search = 24, double toiletFromWall = 13, double maxObject = 72)
        {
            var objects = Objects(strokes, maxObject);
            var used = new HashSet<Obj>();
            // nearest label first, so two labels never take the same fixture
            var pairs = (from f in fixtures
                         where Sizes.ContainsKey(f.Code)
                         from o in objects
                         let s = Sizes[f.Code]
                         let shortSide = Math.Min(o.W, o.L)
                         let longSide = Math.Max(o.W, o.L)
                         where shortSide >= s[0] && shortSide <= s[1] && longSide >= s[2] && longSide <= s[3]
                         let g = Gap(o, f.X, f.Y)
                         where g <= search
                         orderby g
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
                        int n = Math.Max(1, sleeve.Count);
                        var pts = Enumerable.Range(0, n).Select(i => (i - (n - 1) / 2.0) * sleeve.Spacing)
                                            .Select(a => alongX ? (dx + a, dy) : (dx, dy + a)).ToList();
                        return (pts, $"tub's drawn drain circle{(n > 1 ? $", {n} sleeves {sleeve.Spacing:0.#}\" c-c along the tub" : "")}");
                    }
                case "SH":
                case "FD":
                    return (new List<(double X, double Y)> { (o.Cx, o.Cy) }, code == "SH" ? "shower's centre (point drain)" : "drain's centre");
                case "WC":
                    {
                        var w = WallBehind(o, walls, null);
                        if (w == null) return (null, null);
                        var (side, face, _) = w.Value;
                        int outward = side == "X0" || side == "Y0" ? -1 : 1;
                        var p = side[0] == 'X' ? (face - outward * toiletFromWall, o.Cy) : (o.Cx, face - outward * toiletFromWall);
                        return (new List<(double X, double Y)> { p }, $"toilet's centre line, {toiletFromWall:0.#}\" from the wall behind it");
                    }
                default:
                    {
                        // sinks, lavatories, washers: into the wall behind; a drawn faucet (small circles) is on that wall's side
                        var faucet = o.Circles.Where(c => Math.Max(c.X1 - c.X0, c.Y1 - c.Y0) <= 3).ToList();
                        string prefer = null;
                        if (faucet.Count > 0)
                        {
                            double fx = faucet.Average(c => (c.X0 + c.X1) / 2), fy = faucet.Average(c => (c.Y0 + c.Y1) / 2);
                            prefer = new[] { ("X0", fx - o.X0), ("X1", o.X1 - fx), ("Y0", fy - o.Y0), ("Y1", o.Y1 - fy) }.OrderBy(t => t.Item2).First().Item1;
                        }
                        var w = WallBehind(o, walls, prefer);
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
        /// Fixture drawings: strokes whose boxes touch, grown biggest first but never past maxObject inches (a kitchen's
        /// counter lines stay apart from its sink basins). Strokes bigger than that (counters, room outlines) are left out.
        /// </summary>
        private static List<Obj> Objects(IList<Stroke> strokes, double maxObject)
        {
            var objects = new List<Obj>();
            foreach (var s in strokes.Where(s => Math.Max(s.X1 - s.X0, s.Y1 - s.Y0) <= maxObject).OrderByDescending(s => (s.X1 - s.X0) * (s.Y1 - s.Y0)))
            {
                var host = objects.FirstOrDefault(o => Touch(o, s) &&
                    Math.Max(Math.Max(o.X1, s.X1) - Math.Min(o.X0, s.X0), Math.Max(o.Y1, s.Y1) - Math.Min(o.Y0, s.Y0)) <= maxObject);
                if (host == null) objects.Add(host = new Obj { X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1 });
                else { host.X0 = Math.Min(host.X0, s.X0); host.Y0 = Math.Min(host.Y0, s.Y0); host.X1 = Math.Max(host.X1, s.X1); host.Y1 = Math.Max(host.Y1, s.Y1); }
                if (s.Circle) host.Circles.Add(s);
            }
            return objects;
        }

        private static bool Touch(Obj o, Stroke s, double g = 0.6) => o.X0 - g <= s.X1 && s.X0 - g <= o.X1 && o.Y0 - g <= s.Y1 && s.Y0 - g <= o.Y1;

        private static double Gap(Obj o, double x, double y) =>
            Math.Sqrt(Math.Pow(Math.Max(Math.Max(o.X0 - x, 0), x - o.X1), 2) + Math.Pow(Math.Max(Math.Max(o.Y0 - y, 0), y - o.Y1), 2));
    }
}
