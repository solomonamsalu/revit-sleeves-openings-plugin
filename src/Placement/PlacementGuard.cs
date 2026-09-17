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
    /// Walls use exact geometry; columns/beams use bounding boxes.
    /// </summary>
    public class PlacementGuard
    {
        private readonly Document _doc;
        private readonly RuleSet _rules;
        private readonly Level _level;
        private readonly List<Element> _columns, _beams;
        private readonly WallGeometry _walls;

        public PlacementGuard(Document doc, RuleSet rules, Level level, WallGeometry walls = null)
        {
            _doc = doc; _rules = rules; _level = level;
            _walls = walls ?? new WallGeometry(doc, level);
            _columns = OnLevel(Collect(BuiltInCategory.OST_StructuralColumns).Concat(Collect(BuiltInCategory.OST_Columns)));
            _beams = OnLevel(Collect(BuiltInCategory.OST_StructuralFraming));
        }

        private IEnumerable<Element> Collect(BuiltInCategory cat) =>
            new FilteredElementCollector(_doc).OfCategory(cat).WhereElementIsNotElementType();

        private List<Element> OnLevel(IEnumerable<Element> elems)
        {
            double z = _level.Elevation;
            return elems.Where(e =>
            {
                var bb = e.get_BoundingBox(null);
                return bb != null && bb.Min.Z - 1 <= z && bb.Max.Z + 1 >= z;
            }).ToList();
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
                double d = DistanceXY(pt, col.get_BoundingBox(null)) - r;
                if (d < minCol)
                    warnings.Add($"{Units.FormatInches(Math.Max(0, Units.FeetToInches(d)))} from column {col.Id} (rule 26: keep {Units.FormatInches(c.MinFromColumn)})");
            }

            // Rule 27: shear walls (exact)
            foreach (var w in _walls.ShearHits(pt, r))
                warnings.Add($"!Inside concrete shear wall {w.Wall.Id} (rule 27: never)");

            // Rule 28: beams
            foreach (var b in _beams)
                if (Overlaps(pt, hw, hl, b.get_BoundingBox(null)))
                    warnings.Add($"!Inside structural beam {b.Id} (rule 28: never)");

            // General rule: never at the edge of a wall
            foreach (var w in _walls.Straddled(pt, r))
                if (!w.IsShear)
                    warnings.Add($"Sits on the edge of wall {w.Wall.Id} — no room for sheetrock studs (general rule)");

            // Standpipe: 5½" from pipe centre to any wall face
            if (system == SystemKind.Standpipe)
            {
                double minWall = Units.InchesToFeet(_rules.Systems.Standpipe.MinCenterToWall);
                var near = _walls.Nearest(pt, out double face);
                if (near != null && face >= 0 && face < minWall)
                    warnings.Add($"Standpipe centre {Units.FormatInches(Units.FeetToInches(face))} from wall {near.Wall.Id} (min {Units.FormatInches(_rules.Systems.Standpipe.MinCenterToWall)})");
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
