using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A riser label (UP/DN or dryer text) on a PDF floor plan.</summary>
    public class PdfLabel
    {
        public string Floor, Text;
        public int Page;
        public RiserLabel Parsed;
        public double X, Y;              // PDF points (left, baseline)
        public double DwgX, DwgY;        // the same spot in the engineer DWG, once the page is lined up
        public bool Matched;
    }

    /// <summary>How one PDF floor page lines up with the DWG floor: pdf = scale × dwg + shift (from labels found in both).</summary>
    public class PdfFloorFit
    {
        public string Floor;
        public int Page;
        public int Anchors;
        public double Scale, Tx, Ty;
        public double Residual;          // worst anchor, drawing units (inches)
        public bool Ok;
        public string Problem;

        public (double X, double Y) ToDwg(double px, double py) => ((px - Tx) / Scale, (py - Ty) / Scale);
        public (double X, double Y) ToPdf(double x, double y) => (Scale * x + Tx, Scale * y + Ty);
    }

    public class PdfLabelCheck
    {
        public const string Same = "same in the PDF", Differs = "PDF differs", Missing = "not in the PDF", NotChecked = "PDF page not lined up";
        public string Status;
        public PdfLabel Pdf;             // the PDF label found at that spot (Same / Differs)
    }

    public class PdfCheckResult
    {
        public Dictionary<RiserLabel, PdfLabelCheck> Labels = new Dictionary<RiserLabel, PdfLabelCheck>();
        public List<PdfFloorFit> Fits = new List<PdfFloorFit>();
        /// <summary>UP/DN labels on the PDF plan with no DWG label at that spot (the DWG may be older or the label unattached).</summary>
        public List<PdfLabel> OnlyInPdf = new List<PdfLabel>();
        public List<string> Warnings = new List<string>();

        public PdfLabelCheck For(RiserLabel l) => l != null && Labels.TryGetValue(l, out var c) ? c : null;
    }

    /// <summary>
    /// Phase 5, DWG ↔ PDF: the issued PDF is the reference for sizes. Each floor page is lined up with the DWG floor by
    /// the riser labels that appear once on both (scale + shift, as the sheet is plotted), then every DWG label is looked
    /// up at its spot on the PDF: the same text, a different one (the PDF wins, reported), or nothing.
    /// </summary>
    public static class PdfRiserCheck
    {
        /// <param name="radius">Drawing units (inches): a PDF label this close to where the DWG label lands is the same label.</param>
        public static PdfCheckResult Run(string pdfPath, PdfSheetIndex index, DwgRiserResult risers, double radius = 24)
        {
            var result = new PdfCheckResult();
            using (var pdf = PdfDocument.Open(pdfPath))
            {
                var floors = new List<(string Floor, PdfSheet Sheet, List<RiserLabel> Dwg, List<PdfLabel> Pdf, PdfFloorFit Fit)>();
                foreach (var floor in risers.Risers.Select(r => r.Floor).Distinct())
                {
                    var dwgLabels = risers.Risers.Where(r => r.Floor == floor).SelectMany(r => r.Labels).Where(Useful).ToList();
                    if (dwgLabels.Count == 0) continue;                         // roof plans often have none: nothing to compare
                    var sheet = index.FloorPlans.FirstOrDefault(s => s.Floor == floor);
                    if (sheet == null) { Mark(result, dwgLabels, PdfLabelCheck.NotChecked); result.Warnings.Add($"{FloorKey.Describe(floor)}: no PDF floor plan."); continue; }
                    var pdfLabels = Read(pdf.GetPage(sheet.Page), floor, sheet.Page);
                    floors.Add((floor, sheet, dwgLabels, pdfLabels, Fit(floor, sheet.Page, dwgLabels, pdfLabels, radius)));
                }
                // pages with repeated labels only (the cellar: many "6X6 UP"): same plot scale as the others, shift by vote
                var scales = floors.Where(f => f.Fit.Ok).Select(f => f.Fit.Scale).OrderBy(x => x).ToList();
                if (scales.Count > 0)
                    foreach (var f in floors.Where(f => !f.Fit.Ok))
                        Vote(f.Fit, f.Dwg, f.Pdf, scales[scales.Count / 2], radius);

                foreach (var (floor, sheet, dwgLabels, pdfLabels, fit) in floors)
                {
                    result.Fits.Add(fit);
                    if (!fit.Ok) { Mark(result, dwgLabels, PdfLabelCheck.NotChecked); result.Warnings.Add($"{FloorKey.Describe(floor)}: PDF page {sheet.Page} not lined up with the DWG ({fit.Problem})."); continue; }

                    foreach (var p in pdfLabels) (p.DwgX, p.DwgY) = fit.ToDwg(p.X, p.Y);
                    foreach (var l in dwgLabels)
                    {
                        var near = pdfLabels.Select(p => (P: p, D: Dist(p.DwgX, p.DwgY, l.TextX, l.TextY))).Where(t => t.D <= radius).OrderBy(t => t.D).ToList();
                        var same = near.FirstOrDefault(t => !t.P.Matched && Equal(t.P.Parsed, l));
                        // a leader's text may be anchored at its right end in the DWG and read from its left end in the PDF
                        if (same.P == null)
                            same = pdfLabels.Where(p => !p.Matched && Equal(p.Parsed, l) && Math.Abs(p.DwgY - l.TextY) <= radius / 2 && Math.Abs(p.DwgX - l.TextX) <= Width(l.Text) + radius)
                                            .Select(p => (P: p, D: Dist(p.DwgX, p.DwgY, l.TextX, l.TextY))).OrderBy(t => t.D).FirstOrDefault();
                        if (same.P != null) { same.P.Matched = true; result.Labels[l] = new PdfLabelCheck { Status = PdfLabelCheck.Same, Pdf = same.P }; continue; }
                        var other = near.FirstOrDefault(t => !t.P.Matched && t.P.Parsed.Dryer == l.Dryer);
                        if (other.P != null) { other.P.Matched = true; result.Labels[l] = new PdfLabelCheck { Status = PdfLabelCheck.Differs, Pdf = other.P }; continue; }
                        result.Labels[l] = new PdfLabelCheck { Status = PdfLabelCheck.Missing };
                    }
                    // sized UP/DN labels only: "VENT UP IN SHAFT" and general notes are not riser sizes
                    result.OnlyInPdf.AddRange(pdfLabels.Where(p => !p.Matched && (p.Parsed.GoesUp || p.Parsed.GoesDown) && (p.Parsed.Up != null || p.Parsed.Down != null)));
                }
            }
            return result;
        }

        /// <summary>Text width on the plan (inches at 1/4" scale, generous): a label is about 3.5" per character.</summary>
        private static double Width(string text) => (text?.Length ?? 0) * 4.0;

        /// <summary>Shift by vote at a known scale: each DWG/PDF pair with the same label proposes one; the most agreed wins.</summary>
        private static void Vote(PdfFloorFit fit, List<RiserLabel> dwg, List<PdfLabel> pdf, double scale, double radius)
        {
            var shifts = (from d in dwg from p in pdf where Equal(p.Parsed, d) select (X: p.X - scale * d.TextX, Y: p.Y - scale * d.TextY)).ToList();
            double tol = scale * 3;                                  // 3 drawing inches, in points
            var best = shifts.Select(s => (S: s, N: shifts.Count(o => Math.Abs(o.X - s.X) <= tol && Math.Abs(o.Y - s.Y) <= tol)))
                             .OrderByDescending(t => t.N).FirstOrDefault();
            if (best.N < 3) { fit.Problem += $"; no shift agreed by 3 labels (best {best.N})"; return; }
            var agree = shifts.Where(o => Math.Abs(o.X - best.S.X) <= tol && Math.Abs(o.Y - best.S.Y) <= tol).ToList();
            fit.Scale = scale; fit.Tx = agree.Average(o => o.X); fit.Ty = agree.Average(o => o.Y);
            fit.Anchors = best.N; fit.Residual = agree.Max(o => Dist(o.X, o.Y, fit.Tx, fit.Ty)) / scale;
            fit.Ok = true; fit.Problem = null;
        }

        /// <summary>Labels worth comparing: with UP/DN or a dryer note (plain duct sizes are everywhere on a plan).</summary>
        private static bool Useful(RiserLabel l) => l.GoesUp || l.GoesDown || l.Dryer;

        private static void Mark(PdfCheckResult r, IEnumerable<RiserLabel> labels, string status)
        {
            foreach (var l in labels) r.Labels[l] = new PdfLabelCheck { Status = status };
        }

        /// <summary>Horizontal text lines on the page that read as riser labels.</summary>
        private static List<PdfLabel> Read(Page page, string floor, int number)
        {
            var words = page.GetWords().Where(w => w.Letters.Count > 0 && w.TextOrientation == TextOrientation.Horizontal)
                            .OrderBy(w => Math.Round(w.BoundingBox.Bottom)).ThenBy(w => w.BoundingBox.Left).ToList();
            var lines = new List<List<Word>>();
            foreach (var w in words)
            {
                double size = w.Letters[0].PointSize;
                var line = lines.FirstOrDefault(l => Math.Abs(l[l.Count - 1].BoundingBox.Bottom - w.BoundingBox.Bottom) < 1 &&
                                                     Math.Abs(l[0].Letters[0].PointSize - size) < 0.5 &&
                                                     w.BoundingBox.Left >= l[l.Count - 1].BoundingBox.Left &&
                                                     w.BoundingBox.Left - l[l.Count - 1].BoundingBox.Right < 2 * size);
                if (line == null) lines.Add(new List<Word> { w }); else line.Add(w);
            }
            var labels = new List<PdfLabel>();
            foreach (var l in lines)
            {
                string text = Regex.Replace(string.Join(" ", l.Select(w => w.Text)), @"(\d)[OØ]\b", "$1Ø");   // the Ø glyph often extracts as O
                var parsed = RiserLabel.Parse(text);
                if (parsed == null || !Useful(parsed)) continue;
                labels.Add(new PdfLabel { Floor = floor, Page = number, Text = text, Parsed = parsed, X = l[0].BoundingBox.Left, Y = l[0].BoundingBox.Bottom });
            }
            return labels;
        }

        /// <summary>Scale + shift from labels whose text appears exactly once on both; the worst anchor is dropped until all fit.</summary>
        private static PdfFloorFit Fit(string floor, int page, List<RiserLabel> dwg, List<PdfLabel> pdf, double radius)
        {
            var fit = new PdfFloorFit { Floor = floor, Page = page };
            var pairs = new List<(RiserLabel D, PdfLabel P)>();
            foreach (var g in dwg.GroupBy(l => Key(l)).Where(g => g.Count() == 1))
            {
                var ps = pdf.Where(p => Key(p.Parsed) == g.Key).ToList();
                if (ps.Count == 1) pairs.Add((g.First(), ps[0]));
            }
            while (true)
            {
                if (pairs.Count < 3) { fit.Problem = $"only {pairs.Count} label(s) appear once on both"; return fit; }
                double mx = pairs.Average(p => p.D.TextX), my = pairs.Average(p => p.D.TextY);
                double px = pairs.Average(p => p.P.X), py = pairs.Average(p => p.P.Y);
                double num = pairs.Sum(p => (p.D.TextX - mx) * (p.P.X - px) + (p.D.TextY - my) * (p.P.Y - py));
                double den = pairs.Sum(p => Math.Pow(p.D.TextX - mx, 2) + Math.Pow(p.D.TextY - my, 2));
                if (den <= 0 || num <= 0) { fit.Problem = "labels do not spread"; return fit; }
                fit.Scale = num / den; fit.Tx = px - fit.Scale * mx; fit.Ty = py - fit.Scale * my;
                var res = pairs.Select(p => (Pair: p, R: Dist((p.P.X - fit.Tx) / fit.Scale, (p.P.Y - fit.Ty) / fit.Scale, p.D.TextX, p.D.TextY))).OrderByDescending(t => t.R).ToList();
                fit.Residual = res[0].R; fit.Anchors = pairs.Count;
                if (fit.Residual <= radius / 2) { fit.Ok = true; return fit; }
                pairs.Remove(res[0].Pair);
            }
        }

        private static string Key(RiserLabel l) =>
            $"{l.Down}|{l.Up}|{l.GoesDown}|{l.GoesUp}|{l.Dryer}";

        /// <summary>Same sizes and directions (the text may differ in spacing, Ø written as %%C, a line break…).</summary>
        private static bool Equal(RiserLabel a, RiserLabel b) => Key(a) == Key(b);

        private static double Dist(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    }
}
