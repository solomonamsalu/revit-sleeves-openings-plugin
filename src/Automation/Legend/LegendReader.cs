using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;

namespace SleevesOpenings.Automation.Legend
{
    /// <summary>
    /// Reads tag definitions from one PDF page: an ABBREVIATIONS table (short code, wide gap, definition — in one or
    /// more column pairs), a SYMBOLS list (the tag drawn inside a symbol, its description on or just above it) and
    /// schedules (rows under a TAG / DESIGNATION / MARK header; meaning = SERVICE / TYPE / DESCRIPTION cells plus the
    /// schedule's title). Layout is found from geometry, not from fixed positions.
    /// </summary>
    public static class LegendReader
    {
        private class W
        {
            public string Text; public double X, R, Base, Size;
            public double Cx => (X + R) / 2;
        }

        private static readonly Regex AbbrToken = new Regex(@"^[A-Z(][A-Z/&().\-]{0,7}$");
        private static readonly Regex SymbolTag = new Regex(@"^[A-Z]{1,5}$");
        private static readonly Regex ScheduleTag = new Regex(@"^[A-Z]{1,4}-\d+[A-Z]?(/[A-Z]{1,4}-\d+[A-Z]?)?$");

        public static void ReadPage(Page page, IReadOnlyList<Word> words, Legend legend)
        {
            double h = page.Height, pageWidth = page.Width;
            var ws = words.Where(w => w.Letters.Count > 0 && !string.IsNullOrWhiteSpace(w.Text))
                          .Select(w => new W { Text = w.Text.Trim(), X = w.BoundingBox.Left, R = w.BoundingBox.Right, Base = h - w.BoundingBox.Bottom, Size = w.Letters[0].PointSize })
                          .Where(w => w.X < pageWidth * 0.88)          // right-hand title block
                          .ToList();

            foreach (var header in ws.Where(w => w.Text == "ABBREVIATIONS" && w.Size >= 12))
                Abbreviations(ws, header, legend, page.Number);
            foreach (var header in ws.Where(w => (w.Text == "SYMBOLS" || w.Text == "LEGEND") && w.Size >= 12))
                Symbols(ws, header, legend, page.Number);
            Schedules(ws, legend, page.Number);
        }

        private static IEnumerable<W> SameLine(List<W> ws, W w, double tol = 3.5) => ws.Where(o => Math.Abs(o.Base - w.Base) <= tol);

        // ------------------------------------------------------------------ abbreviations

        private static void Abbreviations(List<W> ws, W header, Legend legend, int page)
        {
            var below = ws.Where(w => w.Base > header.Base + 2 && w.Size < header.Size - 2).ToList();

            // Pair starts: a short code followed on its line by a word far to the right (the definition column).
            var starts = new List<(W Abbr, W Def)>();
            foreach (var w in below.Where(w => AbbrToken.IsMatch(w.Text)))
            {
                var next = SameLine(below, w).Where(o => o.X > w.R + 1).OrderBy(o => o.X).FirstOrDefault();
                if (next != null && next.X - w.R >= 25 && next.X - w.X <= 200) starts.Add((w, next));
            }

            // Columns: pair starts sharing a left edge; a real table column has several rows.
            var columns = starts.GroupBy(s => Math.Round(s.Abbr.X / 6))
                                .Where(g => g.Count() >= 5 && g.Min(s => s.Abbr.Base) - header.Base <= 80
                                            && g.Key * 6 > header.X - 450 && g.Key * 6 < header.X + 650)
                                .Select(g => (X: g.Min(s => s.Abbr.X), DefX: Median(g.Select(s => s.Def.X)), Rows: g.OrderBy(s => s.Abbr.Base).ToList()))
                                .OrderBy(c => c.X).ToList();

            for (int ci = 0; ci < columns.Count; ci++)
            {
                var col = columns[ci];
                double limit = ci + 1 < columns.Count ? columns[ci + 1].X - 4 : col.DefX + 420;
                double pitch = Median(col.Rows.Zip(col.Rows.Skip(1), (a, b) => b.Abbr.Base - a.Abbr.Base).Where(d => d > 4));
                if (double.IsNaN(pitch)) pitch = 14;

                // Rows of this column (stop where the column breaks off), abbreviation = words between the column and the definitions
                var rows = new List<(double Base, string Abbr)>();
                double last = col.Rows[0].Abbr.Base;
                foreach (var a in below.Where(o => Math.Abs(o.X - col.X) <= 4 && AbbrToken.IsMatch(o.Text) && o.Base >= col.Rows[0].Abbr.Base - 1).OrderBy(o => o.Base))
                {
                    if (a.Base - last > pitch * 3) break;
                    last = a.Base;
                    string abbr = string.Join(" ", SameLine(below, a).Where(o => o.X >= col.X - 2 && o.X < col.DefX - 5).OrderBy(o => o.X).Select(o => o.Text));
                    rows.Add((a.Base, abbr));
                }

                // Definition lines in the definition column, each given to the nearest row at or above it.
                var defs = below.Where(o => o.X >= col.DefX - 3 && o.X < limit && o.Base >= rows[0].Base - 4 && o.Base <= last + pitch)
                                .GroupBy(o => Math.Round(o.Base))
                                .Select(g => (Base: g.Key, Text: Contiguous(g.OrderBy(o => o.X).ToList(), col.DefX)))
                                .Where(d => d.Text.Length > 0)
                                .OrderBy(d => d.Base);
                var text = rows.ToDictionary(r => r.Base, r => "");
                foreach (var d in defs)
                {
                    var owner = rows.Where(r => r.Base <= d.Base + pitch * 0.45).OrderByDescending(r => r.Base).FirstOrDefault();
                    if (owner.Abbr == null) continue;
                    text[owner.Base] = (text[owner.Base] + " " + d.Text).Trim();
                }

                foreach (var r in rows)
                    foreach (var alias in Aliases(r.Abbr))
                        if (alias.Trim().Length > 0) legend.Add(alias.Trim(), text[r.Base], "abbreviations", page);
            }
        }

        // ------------------------------------------------------------------ symbols

        private static void Symbols(List<W> ws, W header, Legend legend, int page)
        {
            // Region: the column under the header, 500pt down. Tags only pair with text right next to them, so a
            // neighbouring column's heading must not cut the region short.
            var region = ws.Where(w => w.Base > header.Base + 2 && w.Base < header.Base + 500 && w.X > header.X - 150 && w.X < header.X + 500 && w.Size < header.Size - 2).ToList();

            foreach (var tag in region.Where(w => SymbolTag.IsMatch(w.Text)))
            {
                // Isolated: drawn inside a symbol, not a word of a sentence ("FIRE" in "FIRE DAMPER").
                if (SameLine(region, tag, 2).Any(o => o != tag && o.Text != tag.Text && (Math.Abs(o.X - tag.R) < 10 || Math.Abs(tag.X - o.R) < 10))) continue;
                var desc = region.Where(o => o.X > tag.R + 15 && o.Base <= tag.Base + 3 && tag.Base - o.Base <= 40)
                                 .GroupBy(o => Math.Round(o.Base))
                                 .Where(g => g.Min(o => o.X) < tag.R + 200)
                                 .Select(g => (Base: g.Key, Text: Contiguous(g.OrderBy(o => o.X).ToList(), g.Min(o => o.X))))
                                 .Where(d => d.Text.Split(' ').Length >= 2 && !d.Text.StartsWith("(") && Regex.IsMatch(d.Text, "[A-Z]{3,}"))
                                 .OrderBy(d => tag.Base - d.Base).FirstOrDefault();
                if (desc.Text != null) legend.Add(tag.Text, desc.Text, "symbols", page);
            }
        }

        // ------------------------------------------------------------------ schedules

        private static void Schedules(List<W> ws, Legend legend, int page)
        {
            var titles = ws.Where(w => w.Size >= 12).GroupBy(w => Math.Round(w.Base))
                           .Select(g => (Base: g.Key, X: g.Min(o => o.X), R: g.Max(o => o.R), Text: string.Join(" ", g.OrderBy(o => o.X).Select(o => o.Text))))
                           .ToList();
            var headers = ws.Where(w => w.Text == "TAG" || w.Text == "DESIGNATION" || w.Text == "MARK").OrderBy(w => w.X).ToList();

            foreach (var hdr in headers)
            {
                // The table's header band: words just around the TAG header, up to the next table to the right.
                double right = headers.Where(o => o.X > hdr.X + 100 && Math.Abs(o.Base - hdr.Base) < 60).Select(o => o.X - 20).DefaultIfEmpty(hdr.X + 1300).Min();
                var band = ws.Where(o => o.Base >= hdr.Base - 20 && o.Base <= hdr.Base + 25 && o.X >= hdr.X - 20 && o.X < right).OrderBy(o => o.X).ToList();
                var meaningCols = band.Where(o => Regex.IsMatch(o.Text, "^(SERVICE|TYPE|DESCRIPTION)$")).ToList();

                var title = titles.Where(t => t.Base < hdr.Base && hdr.Base - t.Base <= 120 && t.R > hdr.X - 50 && t.X < right)
                                  .OrderByDescending(t => t.Base).Select(t => t.Text).FirstOrDefault();

                double last = hdr.Base;
                foreach (var cell in ws.Where(o => o.Base > hdr.Base + 5 && o.Base < hdr.Base + 450 && o.Cx >= hdr.X - 25 && o.Cx <= hdr.R + 45 && ScheduleTag.IsMatch(o.Text))
                                       .OrderBy(o => o.Base))
                {
                    if (cell.Base - last > 75) break;                           // table ended
                    var line = SameLine(ws, cell).ToList();
                    if (line.Any(o => o.R <= cell.X && o.X >= hdr.X - 40)) continue;   // "2. EF-1 SHALL BE…" — a note, not a row
                    last = cell.Base;

                    var parts = new List<string>();
                    foreach (var mc in meaningCols)
                    {
                        var neighbours = band.Where(o => Math.Abs(o.Base - mc.Base) < 8).OrderBy(o => o.X).ToList();
                        int i = neighbours.IndexOf(mc);
                        double lo = i > 0 ? (neighbours[i - 1].Cx + mc.Cx) / 2 : mc.X - 60;
                        double hi = i + 1 < neighbours.Count ? (mc.Cx + neighbours[i + 1].Cx) / 2 : mc.R + 150;
                        string value = string.Join(" ", line.Where(o => o.Cx > lo && o.Cx < hi && o != cell).OrderBy(o => o.X).Select(o => o.Text));
                        if (Regex.IsMatch(value, "[A-Z]{3,}") && !parts.Contains(value)) parts.Add(value);   // "208/1" is electrical service, not a meaning
                    }
                    string definition = string.Join(" ", parts);
                    if (title != null) definition = (definition + $" ({title})").Trim();
                    foreach (var tag in cell.Text.Split('/'))
                        legend.Add(tag, definition, "schedule: " + (title ?? "untitled"), page);
                }
            }
        }

        /// <summary>"B OR BLR" -> B, BLR; "RR/RG" -> RR, RG; "W/" and "W/O" stay whole.</summary>
        private static IEnumerable<string> Aliases(string abbr)
        {
            foreach (var part in Regex.Split(abbr, @"\s+OR\s+"))
            {
                var halves = part.Split('/');
                if (halves.Length == 2 && halves[0].Length >= 2 && halves[1].Length >= 2) { yield return halves[0]; yield return halves[1]; }
                else yield return part;
            }
        }

        /// <summary>Words of one definition line: must start at the definition column and stop at the first wide gap (next text block).</summary>
        private static string Contiguous(List<W> line, double defX)
        {
            if (line.Count == 0 || line[0].X > defX + 6) return "";
            var take = new List<W> { line[0] };
            for (int i = 1; i < line.Count && line[i].X - line[i - 1].R <= 25; i++) take.Add(line[i]);
            return string.Join(" ", take.Select(o => o.Text));
        }

        private static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count == 0 ? double.NaN : v[v.Count / 2];
        }
    }
}
