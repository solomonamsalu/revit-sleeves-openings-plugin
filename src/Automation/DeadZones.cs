using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// The wet wall's dead zone (Standards Manual p17; user 2026-10-12): every sleeve touching a wet wall goes into it, on
    /// the line through its middle; at two walls with a gap between them, centred between the two walls. From Revit walls
    /// (<see cref="WallGeometry.DeadZone"/>), else from the architect's DWG wall lines (plumbing.dwgWallLayers: each face
    /// is a line). Floor drains (toilet, tub, shower, floor drain) stay where they drain.
    /// </summary>
    public static class DeadZones
    {
        private static readonly HashSet<string> FloorDrains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WC", "BT", "SH", "FD" };
        private static readonly Regex WallHung = new Regex(@"wall.?(hung|mount)", RegexOptions.IgnoreCase);
        /// <summary>DWG face lines this close (feet) are one line (drawn twice).</summary>
        private const double SameLine = 0.25 / 12;
        /// <summary>A wall is at most this thick (feet): face lines further apart are not one wall.</summary>
        private const double MaxWall = 14.0 / 12;
        /// <summary>A strip between two lines this thin or less (feet) is board (gypsum, 5/8"), not a wall's core or a gap.</summary>
        private const double Board = 0.75 / 12;

        /// <summary>A fixture that drains where it stands (a toilet not wall-hung, tub, shower, floor drain).</summary>
        public static bool FloorDrain(string code, string name) =>
            code != null && FloorDrains.Contains(code) && !(name != null && WallHung.IsMatch(name));

        /// <summary>The dead zone a sleeve of radius r (feet) at p touches: Revit walls first, else the DWG's wall lines; null = none.</summary>
        public static WallGeometry.Zone Find(WallGeometry walls, IEnumerable<DwgFixtureReader.Plan> plans, XYZ p, double r, double maxGap)
        {
            var zone = walls?.DeadZone(p, r, maxGap);
            if (zone != null || plans == null) return zone;
            return FromLines(plans.SelectMany(pl => pl.Walls), p, r, maxGap);
        }

        /// <summary>
        /// From DWG wall face lines (model inches): the parallel lines that run past p (along them), grouped into walls by
        /// their spacing. A gap with exactly one wall (one core strip, plus its boards) on each side is the dead zone: its
        /// middle. Else the sleeve goes on the wall, never outside it: the middle of the core strip nearest it. The group the
        /// sleeve touches and moves least to reach.
        /// </summary>
        public static WallGeometry.Zone FromLines(IEnumerable<((double X, double Y) A, (double X, double Y) B)> lines, XYZ p, double r, double maxGap)
        {
            const double tol = 1.0 / 12, parallel = 0.05;
            double reach = 2 * MaxWall + maxGap;
            var near = new List<(XYZ Dir, double Off)>();
            foreach (var (a, b) in lines)
            {
                double ax = a.X / 12, ay = a.Y / 12, dx = b.X / 12 - ax, dy = b.Y / 12 - ay;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.5) continue;
                dx /= len; dy /= len;
                // one direction per line pair: pointing +X (or +Y when vertical)
                if (dx < -1e-9 || (Math.Abs(dx) <= 1e-9 && dy < 0)) { ax += dx * len; ay += dy * len; dx = -dx; dy = -dy; }
                double t = (p.X - ax) * dx + (p.Y - ay) * dy;
                if (t < 0 || t > len) continue;
                double off = (ax - p.X) * -dy + (ay - p.Y) * dx;           // the line's offset from p, across (normal = (-dy, dx))
                if (Math.Abs(off) <= reach) near.Add((new XYZ(dx, dy, 0), off));
            }

            WallGeometry.Zone best = null; double bestMove = double.MaxValue;
            var groups = new List<List<(XYZ Dir, double Off)>>();
            foreach (var l in near)
            {
                var g = groups.FirstOrDefault(x => Math.Abs(x[0].Dir.X * l.Dir.Y - x[0].Dir.Y * l.Dir.X) <= parallel);
                if (g == null) groups.Add(new List<(XYZ, double)> { l }); else g.Add(l);
            }
            foreach (var g in groups)
            {
                var dir = g[0].Dir;
                var offs = new List<double>();
                foreach (var o in g.Select(x => x.Off).OrderBy(x => x))
                    if (offs.Count == 0 || o - offs[offs.Count - 1] > SameLine) offs.Add(o);
                // runs of lines close together: one wall, or two walls and the gap between them
                var run = new List<double>();
                for (int i = 0; i <= offs.Count; i++)
                {
                    if (i < offs.Count && (run.Count == 0 || offs[i] - run[run.Count - 1] <= Math.Max(MaxWall, maxGap) + 1e-6)) { run.Add(offs[i]); continue; }
                    Consider(run, dir);
                    run = i < offs.Count ? new List<double> { offs[i] } : new List<double>();
                }
            }
            return best;

            void Consider(List<double> run, XYZ dir)
            {
                if (run.Count < 2) return;
                double lo = run[0], hi = run[run.Count - 1];
                if (lo > r + tol || hi < -r - tol) return;                   // the sleeve does not touch it (p is at 0)
                // the strips between the lines: board (a finish layer, under 3/4") or a core (studs, a gap, masonry). Two walls
                // with a dead zone between them = one core strip (the wall) on each side of the gap; anything more on a side is
                // not one wall (an exterior wall's furring, air space and masonry: 0.6 | 2.5 | 1 | 1 | 6 | 3 | 1 all filled)
                int Cores(int from, int to) { int c = 0; for (int j = from; j < to; j++) if (run[j + 1] - run[j] > Board) c++; return c; }
                int k = -1;
                for (int i = 0; i + 1 < run.Count; i++)
                {
                    double g = run[i + 1] - run[i];
                    if (g > maxGap + 1e-6 || run[i] - lo > MaxWall || hi - run[i + 1] > MaxWall) continue;
                    if (Cores(0, i) != 1 || Cores(i + 1, run.Count - 1) != 1) continue;
                    // a real gap first (the one nearest the sleeve); walls touching: their joint, the thinnest strip
                    if (k < 0) { k = i; continue; }
                    double gk = run[k + 1] - run[k];
                    if (g > Board ? gk <= Board || Math.Abs(run[i] + run[i + 1]) < Math.Abs(run[k] + run[k + 1]) : gk <= Board && g < gk) k = i;
                }
                double mid, half, gap = -1;
                string what;
                if (k >= 0)
                {
                    gap = run[k + 1] - run[k]; mid = (run[k] + run[k + 1]) / 2; half = gap / 2;
                    what = $"the {Math.Round(gap * 12, 1):0.#}\" gap between the two walls drawn in the DWG";
                    if (gap <= Board)
                    {
                        // walls touching: centred between their two cores (the boards between them)
                        int a = k, b = k + 1;
                        while (a > 0 && run[a] - run[a - 1] <= Board) a--;
                        while (b < run.Count - 1 && run[b + 1] - run[b] <= Board) b++;
                        gap = 0; mid = (run[a] + run[b]) / 2; half = (run[b] - run[a]) / 2;
                        what = "the joint between the two walls drawn in the DWG (touching, no gap)";
                    }
                }
                else
                {
                    // no dead zone: on the wall, never outside it - the middle of the core strip nearest the sleeve (a wall's
                    // studs; at an exterior wall its room-side furring)
                    int c = -1; double cd = double.MaxValue;
                    for (int i = 0; i + 1 < run.Count; i++)
                    {
                        if (run[i + 1] - run[i] <= Board) continue;
                        double d = run[i] > 0 ? run[i] : run[i + 1] < 0 ? -run[i + 1] : 0;
                        if (d < cd - 1e-9 || (Math.Abs(d - cd) <= 1e-9 && Math.Abs(run[i] + run[i + 1]) < Math.Abs(run[c] + run[c + 1]))) { cd = d; c = i; }
                    }
                    if (c < 0) { mid = (lo + hi) / 2; half = (hi - lo) / 2; }
                    else { mid = (run[c] + run[c + 1]) / 2; half = (run[c + 1] - run[c]) / 2; }
                    what = Cores(0, run.Count - 1) <= 1
                        ? $"the {Math.Round((hi - lo) * 12, 1):0.#}\" wall drawn in the DWG"
                        : $"the {Math.Round(half * 24, 1):0.#}\" layer of the {Math.Round((hi - lo) * 12, 1):0.#}\" wall drawn in the DWG (no gap in it)";
                }
                double move = Math.Abs(mid);
                if (move >= bestMove) return;
                var n = new XYZ(-dir.Y, dir.X, 0);
                best = new WallGeometry.Zone { Origin = new XYZ(p.X + n.X * mid, p.Y + n.Y * mid, 0), Normal = n, Half = half, Gap = gap, Where = what };
                bestMove = move;
            }
        }
    }
}
