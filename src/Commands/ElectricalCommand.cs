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
using SleevesOpenings.Risers;
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
                var e = rules.Systems.Electrical;

                App.Log("ELEC| ================= Electrical riser started =================");
                App.Log("ELEC| model '" + doc.Title + "' at " + (doc.PathName ?? "(unsaved)") + "; active view '" + plan.Name + "' on level '" + plan.GenLevel?.Name + "'");
                App.Log("ELEC| rules: spacing " + e.ConduitSpacingCenterToCenter + "\", circle " + e.CircleDiameter + "\", +" + e.ExtraCirclesForRoof +
                        " for roof, margin " + e.OpeningMargin + "\", roof sleeve " + e.RoofSleeveDiameter + "\"; search " + e.SearchRadius + "\" in " + e.SearchStep +
                        "\" steps, " + e.WallGapMin + "-" + e.WallGapMax + "\" from a wall; saved xref folder = " + (state.ElectricalXrefFolder ?? "(none)"));
                foreach (var l in levels.Everything)
                    App.Log("ELEC| LEVEL '" + l.Level.Name + "' role " + l.Role + ", elevation " + l.Level.Elevation.ToString("0.###") +
                            " ft, project elevation " + l.Level.ProjectElevation.ToString("0.###") + " ft");

                if (!WorkGate.Ensure(doc, rules, state)) { App.Log("ELEC| stopped at the general-rules gate"); return Result.Cancelled; }

                // The architect's per-floor xrefs carry both numbers this needs: apartments per floor and the electrical room.
                ElectricalSurvey survey = null;
                List<ElectricalFloor> floors = null;
                List<ClassifiedLevel> riserLevels = null;
                List<string> leftOut = null;
                bool auto = false;
                int columns = 0;
                string folder = null;

                // the drafter may point at the drawings when they are not next to the model; then it is read again
                while (floors == null)
                {
                    Cursor.Current = Cursors.WaitCursor;
                    ElectricalReader.Trace = App.Log;
                    try { survey = ElectricalSurvey.Run(doc, rules, state, levels, folder); }
                    finally { Cursor.Current = Cursors.Default; ElectricalReader.Trace = null; }
                    foreach (var w in survey.Read.Warnings) App.Log("ELEC| WARNING " + w);

                    riserLevels = RiserLevels(doc, levels, survey, out leftOut);
                    if (riserLevels.Count == 0)
                    {
                        TaskDialog.Show("Electrical", "No floor above the electrical room has a floor plan, so the riser has nowhere to go.\n\n" + string.Join("\n", leftOut));
                        return Result.Cancelled;
                    }

                    DialogResult answer;
                    using (var form = new ElectricalForm(riserLevels, levels, e, survey, string.Join("; ", leftOut), survey.Start != null))
                    {
                        answer = form.ShowDialog(UI.RevitWindow.Instance);
                        App.Log("ELEC| FORM closed with " + answer + (answer == DialogResult.OK ? ", find the spot: " + form.AutoLocate + ", circles per row: " + form.Columns : ""));
                        foreach (var f in form.Floors.Reverse())
                        {
                            var read = survey.For(f.Level);
                            App.Log("ELEC| FORM level '" + f.Level.Name + "': apartments " + f.Apartments + (f.OffsetAbove ? ", OFFSET ABOVE" : "") +
                                    " (drawings said " + (read == null ? "nothing" : read.Apartments + " from " + read.File) + ")" +
                                    (read != null && read.Apartments != f.Apartments ? " [EDITED by user]" : ""));
                        }
                        if (answer == DialogResult.OK) { floors = form.Floors.ToList(); auto = form.AutoLocate; columns = form.Columns; }
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

                // One riser (the manual describes one): a riser already in the model is replaced, not stacked on.
                // the add-in's own riser only: an adopted one is the office's hand-placed sleeve, never offered for deletion
                var existing = RiserIndex.AllOpenings(doc).Where(o => o.Data.System == SystemKind.Electrical.ToString() && !o.Data.Adopted).ToList();
                bool replace = false;
                if (existing.Count > 0)
                {
                    var ids = existing.Select(o => o.Riser ?? "?").Distinct().ToList();
                    App.Log("ELEC| EXISTING " + existing.Count + " electrical opening(s), riser(s) " + string.Join(", ", ids));
                    var td = new TaskDialog("Electrical riser")
                    {
                        MainInstruction = $"This model already has {existing.Count} ELECTRIC opening(s)",
                        MainContent = "Riser " + string.Join(", ", ids) + " on " + string.Join(", ", existing.Select(o => o.Level.Name).Distinct()) +
                                      ".\n\nThe manual has one electrical riser. Replacing deletes those openings and their conduit circles.",
                        AllowCancellation = true
                    };
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Replace the existing riser");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep it and add another");
                    var pick = td.Show();
                    if (pick == TaskDialogResult.CommandLink1) replace = true;
                    else if (pick != TaskDialogResult.CommandLink2) return Result.Cancelled;
                    App.Log("ELEC| EXISTING user chose " + (replace ? "REPLACE" : "KEEP and add"));
                }
                var replacing = replace ? new HashSet<ElementId>(existing.Select(o => o.Instance.Id)) : new HashSet<ElementId>();

                // Sleeves placed by hand carry none of the add-in's data, so the search cannot keep clear of them until they
                // are adopted. On an office model that is most of the sleeves: offer the adoption here instead of leaving
                // the riser to land on one of them.
                var adopt = new Adopter(doc, rules, state).Scan();
                if (adopt.Candidates.Count > 0)
                {
                    App.Log("ELEC| ADOPT " + adopt.Candidates.Count + " hand-placed sleeve(s)/opening(s) not yet adopted, " + adopt.AlreadyStamped + " already adopted");
                    var td = new TaskDialog("Electrical riser")
                    {
                        MainInstruction = $"{adopt.Candidates.Count} existing sleeve(s) and opening(s) are not adopted",
                        MainContent = "They were placed by hand, so the riser search does not see them and may land on one. Adopting stamps them " +
                                      "with the add-in's data (nothing is moved or resized), the same as Tools > Adopt Existing.",
                        AllowCancellation = true
                    };
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Adopt them now, then find the riser");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Continue without them (the search ignores them)");
                    var pick = td.Show();
                    if (pick == TaskDialogResult.CommandLink1)
                    {
                        // binds the shared parameters in a transaction of its own, so before the adopting one opens
                        try { PlaceCommandBase.EnsureSharedParams(doc, rules, state); } catch (Exception ex) { App.Log("ELEC| ADOPT shared params not bound: " + ex.Message); }
                        using (var t = new Transaction(doc, "Sleeves & Openings: Adopt existing"))
                        {
                            t.Start();
                            int written = new Adopter(doc, rules, state).Apply(adopt.Candidates);
                            t.Commit();
                            App.Log("ELEC| ADOPT stamped " + written);
                        }
                    }
                    else if (pick == TaskDialogResult.CommandLink2) App.Log("ELEC| ADOPT skipped by the user");
                    else return Result.Cancelled;
                }

                // Where it goes: found from the drawings (rules 3, 10-15, 20-21, 26-28), or clicked and then checked the same way.
                Cursor.Current = Cursors.WaitCursor;
                ElectricalLocator locator;
                try { locator = new ElectricalLocator(doc, rules, floors.Select(f => f.Level).ToList(), survey, replacing, m => App.Log("ELEC| " + m)); }
                finally { Cursor.Current = Cursors.Default; }

                // The structural checks (rules 26-28, slab edge) only see what is in this model and its loaded links. With
                // nothing there every spot passes them, which reads as a clean result when nothing was checked at all.
                if (!locator.StructureSeen)
                {
                    App.Log("ELEC| WARNING no columns, beams, slabs or walls on any riser level: the structural model is not loaded, rules 26-28 NOT checked");
                    var td = new TaskDialog("Electrical riser")
                    {
                        MainInstruction = "No structure found in this model or its loaded links",
                        MainContent = "No columns, beams, slabs or walls were found on any riser level, so the spot cannot be checked against columns " +
                                      "(rule 26), shear walls (27), beams (28) or the slab edge. The engineer rejects openings within 1'-0\" of a column.\n\n" +
                                      "Load the structural model as a link (Insert > Link Revit) and run again, or continue and check the spot on the structural plans yourself.",
                        AllowCancellation = true
                    };
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Cancel and load the structural link");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Continue without the structural checks");
                    if (td.Show() != TaskDialogResult.CommandLink2) { App.Log("ELEC| stopped: no structure loaded"); return Result.Cancelled; }
                    App.Log("ELEC| user continued without structure");
                }
                var noWalls = locator.LevelsWithoutWalls;
                if (noWalls.Count > 0)
                    App.Log("ELEC| WARNING no wall lines on " + string.Join(", ", noWalls) + ": the room there is the nearest label, not a check " +
                            "(dwgProfile.architecturalWallLayers = '" + (rules.DwgProfile?.ArchitecturalWallLayers ?? "") + "')");

                List<ElectricalSegment> segments;
                var found = new List<RiserRun>();
                if (auto)
                {
                    Cursor.Current = Cursors.WaitCursor;
                    try
                    {
                        found = locator.Plan(survey.Start, i => { var s = ElectricalPlanner.SizeFrom(floors, i, e, columns); return (s.Width, s.Length); });
                    }
                    finally { Cursor.Current = Cursors.Default; }

                    // the search's runs become the segments: an offset above the last floor of every run but the top one
                    foreach (var f in floors) f.OffsetAbove = false;
                    foreach (var run in found.Take(found.Count - 1)) floors[run.To].OffsetAbove = true;
                    segments = ElectricalPlanner.Segments(floors, e, columns);
                    for (int i = 0; i < segments.Count && i < found.Count; i++) segments[i].Point = found[i].Point;

                    switch (Propose(segments, found, survey, locator))
                    {
                        case TaskDialogResult.CommandLink1: break;
                        case TaskDialogResult.CommandLink2:
                            App.Log("ELEC| PROPOSAL declined: the drafter clicks");
                            auto = false;
                            foreach (var f in floors) f.OffsetAbove = false;
                            segments = ElectricalPlanner.Segments(floors, e, columns);
                            break;
                        default: App.Log("ELEC| PROPOSAL cancelled"); return Result.Cancelled;
                    }
                }
                else segments = ElectricalPlanner.Segments(floors, e, columns);

                // whatever the search did not settle is clicked, and every clicked spot gets the same checks
                int first = 0;
                for (int i = 0; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    int from = first, to = first + seg.Levels.Count - 1;
                    first = to + 1;
                    if (seg.Point != null) continue;
                    while (true)
                    {
                        string where = i == 0 ? $"click the riser's spot for floors {seg.FloorsText}"
                                              : $"the riser offsets above {segments[i - 1].Levels.Last().Name} — click where floors {seg.FloorsText} run";
                        XYZ p;
                        try
                        {
                            p = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                                $"ELECTRIC: {where} — {seg.Circles} circles, {Units.FormatInches(seg.Width)} x {Units.FormatInches(seg.Length)}. Closet/shaft, against a wall.");
                        }
                        catch (OperationCanceledException) { App.Log("ELEC| click cancelled for segment " + seg.FloorsText); return Result.Cancelled; }

                        var notes = locator.Review(from, to, p, seg.Width, seg.Length);
                        App.Log($"ELEC| CLICK segment {seg.FloorsText} at ({p.X:0.###}, {p.Y:0.###}): {(notes.Count == 0 ? "no problems" : string.Join("; ", notes))}");
                        if (notes.Count == 0) { seg.Point = p; break; }
                        var td = new TaskDialog("Electrical riser")
                        {
                            MainInstruction = "This spot breaks the rules on " + notes.Select(n => n.Split(':')[0]).Distinct().Count() + " floor(s)",
                            MainContent = string.Join("\n", notes.Take(12)) + (notes.Count > 12 ? $"\n… and {notes.Count - 12} more" : ""),
                            AllowCancellation = true
                        };
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Click again");
                        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Place it here anyway");
                        var pick = td.Show();
                        if (pick == TaskDialogResult.CommandLink2) { seg.Point = p; App.Log("ELEC| CLICK placed anyway"); break; }
                        if (pick != TaskDialogResult.CommandLink1) return Result.Cancelled;
                    }
                }
                for (int i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    App.Log($"ELEC| SEGMENT {s.FloorsText} ({string.Join(", ", s.Levels.Select(l => l.Name))}): {s.Circles} circles, grid {s.Columns}x{s.Rows}, " +
                            $"opening {s.Width}\" x {s.Length}\" at ({s.Point.X:0.###}, {s.Point.Y:0.###})");
                }

                // Families: blue box for the floors, round sleeve for the roof. Loaded from the add-in's Families folder
                // when the project has not got them yet, as Auto Run does.
                LoadMissingFamilies(doc, rules, state, FamilyRole.ElectricalOpening, FamilyRole.RoundSleeve);
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
                App.Log("ELEC| BOX family '" + boxSym.Family.Name + "' type '" + boxSym.Name + "' placement " + boxSym.Family.FamilyPlacementType +
                        "; width param '" + boxMap.WidthParam + "', length param '" + boxMap.LengthParam + "', name param '" + boxMap.NameParam + "'");
                App.Log("ELEC| ROOF SLEEVE " + (rndSym == null ? "family NOT FOUND (no roof sleeve will be placed)" :
                        "family '" + rndSym.Family.Name + "' type '" + rndSym.Name + "' placement " + rndSym.Family.FamilyPlacementType +
                        "; diameter param '" + rndMap.DiameterParam + "', size toggles " + rndMap.UsesSizeToggles));

                PlaceCommandBase.EnsureSharedParams(doc, rules, state);

                // a replaced riser keeps its id, so its schedules and tags still read the same
                var oldIds = existing.Select(o => o.Riser).Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
                string riser = replace && oldIds.Count == 1 ? oldIds[0] : RiserIndex.NextRiserId(doc, "Electrical");
                int openings = 0, circles = 0, removed = 0;

                // Rule 16-17: at the roof only one conduit remains — one 2" ELECTRIC sleeve, on the roof slab near the riser below
                var roof = levels.MainRoof?.Level;
                var roofNotes = new List<string>();
                XYZ roofAt = segments.Last().Point;
                if (roof != null && rndSym != null)
                {
                    Cursor.Current = Cursors.WaitCursor;
                    try { roofAt = locator.Roof(roof, roofAt, e.RoofSleeveDiameter, roofNotes) ?? roofAt; }
                    finally { Cursor.Current = Cursors.Default; }
                }

                using (var t = new Transaction(doc, "Sleeves & Openings: Electrical riser"))
                {
                    t.Start();
                    if (replace) removed = Remove(doc, existing, e);
                    var placer = new Placer(doc, plan);
                    bool dumped = false;
                    foreach (var seg in segments)
                    {
                        foreach (var level in seg.Levels)
                        {
                            var spec = OpeningSpec.Rect(SystemKind.Electrical, FamilyRole.ElectricalOpening, seg.Width, seg.Length,
                                $"{rules.Naming.Electrical} - {seg.Circles} CONDUITS");
                            spec.Riser = riser;
                            var inst = placer.Place(spec, boxSym, boxMap, level, seg.Point);
                            CentreOn(doc, inst, seg.Point, level, "box");
                            openings++;
                            LogPlaced("BOX", inst, level, boxMap, $"wanted {seg.Width}\" x {seg.Length}\"", !dumped);
                            dumped = true;
                            circles += ElectricalPlanner.DrawCircles(doc, level, seg, e);
                        }
                    }

                    if (roof != null && rndSym != null)
                    {
                        var spec = OpeningSpec.Round(SystemKind.Electrical, e.RoofSleeveDiameter, rules.Naming.Electrical);
                        spec.Riser = riser;
                        var inst = placer.Place(spec, rndSym, rndMap, roof, roofAt);
                        // the same centring the boxes get: without it a family whose insertion point is off its middle
                        // leaves the sleeve beside the riser, and Final Check reads that as an offset (rule 22-23)
                        CentreOn(doc, inst, roofAt, roof, "sleeve");
                        openings++;
                        LogPlaced("ROOF SLEEVE", inst, roof, rndMap, $"wanted {e.RoofSleeveDiameter}\" dia", false);
                        foreach (var n in roofNotes) App.Log("ELEC| ROOF check: " + n);
                    }
                    else App.Log("ELEC| ROOF SLEEVE skipped: " + (roof == null ? "no level has the Roof role" : "no round sleeve family"));
                    t.Commit();
                }
                App.Log("ELEC| ================= Electrical riser finished: " + openings + " opening(s), " + circles + " circle(s), riser " + riser +
                        (replace ? ", replaced " + removed + " element(s)" : "") + " =================");

                new TaskDialog("Electrical riser")
                {
                    MainInstruction = $"{openings} ELECTRIC opening(s) placed, {circles} conduit circles drawn",
                    MainContent = string.Join("\n", segments.Select(s => $"{s.FloorsText}: {s.Circles} circles, {Units.FormatInches(s.Width)} x {Units.FormatInches(s.Length)}")) +
                                  (levels.MainRoof != null ? $"\n{levels.MainRoof.Name}: one {Units.FormatInches(e.RoofSleeveDiameter)} ELECTRIC sleeve" : "") +
                                  (segments.Count > 1 ? $"\n\n{segments.Count - 1} offset(s)." : "\n\nStraight up, no offsets.") +
                                  (roofNotes.Count > 0 ? "\n\nRoof sleeve: " + string.Join("; ", roofNotes) : "") +
                                  (leftOut.Count > 0 ? "\n\nNot in the riser: " + string.Join("; ", leftOut) : "") +
                                  (replace ? $"\n\nReplaced the previous riser ({removed} element(s) removed)." : "") +
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

        /// <summary>
        /// The slabs the riser passes through, ascending: the floors above the electrical room's floor (the conduits rise
        /// from it, rule 4), each once. Left out, with the reason: the room's own floor and below, reference levels with no
        /// floor plan, and a second level for a floor that already has one (e.g. "06.6-TH FLOOR" beside "06.6 TH FLOOR NEW").
        /// </summary>
        private static List<ClassifiedLevel> RiserLevels(Document doc, LevelMap levels, ElectricalSurvey survey, out List<string> leftOut)
        {
            leftOut = new List<string>();
            var all = levels.All.Where(l => l.Role != LevelRole.Roof && l.Role != LevelRole.Bulkhead).OrderBy(l => l.Elevation).ToList();
            if (all.Count == 0) return all;

            var planned = new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null).Select(v => v.GenLevel.Id));

            // the electrical room's floor (rule 2); with none named, the lowest floor, where the manual says to look
            var roomFloor = survey.Read.Start == null ? null : all.FirstOrDefault(l => survey.For(l.Level) == survey.Read.Start);
            var start = roomFloor ?? all[0];
            string startWhy = roomFloor != null ? $"the {survey.Read.Start.RoomText.ToLowerInvariant()}'s floor" : "the lowest floor, where the electrical room is assumed";

            var kept = new List<ClassifiedLevel>();
            foreach (var l in all)
            {
                string why = null;
                if (l.Elevation < start.Elevation - 1e-6) why = "below the electrical room";
                else if (l == start) why = startWhy;
                else if (!planned.Contains(l.Level.Id)) why = "no floor plan (a reference level)";
                else
                {
                    string key = Automation.Drawings.FloorKey.FromLevelName(l.Name);
                    var twin = key == null ? null : kept.FirstOrDefault(k => Automation.Drawings.FloorKey.FromLevelName(k.Name) == key);
                    if (twin != null)
                    {
                        // one floor, two levels: keep the one the drawings were matched to
                        if (survey.For(l.Level) != null && survey.For(twin.Level) == null)
                        {
                            kept.Remove(twin);
                            leftOut.Add($"{twin.Name} (same floor as {l.Name})");
                        }
                        else why = $"same floor as {twin.Name}";
                    }
                }
                if (why != null) leftOut.Add($"{l.Name} ({why})");
                else kept.Add(l);
            }
            App.Log("ELEC| RISER LEVELS " + string.Join(", ", kept.Select(k => k.Name)) + "; left out: " + (leftOut.Count == 0 ? "none" : string.Join("; ", leftOut)));
            return kept;
        }

        /// <summary>Shows what the search found. CommandLink1 = place it, CommandLink2 = click instead.</summary>
        private static TaskDialogResult Propose(List<ElectricalSegment> segments, List<RiserRun> runs, ElectricalSurvey survey, ElectricalLocator locator)
        {
            var lines = new List<string>();
            if (!locator.StructureSeen)
                lines.Add("NO STRUCTURE LOADED: columns, shear walls, beams and the slab edge were not checked (rules 26-28). Check the spot on the structural plans.");
            for (int i = 0; i < segments.Count && i < runs.Count; i++)
            {
                var s = segments[i]; var r = runs[i];
                lines.Add(r.Point == null
                    ? $"{s.FloorsText}: NO SPOT FOUND ({r.Why}) — you will click it"
                    : $"{s.FloorsText}: {s.Circles} circles, {Units.FormatInches(s.Width)} x {Units.FormatInches(s.Length)}, " +
                      $"{r.Distance:0.#} ft from {(i == 0 ? "the " + survey.Read.Start.RoomText.ToLowerInvariant() : "the run below")}, in {string.Join(" / ", r.Rooms.Distinct())}" +
                      (r.Unnamed ? " — NO named closet or shaft would do here, check this spot" : ""));
            }
            // the room name is only as good as the walls the drawing carries: say so rather than let it read as a check
            var blind = runs.Where(r => r.Point != null).SelectMany(r => r.NoWalls).Distinct().ToList();
            if (blind.Count > 0)
                lines.Add($"The drawing for {string.Join(", ", blind)} carries no walls, so on {(blind.Count == 1 ? "that floor" : "those floors")} " +
                          "the room name is the nearest label, not a check, and whether the spot sits inside a wall was not tested.");
            foreach (var l in lines) App.Log("ELEC| PROPOSAL " + l);
            var td = new TaskDialog("Electrical riser")
            {
                MainInstruction = runs.Any(r => r.Point == null) ? "No allowed spot for some floors — you will click those"
                                : segments.Count == 1 ? "Found a spot that runs straight up" : $"Found the riser in {segments.Count} runs ({segments.Count - 1} offset(s))",
                MainContent = string.Join("\n", lines) + "\n\n(\"?\" = the drawing names no room there.) Clear of columns, beams, shear walls and other " +
                              "openings, in a closet or shaft where the drawings name one, as close to the electrical room as a clear path allows " +
                              "(rule 3). Check it on the plans after placing.",
                AllowCancellation = true
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Place it there");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "I will click the spot myself");
            return td.Show();
        }

        /// <summary>
        /// Moves the opening so the middle of what it draws sits on the riser point, where the circles are drawn. The office
        /// Openings family is not drawn around its insertion point (it keeps the box's top edge 6" above it and sizes
        /// downward), so placed at the point the box sat above its circles. Inside a transaction.
        /// </summary>
        private static void CentreOn(Document doc, FamilyInstance inst, XYZ point, Level level, string what)
        {
            doc.Regenerate();                                   // the size just set, so the box is the new one
            var shape = new ShapeReader(doc).Of(inst, level);
            if (!shape.Found)
            {
                App.Log($"ELEC| CENTRE {what} {inst.Id} on '{level.Name}': nothing to measure, left at its insertion point");
                return;
            }

            var mid = shape.Middle(point.Z);
            double dx = point.X - mid.X, dy = point.Y - mid.Y;
            bool centred = Math.Abs(dx) < 1.0 / 192 && Math.Abs(dy) < 1.0 / 192;    // within 1/16"
            App.Log($"ELEC| CENTRE {what} {inst.Id} on '{level.Name}': {shape.Source} {Units.FormatInches(shape.Width * 12)} x {Units.FormatInches(shape.Length * 12)}, " +
                    $"its middle was {Units.FormatInches(Math.Abs(dx) * 12)} {(dx > 0 ? "left" : "right")} and {Units.FormatInches(Math.Abs(dy) * 12)} {(dy > 0 ? "below" : "above")} the riser point" +
                    (centred ? "; already centred" : "; moved onto it"));
            if (centred) return;
            ElementTransformUtils.MoveElement(doc, inst.Id, new XYZ(dx, dy, 0));
        }

        /// <summary>Deletes the riser being replaced: its openings and the conduit circles drawn over them. Inside a transaction.</summary>
        private static int Remove(Document doc, List<OpeningRecord> existing, Rules.ElectricalRules e)
        {
            double r = Units.InchesToFeet(e.CircleDiameter) / 2;
            var doomed = new HashSet<ElementId>(existing.Select(o => o.Instance.Id));
            foreach (var curve in new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
            {
                if (!(curve is DetailCurve) || !(curve.GeometryCurve is Arc arc) || Math.Abs(arc.Radius - r) > 1e-4) continue;
                var level = (doc.GetElement(curve.OwnerViewId) as ViewPlan)?.GenLevel;
                if (level == null) continue;
                // a circle within an opening's reach on that opening's level belongs to it
                if (existing.Any(o => o.Level.Id == level.Id && RiserRecord.Dist(o.Point, arc.Center) < 3)) doomed.Add(curve.Id);
            }
            var deleted = doc.Delete(doomed.ToList());
            App.Log($"ELEC| REMOVED {existing.Count} opening(s) and {doomed.Count - existing.Count} circle arc(s) of the old riser ({deleted.Count} element(s) in all)");
            return deleted.Count;
        }

        /// <summary>Loads each role's office family from rules.json "file" when the project has none mapped. Outside any transaction.</summary>
        private static void LoadMissingFamilies(Document doc, Rules.RuleSet rules, ProjectState state, params string[] roles)
        {
            var missing = roles.Where(r => FamilyMapping.FindSymbol(doc, FamilyMapping.Get(rules, state, r)) == null).ToList();
            if (missing.Count == 0) return;
            using (var t = new Transaction(doc, "Sleeves & Openings: load families"))
            {
                t.Start();
                foreach (var role in missing)
                {
                    var rule = rules.Family(role);
                    if (rule == null || string.IsNullOrEmpty(rule.Family) || ProjectSetup.FindFamily(doc, rule.Family) != null) continue;
                    var path = Rules.RuleLoader.ResolveFamilyFile(rule);
                    if (path == null || !System.IO.File.Exists(path)) { App.Log("ELEC| FAMILY " + role + ": no file to load (" + path + ")"); continue; }
                    try { App.Log("ELEC| FAMILY " + role + ": " + (doc.LoadFamily(path, out Family f) ? "loaded '" + f.Name + "'" : "NOT loaded") + " from " + path); }
                    catch (Exception ex) { App.Log("ELEC| FAMILY " + role + ": loading " + path + " failed: " + ex.Message); }
                }
                t.Commit();
            }
        }

        /// <summary>Where the instance really ended up and the size it really shows, read back from the model.</summary>
        private static void LogPlaced(string what, FamilyInstance inst, Level level, FamilyMapEntry map, string wanted, bool dumpParams)
        {
            var p = (inst.Location as LocationPoint)?.Point;
            string Read(string name)
            {
                if (string.IsNullOrEmpty(name)) return "(no param)";
                var prm = inst.LookupParameter(name) ?? inst.Symbol.LookupParameter(name);
                return prm == null ? "(missing)" : prm.StorageType == StorageType.Double ? Units.FormatInches(prm.AsDouble() * 12) : prm.AsValueString() ?? prm.AsString();
            }
            App.Log("ELEC| PLACED " + what + " id " + inst.Id + " on '" + level.Name + "' at " +
                    (p == null ? "(no point)" : "(" + p.X.ToString("0.###") + ", " + p.Y.ToString("0.###") + ", " + p.Z.ToString("0.###") + ")") +
                    " ft, type '" + inst.Symbol.Name + "', " + wanted + ", got W=" + Read(map.WidthParam) + " L=" + Read(map.LengthParam) + " D=" + Read(map.DiameterParam));
            if (!dumpParams) return;
            // every length the family exposes, so a size that does not stick can be traced to the parameter that drives it
            foreach (var (owner, e) in new[] { ("instance", (Element)inst), ("type", inst.Symbol) })
                foreach (Parameter prm in e.Parameters)
                    if (prm.StorageType == StorageType.Double && prm.Definition != null)
                        App.Log($"ELEC|   PARAM {owner} '{prm.Definition.Name}' = {prm.AsValueString()}{(prm.IsReadOnly ? " (read-only)" : "")}{(prm.IsShared ? " (shared)" : "")}");
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
    }
}
