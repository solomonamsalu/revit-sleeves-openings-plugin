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
                    var (circles, cols, w, l, rows) = SizeFrom(ordered, i, rules, columns);
                    current.Circles = circles; current.Columns = cols;
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

        /// <summary>
        /// The circles and opening (inches) of a segment that starts at floor <paramref name="i"/> of the ascending list:
        /// apartments on this floor and every floor above + roof (rules 5-7, 13), in a grid of the given columns (0 = square).
        /// </summary>
        public static (int Circles, int Columns, double Width, double Length, int Rows) SizeFrom(IList<ElectricalFloor> ascending, int i, ElectricalRules rules, int columns)
        {
            int circles = rules.CirclesFor(ascending.Skip(i).Sum(f => f.Apartments));
            int cols = columns > 0 ? columns : (int)Math.Ceiling(Math.Sqrt(circles));
            var (w, l, rows) = rules.OpeningFor(circles, cols);
            return (circles, cols, w, l, rows);
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

        private static string Views(Document doc, Level level) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Count(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id) + " plan view(s) on this level";

        /// <summary>Draws the circles as detail lines in the level's plan view (rule 8). Inside a transaction.</summary>
        /// <summary>The level's floor plan the circles go in (a floor plan before any other plan); null when it has none.</summary>
        public static ViewPlan PlanOf(Document doc, Level level) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id)
                .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : 1).FirstOrDefault();

        public static int DrawCircles(Document doc, Level level, ElectricalSegment seg, ElectricalRules rules)
        {
            var view = PlanOf(doc, level);
            if (view == null) { App.Log("ELEC| CIRCLES level '" + level.Name + "': NO plan view for this level, no circles drawn"); return 0; }

            double r = Units.InchesToFeet(rules.CircleDiameter) / 2;
            int n = 0;
            var centres = CircleCentres(seg, rules, seg.Point, level.ProjectElevation);
            foreach (var c in centres)
            {
                // A full circle is two arcs in Revit.
                doc.Create.NewDetailCurve(view, Arc.Create(c, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                doc.Create.NewDetailCurve(view, Arc.Create(c, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                n++;
            }
            if (centres.Count > 0)
                App.Log("ELEC| CIRCLES level '" + level.Name + "': " + n + " circle(s) in view '" + view.Name + "' (" + view.ViewType +
                        ", one of " + Views(doc, level) + "), grid " + seg.Columns + "x" + seg.Rows + ", centres X " +
                        centres.Min(c => c.X).ToString("0.###") + ".." + centres.Max(c => c.X).ToString("0.###") + " Y " +
                        centres.Min(c => c.Y).ToString("0.###") + ".." + centres.Max(c => c.Y).ToString("0.###") + " ft, Z " + centres[0].Z.ToString("0.###"));
            return n;
        }
    }
}
