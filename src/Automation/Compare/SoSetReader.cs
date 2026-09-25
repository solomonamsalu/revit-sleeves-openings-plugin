using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Drawings;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Util;

namespace SleevesOpenings.Automation.Compare
{
    /// <summary>One HVAC opening drawn on a sheet of the office's Sleeves &amp; Openings set.</summary>
    public class SoOpening
    {
        public string Floor;
        public int Page;
        /// <summary>Centre in Revit (feet), set once the sheet is lined up with the Revit grids.</summary>
        public double X, Y;
        /// <summary>Drawn size (inches): the two sides of the rectangle, the first one nearer east-west.</summary>
        public double Width, Length;
        public List<string> Tags = new List<string>();
        /// <summary>Round ducts drawn inside it (a shaft opening for several ducts).</summary>
        public bool HasCircles;
        /// <summary>Drawn inside a bigger opening of the set.</summary>
        public SoOpening Inside;
        internal double[] Corners;           // PDF points: x0,y0, x1,y1, x2,y2, x3,y3
        internal double Cx, Cy;              // PDF points

        public string Name => Tags.Count == 0 ? "(no tag)" : string.Join(" + ", Tags);
        public string SizeText => $"{Units.FormatInches(Math.Round(Width))} x {Units.FormatInches(Math.Round(Length))}";
    }

    /// <summary>One floor sheet of the set and how it lines up with Revit.</summary>
    public class SoSheet
    {
        public int Page;
        public string Floor, Title;
        public int Openings;
        /// <summary>PDF points per foot and shift: pdf = Scale × revit + (Tx, Ty).</summary>
        public double Scale, Tx, Ty;
        public int Grids;
        public double Residual;              // worst grid bubble, inches
        public bool Ok;
        public string Problem;

        public (double X, double Y) ToRevit(double px, double py) => ((px - Tx) / Scale, (py - Ty) / Scale);
    }

    public class SoSetResult
    {
        public string Path;
        public List<SoSheet> Sheets = new List<SoSheet>();
        public List<SoOpening> Openings = new List<SoOpening>();
        public List<string> Warnings = new List<string>();

        public SoSheet For(string floor) => Sheets.FirstOrDefault(s => s.Floor == floor && s.Ok);
    }

    /// <summary>
    /// Reads the office's Sleeves &amp; Openings set (a PDF plotted from Revit, one sheet per floor): the HVAC openings are
    /// the rectangles drawn in the colour the sheet legend gives "HVAC OPENINGS" (an X across, or a shaft outline), their
    /// tags the small text inside them. Each sheet is lined up with Revit by its grid bubbles (names matching the Revit
    /// grids), so every opening gets a Revit position. Free of the Revit API.
    /// </summary>
    public static class SoSetReader
    {
        private static readonly Regex Tag = new Regex(@"^[A-Za-z][A-Za-z0-9]*(-[A-Za-z0-9]+)*$");

        private class Seg { public double X1, Y1, X2, Y2; public double Len => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)); }

        public static SoSetResult Read(string path, IList<GridLine> revitGrids, ReferenceSetRules cfg)
        {
            cfg = cfg ?? new ReferenceSetRules();
            var result = new SoSetResult { Path = path };
            using (var pdf = PdfDocument.Open(path))
            {
                foreach (var page in pdf.GetPages())
                {
                    var words = page.GetWords().Where(w => w.Letters.Count > 0).ToList();
                    var lines = Lines(words);
                    var sheet = new SoSheet { Page = page.Number };

                    // the floor: the biggest text naming exactly one floor ("5TH FLOOR", "ROOF")
                    var titled = lines.Where(l => l.Text.Length <= 40 && FloorKey.Find(l.Text).Count == 1).ToList();
                    if (titled.Count > 0)
                    {
                        double top = titled.Max(l => l.Size);
                        var floors = titled.Where(l => l.Size >= top - 0.5).Select(l => FloorKey.Find(l.Text)[0]).Distinct().ToList();
                        if (floors.Count == 1 && top >= 20) { sheet.Floor = floors[0]; sheet.Title = titled.First(l => l.Size >= top - 0.5).Text; }
                    }
                    if (sheet.Floor == null) continue;                  // cover sheet, notes, details
                    result.Sheets.Add(sheet);

                    // the colour of the openings: the swatch on the legend row
                    var legend = lines.FirstOrDefault(l => Regex.IsMatch(l.Text, "^" + Regex.Escape(cfg.LegendText) + "$", RegexOptions.IgnoreCase));
                    if (legend == null) { sheet.Problem = $"no '{cfg.LegendText}' in the legend"; continue; }
                    var colour = Swatch(page, legend);
                    if (colour == null) { sheet.Problem = $"no colour next to '{cfg.LegendText}' in the legend"; continue; }

                    var segs = new List<Seg>();
                    var curves = new List<(double X, double Y)>();
                    foreach (var p in page.Paths)
                    {
                        if (!p.IsStroked || !Same(p.StrokeColor, colour.Value)) continue;
                        foreach (var sub in p) Segments(sub, segs, curves);
                    }
                    // the legend swatch itself
                    segs.RemoveAll(s => Math.Min(s.X1, s.X2) >= legend.Left - 20 && Math.Abs((s.Y1 + s.Y2) / 2 - legend.Y) <= 25);

                    var openings = Rectangles(segs);
                    foreach (var o in openings)
                    {
                        o.Floor = sheet.Floor; o.Page = sheet.Page;
                        o.HasCircles = curves.Any(c => Inside(o, c.X, c.Y, 0));
                        o.Inside = openings.Where(b => b != o && Area(b) > Area(o) * 1.2 && Inside(b, o.Cx, o.Cy, 1)).OrderBy(Area).FirstOrDefault();
                    }
                    // tags: small text inside an opening (the smallest one holding it; one just touching it only when none does).
                    // Tags are often turned with the opening: turned letters go through the layout-aware extractor (the default
                    // one splits them), level ones through the default extractor (the other one joins neighbouring tags).
                    var small = page.Letters.Where(l => l.PointSize <= cfg.MaxTagSize).ToList();
                    var tagWords = DefaultWordExtractor.Instance.GetWords(small.Where(l => l.TextOrientation == TextOrientation.Horizontal).ToList())
                        .Concat(NearestNeighbourWordExtractor.Instance.GetWords(small.Where(l => l.TextOrientation != TextOrientation.Horizontal).ToList()));
                    foreach (var w in tagWords.Where(w => Tag.IsMatch(w.Text.Trim())))
                    {
                        var c = w.BoundingBox.Centroid;
                        var host = openings.Where(o => Inside(o, c.X, c.Y, 0)).OrderBy(Area).FirstOrDefault()
                                   ?? openings.Where(o => Inside(o, c.X, c.Y, 3)).OrderBy(Area).FirstOrDefault();
                        var text = w.Text.Trim().ToUpperInvariant();
                        if (host != null && !host.Tags.Contains(text)) host.Tags.Add(text);
                    }
                    // untagged rectangles inside a bigger opening are details of it (ducts drawn in a shaft), not openings
                    openings.RemoveAll(o => o.Inside != null && o.Tags.Count == 0);
                    foreach (var o in openings.Where(o => o.Inside != null && !openings.Contains(o.Inside))) o.Inside = null;
                    sheet.Openings = openings.Count;

                    Fit(sheet, words, revitGrids, cfg);
                    if (sheet.Ok)
                        foreach (var o in openings)
                        {
                            (o.X, o.Y) = sheet.ToRevit(o.Cx, o.Cy);
                            double a = Math.Sqrt(Math.Pow(o.Corners[2] - o.Corners[0], 2) + Math.Pow(o.Corners[3] - o.Corners[1], 2)) / sheet.Scale * 12;
                            double b = Math.Sqrt(Math.Pow(o.Corners[6] - o.Corners[0], 2) + Math.Pow(o.Corners[7] - o.Corners[1], 2)) / sheet.Scale * 12;
                            bool firstAlongX = Math.Abs(o.Corners[2] - o.Corners[0]) >= Math.Abs(o.Corners[3] - o.Corners[1]);
                            (o.Width, o.Length) = firstAlongX ? (a, b) : (b, a);
                        }
                    result.Openings.AddRange(openings);
                }
            }
            foreach (var dup in result.Sheets.GroupBy(s => s.Floor).Where(g => g.Count() > 1))
                result.Warnings.Add($"{FloorKey.Describe(dup.Key)} is on pages {string.Join(", ", dup.Select(s => s.Page))}; all are compared.");
            return result;
        }

        // ---------------------------------------------------------------- page content

        private class Line { public string Text; public double Size, Left, Y; }

        private static List<Line> Lines(List<Word> words) =>
            words.Where(w => w.TextOrientation == TextOrientation.Horizontal)
                 .GroupBy(w => (Math.Round(w.BoundingBox.Bottom), Math.Round(w.Letters[0].PointSize, 1)))
                 .SelectMany(g =>
                 {
                     // words on one baseline far apart are separate texts (the two copies of a title, legend columns)
                     var list = new List<Line>();
                     Line cur = null; double right = 0;
                     foreach (var w in g.OrderBy(w => w.BoundingBox.Left))
                     {
                         if (cur == null || w.BoundingBox.Left - right > g.Key.Item2 * 2)
                         {
                             cur = new Line { Text = w.Text, Size = g.Key.Item2, Left = w.BoundingBox.Left, Y = w.BoundingBox.Centroid.Y };
                             list.Add(cur);
                         }
                         else cur.Text += " " + w.Text;
                         right = w.BoundingBox.Right;
                     }
                     return list;
                 })
                 .ToList();

        /// <summary>The colour of the stroke (or fill) drawn right of the legend text, on its row; black/grey/white are frames.</summary>
        private static (double R, double G, double B)? Swatch(Page page, Line legend)
        {
            (double, double, double)? best = null; double bestDx = double.MaxValue;
            foreach (var p in page.Paths)
            {
                var box = p.GetBoundingRectangle();
                if (box == null) continue;
                var r = box.Value;
                if (r.Left < legend.Left + 20 || r.Left > legend.Left + 400) continue;
                if (Math.Abs(r.Centroid.Y - legend.Y) > 12) continue;
                foreach (var (on, color) in new[] { (p.IsStroked, p.StrokeColor), (p.IsFilled, p.FillColor) })
                {
                    if (!on || color == null) continue;
                    var c = color.ToRGBValues();
                    if (Math.Abs(c.r - c.g) < 0.05 && Math.Abs(c.g - c.b) < 0.05) continue;        // black, grey, white
                    if (r.Left - legend.Left < bestDx) { best = (c.r, c.g, c.b); bestDx = r.Left - legend.Left; }
                    break;
                }
            }
            return best;
        }

        private static bool Same(IColor color, (double R, double G, double B) c)
        {
            if (color == null) return false;
            var v = color.ToRGBValues();
            return Math.Abs(v.r - c.R) < 0.03 && Math.Abs(v.g - c.G) < 0.03 && Math.Abs(v.b - c.B) < 0.03;
        }

        private static void Segments(PdfSubpath sub, List<Seg> segs, List<(double, double)> curves)
        {
            PdfPoint? start = null, cur = null;
            foreach (var cmd in sub.Commands)
            {
                switch (cmd)
                {
                    case PdfSubpath.Move m: start = cur = m.Location; break;
                    case PdfSubpath.Line l:
                        segs.Add(new Seg { X1 = l.From.X, Y1 = l.From.Y, X2 = l.To.X, Y2 = l.To.Y });
                        cur = l.To; if (start == null) start = l.From;
                        break;
                    case PdfSubpath.BezierCurve b:
                        curves.Add(((b.StartPoint.X + b.EndPoint.X) / 2, (b.StartPoint.Y + b.EndPoint.Y) / 2));
                        cur = b.EndPoint;
                        break;
                    case PdfSubpath.Close _:
                        if (start != null && cur != null && (start.Value.X != cur.Value.X || start.Value.Y != cur.Value.Y))
                            segs.Add(new Seg { X1 = cur.Value.X, Y1 = cur.Value.Y, X2 = start.Value.X, Y2 = start.Value.Y });
                        cur = start;
                        break;
                }
            }
        }

        // ---------------------------------------------------------------- rectangles

        /// <summary>
        /// Openings: two diagonals that cross at their middles with the same length (the X of an opening symbol, at any
        /// angle), and axis-aligned rectangles closed by four edges (a shaft outline with no X). One per spot and size.
        /// </summary>
        private static List<SoOpening> Rectangles(List<Seg> segs)
        {
            var found = new List<SoOpening>();
            const double tol = 0.6;
            var diag = segs.Where(s => s.Len > 2 && Math.Abs(s.X2 - s.X1) > 1 && Math.Abs(s.Y2 - s.Y1) > 1).ToList();
            for (int i = 0; i < diag.Count; i++)
                for (int j = i + 1; j < diag.Count; j++)
                {
                    Seg a = diag[i], b = diag[j];
                    if (Math.Abs(a.Len - b.Len) > tol * 2) continue;
                    double ax = (a.X1 + a.X2) / 2, ay = (a.Y1 + a.Y2) / 2, bx = (b.X1 + b.X2) / 2, by = (b.Y1 + b.Y2) / 2;
                    if (Math.Abs(ax - bx) > tol || Math.Abs(ay - by) > tol) continue;
                    double cross = ((a.X2 - a.X1) * (b.Y2 - b.Y1) - (a.Y2 - a.Y1) * (b.X2 - b.X1)) / (a.Len * b.Len);
                    if (Math.Abs(cross) < 0.2) continue;                                  // parallel: not an X
                    Add(found, new[] { a.X1, a.Y1, b.X1, b.Y1, a.X2, a.Y2, b.X2, b.Y2 });
                }

            // shaft outlines: two horizontal edges with the same span closed by two vertical ones
            var h = segs.Where(s => Math.Abs(s.Y2 - s.Y1) <= 0.3 && Math.Abs(s.X2 - s.X1) > 2).Select(s => (L: Math.Min(s.X1, s.X2), R: Math.Max(s.X1, s.X2), Y: s.Y1)).ToList();
            var v = segs.Where(s => Math.Abs(s.X2 - s.X1) <= 0.3 && Math.Abs(s.Y2 - s.Y1) > 2).Select(s => (B: Math.Min(s.Y1, s.Y2), T: Math.Max(s.Y1, s.Y2), X: s.X1)).ToList();
            for (int i = 0; i < h.Count; i++)
                for (int j = 0; j < h.Count; j++)
                {
                    var lo = h[i]; var hi = h[j];
                    if (hi.Y - lo.Y < 2 || Math.Abs(lo.L - hi.L) > tol || Math.Abs(lo.R - hi.R) > tol) continue;
                    bool Side(double x) => v.Any(s => Math.Abs(s.X - x) <= tol && s.B <= lo.Y + tol && s.T >= hi.Y - tol);
                    if (!Side(lo.L) || !Side(lo.R)) continue;
                    Add(found, new[] { lo.L, lo.Y, lo.R, lo.Y, lo.R, hi.Y, lo.L, hi.Y });
                }
            return found;
        }

        private static void Add(List<SoOpening> found, double[] c)
        {
            double cx = (c[0] + c[2] + c[4] + c[6]) / 4, cy = (c[1] + c[3] + c[5] + c[7]) / 4;
            var o = new SoOpening { Corners = c, Cx = cx, Cy = cy };
            if (found.Any(f => Math.Abs(f.Cx - cx) < 0.8 && Math.Abs(f.Cy - cy) < 0.8 && Math.Abs(Area(f) - Area(o)) < Math.Max(2, Area(o) * 0.05))) return;
            found.Add(o);
        }

        private static double Area(SoOpening o)
        {
            var c = o.Corners;
            return Math.Abs((c[2] - c[0]) * (c[7] - c[1]) - (c[3] - c[1]) * (c[6] - c[0]));
        }

        /// <summary>Point inside the (possibly turned) rectangle, grown by <paramref name="margin"/> points.</summary>
        private static bool Inside(SoOpening o, double x, double y, double margin)
        {
            var c = o.Corners;
            double ux = c[2] - c[0], uy = c[3] - c[1], vx = c[6] - c[0], vy = c[7] - c[1];
            double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
            if (lu < 1e-6 || lv < 1e-6) return false;
            double pu = ((x - c[0]) * ux + (y - c[1]) * uy) / lu, pv = ((x - c[0]) * vx + (y - c[1]) * vy) / lv;
            return pu >= -margin && pu <= lu + margin && pv >= -margin && pv <= lv + margin;
        }

        // ---------------------------------------------------------------- grids

        /// <summary>
        /// Lines the sheet up with Revit: every grid bubble (the text of a Revit grid's name, in the size most of them
        /// share) gives the grid's X (a north-south grid) or Y (east-west). One scale and shift for all, north up.
        /// </summary>
        private static void Fit(SoSheet sheet, List<Word> words, IList<GridLine> grids, ReferenceSetRules cfg)
        {
            if (grids == null || grids.Count == 0) { sheet.Problem = "no Revit grids to line the sheet up with"; return; }
            var named = new Dictionary<string, (bool NorthSouth, double At)>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in grids)
            {
                double dx = g.X2 - g.X1, dy = g.Y2 - g.Y1, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6 || named.ContainsKey(g.Name)) continue;
                if (Math.Abs(dx) / len < 0.02) named[g.Name] = (true, (g.X1 + g.X2) / 2);
                else if (Math.Abs(dy) / len < 0.02) named[g.Name] = (false, (g.Y1 + g.Y2) / 2);
            }
            var bubbles = words.Where(w => named.ContainsKey(w.Text.Trim())).ToList();
            if (bubbles.Count == 0) { sheet.Problem = "no grid bubbles found"; return; }
            double size = bubbles.GroupBy(w => Math.Round(w.Letters[0].PointSize)).OrderByDescending(g => g.Select(w => w.Text).Distinct().Count()).ThenByDescending(g => g.Key).First().Key;
            var pairs = bubbles.Where(w => Math.Round(w.Letters[0].PointSize) == size)
                               .GroupBy(w => w.Text.Trim().ToUpperInvariant())
                               .Select(g => (Name: g.Key, Grid: named[g.Key], Pdf: g.Key.Length > 0 && named[g.Key].NorthSouth ? g.Average(w => w.BoundingBox.Centroid.X) : g.Average(w => w.BoundingBox.Centroid.Y)))
                               .ToList();
            var ns = pairs.Where(p => p.Grid.NorthSouth).ToList();
            var ew = pairs.Where(p => !p.Grid.NorthSouth).ToList();
            sheet.Grids = pairs.Count;
            if (ns.Count < 2 || ew.Count < 2) { sheet.Problem = $"{ns.Count} north-south and {ew.Count} east-west grid bubble(s) found (2 of each needed)"; return; }

            double mR1 = ns.Average(p => p.Grid.At), mP1 = ns.Average(p => p.Pdf), mR2 = ew.Average(p => p.Grid.At), mP2 = ew.Average(p => p.Pdf);
            double num = ns.Sum(p => (p.Grid.At - mR1) * (p.Pdf - mP1)) + ew.Sum(p => (p.Grid.At - mR2) * (p.Pdf - mP2));
            double den = ns.Sum(p => Math.Pow(p.Grid.At - mR1, 2)) + ew.Sum(p => Math.Pow(p.Grid.At - mR2, 2));
            if (den <= 0) { sheet.Problem = "grid bubbles do not spread"; return; }
            sheet.Scale = num / den;
            if (sheet.Scale <= 0) { sheet.Problem = "the plan is turned or mirrored on the sheet"; return; }
            sheet.Tx = mP1 - sheet.Scale * mR1; sheet.Ty = mP2 - sheet.Scale * mR2;
            var off = ns.Select(p => (p.Name, D: Math.Abs(p.Pdf - (sheet.Scale * p.Grid.At + sheet.Tx)) / sheet.Scale * 12))
                        .Concat(ew.Select(p => (p.Name, D: Math.Abs(p.Pdf - (sheet.Scale * p.Grid.At + sheet.Ty)) / sheet.Scale * 12))).OrderByDescending(t => t.D).ToList();
            sheet.Residual = off[0].D;
            sheet.Ok = sheet.Residual <= cfg.GridTolerance;
            if (!sheet.Ok) sheet.Problem = $"grid {off[0].Name} is {off[0].D:0.#}\" off (the sheet is not to scale, or the grids moved)";
        }
    }
}
