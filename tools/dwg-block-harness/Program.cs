using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SleevesOpenings.Automation.Drawings;
namespace SleevesOpenings.Automation.Drawings
{
    public class FixtureMark { public string Floor, Code; public double X, Y; public List<(double X, double Y)> Drains = new List<(double X, double Y)>(); public string DrainHow; }
}
static class P
{
    static void Main(string[] args)
    {
        var layers = new DwgFixtureBlocks.Layers
        {
            Fixture = new Regex(@"FIXT|PLUMB|PLMB|TOILET|SANIT|KITCHEN|BATH|LAV\b", RegexOptions.IgnoreCase),
            New = new Regex(@"TO ADD|NEW WORK|NEW-WORK", RegexOptions.IgnoreCase),
            Remove = new Regex(@"TO REMOVE|REMOVE|DEMO", RegexOptions.IgnoreCase),
            Kitchen = new Regex(@"KITCHEN|KTCH|\bKIT\b", RegexOptions.IgnoreCase),
        };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++) { var kv = args[i].Split('='); map[kv[0]] = kv[1]; }
        var r = DwgFixtureBlocks.Read(args[0], layers, map);
        Console.WriteLine($"{System.IO.Path.GetFileName(args[0])}: units {r.Units}, {r.Blocks.Count} block fixtures, error {r.Error}");
        foreach (var g in r.Blocks.GroupBy(b => b.Name).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-40} x{g.Count(),-3} code={g.First().Code ?? "-",-5} layer={g.First().Layer,-20} size {g.First().X1 - g.First().X0:0}x{g.First().Y1 - g.First().Y0:0} inner: {string.Join(",", g.First().Inner.Distinct())}");
        Console.WriteLine();
        foreach (var b in r.Blocks.Where(b => b.Code != null && b.Code != "none").OrderBy(b => b.Code).ThenBy(b => b.X0))
        {
            var strokes = b.Lines.Select(l =>
            {
                double x0 = l.Pts.Min(p => p.X), y0 = l.Pts.Min(p => p.Y), x1 = l.Pts.Max(p => p.X), y1 = l.Pts.Max(p => p.Y);
                bool diag = l.Straight && Math.Abs(l.Pts[1].X - l.Pts[0].X) > 0.34 * Math.Max(12, Math.Abs(l.Pts[1].X - l.Pts[0].X) + Math.Abs(l.Pts[1].Y - l.Pts[0].Y)) && Math.Abs(l.Pts[1].Y - l.Pts[0].Y) > 4;
                return new FixtureDrains.Stroke { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Curved = l.Curved, Closed = l.Closed, Circle = l.Round && Math.Abs((x1 - x0) - (y1 - y0)) <= 0.15 * Math.Max(x1 - x0, y1 - y0), Kitchen = l.Kitchen };
            }).ToList();
            var (pts, how) = FixtureDrains.LocateKnown(b.Code, strokes, new List<((double, double), (double, double))>(), b.Code == "BT" ? (2, 7.0) : (1, 0.0));
            Console.WriteLine($"  {b.Code,-4} {b.Name,-24} box ({b.X0 / 12:0.00},{b.Y0 / 12:0.00})-({b.X1 / 12:0.00},{b.Y1 / 12:0.00}) ft mirrored={b.Mirrored} dir=({b.Dx:0.##},{b.Dy:0.##}) -> {(pts == null ? "none" : string.Join("; ", pts.Select(p => $"({p.X / 12:0.00},{p.Y / 12:0.00})")))}  {how}");
        }
    }
}
