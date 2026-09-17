using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Placement
{
    /// <summary>
    /// Manual rules 26-28: keep 1' from columns, never in shear walls, never in beams.
    /// Bounding-box based (fast, approximate) — the full auditor (F8) does exact geometry.
    /// </summary>
    public class PlacementGuard
    {
        private readonly Document _doc;
        private readonly RuleSet _rules;
        private readonly Level _level;
        private readonly List<Element> _columns, _beams, _shearWalls;

        public PlacementGuard(Document doc, RuleSet rules, Level level)
        {
            _doc = doc; _rules = rules; _level = level;
            _columns = OnLevel(Collect(BuiltInCategory.OST_StructuralColumns).Concat(Collect(BuiltInCategory.OST_Columns)));
            _beams = OnLevel(Collect(BuiltInCategory.OST_StructuralFraming));
            _shearWalls = OnLevel(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.StructuralUsage == StructuralWallUsage.Shear || w.StructuralUsage == StructuralWallUsage.Combined)
                .Cast<Element>());
        }

        private IEnumerable<Element> Collect(BuiltInCategory cat) =>
            new FilteredElementCollector(_doc).OfCategory(cat).WhereElementIsNotElementType();

        /// <summary>Elements whose bounding box spans the level elevation (± 1').</summary>
        private List<Element> OnLevel(IEnumerable<Element> elems)
        {
            double z = _level.Elevation;
            return elems.Where(e =>
            {
                var bb = e.get_BoundingBox(null);
                return bb != null && bb.Min.Z - 1 <= z && bb.Max.Z + 1 >= z;
            }).ToList();
        }

        /// <summary>Returns human-readable warnings for a proposed point and half-size (inches) footprint.</summary>
        public List<string> Check(XYZ pt, double halfWidthIn, double halfLengthIn)
        {
            var warnings = new List<string>();
            double hw = Units.InchesToFeet(halfWidthIn), hl = Units.InchesToFeet(halfLengthIn);
            double minCol = Units.InchesToFeet(_rules.Clearances.MinFromColumn);

            foreach (var c in _columns)
            {
                double d = DistanceXY(pt, c.get_BoundingBox(null)) - Math.Max(hw, hl);
                if (d < minCol)
                    warnings.Add($"{Units.FormatInches(Math.Max(0, Units.FeetToInches(d)))} from column {c.Id} (rule: {Units.FormatInches(_rules.Clearances.MinFromColumn)})");
            }
            foreach (var w in _shearWalls)
                if (Overlaps(pt, hw, hl, w.get_BoundingBox(null)))
                    warnings.Add($"Inside concrete shear wall {w.Id} (rule 27: never)");
            foreach (var b in _beams)
                if (Overlaps(pt, hw, hl, b.get_BoundingBox(null)))
                    warnings.Add($"Inside structural beam {b.Id} (rule 28: never)");

            return warnings;
        }

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
