using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Electrical
{
    /// <summary>
    /// The architect's walls, doors and windows as Revit draws the DWGs imported or linked on a level, in model feet.
    /// Read from Revit rather than the file because an AutoCAD Architecture drawing keeps its walls and doors as AEC
    /// objects (AEC_WALL, AEC_DOOR), which the DWG library cannot take apart and Revit shows as plain lines and arcs:
    /// on 24 Skillman the file gave 0 wall lines on most floors where Revit has thousands. Call on Revit's thread.
    /// </summary>
    public static class LinkedPlanReader
    {
        public class Plan
        {
            public List<string> Sources = new List<string>();
            /// <summary>DWGs on the level that are another floor's plan, not read.</summary>
            public List<string> Skipped = new List<string>();
            public List<(double X0, double Y0, double X1, double Y1)> Walls = new List<(double X0, double Y0, double X1, double Y1)>();
            public List<(double X0, double Y0, double X1, double Y1)> DoorLines = new List<(double X0, double Y0, double X1, double Y1)>();
            /// <summary>Window lines: no riser goes there, and a room does not end at a window's gap in its wall.</summary>
            public List<(double X0, double Y0, double X1, double Y1)> Windows = new List<(double X0, double Y0, double X1, double Y1)>();
            /// <summary>Shaft and chase outlines (dwgProfile.architecturalShaftLayers): inside a closed one is where a riser goes.</summary>
            public List<(double X0, double Y0, double X1, double Y1)> Shafts = new List<(double X0, double Y0, double X1, double Y1)>();
            /// <summary>Door swings: hinge (arc centre) and the arc's two ends.</summary>
            public List<((double X, double Y) Hinge, (double X, double Y) A, (double X, double Y) B)> Swings = new List<((double X, double Y) Hinge, (double X, double Y) A, (double X, double Y) B)>();
            public int Count => Walls.Count + DoorLines.Count + Windows.Count + Shafts.Count;
        }

        /// <summary>Window layers (A-GLAZ, A-WINDOW): part of the wall a room is closed by.</summary>
        private static readonly Regex WindowLayers = new Regex(@"GLAZ|WIND", RegexOptions.IgnoreCase);

        /// <summary>
        /// Every DWG on the level, each read once: the same drawing placed twice (imported and linked, in two views)
        /// gives the instance with the most lines, since a view-specific one shows only what its view does.
        /// </summary>
        /// <param name="floor">The floor (FloorKey) the level was matched to; a DWG whose name is another floor's plan
        /// (the typical "03.3 TH TO 7 TH FLOOR.dwg" left on the 8th floor too) is not read. Null = every DWG on the level.</param>
        public static Plan Read(Document doc, Level level, DwgProfile profile, string floor = null)
        {
            var wall = Rx(profile?.ArchitecturalWallLayers);
            var door = Rx(profile?.ArchitecturalDoorLayers);
            var shaft = Rx(profile?.ArchitecturalShaftLayers);
            var plan = new Plan();
            if (wall == null && door == null && shaft == null) return plan;

            var best = new Dictionary<string, Plan>(StringComparer.OrdinalIgnoreCase);
            foreach (var ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                string name = doc.GetElement(ii.GetTypeId())?.Name ?? "";
                if (!name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) continue;
                View owner = ii.ViewSpecific ? doc.GetElement(ii.OwnerViewId) as View : null;
                var on = (owner as ViewPlan)?.GenLevel
                      ?? doc.GetElement(ii.get_Parameter(BuiltInParameter.IMPORT_BASE_LEVEL)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                if (on == null || on.Id != level.Id) continue;
                if (floor != null)
                {
                    var named = FloorKey.FileFloors(name);
                    if (named.Count > 0 && !named.Contains(floor)) { if (!plan.Skipped.Contains(name)) plan.Skipped.Add(name); continue; }
                }

                var one = new Plan();
                try
                {
                    var options = new Options { IncludeNonVisibleObjects = true };
                    if (owner != null) options.View = owner;
                    var geometry = ii.get_Geometry(options);
                    if (geometry != null) Walk(geometry, doc, one, wall, door, shaft);
                }
                catch (Exception ex) { App.Log($"ELEC| {name} on {level.Name} not read: {ex.Message}"); continue; }
                if (one.Count == 0) continue;
                if (!best.TryGetValue(name, out var had) || one.Count > had.Count) best[name] = one;
            }
            foreach (var kv in best)
            {
                plan.Sources.Add(kv.Key);
                plan.Walls.AddRange(kv.Value.Walls);
                plan.DoorLines.AddRange(kv.Value.DoorLines);
                plan.Windows.AddRange(kv.Value.Windows);
                plan.Shafts.AddRange(kv.Value.Shafts);
                plan.Swings.AddRange(kv.Value.Swings);
            }
            return plan;
        }

        private static void Walk(GeometryElement geometry, Document doc, Plan plan, Regex wall, Regex door, Regex shaft, string blockLayer = null)
        {
            // AutoCAD: what a block draws on layer 0 takes the layer the block is inserted on (a door block on A-DOOR)
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
                    if (inner != null) Walk(inner, doc, plan, wall, door, shaft, insert != "" && insert != "0" ? insert : blockLayer);
                    continue;
                }
                string layer = LayerOf(obj);
                bool isWall = wall != null && wall.IsMatch(layer), isDoor = !isWall && door != null && door.IsMatch(layer);
                bool isShaft = !isWall && !isDoor && shaft != null && shaft.IsMatch(layer);
                bool isWindow = !isWall && !isDoor && !isShaft && WindowLayers.IsMatch(layer);
                if (!isWall && !isDoor && !isWindow && !isShaft) continue;

                IList<XYZ> pts;
                if (obj is Curve curve)
                {
                    pts = curve.Tessellate();
                    if (isDoor && curve is Arc arc && arc.IsBound)
                        plan.Swings.Add(((arc.Center.X, arc.Center.Y), (pts[0].X, pts[0].Y), (pts[pts.Count - 1].X, pts[pts.Count - 1].Y)));
                }
                else if (obj is PolyLine poly) pts = poly.GetCoordinates();
                else continue;
                var into = isWall ? plan.Walls : isDoor ? plan.DoorLines : isShaft ? plan.Shafts : plan.Windows;
                for (int i = 1; i < pts.Count; i++)
                    if (pts[i - 1].DistanceTo(pts[i]) > 1e-6) into.Add((pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y));
            }
        }

        private static Regex Rx(string pattern) =>
            string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase);
    }
}
