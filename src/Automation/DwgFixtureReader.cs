using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// The architect's plans as they sit in the model: the DWGs imported or linked on each level (the toilets, tubs and
    /// sinks a drafter sees, when the architecture is not a Revit model). Their lines on the fixture layers and the wall
    /// layers, in model inches, ready for <see cref="FixtureDrains"/> (the same reader as the PDF's fixture drawings).
    /// An alteration drawing draws its whole new layout on one layer ("TO ADD": fixtures, walls, doors, counters): on
    /// such a layer (plumbing.dwgNewLayers) curves, polylines, diagonal lines, short lines and everything in a block are
    /// fixture strokes and straight lines 2 ft and longer outside blocks are walls (AEC walls come as lines); what it
    /// removes ("TO REMOVE", plumbing.dwgRemoveLayers) is skipped, block and all. Revit keeps a linked DWG's geometry even when its file is gone. Call on Revit's thread.
    /// </summary>
    public static class DwgFixtureReader
    {
        /// <summary>On a new-work layer, a straight line this long (inches) and longer is a wall, shorter is part of a fixture.</summary>
        private const double NewWallInches = 24;
        /// <summary>On a new-work layer, a straight line up to this long (inches) is part of a fixture (a tank's side, a tub's rim).</summary>
        private const double NewFixtureInches = 24;

        public class Plan
        {
            public string Level, Source;
            public List<FixtureDrains.Stroke> Strokes = new List<FixtureDrains.Stroke>();
            public List<((double X, double Y) A, (double X, double Y) B)> Walls = new List<((double X, double Y) A, (double X, double Y) B)>();
            /// <summary>Every layer of the drawing with its number of lines (for the log and the scan: which layers to name in the rules).</summary>
            public Dictionary<string, int> Layers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Lines skipped because they are, or sit in a block on, a removal layer.</summary>
            public int Removed;
            /// <summary>Block inserts numbered so far (<see cref="FixtureDrains.Stroke.Group"/>).</summary>
            public int Groups;
            /// <summary>Where the DWG instance sits (its transform origin, feet).</summary>
            public XYZ Origin;
            /// <summary>Something to tell the user about this drawing (two copies at different positions).</summary>
            public string Warning;
            /// <summary>The DWG's placement: its own coordinates (feet) to the model's.</summary>
            public Transform Transform;
            /// <summary>The DWG's scale as imported (1 = its own units).</summary>
            public double ImportScale = 1;
            /// <summary>The DWG file (the link's path, else a file of that name near the model); null when not found.</summary>
            public string FilePath;
        }

        /// <summary>Layers whose sinks are kitchen sinks.</summary>
        public static readonly Regex KitchenLayers = new Regex(@"KITCHEN|KTCH|\bKIT\b", RegexOptions.IgnoreCase);

        /// <summary>A block wider or longer than this (inches) is no fixture (a counter, a bound floor plan): its lines are loose.</summary>
        private const double FixtureInches = 78;

        private class Layers
        {
            public Regex Fixture, Wall, New, Remove;
        }

        /// <summary>The layer rules as regexes; an empty rule matches nothing.</summary>
        public static (Regex Fixture, Regex Wall, Regex New, Regex Remove) Patterns(PlumbingRules rules)
        {
            Regex Make(string pattern, string fallback) =>
                string.IsNullOrWhiteSpace(pattern ?? fallback) ? null : new Regex(pattern ?? fallback, RegexOptions.IgnoreCase);
            return (Make(rules.DwgFixtureLayers, "FIXT"), Make(rules.DwgWallLayers, "WALL"), Make(rules.DwgNewLayers, null), Make(rules.DwgRemoveLayers, null));
        }

        /// <summary>
        /// One plan per level and DWG. The same DWG placed twice on a level (imported and linked, in two views) is read
        /// from every instance and the fullest read is kept: a view-specific instance gives only what its view shows
        /// and a flattened one loses its blocks' layers, so a plan can come back with a tenth of its lines.
        /// </summary>
        /// <param name="onlyLevels">Auto Run's chosen floors (Revit level names): the DWGs of the other levels are not read; null = all.</param>
        public static List<Plan> Read(Document doc, PlumbingRules rules, string onlyLevel = null, ICollection<string> onlyLevels = null)
        {
            var (fixture, wall, @new, remove) = Patterns(rules);
            var layers = new Layers { Fixture = fixture, Wall = wall, New = @new, Remove = remove };
            var best = new Dictionary<string, Plan>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                string name = doc.GetElement(ii.GetTypeId())?.Name ?? "";
                if (!name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) continue;
                View owner = ii.ViewSpecific ? doc.GetElement(ii.OwnerViewId) as View : null;
                var level = (owner as ViewPlan)?.GenLevel
                         ?? doc.GetElement(ii.get_Parameter(BuiltInParameter.IMPORT_BASE_LEVEL)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                if (level == null) continue;
                if (onlyLevel != null && level.Name != onlyLevel) continue;
                if (onlyLevels != null && !onlyLevels.Any(l => AutoPlacer.SameFloor(l, level.Name))) continue;

                var type = doc.GetElement(ii.GetTypeId());
                double scale = type?.get_Parameter(BuiltInParameter.IMPORT_SCALE)?.AsDouble() ?? 0;
                if (scale <= 0) scale = ii.get_Parameter(BuiltInParameter.IMPORT_INSTANCE_SCALE)?.AsDouble() ?? 0;
                var plan = new Plan
                {
                    Level = level.Name, Source = name, Transform = ii.GetTotalTransform(), ImportScale = scale > 0 ? scale : 1,
                    FilePath = FileOf(doc, type, name)
                };
                plan.Origin = plan.Transform?.Origin ?? XYZ.Zero;
                try
                {
                    // hidden layers and categories of the view are read all the same
                    var options = new Options { IncludeNonVisibleObjects = true };
                    if (owner != null) options.View = owner;
                    var geometry = ii.get_Geometry(options);
                    if (geometry != null) Walk(geometry, doc, plan, layers);
                }
                catch (Exception ex) { App.Log($"DWG fixtures: {name} on {level.Name} not read: {ex.Message}"); continue; }
                int lines = plan.Layers.Values.Sum();
                App.Log($"DWG fixtures: {name} on {level.Name} ({(owner != null ? "in view " + owner.Name : "model")}, id {ii.Id}): {plan.Strokes.Count} fixture stroke(s), " +
                        $"{plan.Walls.Count} wall segment(s), {lines} line(s)" + (plan.Removed > 0 ? $", {plan.Removed} on removal layers skipped" : "") + "; layers " +
                        string.Join(", ", plan.Layers.OrderByDescending(kv => kv.Value).Take(40).Select(kv => $"{kv.Key} ({kv.Value})")));

                string key = level.Name + "|" + name;
                if (best.TryGetValue(key, out var other))
                {
                    double apart = Math.Sqrt(Math.Pow(other.Origin.X - plan.Origin.X, 2) + Math.Pow(other.Origin.Y - plan.Origin.Y, 2));
                    if (apart > 1.0 / 24)
                    {
                        string warning = $"{name} is placed twice on {level.Name}, {apart * 12:0.#}\" apart: the fixtures follow the copy with more lines. " +
                                         "Delete the copy that is out of place so the sleeves line up with the plan you see.";
                        plan.Warning = other.Warning = warning;
                        App.Log("DWG fixtures: " + warning);
                    }
                }
                if (!best.TryGetValue(key, out var had)) { best[key] = plan; order.Add(key); }
                else if (lines > had.Layers.Values.Sum())
                {
                    App.Log($"DWG fixtures: {name} on {level.Name}: this instance is the fuller read ({lines} vs {had.Layers.Values.Sum()} lines), kept instead");
                    best[key] = plan;
                }
                else App.Log($"DWG fixtures: {name} on {level.Name}: another instance already read more ({had.Layers.Values.Sum()} vs {lines} lines), this one dropped");
            }
            return order.Select(k => best[k]).ToList();
        }

        /// <summary>The DWG file behind an import: the link's own path, else a file of that name next to the model (3 folders deep, and one up).</summary>
        private static string FileOf(Document doc, Element type, string name)
        {
            try
            {
                if (type != null && type.IsExternalFileReference())
                {
                    var p = type.GetExternalFileReference().GetAbsolutePath();
                    string path = p == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(p);
                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) return path;
                }
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(doc.PathName)) return null;
            var dir = System.IO.Path.GetDirectoryName(doc.PathName);
            return Alignment.ReferenceFiles.Locate(name, new[] { dir, System.IO.Path.GetDirectoryName(dir) });
        }

        private static bool Is(Regex r, string layer) => r != null && layer != null && r.IsMatch(layer);

        /// <param name="inBlock">Inside a block insert (a toilet, a tub, a vanity): on a new-work layer, nothing of it is a wall.</param>
        /// <param name="group">The block the strokes belong to (0 = loose).</param>
        private static void Walk(GeometryElement geometry, Document doc, Plan plan, Layers layers, string blockLayer = null, bool inBlock = false, int group = 0)
        {
            // AutoCAD: what a block draws on layer 0 takes the layer the block is inserted on (a toilet block on A-Fixt)
            string LayerOf(GeometryObject o)
            {
                string own = (doc.GetElement(o.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                return (own == "" || own == "0") && blockLayer != null ? blockLayer : own;
            }
            foreach (var obj in geometry)
            {
                // blocks (and the drawing itself): their geometry already in model coordinates
                if (obj is GeometryInstance gi)
                {
                    string insert = LayerOf(gi);
                    // a fixture the alteration removes: the whole block, whatever layers it draws on
                    if (Is(layers.Remove, insert)) { plan.Removed++; continue; }
                    var inner = gi.GetInstanceGeometry();
                    if (inner == null) continue;
                    // the drawing itself has no layer; a block insert has one (layer 0 included) and is one drawing (a
                    // toilet, a tub, a vanity: the tub and the vanity touching it stay apart), unless it is bigger than a
                    // fixture (a counter, a whole floor bound into one block): then its own lines are loose
                    bool block = insert != "";
                    int first = plan.Strokes.Count, id = block ? ++plan.Groups : 0;
                    Walk(inner, doc, plan, layers, insert != "" && insert != "0" ? insert : blockLayer, inBlock || block, id);
                    if (block)
                    {
                        var own = plan.Strokes.Skip(first).Where(s => s.Group == id).ToList();
                        if (own.Count > 0 && Math.Max(own.Max(s => s.X1) - own.Min(s => s.X0), own.Max(s => s.Y1) - own.Min(s => s.Y0)) > FixtureInches)
                            foreach (var s in own) s.Group = 0;
                    }
                    continue;
                }

                IList<XYZ> pts; bool curved = false, closedRound = false;
                if (obj is Curve curve)
                {
                    pts = curve.Tessellate();
                    curved = !(curve is Line);
                    closedRound = (curve is Arc || curve is Ellipse) &&
                                  (!curve.IsBound || curve.GetEndPoint(0).DistanceTo(curve.GetEndPoint(1)) < 1e-3);
                }
                else if (obj is PolyLine poly) pts = poly.GetCoordinates();
                else continue;
                if (pts == null || pts.Count < 2) continue;
                bool closed = closedRound || (pts.Count > 3 && pts[0].DistanceTo(pts[pts.Count - 1]) < 1.0 / 12) ||
                              (obj is Curve c2 && !c2.IsBound);
                // a straight line at 20-70 degrees, longer than a foot: a shower's X (not a door swing, not a wall)
                bool diagonal = obj is Line line && line.Length > 1.0 &&
                                Math.Abs(line.Direction.X) > 0.34 && Math.Abs(line.Direction.Y) > 0.34;

                string layer = LayerOf(obj);
                plan.Layers[layer] = plan.Layers.TryGetValue(layer, out int n) ? n + 1 : 1;
                if (Is(layers.Remove, layer)) { plan.Removed++; continue; }

                bool isWall = Is(layers.Wall, layer), isFixture = Is(layers.Fixture, layer);
                if (!isWall && !isFixture && Is(layers.New, layer))
                {
                    // new work on one layer: a straight line is a wall when long, part of a fixture when short or in a
                    // block (a tub's 5 ft rim); curves, polylines (a tank, a basin) and a shower's diagonals are fixtures
                    bool straight = obj is Line;
                    double inches = straight ? ((Line)obj).Length * 12 : 0;
                    isWall = straight && !diagonal && !inBlock && inches >= NewWallInches;
                    isFixture = !straight || diagonal || inBlock || inches <= NewFixtureInches;
                }

                if (isWall)
                    for (int i = 0; i + 1 < pts.Count; i++)
                        plan.Walls.Add(((pts[i].X * 12, pts[i].Y * 12), (pts[i + 1].X * 12, pts[i + 1].Y * 12)));
                if (isFixture)
                {
                    double x0 = pts.Min(p => p.X) * 12, y0 = pts.Min(p => p.Y) * 12, x1 = pts.Max(p => p.X) * 12, y1 = pts.Max(p => p.Y) * 12;
                    double w = x1 - x0, l = y1 - y0;
                    plan.Strokes.Add(new FixtureDrains.Stroke
                    {
                        X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Curved = curved, Closed = closed, Diagonal = diagonal,
                        Circle = closedRound && Math.Abs(w - l) <= 0.15 * Math.Max(w, l), Group = group, Kitchen = KitchenLayers.IsMatch(layer)
                    });
                }
            }
        }
    }
}
