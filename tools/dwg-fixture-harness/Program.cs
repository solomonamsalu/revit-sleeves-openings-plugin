using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using SleevesOpenings.Automation.Drawings;
namespace SleevesOpenings.Automation.Drawings
{
    public class FixtureMark { public string Floor, Code; public double X, Y; public List<(double X, double Y)> Drains = new List<(double X, double Y)>(); public string DrainHow; public (double X, double Y)? Back; }
}
struct Xf
{
    public double A, B, C, D, Tx, Ty;
    public static Xf Identity => new Xf { A = 1, D = 1 };
    public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);
    public Xf Then(Insert ins)
    {
        double r = ins.Rotation, sx = ins.XScale, sy = ins.YScale;
        double a = sx * Math.Cos(r), b = -sy * Math.Sin(r), c = sx * Math.Sin(r), d = sy * Math.Cos(r);
        var (tx, ty) = Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
        return new Xf { A = A * a + B * c, B = A * b + B * d, C = C * a + D * c, D = C * b + D * d, Tx = tx, Ty = ty };
    }
}
static class P
{
    static Regex Fx = new Regex(@"FIXT|PLUMB|PLMB|TOILET|SANIT|KITCHEN|BATH|LAV\b", RegexOptions.IgnoreCase);
    static Regex Wl = new Regex(@"WALL", RegexOptions.IgnoreCase);
    static Regex Nw = new Regex(@"TO ADD|NEW WORK|NEW-WORK", RegexOptions.IgnoreCase);
    static Regex Rm = new Regex(@"TO REMOVE|REMOVE|DEMO", RegexOptions.IgnoreCase);
    static double unit = 1; // DWG units -> inches
    class Plan { public List<FixtureDrains.Stroke> Strokes = new(); public List<((double X, double Y) A, (double X, double Y) B)> Walls = new(); public Dictionary<string, int> Layers = new(StringComparer.OrdinalIgnoreCase); public int Removed;
                 public Dictionary<string, int> Types = new(); public Dictionary<string, int> Blocks = new(); }

    static void Main(string[] args)
    {
        bool useNew = args.Length > 1 && args[1] == "new";
        var doc = DwgReader.Read(args[0]);
        string u = doc.Header.InsUnits.ToString().ToLower();
        unit = u.Contains("inch") ? 1 : u.Contains("feet") ? 12 : u.Contains("millimeter") ? 1 / 25.4 : 1;
        Console.WriteLine($"{args[0]} units={doc.Header.InsUnits} newWork={useNew}");
        var plan = new Plan();
        Walk(doc.Entities, Xf.Identity, plan, null, useNew, 0);
        Console.WriteLine($"strokes={plan.Strokes.Count} walls={plan.Walls.Count} removed={plan.Removed}; layers: " + string.Join(", ", plan.Layers.OrderByDescending(k => k.Value).Take(14).Select(k => $"{k.Key} ({k.Value})")));
        if (args.Length > 2)
        {
            Console.WriteLine("  entity types: " + string.Join(", ", plan.Types.OrderByDescending(k => k.Value).Take(30).Select(k => $"{k.Key} ({k.Value})")));
            Console.WriteLine("  blocks: " + string.Join(", ", plan.Blocks.OrderByDescending(k => k.Value).Take(40).Select(k => $"{k.Key} ({k.Value})")));
            var want = new HashSet<string> { "tub (2)", "van", "03435PLN", "15400-03435-PLN (2)", "sink-rect24x20", "09-51_X-REF-BLDG-D-LAYOUT-1$0$I_APPL_MISC_WASH-DRY STACKED_M" };
            foreach (var br in doc.BlockRecords.Where(b => want.Contains(b.Name)))
            {
                Console.WriteLine($"  blockrecord {br.Name}: {br.Entities.Count()} entities");
                foreach (var e in br.Entities)
                {
                    string d = e switch
                    {
                        Line ln => $"len={Dist((ln.StartPoint.X, ln.StartPoint.Y), (ln.EndPoint.X, ln.EndPoint.Y)):0.0}",
                        LwPolyline pl => $"verts={pl.Vertices.Count} closed={pl.IsClosed} bulges={pl.Vertices.Count(v => Math.Abs(v.Bulge) > 1e-6)} box={pl.Vertices.Max(v => v.Location.X) - pl.Vertices.Min(v => v.Location.X):0.0}x{pl.Vertices.Max(v => v.Location.Y) - pl.Vertices.Min(v => v.Location.Y):0.0}",
                        Arc a => $"r={a.Radius:0.0} sweep={(a.EndAngle - a.StartAngle) * 180 / Math.PI:0}",
                        Circle c => $"r={c.Radius:0.0}",
                        Ellipse el => $"major={Math.Sqrt(el.MajorAxisEndPoint.X * el.MajorAxisEndPoint.X + el.MajorAxisEndPoint.Y * el.MajorAxisEndPoint.Y):0.0} ratio={el.RadiusRatio:0.00} full={el.IsFullEllipse}",
                        Insert i2 => $"insert {i2.Block?.Name} [{i2.Block?.Entities?.Count()}] scale={i2.XScale},{i2.YScale} rot={i2.Rotation:0.00}",
                        _ => ""
                    };
                    Console.WriteLine($"      {e.GetType().Name,-14} layer={e.Layer?.Name,-10} {d}");
                }
            }
        }
        var edges = All.Where(q => System.Text.RegularExpressions.Regex.IsMatch(q.L, @"EDGE.?OF.?SLAB|SLAB.?EDGE", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                       .Select(q => ((q.X0, q.Y0), (q.X1, q.Y1))).ToList();
        var shapes = FixtureDrains.Extract(plan.Strokes, plan.Walls, c => c == "BT" ? (2, 7.0) : (1, 0.0), 13, 72, edges);
        foreach (var g in shapes.GroupBy(s => s.Code).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key}: {g.Count()}");
        foreach (var s in shapes.OrderBy(s => s.Code).ThenBy(s => s.Cx))
            Console.WriteLine($"  {s.Code,-9} centre ({s.Cx / 12:0.00},{s.Cy / 12:0.00}) ft  {s.X1 - s.X0:0}x{s.Y1 - s.Y0:0} in  -> {string.Join("; ", s.Points.Select(p => $"({p.X / 12:0.00},{p.Y / 12:0.00})"))}  {s.How}  back {(s.Back.HasValue ? $"({s.Back.Value.X:0},{s.Back.Value.Y:0})" : "-")}");
        int ni = Array.IndexOf(args, "near");
        if (ni >= 0)
        {
            double nx = double.Parse(args[ni + 1]) * 12, ny = double.Parse(args[ni + 2]) * 12;
            double D((string L, double X0, double Y0, double X1, double Y1) q) { double dx = q.X1 - q.X0, dy = q.Y1 - q.Y0, l2 = dx * dx + dy * dy; double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((nx - q.X0) * dx + (ny - q.Y0) * dy) / l2)); return Math.Sqrt(Math.Pow(q.X0 + t * dx - nx, 2) + Math.Pow(q.Y0 + t * dy - ny, 2)); }
            foreach (var g in All.Where(q => D(q) <= 24).GroupBy(q => q.L)) Console.WriteLine($"NEAR {g.Key}: {g.Count()} seg, nearest {g.Min(D):0.0} in; e.g. {string.Join(" | ", g.OrderBy(D).Take(3).Select(q => $"({q.X0 / 12:0.00},{q.Y0 / 12:0.00})-({q.X1 / 12:0.00},{q.Y1 / 12:0.00})"))}");
        }
        if (args.Contains("stacks"))
        {
            // Extract Stacks' finder on these shapes (no Revit pipes, no chases here)
            var rules = new SleevesOpenings.Automation.PlumbingRules();
            var fx = shapes.Select(s => new SleevesOpenings.Automation.StackFinder.Fixture
            {
                Code = s.Code == "LAV/SINK" ? (s.How.Contains("island") ? "KS" : "LAV") : s.Code, Name = s.Code, How = s.How,
                X = s.Points.Average(p => p.X) / 12, Y = s.Points.Average(p => p.Y) / 12, Points = s.Points.Select(p => (p.X / 12, p.Y / 12)).ToList(),
                Back = s.Back.HasValue ? new[] { s.Back.Value.X, s.Back.Value.Y } : null, Island = s.How.Contains("island")
            }).ToList();
            var found = SleevesOpenings.Automation.StackFinder.Find(fx, new List<((double X, double Y) A, (double X, double Y) B)>(), new List<SleevesOpenings.Automation.StackFinder.Pipe>(),
                                                                    new List<SleevesOpenings.Automation.StackFinder.Mark>(), rules, true);
            Console.WriteLine($"STACKS: {found.Stacks.Count}");
            foreach (var st in found.Stacks)
                Console.WriteLine($"  {st.Id} {st.Kind} ({st.X:0.00},{st.Y:0.00}) serves {string.Join(",", st.Serves.Select(f => f.Code))}: " +
                                  string.Join(" ", st.Sleeves.Select(v => $"{(v.Fixture != null && v.Service != v.Fixture ? v.Service + "-" + v.Fixture : v.Service)}{v.Size:0}@({v.X:0.00},{v.Y:0.00})")) +
                                  "  | " + string.Join("; ", st.Missing.Select(m => m.Split(':')[0])) + " | " + string.Join("; ", st.Notes));
        }
    }

    static int groups = 0;
    public static List<(string L, double X0, double Y0, double X1, double Y1)> All = new();
    static void Walk(IEnumerable<Entity> entities, Xf xf, Plan plan, string blockLayer, bool useNew, int depth, int group = 0)
    {
        string LayerOf(Entity e) { string own = e.Layer?.Name ?? ""; return (own == "" || own == "0") && blockLayer != null ? blockLayer : own; }
        foreach (var e in entities)
        {
            if (e is Insert ins)
            {
                string insert = LayerOf(ins);
                string bk = $"{insert}|{ins.Block?.Name}[{ins.Block?.Entities?.Count() ?? -1}]";
                plan.Blocks[bk] = plan.Blocks.TryGetValue(bk, out int bn) ? bn + 1 : 1;
                if (Rm.IsMatch(insert)) { plan.Removed++; continue; }
                if (ins.Block?.Entities != null && depth < 6)
                {
                    int first = plan.Strokes.Count, id = ++groups;
                    Walk(ins.Block.Entities, xf.Then(ins), plan, insert != "" && insert != "0" ? insert : blockLayer, useNew, depth + 1, id);
                    var own = plan.Strokes.Skip(first).Where(s => s.Group == id).ToList();
                    if (own.Count > 0 && Math.Max(own.Max(s => s.X1) - own.Min(s => s.X0), own.Max(s => s.Y1) - own.Min(s => s.Y0)) > 78)
                        foreach (var s in own) s.Group = 0;
                }
                continue;
            }
            string tk = $"{LayerOf(e)}|{e.GetType().Name}";
            plan.Types[tk] = plan.Types.TryGetValue(tk, out int tn) ? tn + 1 : 1;
            List<(double X, double Y)> pts = null; bool curved = false, closedRound = false, closed = false, straight = false;
            switch (e)
            {
                case Line ln: pts = new() { xf.Apply(ln.StartPoint.X, ln.StartPoint.Y), xf.Apply(ln.EndPoint.X, ln.EndPoint.Y) }; straight = true; break;
                case LwPolyline pl:
                    closed = pl.IsClosed; curved = pl.Vertices.Any(v => Math.Abs(v.Bulge) > 1e-6);
                    pts = curved ? Bulged(pl.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(), closed, xf) : pl.Vertices.Select(v => xf.Apply(v.Location.X, v.Location.Y)).ToList();
                    break;
                case Polyline2D p2: pts = p2.Vertices.Select(v => xf.Apply(v.Location.X, v.Location.Y)).ToList(); closed = p2.IsClosed; curved = p2.Vertices.Any(v => Math.Abs(v.Bulge) > 1e-6); break;
                case Arc a: { double s0 = a.StartAngle, s1 = a.EndAngle; while (s1 <= s0) s1 += 2 * Math.PI; pts = Sample(t => (a.Center.X + a.Radius * Math.Cos(t), a.Center.Y + a.Radius * Math.Sin(t)), s0, s1, xf); curved = true; closedRound = s1 - s0 >= 2 * Math.PI - 1e-3; break; }
                case Circle c: pts = Sample(t => (c.Center.X + c.Radius * Math.Cos(t), c.Center.Y + c.Radius * Math.Sin(t)), 0, 2 * Math.PI, xf); curved = true; closedRound = true; break;
                case Ellipse el:
                    {
                        double s0 = el.StartParameter, s1 = el.EndParameter; while (s1 <= s0) s1 += 2 * Math.PI;
                        double mjx = el.MajorAxisEndPoint.X, mjy = el.MajorAxisEndPoint.Y, mnx = -mjy * el.RadiusRatio, mny = mjx * el.RadiusRatio;
                        pts = Sample(t => (el.Center.X + mjx * Math.Cos(t) + mnx * Math.Sin(t), el.Center.Y + mjy * Math.Cos(t) + mny * Math.Sin(t)), s0, s1, xf);
                        curved = true; closedRound = s1 - s0 >= 2 * Math.PI - 1e-3; break;
                    }
                case Spline sp: pts = (sp.FitPoints.Count > 1 ? sp.FitPoints : sp.ControlPoints).Select(v => xf.Apply(v.X, v.Y)).ToList(); curved = true; closed = sp.Flags.ToString().Contains("Closed"); break;
                default: continue;
            }
            if (pts == null || pts.Count < 2) continue;
            closed = closed || closedRound || (pts.Count > 3 && Dist(pts[0], pts[pts.Count - 1]) * unit < 1.0);
            double lenIn = straight ? Dist(pts[0], pts[1]) * unit : 0;
            bool diagonal = straight && lenIn > 12 && Math.Abs(pts[1].X - pts[0].X) * unit / lenIn > 0.34 && Math.Abs(pts[1].Y - pts[0].Y) * unit / lenIn > 0.34;
            string layer = LayerOf(e);
            plan.Layers[layer] = plan.Layers.TryGetValue(layer, out int n) ? n + 1 : 1;
            for (int i = 0; i + 1 < pts.Count; i++) All.Add((layer, pts[i].X * unit, pts[i].Y * unit, pts[i + 1].X * unit, pts[i + 1].Y * unit));
            if (Rm.IsMatch(layer)) { plan.Removed++; continue; }
            bool isWall = Wl.IsMatch(layer), isFixture = Fx.IsMatch(layer);
            if (useNew && !isWall && !isFixture && Nw.IsMatch(layer)) { bool inBlock = depth > 0; isWall = straight && !diagonal && !inBlock && lenIn >= 24; isFixture = !straight || diagonal || inBlock || lenIn <= 24; }
            if (isWall) for (int i = 0; i + 1 < pts.Count; i++) plan.Walls.Add(((pts[i].X * unit, pts[i].Y * unit), (pts[i + 1].X * unit, pts[i + 1].Y * unit)));
            if (isFixture)
            {
                double x0 = pts.Min(p => p.X) * unit, y0 = pts.Min(p => p.Y) * unit, x1 = pts.Max(p => p.X) * unit, y1 = pts.Max(p => p.Y) * unit, w = x1 - x0, l = y1 - y0;
                plan.Strokes.Add(new FixtureDrains.Stroke { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Curved = curved, Closed = closed, Diagonal = diagonal, Circle = closedRound && Math.Abs(w - l) <= 0.15 * Math.Max(w, l), Group = group, Kitchen = System.Text.RegularExpressions.Regex.IsMatch(layer, @"KITCHEN|KTCH|bKITb", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
            }
        }
    }
    static List<(double X, double Y)> Sample(Func<double, (double, double)> f, double t0, double t1, Xf xf) => Enumerable.Range(0, 33).Select(i => { var (x, y) = f(t0 + (t1 - t0) * i / 32); return xf.Apply(x, y); }).ToList();
    static List<(double X, double Y)> Bulged(List<(double X, double Y, double B)> v, bool closed, Xf xf)
    {
        var o = new List<(double X, double Y)>();
        int n = closed ? v.Count : v.Count - 1;
        for (int i = 0; i < n; i++)
        {
            var a = v[i]; var b = v[(i + 1) % v.Count]; o.Add(xf.Apply(a.X, a.Y));
            if (Math.Abs(a.B) > 1e-6)
            {
                double th = 4 * Math.Atan(a.B), chord = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (chord < 1e-9) continue;
                double r = chord / (2 * Math.Sin(Math.Abs(th) / 2));
                double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2, d = Math.Sqrt(Math.Max(0, r * r - chord * chord / 4));
                double nx = -(b.Y - a.Y) / chord, ny = (b.X - a.X) / chord;
                double side = Math.Sign(a.B) * (Math.Abs(th) > Math.PI ? -1 : 1);
                double cx = mx + nx * d * side, cy = my + ny * d * side;
                double a0 = Math.Atan2(a.Y - cy, a.X - cx);
                for (int k = 1; k < 8; k++) o.Add(xf.Apply(cx + r * Math.Cos(a0 + th * k / 8), cy + r * Math.Sin(a0 + th * k / 8)));
            }
        }
        var last = v[closed ? 0 : v.Count - 1]; o.Add(xf.Apply(last.X, last.Y));
        return o;
    }
    static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
