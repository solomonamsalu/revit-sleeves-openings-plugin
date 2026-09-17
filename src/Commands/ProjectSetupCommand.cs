using System;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>Feature 2: level classification, preflight checklist, family loading, view range.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectSetupCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            var doc = uidoc.Document;

            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);

                // Manual p.2: confirm the drawing set before anything else is set up.
                if (!WorkGate.Ensure(doc, rules, state)) return Result.Cancelled;

                var levels = LevelClassifier.Classify(doc, rules, state);

                using (var form = new SetupForm(rules, levels, state))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    form.Apply();

                    var report = new SetupReport();
                    using (var t = new Transaction(doc, "Sleeves & Openings: Project Setup"))
                    {
                        t.Start();
                        ProjectStore.Save(doc, state);
                        if (form.RunLoadFamilies) ProjectSetup.LoadFamilies(doc, rules, report);
                        if (form.RunViewRange) ProjectSetup.ApplyViewRange(doc, rules, levels, report);
                        t.Commit();
                    }

                    var summary =
                        $"Lowest level: {levels.Lowest?.Name ?? "-"}\n" +
                        $"Highest apartment floor (start here): {levels.HighestApartment?.Name ?? "-"}\n" +
                        $"Main roof: {levels.MainRoof?.Name ?? "-"}\n" +
                        $"Setbacks: {(levels.Setbacks.Any() ? string.Join(", ", levels.Setbacks.Select(l => l.Name)) : "-")}\n" +
                        $"Bulkhead: {levels.Bulkhead?.Name ?? "-"}\n\n" +
                        report;

                    var td = new TaskDialog("Project Setup complete")
                    {
                        MainInstruction = report.Warnings > 0 ? $"{report.Warnings} warning(s) — see details" : "Setup applied",
                        MainContent = summary
                    };
                    td.Show();
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("ProjectSetup failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
