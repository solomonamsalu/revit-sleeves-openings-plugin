using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;

namespace SleevesOpenings.Placement
{
    /// <summary>
    /// Exact wall relationships from the wall's location curve and thickness (works for angled and curved walls,
    /// unlike bounding boxes). All distances in feet unless the name says inches.
    /// </summary>
    public class WallGeometry
    {
        public class WallInfo
        {
            public Wall Wall;
            public Curve Centerline;        // in host coordinates (linked walls are transformed)
            public double HalfWidth;
            public bool IsShear;
            public bool IsFoundation;       // foundation / retaining wall (structural usage or wall function)
            public LinkedModels.Source Source;

            /// <summary>"123" or "123 (24 Skillman Str)" for a linked wall.</summary>
            public string Label => Source != null ? Source.Describe(Wall.Id) : Wall.Id.ToString();
        }

        private readonly Document _doc;
        public List<WallInfo> Walls { get; }

        /// <summary>Walls crossing the level, from the host and (per rules) every linked model.</summary>
        public WallGeometry(Document doc, Level level, LinkedModels links = null)
        {
            _doc = doc;
            links = links ?? new LinkedModels(doc, App.Rules(doc));
            double z = level.ProjectElevation;
            Walls = new List<WallInfo>();
            foreach (var src in links.Sources)
            {
                foreach (var w in new FilteredElementCollector(src.Doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (!(w.Location is LocationCurve lc)) continue;
                    var bb = w.get_BoundingBox(null);
                    if (bb == null) continue;
                    bb = LinkedModels.TransformBox(bb, src.Transform);
                    if (bb.Min.Z - 1 > z || bb.Max.Z + 1 < z) continue;
                    Walls.Add(new WallInfo
                    {
                        Wall = w,
                        Centerline = src.Transform.IsIdentity ? lc.Curve : lc.Curve.CreateTransformed(src.Transform),
                        HalfWidth = w.Width / 2,
                        IsShear = w.StructuralUsage == StructuralWallUsage.Shear || w.StructuralUsage == StructuralWallUsage.Combined,
                        IsFoundation = w.WallType?.Function == WallFunction.Foundation || w.WallType?.Function == WallFunction.Retaining,
                        Source = src
                    });
                }
            }
        }

        /// <summary>Distance from a point to the wall centerline, measured in plan.</summary>
        public static double CenterDistance(WallInfo w, XYZ pt)
        {
            var flat = new XYZ(pt.X, pt.Y, w.Centerline.GetEndPoint(0).Z);
            return w.Centerline.Distance(flat);
        }

        /// <summary>Distance from the point to the nearest wall face (negative = inside the wall).</summary>
        public static double FaceDistance(WallInfo w, XYZ pt) => CenterDistance(w, pt) - w.HalfWidth;

        /// <summary>Nearest wall by face distance, or null.</summary>
        public WallInfo Nearest(XYZ pt, out double faceDistance)
        {
            WallInfo best = null; faceDistance = double.MaxValue;
            foreach (var w in Walls)
            {
                double d = FaceDistance(w, pt);
                if (d < faceDistance) { faceDistance = d; best = w; }
            }
            return best;
        }

        /// <summary>
        /// Classifies an opening footprint (circle of radius r, feet) against a wall:
        /// Inside = fully within the wall thickness, Edge = straddles a wall face, Clear = outside.
        /// </summary>
        public enum Relation { Clear, Edge, Inside }

        public static Relation Relate(WallInfo w, XYZ pt, double r)
        {
            double d = CenterDistance(w, pt);
            if (d + r <= w.HalfWidth + 1e-6) return Relation.Inside;
            if (d - r < w.HalfWidth) return Relation.Edge;
            return Relation.Clear;
        }

        /// <summary>Walls whose face the footprint straddles (rule: never at the edge of a wall).</summary>
        public IEnumerable<WallInfo> Straddled(XYZ pt, double r) =>
            Walls.Where(w => Relate(w, pt, r) == Relation.Edge);

        /// <summary>Concrete walls (shear or foundation) the footprint touches at all.</summary>
        public IEnumerable<WallInfo> ShearHits(XYZ pt, double r) =>
            Walls.Where(w => (w.IsShear || w.IsFoundation) && Relate(w, pt, r) != Relation.Clear);

        /// <summary>Room containing the point on this level, or null.</summary>
        public Room RoomAt(XYZ pt, Level level)
        {
            try { return _doc.GetRoomAtPoint(new XYZ(pt.X, pt.Y, level.ProjectElevation + 1.0)); }
            catch { return null; }
        }
    }
}
