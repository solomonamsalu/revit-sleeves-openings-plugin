using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Audit;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// The manual as a guided sequence: computes what is done in this model and launches the next step's
    /// ribbon button. Steps mirror the manual's chapters in order.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class WorkflowCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) { message = "Open a project first."; return Result.Failed; }

            try
            {
                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, rules, state);
                var steps = Build(doc, rules, state, levels);

                using (var form = new WorkflowForm(steps, WorkGate.FileName(doc)))
                {
                    if (form.ShowDialog() != DialogResult.OK || form.LaunchButtonId == null) return Result.Succeeded;

                    // Ribbon buttons of an add-in are postable as CustomCtrl_%CustomCtrl_%<tab>%<panel>%<button>.
                    var id = RevitCommandId.LookupCommandId(form.LaunchButtonId);
                    if (id != null && uiapp.CanPostCommand(id)) { uiapp.PostCommand(id); return Result.Succeeded; }

                    TaskDialog.Show("Workflow", "Use the ribbon button for this step: " + form.LaunchButtonId.Split('%').Last());
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Workflow failed: " + ex);
                return Result.Failed;
            }
        }

        private static string Btn(string panel, string button) => $"CustomCtrl_%CustomCtrl_%{App.TabName}%{panel}%{button}";

        private static List<WorkflowStep> Build(Document doc, Rules.RuleSet rules, ProjectState state, LevelMap levels)
        {
            var openings = RiserIndex.AllOpenings(doc);
            var risers = RiserIndex.Group(openings);
            var top = levels.HighestApartment?.Level;
            var roof = levels.MainRoof?.Level;
            bool familiesOk = FamilyRole.All.Take(3).All(r => FamilyMapping.FindSymbol(doc, FamilyMapping.Get(rules, state, r)) != null);
            int On(Level l, params string[] systems) => l == null ? 0 : openings.Count(o => o.Level.Id == l.Id && systems.Contains(o.Data.System));
            int Any(params string[] systems) => openings.Count(o => systems.Contains(o.Data.System));

            var steps = new List<WorkflowStep>();

            steps.Add(new WorkflowStep
            {
                Title = "1. Project Setup — levels, view range, families",
                Detail = state.LastSetup == null ? "Not run yet. Confirms which level is the roof and the highest apartment floor (rules 1-2, 8)."
                       : $"Done {state.LastSetup:yyyy-MM-dd}. Highest apartment floor: {top?.Name ?? "?"}, roof: {roof?.Name ?? "?"}.",
                State = state.LastSetup == null ? StepState.Todo : StepState.Done,
                ButtonId = Btn("Setup", "ProjectSetup"), ButtonText = "Project Setup"
            });
            steps.Add(new WorkflowStep
            {
                Title = "2. Map Families — which opening families to use",
                Detail = familiesOk ? "Regular Opening, Pipe Reference Opening and Round Sleeve are mapped." : "Map the office families (or create test families) before placing.",
                State = familiesOk ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Setup", familiesOk ? "MapFamilies" : "TestFamilies"), ButtonText = familiesOk ? "Map Families" : "Test Families"
            });
            steps.Add(new WorkflowStep
            {
                Title = "3. General rules confirmed for today (Owner's plan, latest file)",
                Detail = WorkGate.IsConfirmed(rules, state, doc) ? $"Confirmed by {state.Confirmation.User} on {state.Confirmation.Date:yyyy-MM-dd}." : "Asked when Project Setup starts and before any work today.",
                State = WorkGate.IsConfirmed(rules, state, doc) ? StepState.Done : StepState.Todo
            });

            int mechTop = On(top, "Exhaust", "ERV", "GarbageChute", "DryerExhaust", "MotorizedDamper");
            steps.Add(new WorkflowStep
            {
                Title = $"4. Mechanical openings on the highest apartment floor ({top?.Name ?? "?"})",
                Detail = mechTop > 0 ? $"{mechTop} exhaust / chute / dryer / damper opening(s) placed. Every riser on the mechanical plan should be there (rule 13)."
                                     : "Open that floor plan and place one opening per exhaust riser, the garbage chute, dryer exhausts and dampers (rules 8-34).",
                State = mechTop > 0 ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Place – Mechanical", "Exhaust"), ButtonText = "Exhaust"
            });

            int propagated = risers.Count(r => r.Openings.Count > 1);
            int single = risers.Count(r => r.Openings.Count == 1 && !IsRoof(r.Openings[0].Level, levels));
            steps.Add(new WorkflowStep
            {
                Title = "5. Copy risers down floor by floor (Lower Floors)",
                Detail = propagated == 0 && single == 0 ? "Nothing to copy yet."
                       : single == 0 ? $"{propagated} riser(s) run through several floors."
                       : $"{propagated} riser(s) propagated, {single} still on one floor only. Select them on their plan and Propagate (rules 45-51).",
                State = propagated == 0 && single == 0 ? StepState.Todo : single == 0 ? StepState.Done : StepState.Partial,
                ButtonId = Btn("Risers", "Propagate"), ButtonText = "Propagate"
            });

            int onRoof = On(roof, "Exhaust", "ERV", "GarbageChute", "DryerExhaust", "MotorizedDamper", "Refrigeration", "Electrical");
            steps.Add(new WorkflowStep
            {
                Title = $"6. Roof openings ({roof?.Name ?? "no roof level"})",
                Detail = roof == null ? "Set a level's role to Roof in Project Setup."
                       : onRoof > 0 ? $"{onRoof} opening(s) on the roof with roof sizes. Check fire paths and bulkheads by eye (rules 60-64)."
                       : "Generate the roof from the top floor: +4\" exhaust, 6\"x6\" dryer, +3\" refrigeration, spacing (rules 52-67).",
                State = roof == null ? StepState.Todo : onRoof > 0 ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Plan", "Roof"), ButtonText = "Generate Roof"
            });

            bool ptac = state.AcSystem == "PTAC";
            int refr = Any("Refrigeration");
            steps.Add(new WorkflowStep
            {
                Title = "7. Refrigeration lines",
                Detail = ptac ? "PTAC system: no refrigeration coordination required (rule 2)."
                       : refr > 0 ? $"{refr} Pipe Reference Opening(s) placed ({state.AcSystem ?? "system not recorded"})."
                       : "Check the AC system (PTAC / split / VRF), group apartments by stack, place risers up to the condenser roof.",
                State = ptac ? StepState.NotNeeded : refr > 0 ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Plan", "RefrigPlan"), ButtonText = "Refrigeration"
            });

            int elec = Any("Electrical");
            steps.Add(new WorkflowStep
            {
                Title = "8. Electrical riser",
                Detail = elec > 0 ? $"{elec} ELECTRIC opening(s) placed." : "One conduit per apartment + roof, 0.75\" c-c, from the electrical room up, 2\" sleeve at the roof.",
                State = elec > 0 ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Plan", "Electrical"), ButtonText = "Electrical"
            });

            int storm = Any("Storm", "AreaDrain");
            steps.Add(new WorkflowStep
            {
                Title = "9. Storm sleeves — from the highest roof down",
                Detail = storm > 0 ? $"{storm} storm / area drain sleeve(s) placed. Every AD is a pair at 2'-0\"." : "Place area drains (always two) and storm risers, sleeve = pipe + 2\", down to the underground storm system.",
                State = storm > 0 ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Place – Plumbing / FP", "AreaDrain"), ButtonText = "Area Drain"
            });

            int cond = Any("Condensate");
            steps.Add(new WorkflowStep
            {
                Title = "10. Condensate sleeves",
                Detail = state.CondensateRequired == false ? "Project Setup says condensate risers are not required."
                       : cond > 0 ? $"{cond} condensate sleeve(s) placed, 3\" each." : "Confirm with the lead whether the building needs condensate risers; if yes, 3\" sleeves near every unit.",
                State = state.CondensateRequired == false ? StepState.NotNeeded : cond > 0 ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Place – Plumbing / FP", "Condensate"), ButtonText = "Condensate"
            });

            int sp = risers.Count(r => r.System == "Standpipe");
            steps.Add(new WorkflowStep
            {
                Title = "11. Sprinkler standpipes — one per stair",
                Detail = sp >= 2 ? $"{sp} standpipe riser(s)." : sp == 1 ? "1 standpipe riser; most buildings have two (one per stairwell)." : "Place the standpipe-only and combined risers in the stairwells, sleeve = pipe + 2\".",
                State = sp >= 2 ? StepState.Done : sp == 1 ? StepState.Partial : StepState.Todo,
                ButtonId = Btn("Place – Plumbing / FP", "Standpipe"), ButtonText = "Standpipe"
            });

            int issues = openings.Count == 0 ? -1 : new Auditor(doc, rules, state, levels).Run().Count(i => i.Severity == Severity.Error);
            steps.Add(new WorkflowStep
            {
                Title = "12. Final Check",
                Detail = issues < 0 ? "Nothing to check yet." : issues == 0 ? "No errors. Warnings and notes may remain — review them." : $"{issues} error(s) against the manual. Open Final Check; many can be fixed with one click.",
                State = issues < 0 ? StepState.Todo : issues == 0 ? StepState.Done : StepState.Partial,
                ButtonId = Btn("Check", "FinalCheck"), ButtonText = "Final Check"
            });

            steps.Add(new WorkflowStep
            {
                Title = "13. Schedule",
                Detail = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Any(v => v.Name.StartsWith("Sleeves & Openings")) ? "Schedule exists." : "Create the sleeves & openings schedule for the drawing set.",
                State = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Any(v => v.Name.StartsWith("Sleeves & Openings")) ? StepState.Done : StepState.Todo,
                ButtonId = Btn("Document", "Schedule"), ButtonText = "Schedule"
            });

            return steps;
        }

        private static bool IsRoof(Level l, LevelMap levels)
        {
            var role = levels.All.FirstOrDefault(x => x.Level.Id == l.Id)?.Role;
            return role == LevelRole.Roof || role == LevelRole.Setback;
        }
    }
}
