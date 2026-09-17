using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;

namespace SleevesOpenings.Commands
{
    /// <summary>F9 (first cut): modal riser list with select / zoom.</summary>
    [Transaction(TransactionMode.Manual)]
    public class RiserManagerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var doc = uidoc.Document;
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, App.Rules(doc), state);
                using (var form = new RiserManagerForm(uidoc, levels, state))
                    form.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("RiserManager failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
