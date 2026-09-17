using System;
using System.IO;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;
using SleevesOpenings.UI;

namespace SleevesOpenings.Setup
{
    /// <summary>
    /// Enforces the manual's general rules before any work: every working command calls
    /// <see cref="Ensure"/> first. Confirmation is stored per project and re-asked for a new user,
    /// a new day (if configured) or a renamed file.
    /// </summary>
    public static class WorkGate
    {
        public static bool IsConfirmed(RuleSet rules, ProjectState state, Document doc)
        {
            var c = state.Confirmation;
            if (c == null) return false;
            if (!string.Equals(c.User, Environment.UserName, StringComparison.OrdinalIgnoreCase)) return false;
            if (rules.GeneralRules.ReconfirmEveryDay && c.Date.Date != DateTime.Today) return false;
            if (!string.Equals(c.FileName, FileName(doc), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>Returns true when work may proceed. Shows the gate if not yet confirmed today. Call with no open transaction.</summary>
        public static bool Ensure(Document doc, RuleSet rules, ProjectState state)
        {
            if (IsConfirmed(rules, state, doc)) return true;

            using (var form = new GeneralRulesForm(rules.GeneralRules, FileName(doc), Environment.UserName))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    App.Log("General rules not confirmed — work blocked");
                    return false;
                }
            }

            state.Confirmation = new WorkConfirmation { User = Environment.UserName, Date = DateTime.Now, FileName = FileName(doc) };
            using (var t = new Transaction(doc, "Sleeves & Openings: General rules confirmed"))
            {
                t.Start();
                ProjectStore.Save(doc, state);
                t.Commit();
            }
            App.Log($"General rules confirmed by {Environment.UserName} for {FileName(doc)}");
            return true;
        }

        public static string FileName(Document doc) =>
            string.IsNullOrEmpty(doc.PathName) ? doc.Title : Path.GetFileName(doc.PathName);
    }
}
