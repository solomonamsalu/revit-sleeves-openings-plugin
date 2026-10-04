using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Project Setup: the manual's general rules (Owner's plan, latest file), confirmed for today and saved in the model.
    /// Auto Run asks the same when they are not confirmed, so nothing is placed without them. Levels, families and view
    /// ranges are Auto Run's work (the old setup steps live on in SetupForm / ProjectSetup, unused here).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectSetupCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }

            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                if (!WorkGate.Confirm(doc, rules, state)) return Result.Cancelled;

                new TaskDialog("Project Setup")
                {
                    MainInstruction = "Confirmed",
                    MainContent = $"{WorkGate.FileName(doc)}: confirmed by {Environment.UserName} on {DateTime.Now:MM/dd/yy HH:mm}." +
                                  (rules.GeneralRules.ReconfirmEveryDay ? "\n\nAuto Run will not ask again today." : "")
                }.Show();
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
