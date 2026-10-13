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
            /// <summary>Top of the wall (host elevation): a wall of the floor below stops at this level's slab.</summary>
            public double Top;

            /// <summary>"123" or "123 (24 Skillman Str)" for a linked wall.</summary>
            public string Label => Source != null ? Source.Describe(Wall.Id) : Wall.Id.ToString();
        }

        private readonly Document _doc;
        private readonly double _z;
        public List<WallInfo> Walls { get; }

        /// <summary>Walls crossing the level, from the host and (per rules) every linked model.</summary>
        public WallGeometry(Document doc, Level level, LinkedModels links = null)
        {
            _doc = doc;
            links = links ?? new LinkedModels(doc, App.Rules(doc));
            double z = _z = level.ProjectElevation;
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
                        Centerline = Middle(w, src.Transform.IsIdentity ? lc.Curve : lc.Curve.CreateTransformed(src.Transform), src.Transform),
                        HalfWidth = w.Width / 2,
                        IsShear = w.StructuralUsage == StructuralWallUsage.Shear || w.StructuralUsage == StructuralWallUsage.Combined,
                        IsFoundation = w.WallType?.Function == WallFunction.Foundation || w.WallType?.Function == WallFunction.Retaining,
                        Source = src,
                        Top = bb.Max.Z
                    });
                }
            }
        }

        /// <summary>
        /// The wall's centre line: its location line moved to the middle of its thickness when the wall is drawn by a finish
        /// or core face (Revit's Location Line), host coordinates.
        /// </summary>
        private static Curve Middle(Wall w, Curve curve, Transform transform)
        {
            try
            {
                var key = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0;
                if (key == (int)WallLocationLine.WallCenterline) return curve;
                double half = w.Width / 2, off;
                var cs = w.WallType?.GetCompoundStructure();
                if (key == (int)WallLocationLine.FinishFaceExterior) off = half;
                else if (key == (int)WallLocationLine.FinishFaceInterior) off = -half;
                else if (cs != null)
                {
                    // the structure's own offsets, signed so the exterior finish face is +half
                    double ext = cs.GetOffsetForLocationLine(WallLocationLine.FinishFaceExterior);
                    off = cs.GetOffsetForLocationLine((WallLocationLine)key) * (ext < 0 ? -1 : 1);
                }
                else return curve;
                if (Math.Abs(off) < 1e-6) return curve;
                var toExterior = transform.OfVector(w.Orientation);
                return curve.CreateTransformed(Transform.CreateTranslation(new XYZ(-toExterior.X * off, -toExterior.Y * off, 0)));
            }
            catch { return curve; }
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

        /// <summary>
        /// The band a sleeve goes in at a wet wall (Standards Manual p17, the office's "dead zone"): every sleeve of the row
        /// sits on one line through its middle, never on a wall face. Two parallel walls up to maxGap apart (a double wall,
        /// a chase) -> the gap between them, centred between the two walls (the two walls as one band when they touch);
        /// else the thickness of the one wall. Partition walls only (never concrete, rule 27), straight, standing on this
        /// level, and only where the point is along both walls (a gap stops where one of them ends: a closet).
        /// </summary>
        public class Zone
        {
            public XYZ Origin;            // on the band's centre line, plan
            public XYZ Normal;            // across the band, unit
            public double Half;           // half the band's width
            public double Gap = -1;       // the gap between the two walls; -1 = one wall
            public List<WallInfo> Walls = new List<WallInfo>();
            public bool Pair => Walls.Count > 1;
            /// <summary>Signed distance from the band's centre line.</summary>
            public double Offset(XYZ p) => (p.X - Origin.X) * Normal.X + (p.Y - Origin.Y) * Normal.Y;
            /// <summary>The point moved straight across onto the centre line (its place along the wall kept).</summary>
            public XYZ Centre(XYZ p) { double o = Offset(p); return new XYZ(p.X - Normal.X * o, p.Y - Normal.Y * o, 0); }
            public bool Contains(XYZ p, double tol = 1.0 / 12) => Math.Abs(Offset(p)) <= Half + tol;
            /// <summary>What the band is when it comes from DWG wall lines (no Revit walls).</summary>
            public string Where;
            public string Describe() => Where ?? (Pair
                ? (Gap > 0 ? $"the {Math.Round(Gap * 12, 1):0.#}\" gap between walls {Walls[0].Label} and {Walls[1].Label}" : $"walls {Walls[0].Label} and {Walls[1].Label} (touching)")
                : $"wall {Walls[0].Label}");
        }

        /// <summary>
        /// The dead zone a sleeve of radius r at pt touches (it overlaps the band, or it is inside it), or null. A double wall
        /// is preferred to one of its walls; then the zone the sleeve moves least to reach.
        /// </summary>
        public Zone DeadZone(XYZ pt, double r, double maxGap)
        {
            const double tol = 1.0 / 12, parallel = 0.05;     // ~3 degrees
            var lines = Walls.Where(w => !w.IsShear && !w.IsFoundation && w.Centerline is Line && w.Top > _z + 1).ToList();
            Zone pair = null, single = null;
            double pairMove = double.MaxValue, singleMove = double.MaxValue;
            foreach (var w1 in lines)
            {
                var a = Frame(w1, out XYZ dir, out XYZ n, out double len);
                double t = (pt.X - a.X) * dir.X + (pt.Y - a.Y) * dir.Y;
                if (t < 0 || t > len) continue;
                double s = (pt.X - a.X) * n.X + (pt.Y - a.Y) * n.Y;
                double h1 = w1.HalfWidth;
                if (Math.Abs(s) > h1 + r + tol + maxGap + 2) continue;    // too far for any band of this wall

                if (Math.Abs(s) <= h1 + r + tol && Math.Abs(s) < singleMove)
                {
                    single = new Zone { Origin = a, Normal = n, Half = h1, Walls = new List<WallInfo> { w1 } };
                    singleMove = Math.Abs(s);
                }

                foreach (var w2 in lines)
                {
                    if (w2 == w1) continue;
                    var b = Frame(w2, out XYZ dir2, out _, out double len2);
                    if (Math.Abs(dir.X * dir2.Y - dir.Y * dir2.X) > parallel) continue;
                    double t2 = (pt.X - b.X) * dir2.X + (pt.Y - b.Y) * dir2.Y;
                    if (t2 < 0 || t2 > len2) continue;
                    double dc = (b.X - a.X) * n.X + (b.Y - a.Y) * n.Y;      // w2's centre line across w1's frame
                    int sign = dc >= 0 ? 1 : -1;
                    double d = Math.Abs(dc), h2 = w2.HalfWidth, gap = d - h1 - h2;
                    if (gap < -tol || gap > maxGap + 1e-6) continue;
                    double ss = s * sign;                                    // across, w2 on the + side
                    if (ss < -h1 - r - tol || ss > d + h2 + r + tol) continue;
                    // the gap between them, centred; walls touching (no gap): the two as one band
                    bool open = gap >= tol;
                    double mid = open ? (h1 + d - h2) / 2 : (d + h2 - h1) / 2, half = open ? gap / 2 : (d + h1 + h2) / 2;
                    double move = Math.Abs(ss - mid);
                    if (move >= pairMove) continue;
                    pair = new Zone
                    {
                        Origin = new XYZ(a.X + n.X * sign * mid, a.Y + n.Y * sign * mid, 0), Normal = n, Half = half,
                        Gap = Math.Max(0, gap), Walls = new List<WallInfo> { w1, w2 }
                    };
                    pairMove = move;
                }
            }
            return pair ?? single;
        }

        /// <summary>A straight wall's start (plan), direction, normal and length.</summary>
        private static XYZ Frame(WallInfo w, out XYZ dir, out XYZ normal, out double len)
        {
            var p0 = w.Centerline.GetEndPoint(0); var p1 = w.Centerline.GetEndPoint(1);
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            len = Math.Sqrt(dx * dx + dy * dy);
            dir = len < 1e-9 ? XYZ.BasisX : new XYZ(dx / len, dy / len, 0);
            normal = new XYZ(-dir.Y, dir.X, 0);
            return new XYZ(p0.X, p0.Y, 0);
        }

        /// <summary>Room containing the point on this level, or null.</summary>
        public Room RoomAt(XYZ pt, Level level)
        {
            try { return _doc.GetRoomAtPoint(new XYZ(pt.X, pt.Y, level.ProjectElevation + 1.0)); }
            catch { return null; }
        }
    }
}
