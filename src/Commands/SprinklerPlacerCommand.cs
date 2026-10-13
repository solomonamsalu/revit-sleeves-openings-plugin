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
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Commands
{
    /// <summary>
    /// Sprinkler Placer (Extract panel): tick floors, lay out the standpipe / sprinkler riser sleeves from the stairs alone
    /// (no pipes are modelled before the sleeves) by the Standards Manual's p16: in the stairwell, the same spot top to
    /// bottom, 5 1/2" pipe centre to any wall, sleeve = pipe + 2"; one riser per stair enclosure (Combined, then Standpipe
    /// Only). From the list: zoom, mark in the plan, export CSV, change the corner / kind / pipe, add one, place them.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SprinklerPlacerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = data.Application.ActiveUIDocument;
                if (uidoc?.Document == null) { TaskDialog.Show("Sprinkler Placer", "Open a model first."); return Result.Cancelled; }
                var start = (uidoc.Document.ActiveView as ViewPlan)?.GenLevel;
                SprinklerExtractor.Run run = null;
                while (true)
                {
                    System.Windows.Forms.DialogResult result;
                    using (var form = new UI.SprinklerPlacerForm(uidoc, start, run))
                    {
                        result = form.ShowDialog(UI.RevitWindow.Instance);
                        run = form.LaidOut;
                    }
                    if (result != System.Windows.Forms.DialogResult.Retry || run == null) break;
                    // Add riser: a click in the plan, then the window again
                    XYZ pt;
                    try { pt = uidoc.Selection.PickPoint(Autodesk.Revit.UI.Selection.ObjectSnapTypes.None, "Sprinkler Placer: click the new riser's centre (manual: 5 1/2\" from any wall, 8\" c-c from another riser). Esc = back."); }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { continue; }
                    SprinklerExtractor.AddRiser(uidoc.Document, run, pt.X, pt.Y);
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                App.Log("Sprinkler placer failed: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Reads the ticked floors' stairs (no transaction) and lays out the risers; marks and places them (own transactions).</summary>
    public static class SprinklerExtractor
    {
        /// <summary>One riser sleeve on one floor.</summary>
        public class Item
        {
            public StairFinder.Riser Riser;
            /// <summary>The combined riser's drain sleeve (SP-1D), not the riser itself.</summary>
            public bool IsDrain;
            public string Name => IsDrain ? Riser.Name + "D" : Riser.Name;
            public string Kind => IsDrain ? "Drain (combined riser's FCVA)" : Riser.Kind;
            public double Pipe => IsDrain ? Riser.Drain : Riser.Pipe;
            public double X => IsDrain ? Riser.DrainX : Riser.X;
            public double Y => IsDrain ? Riser.DrainY : Riser.Y;
            public Level Level;
            public StairFinder.Floor Floor;
            public double Sleeve;
            public string Grid;
            /// <summary>This floor's drawings show the riser's stair (else the spot comes from the other floors).</summary>
            public bool StairShown;
        }

        public class Run
        {
            public StairFinder.Plan Plan;
            public List<Level> Levels = new List<Level>();
            public List<Item> Items = new List<Item>();
            public double Clear;
            public List<string> Notes = new List<string>();
        }

        /// <summary>Lays out the risers over the ticked floors (top down in the list); the bottom and bulkhead floors only show the stair.</summary>
        public static Run Extract(Document doc, List<Level> levels, RuleSet rules)
        {
            var sp = rules.SprinklerPlacer ?? new SprinklerPlacerRules();
            var run = new Run { Clear = rules.Systems.Standpipe.MinCenterToWall, Levels = levels.OrderByDescending(l => l.ProjectElevation).ToList() };
            var lowest = FixtureExtractor.Levels(doc).FirstOrDefault();
            var skip = string.IsNullOrWhiteSpace(sp.SkipLevels) ? null : new Regex(sp.SkipLevels, RegexOptions.IgnoreCase);
            double sleeve = Sleeve(rules, Math.Max(sp.Pipe, sp.StandpipePipe));
            var floors = new List<StairFinder.Floor>();
            foreach (var level in levels.OrderBy(l => l.ProjectElevation))
            {
                var fl = new StairFinder.Floor { Level = level.Name, Elevation = level.ProjectElevation };
                if ((rules.Sprinkler?.SkipLowestSlab ?? true) && lowest != null && level.Id == lowest.Id) fl.Skip = "the lowest level: its slab is on grade (sprinkler.skipLowestSlab)";
                else if (skip != null && skip.IsMatch(level.Name)) fl.Skip = "the bulkhead roof: the riser ends under it (sprinklerPlacer.skipLevels)";
                try { fl.Drawings.AddRange(Drawings(doc, level, sp, fl.Notes)); }
                catch (Exception ex) { fl.Notes.Add("drawings not read: " + ex.Message); App.Log("Sprinkler placer drawings " + level.Name + ": " + ex); }
                if (fl.Drawings.Count == 0) fl.Notes.Add("no DWG and no Revit stair on this level");
                floors.Add(fl);
            }
            run.Plan = StairFinder.Risers(floors, sp, run.Clear, sleeve, rules.Systems.Standpipe.MinCenterToCenter);
            Build(doc, run, rules);
            foreach (var fl in floors)
                App.Log($"Sprinkler placer {fl.Level}{(fl.Skip != null ? " (no sleeve: " + fl.Skip + ")" : "")}: " + string.Join("; ", fl.Notes));
            foreach (var r in run.Plan.Risers)
                App.Log($"  {r.Name} {r.Kind} {r.Stair} at ({r.X:0.000},{r.Y:0.000}), {r.Options.Count} spot(s){(r.Review ? ", review" : "")}: {string.Join("; ", r.Notes)}");
            foreach (var n in run.Plan.Notes) App.Log("  " + n);
            return run;
        }

        /// <summary>A riser where the user clicked (a second standpipe in one stair, a stair the drawings do not show), on every ticked floor.</summary>
        public static StairFinder.Riser AddRiser(Document doc, Run run, double x, double y)
        {
            var rules = App.Rules(doc);
            int n = run.Plan.Risers.Count + 1;
            while (run.Plan.Risers.Any(o => o.Name == "SP-" + n)) n++;
            var r = new StairFinder.Riser
            {
                Name = "SP-" + n, Kind = run.Plan.Risers.Count == 0 ? StairFinder.Sprinkler : StairFinder.Standpipe, Stair = "picked", X = x, Y = y,
                Pipe = run.Plan.Risers.Count == 0 ? rules.SprinklerPlacer?.Pipe ?? 3 : run.Plan.Risers.Max(o => o.Pipe), Review = true, Shown = run.Plan.Floors.Select(f => f.Level).ToList()
            };
            r.Notes.Add("placed by hand (Add riser)");
            foreach (var o in run.Plan.Risers)
            {
                double cc = Math.Sqrt(Math.Pow(o.X - x, 2) + Math.Pow(o.Y - y, 2)) * 12;
                if (cc < rules.Systems.Standpipe.MinCenterToCenter)
                    r.Notes.Add($"{cc:0.#}\" c-c from {o.Name}: the manual asks {Units.FormatInches(rules.Systems.Standpipe.MinCenterToCenter)} at least");
            }
            run.Plan.Risers.Add(r);
            Build(doc, run, rules);
            return r;
        }

        /// <summary>The sleeve for a pipe (sprinkler.sleeveOverPipe, rounded up to sprinkler.sleeveSizes).</summary>
        public static double Sleeve(RuleSet rules, double pipe) =>
            rules.Sprinkler != null ? rules.Sprinkler.SleeveFor(pipe) : rules.Systems.Standpipe.SleeveSize(pipe);

        /// <summary>The list again from the risers (after a change of corner, kind, pipe or an added riser): one row per riser per sleeved floor.</summary>
        public static void Build(Document doc, Run run, RuleSet rules)
        {
            var grids = FixtureExtractor.Grids(doc);
            run.Items.Clear();
            foreach (var level in run.Levels)
            {
                var fl = run.Plan.Floors.FirstOrDefault(f => f.Level == level.Name);
                if (fl == null || fl.Skip != null) continue;
                foreach (var r in run.Plan.Risers)
                    foreach (bool drain in new[] { false, true })
                    {
                        if (drain && (r.Drain <= 0 || double.IsNaN(r.DrainX))) continue;
                        var it = new Item { Riser = r, IsDrain = drain, Level = level, Floor = fl, StairShown = r.Shown.Contains(level.Name) };
                        it.Sleeve = Sleeve(rules, it.Pipe);
                        it.Grid = FixtureExtractor.GridRef(grids, it.X, it.Y);
                        run.Items.Add(it);
                    }
            }
        }

        // ------------------------------------------------------------------ the floor's drawings

        /// <summary>
        /// The level's DWGs (their lines, door arcs and, from the file, their words) and the Revit stairs and walls of this
        /// model and its links at the level, turned into lines for <see cref="StairFinder"/>. Model feet.
        /// </summary>
        public static List<StairFinder.Drawing> Drawings(Document doc, Level level, SprinklerPlacerRules sp, List<string> notes)
        {
            var list = new List<StairFinder.Drawing>();
            foreach (var ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var type = doc.GetElement(ii.GetTypeId());
                string name = type?.Name ?? "";
                if (!name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) continue;
                View owner = ii.ViewSpecific ? doc.GetElement(ii.OwnerViewId) as View : null;
                var lv = (owner as ViewPlan)?.GenLevel
                         ?? doc.GetElement(ii.get_Parameter(BuiltInParameter.IMPORT_BASE_LEVEL)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                if (lv == null || !AutoPlacer.SameFloor(lv.Name, level.Name)) continue;
                // the same DWG placed twice on the level (imported and linked, two views): read once
                if (list.Any(d => d.Source == name)) continue;
                var d = new StairFinder.Drawing { Source = name };
                try
                {
                    var options = new Options { IncludeNonVisibleObjects = true };
                    if (owner != null) options.View = owner;
                    var geometry = ii.get_Geometry(options);
                    if (geometry != null) Walk(geometry, doc, d, null);
                }
                catch (Exception ex) { notes.Add($"{name}: not read ({ex.Message})"); continue; }
                Words(doc, ii, type, name, d, sp, notes);
                list.Add(d);
            }

            var revit = new StairFinder.Drawing { Source = "Revit stairs and walls" };
            void Model(Document m, Transform t)
            {
                try { RevitStairs(m, t, level, revit); } catch (Exception ex) { notes.Add($"Revit stairs of {m.Title} not read: {ex.Message}"); }
                try { RevitWalls(m, t, level, revit); } catch (Exception ex) { notes.Add($"Revit walls of {m.Title} not read: {ex.Message}"); }
            }
            Model(doc, Transform.Identity);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = link.GetLinkDocument();
                if (linked != null) Model(linked, link.GetTotalTransform());
            }
            if (revit.Lines.Any(l => l.Layer == RevitStair)) list.Add(revit);
            else if (revit.Lines.Count > 0 && list.Count > 0) list[0].Lines.AddRange(revit.Lines);      // Revit walls round a DWG's stair
            return list;
        }

        private const string RevitStair = "STAIR (Revit)", RevitWall = "WALL (Revit)";

        /// <summary>The DWG's lines and arcs as Revit shows them (blocks in model coordinates); a block's layer-0 lines take its layer.</summary>
        private static void Walk(GeometryElement geometry, Document doc, StairFinder.Drawing d, string blockLayer)
        {
            string LayerOf(GeometryObject o)
            {
                string own = (doc.GetElement(o.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                return (own == "" || own == "0") && blockLayer != null ? blockLayer : own;
            }
            foreach (var obj in geometry)
            {
                if (obj is GeometryInstance gi)
                {
                    string insert = LayerOf(gi);
                    var inner = gi.GetInstanceGeometry();
                    if (inner != null) Walk(inner, doc, d, insert != "" && insert != "0" ? insert : blockLayer);
                    continue;
                }
                string layer = LayerOf(obj);
                switch (obj)
                {
                    case Line ln:
                        Add(d, layer, ln.GetEndPoint(0), ln.GetEndPoint(1));
                        break;
                    case Arc arc when arc.IsBound:
                        {
                            var a = arc.GetEndPoint(0); var b = arc.GetEndPoint(1);
                            d.Arcs.Add(new StairFinder.Swing { Layer = layer, Cx = arc.Center.X, Cy = arc.Center.Y, R = arc.Radius, Ax = a.X, Ay = a.Y, Bx = b.X, By = b.Y });
                            break;
                        }
                    case Curve c when c.IsBound && !(c is Arc):
                        {
                            var pts = c.Tessellate();
                            for (int i = 0; i + 1 < pts.Count; i++) Add(d, layer, pts[i], pts[i + 1]);
                            break;
                        }
                    case PolyLine pl:
                        {
                            var pts = pl.GetCoordinates();
                            for (int i = 0; i + 1 < pts.Count; i++) Add(d, layer, pts[i], pts[i + 1]);
                            break;
                        }
                }
            }
        }

        private static void Add(StairFinder.Drawing d, string layer, XYZ a, XYZ b) =>
            d.Lines.Add(new StairFinder.Seg { Layer = layer, X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y });

        /// <summary>
        /// The DWG file's words (stair names, a standpipe note) where Revit shows the drawing: Revit does not show a DWG's
        /// text as geometry, so the file is read and its words moved by the import's placement, at the scale under which
        /// the file's stair and wall lines land on the lines Revit shows.
        /// </summary>
        private static void Words(Document doc, ImportInstance ii, Element type, string name, StairFinder.Drawing d, SprinklerPlacerRules sp, List<string> notes)
        {
            string path = DwgFixtureReader.FileOf(doc, type, name);
            if (path == null) { notes.Add($"{name}: its file was not found: no stair names from it"); return; }
            try
            {
                var cad = DwgSheetIndex.Open(path);
                var file = DwgFlat.Read(cad, name, 1);
                if (file.Texts.Count == 0) return;
                var t = ii.GetTotalTransform();
                double import = type?.get_Parameter(BuiltInParameter.IMPORT_SCALE)?.AsDouble() ?? 0;
                if (import <= 0) import = ii.get_Parameter(BuiltInParameter.IMPORT_INSTANCE_SCALE)?.AsDouble() ?? 1;
                double unit = DwgFixtureBlocks.InchesPerUnit(cad.Header.InsUnits.ToString()) / 12;
                var layers = new Regex($"({sp.StairLayers})|({sp.WallLayers})", RegexOptions.IgnoreCase);
                var shown = new HashSet<(long, long)>(d.Lines.Where(l => layers.IsMatch(l.Layer ?? "")).SelectMany(l => new[] { Key(l.X0, l.Y0), Key(l.X1, l.Y1) }));
                var sample = file.Lines.Where(l => layers.IsMatch(l.Layer ?? "")).Take(400).ToList();
                if (shown.Count == 0 || sample.Count == 0) { notes.Add($"{name}: no stair or wall lines to line its words up with"); return; }
                double best = -1, scale = unit;
                foreach (var s in new[] { unit * import, unit, import, 1.0 / 12, 1.0 }.Where(s => s > 0).Distinct())
                {
                    int hit = sample.Count(l =>
                    {
                        var q = t.OfPoint(new XYZ(l.X0 * s, l.Y0 * s, 0));
                        var k = Key(q.X, q.Y);
                        for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) if (shown.Contains((k.Item1 + dx, k.Item2 + dy))) return true;
                        return false;
                    });
                    double fit = (double)hit / sample.Count;
                    if (fit > best) { best = fit; scale = s; }
                }
                if (best < 0.5) { notes.Add($"{name}: the file does not line up with the drawing Revit shows ({best:P0}): no stair names from it"); return; }
                foreach (var w in file.Texts)
                {
                    var q = t.OfPoint(new XYZ(w.X * scale, w.Y * scale, 0));
                    d.Texts.Add(new StairFinder.Txt { Layer = w.Layer, Text = w.Text, X = q.X, Y = q.Y });
                }
            }
            catch (Exception ex) { notes.Add($"{name}: words not read ({ex.Message})"); }
        }

        /// <summary>A point to 1/10 ft cells, for matching line ends.</summary>
        private static (long, long) Key(double x, double y) => ((long)Math.Round(x * 10), (long)Math.Round(y * 10));

        /// <summary>Revit stairs at the level (base below it, top above it): each straight run drawn as its treads (11" apart).</summary>
        private static void RevitStairs(Document m, Transform t, Level level, StairFinder.Drawing d)
        {
            double z = level.ProjectElevation;
            foreach (var st in new FilteredElementCollector(m).OfCategory(BuiltInCategory.OST_Stairs).WhereElementIsNotElementType().OfType<Autodesk.Revit.DB.Architecture.Stairs>())
            {
                var box = st.get_BoundingBox(null);
                if (box == null) continue;
                double lo = t.OfPoint(box.Min).Z, hi = t.OfPoint(box.Max).Z;
                if (z < lo - 1 || z > hi + 1) continue;
                foreach (var id in st.GetStairsRuns())
                {
                    if (!(m.GetElement(id) is Autodesk.Revit.DB.Architecture.StairsRun run)) continue;
                    var path = run.GetStairsPath().FirstOrDefault(c => c is Line) as Line;
                    if (path == null) continue;
                    var a = t.OfPoint(path.GetEndPoint(0)); var b = t.OfPoint(path.GetEndPoint(1));
                    var dir = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                    double len = dir.GetLength();
                    if (len < 2) continue;
                    dir = dir.Normalize();
                    var across = new XYZ(-dir.Y, dir.X, 0);
                    double half = run.ActualRunWidth / 2;
                    // the path runs on the run's centre line, or on its left edge: centre it on the footprint
                    var foot = run.GetFootprintBoundary();
                    if (foot != null)
                    {
                        var pts = foot.SelectMany(c => new[] { t.OfPoint(c.GetEndPoint(0)), t.OfPoint(c.GetEndPoint(1)) }).ToList();
                        double off = pts.Average(p => (p.X - a.X) * across.X + (p.Y - a.Y) * across.Y);
                        a = new XYZ(a.X + across.X * off, a.Y + across.Y * off, 0);
                    }
                    int n = Math.Max(2, (int)Math.Round(len / (11.0 / 12)));
                    for (int i = 0; i <= n; i++)
                    {
                        var c = new XYZ(a.X + dir.X * len * i / n, a.Y + dir.Y * len * i / n, 0);
                        d.Lines.Add(new StairFinder.Seg { Layer = RevitStair, X0 = c.X - across.X * half, Y0 = c.Y - across.Y * half, X1 = c.X + across.X * half, Y1 = c.Y + across.Y * half });
                    }
                }
            }
        }

        /// <summary>Revit walls standing at the level: their two faces as lines.</summary>
        private static void RevitWalls(Document m, Transform t, Level level, StairFinder.Drawing d)
        {
            double z = level.ProjectElevation + 3;
            foreach (var w in new FilteredElementCollector(m).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line ln)) continue;
                var box = w.get_BoundingBox(null);
                if (box == null) continue;
                if (z < t.OfPoint(box.Min).Z || z > t.OfPoint(box.Max).Z) continue;
                var a = t.OfPoint(ln.GetEndPoint(0)); var b = t.OfPoint(ln.GetEndPoint(1));
                var dir = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                if (dir.GetLength() < 1e-6) continue;
                dir = dir.Normalize();
                var n = new XYZ(-dir.Y, dir.X, 0);
                double half = w.Width / 2;
                foreach (var s in new[] { -half, half })
                    d.Lines.Add(new StairFinder.Seg { Layer = RevitWall, X0 = a.X + n.X * s, Y0 = a.Y + n.Y * s, X1 = b.X + n.X * s, Y1 = b.Y + n.Y * s });
            }
        }

        // ------------------------------------------------------------------ output

        /// <summary>Detail lines: each sleeve as a circle of its size with a cross, its name as text.</summary>
        public static List<ElementId> Mark(Document doc, ViewPlan view, Level level, IEnumerable<Item> items)
        {
            var ids = new List<ElementId>();
            using (var t = new Transaction(doc, "Sprinkler placer: mark (undo to remove)"))
            {
                t.Start();
                double z = level.ProjectElevation;
                var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                foreach (var i in items)
                {
                    var c = new XYZ(i.X, i.Y, z);
                    double r = i.Sleeve / 24;
                    ids.Add(doc.Create.NewDetailCurve(view, Arc.Create(c, r, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)).Id);
                    ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(c + new XYZ(-r, -r, 0) * 0.7, c + new XYZ(r, r, 0) * 0.7)).Id);
                    ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(c + new XYZ(-r, r, 0) * 0.7, c + new XYZ(r, -r, 0) * 0.7)).Id);
                    if (textType != ElementId.InvalidElementId)
                        ids.Add(TextNote.Create(doc, view.Id, new XYZ(c.X + 0.4, c.Y + 0.4, z), $"{i.Name} {i.Sleeve:0}\"", textType).Id);
                }
                t.Commit();
            }
            return ids;
        }

        public static void SaveCsv(string path, IEnumerable<Item> items, Run run)
        {
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("Level,Name,Kind,Stair,Pipe (in),Sleeve (in),Location (grids),X (ft),Y (ft),Review,Notes\n");
            foreach (var i in items)
                csv.AppendLine(string.Join(",", Q(i.Level.Name), Q(i.Name), Q(i.Kind), Q(i.Riser.Stair), i.Pipe.ToString("0.##"), i.Sleeve.ToString("0.##"),
                                           Q(i.Grid), i.X.ToString("0.###"), i.Y.ToString("0.###"), i.Riser.Review ? "yes" : "",
                                           Q(string.Join("; ", i.Riser.Notes.Concat(i.StairShown ? new string[0] : new[] { "stair not drawn on this floor: same spot as the others" })))));
            foreach (var f in run.Plan.Floors)
                foreach (var n in f.Notes)
                    csv.AppendLine(string.Join(",", Q(f.Level), Q("note"), Q(f.Skip ?? ""), "", "", "", Q(""), "", "", "", Q(n)));
            foreach (var n in run.Plan.Notes)
                csv.AppendLine(string.Join(",", Q(""), Q("note"), "", "", "", "", Q(""), "", "", "", Q(n)));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }

        public static string DefaultCsv(Document doc)
        {
            string Safe(string s) => string.Concat((s ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "sprinkler");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Safe(doc.Title) + ".csv");
        }

        /// <summary>The riser sleeves through Auto Run's placer (MPI round sleeve, Fire toggle, structure checks); one already there is left as it is.</summary>
        public static string Place(Document doc, List<Item> items)
        {
            var rules = App.Rules(doc);
            var state = ProjectStore.Load(doc);
            var existing = ExistingCheck.Run(doc, rules, state);
            var crossings = new List<Crossing>();
            foreach (var i in items.OrderBy(i => i.Level.ProjectElevation))
            {
                var r = i.Riser;
                string floor = FloorKey.FromLevelName(i.Level.Name) ?? i.Level.Name;
                var c = new Crossing
                {
                    Floor = floor, Level = i.Level.Name, Tag = i.Name, System = "Standpipe", Size = new DuctSize { Diameter = i.Pipe }, SleeveSize = i.Sleeve,
                    X = i.X, Y = i.Y, HasPosition = true, Status = Crossing.Place, Confidence = r.Review ? "medium" : "high", Check = r.Review || !i.StairShown, FromModel = true
                };
                c.From.Add($"{FloorKey.Describe(floor)}: {r.Label} (Sprinkler Placer, manual p16)");
                c.Notes.Add($"{i.Kind}, {i.Pipe:0.##}\" pipe, {i.Sleeve:0.##}\" sleeve");
                c.Notes.AddRange(r.Notes);
                if (!i.StairShown) c.Notes.Add("this floor's drawings do not show the stair: same spot as the floors that do");
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
                App.Log($"Sprinkler placer: {o.Result} {o.Crossing.Name} {o.Size} on {o.Crossing.Level}" +
                        (o.Ids.Count > 0 ? " [" + string.Join(",", o.Ids.Select(x => x.ToString())) + "]" : "") + (o.Detail != null ? ": " + o.Detail : ""));
            return $"{outcomes.Count(o => o.Result == PlacementOutcome.Placed)} sleeve(s) placed on {items.Select(i => i.Level.Name).Distinct().Count()} floor(s), " +
                   $"{outcomes.Count(o => o.Result == PlacementOutcome.Existing)} already had one, " +
                   $"{outcomes.Count(o => o.Result == PlacementOutcome.Skipped)} skipped (structure or no family), {outcomes.Count(o => o.Result == PlacementOutcome.Failed)} failed." +
                   (outcomes.Any(o => o.Warnings.Count > 0) ? "\n\nWarnings:\n" + string.Join("\n", outcomes.Where(o => o.Warnings.Count > 0).Select(o => $"{o.Crossing.Level} {o.Crossing.Name}: {string.Join("; ", o.Warnings)}")) : "") +
                   (outcomes.Any(o => o.Result == PlacementOutcome.Skipped || o.Result == PlacementOutcome.Failed)
                       ? "\n\n" + string.Join("\n", outcomes.Where(o => o.Detail != null && o.Result != PlacementOutcome.Existing).Select(o => $"{o.Crossing.Level} {o.Crossing.Name}: {o.Detail}")) : "");
        }
    }
}
