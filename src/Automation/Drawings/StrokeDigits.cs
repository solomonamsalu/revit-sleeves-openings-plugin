using System;
using System.Collections.Generic;
using System.Linq;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// Reads the number in a tag bubble that a PDF plots as lines (CAD text exported as strokes, no text to extract).
    /// CAD stroke fonts (simplex / romans) are the Hershey Simplex shapes: each character drawn is compared with those
    /// shapes, both stretched to the same box and drawn soft on a small grid, by correlation. A "P" above the bubble's
    /// divider is recognised and left out; the digits below it, left to right, are the number. Free of the Revit API.
    /// </summary>
    public static class StrokeDigits
    {
        private const int GW = 24, GH = 32;
        /// <summary>Correlation a character needs to be read, and its lead over the next best shape.</summary>
        private const double MinScore = 0.6, MinLead = 0.1;

        // Hershey Simplex, y down (top of a capital = -21, baseline = 0); one string per stroke
        private static readonly Dictionary<char, string[]> Shapes = new Dictionary<char, string[]>
        {
            ['0'] = new[] { "-1,-21 -4,-20 -6,-17 -7,-12 -7,-9 -6,-4 -4,-1 -1,0 1,0 4,-1 6,-4 7,-9 7,-12 6,-17 4,-20 1,-21 -1,-21" },
            ['1'] = new[] { "-4,-17 -2,-18 1,-21 1,0" },
            ['2'] = new[] { "-6,-16 -6,-17 -5,-19 -4,-20 -2,-21 2,-21 4,-20 5,-19 6,-17 6,-15 5,-13 3,-10 -7,0 7,0" },
            ['3'] = new[] { "-5,-21 6,-21 0,-13 3,-13 5,-12 6,-11 7,-8 7,-6 6,-3 4,-1 1,0 -2,0 -5,-1 -6,-2 -7,-4" },
            ['4'] = new[] { "3,-21 -7,-7 8,-7", "3,-21 3,0" },
            ['5'] = new[] { "5,-21 -5,-21 -6,-12 -5,-13 -2,-14 1,-14 4,-13 6,-11 7,-8 7,-6 6,-3 4,-1 1,0 -2,0 -5,-1 -6,-2 -7,-4" },
            ['6'] = new[] { "6,-18 5,-20 2,-21 0,-21 -3,-20 -5,-17 -6,-12 -6,-7 -5,-3 -3,-1 0,0 1,0 4,-1 6,-3 7,-6 7,-7 6,-10 4,-12 1,-13 0,-13 -3,-12 -5,-10 -6,-7" },
            ['7'] = new[] { "7,-21 -3,0", "-7,-21 7,-21" },
            ['8'] = new[] { "-2,-21 -5,-20 -6,-18 -6,-16 -5,-14 -3,-13 1,-12 4,-11 6,-9 7,-7 7,-4 6,-2 5,-1 2,0 -2,0 -5,-1 -6,-2 -7,-4 -7,-7 -6,-9 -4,-11 -1,-12 3,-13 5,-14 6,-16 6,-18 5,-20 2,-21 -2,-21" },
            ['9'] = new[] { "6,-14 5,-11 3,-9 0,-8 -1,-8 -4,-9 -6,-11 -7,-14 -7,-15 -6,-18 -4,-20 -1,-21 0,-21 3,-20 5,-18 6,-14 6,-9 5,-4 3,-1 0,0 -2,0 -5,-1 -6,-3" },
            ['P'] = new[] { "-7,-21 -7,0", "-7,-21 2,-21 5,-20 6,-19 7,-17 7,-14 6,-12 5,-11 2,-10 -7,-10" },
        };

        private static readonly Lazy<Dictionary<char, double[]>> Templates = new Lazy<Dictionary<char, double[]>>(() =>
            Shapes.ToDictionary(kv => kv.Key, kv => Raster(kv.Value.Select(s => s.Split(' ').Select(p =>
            {
                var xy = p.Split(',');
                return (X: double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), Y: double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
            }).ToList()).ToList())));

        /// <summary>
        /// The number drawn in a bubble, or null when it cannot be read with confidence.
        /// </summary>
        /// <param name="strokes">The strokes inside the bubble, relative to its centre (polylines).</param>
        /// <param name="yUp">True when the coordinates have y pointing up (PDF user space).</param>
        public static string Read(IList<List<(double X, double Y)>> strokes, bool yUp = true)
        {
            if (strokes == null || strokes.Count == 0) return null;
            var down = strokes.Where(s => s.Count >= 2).Select(s => s.Select(p => (p.X, Y: yUp ? -p.Y : p.Y)).ToList()).ToList();
            var digits = new List<(double Row, double X, char C)>();
            foreach (var ch in Characters(down))
            {
                var img = Raster(ch);
                var ranked = Templates.Value.Select(t => (C: t.Key, S: Score(img, t.Value))).OrderByDescending(t => t.S).ToList();
                if (ranked[0].S < MinScore || ranked[0].S - ranked[1].S < MinLead) return null;   // a character we cannot read: no guess
                if (char.IsDigit(ranked[0].C))
                    digits.Add((Math.Round(ch.SelectMany(s => s).Average(p => p.Y)), ch.SelectMany(s => s).Min(p => p.X), ranked[0].C));
            }
            if (digits.Count == 0) return null;
            // one line of digits (below the divider): left to right
            double row = digits.Max(d => d.Row);
            var line = digits.Where(d => Math.Abs(d.Row - row) <= Height(down) * 0.25).OrderBy(d => d.X);
            return new string(line.Select(d => d.C).ToArray());
        }

        private static double Height(List<List<(double X, double Y)>> strokes)
        {
            var pts = strokes.SelectMany(s => s).ToList();
            return pts.Max(p => p.Y) - pts.Min(p => p.Y);
        }

        /// <summary>Strokes grouped into characters: strokes on one line whose extents overlap side to side are one character (a "4" may be two strokes).</summary>
        private static List<List<List<(double X, double Y)>>> Characters(List<List<(double X, double Y)>> strokes)
        {
            var boxes = strokes.Select(s => (S: s, X0: s.Min(p => p.X), X1: s.Max(p => p.X), Y0: s.Min(p => p.Y), Y1: s.Max(p => p.Y))).ToList();
            var chars = new List<List<(List<(double X, double Y)> S, double X0, double X1, double Y0, double Y1)>>();
            foreach (var b in boxes)
            {
                var hit = chars.Where(c => c.Any(o => b.X0 <= o.X1 && o.X0 <= b.X1 && b.Y0 <= o.Y1 && o.Y0 <= b.Y1)).ToList();
                var into = hit.FirstOrDefault() ?? new List<(List<(double X, double Y)>, double, double, double, double)>();
                if (hit.Count == 0) chars.Add(into);
                foreach (var other in hit.Skip(1)) { into.AddRange(other); chars.Remove(other); }
                into.Add(b);
            }
            return chars.Select(c => c.Select(b => b.S).ToList()).ToList();
        }

        /// <summary>The character drawn soft on a GW x GH grid, stretched to the box (thin characters such as "1" keep their shape).</summary>
        private static double[] Raster(List<List<(double X, double Y)>> strokes)
        {
            var segs = new List<(double Ax, double Ay, double Bx, double By)>();
            foreach (var s in strokes) for (int i = 1; i < s.Count; i++) segs.Add((s[i - 1].X, s[i - 1].Y, s[i].X, s[i].Y));
            var img = new double[GW * GH];
            if (segs.Count == 0) return img;
            double x0 = segs.Min(g => Math.Min(g.Ax, g.Bx)), x1 = segs.Max(g => Math.Max(g.Ax, g.Bx));
            double y0 = segs.Min(g => Math.Min(g.Ay, g.By)), y1 = segs.Max(g => Math.Max(g.Ay, g.By));
            double w = x1 - x0, h = Math.Max(y1 - y0, 1e-6), sy = (GH - 6) / h, sx = w < 0.4 * h ? sy : (GW - 6) / w;
            double ox = GW / 2.0 - (x0 + x1) / 2 * sx, oy = GH / 2.0 - (y0 + y1) / 2 * sy;
            var scaled = segs.Select(g => (Ax: g.Ax * sx + ox, Ay: g.Ay * sy + oy, Bx: g.Bx * sx + ox, By: g.By * sy + oy)).ToList();
            for (int j = 0; j < GH; j++)
                for (int i = 0; i < GW; i++)
                {
                    double px = i + 0.5, py = j + 0.5, best = double.MaxValue;
                    foreach (var g in scaled)
                    {
                        double dx = g.Bx - g.Ax, dy = g.By - g.Ay, len = dx * dx + dy * dy;
                        double t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - g.Ax) * dx + (py - g.Ay) * dy) / len));
                        double ex = px - g.Ax - t * dx, ey = py - g.Ay - t * dy;
                        best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
                    }
                    img[j * GW + i] = Math.Max(0, 1 - best / 2);
                }
            return img;
        }

        private static double Score(double[] a, double[] b)
        {
            double ma = a.Average(), mb = b.Average(), num = 0, da = 0, db = 0;
            for (int i = 0; i < a.Length; i++) { double x = a[i] - ma, y = b[i] - mb; num += x * y; da += x * x; db += y * y; }
            return da == 0 || db == 0 ? 0 : num / Math.Sqrt(da * db);
        }
    }
}
