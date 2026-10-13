using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation;

// Runs DeadZones.FromLines (the add-in's own code) on a DWG's wall lines outside Revit, with stand-ins for the Revit types.
// dotnet run -c Release -- "<floor>.dwg" <xFt> <yFt> <diameterIn> [maxGapIn]
namespace Autodesk.Revit.DB
{
    public class XYZ
    {
        public double X, Y, Z;
        public XYZ(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double DistanceTo(XYZ o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
    }
}
namespace SleevesOpenings.Placement
{
    public class WallInfo { public string Label; }
    public class WallGeometry
    {
        public Zone DeadZone(XYZ p, double r, double maxGap) => null;
        public class Zone
        {
            public XYZ Origin, Normal; public double Half, Gap = -1; public List<WallInfo> Walls = new List<WallInfo>(); public string Where;
            public double Offset(XYZ p) => (p.X - Origin.X) * Normal.X + (p.Y - Origin.Y) * Normal.Y;
            public XYZ Centre(XYZ p) { double o = Offset(p); return new XYZ(p.X - Normal.X * o, p.Y - Normal.Y * o, 0); }
            public string Describe() => Where;
        }
    }
}
namespace SleevesOpenings.Automation
{
    public static class DwgFixtureReader { public class Plan { public List<((double X, double Y) A, (double X, double Y) B)> Walls = new(); } }
}

static class P
{
    static readonly Regex Wall = new Regex("WALL", RegexOptions.IgnoreCase);
    static void Main(string[] a)
    {
        var doc = DwgReader.Read(a[0]);
        var lines = new List<((double X, double Y) A, (double X, double Y) B)>();
        Walk(doc.Entities, (0, 0), 0, lines);
        double x = double.Parse(a[1]), y = double.Parse(a[2]), r = double.Parse(a[3]) / 24, gap = (a.Length > 4 ? double.Parse(a[4]) : 12) / 12;
        var p = new XYZ(x, y, 0);
        // the truth: which strips across the wall the architect hatched (A-WALL-PATT), from the vertical (or horizontal) through p
        var hatches = doc.Entities.OfType<Hatch>().Where(h => Wall.IsMatch(h.Layer?.Name ?? "")).ToList();
        bool vert = a.Length > 5 && a[5] == "v";   // the wall runs along Y: look across in X
        var across = lines.Where(l => vert ? Math.Abs(l.A.X - l.B.X) > 6 ? false : Math.Min(l.A.Y, l.B.Y) <= y * 12 && Math.Max(l.A.Y, l.B.Y) >= y * 12
                                          : Math.Abs(l.A.Y - l.B.Y) < 1e-3 && Math.Min(l.A.X, l.B.X) <= x * 12 && Math.Max(l.A.X, l.B.X) >= x * 12)
                          .Select(l => vert ? l.A.X : l.A.Y).Where(o => Math.Abs(o - (vert ? x : y) * 12) <= 30).Distinct().OrderBy(o => o).ToList();
        for (int i = 0; i < across.Count; i++)
        {
            string fill = "";
            if (i + 1 < across.Count)
            {
                double m = (across[i] + across[i + 1]) / 2;
                var q = vert ? (m, y * 12) : (x * 12, m);
                fill = hatches.Any(h => Inside(h, q)) ? "  ### hatched" : "  ... empty";
                Console.WriteLine($"   line {across[i] - (vert ? x : y) * 12,7:0.00}\"   next {across[i + 1] - across[i]:0.00}\"{fill}");
            }
            else Console.WriteLine($"   line {across[i] - (vert ? x : y) * 12,7:0.00}\"");
        }
        var z = DeadZones.FromLines(lines, p, r, gap);
        if (z == null) { Console.WriteLine($"({x},{y}) {a[3]}\": no zone ({lines.Count} wall lines)"); return; }
        var to = z.Centre(p);
        Console.WriteLine($"({x},{y}) {a[3]}\": -> ({to.X:0.000},{to.Y:0.000}) moved {to.DistanceTo(p) * 12:0.0}\"  half {z.Half * 12:0.0}\"  {z.Where}");
    }
    static bool Inside(Hatch h, (double X, double Y) q)
    {
        int crossings = 0;
        foreach (var path in h.Paths)
        {
            var seg = new List<((double X, double Y), (double X, double Y))>();
            foreach (var e in path.Edges)
            {
                if (e is Hatch.BoundaryPath.Line ln) seg.Add(((ln.Start.X, ln.Start.Y), (ln.End.X, ln.End.Y)));
                else if (e is Hatch.BoundaryPath.Polyline pl)
                {
                    var v = pl.Vertices.Select(t => (t.X, t.Y)).ToList();
                    if (pl.IsClosed && v.Count > 0) v.Add(v[0]);
                    for (int i = 0; i + 1 < v.Count; i++) seg.Add((v[i], v[i + 1]));
                }
            }
            foreach (var (a, b) in seg)
                if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < a.X + (q.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) crossings++;
        }
        return crossings % 2 == 1;
    }
    static void Walk(IEnumerable<Entity> es, (double X, double Y) off, int depth, List<((double, double), (double, double))> lines)
    {
        foreach (var e in es)
        {
            if (e is Insert ins && ins.Block?.Entities != null && depth < 4 && ins.Rotation == 0 && ins.XScale == 1 && ins.YScale == 1)
            { Walk(ins.Block.Entities, (off.X + ins.InsertPoint.X, off.Y + ins.InsertPoint.Y), depth + 1, lines); continue; }
            if (!Wall.IsMatch(e.Layer?.Name ?? "")) continue;
            if (e is Line l) lines.Add(((l.StartPoint.X + off.X, l.StartPoint.Y + off.Y), (l.EndPoint.X + off.X, l.EndPoint.Y + off.Y)));
            else if (e is LwPolyline pl)
            {
                var v = pl.Vertices.Select(q => (q.Location.X + off.X, q.Location.Y + off.Y)).ToList();
                if (pl.IsClosed) v.Add(v[0]);
                for (int i = 0; i + 1 < v.Count; i++) lines.Add((v[i], v[i + 1]));
            }
        }
    }
}
