using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Audit;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;

namespace SleevesOpenings.Commands
{
    /// <summary>F8: run every "Final Check" from the manual against the model.</summary>
    [Transaction(TransactionMode.Manual)]
    public class FinalCheckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var doc = uidoc.Document;
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, rules, state);

                var auditor = new Auditor(doc, rules, state, levels);
                var issues = auditor.Run();
                App.Log($"FinalCheck: {auditor.OpeningCount} openings, {issues.Count} issues");

                List<AuditIssue> Rerun() => new Auditor(doc, rules, ProjectStore.Load(doc), levels).Run();
                using (var form = new AuditForm(uidoc, issues, auditor.OpeningCount, Rerun))
                    form.ShowDialog(UI.RevitWindow.Instance);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("FinalCheck failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
