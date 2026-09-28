using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>One riser of the diagram: the tag written at its fan (GX-1, ERV-1) and the slabs it passes through.</summary>
    public class DiagramRiser
    {
        public string Tag;
        /// <summary>The nearest tag at the run, shared by runs side by side (both ducts of ERV-1); null for a plain exhaust run.</summary>
        public string Near;
        public int Page;
        public double X;                                  // PDF points (the column)
        /// <summary>Slab (FloorKey of the floor whose slab it crosses) and the duct size written there.</summary>
        public List<(string Slab, DuctSize Size)> Slabs = new List<(string, DuctSize)>();
        /// <summary>Dampers drawn on the run (MD, FSD, GD) and the slab line they sit on.</summary>
        public List<(string Slab, string Code)> Dampers = new List<(string, string)>();

        public DuctSize SizeAt(string slab) => Slabs.FirstOrDefault(s => s.Slab == slab).Size;

        public string Describe() =>
            string.Join(", ", Slabs.GroupBy(s => s.Size?.ToString() ?? "?")
                                   .Select(g => $"{g.Key} through the {Range(g.Select(s => s.Slab).ToList())} slab{(g.Count() > 1 ? "s" : "")}"));

        private static string Range(List<string> slabs) =>
            slabs.Count == 1 ? FloorKey.Describe(slabs[0]) : $"{FloorKey.Describe(slabs[0])} to {FloorKey.Describe(slabs[slabs.Count - 1])}";
    }

    public class RiserDiagramResult
    {
        public int Page;
        public List<string> Floors = new List<string>();  // floor lines found, bottom up
        public List<DiagramRiser> Risers = new List<DiagramRiser>();   // runs with a wanted tag
        /// <summary>Every run found, tagged or not (the dampers are on untagged exhaust runs too).</summary>
        public List<DiagramRiser> Runs = new List<DiagramRiser>();
        public int Columns;                               // duct columns found (tagged or not)
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// The engineer's DUCT RISER DIAGRAM (PDF, plan section 6.2.4): floor lines named at the left (CELLAR, 1ST FL…ROOF),
    /// vertical duct runs with their size written along them just below each floor line they pass, and the fan/unit
    /// tag at the end of the run. Only runs that carry a tag the caller cares about (an opening system, or a tag the PDF
    /// does not define) are returned: the diagram has no plan positions, so it is a check on the floor plans, not a
    /// source of openings. Free of the Revit API.
    /// </summary>
    public static class RiserDiagram
    {
        private static readonly Regex SizeText = new Regex(@"^(\d{1,2})\s*[X×]\s*(\d{1,2})$", RegexOptions.IgnoreCase);
        private static readonly Regex TagText = new Regex(@"^[A-Z]{1,4}-?\d{1,2}[A-Z]?$", RegexOptions.IgnoreCase);

        private static readonly Regex CodeText = new Regex(@"^[A-Z]{2,4}$");

        /// <summary>PDF points: a run this close to a unit's tag is one of that unit's ducts (ERV-1's two ducts under it).</summary>
        private const double UnitWidth = 40;

        /// <param name="wanted">Tag text → true when the run it names should be returned.</param>
        /// <param name="damper">Code text → true when it is a damper drawn on a duct (MD, FSD, GD: the legend's "label on host").</param>
        public static RiserDiagramResult Read(string pdfPath, PdfSheetIndex index, Func<string, bool> wanted, Func<string, bool> damper = null)
        {
            var sheet = index?.Sheets.FirstOrDefault(s => s.HasRiserDiagram);
            if (sheet == null) return null;
            var result = new RiserDiagramResult { Page = sheet.Page };
            using (var pdf = PdfDocument.Open(pdfPath))
            {
                var page = pdf.GetPage(sheet.Page);
                // turned text (sizes along the runs) needs the layout-aware extractor; the default one splits it into letters
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).Where(w => w.Letters.Count > 0).ToList();

                // floor lines: the floor names down the left edge
                var floors = new Dictionary<string, double>();
                foreach (var w in Lines(words.Where(w => w.TextOrientation == TextOrientation.Horizontal && w.BoundingBox.Left < page.Width * 0.15)))
                {
                    var keys = FloorKey.Find(w.Text);
                    if (keys.Count == 1 && !floors.ContainsKey(keys[0])) floors[keys[0]] = w.Y;
                }
                var levels = floors.OrderBy(kv => kv.Value).ToList();         // bottom up
                result.Floors = levels.Select(kv => kv.Key).ToList();
                if (levels.Count < 3) { result.Warnings.Add($"page {sheet.Page}: only {levels.Count} floor line(s) found"); return result; }
                double storey = Median(levels.Zip(levels.Skip(1), (a, b) => b.Value - a.Value).ToList());

                // duct sizes written along the vertical runs (turned text); a run is a column of them
                var sizes = words.Where(w => w.TextOrientation != TextOrientation.Horizontal && SizeText.IsMatch(w.Text.Trim()))
                                 .Select(w => (X: w.BoundingBox.Centroid.X, Y: w.BoundingBox.Centroid.Y, Text: w.Text.Trim())).ToList();
                var columns = new List<List<(double X, double Y, string Text)>>();
                foreach (var s in sizes.OrderBy(s => s.X))
                {
                    var col = columns.FirstOrDefault(c => Math.Abs(c.Average(p => p.X) - s.X) <= 6);
                    if (col == null) columns.Add(new List<(double, double, string)> { s }); else col.Add(s);
                }
                // one column can hold two runs (a bulkhead fan above a riser): split where more than a storey and a half is empty
                var runs = new List<List<(double X, double Y, string Text)>>();
                foreach (var col in columns)
                {
                    var sorted = col.OrderBy(p => p.Y).ToList();
                    var run = new List<(double, double, string)> { sorted[0] };
                    for (int i = 1; i < sorted.Count; i++)
                    {
                        if (sorted[i].Y - sorted[i - 1].Y > storey * 1.6) { runs.Add(run); run = new List<(double, double, string)>(); }
                        run.Add(sorted[i]);
                    }
                    runs.Add(run);
                }
                result.Columns = runs.Count;

                // every run with the slabs it crosses: the floor line just above each size
                List<(string Slab, DuctSize Size)> SlabsOf(List<(double X, double Y, string Text)> run)
                {
                    var slabs = new List<(string, DuctSize)>();
                    foreach (var s in run.OrderBy(p => p.Y))
                    {
                        var above = levels.Where(l => l.Value > s.Y && l.Value - s.Y <= storey).Select(l => l.Key).FirstOrDefault();
                        if (above == null || slabs.Any(x => x.Item1 == above)) continue;
                        var m = SizeText.Match(s.Text);
                        slabs.Add((above, new DuctSize { Width = double.Parse(m.Groups[1].Value), Length = double.Parse(m.Groups[2].Value) }));
                    }
                    return slabs;
                }
                var all = runs.Select(run => new DiagramRiser { Page = sheet.Page, X = run.Average(p => p.X), Slabs = SlabsOf(run) }).ToList();
                result.Runs = all.Where(r => r.Slabs.Count > 0).ToList();

                // the tag of each run: the nearest wanted tag text beside or at the end of it
                var tags = words.Where(w => TagText.IsMatch(w.Text.Trim()) && wanted(w.Text.Trim().ToUpperInvariant()))
                                .Select(w => (Text: w.Text.Trim().ToUpperInvariant(), X: w.BoundingBox.Centroid.X, Y: w.BoundingBox.Centroid.Y)).ToList();
                var pairs = (from t in tags
                             from r in runs.Select((run, i) => (Run: run, I: i))
                             let score = Score(r.Run, t.X, t.Y, storey)
                             where score < double.MaxValue
                             orderby score
                             select (Tag: t, r.Run, r.I)).ToList();
                var takenRuns = new HashSet<int>(); var takenTags = new HashSet<(string, double, double)>();
                foreach (var (t, run, i) in pairs)
                {
                    if (takenRuns.Contains(i) || takenTags.Contains(t)) continue;
                    takenRuns.Add(i); takenTags.Add(t);
                    var riser = all[i];
                    riser.Tag = riser.Near = t.Text;
                    if (riser.Slabs.Count > 0) result.Risers.Add(riser);
                }
                // runs side by side under one unit share its tag (ERV-1's supply and exhaust ducts), without being named by it
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Near == null)
                        all[i].Near = tags.Where(t => Math.Abs(all[i].X - t.X) <= UnitWidth)
                                          .Select(t => (t.Text, S: Score(runs[i], t.X, t.Y, storey))).Where(x => x.S < double.MaxValue)
                                          .OrderBy(x => x.S).Select(x => x.Text).FirstOrDefault();

                // dampers: the code written on a floor line beside the run that crosses that slab
                if (damper != null)
                    foreach (var w in words.Where(w => w.TextOrientation == TextOrientation.Horizontal && CodeText.IsMatch(w.Text.Trim()) && damper(w.Text.Trim())))
                    {
                        double x = w.BoundingBox.Centroid.X, y = w.BoundingBox.Centroid.Y;
                        var line = levels.Select(l => (l.Key, D: Math.Abs(l.Value - y))).Where(l => l.D <= storey * 0.3).OrderBy(l => l.D).Select(l => l.Key).FirstOrDefault();
                        if (line == null) continue;
                        var run = result.Runs.Where(r => r.SizeAt(line) != null && Math.Abs(r.X - x) <= 60).OrderBy(r => Math.Abs(r.X - x)).FirstOrDefault();
                        if (run == null) { result.Warnings.Add($"{w.Text.Trim()} on the {FloorKey.Describe(line)} line: no duct run next to it"); continue; }
                        run.Dampers.Add((line, w.Text.Trim()));
                    }
            }
            return result;
        }

        /// <summary>How far a tag is from a run: beside it (up to 120 points across) and within or just past its ends.</summary>
        private static double Score(List<(double X, double Y, string Text)> run, double x, double y, double storey)
        {
            double cx = run.Average(p => p.X), dx = Math.Abs(cx - x);
            if (dx > 120) return double.MaxValue;
            double lo = run.Min(p => p.Y), hi = run.Max(p => p.Y);
            double dy = y < lo ? lo - y : y > hi ? y - hi : 0;
            if (dy > storey * 1.2) return double.MaxValue;
            return dy + dx * 0.5;
        }

        private class Line { public string Text; public double Y; }

        private static IEnumerable<Line> Lines(IEnumerable<Word> words) =>
            words.GroupBy(w => Math.Round(w.BoundingBox.Bottom))
                 .Select(g => new Line { Text = string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)), Y = g.Average(w => w.BoundingBox.Centroid.Y) });

        private static double Median(List<double> v)
        {
            if (v.Count == 0) return 0;
            v = v.OrderBy(x => x).ToList();
            return v[v.Count / 2];
        }
    }
}
