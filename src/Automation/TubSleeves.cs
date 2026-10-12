using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Assembly;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Tub sleeves by the tub's direction on the floor plan (office rule, 2026-10-12): a tub running left-right gets the
    /// rules' sleeves along it (two 6"), one running up-down the plumbing.fixtures BT "vertical" layout (one 10") at its
    /// drain. Left-right is the floor plan view's own across direction, so a rotated view reads as the sheet does.
    /// </summary>
    public static class TubSleeves
    {
        /// <summary>The across direction (model plan, unit) of the level's floor plan: its Sleeves plan, else the plan named like the level, else any.</summary>
        public static XYZ PlanRight(Document doc, string levelName)
        {
            try
            {
                var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null &&
                                (string.Equals(v.GenLevel.Name, levelName, StringComparison.OrdinalIgnoreCase) || AutoPlacer.SameFloor(v.GenLevel.Name, levelName)))
                    .ToList();
                var view = plans.FirstOrDefault(v => v.Name.IndexOf("sleeve", StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? plans.FirstOrDefault(v => string.Equals(v.Name, levelName, StringComparison.OrdinalIgnoreCase))
                        ?? plans.FirstOrDefault();
                var r = view?.RightDirection;
                if (r != null)
                {
                    var flat = new XYZ(r.X, r.Y, 0);
                    if (flat.GetLength() > 1e-6) return flat.Normalize();
                }
            }
            catch (Exception ex) { App.Log("Tub sleeves: plan direction not read: " + ex.Message); }
            return XYZ.BasisX;
        }

        /// <summary>Along the tub (model plan, unit): between its first and last sleeve point when it has two or more, else its box's long side; null = not known.</summary>
        public static XYZ Along(IList<XYZ> points, double x0 = 0, double y0 = 0, double x1 = 0, double y1 = 0)
        {
            if (points != null && points.Count > 1)
            {
                var d = new XYZ(points[points.Count - 1].X - points[0].X, points[points.Count - 1].Y - points[0].Y, 0);
                if (d.GetLength() > 1e-6) return d.Normalize();
            }
            double w = Math.Abs(x1 - x0), l = Math.Abs(y1 - y0);
            if (Math.Max(w, l) < 1e-6 || Math.Abs(w - l) < 0.1 * Math.Max(w, l)) return null;        // no box, or square: no direction
            return w > l ? XYZ.BasisX : XYZ.BasisY;
        }

        /// <summary>The tub's long side runs up-down on the plan whose across direction is <paramref name="right"/>.</summary>
        public static bool RunsUpDown(XYZ along, XYZ right)
        {
            var up = new XYZ(-right.Y, right.X, 0);
            return Math.Abs(along.DotProduct(up)) > Math.Abs(along.DotProduct(right));
        }

        /// <summary>
        /// The tub's sleeve points and size: unchanged (Sleeve null) when it runs left-right or its direction is not known,
        /// else the vertical layout centred on its drain (the middle of its points). A tub not laid out with the rules' row
        /// of sleeves (a freestanding tub's one sleeve, a tub with no drain drawn: its centre, flagged) is left as it is.
        /// How = what was decided, for the notes.
        /// </summary>
        public static (List<XYZ> Points, double? Sleeve, string How) Decide(PlumbingRules rules, List<XYZ> points, XYZ along, XYZ right)
        {
            if (points == null || !rules.Fixtures.TryGetValue("BT", out var bt) || bt.Count < 2 || points.Count < bt.Count) return (points, null, null);
            string Size(int n, double s) => (n > 1 ? $"{n} x " : "") + Units.FormatInches(s);
            if (along == null) return (points, null, $"tub direction not known: {Size(bt.Count, rules.SleeveFor(bt.Pipe))} (check)");
            if (bt.Vertical == null || !RunsUpDown(along, right)) return (points, null, $"tub runs left-right on the plan: {Size(bt.Count, rules.SleeveFor(bt.Pipe))}");

            var mid = new XYZ(points.Average(p => p.X), points.Average(p => p.Y), 0);
            int n = Math.Max(1, bt.Vertical.Count);
            var pts = Enumerable.Range(0, n).Select(i => mid + along * ((i - (n - 1) / 2.0) * bt.Vertical.Spacing / 12)).ToList();
            double sleeve = bt.Vertical.Sleeve > 0 ? bt.Vertical.Sleeve : rules.SleeveFor(bt.Pipe);
            return (pts, sleeve, $"tub runs up-down on the plan: {Size(n, sleeve)} at its drain");
        }

        /// <summary>A tub crossing laid out by its direction (its points give it). Returns true when it runs up-down.</summary>
        public static bool Apply(Crossing c, PlumbingRules rules, XYZ right)
        {
            var pts = c.EachPoint && c.Points.Count > 0 ? c.Points.Select(p => new XYZ(p[0], p[1], 0)).ToList() : new List<XYZ> { new XYZ(c.X, c.Y, 0) };
            var (points, sleeve, how) = Decide(rules, pts, Along(pts), right);
            if (how != null) c.Notes.Add(how);
            if (sleeve == null) return false;
            c.SleeveSize = sleeve;
            c.Points.Clear();
            c.EachPoint = points.Count > 1;
            if (c.EachPoint) c.Points.AddRange(points.Select(p => new[] { p.X, p.Y }));
            c.X = points.Average(p => p.X); c.Y = points.Average(p => p.Y);
            return true;
        }
    }
}
