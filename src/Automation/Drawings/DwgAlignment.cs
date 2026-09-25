using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A named block insert in model space (dynamic blocks by their source name).</summary>
    public class BlockPoint
    {
        public string Name;
        public double X, Y;
    }

    /// <summary>The shift that moves one floor of the engineer DWG onto a reference drawing of the same floor.</summary>
    public class FloorShift
    {
        public double Dx, Dy;
        /// <summary>Engineer blocks that land on a same-named reference block within the tolerance.</summary>
        public int Support;
        /// <summary>Support of the best other shift (more than a foot away); a close second means the match is ambiguous.</summary>
        public int RunnerUp;
        /// <summary>Engineer blocks on the floor that have a same-named block anywhere in the reference.</summary>
        public int Comparable;
        public double Residual;                   // RMS of the matched pairs around the shift (drawing units)
        /// <summary>Different block names among the agreeing blocks. A row of identical symbols can match itself at a wrong shift, so one name is not enough.</summary>
        public int Names;
        public bool Found => Support >= MinSupport && Names >= 2 && Support >= 2 * RunnerUp;
        public const int MinSupport = 3;
    }

    /// <summary>
    /// Lines up a floor of the engineer DWG (all floors in one file, side by side) with a drawing of that floor
    /// that is already at Revit's position (per-floor xref at the office 0,0, or the DWG imported in the model).
    /// The same blocks (diffusers, fans, riser symbols...) are drawn in both; the shift most of them agree on wins.
    /// Translation only: a reference that is rotated or scaled against the engineer drawing is reported, not guessed.
    /// </summary>
    public static class DwgAlignment
    {
        /// <summary>Named block inserts, including inside packed blocks (bound xrefs), with their model-space position.</summary>
        public static List<BlockPoint> Blocks(CadDocument doc)
        {
            var list = new List<BlockPoint>();
            Collect(doc.Entities, DwgXform.Identity, 0, list);
            return list;
        }

        private static void Collect(IEnumerable<Entity> entities, DwgXform xf, int depth, List<BlockPoint> list)
        {
            foreach (var ins in entities.OfType<Insert>())
            {
                var block = ins.Block;
                if (block == null) continue;
                string name = block.Source?.Name ?? block.Name;
                if (depth < 3 && Container(block)) { Collect(block.Entities, xf.Then(ins), depth + 1, list); continue; }
                if (name.StartsWith("*") || name.StartsWith("A$")) continue;     // anonymous / pasted blocks carry no meaning
                var (x, y) = xf.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                list.Add(new BlockPoint { Name = name, X = x, Y = y });
            }
        }

        /// <summary>
        /// A block that holds a drawing rather than being a symbol: a packed xref (many objects), or a pasted-as-block
        /// (A$C...) / anonymous group of any size. Dynamic blocks (anonymous, with a source) are symbols.
        /// </summary>
        private static bool Container(ACadSharp.Tables.BlockRecord block) =>
            block.Entities != null && (block.Entities.Count() > 200 ||
                                       (block.Source == null && (block.Name.StartsWith("A$") || block.Name.StartsWith("*"))));

        /// <summary>
        /// Every same-name pair proposes a shift; the one most engineer blocks agree on (within <paramref name="tolerance"/>) wins.
        /// </summary>
        public static FloorShift Shift(IList<BlockPoint> floor, IList<BlockPoint> reference, double tolerance = 1) =>
            Shift(floor, reference, tolerance, null);

        /// <summary>How well a given shift fits (no search): used to test one floor against the shift the other floors share.</summary>
        public static FloorShift Test(IList<BlockPoint> floor, IList<BlockPoint> reference, double dx, double dy, double tolerance = 1) =>
            Shift(floor, reference, tolerance, (dx, dy));

        private static FloorShift Shift(IList<BlockPoint> floor, IList<BlockPoint> reference, double tolerance, (double Dx, double Dy)? given)
        {
            var byName = reference.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.ToList());
            var mine = floor.Where(b => byName.ContainsKey(b.Name)).ToList();
            var result = new FloorShift { Comparable = mine.Count };
            if (given.HasValue) { result.Dx = given.Value.Dx; result.Dy = given.Value.Dy; }     // kept even with nothing to compare
            if (mine.Count == 0) return result;

            if (given.HasValue)
            {
                var fit = Pairs(mine, byName, given.Value.Dx, given.Value.Dy, tolerance);
                result.Dx = given.Value.Dx; result.Dy = given.Value.Dy; result.Support = fit.Count;
                result.Names = fit.Select(p => p.Name).Distinct().Count();
                if (fit.Count > 0) result.Residual = Math.Sqrt(fit.Average(p => (p.Dx - result.Dx) * (p.Dx - result.Dx) + (p.Dy - result.Dy) * (p.Dy - result.Dy)));
                return result;
            }

            // Candidate shifts, bucketed to 2 units; the busiest buckets are scored exactly.
            const double cell = 2;
            var buckets = new Dictionary<(long, long), int>();
            foreach (var a in mine)
                foreach (var b in byName[a.Name])
                {
                    var k = ((long)Math.Round((b.X - a.X) / cell), (long)Math.Round((b.Y - a.Y) / cell));
                    buckets.TryGetValue(k, out int n);
                    buckets[k] = n + 1;
                }

            var scored = new List<(double Dx, double Dy, int Support, double Residual, int Names)>();
            foreach (var k in buckets.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key))
            {
                double dx = k.Item1 * cell, dy = k.Item2 * cell;
                for (int pass = 0; pass < 2; pass++)           // score, re-centre on the matched pairs, score again
                {
                    var pairs = Pairs(mine, byName, dx, dy, pass == 0 ? cell * 1.5 : tolerance);
                    if (pairs.Count == 0) break;
                    dx = pairs.Average(p => p.Dx); dy = pairs.Average(p => p.Dy);
                    if (pass == 1)
                        scored.Add((dx, dy, pairs.Count, Math.Sqrt(pairs.Average(p => (p.Dx - dx) * (p.Dx - dx) + (p.Dy - dy) * (p.Dy - dy))),
                                    pairs.Select(p => p.Name).Distinct().Count()));
                }
            }
            if (scored.Count == 0) return result;

            var best = scored.OrderByDescending(s => s.Support).ThenBy(s => s.Residual).First();
            result.Dx = best.Dx; result.Dy = best.Dy; result.Support = best.Support; result.Residual = best.Residual; result.Names = best.Names;
            result.RunnerUp = scored.Where(s => Math.Abs(s.Dx - best.Dx) > 12 || Math.Abs(s.Dy - best.Dy) > 12).Select(s => s.Support).DefaultIfEmpty(0).Max();
            return result;
        }

        /// <summary>Each engineer block with its closest same-named reference block under the shift, if within the tolerance.</summary>
        private static List<(double Dx, double Dy, string Name)> Pairs(List<BlockPoint> mine, Dictionary<string, List<BlockPoint>> byName, double dx, double dy, double tol)
        {
            var pairs = new List<(double, double, string)>();
            foreach (var a in mine)
            {
                BlockPoint hit = null; double bestD = tol;
                foreach (var b in byName[a.Name])
                {
                    double d = Math.Sqrt(Math.Pow(b.X - a.X - dx, 2) + Math.Pow(b.Y - a.Y - dy, 2));
                    if (d <= bestD) { bestD = d; hit = b; }
                }
                if (hit != null) pairs.Add((hit.X - a.X, hit.Y - a.Y, a.Name));
            }
            return pairs;
        }

        /// <summary>Extent of the drawn geometry (lines, polylines, circles, block inserts); text is left out. Null when empty.</summary>
        public static (double MinX, double MinY, double MaxX, double MaxY)? Extents(CadDocument doc)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            void Add((double X, double Y) p) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
            void Walk(IEnumerable<Entity> entities, DwgXform xf, int depth)
            {
                foreach (var e in entities)
                {
                    switch (e)
                    {
                        case Line l: Add(xf.Apply(l.StartPoint.X, l.StartPoint.Y)); Add(xf.Apply(l.EndPoint.X, l.EndPoint.Y)); break;
                        case LwPolyline pl: foreach (var v in pl.Vertices) Add(xf.Apply(v.Location.X, v.Location.Y)); break;
                        case Circle c: Add(xf.Apply(c.Center.X, c.Center.Y)); break;
                        case Insert ins:
                            // the geometry inside the block, whatever its size (a small drawing is often one pasted block)
                            if (depth < 4 && ins.Block?.Entities != null && ins.Block.Entities.Any()) Walk(ins.Block.Entities, xf.Then(ins), depth + 1);
                            else Add(xf.Apply(ins.InsertPoint.X, ins.InsertPoint.Y));
                            break;
                    }
                }
            }
            Walk(doc.Entities, DwgXform.Identity, 0);
            return x0 <= x1 ? (x0, y0, x1, y1) : ((double, double, double, double)?)null;
        }
    }
}
