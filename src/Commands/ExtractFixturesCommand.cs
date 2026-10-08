using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Extract Fixtures (its own ribbon panel): pick a floor, list every fixture the model shows there with where it is.
    /// The architect's DWG on that level (toilets, tubs, showers, basins, washers by shape) and Revit plumbing fixtures of
    /// this model and its links. Each row: type, location (X/Y and the nearest grids), size and sleeve point. From the
    /// list: zoom to a fixture, mark them in the floor's plan, export CSV, place their sleeves.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExtractFixturesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = data.Application.ActiveUIDocument;
                if (uidoc?.Document == null) { TaskDialog.Show("Extract Fixtures", "Open a model first."); return Result.Cancelled; }
                var start = (uidoc.Document.ActiveView as ViewPlan)?.GenLevel;
                using (var form = new UI.ExtractFixturesForm(uidoc, start))
                    form.ShowDialog(UI.RevitWindow.Instance);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                App.Log("Extract fixtures failed: " + ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Reads the fixtures of one floor (no transaction) and places or marks them (own transactions).</summary>
    public static class FixtureExtractor
    {
        private const double MarkRadius = 0.25;       // feet
        /// <summary>A fixture stands on a level from this far (feet) below it up to this far below the next level.</summary>
        private const double LevelTolerance = 1.0;
        /// <summary>The same fixture read twice (two DWGs of the floor) within this many feet is one.</summary>
        private const double SameFixtureFeet = 1.0;
        /// <summary>A sleeve already in the model this close (feet) to a fixture's sleeve point is that fixture's sleeve.</summary>
        private const double ExistingFeet = 1.5;

        public class Row
        {
            /// <summary>Rules fixture code (WC, LAV, BT...), "?" when the family is not one the rules sleeve.</summary>
            public string Code;
            /// <summary>What it is: the shape found ("LAV/SINK") or the family and type name.</summary>
            public string Name;
            public string Source, How, Level, Grid;
            public double X0, Y0, X1, Y1;             // feet, model coordinates
            public List<XYZ> Points = new List<XYZ>();
            /// <summary>The family instance in this model (null for DWG drawings and linked families).</summary>
            public ElementId Id;
            public bool FromDwg;
            public double Cx => (X0 + X1) / 2;
            public double Cy => (Y0 + Y1) / 2;
            public bool Sleeved(PlumbingRules rules) => Code != null && rules.Fixtures.ContainsKey(Code);
        }

        /// <summary>The model's levels, lowest first.</summary>
        public static List<Level> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ThenBy(l => l.Name).ToList();

        public static List<Row> Extract(Document doc, Level level, PlumbingRules rules, bool dwg, bool revit, out List<DwgFixtureReader.Plan> plans)
        {
            (int, double) Sleeves(string code) => rules.Fixtures.TryGetValue(code, out var fs) ? (fs.Count, fs.Spacing) : (1, 0);
            var rows = new List<Row>();
            plans = new List<DwgFixtureReader.Plan>();

            if (dwg)
            {
                plans = DwgFixtureReader.Read(doc, rules, level.Name);
                foreach (var p in plans)
                    foreach (var s in FixtureDrains.Extract(p.Strokes, p.Walls, Sleeves, 13, 72))
                        rows.Add(new Row
                        {
                            Code = s.Code == "LAV/SINK" ? "LAV" : s.Code, Name = s.Code, Source = p.Source, How = s.How, Level = level.Name, FromDwg = true,
                            X0 = s.X0 / 12, Y0 = s.Y0 / 12, X1 = s.X1 / 12, Y1 = s.Y1 / 12,
                            Points = s.Points.Select(q => new XYZ(q.X / 12, q.Y / 12, 0)).ToList()
                        });
            }

            if (revit)
            {
                // the floor of a fixture is the level it stands on by height (a linked model names its levels its own way)
                var above = Levels(doc).FirstOrDefault(l => l.ProjectElevation > level.ProjectElevation + 0.01);
                double lo = level.ProjectElevation - LevelTolerance, hi = (above?.ProjectElevation ?? double.MaxValue) - LevelTolerance;
                void Read(Document d, Transform t, string source, bool own)
                {
                    foreach (var fi in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
                    {
                        string name = $"{fi.Symbol?.Family?.Name} : {fi.Symbol?.Name}".Trim();
                        if (OpeningData.Read(fi) != null || name.IndexOf("sleeve", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        var box = fi.get_BoundingBox(null);
                        var local = (fi.Location as LocationPoint)?.Point ?? (box == null ? null : (box.Min + box.Max) / 2);
                        if (local == null) continue;
                        var pt = t.OfPoint(local);
                        if (pt.Z < lo || pt.Z >= hi) continue;

                        var row = new Row
                        {
                            Code = FixtureSleeveLocator.Classify(name, rules) ?? "?", Name = name, Source = source, Level = level.Name, Id = own ? fi.Id : null,
                            X0 = pt.X, Y0 = pt.Y, X1 = pt.X, Y1 = pt.Y
                        };
                        if (box != null)
                        {
                            var c = new[] { box.Min, new XYZ(box.Max.X, box.Min.Y, box.Min.Z), new XYZ(box.Min.X, box.Max.Y, box.Min.Z), box.Max }.Select(t.OfPoint).ToList();
                            row.X0 = c.Min(q => q.X); row.Y0 = c.Min(q => q.Y); row.X1 = c.Max(q => q.X); row.Y1 = c.Max(q => q.Y);
                        }
                        var drains = new List<XYZ>();
                        var manager = fi.MEPModel?.ConnectorManager;
                        if (manager != null)
                            foreach (Connector cn in manager.Connectors)
                                if (cn.Domain == Domain.DomainPiping && cn.ConnectorType == ConnectorType.End &&
                                    cn.PipeSystemType.ToString().IndexOf("Sanitary", StringComparison.OrdinalIgnoreCase) >= 0)
                                    drains.Add(t.OfPoint(cn.Origin));
                        row.Points = (drains.Count > 0 ? drains : new List<XYZ> { pt }).Select(q => new XYZ(q.X, q.Y, 0)).ToList();
                        row.How = drains.Count > 0 ? "sanitary connector" : "insertion point (no sanitary connector)";
                        rows.Add(row);
                    }
                }
                Read(doc, Transform.Identity, "Revit family (this model)", true);
                foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linked = link.GetLinkDocument();
                    if (linked != null) Read(linked, link.GetTotalTransform(), $"Revit link '{linked.Title}'", false);
                }
            }

            // one row per fixture: the same toilet in the architect's and the engineer's DWG is one
            var kept = new List<Row>();
            foreach (var r in rows)
                if (!kept.Any(k => k.Code == r.Code && k.FromDwg == r.FromDwg && k.Points[0].DistanceTo(r.Points[0]) <= SameFixtureFeet)) kept.Add(r);

            var grids = Grids(doc);
            foreach (var r in kept)
                r.Grid = GridRef(grids, r.Points.Count > 0 ? r.Points.Average(p => p.X) : r.Cx, r.Points.Count > 0 ? r.Points.Average(p => p.Y) : r.Cy);
            return kept.OrderBy(r => r.Code).ThenByDescending(r => r.Cy).ThenBy(r => r.Cx).ToList();
        }

        // ------------------------------------------------------------------ location

        private static List<(string Name, XYZ O, XYZ D)> Grids(Document doc)
        {
            var list = new List<(string, XYZ, XYZ)>();
            void Add(Document d, Transform t)
            {
                foreach (var g in new FilteredElementCollector(d).OfClass(typeof(Grid)).Cast<Grid>())
                {
                    if (!(g.Curve is Line ln)) continue;
                    var a = t.OfPoint(ln.GetEndPoint(0)); var b = t.OfPoint(ln.GetEndPoint(1));
                    var dir = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                    if (dir.GetLength() < 1e-6) continue;
                    list.Add((g.Name, new XYZ(a.X, a.Y, 0), dir.Normalize()));
                }
            }
            Add(doc, Transform.Identity);
            if (list.Count == 0)
                foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linked = link.GetLinkDocument();
                    if (linked == null) continue;
                    Add(linked, link.GetTotalTransform());
                    if (list.Count > 0) break;
                }
            return list;
        }

        /// <summary>The two nearest crossing grids and the distance to each: C/3: 1'-2" from C, 0'-8" from 3.</summary>
        private static string GridRef(List<(string Name, XYZ O, XYZ D)> grids, double x, double y)
        {
            if (grids.Count == 0) return "";
            var p = new XYZ(x, y, 0);
            double Off((string Name, XYZ O, XYZ D) g) { var v = p - g.O; return Math.Abs(v.X * g.D.Y - v.Y * g.D.X); }
            var first = grids.OrderBy(Off).First();
            var second = grids.Where(g => Math.Abs(g.D.DotProduct(first.D)) < 0.5).OrderBy(Off).FirstOrDefault();
            if (second.Name == null) return $"{Ft(Off(first))} from {first.Name}";
            return $"{first.Name}/{second.Name}: {Ft(Off(first))} from {first.Name}, {Ft(Off(second))} from {second.Name}";
        }

        /// <summary>Feet as 12'-3 1/2" text (to the quarter inch).</summary>
        public static string Ft(double feet)
        {
            bool neg = feet < 0; feet = Math.Abs(feet);
            int f = (int)Math.Floor(feet);
            double inch = Math.Round((feet - f) * 12 * 4) / 4;
            if (inch >= 12) { f++; inch -= 12; }
            int whole = (int)Math.Floor(inch); double frac = inch - whole;
            string fr = frac >= 0.74 ? " 3/4" : frac >= 0.49 ? " 1/2" : frac >= 0.24 ? " 1/4" : "";
            return (neg ? "-" : "") + $"{f}'-{whole}{fr}\"";
        }

        // ------------------------------------------------------------------ output

        /// <summary>A floor plan of the level: the active view when it is one, else the floor's Sleeves view, else any floor plan.</summary>
        public static ViewPlan PlanFor(Document doc, Level level, View active)
        {
            if (active is ViewPlan vp && vp.GenLevel?.Id == level.Id && !vp.IsTemplate) return vp;
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                                                         .Where(v => !v.IsTemplate && v.GenLevel?.Id == level.Id && v.ViewType == ViewType.FloorPlan).ToList();
            return plans.FirstOrDefault(v => v.Name.IndexOf("sleeve", StringComparison.OrdinalIgnoreCase) >= 0) ?? plans.FirstOrDefault();
        }

        /// <summary>Detail lines in the view: each fixture's box, a circle at each sleeve point, its type as text. Returns the ids.</summary>
        public static List<ElementId> Mark(Document doc, ViewPlan view, Level level, IEnumerable<Row> rows)
        {
            var ids = new List<ElementId>();
            using (var t = new Transaction(doc, "Extract fixtures: mark (undo to remove)"))
            {
                t.Start();
                double z = level.ProjectElevation;
                var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                foreach (var r in rows)
                {
                    var corners = new[] { new XYZ(r.X0, r.Y0, z), new XYZ(r.X1, r.Y0, z), new XYZ(r.X1, r.Y1, z), new XYZ(r.X0, r.Y1, z) };
                    for (int i = 0; i < 4; i++)
                    {
                        var a = corners[i]; var b = corners[(i + 1) % 4];
                        if (a.DistanceTo(b) > 0.01) ids.Add(doc.Create.NewDetailCurve(view, Line.CreateBound(a, b)).Id);
                    }
                    foreach (var p in r.Points)
                        ids.Add(doc.Create.NewDetailCurve(view, Arc.Create(new XYZ(p.X, p.Y, z), MarkRadius, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY)).Id);
                    if (textType != ElementId.InvalidElementId)
                        ids.Add(TextNote.Create(doc, view.Id, new XYZ(r.X1 + 0.2, r.Y1, z), r.Code == "?" ? r.Name : r.Code, textType).Id);
                }
                t.Commit();
            }
            return ids;
        }

        public static void SaveCsv(string path, IEnumerable<Row> rows)
        {
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("Level,Type,Name,Location (grids),Centre X (ft),Centre Y (ft),Width (in),Length (in),Sleeve points (ft),Source,How\n");
            foreach (var r in rows)
                csv.AppendLine(string.Join(",", Q(r.Level), Q(r.Code), Q(r.Name), Q(r.Grid), r.Cx.ToString("0.###"), r.Cy.ToString("0.###"),
                                           ((r.X1 - r.X0) * 12).ToString("0.#"), ((r.Y1 - r.Y0) * 12).ToString("0.#"),
                                           Q(string.Join("; ", r.Points.Select(p => $"{p.X:0.###} {p.Y:0.###}"))), Q(r.Source), Q(r.How)));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }

        public static string DefaultCsv(Document doc, Level level)
        {
            string Safe(string s) => string.Concat((s ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "fixtures", Safe(doc.Title));
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, Safe(level.Name) + ".csv");
        }

        /// <summary>
        /// Sleeves at the fixtures, through Auto Run's placer (sizes from rules plumbing.fixtures, office families, structure
        /// checks). A fixture with a sleeve already near its sleeve point is left as it is.
        /// </summary>
        public static string Place(Document doc, Level level, List<Row> rows, PlumbingRules plumbing)
        {
            var rules = App.Rules(doc);
            var state = ProjectStore.Load(doc);
            var existing = ExistingCheck.Run(doc, rules, state);
            string floor = FloorKey.FromLevelName(level.Name) ?? level.Name;
            var crossings = new List<Crossing>();
            int already = 0;
            foreach (var r in rows.Where(r => r.Sleeved(plumbing)))
            {
                if (existing.Items.Any(e => e.Point != null && e.Source != "native opening" && AutoPlacer.SameFloor(e.Level, level.Name) &&
                                            r.Points.Any(p => Math.Sqrt(Math.Pow(e.Point.X - p.X, 2) + Math.Pow(e.Point.Y - p.Y, 2)) <= ExistingFeet)))
                { already++; continue; }
                var fs = plumbing.Fixtures[r.Code];
                var c = new Crossing
                {
                    Floor = floor, Level = level.Name, Tag = r.Code, System = fs.System, Size = new DuctSize { Diameter = fs.Pipe },
                    X = r.Points.Average(p => p.X), Y = r.Points.Average(p => p.Y), Status = Crossing.Place, Confidence = "medium"
                };
                c.From.Add($"{FloorKey.Describe(floor)}: fixture '{r.Code}' ({fs.Name}) from {r.Source}");
                if (r.Points.Count > 1) { c.EachPoint = true; c.Points.AddRange(r.Points.Select(p => new[] { p.X, p.Y })); }
                crossings.Add(c);
            }
            if (crossings.Count == 0) return $"Nothing to place: {already} fixture(s) already have a sleeve within 1'-6\".";

            var placer = new AutoPlacer(doc, rules, state, existing, state.Automation.Existing);
            placer.LoadMissingFamilies(crossings);
            var missing = placer.MissingFamilies(crossings);
            if (missing.Count > 0) return "No family loaded for " + string.Join(", ", missing.Select(FamilyRole.Describe)) + ": map it with Map Families first.";
            PlaceCommandBase.EnsureSharedParams(doc, rules, state);
            var outcomes = placer.Run(crossings);
            foreach (var o in outcomes)
                App.Log($"Extract fixtures: {o.Result} {o.Crossing.Name} {o.Size} on {o.Crossing.Level}" +
                        (o.Ids.Count > 0 ? " [" + string.Join(",", o.Ids.Select(i => i.ToString())) + "]" : "") + (o.Detail != null ? ": " + o.Detail : ""));
            return $"{level.Name}: {outcomes.Count(o => o.Result == PlacementOutcome.Placed)} sleeve(s) placed, " +
                   $"{already + outcomes.Count(o => o.Result == PlacementOutcome.Existing)} already had one, " +
                   $"{outcomes.Count(o => o.Result == PlacementOutcome.Skipped)} skipped (structure or no family), {outcomes.Count(o => o.Result == PlacementOutcome.Failed)} failed." +
                   (outcomes.Any(o => o.Result == PlacementOutcome.Skipped || o.Result == PlacementOutcome.Failed)
                       ? "\n\n" + string.Join("\n", outcomes.Where(o => o.Detail != null && o.Result != PlacementOutcome.Existing).Select(o => $"{o.Crossing.Name}: {o.Detail}")) : "");
        }
    }
}
