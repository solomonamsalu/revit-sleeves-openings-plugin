using System;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;

namespace SleevesOpenings.Commands
{
    /// <summary>Choose which loaded families/parameters stand in for each role. Saved in the project.</summary>
    [Transaction(TransactionMode.Manual)]
    public class MapFamiliesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                return ShowAndSave(doc, App.Rules(doc), ProjectStore.Load(doc)) ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        public static bool ShowAndSave(Document doc, RuleSet rules, ProjectState state)
        {
            using (var form = new FamilyMapForm(doc, rules, state))
            {
                if (form.ShowDialog() != DialogResult.OK) return false;
                form.Apply();
                using (var t = new Transaction(doc, "Sleeves & Openings: Map Families"))
                {
                    t.Start();
                    ProjectStore.Save(doc, state);
                    t.Commit();
                }
                return true;
            }
        }
    }
}
