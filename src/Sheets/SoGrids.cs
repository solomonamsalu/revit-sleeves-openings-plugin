using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SleevesOpenings.Sheets
{
    /// <summary>
    /// Grid bubbles drawn on top of each other in the Sleeves views (24 Skillman: "BBBBBBB", "DDDDDDD" over the column
    /// lines): grids a few inches apart whose ends meet. Of this model's grids one bubble stays and the others are hidden
    /// in that view (Revit's "Hide Bubble": the lines stay, so do dimensions to them). A link's grids cannot be hidden one by
    /// one, so stacks with a link's grids are reported.
    /// </summary>
    internal static class SoGrids
    {
        /// <summary>Bubbles closer than this on paper (feet) overlap.</summary>
        private const double OnPaper = 0.5 / 12;

        private class Entry
        {
            public Grid Grid;                       // null = a link's grid
            public string Name, Link;
            public XYZ A, B;                        // plan ends (End0, End1)
            public readonly HashSet<int> Stacked = new HashSet<int>();
        }

        public static void Unstack(Document doc, IList<ViewPlan> views, List<string> problems)
        {
            int hidden = 0;
            var hiddenIn = new List<string>();
            var linked = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in views.Where(v => v != null).GroupBy(v => v.Id).Select(g => g.First()))
            {
                if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Grids))) continue;
                var entries = Entries(doc, view);
                double reach = OnPaper * view.Scale;
                var parent = Enumerable.Range(0, entries.Count).ToArray();
                int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
                for (int i = 0; i < entries.Count; i++)
                    for (int j = i + 1; j < entries.Count; j++)
                        if (Stack(entries[i], entries[j], reach)) parent[Root(i)] = Root(j);

                int here = 0;
                foreach (var group in Enumerable.Range(0, entries.Count).GroupBy(Root).Where(g => g.Count() > 1).Select(g => g.Select(i => entries[i]).ToList()))
                {
                    var own = group.Where(e => e.Grid != null).OrderBy(e => e.Name.Length).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var e in own.Skip(1))
                        foreach (var end in e.Stacked)
                        {
                            var de = end == 0 ? DatumEnds.End0 : DatumEnds.End1;
                            try
                            {
                                if (!e.Grid.IsBubbleVisibleInView(de, view)) continue;
                                e.Grid.HideBubbleInView(de, view);
                                here++;
                            }
                            catch (Exception ex) { App.Log($"S&O grids: bubble of {e.Name} in '{view.Name}' not hidden: {ex.Message}"); }
                        }
                    var links = group.Where(e => e.Grid == null).ToList();
                    if (links.Count + (own.Count > 0 ? 1 : 0) < 2) continue;
                    foreach (var e in links)
                    {
                        if (!linked.TryGetValue(e.Link, out var names)) linked[e.Link] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        names.Add(e.Name);
                    }
                }
                if (here > 0) { hidden += here; hiddenIn.Add(view.Name); }
            }
            if (hidden > 0)
                problems.Add($"Stacked grid bubbles: {hidden} hidden in {string.Join(", ", hiddenIn)} (Hide Bubble in the view; the grid lines stay)");
            foreach (var kv in linked)
                problems.Add($"Grids of the link '{kv.Key}' ({string.Join(", ", kv.Value)}) are drawn on top of other grids on the S&O sheets; " +
                             "hide that link's grids in the Sleeves views (Visibility/Graphics > Revit Links > Custom > Grids off), or correct the link");
        }

        /// <summary>This model's grids as the view draws them, and the grids of the links shown in it that reach its level.</summary>
        private static List<Entry> Entries(Document doc, ViewPlan view)
        {
            var list = new List<Entry>();
            foreach (var g in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                Curve c = null;
                try { c = g.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault(); } catch { }
                if (!((c ?? g.Curve) is Line line)) continue;
                list.Add(new Entry { Grid = g, Name = g.Name, A = line.GetEndPoint(0), B = line.GetEndPoint(1) });
            }
            double? z = view.GenLevel?.Elevation;
            foreach (var link in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = link.GetLinkDocument();
                if (ld == null) continue;
                var t = link.GetTotalTransform();
                foreach (var g in new FilteredElementCollector(ld).OfClass(typeof(Grid)).Cast<Grid>())
                {
                    if (!(g.Curve is Line line)) continue;
                    if (z.HasValue)
                        try
                        {
                            var ext = g.GetExtents();
                            double lo = t.OfPoint(ext.MinimumPoint).Z, hi = t.OfPoint(ext.MaximumPoint).Z;
                            if (z.Value < Math.Min(lo, hi) - 0.01 || z.Value > Math.Max(lo, hi) + 0.01) continue;
                        }
                        catch { }
                    list.Add(new Entry { Name = g.Name, Link = link.Name, A = t.OfPoint(line.GetEndPoint(0)), B = t.OfPoint(line.GetEndPoint(1)) });
                }
            }
            return list;
        }

        /// <summary>Parallel, closer than a bubble, and an end of each within a bubble of the other's: those ends are marked.</summary>
        private static bool Stack(Entry a, Entry b, double reach)
        {
            double ax = a.B.X - a.A.X, ay = a.B.Y - a.A.Y, bx = b.B.X - b.A.X, by = b.B.Y - b.A.Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6) return false;
            if (Math.Abs(ax * by - ay * bx) / (la * lb) > Math.Sin(Math.PI / 180)) return false;          // within 1°
            if (Math.Abs((b.A.X - a.A.X) * ay - (b.A.Y - a.A.Y) * ax) / la >= reach) return false;       // b's line off a's
            var ea = new[] { a.A, a.B }; var eb = new[] { b.A, b.B };
            // the same grid twice (a link's copy of this model's, exactly on it): one bubble is seen, nothing to do
            if (string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) && ea.All(p => eb.Any(q => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)) < reach / 8)))
                return false;
            bool any = false;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    double dx = ea[i].X - eb[j].X, dy = ea[i].Y - eb[j].Y;
                    if (Math.Sqrt(dx * dx + dy * dy) >= reach) continue;
                    a.Stacked.Add(i); b.Stacked.Add(j); any = true;
                }
            return any;
        }
    }
}
