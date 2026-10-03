using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Sheets
{
    /// <summary>S&amp;O Set: makes / completes the SL sheets from the pattern sheet, writes the notes, prints the PDF.</summary>
    [Transaction(TransactionMode.Manual)]
    public class SoSetCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var rules = App.Rules(doc);
                var cfg = rules.SoSheets ?? new SoSheetRules();
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, rules, state);
                var plan = SoSheets.Plan(doc, rules, levels);
                App.Log($"S&O set: pattern {(plan.Pattern != null ? plan.Pattern.Describe() : "none")}; " +
                        string.Join("; ", plan.Floors.Select(f => $"{f.Level.Name} -> {f.Number} {f.Action}")) +
                        (plan.Problems.Count > 0 ? "; problems: " + string.Join("; ", plan.Problems) : ""));

                string folder = state.SoPdfFolder ?? DefaultFolder(doc, cfg, state);
                using (var form = new SoSetForm(plan, state.SoNotes, folder, SoSheets.PdfName(cfg, plan.ProjectName)))
                {
                    if (form.ShowDialog(SleevesOpenings.UI.RevitWindow.Instance) != DialogResult.OK) return Result.Cancelled;
                    state.SoNotes = form.Notes;
                    if (form.PrintPdf && !string.Equals(form.PdfFolderPath, DefaultFolder(doc, cfg, state), StringComparison.OrdinalIgnoreCase))
                        state.SoPdfFolder = form.PdfFolderPath;
                    using (var t = new Transaction(doc, "Sleeves & Openings: S&O notes"))
                    {
                        t.Start();
                        ProjectStore.Save(doc, state);
                        t.Commit();
                    }

                    var result = SoSheets.Apply(doc, rules, plan, state.SoNotes);
                    if (form.PrintPdf && result.SheetIds.Count > 0)
                        try { result.Pdf = SoSheets.ExportPdf(doc, rules, Ordered(doc, result), form.PdfFolderPath, form.PdfFileName); }
                        catch (Exception ex) { result.Problems.Add("PDF not printed: " + ex.Message); App.Log("S&O set: PDF failed: " + ex); }
                    App.Log("S&O set: " + result.Summary().Replace("\n", "; "));
                    TaskDialog.Show("S&O Set", result.Summary());
                }
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; App.Log("S&O set failed: " + ex); return Result.Failed; }
        }

        /// <summary>
        /// End of Auto Run in the PL / FP model (rules.json soSheets.afterAutoRun): sheets completed with the saved notes,
        /// then the PDF (pdfAfterAutoRun). Returns the lines for the Auto Run summary; null when it is off.
        /// </summary>
        public static string AfterAutoRun(Document doc, RuleSet rules, LevelMap levels, ProjectState state)
        {
            var cfg = rules.SoSheets ?? new SoSheetRules();
            if (!cfg.Enabled || !cfg.AfterAutoRun) return null;
            var plan = SoSheets.Plan(doc, rules, levels);
            if (plan.Pattern == null) return "S&O sheets not made: " + string.Join("; ", plan.Problems);
            var result = SoSheets.Apply(doc, rules, plan, state.SoNotes);
            if (cfg.PdfAfterAutoRun && result.SheetIds.Count > 0)
            {
                string folder = state.SoPdfFolder ?? DefaultFolder(doc, cfg, state);
                try { result.Pdf = SoSheets.ExportPdf(doc, rules, Ordered(doc, result), folder, SoSheets.PdfName(cfg, plan.ProjectName)); }
                catch (Exception ex) { result.Problems.Add("PDF not printed: " + ex.Message); App.Log("AutoRun: S&O PDF failed: " + ex); }
            }
            App.Log("AutoRun: S&O set: " + result.Summary().Replace("\n", "; "));
            return result.Summary();
        }

        /// <summary>The Structural folder; else next to the model; else Documents.</summary>
        private static string DefaultFolder(Document doc, SoSheetRules cfg, ProjectState state) =>
            SoSheets.PdfFolder(doc, cfg, state)
            ?? (!string.IsNullOrEmpty(doc.PathName) && Path.IsPathRooted(doc.PathName) ? Path.GetDirectoryName(doc.PathName) : null)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        private static System.Collections.Generic.List<ElementId> Ordered(Document doc, SoSetResult result) =>
            result.SheetIds.Select(id => doc.GetElement(id) as ViewSheet).Where(s => s != null)
                  .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).Select(s => s.Id).ToList();
    }
}
