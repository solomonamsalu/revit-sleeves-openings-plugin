using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Report;
using SleevesOpenings.Automation.UI;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Last Report: reopens the review list of this model's latest Auto Run (report.json in its run folder) without
    /// running again. Runs saved before report.json existed open their report.html instead.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LastReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var modelFolder = Path.GetDirectoryName(RiserListFile.RunFolder(doc.Title, DateTime.Now));
                var runs = Directory.Exists(modelFolder)
                    ? Directory.GetDirectories(modelFolder).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList()
                    : new System.Collections.Generic.List<string>();
                // the newest run that can reopen the review list; else the newest browser report (older versions)
                var folder = runs.FirstOrDefault(f => File.Exists(Path.Combine(f, RunReport.JsonFile)))
                          ?? runs.FirstOrDefault(f => File.Exists(Path.Combine(f, "report.html")));
                if (folder == null)
                {
                    TaskDialog.Show("Last Report", $"No Auto Run report for '{doc.Title}' yet. Run Auto Run first.");
                    return Result.Cancelled;
                }

                string html = Path.Combine(folder, "report.html");
                var report = RunReport.Read(folder);
                if (report == null)
                {
                    // saved by an older version: only the browser report exists
                    Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
                    return Result.Succeeded;
                }

                var rules = App.Rules(doc);
                var levels = LevelClassifier.Classify(doc, rules, ProjectStore.Load(doc));
                string summary = $"Auto Run of {Path.GetFileName(folder)} (reopened; nothing was run again).\n\n" + (report.Summary ?? "") +
                                 (File.Exists(html) ? $"\n\nReport: {html}" : "");
                using (var review = new ReviewForm(uidoc, report, levels, summary, rules.SleeveViews?.NameSuffix, File.Exists(html) ? html : null, folder))
                    review.ShowDialog(SleevesOpenings.UI.RevitWindow.Instance);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Last Report failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
