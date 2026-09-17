using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SleevesOpenings.Electrical;
using SleevesOpenings.Placement;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>F6: electrical riser — conduit count per segment, openings on every floor, circles, roof sleeve.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ElectricalCommand : IExternalCommand
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
                    TaskDialog.Show("Electrical", "Open a floor plan first (ideally the lowest level, near the electrical room) so you can click locations.");
                    return Result.Cancelled;
                }

                var rules = App.Rules(doc);
                var state = ProjectStore.Load(doc);
                var levels = LevelClassifier.Classify(doc, rules, state);

                if (!WorkGate.Ensure(doc, rules, state)) return Result.Cancelled;

                List<ElectricalSegment> segments;
                using (var form = new ElectricalForm(levels, rules.Systems.Electrical))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    segments = form.Result;
                }
                if (segments == null || segments.Count == 0) return Result.Cancelled;

                // Families: blue box for the floors, round sleeve for the roof.
                var boxMap = FamilyMapping.Get(rules, state, FamilyRole.ElectricalOpening);
                var boxSym = FamilyMapping.FindSymbol(doc, boxMap);
                if (boxSym == null) { boxMap = FamilyMapping.Get(rules, state, FamilyRole.RegularOpening); boxSym = FamilyMapping.FindSymbol(doc, boxMap); }
                var rndMap = FamilyMapping.Get(rules, state, FamilyRole.RoundSleeve);
                var rndSym = FamilyMapping.FindSymbol(doc, rndMap);
                if (boxSym == null)
                {
                    TaskDialog.Show("Electrical", "No family mapped for Electrical Opening / Regular Opening. Run Map Families or Create Test Families.");
                    return Result.Cancelled;
                }

                PlaceCommandBase.EnsureSharedParams(doc, rules, state);

                // One click per segment (rule 3: as close to the electrical room as a clear path allows).
                foreach (var seg in segments)
                {
                    try
                    {
                        seg.Point = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                            $"ELECTRIC: click location for floors {seg.FloorsText} — {seg.Circles} circles, {Units.FormatInches(seg.Width)} x {Units.FormatInches(seg.Length)}");
                    }
                    catch (OperationCanceledException) { return Result.Cancelled; }
                }

                string riser = Risers.RiserIndex.NextRiserId(doc, "Electrical");
                var e = rules.Systems.Electrical;
                int openings = 0, circles = 0;

                using (var t = new Transaction(doc, "Sleeves & Openings: Electrical riser"))
                {
                    t.Start();
                    var placer = new Placer(doc, plan);
                    foreach (var seg in segments)
                    {
                        foreach (var level in seg.Levels)
                        {
                            var spec = OpeningSpec.Rect(SystemKind.Electrical, FamilyRole.ElectricalOpening, seg.Width, seg.Length,
                                $"{rules.Naming.Electrical} - {seg.Circles} CONDUITS");
                            spec.Riser = riser;
                            placer.Place(spec, boxSym, boxMap, level, seg.Point);
                            openings++;
                            circles += ElectricalPlanner.DrawCircles(doc, level, seg, e);
                        }
                    }

                    // Rule 16-17: at the roof only one conduit remains — one 2" ELECTRIC sleeve, indoors if possible.
                    var roof = levels.MainRoof?.Level;
                    if (roof != null && rndSym != null)
                    {
                        var spec = OpeningSpec.Round(SystemKind.Electrical, e.RoofSleeveDiameter, rules.Naming.Electrical);
                        spec.Riser = riser;
                        placer.Place(spec, rndSym, rndMap, roof, segments.Last().Point);
                        openings++;
                    }
                    t.Commit();
                }

                App.Log($"Electrical: {openings} openings, {circles} circles, riser {riser}");
                new TaskDialog("Electrical riser")
                {
                    MainInstruction = $"{openings} ELECTRIC opening(s) placed, {circles} conduit circles drawn",
                    MainContent = string.Join("\n", segments.Select(s => $"{s.FloorsText}: {s.Circles} circles, {Units.FormatInches(s.Width)} x {Units.FormatInches(s.Length)}")) +
                                  (levels.MainRoof != null ? $"\n{levels.MainRoof.Name}: one {Units.FormatInches(e.RoofSleeveDiameter)} ELECTRIC sleeve" : "") +
                                  "\n\nCircles are detail lines in each level's floor plan (rule 8). Riser id: " + riser
                }.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log("Electrical failed: " + ex);
                return Result.Failed;
            }
        }
    }
}
