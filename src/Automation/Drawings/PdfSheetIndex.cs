using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using SleevesOpenings.Automation.Legend;

namespace SleevesOpenings.Automation.Drawings
{
    public enum SheetKind { FloorPlan, Legend, RiserDiagram, Schedule, Other }

    /// <summary>One page of an engineer's PDF set and what it holds.</summary>
    public class PdfSheet
    {
        public int Page;
        public string SheetNumber;       // "M-305.00" when the title block shows one
        public string Title;             // largest plan title on the page, e.g. "5TH FLOOR PLAN"
        public string Floor;             // FloorKey, null when not a single-floor plan
        public bool HasAbbreviations;    // an ABBREVIATIONS table header is on the page
        public bool HasSymbols;
        public bool HasRiserDiagram;
        public bool HasSchedule;
        public int Words;

        public SheetKind Kind =>
            Floor != null ? SheetKind.FloorPlan :
            HasRiserDiagram ? SheetKind.RiserDiagram :
            HasAbbreviations || HasSymbols ? SheetKind.Legend :
            HasSchedule ? SheetKind.Schedule : SheetKind.Other;
    }

    public class PdfSheetIndex
    {
        public string Path;
        public int PageCount;
        public string Discipline;        // "M", "P", "SP", "FP", "A", "S", "E" from sheet-number prefixes
        public List<PdfSheet> Sheets = new List<PdfSheet>();
        public List<string> Warnings = new List<string>();
        /// <summary>Tag definitions from the abbreviations table, symbols list and schedules.</summary>
        public SleevesOpenings.Automation.Legend.Legend Legend = new SleevesOpenings.Automation.Legend.Legend();

        public IEnumerable<PdfSheet> FloorPlans => Sheets.Where(s => s.Floor != null);

        private static readonly Regex SheetNo = new Regex(@"^(M|P|SP|FP|FA|A|S|E|H|PL)-\d{3}(\.\d{2})?$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads every page: the plan title is the largest-font line that names exactly one floor; a drawing index
        /// (many floor titles in a small font) is not a plan. Pages are classified, nothing else is extracted yet.
        /// </summary>
        public static PdfSheetIndex Read(string path)
        {
            var index = new PdfSheetIndex { Path = path };
            var prefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using (var pdf = PdfDocument.Open(path))
            {
                index.PageCount = pdf.NumberOfPages;
                foreach (var page in pdf.GetPages())
                {
                    var words = page.GetWords().ToList();
                    var lines = Lines(words);
                    var sheet = new PdfSheet { Page = page.Number, Words = words.Count };

                    // Plan title: the biggest text naming a floor; if the biggest size names several floors it is an index.
                    var titled = lines.Where(l => l.Text.Length <= 60 && FloorKey.Find(l.Text).Count > 0 && Regex.IsMatch(l.Text, @"\bPLAN\b", RegexOptions.IgnoreCase))
                                      .ToList();
                    if (titled.Count > 0)
                    {
                        double top = titled.Max(l => l.Size);
                        var biggest = titled.Where(l => l.Size >= top - 0.5).ToList();
                        var floors = biggest.Select(l => FloorKey.FromPlanTitle(l.Text)).Where(k => k != null).Distinct().ToList();
                        if (floors.Count == 1 && top >= 10)
                        {
                            sheet.Floor = floors[0];
                            sheet.Title = biggest.First(l => FloorKey.FromPlanTitle(l.Text) == floors[0]).Text;
                        }
                    }

                    double big = lines.Count == 0 ? 0 : lines.Max(l => l.Size);
                    sheet.HasAbbreviations = lines.Any(l => Regex.IsMatch(l.Text, @"^ABBREVIATIONS?$", RegexOptions.IgnoreCase));
                    sheet.HasSymbols = lines.Any(l => Regex.IsMatch(l.Text, @"^(SYMBOLS?|SYMBOL LIST|LEGEND)$", RegexOptions.IgnoreCase));
                    // Diagram / schedule headings are large; the drawing index mentions them in small text only.
                    sheet.HasRiserDiagram = lines.Any(l => l.Size >= 10 && Regex.IsMatch(l.Text, @"RISER\s+DIAGRAM", RegexOptions.IgnoreCase));
                    sheet.HasSchedule = lines.Any(l => l.Size >= 10 && Regex.IsMatch(l.Text, @"\bSCHEDULES?\b", RegexOptions.IgnoreCase));

                    if (sheet.Floor == null && (sheet.HasAbbreviations || sheet.HasSymbols || sheet.HasSchedule))
                    {
                        try { LegendReader.ReadPage(page, words, index.Legend); }
                        catch (Exception ex) { index.Warnings.Add($"Page {page.Number}: legend could not be read ({ex.Message})."); }
                    }

                    // Sheet number: the token in the title block; the index page lists many, so count only pages with one.
                    var numbers = words.Select(w => w.Text.Trim()).Where(t => SheetNo.IsMatch(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (numbers.Count == 1)
                    {
                        sheet.SheetNumber = numbers[0].ToUpperInvariant();
                        string p = sheet.SheetNumber.Split('-')[0];
                        prefixes[p] = prefixes.TryGetValue(p, out int n) ? n + 1 : 1;
                    }
                    index.Sheets.Add(sheet);
                }
            }

            index.Discipline = prefixes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            foreach (var dup in index.FloorPlans.GroupBy(s => s.Floor).Where(g => g.Count() > 1))
                index.Warnings.Add($"{FloorKey.Describe(dup.Key)} appears on pages {string.Join(", ", dup.Select(s => s.Page))}; the first is used.");
            return index;
        }

        private class Line { public string Text; public double Size; }

        /// <summary>Words grouped into text lines by baseline and font size (drawing text is rarely one PDF line).</summary>
        private static List<Line> Lines(List<Word> words)
        {
            return words.Where(w => w.Letters.Count > 0)
                .GroupBy(w => (Math.Round(w.BoundingBox.Bottom), Math.Round(w.Letters[0].PointSize, 1)))
                .Select(g => new Line
                {
                    Text = Regex.Replace(string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)), @"\s+", " ").Trim(),
                    Size = g.Key.Item2
                })
                .ToList();
        }
    }
}
