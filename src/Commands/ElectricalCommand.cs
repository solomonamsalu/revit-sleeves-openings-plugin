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

                // The architect's per-floor xrefs carry both numbers this needs: apartments per floor and the electrical room.
                ElectricalSurvey survey = null;
                List<ElectricalSegment> segments = null;
                string folder = null;

                // the drafter may point at the drawings when they are not next to the model; then it is read again
                while (segments == null)
                {
                    Cursor.Current = Cursors.WaitCursor;
                    try { survey = ElectricalSurvey.Run(doc, rules, state, levels, folder); }
                    finally { Cursor.Current = Cursors.Default; }
                    App.Log("Electrical: read " + survey.Read.Floors.Count + " architectural floor(s), " + survey.Read.Total +
                            " apartments, room " + (survey.Start == null ? "not found" : "on " + survey.StartFloor) +
                            (survey.Read.Folder == null ? "" : ", from " + survey.Read.Folder));

                    DialogResult answer;
                    using (var form = new ElectricalForm(levels, rules.Systems.Electrical, survey))
                    {
                        answer = form.ShowDialog(UI.RevitWindow.Instance);
                        if (answer == DialogResult.OK) segments = form.Result;
                    }
                    if (answer == ElectricalForm.FindDrawings)
                    {
                        folder = AskForFolder(survey.Read.Folder);
                        if (folder == null) return Result.Cancelled;
                        using (var t = new Transaction(doc, "Sleeves & Openings: architect's drawings folder"))
                        {
                            t.Start();
                            state.ElectricalXrefFolder = folder;
                            ProjectStore.Save(doc, state);
                            t.Commit();
                        }
                        continue;
                    }
                    if (answer != DialogResult.OK) return Result.Cancelled;
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

                // rules.json leaves the size parameters unnamed, so find them on the family itself; without this the
                // opening keeps the family type's own size instead of the one the conduits need. Probes in its own
                // transaction, so it has to run before the placing one is open.
                FamilyMapping.GuessParams(doc, boxSym, boxMap);
                if (rndSym != null) FamilyMapping.GuessParams(doc, rndSym, rndMap);

                PlaceCommandBase.EnsureSharedParams(doc, rules, state);

                // Rule 3: as close to the electrical room as a clear path allows. The drawings name that room, so the
                // first segment can start there; a later segment only exists because the riser offsets, so it is clicked.
                bool fromRoom = survey.Start != null && UseRoom(survey, segments.Count);
                for (int i = 0; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    if (i == 0 && fromRoom) { seg.Point = survey.Start; continue; }
                    try
                    {
                        string where = i == 0
                            ? $"click location for floors {seg.FloorsText}"
                            : $"the riser offsets above {segments[i - 1].Levels.Last().Name} — click where floors {seg.FloorsText} run";
                        seg.Point = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                            $"ELECTRIC: {where} — {seg.Circles} circles, {Units.FormatInches(seg.Width)} x {Units.FormatInches(seg.Length)}");
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
                                  (fromRoom ? $"\n\nStarted at the '{survey.Read.Start.RoomText}' label on {survey.StartFloor} — check it clears the structure." : "") +
                                  (survey.Any ? "\n\nApartments read from " + System.IO.Path.GetFileName(survey.Read.Folder ?? "") + ":\n" + survey.Describe() : "") +
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

        /// <summary>The folder holding the architect's per-floor DWGs; null when the drafter cancels.</summary>
        private static string AskForFolder(string start)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Choose the folder holding the architect's per-floor drawings (one DWG per floor, e.g. Xref AR).",
                ShowNewFolderButton = false
            })
            {
                if (!string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start)) dialog.SelectedPath = start;
                return dialog.ShowDialog(UI.RevitWindow.Instance) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        /// <summary>Offers the electrical room the drawings name; false means the drafter clicks the locations instead.</summary>
        private static bool UseRoom(ElectricalSurvey survey, int segments)
        {
            var dialog = new TaskDialog("Electrical riser")
            {
                MainInstruction = $"Start at the '{survey.Read.Start.RoomText}' on {survey.StartFloor}?",
                MainContent = "That is where the label sits in the architect's drawing — mid-room, not against a wall. " +
                              "The riser has to clear beams and walls, so check it after placing, or click the spot yourself." +
                              (segments > 1 ? $"\n\nThe other {segments - 1} segment(s) offset, so you will click those either way." : ""),
                AllowCancellation = false
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Start at the electrical room",
                $"({survey.Start.X:0.##}, {survey.Start.Y:0.##}) in the model");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "I will click the locations");
            return dialog.Show() == TaskDialogResult.CommandLink1;
        }
    }
}
