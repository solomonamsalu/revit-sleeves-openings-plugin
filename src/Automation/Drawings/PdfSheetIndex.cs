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
        public string Floor;             // FloorKey, null when not a floor plan
        /// <summary>A typical plan ("2ND THRU 7TH FLOOR PLAN"): every floor it is drawn for; null for a one-floor plan.
        /// FloorPlans holds one entry per floor of the range, all on this page.</summary>
        public List<string> Typical;
        /// <summary>Elevations (feet) the plan prints for its floor, every datum shown (FloorElevations); empty when none.</summary>
        public List<double> Elevations = new List<double>();
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

        /// <summary>One entry per floor: a typical plan gives one per floor of its range (same page).</summary>
        public IEnumerable<PdfSheet> FloorPlans => _floorPlans ?? Sheets.Where(s => s.Floor != null);
        private List<PdfSheet> _floorPlans;

        private static readonly Regex SheetNo = new Regex(@"^(M|P|SP|FP|FA|A|S|E|H|PL)-\d{3}(\.\d{2})?$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads every page: the plan title is the largest-font line that names one floor, or a range of floors drawn
        /// once (a typical plan, "2ND THRU 7TH FLOOR PLAN"); a drawing index (many floor titles in a small font) is not a
        /// plan. Pages are classified, nothing else is extracted yet.
        /// </summary>
        public static PdfSheetIndex Read(string path)
        {
            var index = new PdfSheetIndex { Path = path };
            var copies = new List<PdfSheet>();            // a typical plan's other floors
            var prefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using (var pdf = PdfDocument.Open(path))
            {
                index.PageCount = pdf.NumberOfPages;
                foreach (var page in pdf.GetPages())
                {
                    var words = page.GetWords().ToList();
                    var lines = Lines(words);
                    var sheet = new PdfSheet { Page = page.Number, Words = words.Count };

                    // Plan title: the biggest text naming a floor (or a typical plan's range); if the biggest size names
                    // different floors it is an index.
                    var titled = lines.Where(l => l.Text.Length <= 60 && FloorKey.Find(l.Text).Count > 0 && Regex.IsMatch(l.Text, @"\bPLAN\b", RegexOptions.IgnoreCase))
                                      .ToList();
                    if (titled.Count > 0)
                    {
                        double top = titled.Max(l => l.Size);
                        var biggest = titled.Where(l => l.Size >= top - 0.5).ToList();
                        var sets = biggest.Select(l => (Line: l, Floors: FloorKey.PlanFloors(l.Text))).Where(t => t.Floors.Count > 0).ToList();
                        var distinct = sets.Select(t => string.Join(",", t.Floors)).Distinct().ToList();
                        if (distinct.Count == 1 && top >= 10)
                        {
                            var (title, floors) = sets[0];
                            sheet.Floor = floors[0];
                            sheet.Title = title.Text;
                            if (floors.Count == 1) sheet.Elevations = FloorElevations.FromPage(lines, sheet.Floor, title);
                            else
                            {
                                sheet.Typical = floors;
                                sheet.Elevations = FloorElevations.FromPage(lines, floors[0], title, namedOnly: true);
                                foreach (var f in floors.Skip(1))
                                    copies.Add(new PdfSheet
                                    {
                                        Page = sheet.Page, Words = sheet.Words, Title = sheet.Title, Floor = f, Typical = floors,
                                        Elevations = FloorElevations.FromPage(lines, f, title, namedOnly: true)
                                    });
                                index.Warnings.Add($"Page {page.Number} is a typical plan ('{title.Text}'): used for each of {FloorKey.DescribeRange(floors)}.");
                            }
                        }
                        else if (top >= 10 && biggest.Any(l => FloorKey.Find(l.Text).Count > 1 && FloorKey.PlanFloors(l.Text).Count == 0))
                            index.Warnings.Add($"Page {page.Number}: '{biggest.First(l => FloorKey.Find(l.Text).Count > 1).Text}' names several floors that are not one range: not used (split it, or match those floors by hand).");
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
            foreach (var c in copies) c.SheetNumber = index.Sheets.First(s => s.Page == c.Page).SheetNumber;
            // a floor drawn on its own page wins over the same floor in a typical plan's range
            var own = new HashSet<string>(index.Sheets.Where(s => s.Floor != null && s.Typical == null).Select(s => s.Floor));
            foreach (var c in index.Sheets.Where(s => s.Typical != null).Concat(copies).Where(s => own.Contains(s.Floor)))
                index.Warnings.Add($"{FloorKey.Describe(c.Floor)} has its own page ({index.Sheets.First(s => s.Floor == c.Floor && s.Typical == null).Page}) and is also in the " +
                                   $"typical plan on page {c.Page} ({FloorKey.DescribeRange(c.Typical)}): its own page is used. Check that page's title.");
            index._floorPlans = index.Sheets.Where(s => s.Floor != null).Concat(copies)
                .Where(s => s.Typical == null || !own.Contains(s.Floor))
                .OrderBy(s => s.Page).ThenBy(s => FloorKey.Order(s.Floor)).ToList();
            foreach (var dup in index.FloorPlans.GroupBy(s => s.Floor).Where(g => g.Count() > 1))
                index.Warnings.Add($"{FloorKey.Describe(dup.Key)} appears on pages {string.Join(", ", dup.Select(s => s.Page))}; the first is used.");
            return index;
        }

        /// <summary>Words grouped into text lines by baseline and font size (drawing text is rarely one PDF line).</summary>
        private static List<TextLine> Lines(List<Word> words)
        {
            return words.Where(w => w.Letters.Count > 0)
                .GroupBy(w => (Math.Round(w.BoundingBox.Bottom), Math.Round(w.Letters[0].PointSize, 1)))
                .Select(g => new TextLine
                {
                    Text = Regex.Replace(string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)), @"\s+", " ").Trim(),
                    Size = g.Key.Item2,
                    X = g.Min(w => w.BoundingBox.Left),
                    Right = g.Max(w => w.BoundingBox.Right),
                    Y = g.Key.Item1
                })
                .ToList();
        }
    }
}
