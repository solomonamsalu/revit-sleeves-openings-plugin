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

        [Newtonsoft.Json.JsonIgnore] public string Where => Floor == null ? Level ?? "-" : FloorKey.Describe(Floor) + (Level != null && !string.Equals(Level, FloorKey.Describe(Floor), StringComparison.OrdinalIgnoreCase) ? $" ({Level})" : "");
    }

    /// <summary>
    /// Plan section 9: the report of one Auto Run, saved next to risers.json as report.html (read in a browser, printable)
    /// and report.csv (every row, for Excel). The review list in Revit shows the same rows. Free of the Revit API.
    /// </summary>
    public class RunReport
    {
        public string Model, RunAt;
        /// <summary>Mechanical, Plumbing or Sprinkler: shown at the top and in the inputs.</summary>
        public string Discipline;
        public List<(string What, string File)> Inputs = new List<(string, string)>();
        public List<string> Lines = new List<string>();                   // the summary (alignment, counts, views…)
        public List<(string Label, int Count, string Tone)> Tiles = new List<(string, int, string)>();
        public List<ReportRow> Rows = new List<ReportRow>();
        /// <summary>The Auto Run's end-of-run summary, shown again when the review list is reopened (Last Report).</summary>
        public string Summary;

        [Newtonsoft.Json.JsonIgnore] public IEnumerable<ReportRow> Attention => Rows.Where(r => r.Attention);

        public const string JsonFile = "report.json";

        public (string Html, string Csv) Write(string folder)
        {
            Directory.CreateDirectory(folder);
            string html = Path.Combine(folder, "report.html"), csv = Path.Combine(folder, "report.csv");
            File.WriteAllText(html, Html(), Encoding.UTF8);
            File.WriteAllText(csv, Csv(), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(folder, JsonFile), Newtonsoft.Json.JsonConvert.SerializeObject(this), Encoding.UTF8);
            return (html, csv);
        }

        /// <summary>The report saved by <see cref="Write"/> (report.json), or null when the folder has none.</summary>
        public static RunReport Read(string folder)
        {
            string path = Path.Combine(folder, JsonFile);
            return File.Exists(path) ? Newtonsoft.Json.JsonConvert.DeserializeObject<RunReport>(File.ReadAllText(path, Encoding.UTF8)) : null;
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

        /// <summary>A row's status as the page shows it: filter key, badge text, colour.</summary>
        private static (string Key, string Label, string Tone) Status(ReportRow r)
        {
            string x = (r.Result ?? "").ToLowerInvariant();
            switch (r.Section)
            {
                case ReportRow.Reported: return ("reported", Cap(r.Result ?? "reported"), "info");
                case ReportRow.SoSetDiff: return ("so", "Only in the S&O set", "warn");
                case ReportRow.FinalCheck:
                    if (x == "fixed") return ("fixed", "Fixed", "muted");
                    if (x.StartsWith("error")) return ("check", r.Result, "bad");
                    if (x.StartsWith("warning")) return ("check", r.Result, "warn");
                    return ("check", r.Result, "muted");
            }
            if (x == "placed") return r.Attention ? ("placed", "Placed · warning", "warn") : ("placed", "Placed", "good");
            if (x == "already in the model" || x == "resized") return ("existing", x == "resized" ? "Resized" : "Already in model", "good");
            if (x == "review") return ("review", "Review", "warn");
            if (x == ReportBuilder.Merged) return ("merged", "In combined opening", "good");
            if (x.StartsWith("to place")) return ("toplace", "To place", "good");
            return ("notplaced", "Not placed", "bad");
        }

        /// <summary>How urgent a row is in the "needs attention" list (lower first).</summary>
        private static int Severity(ReportRow r)
        {
            var (key, _, tone) = Status(r);
            if (tone == "bad") return 0;
            if (key == "review") return 1;
            if (key == "reported" || key == "so") return 2;
            return 3;
        }

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private string Html()
        {
            var openings = Rows.Where(r => r.Section == ReportRow.Opening).ToList();
            var attention = Attention.OrderBy(Severity).ThenByDescending(r => FloorKey.Order(r.Floor)).ThenBy(r => r.Tag).ToList();
            string verdict = Lines.FirstOrDefault(l => l.StartsWith("Result:", StringComparison.OrdinalIgnoreCase));
            bool passed = verdict != null && verdict.IndexOf("NOT PASSED", StringComparison.OrdinalIgnoreCase) < 0 && verdict.IndexOf("PASSED", StringComparison.OrdinalIgnoreCase) >= 0;
            bool pdfOnly = Lines.Any(l => l.StartsWith("DWG: none", StringComparison.OrdinalIgnoreCase)) || Inputs.Any(i => i.What.EndsWith("DWG") && i.File == null);
            int placed = openings.Count(r => Status(r).Key == "placed");

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append($"<title>Auto Run report · {E(Model)}</title><style>{Css}</style></head><body><main>");

            // ---- header: what ran, on what, and the verdict
            sb.Append("<header class=\"top\"><div><p class=\"eyebrow\">Sleeves &amp; Openings · Auto Run report</p>");
            sb.Append($"<h1>{E(Model)}</h1><p class=\"meta\">");
            if (!string.IsNullOrEmpty(Discipline)) sb.Append($"<span class=\"chip\">{E(Discipline)}</span>");
            sb.Append(pdfOnly ? "<span class=\"chip accent\">PDF only</span>" : "<span class=\"chip\">PDF + DWG</span>");
            sb.Append($"<span>{E(RunAt)}</span></p></div>");
            if (verdict != null)
                sb.Append($"<div class=\"verdict {(passed ? "good" : "bad")}\"><b>{(passed ? "Position check passed" : "Position check not passed")}</b>" +
                          $"<span>{(passed ? $"{placed} opening(s) placed" : "nothing was placed")}</span></div>");
            sb.Append("</header>");

            // ---- tiles: click one to filter the openings list
            var tiles = new List<(string Key, string Label, int Count, string Tone)>
            {
                ("placed", "Placed", placed, "good"),
                ("existing", "Already in the model", openings.Count(r => Status(r).Key == "existing"), "good"),
                ("review", "To review", openings.Count(r => Status(r).Key == "review"), "warn"),
                ("notplaced", "Not placed", openings.Count(r => Status(r).Key == "notplaced"), "bad"),
                ("reported", "Reported", Rows.Count(r => r.Section == ReportRow.Reported), "info"),
                ("check", "Final Check issues", Rows.Count(r => r.Section == ReportRow.FinalCheck && r.Attention), "bad")
            };
            if (openings.Any(r => Status(r).Key == "toplace")) tiles.Insert(0, ("toplace", "To place", openings.Count(r => Status(r).Key == "toplace"), "good"));
            sb.Append("<nav class=\"tiles\" aria-label=\"Totals\">");
            foreach (var t in tiles.Where(t => t.Count > 0 || t.Key == "placed" || t.Key == "review"))
                sb.Append($"<button class=\"tile {t.Tone}\" data-filter=\"{t.Key}\" type=\"button\"><b>{t.Count}</b><span>{E(t.Label)}</span></button>");
            foreach (var t in Tiles.Where(t => t.Label.IndexOf("S&O", StringComparison.Ordinal) >= 0))
                sb.Append($"<div class=\"tile {t.Tone} static\"><b>{t.Count}</b><span>{E(t.Label)}</span></div>");
            sb.Append("</nav>");

            // ---- needs attention, most urgent first
            sb.Append($"<section id=\"attention\"><div class=\"head\"><h2>Needs attention</h2><span class=\"count\">{attention.Count}</span></div>");
            if (attention.Count == 0) sb.Append("<p class=\"empty\">Nothing to look at: every opening was placed and passes Final Check.</p>");
            else
            {
                sb.Append("<p class=\"hint\">Grouped by urgency. In Revit, the review list zooms to each row.</p>");
                // the few that block (not placed, errors) and the openings to decide on stay open; warnings fold away
                foreach (var (title, open, rows) in new[]
                {
                    ("Not placed and errors", true, attention.Where(r => Severity(r) == 0).ToList()),
                    ("To review", true, attention.Where(r => Severity(r) == 1).ToList()),
                    ("Needs a decision", true, attention.Where(r => Severity(r) == 2).ToList()),
                    ("Warnings (placed, check when convenient)", false, attention.Where(r => Severity(r) == 3).ToList())
                })
                {
                    if (rows.Count == 0) continue;
                    sb.Append($"<details class=\"group\"{(open ? " open" : "")}><summary><h3>{E(title)}</h3><span class=\"count\">{rows.Count}</span></summary>{Table(rows, true, false)}</details>");
                }
            }
            sb.Append("</section>");

            // ---- every opening, by floor, with search and filters
            sb.Append($"<section id=\"openings\"><div class=\"head\"><h2>Openings by floor</h2><span class=\"count\" id=\"shown\">{openings.Count}</span></div>");
            sb.Append("<div class=\"tools\"><input id=\"q\" type=\"search\" placeholder=\"Search tag, system, notes…\" aria-label=\"Search\">");
            sb.Append("<div class=\"filters\" role=\"group\" aria-label=\"Status\"><button type=\"button\" class=\"on\" data-filter=\"\">All</button>");
            foreach (var t in tiles.Where(t => t.Count > 0 && t.Key != "reported" && t.Key != "check"))
                sb.Append($"<button type=\"button\" data-filter=\"{t.Key}\">{E(t.Label)}</button>");
            sb.Append("</div></div>");
            var floors = openings.GroupBy(r => r.Floor).OrderByDescending(g => FloorKey.Order(g.Key)).ToList();
            sb.Append("<div class=\"floornav\">");
            foreach (var g in floors) sb.Append($"<a href=\"#f-{Id(g.Key)}\">{E(FloorKey.Describe(g.Key))} <small>{g.Count()}</small></a>");
            sb.Append("</div>");
            foreach (var g in floors)
                sb.Append($"<div class=\"floor\" id=\"f-{Id(g.Key)}\"><h3>{E(g.First().Where)}</h3>{Table(g.OrderBy(r => r.Tag).ToList(), false, true)}</div>");
            sb.Append("<p class=\"empty\" id=\"none\" hidden>No opening matches.</p></section>");

            // ---- the other sections
            foreach (var (section, title, blurb) in new[]
            {
                (ReportRow.Reported, "Reported, not placed", "Seen on the drawings but not placed. The reason is in the notes."),
                (ReportRow.FinalCheck, "Final Check on the openings placed", "The manual's checks after placing. Fixes with one right answer were applied and are listed as Fixed."),
                (ReportRow.SoSetDiff, "Only in the S&O set", "Drawn in the office's Sleeves & Openings set with nothing from the drawings there (the set may be an older revision).")
            })
            {
                var rows = Rows.Where(r => r.Section == section).ToList();
                if (rows.Count == 0) continue;
                sb.Append($"<details class=\"block\"><summary><h2>{E(title)}</h2><span class=\"count\">{rows.Count}</span></summary>");
                sb.Append($"<p class=\"hint\">{E(blurb)}</p>{Table(rows.OrderBy(Severity).ThenByDescending(r => FloorKey.Order(r.Floor)).ToList(), true, false)}</details>");
            }

            // ---- inputs and the run log
            sb.Append("<details class=\"block\"><summary><h2>Drawings and run log</h2></summary><div class=\"files\">");
            foreach (var (what, file) in Inputs)
                sb.Append($"<div class=\"file\"><span>{E(what)}</span><b>{E(file == null ? "none" : Path.GetFileName(file))}</b>{Dated(file)}</div>");
            sb.Append("</div><ul class=\"log\">");
            foreach (var l in Lines) sb.Append($"<li class=\"{(l.StartsWith("Result:") ? (passed ? "good" : "bad") : l.StartsWith("NOT placed") ? "bad" : "")}\">{E(l)}</li>");
            sb.Append("</ul></details>");

            sb.Append("<footer>Positions are Revit internal coordinates (feet). report.csv next to this page has every row for Excel.</footer>");
            sb.Append($"</main><script>{Js}</script></body></html>");
            return sb.ToString();
        }

        private static string Id(string floor) => System.Text.RegularExpressions.Regex.Replace(floor ?? "none", "[^A-Za-z0-9]", "");

        private static string Table(List<ReportRow> rows, bool floorColumn, bool filterable)
        {
            var sb = new StringBuilder($"<div class=\"scroll\"><table{(filterable ? " class=\"filterable\"" : "")}><thead><tr>");
            if (floorColumn) sb.Append("<th>Floor</th>");
            sb.Append("<th>Tag</th><th>Status</th><th>System</th><th>Duct</th><th>Opening</th><th class=\"wide\">Notes</th></tr></thead><tbody>");
            foreach (var r in rows)
            {
                var (key, label, tone) = Status(r);
                string text = string.Join(" ", r.Tag, r.System, r.DuctSize, r.OpeningSize, label, r.Notes, r.Source, FloorKey.Describe(r.Floor)).ToLowerInvariant();
                sb.Append($"<tr data-s=\"{key}\" data-t=\"{E(text)}\">");
                if (floorColumn) sb.Append($"<td class=\"nowrap\">{E(r.Floor == null ? r.Level ?? "-" : FloorKey.Describe(r.Floor))}</td>");
                sb.Append($"<td class=\"tag\">{E(r.Tag ?? "-")}</td><td><span class=\"badge {tone}\">{E(label)}</span></td>");
                sb.Append($"<td>{E(Spaced(r.System))}</td><td class=\"nowrap\">{E(r.DuctSize)}</td><td class=\"nowrap\">{E(r.OpeningSize)}</td>");
                sb.Append($"<td>{Notes(r)}</td></tr>");
            }
            return sb.Append("</tbody></table></div>").ToString();
        }

        /// <summary>The notes as short points: the first two shown, the rest (and where it was read) behind "more".</summary>
        private static string Notes(ReportRow r)
        {
            var points = (r.Notes ?? "").Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (r.SoSet != null) points.Add("S&O set: " + r.SoSet);
            var sb = new StringBuilder();
            if (points.Count > 0) sb.Append("<ul class=\"pts\">" + string.Concat(points.Take(2).Select(p => $"<li>{E(Cap(p))}</li>")) + "</ul>");
            var more = points.Skip(2).ToList();
            if (more.Count > 0 || !string.IsNullOrEmpty(r.Source))
            {
                sb.Append($"<details class=\"more\"><summary>{(more.Count > 0 ? $"{more.Count} more" : "read from")}</summary>");
                if (more.Count > 0) sb.Append("<ul class=\"pts\">" + string.Concat(more.Select(p => $"<li>{E(Cap(p))}</li>")) + "</ul>");
                if (!string.IsNullOrEmpty(r.Source)) sb.Append($"<p class=\"src\">Read from: {E(r.Source)}</p>");
                sb.Append("</details>");
            }
            return sb.Length == 0 ? "<span class=\"src\">-</span>" : sb.ToString();
        }

        /// <summary>"DryerExhaust" -> "Dryer exhaust".</summary>
        private static string Spaced(string s) => string.IsNullOrEmpty(s) ? "-" : Cap(System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant().Replace("erv", "ERV"));

        private static string Dated(string file)
        {
            try { return file != null && File.Exists(file) ? $"<small>{File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}</small>" : ""; }
            catch { return ""; }
        }

        private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

        private const string Css = @"
:root{--bg:#f4f5f7;--card:#fff;--ink:#1b2130;--muted:#5f6878;--line:#e2e5ea;--accent:#2f5d9b;--accent-bg:#e8eef8;
--good:#1f7a3d;--good-bg:#e5f4ea;--warn:#8a5a00;--warn-bg:#fdf2d3;--bad:#b42318;--bad-bg:#fde7e5;--info:#4b3fa8;--info-bg:#ece9fb;--mut-bg:#eef0f3}
@media (prefers-color-scheme:dark){:root{--bg:#14171c;--card:#1c2028;--ink:#e7eaf0;--muted:#9aa3b2;--line:#2e3440;--accent:#8fb4ea;--accent-bg:#1f2b3d;
--good:#7fd19b;--good-bg:#183223;--warn:#f0c968;--warn-bg:#3a3017;--bad:#ff9b8f;--bad-bg:#3d1f1d;--info:#b6adf5;--info-bg:#28243f;--mut-bg:#262b34}}
*{box-sizing:border-box}html{scroll-behavior:smooth}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 'Segoe UI',system-ui,-apple-system,sans-serif}
main{max-width:1240px;margin:0 auto;padding:28px 20px 56px}
h1{margin:2px 0 6px;font-size:26px;letter-spacing:-.01em}h2{font-size:17px;margin:0}h3{font-size:14px;margin:22px 0 8px;color:var(--accent);text-transform:uppercase;letter-spacing:.04em}
.top{display:flex;flex-wrap:wrap;gap:16px;justify-content:space-between;align-items:flex-end}
.eyebrow{margin:0;color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.08em}
.meta{margin:0;display:flex;flex-wrap:wrap;gap:8px;align-items:center;color:var(--muted)}
.chip{border:1px solid var(--line);background:var(--card);border-radius:999px;padding:1px 10px;font-size:12px;color:var(--ink)}
.chip.accent{background:var(--accent-bg);border-color:transparent;color:var(--accent);font-weight:600}
.verdict{border-radius:10px;padding:10px 16px;display:flex;flex-direction:column;min-width:220px}
.verdict b{font-size:15px}.verdict span{font-size:13px;opacity:.85}
.verdict.good{background:var(--good-bg);color:var(--good)}.verdict.bad{background:var(--bad-bg);color:var(--bad)}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:10px;margin:22px 0 6px}
.tile{font:inherit;text-align:left;cursor:pointer;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 14px;color:var(--ink)}
.tile.static{cursor:default}.tile:hover:not(.static){border-color:var(--accent)}.tile.on{outline:2px solid var(--accent);outline-offset:1px}
.tile b{display:block;font-size:26px;line-height:1.1}.tile span{color:var(--muted);font-size:12px}
.tile.good{background:var(--good-bg);color:var(--good);border-color:transparent}.tile.warn{background:var(--warn-bg);color:var(--warn);border-color:transparent}
.tile.bad{background:var(--bad-bg);color:var(--bad);border-color:transparent}.tile.info{background:var(--info-bg);color:var(--info);border-color:transparent}
.tile.good span,.tile.warn span,.tile.bad span,.tile.info span{color:inherit;opacity:.85}
section,.block{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px 18px;margin-top:16px}
.head,.block>summary{display:flex;align-items:center;gap:10px}
.block>summary{cursor:pointer;list-style:none}.block>summary::-webkit-details-marker{display:none}
.block>summary::after{content:'▸';margin-left:auto;color:var(--muted)}.block[open]>summary::after{content:'▾'}
.count{background:var(--mut-bg);border-radius:999px;padding:0 9px;font-size:12px;font-weight:600;color:var(--muted)}
.hint,.empty{color:var(--muted);margin:8px 0 10px}.empty{padding:6px 0}
.group{margin-top:10px}.group>summary{display:flex;align-items:center;gap:8px;cursor:pointer;list-style:none;padding:6px 0}
.group>summary::-webkit-details-marker{display:none}.group>summary::before{content:'▸';color:var(--muted);width:10px}.group[open]>summary::before{content:'▾'}
.group>summary h3{margin:0}
.tools{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin:12px 0 4px}
#q{flex:1 1 240px;font:inherit;padding:7px 12px;border-radius:8px;border:1px solid var(--line);background:var(--bg);color:var(--ink)}
.filters{display:flex;flex-wrap:wrap;gap:6px}
.filters button{font:inherit;font-size:12px;cursor:pointer;border:1px solid var(--line);background:var(--card);color:var(--ink);border-radius:999px;padding:3px 11px}
.filters button.on{background:var(--accent);border-color:var(--accent);color:#fff}
.floornav{position:sticky;top:0;z-index:2;display:flex;flex-wrap:wrap;gap:6px;padding:10px 0;background:var(--card);border-bottom:1px solid var(--line)}
.floornav a{font-size:12px;text-decoration:none;color:var(--accent);background:var(--accent-bg);border-radius:6px;padding:2px 9px}
.floornav small{color:var(--muted)}
.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:13px}
th{position:sticky;top:0;text-align:left;font-weight:600;color:var(--muted);font-size:12px;padding:6px 8px;border-bottom:1px solid var(--line);background:var(--card)}
td{padding:7px 8px;border-bottom:1px solid var(--line);vertical-align:top}tbody tr:hover td{background:var(--bg)}
th.wide{width:45%}.tag{font-weight:600;white-space:nowrap}.nowrap{white-space:nowrap}
.badge{display:inline-flex;align-items:center;gap:6px;white-space:nowrap;border-radius:999px;padding:1px 9px 1px 8px;font-size:12px;font-weight:600}
.badge::before{content:'';width:7px;height:7px;border-radius:50%;background:currentColor}
.badge.good{background:var(--good-bg);color:var(--good)}.badge.warn{background:var(--warn-bg);color:var(--warn)}.badge.bad{background:var(--bad-bg);color:var(--bad)}
.badge.info{background:var(--info-bg);color:var(--info)}.badge.muted{background:var(--mut-bg);color:var(--muted)}
.pts{margin:0;padding-left:16px}.pts li{margin:1px 0}
.more summary{cursor:pointer;color:var(--accent);font-size:12px;margin-top:2px}.src{color:var(--muted);font-size:12px;margin:4px 0 0}
.files{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:8px;margin:12px 0}
.file{border:1px solid var(--line);border-radius:8px;padding:8px 12px;display:flex;flex-direction:column}
.file span,.file small{color:var(--muted);font-size:12px}.file b{word-break:break-all}
.log{margin:6px 0 0;padding-left:18px;color:var(--ink)}.log li{margin:2px 0}.log li.good{color:var(--good);font-weight:600}.log li.bad{color:var(--bad);font-weight:600}
footer{margin-top:28px;color:var(--muted);font-size:12px;text-align:center}
[hidden]{display:none!important}
@media print{body{background:#fff}.tools,.floornav{display:none}section,.block{break-inside:auto;border-color:#ccc}th{position:static}}";

        /// <summary>Tiles and chips filter the openings list; the search box narrows it; printing opens every collapsed part.</summary>
        private const string Js = @"
(function(){
var state={s:'',q:''};
var rows=[].slice.call(document.querySelectorAll('table.filterable tbody tr'));
function apply(){
  var n=0;
  rows.forEach(function(tr){var ok=(!state.s||tr.dataset.s===state.s)&&(!state.q||tr.dataset.t.indexOf(state.q)>=0);tr.hidden=!ok;if(ok)n++;});
  document.querySelectorAll('.floor').forEach(function(f){f.hidden=!f.querySelector('tbody tr:not([hidden])');});
  document.querySelectorAll('.floornav a').forEach(function(a){var f=document.querySelector(a.getAttribute('href'));a.hidden=f&&f.hidden;});
  document.getElementById('shown').textContent=n;document.getElementById('none').hidden=n>0;
  document.querySelectorAll('[data-filter]').forEach(function(b){b.classList.toggle('on',b.dataset.filter===state.s&&(b.dataset.filter!==''||b.closest('.filters')));});
}
document.querySelectorAll('[data-filter]').forEach(function(b){b.addEventListener('click',function(){
  var f=b.dataset.filter;
  if(f==='reported'||f==='check'){var t=f==='reported'?'Reported':'Final Check';
    document.querySelectorAll('details.block').forEach(function(d){if(d.querySelector('summary h2').textContent.indexOf(t)===0){d.open=true;d.scrollIntoView();}});return;}
  state.s=(b.classList.contains('tile')&&state.s===f)?'':f;apply();
  if(b.classList.contains('tile'))document.getElementById('openings').scrollIntoView();
});});
document.getElementById('q').addEventListener('input',function(e){state.q=e.target.value.trim().toLowerCase();apply();});
window.addEventListener('beforeprint',function(){document.querySelectorAll('details').forEach(function(d){d.open=true;});});
apply();
})();";
    }
}
