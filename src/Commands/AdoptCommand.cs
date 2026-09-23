using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Placement;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Stamp openings/sleeves that were placed by hand (per rules.json "adopt") so Final Check, Riser Manager,
    /// Propagate and Schedule treat them like the add-in's own. Shows what it found first; nothing is moved or resized.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AdoptCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                if (!WorkGate.Ensure(doc, rules, state)) return Result.Cancelled;

                var report = new Adopter(doc, rules, state).Scan();
                App.Log($"Adopt: {report.Candidates.Count} candidates, {report.AlreadyStamped} already stamped, " +
                        $"{report.UnknownSystem.Values.Sum()} unknown system, {report.UnmatchedFamilies.Values.Sum()} unmatched");

                var td = new TaskDialog("Adopt Existing Openings")
                {
                    MainInstruction = report.Candidates.Count == 0
                        ? "Nothing to adopt"
                        : $"Adopt {report.Candidates.Count} existing opening(s) / sleeve(s)?",
                    MainContent = Summary(report),
                    ExpandedContent = Details(report),
                    CommonButtons = report.Candidates.Count == 0 ? TaskDialogCommonButtons.Close : TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.Yes
                };
                if (td.Show() != TaskDialogResult.Yes || report.Candidates.Count == 0) return Result.Cancelled;

                int written;
                using (var t = new Transaction(doc, "Sleeves & Openings: Adopt existing"))
                {
                    t.Start();
                    try { PlaceCommandBase.EnsureSharedParams(doc, rules, state); } catch (Exception ex) { App.Log("Adopt: shared params not bound: " + ex.Message); }
                    written = new Adopter(doc, rules, state).Apply(report.Candidates);
                    t.Commit();
                }
                App.Log($"Adopt: stamped {written}");
                TaskDialog.Show("Adopt Existing Openings",
                    $"{written} element(s) adopted. They now show in Riser Manager and are checked by Final Check.\n\n" +
                    "Adopted elements were not moved or resized. Run Check → Final Check next.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Adopt failed: " + ex);
                return Result.Failed;
            }
        }

        private static string Summary(AdoptReport r)
        {
            var sb = new StringBuilder();
            foreach (var g in r.BySystem)
            {
                int risers = g.Select(c => c.Riser).Distinct().Count();
                int levels = g.Select(c => c.Level.Id).Distinct().Count();
                sb.AppendLine($"{g.Key}: {g.Count()} on {levels} level(s), {risers} riser(s)");
            }
            if (r.AlreadyStamped > 0) sb.AppendLine($"\nAlready adopted / placed by the add-in: {r.AlreadyStamped} (skipped)");
            int unknown = r.UnknownSystem.Values.Sum(), unmatched = r.UnmatchedFamilies.Values.Sum();
            if (unknown > 0) sb.AppendLine($"System could not be decided: {unknown} (see details)");
            if (unmatched > 0) sb.AppendLine($"Sleeve/opening families not in rules.json 'adopt': {unmatched} (see details)");
            if (r.NoLevel > 0) sb.AppendLine($"No level / no location point: {r.NoLevel} (skipped)");
            return sb.ToString().TrimEnd();
        }

        private static string Details(AdoptReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("How each system was decided:");
            foreach (var g in r.Candidates.GroupBy(c => c.SystemSource ?? "?"))
                sb.AppendLine($"  by {g.Key}: {g.Count()}");

            if (r.UnknownSystem.Count > 0)
            {
                sb.AppendLine("\nMatched family but no system (add a nameToSystem pattern, a toggle prefix, or a default system to the matcher):");
                foreach (var kv in r.UnknownSystem.OrderByDescending(k => k.Value)) sb.AppendLine($"  {kv.Key}: {kv.Value}");
            }
            if (r.UnmatchedFamilies.Count > 0)
            {
                sb.AppendLine("\nFamilies that look like sleeves/openings but have no matcher in rules.json 'adopt':");
                foreach (var kv in r.UnmatchedFamilies.OrderByDescending(k => k.Value)) sb.AppendLine($"  {kv.Key}: {kv.Value}");
            }
            var sample = r.Candidates.Where(c => c.Label != null).Take(12).ToList();
            if (sample.Count > 0)
            {
                sb.AppendLine("\nSample:");
                foreach (var c in sample)
                {
                    string size = c.Diameter.HasValue ? Units.FormatInches(c.Diameter.Value)
                                : c.Width.HasValue ? $"{Units.FormatInches(c.Width.Value)} x {Units.FormatInches(c.Length ?? 0)}" : "size ?";
                    sb.AppendLine($"  {c.Level.Name}: {c.Instance.Symbol.Family.Name} '{c.Label}' → {c.System} {size}, riser {c.Riser}");
                }
            }
            sb.AppendLine("\nEdit the 'adopt' section with Setup → Edit Rules, then Reload Rules and run again.");
            return sb.ToString().TrimEnd();
        }
    }
}
