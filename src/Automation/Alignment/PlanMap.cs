using System;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>
    /// Plan transform from a reference drawing's coordinates (drawing units) to Revit model coordinates (feet):
    /// scale, then rotation, then shift. Kept free of the Revit API so the alignment can be tested outside Revit.
    /// </summary>
    public class PlanMap
    {
        public double Scale = 1.0 / 12, Cos = 1, Sin, Ox, Oy;

        /// <summary>Drawing placed at Revit's origin without rotation (office standard: CAD files share Revit's 0,0).</summary>
        public static PlanMap AtOrigin(double unitsToFeet) => new PlanMap { Scale = unitsToFeet };

        public (double X, double Y) Apply(double x, double y)
        {
            double sx = x * Scale, sy = y * Scale;
            return (Ox + sx * Cos - sy * Sin, Oy + sx * Sin + sy * Cos);
        }

        /// <summary>Revit point (feet) -> drawing point: the inverse of <see cref="Apply"/>.</summary>
        public (double X, double Y) Invert(double x, double y)
        {
            double dx = x - Ox, dy = y - Oy;
            return ((dx * Cos + dy * Sin) / Scale, (-dx * Sin + dy * Cos) / Scale);
        }

        /// <summary>Feet per drawing unit for a DWG $INSUNITS name; null when the units are unknown.</summary>
        public static double? FeetPerUnit(string insUnits)
        {
            switch ((insUnits ?? "").ToLowerInvariant())
            {
                case "inches": return 1.0 / 12;
                case "feet": return 1.0;
                case "millimeters": return 1.0 / 304.8;
                case "centimeters": return 1.0 / 30.48;
                case "meters": return 1.0 / 0.3048;
                default: return null;
            }
        }

        public override string ToString() =>
            $"scale {Scale:0.#####} ft/unit, rotation {Math.Atan2(Sin, Cos) * 180 / Math.PI:0.##}°, origin ({Ox:0.###}, {Oy:0.###}) ft";
    }
}
