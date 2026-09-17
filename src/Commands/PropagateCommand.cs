using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>F4: copy selected openings floor by floor to each riser's termination level.</summary>
    [Transaction(TransactionMode.Manual)]
    public class PropagateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            var doc = uidoc.Document;

            try
            {
                if (!(doc.ActiveView is ViewPlan plan) || plan.GenLevel == null)
                {
                    TaskDialog.Show("Propagate", "Open the floor plan that holds the openings you want to copy (normally the highest apartment floor).");
                    return Result.Cancelled;
                }

                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, rules, state);

                if (!WorkGate.Ensure(doc, rules, state)) return Result.Cancelled;

                var sources = SelectedOpenings(uidoc, plan.GenLevel);
                if (sources == null) return Result.Cancelled;
                if (sources.Count == 0)
                {
                    TaskDialog.Show("Propagate", "No add-in openings selected on this level. Select openings placed with the Place buttons.");
                    return Result.Cancelled;
                }

                PlaceCommandBase.EnsureSharedParams(doc, rules, state);

                // Every propagated opening needs a riser id so lower floors can be tracked and audited.
                using (var t = new Transaction(doc, "Sleeves & Openings: assign riser ids"))
                {
                    t.Start();
                    foreach (var s in sources.Where(s => string.IsNullOrEmpty(s.Riser)))
                    {
                        s.Data.Riser = RiserIndex.NextRiserId(doc, s.Data.System);
                        s.Data.WriteTo(s.Instance);
                    }
                    t.Commit();
                }

                using (var form = new PropagateForm(sources, levels, plan.GenLevel))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    var items = form.Items();

                    PropagateReport report;
                    using (var t = new Transaction(doc, "Sleeves & Openings: Propagate risers"))
                    {
                        t.Start();
                        report = new Propagator(doc, levels).Run(plan, items, form.SkipExisting);

                        // The chosen stop level is where the drafter says the riser ends (tap-out, bulkhead...):
                        // remember it so Final Check does not flag the riser for stopping there.
                        foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.Source.Riser)))
                        {
                            if (!state.RiserEnds.TryGetValue(item.Source.Riser, out var ends))
                                state.RiserEnds[item.Source.Riser] = ends = new RiserEnds();
                            if (item.StopLevel.Elevation < item.Source.Level.Elevation) ends.Bottom = item.StopLevel.Name;
                            else if (item.StopLevel.Elevation > item.Source.Level.Elevation) ends.Top = item.StopLevel.Name;
                        }
                        ProjectStore.Save(doc, state);
                        t.Commit();
                    }

                    App.Log($"Propagate: {report.Created} created, {report.Skipped} skipped from {plan.GenLevel.Name}");
                    new TaskDialog("Propagate")
                    {
                        MainInstruction = $"{report.Created} opening(s) created, {report.Skipped} skipped",
                        MainContent = report.ToString()
                    }.Show();
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Propagate failed: " + ex);
                return Result.Failed;
            }
        }

        /// <summary>Current selection if it holds stamped openings on this level; otherwise prompts to pick.</summary>
        private static List<OpeningRecord> SelectedOpenings(UIDocument uidoc, Level level)
        {
            var doc = uidoc.Document;
            var everything = RiserIndex.AllOpenings(doc);
            var all = everything.Where(o => o.Level.Id == level.Id).ToList();
            if (all.Count == 0)
            {
                var where = everything.GroupBy(o => o.Level.Name).OrderByDescending(g => g.First().Level.Elevation)
                    .Select(g => $"  {g.Key}: {g.Count()} opening(s)").ToList();
                new TaskDialog("Propagate")
                {
                    MainInstruction = $"No add-in openings on {level.Name}",
                    MainContent = "Propagate copies openings from the plan you are looking at. " +
                                  (where.Count > 0
                                      ? "Openings exist on:\n" + string.Join("\n", where) + "\n\nOpen one of those floor plans and run Propagate there."
                                      : "Place some openings first with the Place buttons, then run Propagate from that plan.")
                }.Show();
                return null;
            }
            var byId = all.ToDictionary(o => o.Instance.Id, o => o);

            var picked = uidoc.Selection.GetElementIds().Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (picked.Count > 0) return picked;

            try
            {
                var refs = uidoc.Selection.PickObjects(ObjectType.Element, new StampedFilter(byId.Keys),
                    "Select the openings to propagate (add-in openings on this level only), then click Finish");
                return refs.Select(r => byId[r.ElementId]).ToList();
            }
            catch (OperationCanceledException) { return null; }
        }

        private class StampedFilter : ISelectionFilter
        {
            private readonly HashSet<ElementId> _ids;
            public StampedFilter(IEnumerable<ElementId> ids) { _ids = new HashSet<ElementId>(ids); }
            public bool AllowElement(Element e) => _ids.Contains(e.Id);
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
