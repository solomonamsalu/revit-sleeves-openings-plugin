using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using SleevesOpenings.Automation;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Placement;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Extract Stacks (Extract panel, a test bench before Auto Run uses it): pick a floor, find its plumbing stacks from the
    /// model alone (no engineer's drawing): vertical pipes modelled in Revit, else the wet walls of the fixtures Extract
    /// Fixtures finds (a chase drawn next to them, else the wall behind them), laid out by the office rule. From the list:
    /// zoom, mark in the plan, export CSV, place the sleeves.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExtractStacksCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = data.Application.ActiveUIDocument;
                if (uidoc?.Document == null) { TaskDialog.Show("Extract Stacks", "Open a model first."); return Result.Cancelled; }
                var start = (uidoc.Document.ActiveView as ViewPlan)?.GenLevel;
                using (var form = new UI.ExtractStacksForm(uidoc, start))
                    form.ShowDialog(UI.RevitWindow.Instance);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                App.Log("Extract stacks failed: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Reads one floor's stacks (no transaction) and places or marks them (own transactions).</summary>
    public static class StackExtractor
    {
        /// <summary>A pipe this close to vertical (direction's Z) is a riser.</summary>
        private const double Vertical = 0.9;
        /// <summary>A sleeve already in the model this close (feet) to a stack sleeve's spot is that sleeve.</summary>
        private const double ExistingFeet = 0.5;

        public class Floor
        {
            public string Level;
            public List<StackFinder.Stack> Stacks = new List<StackFinder.Stack>();
            public int Fixtures, Pipes, Water;
            public List<string> Dwgs = new List<string>();
            public List<string> Notes = new List<string>();
            /// <summary>Stack id -> nearest grids.</summary>
            public Dictionary<string, string> Grids = new Dictionary<string, string>();
        }

        /// <param name="withFixtures">Also each fixture's own sleeves (drain, vents, an island sink's row).</param>
        public static Floor Extract(Document doc, Level level, PlumbingRules rules, bool withFixtures)
        {
            var floor = new Floor { Level = level.Name };
            var rows = FixtureExtractor.Extract(doc, level, rules, true, true, false, out var plans, out _, out var notes);
            floor.Notes.AddRange(notes.Distinct());
            floor.Dwgs.AddRange(plans.Select(p => p.Source).Distinct());
            var fixtures = rows.Where(r => r.Sleeved(rules) && r.Points.Count > 0).Select(r => new StackFinder.Fixture
            {
                Code = r.Code, Name = r.Name, Source = r.Source, How = r.How, X = r.Points.Average(p => p.X), Y = r.Points.Average(p => p.Y), Back = r.Back,
                Points = r.Points.Select(p => (p.X, p.Y)).ToList(), Island = (r.How ?? "").IndexOf("island", StringComparison.OrdinalIgnoreCase) >= 0
            }).ToList();
            floor.Fixtures = fixtures.Count;
            var shafts = plans.SelectMany(p => p.Shafts).Select(l => ((l.A.X / 12, l.A.Y / 12), (l.B.X / 12, l.B.Y / 12))).ToList();
            var pipes = Pipes(doc, level, floor.Notes);
            floor.Pipes = pipes.Count;
            // water risers a plumbing DWG on the level draws: evidence that hot / cold go up there
            var water = plans.SelectMany(p => p.Risers.Select(c => (C: c, P: p)))
                             .Select(x => new StackFinder.Mark { X = x.C.X / 12, Y = x.C.Y / 12, Source = x.P.Source,
                                                                 System = rules.Services.TryGetValue(rules.ServiceForLayer(x.C.Layer) ?? "", out var sv) ? sv.System : null })
                             .Where(m => m.System == "HotWater" || m.System == "ColdWater").ToList();
            floor.Water = water.Count + pipes.Count(p => p.System == "HotWater" || p.System == "ColdWater");

            var found = StackFinder.Find(fixtures, shafts, pipes, water, rules, withFixtures);
            floor.Stacks = found.Stacks;
            var grids = FixtureExtractor.Grids(doc);
            foreach (var s in floor.Stacks) floor.Grids[s.Id] = FixtureExtractor.GridRef(grids, s.X, s.Y);
            App.Log($"Extract stacks {level.Name}: {fixtures.Count} fixture(s), {pipes.Count} vertical pipe(s), {floor.Water} water riser(s), {shafts.Count} shaft line(s) -> " +
                    $"{floor.Stacks.Count} row(s) ({string.Join(", ", floor.Stacks.GroupBy(s => s.Kind).Select(g => $"{g.Count()} {g.Key}"))})");
            foreach (var s in floor.Stacks)
                App.Log($"  {s.Id} {s.Kind} ({s.Source}) at ({s.X:0.00},{s.Y:0.00}): {string.Join(" ", s.Sleeves.Select(v => $"{Name(v)} {v.Size:0}\" ({v.X:0.00},{v.Y:0.00})"))}; " +
                        $"serves {string.Join(", ", s.Serves.Select(f => f.Code))}; {s.How}" + (s.Missing.Count > 0 ? "; not placed: " + string.Join("; ", s.Missing) : ""));
            return floor;
        }

        /// <summary>Vertical pipes of this model and its links that cross the level's slab, with their system.</summary>
        private static List<StackFinder.Pipe> Pipes(Document doc, Level level, List<string> notes)
        {
            var list = new List<StackFinder.Pipe>();
            double z = level.ProjectElevation;
            int unknown = 0;
            void Read(Document d, Transform t, string source)
            {
                foreach (var pipe in new FilteredElementCollector(d).OfClass(typeof(Pipe)).Cast<Pipe>())
                {
                    if (!(pipe.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                    var a = t.OfPoint(line.GetEndPoint(0)); var b = t.OfPoint(line.GetEndPoint(1));
                    var dir = b - a;
                    if (dir.GetLength() < 1e-6 || Math.Abs(dir.Normalize().Z) < Vertical) continue;
                    if (Math.Min(a.Z, b.Z) > z - 0.1 || Math.Max(a.Z, b.Z) < z + 0.1) continue;         // does not go through this slab
                    string system = SystemOf(pipe);
                    if (system == null) { unknown++; continue; }
                    double k = (z - a.Z) / (b.Z - a.Z);
                    list.Add(new StackFinder.Pipe { X = a.X + (b.X - a.X) * k, Y = a.Y + (b.Y - a.Y) * k, System = system, Diameter = pipe.Diameter * 12, Source = source });
                }
            }
            Read(doc, Transform.Identity, "this model");
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = link.GetLinkDocument();
                if (linked != null) Read(linked, link.GetTotalTransform(), $"link '{linked.Title}'");
            }
            if (unknown > 0) notes.Add($"{unknown} vertical pipe(s) through this slab whose system is not plumbing the rules know (skipped)");
            return list;
        }

        /// <summary>The rules' system name of a pipe from its system classification and system type name; null = not one we sleeve.</summary>
        private static string SystemOf(Pipe pipe)
        {
            string cls = pipe.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? "";
            string type = (pipe.Document.GetElement(pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId))?.Name ?? "";
            string s = (cls + " " + type).ToUpperInvariant();
            if (s.Contains("STORM") || s.Contains("RAIN")) return "Storm";
            if (s.Contains("GAS")) return "Gas";
            if (s.Contains("VENT")) return "Vent";
            if (s.Contains("SANITARY") || s.Contains("WASTE") || s.Contains("SOIL")) return "Sanitary";
            if (s.Contains("HOT")) return "HotWater";
            if (s.Contains("COLD")) return "ColdWater";
            return null;
        }

        /// <summary>Detail lines: each sleeve as a circle of its size, a line from the stack to each fixture it serves, its id as text.</summary>
        public static List<ElementId> Mark(Document doc, ViewPlan view, Level level, IEnumerable<StackFinder.Stack> stacks)
        {
            var ids = new List<ElementId>();
            using (var t = new Transaction(doc, "Extract stacks: mark (undo to remove)"))
            {
                t.Start();
                double z = level.ProjectElevation;
                var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                foreach (var s in stacks)
                {
                    foreach (var v in s.Sleeves)
                        ids.Add(doc.Create.NewDetailCurve(view, Arc.Create(new XYZ(v.X, v.Y, z), v.Size / 24, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)).Id);
                    foreach (var f in s.Serves)
                    {
                        var a = new XYZ(s.X, s.Y, z); var b = new XYZ(f.X, f.Y, z);
                        if (a.DistanceTo(b) > 0.05) ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(a, b)).Id);
                    }
                    if (textType != ElementId.InvalidElementId)
                        ids.Add(TextNote.Create(doc, view.Id, new XYZ(s.X + 0.4, s.Y + 0.4, z),
                                                $"{s.Id} {s.Kind}" + (s.StackSleeves.Any() ? " " + string.Join("/", s.StackSleeves.Select(v => v.Service)) : ""), textType).Id);
                }
                t.Commit();
            }
            return ids;
        }

        public static void SaveCsv(string path, Floor floor)
        {
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("Level,Row,Kind,Source,Location (grids),X (ft),Y (ft),Stack sleeves,Fixture sleeves,Serves,Not placed,How,Notes\n");
            string List(IEnumerable<StackFinder.Sleeve> v) => string.Join("; ", v.Select(x => $"{Name(x)} {x.Size:0}\" at {x.X:0.###} {x.Y:0.###}"));
            foreach (var s in floor.Stacks)
                csv.AppendLine(string.Join(",", Q(floor.Level), Q(s.Id), Q(s.Kind), Q(s.Source), Q(floor.Grids.TryGetValue(s.Id, out var g) ? g : ""), s.X.ToString("0.###"), s.Y.ToString("0.###"),
                                           Q(List(s.StackSleeves)), Q(List(s.FixtureSleeves)), Q(string.Join(", ", s.Serves.Select(f => f.Code))),
                                           Q(string.Join("; ", s.Missing)), Q(s.How), Q(string.Join("; ", s.Notes))));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }

        /// <summary>A sleeve's name: the stack's service (S, V, HW) or the fixture's (WC, V-WC, HW-KS).</summary>
        public static string Name(StackFinder.Sleeve v) => v.Fixture == null || v.Service == v.Fixture ? v.Service : v.Service + "-" + v.Fixture;

        public static string DefaultCsv(Document doc, Level level)
        {
            string Safe(string s) => string.Concat((s ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "stacks", Safe(doc.Title));
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Safe(level.Name) + ".csv");
        }

        /// <summary>
        /// The rows' sleeves through Auto Run's placer (office families, structure checks): the stacks' as risers (S-M3), the
        /// fixtures' own as fixture sleeves (WC, V-WC, HW-KS); one already there (within 6") is left as it is.
        /// </summary>
        public static string Place(Document doc, Level level, List<StackFinder.Stack> stacks, PlumbingRules plumbing)
        {
            var rules = App.Rules(doc);
            var state = ProjectStore.Load(doc);
            var existing = ExistingCheck.Run(doc, rules, state);
            string floor = FloorKey.FromLevelName(level.Name) ?? level.Name;
            var crossings = new List<Crossing>();
            int already = 0;
            bool There(StackFinder.Sleeve v) => existing.Items.Any(e => e.Point != null && e.Source != "native opening" && AutoPlacer.SameFloor(e.Level, level.Name) &&
                                                                    Math.Sqrt(Math.Pow(e.Point.X - v.X, 2) + Math.Pow(e.Point.Y - v.Y, 2)) <= ExistingFeet);
            foreach (var s in stacks)
            {
                // the stack's sleeves: a riser each (S-M3)
                foreach (var v in s.StackSleeves)
                {
                    if (There(v)) { already++; continue; }
                    var c = new Crossing
                    {
                        Floor = floor, Level = level.Name, Tag = plumbing.Name(v.Service, s.Id), System = v.System, Size = new DuctSize { Diameter = v.Pipe },
                        X = v.X, Y = v.Y, HasPosition = true, Status = Crossing.Place, Confidence = s.Check ? "medium" : "high", Check = s.Check
                    };
                    c.From.Add($"{FloorKey.Describe(floor)}: model stack {s.Id} ({s.Source})");
                    c.Notes.Add(s.How);
                    crossings.Add(c);
                }
                // each fixture's own sleeves: one opening per fixture and service (a tub's two drains together)
                foreach (var own in s.FixtureSleeves.GroupBy(v => v.Owner))
                {
                    var list = own.Where(v => !There(v)).ToList();
                    already += own.Count() - list.Count;
                    if (list.Count == 0) continue;
                    var v = list[0];
                    var c = new Crossing
                    {
                        Floor = floor, Level = level.Name, Tag = Name(v), System = v.System, Size = new DuctSize { Diameter = v.Pipe },
                        X = list.Average(x => x.X), Y = list.Average(x => x.Y), HasPosition = true, Status = Crossing.Place, Confidence = "medium", Check = true
                    };
                    if (list.Count > 1) { c.EachPoint = true; c.Points.AddRange(list.Select(x => new[] { x.X, x.Y })); }
                    c.From.Add($"{FloorKey.Describe(floor)}: fixture '{v.Fixture}' ({(plumbing.Fixtures.TryGetValue(v.Fixture, out var fs) ? fs.Name : v.Fixture)}) {s.Id}");
                    c.Notes.Add($"{Name(v)}: the fixture's own sleeve ({s.Kind} {s.Id}), by the office rule");
                    crossings.Add(c);
                }
            }
            if (crossings.Count == 0) return $"Nothing to place: {already} sleeve(s) already in the model there.";

            var placer = new AutoPlacer(doc, rules, state, existing, state.Automation.Existing);
            placer.LoadMissingFamilies(crossings);
            var missing = placer.MissingFamilies(crossings);
            if (missing.Count > 0) return "No family loaded for " + string.Join(", ", missing.Select(FamilyRole.Describe)) + ": map it with Map Families first.";
            PlaceCommandBase.EnsureSharedParams(doc, rules, state);
            var outcomes = placer.Run(crossings);
            foreach (var o in outcomes)
                App.Log($"Extract stacks: {o.Result} {o.Crossing.Name} {o.Size} on {o.Crossing.Level}" +
                        (o.Ids.Count > 0 ? " [" + string.Join(",", o.Ids.Select(i => i.ToString())) + "]" : "") + (o.Detail != null ? ": " + o.Detail : ""));
            return $"{level.Name}: {outcomes.Count(o => o.Result == PlacementOutcome.Placed)} sleeve(s) placed, " +
                   $"{already + outcomes.Count(o => o.Result == PlacementOutcome.Existing)} already had one, " +
                   $"{outcomes.Count(o => o.Result == PlacementOutcome.Skipped)} skipped (structure or no family), {outcomes.Count(o => o.Result == PlacementOutcome.Failed)} failed." +
                   (outcomes.Any(o => o.Result == PlacementOutcome.Skipped || o.Result == PlacementOutcome.Failed)
                       ? "\n\n" + string.Join("\n", outcomes.Where(o => o.Detail != null && o.Result != PlacementOutcome.Existing).Select(o => $"{o.Crossing.Name}: {o.Detail}")) : "");
        }
    }
}
