using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A duct cross-section drawn in plan: a rectangle (or its diagonal), axis-aligned in drawing units.</summary>
    public class DuctOutline
    {
        public double X, Y;              // centre
        public double SizeX, SizeY;      // extent along the drawing's X and Y
    }

    /// <summary>
    /// Rectangles on the duct layers (closed 4-point polylines, or the diagonal line of the "X" drawn in a riser):
    /// used to turn a rectangular opening the way the duct runs. Only axis-aligned shapes; a rotated duct keeps no outline.
    /// </summary>
    public static class DuctOutlines
    {
        public static List<DuctOutline> Read(CadDocument doc, DwgProfile profile)
        {
            var layers = new Regex(profile.DuctLayers, RegexOptions.IgnoreCase);
            var list = new List<DuctOutline>();
            foreach (var e in doc.Entities)
            {
                if (!layers.IsMatch(e.Layer?.Name ?? "")) continue;
                List<(double X, double Y)> pts = null;
                if (e is LwPolyline pl && (pl.Vertices.Count == 2 || (pl.IsClosed && pl.Vertices.Count == 4) || pl.Vertices.Count == 5))
                    pts = pl.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList();
                else if (e is Line ln)
                    pts = new List<(double, double)> { (ln.StartPoint.X, ln.StartPoint.Y), (ln.EndPoint.X, ln.EndPoint.Y) };
                if (pts == null) continue;

                double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
                double sx = maxX - minX, sy = maxY - minY;
                if (sx < 2 || sy < 2 || sx > 60 || sy > 60) continue;              // a line along a duct, or not a riser-sized box
                if (pts.Count >= 4 && !pts.All(p => Near(p.X, minX) || Near(p.X, maxX)))  continue;   // rotated: not used
                list.Add(new DuctOutline { X = (minX + maxX) / 2, Y = (minY + maxY) / 2, SizeX = sx, SizeY = sy });
            }
            return list;
        }

        /// <summary>
        /// Angle (radians, in the drawing) of the duct's first dimension near a point: 0 = the first number runs along X,
        /// π/2 = along Y. Null when no outline of that size is drawn there.
        /// </summary>
        public static double? Orientation(IEnumerable<DuctOutline> outlines, double x, double y, double first, double second, double reach = 12)
        {
            if (Math.Abs(first - second) < 0.5) return 0;                        // square: either way
            var near = outlines.Where(o => Math.Abs(o.X - x) <= reach + o.SizeX / 2 && Math.Abs(o.Y - y) <= reach + o.SizeY / 2)
                               .OrderBy(o => Math.Abs(o.X - x) + Math.Abs(o.Y - y)).ToList();
            foreach (var o in near)
            {
                if (Math.Abs(o.SizeX - first) <= 1 && Math.Abs(o.SizeY - second) <= 1) return 0;
                if (Math.Abs(o.SizeX - second) <= 1 && Math.Abs(o.SizeY - first) <= 1) return Math.PI / 2;
            }
            return null;
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
    }
}
