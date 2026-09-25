using System;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.UI;
using SleevesOpenings.Setup;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Auto Run (AUTOMATION_PLAN.md): places sleeves/openings from the engineer's PDF + DWG.
    /// Built so far: step 1 (what is already in the model) and step 2 (pick and check the drawings, match floors
    /// to levels). The choices are saved in the project; extraction and placement come in the next phases.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoRunCommand : IExternalCommand
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
                state = ProjectStore.Load(doc);                 // the gate may have saved a confirmation
                var levels = LevelClassifier.Classify(doc, rules, state);

                var existing = ExistingCheck.Run(doc, rules, state);
                App.Log($"AutoRun: existing — {existing.Items.Count} item(s), {existing.UnknownFamilies.Values.Sum()} unrecognised");

                PdfSheetIndex pdf; DwgSheetIndex dwg; int risersFound;
                using (var form = new AutoRunForm(existing, state.Automation, levels, rules.Legend, rules.DwgProfile))
                {
                    if (form.ShowDialog(SleevesOpenings.UI.RevitWindow.Instance) != DialogResult.OK) return Result.Cancelled;
                    pdf = form.Pdf; dwg = form.Dwg; risersFound = form.Risers?.Risers.Count ?? 0;
                }

                using (var t = new Transaction(doc, "Sleeves & Openings: Auto Run inputs"))
                {
                    t.Start();
                    ProjectStore.Save(doc, state);
                    t.Commit();
                }

                var files = state.Automation.For(AutomationInputs.Mechanical);
                App.Log($"AutoRun: inputs saved — PDF '{files.Pdf}', DWG '{files.Dwg}', existing = {state.Automation.Existing}, " +
                        $"PDF floors {pdf?.FloorPlans.Count() ?? 0}, DWG floors {dwg?.Floors.Count ?? 0}");

                new TaskDialog("Auto Run")
                {
                    MainInstruction = "Drawings checked and saved for this project",
                    MainContent =
                        $"PDF: {(pdf == null ? "none" : $"{pdf.FloorPlans.Count()} floor plan(s)")}\n" +
                        $"DWG: {(dwg == null ? "none" : $"{dwg.Floors.Count} floor plan(s)")}\n" +
                        $"Risers found in the DWG: {risersFound}\n" +
                        $"Tags defined in the PDF: {pdf?.Legend.Entries.Select(e => e.Tag).Distinct().Count() ?? 0}\n" +
                        $"Existing sleeves/openings: {existing.Items.Count} ({(state.Automation.Existing == ExistingPolicy.Update ? "update" : "keep and add missing")})\n\n" +
                        "Tags and risers are read. Lining up with Revit and placing openings are the next build phases; nothing was placed.",
                    CommonButtons = TaskDialogCommonButtons.Close
                }.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("AutoRun failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
