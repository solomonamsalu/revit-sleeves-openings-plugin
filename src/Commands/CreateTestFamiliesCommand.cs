using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Generates simple parametric test families (rect opening + round sleeve), loads them into the
    /// project and maps them to every role, so the Place buttons can be tried without office RFAs.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CreateTestFamiliesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            if (doc.IsFamilyDocument) { message = "Open a project, not a family."; return Result.Failed; }

            try
            {
                var td = new TaskDialog("Create Test Families")
                {
                    MainInstruction = "Create and load test families?",
                    MainContent =
                        $"Creates two Generic Model families with the Revit API:\n" +
                        $"  • {TestFamilyFactory.OpeningFamilyName}  (Width x Length, instance parameters)\n" +
                        $"  • {TestFamilyFactory.SleeveFamilyName}  (Diameter, instance parameter)\n\n" +
                        "They are loaded into this project and mapped to all roles (Map Families), " +
                        "so every Place button works immediately. Replace them with the office families later.",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
                };
                if (td.Show() != TaskDialogResult.Ok) return Result.Cancelled;

                // LoadFamily manages its own transaction and refuses to run inside an open one,
                // so build + load first, then save the mapping in a separate transaction.
                var (opening, sleeve, dir) = TestFamilyFactory.CreateAndLoad(uiapp.Application, doc);

                using (var t = new Transaction(doc, "Sleeves & Openings: Map test families"))
                {
                    t.Start();
                    var state = ProjectStore.Load(doc);
                    state.FamilyMap = TestFamilyFactory.Mapping();
                    ProjectStore.Save(doc, state);
                    t.Commit();
                }

                TaskDialog.Show("Create Test Families",
                    $"Loaded:\n  • {opening?.Name}\n  • {sleeve?.Name}\n\nSaved to:\n{dir}\n\n" +
                    "Now open a floor plan and try Place → Garbage Chute (no input needed), then Storm, Area Drain, Exhaust...");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Failed at step '{TestFamilyFactory.Step}': {ex.Message}";
                App.Log($"CreateTestFamilies failed at '{TestFamilyFactory.Step}': {ex}");
                return Result.Failed;
            }
        }
    }
}
