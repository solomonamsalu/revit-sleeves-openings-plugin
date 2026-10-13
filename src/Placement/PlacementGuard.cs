using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Placement
{
    /// <summary>
    /// Click-time checks from the manual: 1' from columns (26), never in shear walls (27) or beams (28),
    /// never at the edge of a wall (general rule), not mid-room (20-21), standpipe 5½" to wall.
    /// Walls use exact geometry; columns/beams use bounding boxes. Structure is read from the host and from
    /// linked Revit models (clearances.includeLinkedModels) — on most projects the structural model is a link.
    /// </summary>
    public class PlacementGuard
    {
        private readonly RuleSet _rules;
        private readonly Level _level;
        private readonly List<Structural> _columns, _beams;
        private readonly WallGeometry _walls;

        /// <summary>A column or beam from the host or a linked model, with its box in host coordinates.</summary>
        private class Structural
        {
            public BoundingBoxXYZ Box;
            public string Label;
        }

        public PlacementGuard(Document doc, RuleSet rules, Level level, WallGeometry walls = null)
        {
            _rules = rules; _level = level;
            var links = new LinkedModels(doc, rules);
            _walls = walls ?? new WallGeometry(doc, level, links);
            _columns = OnLevel(links.Collect(BuiltInCategory.OST_StructuralColumns).Concat(links.Collect(BuiltInCategory.OST_Columns)));
            _beams = OnLevel(links.Collect(BuiltInCategory.OST_StructuralFraming));
            try { _slabs = Slabs(links); }
            catch (Exception ex) { App.Log("PlacementGuard: slab outlines not read: " + ex.Message); }
        }

        /// <summary>The outer edges of the level's slabs (floors from the host and the links), host plan; empty = no slab check.</summary>
        private readonly List<List<XYZ>> _slabs = new List<List<XYZ>>();

        /// <summary>
        /// Each floor on this level (its box spans the level, like the columns): the outer loop of every upward face, host
        /// plan. A slab's own openings (shafts) are not edges: a sleeve in one is still over the slab.
        /// </summary>
        private List<List<XYZ>> Slabs(LinkedModels links)
        {
            var loops = new List<List<XYZ>>();
            double z = _level.ProjectElevation;
            foreach (var (e, box, src) in links.Collect(BuiltInCategory.OST_Floors))
            {
                if (!(e is Floor) || box.Min.Z - 1 > z || box.Max.Z + 1 < z) continue;
                foreach (var solid in Solids(e.get_Geometry(new Options())))
                    foreach (Face f in solid.Faces)
                    {
                        if (!(f is PlanarFace pf) || src.Transform.OfVector(pf.FaceNormal).Z < 0.9) continue;
                        var outer = pf.GetEdgesAsCurveLoops()
                                      .Select(l => l.SelectMany(c => c.Tessellate().Skip(1)).Select(p => src.Transform.OfPoint(p)).ToList())
                                      .Where(l => l.Count >= 3).OrderByDescending(l => Math.Abs(Area(l))).FirstOrDefault();
                        if (outer != null) loops.Add(outer);
                    }
            }
            return loops;
        }

        private static IEnumerable<Solid> Solids(GeometryElement geo)
        {
            if (geo == null) yield break;
            foreach (var g in geo)
            {
                if (g is Solid s && s.Volume > 0) yield return s;
                else if (g is GeometryInstance gi)
                    foreach (var inner in Solids(gi.GetInstanceGeometry())) yield return inner;
            }
        }

        private static double Area(List<XYZ> l)
        {
            double a = 0;
            for (int i = 0; i < l.Count; i++) { var p = l[i]; var q = l[(i + 1) % l.Count]; a += p.X * q.Y - q.X * p.Y; }
            return a / 2;
        }

        private static bool Inside(List<XYZ> l, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                if ((l[i].Y > y) != (l[j].Y > y) && x < (l[j].X - l[i].X) * (y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X) inside = !inside;
            return inside;
        }

        private bool OnSlab(double x, double y) => _slabs.Any(l => Inside(l, x, y));

        /// <summary>
        /// The general rule that a sleeve goes through the slab: its centre off every slab of the level = hard (the plans put
        /// it beyond the building: moved in, else not placed); part of it over the edge = to clear (moved in like a column).
        /// Null when it is on the slab or the level has no slab to check against.
        /// </summary>
        private string SlabEdge(XYZ pt, double hw, double hl)
        {
            if (_slabs.Count == 0) return null;
            if (!OnSlab(pt.X, pt.Y)) return "!Outside the slab edge: the plans put it beyond the building (general rule: through the slab)";
            for (int k = 0; k < 16; k++)
            {
                double a = k * Math.PI / 8;
                if (!OnSlab(pt.X + hw * Math.Cos(a), pt.Y + hl * Math.Sin(a)))
                    return "Over the slab edge: part of it is beyond the slab (general rule: through the slab)";
            }
            return null;
        }

        private List<Structural> OnLevel(IEnumerable<(Element Element, BoundingBoxXYZ Box, LinkedModels.Source Source)> elems)
        {
            double z = _level.ProjectElevation;          // model Z (Elevation follows the level type's base point)
            return elems.Where(e => e.Box.Min.Z - 1 <= z && e.Box.Max.Z + 1 >= z)
                        .Select(e => new Structural { Box = e.Box, Label = e.Source.Describe(e.Element.Id) })
                        .ToList();
        }

        /// <summary>Warnings for a proposed point and half-sizes (inches). Prefix "!" marks hard errors (structure).</summary>
        public List<string> Check(XYZ pt, double halfWidthIn, double halfLengthIn, SystemKind? system = null)
        {
            var warnings = new List<string>();
            double hw = Units.InchesToFeet(halfWidthIn), hl = Units.InchesToFeet(halfLengthIn);
            double r = Math.Max(hw, hl);
            var c = _rules.Clearances;

            // Rule 26: columns
            double minCol = Units.InchesToFeet(c.MinFromColumn);
            foreach (var col in _columns)
            {
                double d = DistanceXY(pt, col.Box) - r;
                if (d < minCol)
                    warnings.Add($"{Units.FormatInches(Math.Max(0, Units.FeetToInches(d)))} from column {col.Label} (rule 26: keep {Units.FormatInches(c.MinFromColumn)})");
            }

            // Rule 27: shear walls (exact); foundation / retaining walls are concrete too (engineer's comment: out of the FND wall)
            foreach (var w in _walls.ShearHits(pt, r))
                warnings.Add(w.IsShear ? $"!Inside concrete shear wall {w.Label} (rule 27: never)"
                                       : $"!Inside foundation wall {w.Label} (rule 27: never)");

            // Rule 28: beams
            foreach (var b in _beams)
                if (Overlaps(pt, hw, hl, b.Box))
                    warnings.Add($"!Inside structural beam {b.Label} (rule 28: never)");

            // General rule: through the slab, never beyond its edge
            var edge = SlabEdge(pt, hw, hl);
            if (edge != null) warnings.Add(edge);

            // General rule: never at the edge of a wall; a round sleeve in a wet wall's dead zone is meant to be there (manual p17)
            var zone = DeadZone(pt, Math.Abs(hw - hl) < 1e-6 ? r : -1);
            foreach (var w in _walls.Straddled(pt, r))
                if (!w.IsShear && !w.IsFoundation && !(zone != null && zone.Contains(pt) && zone.Walls.Contains(w)))
                    warnings.Add($"Sits on the edge of wall {w.Label} — no room for sheetrock studs (general rule)");

            // Standpipe: 5½" from pipe centre to any wall face
            if (system == SystemKind.Standpipe)
            {
                double minWall = Units.InchesToFeet(_rules.Systems.Standpipe.MinCenterToWall);
                var near = _walls.Nearest(pt, out double face);
                if (near != null && face >= 0 && face < minWall)
                    warnings.Add($"Standpipe centre {Units.FormatInches(Units.FeetToInches(face))} from wall {near.Label} (min {Units.FormatInches(_rules.Systems.Standpipe.MinCenterToWall)})");
            }

            // Rules 20-21: avoid the middle of rooms
            double midRoom = Units.InchesToFeet(c.MidRoomDistance);
            if (midRoom > 0 && _walls.Walls.Count > 0)
            {
                var room = _walls.RoomAt(pt, _level);
                var near = _walls.Nearest(pt, out double face);
                if (room != null && near != null && face > midRoom)
                    warnings.Add($"In the middle of '{room.Name}', {Units.FormatInches(Units.FeetToInches(face))} from the nearest wall (rules 20-21: prefer walls, closets, shafts)");
            }

            return warnings;
        }

        /// <summary>The wet wall's dead zone a round sleeve of radius r (feet) touches, or null (rule off, r &lt; 0).</summary>
        public WallGeometry.Zone DeadZone(XYZ pt, double r)
        {
            var d = _rules.Automation?.Decisions;
            if (r < 0 || d == null || !d.DeadZone) return null;
            return _walls.DeadZone(pt, r, Units.InchesToFeet(d.DeadZoneMaxGap));
        }

        public static bool IsHard(string warning) => warning.StartsWith("!");
        public static string Clean(string warning) => warning.TrimStart('!');

        private static double DistanceXY(XYZ p, BoundingBoxXYZ bb)
        {
            if (bb == null) return double.MaxValue;
            double dx = Math.Max(Math.Max(bb.Min.X - p.X, 0), p.X - bb.Max.X);
            double dy = Math.Max(Math.Max(bb.Min.Y - p.Y, 0), p.Y - bb.Max.Y);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Overlaps(XYZ p, double hw, double hl, BoundingBoxXYZ bb) =>
            bb != null && p.X + hw > bb.Min.X && p.X - hw < bb.Max.X && p.Y + hl > bb.Min.Y && p.Y - hl < bb.Max.Y;
    }
}
