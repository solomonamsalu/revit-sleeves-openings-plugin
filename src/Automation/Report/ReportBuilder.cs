using System;
using System.Collections.Generic;
using System.Linq;
using SleevesOpenings.Audit;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Compare;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Automation.Report
{
    /// <summary>Final Check on the openings one run placed: what it fixed on its own and what is left.</summary>
    public class FinalCheckRun
    {
        public int Checked;
        public List<(AuditIssue Issue, string Fix)> Fixed = new List<(AuditIssue, string)>();
        public List<AuditIssue> Remaining = new List<AuditIssue>();
        public List<string> Failed = new List<string>();

        public override string ToString() =>
            $"Final Check on the {Checked} opening(s) placed: {Fixed.Count} fixed automatically, " +
            $"{Remaining.Count(i => i.Severity == Severity.Error)} error(s), {Remaining.Count(i => i.Severity == Severity.Warning)} warning(s), " +
            $"{Remaining.Count(i => i.Severity == Severity.Info)} note(s) left" + (Failed.Count > 0 ? $"; {Failed.Count} fix(es) failed" : "");
    }

    /// <summary>Turns one Auto Run (drawings, placement, Final Check, S&amp;O comparison) into report rows.</summary>
    public static class ReportBuilder
    {
        public static RunReport Build(string model, DisciplineFiles files, AlignmentResult alignment, RiserAssembly assembly,
                                      List<PlacementOutcome> outcomes, SoCompareResult so, FinalCheckRun final, RuleSet rules, IEnumerable<string> lines)
        {
            var report = new RunReport { Model = model, RunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            report.Inputs.Add(("Mechanical PDF", files?.Pdf));
            report.Inputs.Add(("Mechanical DWG", files?.Dwg));
            if (!string.IsNullOrEmpty(files?.Xrefs)) report.Inputs.Add(("Xrefs at Revit 0,0", files.Xrefs));
            if (!string.IsNullOrEmpty(files?.Reference)) report.Inputs.Add(("S&O set compared", files.Reference));
            report.Lines.AddRange(lines ?? Enumerable.Empty<string>());
            if (assembly == null) return report;

            var outcome = new Dictionary<Crossing, PlacementOutcome>();
            foreach (var o in outcomes ?? new List<PlacementOutcome>()) outcome[o.Crossing] = o;
            var match = new Dictionary<Crossing, SoMatch>();
            foreach (var m in so?.Matches.Where(m => m.Ours != null) ?? Enumerable.Empty<SoMatch>()) match[m.Ours] = m;
            var byId = new Dictionary<long, Crossing>();
            foreach (var o in outcome.Values) foreach (var id in o.Ids) byId[id.Value] = o.Crossing;

            foreach (var c in assembly.Crossings)
            {
                outcome.TryGetValue(c, out var o);
                match.TryGetValue(c, out var m);
                var size = o?.Size;
                if (size == null) { var s = AutoPlacer.OpeningSize(rules, c); if (s.HasValue) size = $"{Units.FormatInches(s.Value.W)} x {Units.FormatInches(s.Value.L)}"; }
                var notes = new List<string>(c.Notes);
                if (o?.Detail != null) notes.Add(o.Detail);
                if (o != null && o.Warnings.Count > 0) notes.Add("rule warnings: " + string.Join("; ", o.Warnings));
                string result = o != null ? o.Result
                              : c.Status == Crossing.Review ? "review" : c.Status == Crossing.Skip ? "not placed" : "to place (not placed this run)";
                report.Rows.Add(new ReportRow
                {
                    Floor = c.Floor, Level = c.Level, Tag = c.Name, System = c.System,
                    DuctSize = c.Size?.ToString() ?? (c.System == "DryerExhaust" ? $"dryer x{c.Ducts}" : null),
                    OpeningSize = size, Result = result, Confidence = c.Confidence, Pdf = c.Pdf,
                    SoSet = m == null ? null : m.Status + (string.IsNullOrEmpty(m.Detail) ? "" : ": " + m.Detail),
                    Source = string.Join(" + ", c.From), Notes = string.Join("; ", notes),
                    X = c.HasPosition ? c.X : (double?)null, Y = c.HasPosition ? c.Y : (double?)null,
                    Ids = o?.Ids.Select(i => i.Value).ToList() ?? new List<long>(),
                    Attention = o == null ? c.Status != Crossing.Place
                              : o.Result == PlacementOutcome.Skipped || o.Result == PlacementOutcome.Failed || o.Warnings.Count > 0
                });
            }

            foreach (var i in assembly.Issues)
                report.Rows.Add(new ReportRow
                {
                    Section = ReportRow.Reported, Floor = i.Floor, Tag = i.Tag, Result = i.Type, Notes = i.Detail, X = i.X, Y = i.Y, Attention = true,
                    Level = assembly.Crossings.FirstOrDefault(c => c.Floor == i.Floor && c.Level != null)?.Level
                });

            foreach (var m in so?.Matches.Where(m => m.Ours == null) ?? Enumerable.Empty<SoMatch>())
                report.Rows.Add(new ReportRow
                {
                    Section = ReportRow.SoSetDiff, Floor = m.Floor, Tag = m.Theirs.Name, OpeningSize = m.Theirs.SizeText, Result = m.Status, Notes = m.Detail,
                    X = m.Theirs.X, Y = m.Theirs.Y, Level = assembly.Crossings.FirstOrDefault(c => c.Floor == m.Floor && c.Level != null)?.Level
                });

            if (final != null)
            {
                foreach (var (issue, fix) in final.Fixed)
                    report.Rows.Add(FinalRow(issue, byId, "fixed", $"{issue.Message} → {fix}", false));
                foreach (var issue in final.Remaining)
                    report.Rows.Add(FinalRow(issue, byId, $"{issue.Severity} ({issue.Rule})", issue.Message + (issue.CanFix ? $" (fix available in Final Check: {issue.FixLabel})" : ""),
                                             issue.Severity != Severity.Info));
            }

            // the tiles at the top
            int Count(Func<ReportRow, bool> f) => report.Rows.Count(f);
            if (outcomes != null)
            {
                report.Tiles.Add(("placed", Count(r => r.Section == ReportRow.Opening && r.Result == PlacementOutcome.Placed), "good"));
                report.Tiles.Add(("already in the model", Count(r => r.Section == ReportRow.Opening && (r.Result == PlacementOutcome.Existing || r.Result == PlacementOutcome.Resized)), "good"));
            }
            else report.Tiles.Add(("to place", Count(r => r.Section == ReportRow.Opening && r.Result.StartsWith("to place")), "good"));
            report.Tiles.Add(("to review", Count(r => r.Section == ReportRow.Opening && r.Result == "review"), "warn"));
            report.Tiles.Add(("not placed", Count(r => r.Section == ReportRow.Opening && (r.Result == "not placed" || r.Result == PlacementOutcome.Skipped || r.Result == PlacementOutcome.Failed)), "bad"));
            report.Tiles.Add(("reported", Count(r => r.Section == ReportRow.Reported), "warn"));
            if (final != null) report.Tiles.Add(("Final Check errors/warnings", final.Remaining.Count(i => i.Severity != Severity.Info), final.Remaining.Any(i => i.Severity == Severity.Error) ? "bad" : "warn"));
            if (so != null && so.Floors.Count > 0)
            {
                report.Tiles.Add(("same as the S&O set", so.Matches.Count(m => m.Agrees), "good"));
                report.Tiles.Add(("differ from the S&O set", so.Matches.Count(m => !m.Agrees), "warn"));
            }
            return report;
        }

        private static ReportRow FinalRow(AuditIssue issue, Dictionary<long, Crossing> byId, string result, string notes, bool attention)
        {
            var c = issue.Elements.Select(e => byId.TryGetValue(e.Value, out var x) ? x : null).FirstOrDefault(x => x != null);
            return new ReportRow
            {
                Section = ReportRow.FinalCheck, Floor = c?.Floor, Level = issue.Level ?? c?.Level, Tag = issue.Riser ?? c?.Name, System = issue.System,
                Result = result, Notes = notes, Ids = issue.Elements.Select(e => e.Value).ToList(), Attention = attention,
                X = c?.X, Y = c?.Y
            };
        }
    }
}
