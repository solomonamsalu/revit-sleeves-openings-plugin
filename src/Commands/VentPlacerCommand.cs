using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Automation;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Placement;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Vent Placer (Extract panel): tick floors, lay out their vent (yellow) sleeves from the model alone as the Standards
    /// Manual's toilet sleeves page draws them (each fixture's vent in its wet wall, one vent stack per bathroom row, shared
    /// back to back, in line floor to floor). From the list: zoom, mark in the plan, export CSV, place the sleeves.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class VentPlacerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = data.Application.ActiveUIDocument;
                if (uidoc?.Document == null) { TaskDialog.Show("Vent Placer", "Open a model first."); return Result.Cancelled; }
                var start = (uidoc.Document.ActiveView as ViewPlan)?.GenLevel;
                using (var form = new UI.VentPlacerForm(uidoc, start))
                    form.ShowDialog(UI.RevitWindow.Instance);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                App.Log("Vent placer failed: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Reads the ticked floors' vents (no transaction) and places or marks them (own transactions).</summary>
    public static class VentExtractor
    {
        /// <summary>A stack already in the model named S-T3 / V-T3: new vent stacks number past it.</summary>
        private static readonly Regex StackName = new Regex(@"^[SV]-T(\d+)$", RegexOptions.IgnoreCase);

        public class Floor
        {
            public Level Level;
            public List<VentFinder.Row> Rows = new List<VentFinder.Row>();
            public int Fixtures;
            public List<string> Notes = new List<string>();
            public bool OnGrade;
        }

        /// <summary>One vent sleeve of the list, with its floor and row.</summary>
        public class Item
        {
            public Floor Floor;
            public VentFinder.Row Row;
            public VentFinder.Vent Vent;
            public string Grid;
        }

        public class Run
        {
            public List<Floor> Floors = new List<Floor>();
            public List<Item> Items = new List<Item>();
            public List<string> Notes = new List<string>();
        }

        /// <summary>The floors top down, so each vent stack lines up with the one above it and takes its name.</summary>
        public static Run Extract(Document doc, List<Level> levels, PlumbingRules rules)
        {
            var run = new Run();
            var all = FixtureExtractor.Levels(doc);
            var lowest = all.FirstOrDefault();
            var state = ProjectStore.Load(doc);
            var existing = ExistingCheck.Run(doc, App.Rules(doc), state);
            var grids = FixtureExtractor.Grids(doc);

            // vent stacks already in the model (V-T#) on any floor: this run lines up with them and numbers past them
            var namer = new VentFinder.Namer();
            foreach (var e in existing.Items.Where(e => e.Riser != null && e.Point != null))
            {
                var m = StackName.Match(e.Riser);
                if (!m.Success) continue;
                namer.Last = Math.Max(namer.Last, int.Parse(m.Groups[1].Value));
                if (e.System == "Vent") namer.Stacks.Add(new VentFinder.StackSpot { X = e.Point.X, Y = e.Point.Y, Name = e.Riser, Floor = e.Level });
            }

            foreach (var level in levels.OrderByDescending(l => l.ProjectElevation))
            {
                var floor = new Floor { Level = level };
                run.Floors.Add(floor);
                if (rules.SkipLowestSlab && lowest != null && level.Id == lowest.Id)
                {
                    floor.OnGrade = true;
                    floor.Notes.Add($"{level.Name} is the lowest level: its slab is on grade, no sleeves (plumbing.skipLowestSlab)");
                    continue;
                }
                var rows = FixtureExtractor.Extract(doc, level, rules, true, true, false, out _, out _, out var notes);
                floor.Notes.AddRange(notes.Distinct());
                var fixtures = rows.Where(r => r.Sleeved(rules) && r.Points.Count > 0).Select(r => new StackFinder.Fixture
                {
                    Code = r.Code, Name = r.Name, Source = r.Source, How = r.How, X = r.Points.Average(p => p.X), Y = r.Points.Average(p => p.Y), Back = r.Back,
                    Points = r.Points.Select(p => (p.X, p.Y)).ToList(), Island = (r.How ?? "").IndexOf("island", StringComparison.OrdinalIgnoreCase) >= 0
                }).ToList();
                floor.Fixtures = fixtures.Count;
                var here = existing.Items.Where(e => e.Point != null && e.Source != "native opening" && AutoPlacer.SameFloor(e.Level, level.Name))
                                   .Select(e => new VentFinder.Existing
                                   {
                                       X = e.Point.X, Y = e.Point.Y, System = e.System, Name = e.Riser,
                                       // the families do not say their size here: the office's usual ones
                                       Size = e.System == "Sanitary" ? 6 : e.System == "Vent" && e.Riser != null && StackName.IsMatch(e.Riser) ? 6 : 4
                                   }).ToList();
                var found = VentFinder.Find(fixtures, here, rules, namer, level.Name);
                floor.Rows = found.Rows;
                foreach (var r in found.Rows)
                    foreach (var v in r.Vents)
                        run.Items.Add(new Item { Floor = floor, Row = r, Vent = v, Grid = FixtureExtractor.GridRef(grids, v.X, v.Y) });
                App.Log($"Vent placer {level.Name}: {fixtures.Count} fixture(s), {here.Count} sleeve(s) in the model -> {found.Rows.Count} row(s), {found.Vents.Count()} vent sleeve(s)");
                foreach (var r in found.Rows)
                    App.Log($"  {r.Id} {r.Kind}: {string.Join(" ", r.Vents.Select(v => $"{v.Name} {v.Size:0}\" ({v.X:0.00},{v.Y:0.00})"))}; serves {string.Join(", ", r.Serves.Select(f => f.Code))}" +
                            (r.Skipped.Count > 0 ? "; skipped: " + string.Join("; ", r.Skipped) : "") + (r.Notes.Count > 0 ? "; " + string.Join("; ", r.Notes) : ""));
            }
            return run;
        }

        /// <summary>Detail lines: each vent as a circle of its size, a line from each vent stack to the fixtures of its row, the name as text.</summary>
        public static List<ElementId> Mark(Document doc, ViewPlan view, Level level, IEnumerable<Item> items)
        {
            var ids = new List<ElementId>();
            using (var t = new Transaction(doc, "Vent placer: mark (undo to remove)"))
            {
                t.Start();
                double z = level.ProjectElevation;
                var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                foreach (var i in items)
                {
                    var v = i.Vent;
                    ids.Add(doc.Create.NewDetailCurve(view, Arc.Create(new XYZ(v.X, v.Y, z), v.Size / 24, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)).Id);
                    if (v.IsStack)
                        foreach (var f in i.Row.Serves)
                        {
                            var a = new XYZ(v.X, v.Y, z); var b = new XYZ(f.X, f.Y, z);
                            if (a.DistanceTo(b) > 0.05) ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(a, b)).Id);
                        }
                    if (textType != ElementId.InvalidElementId)
                        ids.Add(TextNote.Create(doc, view.Id, new XYZ(v.X + 0.3, v.Y + 0.3, z), v.Name, textType).Id);
                }
                t.Commit();
            }
            return ids;
        }

        public static void SaveCsv(string path, IEnumerable<Item> items, Run run)
        {
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("Level,Row,Row kind,Name,Kind,Fixture,Size (in),Location (grids),X (ft),Y (ft),How,Notes\n");
            foreach (var i in items)
                csv.AppendLine(string.Join(",", Q(i.Floor.Level.Name), Q(i.Row.Id), Q(i.Row.Kind), Q(i.Vent.Name), Q(i.Vent.Kind), Q(i.Vent.Fixture), i.Vent.Size.ToString("0"),
                                           Q(i.Grid), i.Vent.X.ToString("0.###"), i.Vent.Y.ToString("0.###"), Q(i.Vent.How),
                                           Q(string.Join("; ", i.Vent.Notes.Concat(i.Row.Notes)))));
            foreach (var f in run.Floors)
                foreach (var r in f.Rows)
                    foreach (var s in r.Skipped)
                        csv.AppendLine(string.Join(",", Q(f.Level.Name), Q(r.Id), Q(r.Kind), Q(""), Q("not placed"), Q(""), "", Q(""), "", "", Q(s), Q("")));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }

        public static string DefaultCsv(Document doc)
        {
            string Safe(string s) => string.Concat((s ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "vents");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Safe(doc.Title) + ".csv");
        }

        /// <summary>The vents through Auto Run's placer (office families, structure checks), flagged for a check; one already there is left as it is.</summary>
        public static string Place(Document doc, List<Item> items)
        {
            var rules = App.Rules(doc);
            var state = ProjectStore.Load(doc);
            var existing = ExistingCheck.Run(doc, rules, state);
            var crossings = new List<Crossing>();
            foreach (var i in items)
            {
                var v = i.Vent;
                var level = i.Floor.Level;
                string floor = FloorKey.FromLevelName(level.Name) ?? level.Name;
                var c = new Crossing
                {
                    Floor = floor, Level = level.Name, Tag = v.Name, System = v.System, Size = new DuctSize { Diameter = v.Pipe },
                    X = v.X, Y = v.Y, HasPosition = true, Status = Crossing.Place, Confidence = "medium", Check = true, FromModel = true
                };
                c.From.Add(v.IsStack ? $"{FloorKey.Describe(floor)}: vent stack {v.Name} of row {i.Row.Id} (Vent Placer)"
                                     : $"{FloorKey.Describe(floor)}: fixture '{v.Fixture}' {v.Kind} (Vent Placer, row {i.Row.Id})");
                c.Notes.Add($"{v.Size:0}\" {v.Kind}: {v.How}; by the office rule (manual, toilet sleeves page), check");
                c.Notes.AddRange(v.Notes);
                crossings.Add(c);
            }
            if (crossings.Count == 0) return "Nothing to place.";

            var placer = new AutoPlacer(doc, rules, state, existing, state.Automation.Existing);
            placer.LoadMissingFamilies(crossings);
            var missing = placer.MissingFamilies(crossings);
            if (missing.Count > 0) return "No family loaded for " + string.Join(", ", missing.Select(FamilyRole.Describe)) + ": map it with Map Families first.";
            PlaceCommandBase.EnsureSharedParams(doc, rules, state);
            var outcomes = placer.Run(crossings);
            foreach (var o in outcomes)
                App.Log($"Vent placer: {o.Result} {o.Crossing.Name} {o.Size} on {o.Crossing.Level}" +
                        (o.Ids.Count > 0 ? " [" + string.Join(",", o.Ids.Select(x => x.ToString())) + "]" : "") + (o.Detail != null ? ": " + o.Detail : ""));
            return $"{outcomes.Count(o => o.Result == PlacementOutcome.Placed)} vent sleeve(s) placed on {items.Select(i => i.Floor.Level.Name).Distinct().Count()} floor(s), " +
                   $"{outcomes.Count(o => o.Result == PlacementOutcome.Existing)} already had one, " +
                   $"{outcomes.Count(o => o.Result == PlacementOutcome.Skipped)} skipped (structure or no family), {outcomes.Count(o => o.Result == PlacementOutcome.Failed)} failed." +
                   (outcomes.Any(o => o.Result == PlacementOutcome.Skipped || o.Result == PlacementOutcome.Failed)
                       ? "\n\n" + string.Join("\n", outcomes.Where(o => o.Detail != null && o.Result != PlacementOutcome.Existing).Select(o => $"{o.Crossing.Level} {o.Crossing.Name}: {o.Detail}")) : "");
        }
    }
}
