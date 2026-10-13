using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// A DWG file's model space flattened for <see cref="StairFinder"/>: lines (polylines split), arcs (by their end points,
    /// so a mirrored block's door swing turns the right way) and words, blocks exploded, in the file's own coordinates
    /// times <paramref name="feetPerUnit"/>. What a block draws on layer 0 takes the insert's layer.
    /// </summary>
    public static class DwgFlat
    {
        private struct Xf
        {
            public double A, B, C, D, Tx, Ty;
            public static Xf Identity => new Xf { A = 1, D = 1 };
            public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);
            public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
            public Xf Then(Insert ins)
            {
                double r = ins.Rotation, sx = ins.XScale, sy = ins.YScale;
                double a = sx * Math.Cos(r), b = -sy * Math.Sin(r), c = sx * Math.Sin(r), d = sy * Math.Cos(r);
                // the block's base point goes to the insertion point
                var bp = ins.Block?.BlockEntity?.BasePoint;
                double bx = bp?.X ?? 0, by = bp?.Y ?? 0;
                double ix = ins.InsertPoint.X - (a * bx + b * by), iy = ins.InsertPoint.Y - (c * bx + d * by);
                if (ins.Normal.Z < 0) { a = -a; b = -b; ix = -ix; }          // extrusion (0,0,-1): mirrored in X
                var (tx, ty) = Apply(ix, iy);
                return new Xf { A = A * a + B * c, B = A * b + B * d, C = C * a + D * c, D = C * b + D * d, Tx = tx, Ty = ty };
            }
        }

        private static readonly Regex MTextCodes = new Regex(@"\\[A-Za-z][^;\\{}]*;|\\[PpNn~]|[{}]");

        public static StairFinder.Drawing Read(CadDocument cad, string source, double feetPerUnit)
        {
            var d = new StairFinder.Drawing { Source = source };
            Walk(cad.Entities, Xf.Identity, null, 0, d, feetPerUnit);
            return d;
        }

        private static void Walk(IEnumerable<Entity> entities, Xf x, string blockLayer, int depth, StairFinder.Drawing d, double k)
        {
            foreach (var e in entities)
            {
                string layer = e.Layer?.Name ?? "";
                if ((layer == "0" || layer == "") && blockLayer != null) layer = blockLayer;
                try
                {
                    switch (e)
                    {
                        case Insert ins when depth < 8 && ins.Block != null:
                            Walk(ins.Block.Entities, x.Then(ins), layer == "0" ? blockLayer : layer, depth + 1, d, k);
                            break;
                        case Line ln:
                            Add(d, layer, x.Apply(ln.StartPoint.X, ln.StartPoint.Y), x.Apply(ln.EndPoint.X, ln.EndPoint.Y), k);
                            break;
                        case LwPolyline pl:
                            {
                                double flip = pl.Normal.Z < 0 ? -1 : 1;
                                var v = pl.Vertices.Select(q => x.Apply(flip * q.Location.X, q.Location.Y)).ToList();
                                for (int i = 0; i + 1 < v.Count; i++) Add(d, layer, v[i], v[i + 1], k);
                                if (pl.IsClosed && v.Count > 2) Add(d, layer, v[v.Count - 1], v[0], k);
                                break;
                            }
                        case Arc ar:
                            {
                                double flip = ar.Normal.Z < 0 ? -1 : 1;
                                var c = x.Apply(flip * ar.Center.X, ar.Center.Y);
                                var a = x.Apply(flip * (ar.Center.X + ar.Radius * Math.Cos(ar.StartAngle)), ar.Center.Y + ar.Radius * Math.Sin(ar.StartAngle));
                                var b = x.Apply(flip * (ar.Center.X + ar.Radius * Math.Cos(ar.EndAngle)), ar.Center.Y + ar.Radius * Math.Sin(ar.EndAngle));
                                d.Arcs.Add(new StairFinder.Swing { Layer = layer, Cx = c.X * k, Cy = c.Y * k, R = ar.Radius * x.Scale * k, Ax = a.X * k, Ay = a.Y * k, Bx = b.X * k, By = b.Y * k });
                                break;
                            }
                        case TextEntity t:
                            {
                                var p = x.Apply(t.InsertPoint.X, t.InsertPoint.Y);
                                d.Texts.Add(new StairFinder.Txt { Layer = layer, X = p.X * k, Y = p.Y * k, Text = (t.Value ?? "").Trim() });
                                break;
                            }
                        case MText m:
                            {
                                var p = x.Apply(m.InsertPoint.X, m.InsertPoint.Y);
                                d.Texts.Add(new StairFinder.Txt { Layer = layer, X = p.X * k, Y = p.Y * k, Text = Regex.Replace(MTextCodes.Replace(m.Value ?? "", " "), @"\s+", " ").Trim() });
                                break;
                            }
                    }
                }
                catch (Exception) { /* one odd entity does not stop the drawing */ }
            }
        }

        private static void Add(StairFinder.Drawing d, string layer, (double X, double Y) a, (double X, double Y) b, double k) =>
            d.Lines.Add(new StairFinder.Seg { Layer = layer, X0 = a.X * k, Y0 = a.Y * k, X1 = b.X * k, Y1 = b.Y * k });
    }
}
