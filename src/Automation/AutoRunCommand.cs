using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Audit;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Compare;
using SleevesOpenings.Automation.Report;
using SleevesOpenings.Automation.UI;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Auto Run (AUTOMATION_PLAN.md): places sleeves/openings from the engineer's PDF + DWG. Steps: what is already in
    /// the model, pick and check the drawings (floors to levels, tag meanings, risers, Revit position, slab openings,
    /// riser diagram, the office's S&amp;O set), place (one undo), per-floor Sleeves views, Final Check on what was placed,
    /// then the report (report.html / report.csv next to risers.json) and the review list that zooms to each item.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoRunCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }
            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                if (!WorkGate.Ensure(doc, rules, state)) return Result.Cancelled;
                state = ProjectStore.Load(doc);                 // the gate may have saved a confirmation
                var levels = LevelClassifier.Classify(doc, rules, state);

                var existing = ExistingCheck.Run(doc, rules, state);
                App.Log($"AutoRun: existing — {existing.Items.Count} item(s), {existing.UnknownFamilies.Values.Sum()} unrecognised");

                // HV model: mechanical drawings, duct openings. PL model: plumbing drawings, pipe sleeves (rules.json "plumbing"),
                // or sprinkler drawings, standpipe / FDC sleeves (rules.json "sprinkler"; the office puts them in the PL model).
                // What the model is comes from the user's saved answer, its name, then what it holds (ModelDiscipline); the
                // drawings picked say which discipline this run is (their sheet numbers, P- / SP- / M-). A model nothing
                // recognises can run any of them: the drawings decide, or the user is asked once.
                var model = ModelDiscipline.Detect(doc, rules, state, existing);
                var allowed = ModelDiscipline.Disciplines(model.Kind, rules);
                var pipeOptions = PipeRules(rules);
                string discipline = allowed.Contains(state.Automation.LastDiscipline ?? "") ? state.Automation.LastDiscipline : allowed.First();
                var pipeRules = pipeOptions.TryGetValue(discipline, out var pr) ? pr : null;
                var profile = pipeRules?.Profile ?? rules.DwgProfile;
                App.Log($"AutoRun: model '{doc.Title}' is {model.Describe()}; can run {string.Join(", ", allowed)}; opens as {discipline}");

                var folders = allowed.Select(d => pipeOptions.TryGetValue(d, out var o) ? o.Profile : rules.DwgProfile)
                    .Distinct().Select(p => ReferenceFiles.FindFolder(doc.PathName, p.ReferenceFolders))
                    .Concat(allowed.Select(d => state.Automation.For(d).Xrefs)).ToList();
                var modelRefs = CadReferences.Collect(doc, folders);
                App.Log($"AutoRun: {modelRefs.Count} DWG(s) imported/linked in the model: " +
                        string.Join(", ", modelRefs.Select(r => $"{r.Name} [{r.Method}, level {r.Level ?? "-"}, {(r.Path != null ? "file found" : r.Problem)}]")));

                var revitGrids = RevitGrids.Collect(doc);
                App.Log($"AutoRun: {revitGrids.Revit.Count} straight grid(s) from {revitGrids.RevitSource}: {string.Join(", ", revitGrids.Revit.Select(g => g.Name))}");

                // PDF only (no DWG; plumbing, sprinkler or mechanical): the plans are lined up by their columns against the model's columns
                RevitColumns columns = null;
                if (rules.PdfOnly.Enabled)
                {
                    columns = RevitColumnCollector.Collect(doc, rules, levels);
                    App.Log($"AutoRun: {columns.Columns.Count} column(s) in the model for PDF-only alignment ({columns.Source})");
                }

                // which floor each level is, from what the model holds on it (rooms, linked levels, xrefs, views)
                var levelHints = LevelEvidence.Collect(doc, levels, modelRefs);
                App.Log($"AutoRun: level evidence — " + (levelHints.Hints.Count == 0 ? "none" :
                        string.Join("; ", levelHints.Hints.Select(kv => $"{kv.Key} = {FloorKey.Describe(kv.Value.Floor)} ({string.Join(", ", kv.Value.Why)})"))));
                foreach (var n in levelHints.Notes) App.Log("AutoRun: level evidence: " + n);

                PdfSheetIndex pdf; DwgSheetIndex dwg; int risersFound; PdfPlanResult pdfPlans; AlignmentResult alignment; RiserAssembly assembly; bool mark, place;
                RiserDiagramResult diagram; SoCompareResult soCompare; SoSetResult soSet;
                using (var form = new AutoRunForm(existing, state.Automation, levels, rules.Legend, profile, modelRefs, revitGrids, doc.PathName,
                                                  rules.Systems.DryerExhaust.MinSpacing, rules.Automation, c => AutoPlacer.OpeningSize(rules, c),
                                                  a => OpeningLayout.Apply(a, c => AutoPlacer.OpeningSize(rules, c), rules.Clearances.ErvFloorCenterToCenter,
                                                                           rules.Clearances.ErvSpacingExact, rules.Systems.DryerExhaust.Diameter,
                                                                           rules.Systems.DryerExhaust.ShaftOpening,
                                                                           firstNumberEastWest: rules.Systems.Exhaust.FirstNumberEastWest,
                                                                           decisions: rules.Automation.Decisions,
                                                                           roofDryerWidth: rules.Systems.DryerExhaust.RoofOpeningWidth,
                                                                           roofDryerLength: rules.Systems.DryerExhaust.RoofOpeningLength,
                                                                           dryerGap: rules.Systems.DryerExhaust.MinSpacing),
                                                  discipline, pipeRules, columns, rules.PdfOnly,
                                                  pipeOptions.ContainsKey(AutomationInputs.Plumbing) ? a => FixtureSleeveLocator.Apply(doc, a, rules.Plumbing, existing) : null,
                                                  pipeOptions, model, allowed, rules.DwgProfile, levelHints))
                {
                    if (form.ShowDialog(SleevesOpenings.UI.RevitWindow.Instance) != DialogResult.OK) return Result.Cancelled;
                    pdf = form.Pdf; dwg = form.Dwg; risersFound = form.Risers?.Risers.Count ?? 0;
                    alignment = form.Alignment; assembly = form.Assembly; mark = form.MarkAnchors; place = form.PlaceNow;
                    diagram = form.Diagram; soCompare = form.SoCompare; soSet = form.SoSet; pdfPlans = form.PdfPlans;
                    discipline = form.Discipline;
                    if (form.ChosenModelKind != null)
                    {
                        state.Automation.ModelKind = form.ChosenModelKind;
                        model = new ModelKindResult { Kind = form.ChosenModelKind, Source = "your choice", Reason = "chosen in Auto Run, saved in this model" };
                    }
                }
                state.Automation.LastDiscipline = discipline;
                App.Log($"AutoRun: discipline {discipline} (from the drawings picked)" +
                        (state.Automation.ModelKind != null ? $"; model kind {state.Automation.ModelKind} (your choice, saved)" : ""));

                using (var t = new Transaction(doc, "Sleeves & Openings: Auto Run inputs"))
                {
                    t.Start();
                    ProjectStore.Save(doc, state);
                    t.Commit();
                }

                var files = state.Automation.For(discipline);
                if (pdfPlans != null)
                    foreach (var p in pdfPlans.Plans)
                        App.Log($"AutoRun: PDF only p{p.Page} {p.Floor}: scale {p.ScaleText ?? PdfPlanReader.Ratio(p.Scale)}, {p.Layers} layer(s), {p.Columns.Count} column(s){(p.Problem != null ? ": " + p.Problem : "")}");
                App.Log($"AutoRun: inputs saved — PDF '{files.Pdf}', DWG '{files.Dwg}', xrefs '{files.Xrefs}', S&O set '{files.Reference}', existing = {state.Automation.Existing}, " +
                        $"PDF floors {pdf?.FloorPlans.Count() ?? 0}, DWG floors {dwg?.Floors.Count ?? 0}");
                if (alignment != null)
                {
                    foreach (var f in alignment.Floors)
                        App.Log($"AutoRun: align {f.Floor} -> {f.Level ?? "-"}: {f.Status}, via {f.Reference?.Name ?? "-"} ({f.Reference?.Method}), " +
                                $"shift {f.Shift?.Dx:0.##},{f.Shift?.Dy:0.##} ({f.Shift?.Support} blocks), map {f.Map}; {string.Join("; ", f.Notes)}");
                    foreach (var g in alignment.Grids) App.Log($"AutoRun: grid check with map {g.Map}: {g.Summary}");
                    App.Log($"AutoRun: Revit position {(alignment.RevitProven ? "proven" : "NOT proven")}: {alignment.RevitSummary}");
                    foreach (var a in alignment.Anchors)
                        App.Log($"AutoRun: check riser {a.Tag} {a.Floor} at ({a.X:0.####}, {a.Y:0.####}) ft, {a.Distance:0.##}\" from {a.Evidence}");
                }

                string listPath = null;
                if (assembly != null)
                {
                    foreach (var c in assembly.Crossings)
                        App.Log($"AutoRun: opening {c.Floor} {c.Tag ?? c.System} {c.Size} x{c.Ducts} at ({c.X:0.###}, {c.Y:0.###}) ft: {c.Status}, {c.Confidence}, PDF {c.Pdf}; " +
                                $"{string.Join(" + ", c.From)}; {string.Join("; ", c.Notes)}");
                    foreach (var x in assembly.Issues) App.Log($"AutoRun: reported {x.Floor} {x.Tag ?? "-"} {x.Type}: {x.Detail}");
                    foreach (var r in diagram?.Risers ?? new List<DiagramRiser>()) App.Log($"AutoRun: riser diagram p{r.Page} {r.Tag}: {r.Describe()}");
                    if (soCompare != null)
                    {
                        App.Log($"AutoRun: S&O set {soSet?.Path}: {soCompare.Summary()}");
                        foreach (var s in soSet?.Sheets ?? new List<SoSheet>())
                            App.Log($"AutoRun: S&O sheet p{s.Page} {s.Floor}: {s.Openings} opening(s), scale {s.Scale:0.###}, {s.Grids} grid(s), worst {s.Residual:0.##}\" {(s.Ok ? "ok" : s.Problem)}");
                        foreach (var m in soCompare.Matches)
                            App.Log($"AutoRun: S&O {m.Floor} {m.Status}: ours {m.Ours?.Name ?? "-"} {m.OurSize}, set {m.Theirs?.Name ?? "-"} {m.Theirs?.SizeText}; {m.Detail}");
                    }
                    try
                    {
                        listPath = RiserListFile.Write(RiserListFile.RunFolder(doc.Title, DateTime.Now), doc.Title, files, alignment, assembly, pdf?.Legend, rules.Legend,
                                                       soCompare, diagram);
                        App.Log("AutoRun: riser list saved to " + listPath);
                    }
                    catch (Exception ex) { App.Log("AutoRun: riser list not saved: " + ex); }
                }

                List<PlacementOutcome> outcomes = null;
                if (place)
                {
                    var toPlace = assembly.ToPlace.ToList();
                    var placer = new AutoPlacer(doc, rules, state, existing, state.Automation.Existing);
                    var loadedFamilies = placer.LoadMissingFamilies(toPlace);         // from the add-in's Families folder
                    if (loadedFamilies.Count > 0) App.Log("AutoRun: loaded families " + string.Join(", ", loadedFamilies));
                    var missing = placer.MissingFamilies(toPlace);
                    if (missing.Count > 0)
                    {
                        var td = new TaskDialog("Auto Run")
                        {
                            MainInstruction = "Families needed to place the openings could not be loaded",
                            MainContent = string.Join("\n", missing.Select(Placement.FamilyRole.Describe)) + "\n\nMap them now? (No = those openings are skipped and reported.)",
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                        };
                        if (td.Show() == TaskDialogResult.Yes) Commands.MapFamiliesCommand.ShowAndSave(doc, rules, state);
                        state = ProjectStore.Load(doc);
                        placer = new AutoPlacer(doc, rules, state, existing, state.Automation.Existing);
                    }
                    Commands.PlaceCommandBase.EnsureSharedParams(doc, rules, state);
                    outcomes = placer.Run(toPlace);
                    foreach (var o in outcomes)
                        App.Log($"AutoRun: {o.Result} {o.Crossing.Floor} {o.Crossing.Name} {o.Size} on {o.Crossing.Level}" +
                                $"{(o.Ids.Count > 0 ? " [" + string.Join(",", o.Ids.Select(i => i.ToString())) + "]" : "")}" +
                                $"{(o.Detail != null ? ": " + o.Detail : "")}{(o.Warnings.Count > 0 ? " | warnings: " + string.Join("; ", o.Warnings) : "")}");
                    try { SaveDrawnOffsets(doc, outcomes); }
                    catch (Exception ex) { App.Log("AutoRun: drawn offsets not saved: " + ex); }
                }

                SleeveViewResult views = null;
                if (place)
                {
                    try { views = SleeveViews.Ensure(doc, rules, levels, rules.SleeveViews); App.Log("AutoRun: " + views); }
                    catch (Exception ex) { App.Log("AutoRun: sleeve views failed: " + ex); }
                    // the name drawn inside the opening families: no white box behind it. Only the families this run
                    // placed: reloading an unrelated family (e.g. Ref pipes opening at Down Height 0) can fail to regenerate
                    if (rules.SleeveViews?.TransparentLabels != false)
                        try
                        {
                            var used = (outcomes ?? new List<PlacementOutcome>())
                                .Where(o => o.Result == PlacementOutcome.Placed || o.Result == PlacementOutcome.Resized)
                                .SelectMany(o => o.Ids).Select(id => (doc.GetElement(id) as FamilyInstance)?.Symbol?.Family?.Name)
                                .Where(n => n != null).Distinct().ToList();
                            var fams = used.Count > 0 ? FamilyText.MakeTransparent(doc, used) : new List<string>();
                            if (fams.Count > 0) App.Log("AutoRun: text made transparent in " + string.Join(", ", fams));
                        }
                        catch (Exception ex) { App.Log("AutoRun: transparent family text failed: " + ex); }
                }

                // Final Check on what this run placed; fixes with one right answer are applied to those openings only
                FinalCheckRun final = null;
                if (outcomes != null && rules.Automation.FinalCheck)
                {
                    var placedIds = new HashSet<long>(outcomes.Where(o => o.Result == PlacementOutcome.Placed || o.Result == PlacementOutcome.Resized)
                                                              .SelectMany(o => o.Ids).Select(i => i.Value));
                    if (placedIds.Count > 0)
                        try
                        {
                            // sleeves already in the model that this run's risers use are checked as part of those risers
                            var serving = new Dictionary<long, string>();
                            foreach (var o in outcomes.Where(o => o.Result == PlacementOutcome.Existing && o.Crossing?.Tag != null))
                                foreach (var id in o.Ids) serving[id.Value] = o.Crossing.Tag;
                            final = FinalCheck(doc, rules, levels, placedIds, serving);
                            App.Log("AutoRun: " + final);
                            foreach (var (i, fix) in final.Fixed) App.Log($"AutoRun: final check fixed {i.Level} {i.Riser}: {i.Message} -> {fix}");
                            foreach (var i in final.Remaining) App.Log($"AutoRun: final check {i.Severity} {i.Rule} {i.Level} {i.Riser}: {i.Message}");
                        }
                        catch (Exception ex) { App.Log("AutoRun: final check failed: " + ex); }
                }

                // the S&O set (SL101…): completed from the pattern sheet and printed, in the PL / FP model where the office keeps it
                string soSheets = null;
                if (place && discipline != AutomationInputs.Mechanical)
                    try { soSheets = Sheets.SoSetCommand.AfterAutoRun(doc, rules, levels, ProjectStore.Load(doc)); }
                    catch (Exception ex) { soSheets = "S&O sheets failed: " + ex.Message; App.Log("AutoRun: S&O sheets failed: " + ex); }

                var marks = new List<ElementId>();
                if (mark && alignment != null)
                {
                    using (var t = new Transaction(doc, "Sleeves & Openings: Auto Run check marks"))
                    {
                        t.Start();
                        marks = AnchorMarks.Draw(doc, alignment.Anchors, levels);
                        t.Commit();
                    }
                    if (marks.Count > 0) { uidoc.Selection.SetElementIds(marks); uidoc.ShowElements(marks); }
                }

                string summary =
                        $"Model: {model.Describe()}\n" +
                        $"PDF: {(pdf == null ? "none" : $"{pdf.FloorPlans.Count()} floor plan(s)")}\n" +
                        (pdfPlans != null
                            ? $"DWG: none (PDF only: {pdfPlans.Plans.Count(p => p.Problem == null)} floor plan(s) read from the PDF's CAD layers, lined up by their columns)\n" +
                              $"Pipe groups found in the PDF: {risersFound}\n"
                            : $"DWG: {(dwg == null ? "none" : $"{dwg.Floors.Count} floor plan(s)")}\n" +
                              $"Risers found in the DWG: {risersFound}\n") +
                        $"Tags defined in the PDF: {pdf?.Legend.Entries.Select(e => e.Tag).Distinct().Count() ?? 0}\n" +
                        $"Existing sleeves/openings: {existing.Items.Count} ({(state.Automation.Existing == ExistingPolicy.Update ? "update" : "keep and add missing")})\n\n" +
                        AlignmentText(alignment, marks.Count > 0) +
                        OpeningsText(assembly, listPath) +
                        PlacedText(outcomes, place, alignment, assembly) + (views != null ? "\n\n" + views : "") +
                        (final != null ? "\n\n" + final : "") +
                        (soSheets != null ? "\n\n" + soSheets : "") +
                        (soCompare != null ? $"\n\nS&O set ({System.IO.Path.GetFileName(soSet?.Path)}): {soCompare.Summary()}" : "") +
                        (diagram != null ? $"\nRiser diagram (page {diagram.Page}): {diagram.Risers.Count} tagged run(s) checked against the plans" : "");

                // the report next to risers.json, and the review list
                string html = null, folder = listPath != null ? System.IO.Path.GetDirectoryName(listPath) : RiserListFile.RunFolder(doc.Title, DateTime.Now);
                RunReport report = null;
                try
                {
                    report = ReportBuilder.Build(doc.Title, files, alignment, assembly, outcomes, soCompare, final, rules,
                                                 summary.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("List saved")), discipline);
                    report.Summary = summary;
                    (html, _) = report.Write(folder);
                    App.Log("AutoRun: report saved to " + html);
                }
                catch (Exception ex) { App.Log("AutoRun: report not saved: " + ex); }

                if (report != null)
                    using (var review = new ReviewForm(uidoc, report, levels, summary + (html != null ? $"\n\nReport: {html}" : ""), rules.SleeveViews?.NameSuffix, html, folder))
                        review.ShowDialog(SleevesOpenings.UI.RevitWindow.Instance);
                else
                    new TaskDialog("Auto Run") { MainInstruction = "Auto Run finished", MainContent = summary, CommonButtons = TaskDialogCommonButtons.Close }.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("AutoRun failed: " + ex);
                return Result.Failed;
            }
        }

        /// <summary>The pipe disciplines' rules: plumbing, and sprinkler / standpipe unless rules.json turns it off.</summary>
        internal static Dictionary<string, PlumbingRules> PipeRules(RuleSet rules)
        {
            var options = new Dictionary<string, PlumbingRules> { [AutomationInputs.Plumbing] = rules.Plumbing };
            if (rules.Sprinkler != null && rules.Sprinkler.Enabled) options[AutomationInputs.Sprinkler] = rules.Sprinkler;
            return options;
        }

        /// <summary>
        /// The riser offsets the plans draw (<see cref="Crossing.DrawnOffset"/>), saved in the model with where both sleeves
        /// ended up: Final Check notes them instead of warning until someone moves either sleeve.
        /// </summary>
        private static void SaveDrawnOffsets(Document doc, List<PlacementOutcome> outcomes)
        {
            XYZ At(PlacementOutcome o) => o.Ids.Select(id => ((doc.GetElement(id) as FamilyInstance)?.Location as LocationPoint)?.Point).FirstOrDefault(p => p != null);
            // the sleeve's own level (a hand-placed sleeve can be on the floor's other level: "06.6-TH FLOOR" / "06.6 TH FLOOR NEW")
            string LevelOf(PlacementOutcome o) => o.Ids.Select(id => doc.GetElement(id) as FamilyInstance).Where(i => i != null)
                                                      .Select(i => SleevesOpenings.Risers.RiserIndex.LevelOf(doc, i)?.Name).FirstOrDefault(n => n != null) ?? o.Crossing.Level;
            var kept = outcomes.Where(o => o.Crossing?.Tag != null && o.Ids.Count > 0 &&
                                           (o.Result == PlacementOutcome.Placed || o.Result == PlacementOutcome.Resized || o.Result == PlacementOutcome.Existing)).ToList();
            var found = new List<DrawnOffset>();
            foreach (var o in kept.Where(o => o.Crossing.DrawnOffset))
            {
                var below = kept.Where(b => b.Crossing.Tag == o.Crossing.Tag && FloorKey.Order(b.Crossing.Floor) < FloorKey.Order(o.Crossing.Floor))
                                .OrderByDescending(b => FloorKey.Order(b.Crossing.Floor)).FirstOrDefault();
                XYZ here = At(o), under = below != null ? At(below) : null;
                if (here == null || under == null) continue;
                found.Add(new DrawnOffset { Riser = o.Crossing.Tag, Level = LevelOf(o), X = here.X, Y = here.Y, BelowX = under.X, BelowY = under.Y });
            }
            // this run's risers: what an earlier run saved for them is replaced
            var risers = new HashSet<string>(kept.Select(o => o.Crossing.Tag));
            var state = ProjectStore.Load(doc);
            if (found.Count == 0 && !state.DrawnOffsets.Any(d => risers.Contains(d.Riser))) return;
            state.DrawnOffsets.RemoveAll(d => risers.Contains(d.Riser));
            state.DrawnOffsets.AddRange(found);
            using (var t = new Transaction(doc, "Sleeves & Openings: Auto Run - riser offsets drawn on the plans"))
            {
                t.Start();
                ProjectStore.Save(doc, state);
                t.Commit();
            }
            App.Log($"AutoRun: {found.Count} riser offset(s) drawn on the plans saved: " + string.Join(", ", found.Select(f => $"{f.Riser} at {f.Level}")));
        }

        /// <summary>Final Check on the openings one run placed (issues touching them); safe fixes (size, name, riser id) applied once, then checked again.</summary>
        private static FinalCheckRun FinalCheck(Document doc, RuleSet rules, LevelMap levels, HashSet<long> ids, IDictionary<long, string> serving = null)
        {
            var run = new FinalCheckRun { Checked = ids.Count };
            List<AuditIssue> Mine() => new Auditor(doc, rules, ProjectStore.Load(doc), levels, serving).Run()
                                           .Where(i => i.Elements.Any(e => ids.Contains(e.Value))).ToList();
            var issues = Mine();
            var fixable = !rules.Automation.AutoFix ? new List<AuditIssue>()
                : issues.Where(i => i.CanFix && ids.Contains(i.Elements[0].Value) && SafeFix(i.FixLabel)).ToList();
            if (fixable.Count > 0)
            {
                using (var t = new Transaction(doc, "Sleeves & Openings: Auto Run - Final Check fixes"))
                {
                    t.Start();
                    foreach (var i in fixable)
                    {
                        try { i.Fix(doc); run.Fixed.Add((i, i.FixLabel)); }
                        catch (Exception ex) { run.Failed.Add($"{i.Message}: {ex.Message}"); }
                    }
                    t.Commit();
                }
                issues = Mine();
            }
            run.Remaining = issues;
            return run;
        }

        /// <summary>Fixes Auto Run may apply on its own: they change one opening's size, name or riser id; never copy, move or add.</summary>
        private static bool SafeFix(string label) =>
            label != null && (label.StartsWith("Set to") || label.StartsWith("Name '") || label.StartsWith("Assign id"));

        private static string PlacedText(List<PlacementOutcome> o, bool place, AlignmentResult alignment, RiserAssembly assembly)
        {
            if (o == null)
                return assembly == null ? "Nothing was placed." :
                       alignment?.Passed != true ? "Nothing was placed: the Revit position check did not pass." : "Nothing was placed (placing was switched off).";
            int placed = o.Where(x => x.Result == PlacementOutcome.Placed).Sum(x => x.Ids.Count);
            var text = $"PLACED: {placed} opening(s)/sleeve(s) for {o.Count(x => x.Result == PlacementOutcome.Placed)} riser crossing(s) — one undo removes them all.\n" +
                       $"Already in the model: {o.Count(x => x.Result == PlacementOutcome.Existing || x.Result == PlacementOutcome.Resized)}" +
                       $"{(o.Any(x => x.Result == PlacementOutcome.Resized) ? $" ({o.Count(x => x.Result == PlacementOutcome.Resized)} resized)" : "")}\n" +
                       $"Placed with a rule warning: {o.Count(x => x.Result == PlacementOutcome.Placed && x.Warnings.Count > 0)}\n";
            foreach (var x in o.Where(x => x.Result == PlacementOutcome.Skipped || x.Result == PlacementOutcome.Failed))
                text += $"  NOT placed: {FloorKey.Describe(x.Crossing.Floor)} {x.Crossing.Name} — {x.Detail}\n";
            return text + "Review items are in the list below and in the report.";
        }

        private static string OpeningsText(RiserAssembly a, string path)
        {
            if (a == null) return "";
            return $"Openings from the drawings: {a.Crossings.Count(c => c.Status == Crossing.Place)} to place, " +
                   $"{a.Crossings.Count(c => c.Status == Crossing.Review)} to review, {a.Crossings.Count(c => c.Status == Crossing.Skip)} not placed; " +
                   $"{a.Issues.Count} item(s) reported.\n" +
                   (path != null ? $"List saved: {path}\n\n" : "\n");
        }

        private static string AlignmentText(AlignmentResult a, bool marked)
        {
            if (a == null) return "Revit position: not checked (no DWG).\n\n";
            var text = $"Revit position: {a.Floors.Count(f => f.Usable)} of {a.Floors.Count} floor(s) line up with the drawings at Revit's position.\n" +
                       $"3 check risers: {(a.DrawingsMatch ? "match" : "do NOT match")}. Revit position: {(a.RevitProven ? "PROVEN" : "NOT proven")} ({a.RevitSummary}).\n" +
                       $"Result: {(a.Passed ? "PASSED" : "NOT PASSED — nothing will be placed")}.\n";
            foreach (var x in a.Anchors)
                text += $"  {x.Tag ?? "(no tag)"} on the {FloorKey.Describe(x.Floor)}: {x.Distance:0.##}\" from the {x.Evidence}\n";
            if (marked)
                text += "The check risers are marked (circle + cross) and selected: compare them with the drawings in Revit, then undo to remove the marks.\n";
            return text + "\n";
        }
    }
}
