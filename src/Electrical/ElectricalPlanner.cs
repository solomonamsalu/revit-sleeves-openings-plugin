using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Electrical
{
    /// <summary>One floor in the electrical plan: how many apartments it holds and whether the riser offsets above it.</summary>
    public class ElectricalFloor
    {
        public Level Level;
        public int Apartments;
        public bool OffsetAbove;
    }

    /// <summary>A straight run of the electrical riser: same circles and opening size on every floor.</summary>
    public class ElectricalSegment
    {
        public List<Level> Levels = new List<Level>();     // ascending
        public int Circles;
        public int Columns;
        public int Rows;
        public double Width, Length;                        // inches
        public XYZ Point;                                   // chosen by the user
        public string FloorsText => Levels.Count == 1 ? Levels[0].Name : $"{Levels.First().Name} – {Levels.Last().Name}";
    }

    /// <summary>
    /// Electrical rules 4-16: one conduit circle per apartment above + one for the roof, 0.75" c-c,
    /// same count all the way up unless the riser offsets, then recalculated for the floors that remain.
    /// </summary>
    public static class ElectricalPlanner
    {
        public static List<ElectricalSegment> Segments(IList<ElectricalFloor> floors, ElectricalRules rules, int columns)
        {
            var ordered = floors.OrderBy(f => f.Level.Elevation).ToList();
            var segments = new List<ElectricalSegment>();
            var current = new ElectricalSegment();

            for (int i = 0; i < ordered.Count; i++)
            {
                if (current.Levels.Count == 0)
                {
                    // Circles = apartments on this floor and every floor above + roof (rules 5-7, 13)
                    current.Circles = rules.CirclesFor(ordered.Skip(i).Sum(f => f.Apartments));
                    current.Columns = columns > 0 ? columns : (int)Math.Ceiling(Math.Sqrt(current.Circles));
                    var (w, l, rows) = rules.OpeningFor(current.Circles, current.Columns);
                    current.Width = w; current.Length = l; current.Rows = rows;
                }
                current.Levels.Add(ordered[i].Level);
                if (ordered[i].OffsetAbove && i < ordered.Count - 1)
                {
                    segments.Add(current);
                    current = new ElectricalSegment();
                }
            }
            if (current.Levels.Count > 0) segments.Add(current);
            return segments;
        }

        /// <summary>Centres of the conduit circles for a segment, in feet, around the given point.</summary>
        public static List<XYZ> CircleCentres(ElectricalSegment seg, ElectricalRules rules, XYZ centre, double z)
        {
            double s = Units.InchesToFeet(rules.ConduitSpacingCenterToCenter);
            double x0 = centre.X - (seg.Columns - 1) * s / 2;
            double y0 = centre.Y - (seg.Rows - 1) * s / 2;
            var pts = new List<XYZ>();
            for (int n = 0; n < seg.Circles; n++)
            {
                int col = n % seg.Columns, row = n / seg.Columns;
                pts.Add(new XYZ(x0 + col * s, y0 + row * s, z));
            }
            return pts;
        }

        /// <summary>Draws the circles as detail lines in the level's plan view (rule 8). Inside a transaction.</summary>
        public static int DrawCircles(Document doc, Level level, ElectricalSegment seg, ElectricalRules rules)
        {
            var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id)
                .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : 1).FirstOrDefault();
            if (view == null) return 0;

            double r = Units.InchesToFeet(rules.CircleDiameter) / 2;
            int n = 0;
            foreach (var c in CircleCentres(seg, rules, seg.Point, level.ProjectElevation))
            {
                // A full circle is two arcs in Revit.
                doc.Create.NewDetailCurve(view, Arc.Create(c, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                doc.Create.NewDetailCurve(view, Arc.Create(c, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                n++;
            }
            return n;
        }
    }
}
