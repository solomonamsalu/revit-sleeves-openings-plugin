using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SleevesOpenings.Placement;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>F7: refrigeration risers per apartment stack — every floor served plus the condenser roof (+3").</summary>
    [Transaction(TransactionMode.Manual)]
    public class RefrigerationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            var doc = uidoc.Document;

            try
            {
                if (!(doc.ActiveView is ViewPlan plan))
                {
                    TaskDialog.Show("Refrigeration", "Open a floor plan first so you can click a location per stack.");
                    return Result.Cancelled;
                }

                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, rules, state);

                if (!WorkGate.Ensure(doc, rules, state)) return Result.Cancelled;

                List<RefrigerationStack> stacks;
                using (var form = new RefrigerationForm(levels, rules.Systems.Refrigeration))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    state.AcSystem = form.IsPtac ? "PTAC" : form.IsSplit ? "Split" : "VRF";
                    using (var t = new Transaction(doc, "Sleeves & Openings: AC system")) { t.Start(); ProjectStore.Save(doc, state); t.Commit(); }
                    if (form.IsPtac)
                    {
                        TaskDialog.Show("Refrigeration", "PTAC units: no refrigeration line coordination required (rule 2). Nothing placed.");
                        return Result.Succeeded;
                    }
                    stacks = form.Stacks;
                }
                if (stacks.Count == 0) return Result.Cancelled;

                var map = FamilyMapping.Get(rules, state, FamilyRole.PipeReferenceOpening);
                var sym = FamilyMapping.FindSymbol(doc, map);
                if (sym == null)
                {
                    TaskDialog.Show("Refrigeration", "No family mapped for Pipe Reference Opening. Run Map Families or Create Test Families.");
                    return Result.Cancelled;
                }

                PlaceCommandBase.EnsureSharedParams(doc, rules, state);

                foreach (var st in stacks)
                {
                    try
                    {
                        st.Point = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                            $"REFRIGERATION stack {st.Name}: click location ({st.TotalLines} lines, {Units.FormatInches(st.Width)} x {Units.FormatInches(st.Length)}, floors {st.From.Name} – {st.To.Name}, condensers on {st.Condenser.Name})");
                    }
                    catch (OperationCanceledException) { return Result.Cancelled; }
                }

                var r = rules.Systems.Refrigeration;
                int placed = 0;
                var lines = new List<string>();
                using (var t = new Transaction(doc, "Sleeves & Openings: Refrigeration risers"))
                {
                    t.Start();
                    var placer = new Placer(doc, plan);
                    foreach (var st in stacks)
                    {
                        string riser = "REF-" + st.Name;
                        var floors = levels.All.Where(l => l.Elevation >= st.From.Elevation && l.Elevation <= st.To.Elevation).Select(l => l.Level).ToList();
                        foreach (var level in floors)
                        {
                            var spec = OpeningSpec.Rect(SystemKind.Refrigeration, FamilyRole.PipeReferenceOpening, st.Width, st.Length,
                                $"REF {st.TotalLines} LINES - STACK {st.Name}");
                            spec.DownHeight = r.DownHeight;
                            spec.Riser = riser;
                            placer.Place(spec, sym, map, level, st.Point);
                            placed++;
                        }
                        // Condenser level: rule 23 (+3" W/L for the condenser conduits), unless lines come from below only.
                        if (st.Condenser.Elevation > st.To.Elevation)
                        {
                            var spec = OpeningSpec.Rect(SystemKind.Refrigeration, FamilyRole.PipeReferenceOpening,
                                st.Width + r.RoofExtraWidth, st.Length + r.RoofExtraLength, $"REF {st.TotalLines} LINES - STACK {st.Name}");
                            spec.DownHeight = r.DownHeight;
                            spec.Riser = riser;
                            placer.Place(spec, sym, map, st.Condenser, st.Point);
                            placed++;
                        }
                        lines.Add($"Stack {st.Name}: {floors.Count} floor(s) + {st.Condenser.Name}, {st.TotalLines} lines, {Units.FormatInches(st.Width)} x {Units.FormatInches(st.Length)}");
                    }
                    t.Commit();
                }

                App.Log($"Refrigeration: {placed} openings for {stacks.Count} stacks");
                new TaskDialog("Refrigeration risers")
                {
                    MainInstruction = $"{placed} Pipe Reference Opening(s) placed for {stacks.Count} stack(s)",
                    MainContent = string.Join("\n", lines) + "\n\nOn the roof, align openings with the condenser layout and group per condenser cluster (rules 20-22) — drag them as needed, then run Final Check."
                }.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Refrigeration failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
