using System;
using System.Collections.Generic;
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
                var state = ProjectStore.Load(doc);
                var cfg = SoProjectSettings.Apply(rules.SoSheets, state.SoSettings);
                var levels = LevelClassifier.Classify(doc, rules, state);
                var plan = SoSheets.Plan(doc, rules, cfg, levels);
                App.Log($"S&O set: pattern {(plan.Pattern != null ? plan.Pattern.Describe() : "none")}; " +
                        string.Join("; ", plan.Floors.Select(f => $"{f.Level.Name} -> {f.Number} {f.Action}")) +
                        (plan.Problems.Count > 0 ? "; problems: " + string.Join("; ", plan.Problems) : ""));

                string folder = state.SoPdfFolder ?? DefaultFolder(doc, cfg, state);
                var fields = SoSheets.ReadFields(doc, plan.Pattern);
                string suffix = rules.SleeveViews?.NameSuffix ?? " Sleeves";
                // Templates tab: this model's S&O sheet saved as a template (only when it has one)
                Func<string, List<string>> saveTemplate = plan.Pattern == null ? (Func<string, List<string>>)null
                    : path => SoTemplates.Save(doc, plan.Pattern, suffix, path);
                using (var form = new SoSetForm(plan, state.SoNotes, fields, rules.SoSheets, state.SoSettings, folder,
                                                mine => SoSheets.Plan(doc, rules, SoProjectSettings.Apply(rules.SoSheets, mine), levels),
                                                doc.PathName, saveTemplate, () => { rules = App.ReloadRules(doc); return rules.SoSheets; }))
                {
                    if (form.ShowDialog(SleevesOpenings.UI.RevitWindow.Instance) != DialogResult.OK) return Result.Cancelled;
                    state.SoNotes = form.Notes;
                    state.SoSettings = form.Settings;
                    if (form.PrintPdf && !string.Equals(form.PdfFolderPath, DefaultFolder(doc, cfg, state), StringComparison.OrdinalIgnoreCase))
                        state.SoPdfFolder = form.PdfFolderPath;
                    using (var t = new Transaction(doc, "Sleeves & Openings: S&O notes"))
                    {
                        t.Start();
                        ProjectStore.Save(doc, state);
                        t.Commit();
                    }

                    // the naming may have changed in the window: the set as it is now
                    cfg = SoProjectSettings.Apply(rules.SoSheets, state.SoSettings);
                    plan = SoSheets.Plan(doc, rules, cfg, levels);
                    var seedProblems = new List<string>();
                    plan = SoTemplates.Seed(doc, rules, cfg, levels, plan, seedProblems);
                    var result = SoSheets.Apply(doc, cfg, plan, state.SoNotes, form.Fields);
                    result.Problems.InsertRange(0, seedProblems);
                    if (form.PrintPdf && result.SheetIds.Count > 0)
                        try { result.Pdf = SoSheets.ExportPdf(doc, cfg, Ordered(doc, result), form.PdfFolderPath, form.PdfFileName); }
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
        /// <param name="onlyLevels">Auto Run's chosen floors (Revit level names): only their sheets are made or completed and
        /// named in the summary; the PDF is still the whole set (the other floors' sheets as they are). Null = all.</param>
        public static string AfterAutoRun(Document doc, RuleSet rules, LevelMap levels, ProjectState state, ICollection<string> onlyLevels = null)
        {
            var cfg = SoProjectSettings.Apply(rules.SoSheets, state.SoSettings);
            if (!cfg.Enabled || !cfg.AfterAutoRun) return null;
            var plan = SoSheets.Plan(doc, rules, cfg, levels);
            var seedProblems = new List<string>();
            plan = SoTemplates.Seed(doc, rules, cfg, levels, plan, seedProblems);
            var others = new List<ElementId>();
            if (onlyLevels != null)
            {
                bool Chosen(string level) => onlyLevels.Any(l => Automation.AutoPlacer.SameFloor(l, level));
                var otherNames = levels.All.Select(l => l.Level.Name).Where(n => !Chosen(n)).ToList();
                bool Other(string problem) => otherNames.Any(n => problem.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                others = plan.Floors.Where(f => !Chosen(f.Level.Level.Name) && f.Sheet != null).Select(f => f.Sheet.Id).ToList();
                plan.Floors = plan.Floors.Where(f => Chosen(f.Level.Level.Name)).ToList();
                plan.Problems.RemoveAll(Other);
                seedProblems.RemoveAll(Other);
            }
            if (plan.Pattern == null) return "S&O sheets not made: " + string.Join("; ", seedProblems.Concat(plan.Problems));
            var result = SoSheets.Apply(doc, cfg, plan, state.SoNotes);
            result.Problems.InsertRange(0, seedProblems);
            if (cfg.PdfAfterAutoRun && result.SheetIds.Count > 0)
            {
                string folder = state.SoPdfFolder ?? DefaultFolder(doc, cfg, state);
                var set = Ordered(doc, result.SheetIds.Concat(others).Distinct());
                try { result.Pdf = SoSheets.ExportPdf(doc, cfg, set, folder, SoSheets.PdfName(cfg, plan.ProjectName)); }
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

        private static System.Collections.Generic.List<ElementId> Ordered(Document doc, SoSetResult result) => Ordered(doc, result.SheetIds);

        private static System.Collections.Generic.List<ElementId> Ordered(Document doc, IEnumerable<ElementId> ids) =>
            ids.Select(id => doc.GetElement(id) as ViewSheet).Where(s => s != null)
                  .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).Select(s => s.Id).ToList();
    }
}
