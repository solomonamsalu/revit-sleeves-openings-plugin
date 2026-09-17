using System;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Roof;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>F5: copy the top apartment floor's openings to a roof level with roof sizes and spacing.</summary>
    [Transaction(TransactionMode.Manual)]
    public class RoofCommand : IExternalCommand
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
                var levels = LevelClassifier.Classify(doc, rules, state);

                if (!WorkGate.Ensure(doc, rules, state)) return Result.Cancelled;

                var names = levels.All.Select(l => l.Name).Reverse().ToArray();
                var roofNames = levels.All.Where(l => l.Role == LevelRole.Roof || l.Role == LevelRole.Setback).Select(l => l.Name).Reverse().ToArray();
                if (roofNames.Length == 0)
                {
                    TaskDialog.Show("Generate Roof", "No level is classified as Roof or Setback. Run Project Setup and set the roof level's role.");
                    return Result.Cancelled;
                }

                var form = new InputForm("Generate Roof Openings",
                    "Copies the source floor's exhaust, chute, dryer, damper, refrigeration and electrical openings to the roof " +
                    "with the roof sizes (+4\" exhaust, 6\"x6\" dryer, +3\" refrigeration, 2\" ELECTRIC) and pushes them apart to meet " +
                    "the spacing rules (1' from walls/curbs, 2' between openings, 8\" between dryers).",
                    new[]
                    {
                        new InputForm.Field { Key = "src", Label = "Source floor", Choices = names, Default = levels.HighestApartment?.Name },
                        new InputForm.Field { Key = "roof", Label = "Roof / setback level", Choices = roofNames, Default = levels.MainRoof?.Name ?? roofNames[0] },
                        new InputForm.Field { Key = "space", Label = "Auto-space openings", Choices = new[] { "Yes", "No" }, Default = "Yes" },
                        new InputForm.Field { Key = "skip", Label = "Skip risers already on roof", Choices = new[] { "Yes", "No" }, Default = "Yes" },
                    });
                if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;

                var source = levels.All.First(l => l.Name == form.Value("src")).Level;
                var roof = levels.All.First(l => l.Name == form.Value("roof")).Level;

                PlaceCommandBase.EnsureSharedParams(doc, rules, state);

                RoofReport report;
                using (var t = new Transaction(doc, "Sleeves & Openings: Generate roof openings"))
                {
                    t.Start();
                    var view = doc.ActiveView is ViewPlan vp ? vp : new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);
                    report = new RoofGenerator(doc, rules, state, levels).Run(view, source, roof, form.Value("space") == "Yes", form.Value("skip") == "Yes");
                    t.Commit();
                }

                App.Log($"Roof: {report.Created} created, {report.Skipped} skipped, {report.Moved} moved on {roof.Name}");
                new TaskDialog("Generate Roof Openings")
                {
                    MainInstruction = $"{report.Created} created on {roof.Name}, {report.Skipped} skipped, {report.Moved} moved for spacing",
                    MainContent = report + "\n\nOpen the roof plan to review; run Final Check for fire paths, curbs and bulkhead coordination (rules 60-64 need your eyes)."
                }.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Roof failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
