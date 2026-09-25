using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Report
{
    /// <summary>One line of the Auto Run report: an opening, something reported, a Final Check finding or an S&amp;O set difference.</summary>
    public class ReportRow
    {
        public const string Opening = "opening", Reported = "reported", FinalCheck = "final check", SoSetDiff = "S&O set";

        public string Section = Opening;
        public string Floor;                 // FloorKey
        public string Level;                 // Revit level name
        public string Tag, System;
        public string DuctSize;              // from the drawings
        public string OpeningSize;           // placed (with clearances)
        public string Result;                // placed, already in the model, review, not placed, <issue type>, Error…
        public string Confidence, Pdf, SoSet;
        public string Source;                // where it was read
        public string Notes;
        public double? X, Y;                 // Revit feet
        public List<long> Ids = new List<long>();
        /// <summary>Someone has to look at it: not placed, placed with a warning, a Final Check error…</summary>
        public bool Attention;

        public string Where => Floor == null ? Level ?? "-" : FloorKey.Describe(Floor) + (Level != null && !string.Equals(Level, FloorKey.Describe(Floor), StringComparison.OrdinalIgnoreCase) ? $" ({Level})" : "");
    }

    /// <summary>
    /// Plan section 9: the report of one Auto Run, saved next to risers.json as report.html (read in a browser, printable)
    /// and report.csv (every row, for Excel). The review list in Revit shows the same rows. Free of the Revit API.
    /// </summary>
    public class RunReport
    {
        public string Model, RunAt;
        public List<(string What, string File)> Inputs = new List<(string, string)>();
        public List<string> Lines = new List<string>();                   // the summary (alignment, counts, views…)
        public List<(string Label, int Count, string Tone)> Tiles = new List<(string, int, string)>();
        public List<ReportRow> Rows = new List<ReportRow>();

        public IEnumerable<ReportRow> Attention => Rows.Where(r => r.Attention);

        public (string Html, string Csv) Write(string folder)
        {
            Directory.CreateDirectory(folder);
            string html = Path.Combine(folder, "report.html"), csv = Path.Combine(folder, "report.csv");
            File.WriteAllText(html, Html(), Encoding.UTF8);
            File.WriteAllText(csv, Csv(), new UTF8Encoding(true));
            return (html, csv);
        }

        // ---------------------------------------------------------------- CSV

        private string Csv()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", new[] { "Section", "Floor", "Level", "Tag", "System", "Duct size", "Opening size", "Result", "Needs attention",
                                                   "Confidence", "PDF", "S&O set", "Read from", "Notes", "X (ft)", "Y (ft)", "Element ids" }.Select(Q)));
            foreach (var r in Rows)
                sb.AppendLine(string.Join(",", new[] { r.Section, FloorKey.Describe(r.Floor), r.Level, r.Tag, r.System, r.DuctSize, r.OpeningSize, r.Result,
                                                       r.Attention ? "yes" : "", r.Confidence, r.Pdf, r.SoSet, r.Source, r.Notes,
                                                       r.X?.ToString("0.####"), r.Y?.ToString("0.####"), string.Join(" ", r.Ids) }.Select(Q)));
            return sb.ToString();
        }

        private static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        // ---------------------------------------------------------------- HTML

        private string Html()
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append($"<title>Auto Run report - {E(Model)}</title><style>{Css}</style></head><body><main>");
            sb.Append($"<header><h1>Sleeves &amp; Openings — Auto Run</h1><p class=\"sub\">{E(Model)} · {E(RunAt)}</p></header>");

            sb.Append("<section><h2>Inputs</h2><table class=\"kv\">");
            foreach (var (what, file) in Inputs)
                sb.Append($"<tr><th>{E(what)}</th><td>{E(file == null ? "-" : Path.GetFileName(file))}{Dated(file)}</td></tr>");
            sb.Append("</table></section>");

            if (Tiles.Count > 0)
            {
                sb.Append("<section class=\"tiles\">");
                foreach (var (label, count, tone) in Tiles) sb.Append($"<div class=\"tile {tone}\"><b>{count}</b><span>{E(label)}</span></div>");
                sb.Append("</section>");
            }
            if (Lines.Count > 0) sb.Append("<section><h2>Summary</h2><ul>" + string.Concat(Lines.Select(l => $"<li>{E(l)}</li>")) + "</ul></section>");

            var attention = Attention.ToList();
            sb.Append($"<section><h2>Needs attention ({attention.Count})</h2>");
            sb.Append(attention.Count == 0 ? "<p class=\"ok\">Nothing: every opening was placed and passes Final Check.</p>" : Table(attention, true));
            sb.Append("</section>");

            var openings = Rows.Where(r => r.Section == ReportRow.Opening).ToList();
            sb.Append($"<section><h2>Openings from the drawings ({openings.Count})</h2>");
            foreach (var g in openings.GroupBy(r => r.Floor).OrderByDescending(g => FloorKey.Order(g.Key)))
                sb.Append($"<h3>{E(g.First().Where)}</h3>" + Table(g.ToList(), false));
            sb.Append("</section>");

            foreach (var (section, title, blurb) in new[]
            {
                (ReportRow.Reported, "Reported, not placed", "Seen on the drawings but not placed: the reason is in the notes."),
                (ReportRow.FinalCheck, "Final Check on the openings placed", "The manual's checks after placing; fixes with one right answer were applied and are listed."),
                (ReportRow.SoSetDiff, "Openings only in the S&O set", "Drawn in the office's Sleeves & Openings set with nothing from the drawings there (the set may be of an older revision).")
            })
            {
                var rows = Rows.Where(r => r.Section == section).ToList();
                if (rows.Count == 0) continue;
                sb.Append($"<section><h2>{E(title)} ({rows.Count})</h2><p class=\"note\">{E(blurb)}</p>{Table(rows, true)}</section>");
            }
            sb.Append("<footer>Positions are Revit internal coordinates (feet). The review list in Revit (Auto Run) zooms to each row; report.csv has every row.</footer>");
            sb.Append("</main></body></html>");
            return sb.ToString();
        }

        private static string Table(List<ReportRow> rows, bool floorColumn)
        {
            var sb = new StringBuilder("<div class=\"scroll\"><table><thead><tr>");
            if (floorColumn) sb.Append("<th>Floor</th>");
            sb.Append("<th>Tag</th><th>System</th><th>Duct</th><th>Opening</th><th>Result</th><th>S&amp;O set</th><th>Notes</th></tr></thead><tbody>");
            foreach (var r in rows)
            {
                sb.Append($"<tr class=\"{Tone(r)}\">");
                if (floorColumn) sb.Append($"<td>{E(r.Where)}</td>");
                sb.Append($"<td class=\"tag\">{E(r.Tag ?? "-")}</td><td>{E(r.System)}</td><td>{E(r.DuctSize)}</td><td>{E(r.OpeningSize)}</td>");
                sb.Append($"<td><span class=\"pill\">{E(r.Result)}</span></td><td>{E(r.SoSet)}</td>");
                sb.Append($"<td>{E(r.Notes)}{(string.IsNullOrEmpty(r.Source) ? "" : $"<div class=\"src\">{E(r.Source)}</div>")}</td></tr>");
            }
            return sb.Append("</tbody></table></div>").ToString();
        }

        private static string Tone(ReportRow r)
        {
            string x = (r.Result ?? "").ToLowerInvariant();
            if (x == "placed" || x == "already in the model" || x == "resized") return r.Attention ? "warn" : "good";
            if (x == "review" || x.StartsWith("warning") || x == "only in the s&o set") return "warn";
            return r.Attention ? "bad" : "";
        }

        private static string Dated(string file)
        {
            try { return file != null && File.Exists(file) ? $" <span class=\"src\">({File.GetLastWriteTime(file):yyyy-MM-dd HH:mm})</span>" : ""; }
            catch { return ""; }
        }

        private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

        private const string Css = @"
:root{--bg:#f6f7f9;--card:#fff;--ink:#1d2330;--muted:#5b6475;--line:#dde1e8;--good:#e7f5ea;--warn:#fff6d6;--bad:#fde8e8;--accent:#2f5d9b}
@media (prefers-color-scheme:dark){:root{--bg:#15181e;--card:#1e222a;--ink:#e6e9ef;--muted:#9aa3b2;--line:#323845;--good:#1f3326;--warn:#3a3419;--bad:#3d2224;--accent:#8fb4ea}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.45 'Segoe UI',system-ui,sans-serif}
main{max-width:1200px;margin:0 auto;padding:24px 16px 48px}h1{margin:0;font-size:24px}h2{font-size:18px;margin:28px 0 8px}h3{font-size:15px;margin:18px 0 6px;color:var(--accent)}
.sub,.note,footer,.src{color:var(--muted)}.src{font-size:12px}footer{margin-top:32px;font-size:12px}
section{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:4px 16px 12px;margin-top:16px}
.tiles{display:flex;flex-wrap:wrap;gap:10px;padding:12px}.tile{flex:1 1 130px;border-radius:6px;padding:10px 12px;border:1px solid var(--line)}
.tile b{display:block;font-size:24px}.tile span{color:var(--muted);font-size:12px}.tile.good{background:var(--good)}.tile.warn{background:var(--warn)}.tile.bad{background:var(--bad)}
.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--muted);font-weight:600}.kv th{width:140px}tr.good td{background:var(--good)}tr.warn td{background:var(--warn)}tr.bad td{background:var(--bad)}
.tag{font-weight:600;white-space:nowrap}.pill{white-space:nowrap}.ok{color:var(--muted)}
@media print{body{background:#fff}section{break-inside:avoid-page}}";
    }
}
