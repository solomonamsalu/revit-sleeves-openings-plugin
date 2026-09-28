using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Tokens;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A column drawn on a PDF floor plan (real inches in the sheet's frame): the rectangle's centre and its sides along the sheet's X and Y (0 when drawn turned).</summary>
    public class PdfColumn
    {
        public double X, Y;
        public double W, L;
    }

    /// <summary>One floor plan page read in PDF-only mode.</summary>
    public class PdfPlan
    {
        public string Floor;
        public int Page;
        /// <summary>Real inches per sheet inch (48 = 1/4" = 1'-0").</summary>
        public double Scale;
        /// <summary>The scale as printed; null when none was found and the default was used.</summary>
        public string ScaleText;
        public int Layers;
        public List<PdfColumn> Columns = new List<PdfColumn>();
        /// <summary>Why nothing was read from the page (scanned, no layers).</summary>
        public string Problem;
    }

    public class PdfPlanResult
    {
        /// <summary>The floor plans as DWG floors (for the floor grid and the plumbing merge): region = the whole page.</summary>
        public DwgSheetIndex Index = new DwgSheetIndex();
        /// <summary>Pipe groups, bubbles, service lists and fixtures, exactly as the DWG reader gives them.</summary>
        public DwgRiserResult Risers = new DwgRiserResult();
        public List<PdfPlan> Plans = new List<PdfPlan>();
        public List<string> Warnings = new List<string>();
        public PdfPlan For(string floor) => Plans.FirstOrDefault(p => p.Floor == floor);
    }

    /// <summary>
    /// PDF-only mode: reads the floor plans of a vector PDF plotted from CAD. The plot keeps the CAD layers (as optional
    /// content), so each shape is known by its layer as in the DWG: pipe circles on the pipe layers (true circles, or
    /// polygons of 10+ sides; trimmed arcs over 270° count), leaders on the connector layer (extended to the tip of their
    /// filled arrowhead), tag bubbles (circles at a leader's far end), the service text next to each bubble, fixture names,
    /// and the columns of the architectural background. Positions are real inches after the sheet scale printed under the
    /// plan title. The tag numbers inside the bubbles are drawn as strokes, not text: bubbles whose strokes are identical
    /// carry the same tag, so one stack gets one name on every floor (numbered 1, 2, 3... in the order found).
    /// Free of the Revit API.
    /// </summary>
    public static class PdfPlanReader
    {
        private class Shape { public string Layer; public PdfPath Path; }
        private class Ring { public double X, Y, R; }
        private class Line { public string Text; public double X0, Y0, X1, Y1, Size; public double Cx => (X0 + X1) / 2; public double Cy => (Y0 + Y1) / 2; }
        private class PageBubble { public double X, Y, R; public List<(double X, double Y)> Glyph; public string Tag; }

        public static PdfPlanResult Read(string path, PdfSheetIndex sheets, DwgProfile profile, PdfOnlyRules rules)
        {
            var result = new PdfPlanResult();
            result.Index.Path = path; result.Index.Version = "PDF (vector)"; result.Index.Units = "Inches";
            Regex Rx(string p) => string.IsNullOrWhiteSpace(p) ? null : new Regex(p, RegexOptions.IgnoreCase);
            var pipeLayers = Rx(profile.RiserCircleLayers); var connector = Rx(profile.ConnectorLayers);
            var columnLayers = Rx(rules.ColumnLayers); var fixtureText = Rx(profile.FixtureText);
            if (pipeLayers == null || connector == null)
            {
                result.Warnings.Add("PDF only: the drawing profile names no pipe-circle or leader layers; nothing read.");
                return result;
            }

            var pages = new List<(PdfPlan Plan, List<RiserSymbol> Symbols, List<List<(double X, double Y)>> Lines, List<PageBubble> Bubbles,
                                  List<(string Text, double X, double Y)> Texts)>();
            using (var pdf = PdfDocument.Open(path))
            {
                if (!sheets.FloorPlans.Any())
                {
                    int scanned = pdf.GetPages().Count(Scanned);
                    result.Warnings.Add(scanned > 0
                        ? $"PDF only: {scanned} of {pdf.NumberOfPages} page(s) are scanned images and no floor plan title was found; PDF-only mode needs a vector PDF plotted from CAD."
                        : "PDF only: no floor plan title (e.g. \"5TH FLOOR PLAN\") was found on any page.");
                }
                foreach (var sheet in sheets.FloorPlans.GroupBy(s => s.Floor).Select(g => g.First()))
                {
                    var page = pdf.GetPage(sheet.Page);
                    var plan = new PdfPlan { Floor = sheet.Floor, Page = sheet.Page };
                    result.Plans.Add(plan);

                    var shapes = Shapes(page);
                    plan.Layers = shapes.Select(s => s.Layer).Distinct().Count();
                    if (plan.Layers == 0)
                    {
                        plan.Problem = Scanned(page) ? "the page is a scanned image: nothing can be read from it"
                                                     : "the PDF has no CAD layers (plotted without them): pipes cannot be told apart";
                        result.Warnings.Add($"{FloorKey.Describe(sheet.Floor)} (page {sheet.Page}): {plan.Problem}.");
                        continue;
                    }

                    var lines = TextLines(page.GetWords().Where(w => w.Letters.Count > 0).ToList());
                    (plan.Scale, plan.ScaleText) = Scale(lines, sheet, rules.DefaultScale);
                    if (plan.ScaleText == null)
                        result.Warnings.Add($"{FloorKey.Describe(sheet.Floor)} (page {sheet.Page}): no printed scale found; {Ratio(plan.Scale)} assumed (rules.json pdfOnly.defaultScale).");
                    double k = plan.Scale / 72;                              // real inches per PDF point
                    (double X, double Y) Real(PdfPoint p) => (p.X * k, p.Y * k);

                    // ---- pipe circles
                    var symbols = new List<RiserSymbol>();
                    foreach (var s in shapes.Where(s => pipeLayers.IsMatch(s.Layer)))
                        foreach (var sub in s.Path)
                        {
                            var ring = Round(Points(sub));
                            if (ring == null) continue;
                            double r = ring.R * k;
                            if (r < profile.RiserCircleMinRadius || r > profile.RiserCircleMaxRadius) continue;
                            double x = ring.X * k, y = ring.Y * k;
                            if (symbols.Any(o => o.Layer == s.Layer && Dist(o.X, o.Y, x, y) < 0.5)) continue;     // drawn twice
                            symbols.Add(new RiserSymbol { X = x, Y = y, Layer = s.Layer, Block = DwgRiserReader.PipeCircle, Radius = r });
                        }

                    // ---- leaders: stroked lines, each end with a filled arrowhead extended to the arrow's tip
                    var polylines = new List<List<(double X, double Y)>>();
                    var arrows = new List<List<PdfPoint>>();
                    foreach (var s in shapes.Where(s => connector.IsMatch(s.Layer)))
                        foreach (var sub in s.Path)
                        {
                            var pts = Points(sub);
                            if (pts.Count < 2) continue;
                            if (s.Path.IsFilled && pts.Count <= 5) arrows.Add(pts);
                            else polylines.Add(pts.Select(Real).ToList());
                        }
                    foreach (var line in polylines)
                        foreach (bool first in new[] { true, false })
                        {
                            var end = first ? line[0] : line[line.Count - 1];
                            var arrow = arrows.Select(a => (A: a, D: Dist(a.Average(q => q.X) * k, a.Average(q => q.Y) * k, end.X, end.Y)))
                                              .Where(t => t.D <= 8).OrderBy(t => t.D).Select(t => t.A).FirstOrDefault();
                            if (arrow == null) continue;
                            arrows.Remove(arrow);
                            var tip = arrow.Select(Real).OrderByDescending(q => Dist(q.X, q.Y, end.X, end.Y)).First();
                            if (first) line.Insert(0, tip); else line.Add(tip);
                        }

                    // ---- tag bubbles: circles at the far end of a leader (the ring drawn twice / double ring = one bubble)
                    var ends = polylines.SelectMany(l => new[] { l[0], l[l.Count - 1] }).ToList();
                    var bubbles = new List<PageBubble>();
                    foreach (var s in shapes)
                        foreach (var sub in s.Path)
                        {
                            var ring = Round(Points(sub));
                            if (ring == null) continue;
                            double r = ring.R * k, x = ring.X * k, y = ring.Y * k;
                            if (r < rules.BubbleMinRadius || r > rules.BubbleMaxRadius) continue;
                            if (!ends.Any(e => Dist(e.X, e.Y, x, y) <= r + 4)) continue;
                            var same = bubbles.FirstOrDefault(b => Dist(b.X, b.Y, x, y) < 2);
                            if (same != null) { same.R = Math.Max(same.R, r); continue; }
                            bubbles.Add(new PageBubble { X = x, Y = y, R = r });
                        }
                    if (bubbles.Count > 0)
                    {
                        var strokes = page.Paths.SelectMany(q => q).Select(Points).Where(q => q.Count >= 2).ToList();
                        foreach (var b in bubbles) b.Glyph = Glyph(strokes, b, k);
                    }

                    // ---- columns (architectural background)
                    if (columnLayers != null)
                        foreach (var s in shapes.Where(s => columnLayers.IsMatch(s.Layer)))
                            foreach (var rect in ColumnShapes(s.Path))
                            {
                                double a = rect.A * k, b = rect.B * k, x = rect.X * k, y = rect.Y * k;
                                if (Math.Min(a, b) < rules.ColumnMinSize || Math.Max(a, b) > rules.ColumnMaxSize) continue;
                                if (plan.Columns.Any(c => Dist(c.X, c.Y, x, y) < 1)) continue;          // outline + hatch, drawn twice
                                plan.Columns.Add(new PdfColumn { X = x, Y = y, W = rect.W * k, L = rect.L * k });
                            }

                    // ---- text: fixture names at their spot, the rest as notes (service lists attach to the bubbles)
                    var texts = new List<(string Text, double X, double Y)>();
                    foreach (var l in lines)
                    {
                        string t = l.Text.Trim().ToUpperInvariant();
                        if (fixtureText != null && !t.Contains(" ") && fixtureText.IsMatch(t))
                            result.Risers.Fixtures.Add(new FixtureMark { Floor = sheet.Floor, Code = t, X = l.Cx * k, Y = l.Cy * k });
                        else texts.Add((l.Text, l.Cx * k, l.Cy * k));
                    }

                    result.Index.Floors.Add(new DwgFloor
                    {
                        Floor = sheet.Floor, Title = sheet.Title, Layout = $"PDF page {sheet.Page}", Scale = (int)Math.Round(plan.Scale),
                        MinX = 0, MinY = 0, MaxX = page.Width * k, MaxY = page.Height * k
                    });
                    pages.Add((plan, symbols, polylines, bubbles, texts));
                    if (plan.Columns.Count == 0)
                        result.Warnings.Add($"{FloorKey.Describe(sheet.Floor)} (page {sheet.Page}): no columns found on the column layers (rules.json pdfOnly.columnLayers).");
                }
            }

            // ---- one name per tag drawing: bubbles whose strokes match carry the same tag, numbered in the order found
            //      (lowest floor first, then left to right)
            var classes = new List<List<PageBubble>>();
            foreach (var b in pages.OrderBy(p => FloorKey.Order(p.Plan.Floor)).SelectMany(p => p.Bubbles.OrderBy(x => x.X)).Where(b => b.Glyph.Count > 0))
            {
                var cls = classes.FirstOrDefault(c => SameGlyph(c[0].Glyph, b.Glyph));
                if (cls == null) classes.Add(cls = new List<PageBubble>());
                cls.Add(b);
            }
            for (int i = 0; i < classes.Count; i++)
                foreach (var b in classes[i]) b.Tag = (i + 1).ToString();
            int loose = 0;
            foreach (var b in pages.SelectMany(p => p.Bubbles).Where(b => b.Tag == null)) b.Tag = "?" + (++loose);

            foreach (var p in pages)
            {
                DwgRiserReader.ReadFloor(p.Plan.Floor, p.Symbols, p.Bubbles.Select(b => (b.Tag, b.X, b.Y, b.R)), p.Lines, p.Texts, profile, result.Risers);
                foreach (var r in result.Risers.Risers.Where(r => r.Floor == p.Plan.Floor))
                    if (!r.Evidence.Any(e => e.StartsWith("read from PDF"))) r.Evidence.Add($"read from PDF page {p.Plan.Page} ({Ratio(p.Plan.Scale)})");
            }
            if (loose > 0)
                result.Warnings.Add($"{loose} tag bubble(s) with nothing drawn inside them; named ?1, ?2...");
            return result;
        }

        /// <summary>"1/4\" = 1'-0\"" for 48.</summary>
        public static string Ratio(double scale)
        {
            double paper = 12 / scale;
            foreach (int den in new[] { 1, 2, 4, 8, 16, 32, 64 })
            {
                double num = paper * den;
                if (Math.Abs(num - Math.Round(num)) < 1e-6)
                    return den == 1 ? $"{Math.Round(num)}\" = 1'-0\"" : $"{Math.Round(num)}/{den}\" = 1'-0\"";
            }
            return $"1:{scale:0.##}";
        }

        // ------------------------------------------------------------------ page content by CAD layer

        /// <summary>Every path inside an optional-content (layer) section of the page, with its layer name.</summary>
        private static List<Shape> Shapes(Page page)
        {
            var list = new List<Shape>();
            void Walk(MarkedContentElement e, string layer)
            {
                if (e.Tag == "OC" && e.Properties != null && e.Properties.TryGet(NameToken.Name, out var n))
                    layer = n is StringToken s ? s.Data : n.ToString();
                if (layer != null) foreach (var p in e.Paths) list.Add(new Shape { Layer = layer, Path = p });
                foreach (var c in e.Children) Walk(c, layer);
            }
            foreach (var e in page.GetMarkedContents()) Walk(e, null);
            return list;
        }

        /// <summary>Images cover most of the page: a scan (or a raster plot).</summary>
        private static bool Scanned(Page page)
        {
            double area = page.Width * page.Height, covered = 0;
            foreach (var img in page.GetImages())
            {
                var b = img.Bounds;
                double w = Math.Max(0, Math.Min(b.Right, page.Width) - Math.Max(b.Left, 0)), h = Math.Max(0, Math.Min(b.Top, page.Height) - Math.Max(b.Bottom, 0));
                covered += w * h;
            }
            return area > 0 && covered / area > 0.5;
        }

        /// <summary>The points along a subpath (curves sampled), in PDF points.</summary>
        private static List<PdfPoint> Points(PdfSubpath sub)
        {
            var pts = new List<PdfPoint>();
            void Add(PdfPoint p) { if (pts.Count == 0 || Math.Abs(pts[pts.Count - 1].X - p.X) > 1e-6 || Math.Abs(pts[pts.Count - 1].Y - p.Y) > 1e-6) pts.Add(p); }
            foreach (var c in sub.Commands)
                switch (c)
                {
                    case PdfSubpath.Move m: Add(m.Location); break;
                    case PdfSubpath.Line l: Add(l.From); Add(l.To); break;
                    case PdfSubpath.CubicBezierCurve b:
                        Add(b.StartPoint);
                        foreach (double t in new[] { 0.25, 0.5, 0.75 })
                        {
                            double u = 1 - t;
                            Add(new PdfPoint(u * u * u * b.StartPoint.X + 3 * u * u * t * b.FirstControlPoint.X + 3 * u * t * t * b.SecondControlPoint.X + t * t * t * b.EndPoint.X,
                                             u * u * u * b.StartPoint.Y + 3 * u * u * t * b.FirstControlPoint.Y + 3 * u * t * t * b.SecondControlPoint.Y + t * t * t * b.EndPoint.Y));
                        }
                        Add(b.EndPoint);
                        break;
                }
            return pts;
        }

        /// <summary>
        /// A circle through the points (least squares), when they all lie on it and go at least 270° around: a true
        /// circle, a polygon of 8+ sides, or an arc trimmed where a leader crosses it. Null otherwise.
        /// </summary>
        private static Ring Round(List<PdfPoint> pts)
        {
            if (pts.Count < 6) return null;
            // x² + y² + Dx + Ey + F = 0, centred on the mean for accuracy
            double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
            double sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
            foreach (var p in pts)
            {
                double x = p.X - mx, y = p.Y - my, z = x * x + y * y;
                sxx += x * x; syy += y * y; sxy += x * y; sxz += x * z; syz += y * z;
            }
            double det = sxx * syy - sxy * sxy;
            if (Math.Abs(det) < 1e-12) return null;
            double cx = (sxz * syy - syz * sxy) / det / 2, cy = (syz * sxx - sxz * sxy) / det / 2;
            double r = pts.Average(p => Dist(p.X - mx, p.Y - my, cx, cy));
            if (r <= 0 || pts.Any(p => Math.Abs(Dist(p.X - mx, p.Y - my, cx, cy) - r) > 0.08 * r + 0.05)) return null;
            var angles = pts.Select(p => Math.Atan2(p.Y - my - cy, p.X - mx - cx)).OrderBy(a => a).ToList();
            double gap = 2 * Math.PI - (angles[angles.Count - 1] - angles[0]);
            for (int i = 1; i < angles.Count; i++) gap = Math.Max(gap, angles[i] - angles[i - 1]);
            if (gap > Math.PI / 2 + 1e-6) return null;
            return new Ring { X = cx + mx, Y = cy + my, R = r };
        }

        /// <summary>
        /// Column outlines in one path: a closed rectangle (each subpath), or a shape whose points all sit on the corners of
        /// its box (the outline traced with its diagonals, or its hatch drawn as triangles).
        /// </summary>
        private static IEnumerable<(double X, double Y, double W, double L, double A, double B)> ColumnShapes(PdfPath path)
        {
            var subs = path.Select(Points).Where(p => p.Count >= 2).ToList();
            var all = subs.SelectMany(p => p).ToList();
            if (all.Count >= 3)
            {
                double x0 = all.Min(p => p.X), x1 = all.Max(p => p.X), y0 = all.Min(p => p.Y), y1 = all.Max(p => p.Y);
                bool corners = x1 - x0 > 0.5 && y1 - y0 > 0.5 &&
                               all.All(p => (Math.Abs(p.X - x0) < 0.1 || Math.Abs(p.X - x1) < 0.1) && (Math.Abs(p.Y - y0) < 0.1 || Math.Abs(p.Y - y1) < 0.1));
                if (corners) { yield return ((x0 + x1) / 2, (y0 + y1) / 2, x1 - x0, y1 - y0, x1 - x0, y1 - y0); yield break; }
            }
            foreach (var sub in subs)
            {
                var r = Rectangle(sub);
                if (r == null) continue;
                // sides along the sheet's X / Y when drawn square to the sheet; 0 when turned
                bool firstAlongX = Math.Abs(sub[0].Y - sub[1].Y) < 0.05, firstAlongY = Math.Abs(sub[0].X - sub[1].X) < 0.05;
                double w = firstAlongX ? r.Value.A : firstAlongY ? r.Value.B : 0, l = firstAlongX ? r.Value.B : firstAlongY ? r.Value.A : 0;
                yield return (r.Value.X, r.Value.Y, w, l, r.Value.A, r.Value.B);
            }
        }

        /// <summary>A closed four-cornered outline with right angles: centre and the two sides (PDF points).</summary>
        private static (double X, double Y, double A, double B)? Rectangle(List<PdfPoint> pts)
        {
            var c = pts.ToList();
            if (c.Count == 5 && Dist(c[0].X, c[0].Y, c[4].X, c[4].Y) < 0.05) c.RemoveAt(4);
            if (c.Count != 4) return null;
            for (int i = 0; i < 4; i++)
            {
                var a = c[i]; var b = c[(i + 1) % 4]; var d = c[(i + 2) % 4];
                double ux = b.X - a.X, uy = b.Y - a.Y, vx = d.X - b.X, vy = d.Y - b.Y;
                double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
                if (lu < 1e-6 || lv < 1e-6 || Math.Abs(ux * vx + uy * vy) / (lu * lv) > 0.02) return null;
            }
            return (c.Average(p => p.X), c.Average(p => p.Y), Dist(c[0].X, c[0].Y, c[1].X, c[1].Y), Dist(c[1].X, c[1].Y, c[2].X, c[2].Y));
        }

        /// <summary>
        /// The strokes drawn inside a bubble (its tag, plotted as lines), relative to the bubble's centre (PDF points).
        /// Rectangles (the white boxes behind the text, which shift a little from bubble to bubble) and the divider line are left out.
        /// </summary>
        private static List<(double X, double Y)> Glyph(List<List<PdfPoint>> strokes, PageBubble b, double k)
        {
            double cx = b.X / k, cy = b.Y / k, inner = b.R / k * 0.8;
            var pts = new List<(double X, double Y)>();
            foreach (var s in strokes)
            {
                if (s.Any(p => Dist(p.X, p.Y, cx, cy) > inner)) continue;
                if (s.Count == 2 && Math.Abs(s[0].Y - s[1].Y) < 0.01 && Math.Abs(s[0].X - s[1].X) > inner) continue;   // the bubble's divider
                if (Rectangle(s) != null) continue;
                pts.AddRange(s.Select(p => (p.X - cx, p.Y - cy)));
            }
            return pts;
        }

        /// <summary>Two tag drawings are the same when every point of each lies within 1 pt of the other (after centring both).</summary>
        private static bool SameGlyph(List<(double X, double Y)> a, List<(double X, double Y)> b)
        {
            if (a.Count == 0 || b.Count == 0 || Math.Abs(a.Count - b.Count) > 0.3 * Math.Max(a.Count, b.Count)) return false;
            double ax = a.Average(p => p.X), ay = a.Average(p => p.Y), bx = b.Average(p => p.X), by = b.Average(p => p.Y);
            bool Covered(List<(double X, double Y)> from, double fx, double fy, List<(double X, double Y)> to, double tx, double ty) =>
                from.All(p => to.Any(q => Dist(p.X - fx, p.Y - fy, q.X - tx, q.Y - ty) <= 1.0));
            return Covered(a, ax, ay, b, bx, by) && Covered(b, bx, by, a, ax, ay);
        }

        // ------------------------------------------------------------------ text

        /// <summary>Words joined into text lines: same size, same baseline, close together.</summary>
        private static List<Line> TextLines(List<Word> words)
        {
            var lines = new List<Line>();
            foreach (var g in words.GroupBy(w => (Math.Round(w.BoundingBox.Bottom), Math.Round(w.Letters[0].PointSize, 1))))
            {
                double size = Math.Max(1, g.Key.Item2);
                Line cur = null;
                foreach (var w in g.OrderBy(w => w.BoundingBox.Left))
                {
                    var bb = w.BoundingBox;
                    if (cur != null && bb.Left - cur.X1 <= 1.5 * size)
                    {
                        cur.Text += " " + w.Text; cur.X1 = Math.Max(cur.X1, bb.Right); cur.Y0 = Math.Min(cur.Y0, bb.Bottom); cur.Y1 = Math.Max(cur.Y1, bb.Top);
                        continue;
                    }
                    cur = new Line { Text = w.Text, X0 = bb.Left, X1 = bb.Right, Y0 = bb.Bottom, Y1 = bb.Top, Size = size };
                    lines.Add(cur);
                }
            }
            foreach (var l in lines) l.Text = Regex.Replace(l.Text, @"\s+", " ").Trim();
            return lines.Where(l => l.Text.Length > 0).ToList();
        }

        private static readonly Regex ScaleRx = new Regex(
            @"(?:(?<whole>\d+)\s+)?(?:(?<num>\d+)\s*/\s*(?<den>\d+)|(?<int>\d+))\s*(?:""|'')\s*=\s*(?<ft>\d+)\s*'\s*(?:-?\s*(?<in>\d+)\s*(?:""|''))?",
            RegexOptions.Compiled);

        /// <summary>The plan's printed scale: the scale text nearest its title (a sheet may hold details at other scales).</summary>
        private static (double Scale, string Text) Scale(List<Line> lines, PdfSheet sheet, double fallback)
        {
            var found = new List<(double Scale, string Text, Line L)>();
            foreach (var l in lines)
            {
                var m = ScaleRx.Match(l.Text.Replace('′', '\'').Replace('″', '"').Replace('”', '"'));
                if (!m.Success) continue;
                double paper = m.Groups["int"].Success ? double.Parse(m.Groups["int"].Value)
                             : (m.Groups["whole"].Success ? double.Parse(m.Groups["whole"].Value) : 0) + double.Parse(m.Groups["num"].Value) / Math.Max(1, double.Parse(m.Groups["den"].Value));
                double real = double.Parse(m.Groups["ft"].Value) * 12 + (m.Groups["in"].Success ? double.Parse(m.Groups["in"].Value) : 0);
                if (paper <= 0 || real <= 0) continue;
                found.Add((real / paper, m.Value.Trim(), l));
            }
            if (found.Count == 0) return (fallback, null);
            var titles = lines.Where(l => FloorKey.FromPlanTitle(l.Text) == sheet.Floor && Regex.IsMatch(l.Text, @"\bPLAN\b", RegexOptions.IgnoreCase)).ToList();
            if (titles.Count > 0)
            {
                var title = titles.OrderByDescending(t => t.Size).First();
                var best = found.OrderBy(f => Dist(f.L.Cx, f.L.Cy, title.Cx, title.Cy)).First();
                return (best.Scale, best.Text);
            }
            var common = found.GroupBy(f => Math.Round(f.Scale, 3)).OrderByDescending(g => g.Count()).First().First();
            return (common.Scale, common.Text);
        }

        private static double Dist(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    }
}
