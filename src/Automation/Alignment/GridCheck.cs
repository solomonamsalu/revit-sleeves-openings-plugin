using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>A named grid line: from a DWG (drawing units) or from Revit (feet).</summary>
    public class GridLine
    {
        public string Name;
        public double X1, Y1, X2, Y2;
        public string Source;
    }

    /// <summary>How the office grid drawing, placed like the floor drawings, lands on the Revit grids.</summary>
    public class GridCheckResult
    {
        public string File;
        public PlanMap Map;
        /// <summary>Grid name -> worst distance (inches) of the drawing's grid line from the Revit grid of that name.</summary>
        public List<(string Name, double Off)> Matches = new List<(string, double)>();
        /// <summary>Best-fit shift (inches) that would move the drawing's grids onto Revit's; null with too few grids.</summary>
        public (double X, double Y)? Offset;
        public bool Passed;
        public string Summary;
    }

    /// <summary>
    /// Proves the Revit position (plan section 10, method 2): the office grid-lines DWG sits in the same 0,0 as the
    /// per-floor xrefs, so mapped the same way, its grid lines must fall on the Revit grids of the same name.
    /// Needs two matching grids that are not parallel; every matching grid must be within the tolerance.
    /// </summary>
    public static class GridCheck
    {
        /// <summary>Grid lines of a DWG: a line with a bubble (circle with a short name inside) on its end or its extension.</summary>
        public static List<GridLine> Read(CadDocument doc)
        {
            var lines = new List<(double X1, double Y1, double X2, double Y2)>();
            var circles = new List<(double X, double Y, double R)>();
            var texts = new List<(string T, double X, double Y)>();
            Walk(doc.Entities, DwgXform.Identity, 0, lines, circles, texts);

            var bubbles = new List<(string Name, double X, double Y, double R)>();
            foreach (var c in circles)
            {
                var t = texts.Where(x => Math.Sqrt(Math.Pow(x.X - c.X, 2) + Math.Pow(x.Y - c.Y, 2)) <= c.R).Select(x => x.T).FirstOrDefault();
                if (t != null) bubbles.Add((t, c.X, c.Y, c.R));
            }

            var grids = new List<GridLine>();
            foreach (var l in lines)
            {
                double len = Math.Sqrt(Math.Pow(l.X2 - l.X1, 2) + Math.Pow(l.Y2 - l.Y1, 2));
                if (len < 60) continue;                                    // grid lines are long; skip bubble details
                var b = bubbles.Where(x => ToLine(x.X, x.Y, l) <= 1 && Math.Min(D(x.X, x.Y, l.X1, l.Y1), D(x.X, x.Y, l.X2, l.Y2)) <= x.R * 3)
                               .Select(x => x.Name).FirstOrDefault();
                if (b != null && !grids.Any(g => g.Name == b))
                    grids.Add(new GridLine { Name = b, X1 = l.X1, Y1 = l.Y1, X2 = l.X2, Y2 = l.Y2 });
            }
            return grids;
        }

        private static void Walk(IEnumerable<Entity> entities, DwgXform xf, int depth, List<(double, double, double, double)> lines,
                                 List<(double, double, double)> circles, List<(string, double, double)> texts)
        {
            foreach (var e in entities)
            {
                switch (e)
                {
                    case Line l:
                        {
                            var a = xf.Apply(l.StartPoint.X, l.StartPoint.Y); var b = xf.Apply(l.EndPoint.X, l.EndPoint.Y);
                            lines.Add((a.X, a.Y, b.X, b.Y));
                            break;
                        }
                    case Circle c:
                        {
                            var p = xf.Apply(c.Center.X, c.Center.Y);
                            circles.Add((p.X, p.Y, c.Radius * Math.Abs(xf.Sx)));
                            break;
                        }
                    case MText m: Text(m.PlainText, m.InsertPoint.X, m.InsertPoint.Y); break;
                    case TextEntity t: Text(t.Value, t.InsertPoint.X, t.InsertPoint.Y); break;
                    case Insert ins:
                        foreach (var at in ins.Attributes) Text(at.Value, at.InsertPoint.X, at.InsertPoint.Y);      // attributes sit in the space holding the insert
                        if (depth < 4 && ins.Block?.Entities != null) Walk(ins.Block.Entities, xf.Then(ins), depth + 1, lines, circles, texts);
                        break;
                }
            }

            void Text(string value, double x, double y)
            {
                var v = (value ?? "").Trim().ToUpperInvariant();
                if (v.Length == 0 || v.Length > 4) return;
                var p = xf.Apply(x, y);
                texts.Add((v, p.X, p.Y));
            }
        }

        /// <summary>Maps the DWG grids with <paramref name="map"/> and measures them against the Revit grids (feet).</summary>
        public static GridCheckResult Compare(List<GridLine> dwg, PlanMap map, List<GridLine> revit, double tolerance = 1)
        {
            var result = new GridCheckResult { Map = map };
            var rows = new List<(double Nx, double Ny, double D)>();          // Revit normal, signed offset (inches)
            foreach (var g in dwg)
            {
                var r = revit.FirstOrDefault(x => Norm(x.Name) == Norm(g.Name));
                if (r == null) continue;
                var a = map.Apply(g.X1, g.Y1); var b = map.Apply(g.X2, g.Y2);
                double dx = r.X2 - r.X1, dy = r.Y2 - r.Y1, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                double nx = -dy / len, ny = dx / len;
                double da = ((a.X - r.X1) * nx + (a.Y - r.Y1) * ny) * 12, db = ((b.X - r.X1) * nx + (b.Y - r.Y1) * ny) * 12;
                result.Matches.Add((g.Name, Math.Max(Math.Abs(da), Math.Abs(db))));
                rows.Add((nx, ny, (da + db) / 2));
            }

            // two grids that are not parallel, e.g. a numbered and a lettered one
            bool crossing = rows.Any(p => rows.Any(q => Math.Abs(p.Nx * q.Ny - p.Ny * q.Nx) > 0.5));
            if (crossing)
            {
                // least-squares shift t with n_i . t = d_i (the drawing grid sits d_i from the Revit grid along its normal)
                double a11 = rows.Sum(p => p.Nx * p.Nx), a12 = rows.Sum(p => p.Nx * p.Ny), a22 = rows.Sum(p => p.Ny * p.Ny);
                double b1 = rows.Sum(p => p.Nx * p.D), b2 = rows.Sum(p => p.Ny * p.D), det = a11 * a22 - a12 * a12;
                if (Math.Abs(det) > 1e-9) result.Offset = ((a22 * b1 - a12 * b2) / det, (a11 * b2 - a12 * b1) / det);
            }
            result.Passed = crossing && result.Matches.All(m => m.Off <= tolerance);

            if (result.Matches.Count == 0)
                result.Summary = $"no grid names in common (drawing: {string.Join(", ", dwg.Select(g => g.Name))}; Revit: {string.Join(", ", revit.Select(g => g.Name).Distinct())})";
            else if (!crossing)
                result.Summary = $"only parallel grids in common ({string.Join(", ", result.Matches.Select(m => m.Name))}); position not proven";
            else if (result.Passed)
                result.Summary = $"{result.Matches.Count} grids ({string.Join(", ", result.Matches.Select(m => m.Name))}) within {result.Matches.Max(m => m.Off):0.##}\" of the Revit grids";
            else
                result.Summary = $"grids do NOT match the Revit grids: {string.Join(", ", result.Matches.Select(m => $"{m.Name} {m.Off:0.#}\""))}" +
                                 (result.Offset.HasValue ? $"; the drawings sit {Units.FormatInches(Math.Abs(result.Offset.Value.X))} {(result.Offset.Value.X >= 0 ? "east" : "west")} and " +
                                                           $"{Units.FormatInches(Math.Round(Math.Abs(result.Offset.Value.Y), 1))} {(result.Offset.Value.Y >= 0 ? "north" : "south")} of them" : "");
            return result;
        }

        private static string Norm(string name) => (name ?? "").Trim().ToUpperInvariant();
        private static double D(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

        private static double ToLine(double x, double y, (double X1, double Y1, double X2, double Y2) l)
        {
            double dx = l.X2 - l.X1, dy = l.Y2 - l.Y1, len = Math.Sqrt(dx * dx + dy * dy);
            return len == 0 ? D(x, y, l.X1, l.Y1) : Math.Abs((x - l.X1) * dy - (y - l.Y1) * dx) / len;
        }
    }
}
