using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Placement;
using SleevesOpenings.Risers;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Selects every ELECTRIC opening and sleeve in the model (the add-in's and adopted hand-placed ones) and zooms to
    /// them, listing where each one sits floor by floor: the quick way to see the electrical riser without hunting for ids.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowElectricalCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            var doc = uidoc.Document;
            try
            {
                var electric = RiserIndex.AllOpenings(doc)
                    .Where(o => string.Equals(o.Data.System, SystemKind.Electrical.ToString(), StringComparison.OrdinalIgnoreCase))
                    .OrderBy(o => o.Riser).ThenBy(o => o.Level.Elevation).ToList();
                if (electric.Count == 0)
                {
                    TaskDialog.Show("Electrical", "This model has no ELECTRIC opening or sleeve yet.\n\nRun Electrical Riser to place one, or Adopt Existing so hand-placed ELECTRIC sleeves are found.");
                    return Result.Cancelled;
                }

                var ids = electric.Select(o => o.Instance.Id).ToList();
                uidoc.Selection.SetElementIds(ids);
                var lines = new List<string>();
                foreach (var g in electric.GroupBy(o => o.Riser ?? "(no riser id)"))
                {
                    lines.Add(g.Key + ": " + g.Count() + " on " + string.Join(", ", g.Select(o => o.Level.Name)));
                    foreach (var o in g)
                        lines.Add($"   {o.Level.Name}: ({o.Point.X:0.0}, {o.Point.Y:0.0}) ft   id {o.Instance.Id}");
                }
                var td = new TaskDialog("Electrical openings and sleeves")
                {
                    MainInstruction = electric.Count + " ELECTRIC element(s) selected",
                    MainContent = string.Join("\n", lines) +
                                  "\n\nThey are selected now: Revit zooms to the ones in this view. For another floor open its plan and use Riser Manager > Show / zoom, or Final Check.",
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td.Show();
                // zoom the active view to what it can show; Revit offers to open another view when none is visible here
                var visible = ids.Where(id => doc.GetElement(id) != null && !doc.GetElement(id).IsHidden(doc.ActiveView)).ToList();
                try { uidoc.ShowElements(visible.Count > 0 ? visible : ids); } catch (Exception ex) { App.Log("SHOW ELECTRIC zoom: " + ex.Message); }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                App.Log("SHOW ELECTRIC failed: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
