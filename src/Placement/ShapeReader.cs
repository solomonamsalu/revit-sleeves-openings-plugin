using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SleevesOpenings.Placement
{
    /// <summary>The extent of what an opening family draws, in model feet.</summary>
    public class DrawnShape
    {
        public double X0 = double.MaxValue, Y0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue;

        /// <summary>Where it was measured: "solid", or "plan lines in '&lt;view&gt;'"; null when nothing was found.</summary>
        public string Source;

        public bool Found => X0 <= X1;
        public double Width => X1 - X0;
        public double Length => Y1 - Y0;

        /// <summary>Middle of the drawn shape in XY, at the given elevation.</summary>
        public XYZ Middle(double z) => new XYZ((X0 + X1) / 2, (Y0 + Y1) / 2, z);
    }

    /// <summary>
    /// Measures where an opening family actually draws its hole. The office Openings family is not drawn around its
    /// insertion point (it keeps the box's top edge 6" above it and sizes downward), and its box is plan lines only, so
    /// the hole's middle cannot be read off the instance's location: it is measured on the instance's solid, else on the
    /// lines it draws in the level's floor plan. Two openings drawn in line can therefore have insertion points 4.5"
    /// apart, which is why the riser checks compare middles. Keep one reader per document - it caches the plan views.
    /// </summary>
    public class ShapeReader
    {
        private readonly Document _doc;
        private readonly Dictionary<ElementId, ViewPlan> _plans = new Dictionary<ElementId, ViewPlan>();

        public ShapeReader(Document doc) { _doc = doc; }

        public DrawnShape Of(FamilyInstance inst, Level level)
        {
            var shape = new DrawnShape();
            if (inst == null) return shape;
            Take(shape, Geometry(inst, null), false);
            if (shape.Found) { shape.Source = "solid"; return shape; }
            var view = level != null ? Plan(level) : null;
            if (view == null) return shape;
            Take(shape, Geometry(inst, view), true);
            if (shape.Found) shape.Source = "plan lines in '" + view.Name + "'";
            return shape;
        }

        /// <summary>The level's floor plan (a floor plan before any other plan); null when it has none. Looked up once per level.</summary>
        private ViewPlan Plan(Level level)
        {
            if (_plans.TryGetValue(level.Id, out var cached)) return cached;
            ViewPlan view = null;
            try
            {
                view = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id)
                    .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : 1).FirstOrDefault();
            }
            catch (Exception) { }
            _plans[level.Id] = view;
            return view;
        }

        private static GeometryElement Geometry(FamilyInstance inst, View view)
        {
            try
            {
                return inst.get_Geometry(view == null
                    ? new Options { DetailLevel = ViewDetailLevel.Fine }
                    : new Options { View = view });
            }
            catch (Exception) { return null; }   // a deleted view, a family that will not regenerate
        }

        private static void Take(DrawnShape s, GeometryElement geo, bool lines)
        {
            if (geo == null) return;
            foreach (var g in geo)
            {
                if (g is GeometryInstance gi) Take(s, gi.GetInstanceGeometry(), lines);
                else if (!lines && g is Solid solid && solid.Volume > 0) foreach (Edge e in solid.Edges) Add(s, e.Tessellate());
                else if (lines && g is Curve c) Add(s, c.Tessellate());
            }
        }

        private static void Add(DrawnShape s, IEnumerable<XYZ> pts)
        {
            foreach (var p in pts)
            {
                s.X0 = Math.Min(s.X0, p.X); s.Y0 = Math.Min(s.Y0, p.Y);
                s.X1 = Math.Max(s.X1, p.X); s.Y1 = Math.Max(s.Y1, p.Y);
            }
        }
    }
}
