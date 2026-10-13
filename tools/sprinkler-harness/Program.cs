// Sprinkler Placer test bench: runs the real StairFinder (src/Automation/StairFinder.cs) on per-floor architectural DWGs.
// usage: sph <folder of floor DWGs> [--lowest <file>] [--skip <regex>] [--office x y] [--corner nearDoor|awayFromDoor] [--dump file x y r]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp.IO;
using SleevesOpenings.Automation;
using SleevesOpenings.Automation.Drawings;

static class P
{
    static void Main(string[] args)
    {
        var rules = new SprinklerPlacerRules();
        int ci = Array.IndexOf(args, "--corner"); if (ci >= 0) rules.Corner = args[ci + 1];
        int si = Array.IndexOf(args, "--standpipe"); if (si >= 0) rules.Standpipe = args[si + 1];
        double clear = 5.5, sleeve = 5;
        var files = Directory.GetFiles(args[0], "*.dwg").Where(f => !Regex.IsMatch(Path.GetFileName(f), "GRID", RegexOptions.IgnoreCase)).OrderBy(f => f).ToList();
        var skip = new Regex(rules.SkipLevels, RegexOptions.IgnoreCase);
        var floors = new List<StairFinder.Floor>();
        foreach (var f in files)
        {
            var cad = DwgReader.Read(f);
            string u = cad.Header.InsUnits.ToString().ToLowerInvariant();
            double k = u.Contains("inch") ? 1.0 / 12 : u.Contains("feet") || u.Contains("foot") ? 1 : u.Contains("milli") ? 1 / 304.8 : 1.0 / 12;
            var fl = new StairFinder.Floor { Level = Path.GetFileNameWithoutExtension(f), Elevation = files.IndexOf(f) };
            fl.Drawings.Add(DwgFlat.Read(cad, Path.GetFileName(f), k));
            if (f == files[0]) fl.Skip = "lowest level: slab on grade";
            else if (skip.IsMatch(fl.Level)) fl.Skip = "bulkhead roof";
            floors.Add(fl);
        }
        string map = Environment.GetEnvironmentVariable("SPH_MAP");
        string current = null;
        if (map != null)
            StairFinder.Debug = (st, l, wall, fill, leak) =>
            {
                if (current == null || !current.Contains(map)) return;
                int nu = wall.GetLength(0), nv = wall.GetLength(1);
                Console.WriteLine($"-- map {current} {l.End} leak={leak} box u {l.GetType()} ({nu}x{nv}) 3\" per char, top = far end");
                for (int j = nv - 1; j >= 0; j -= 3)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < nu; i += 3)
                    {
                        bool w = false, f = false;
                        for (int a = i; a < Math.Min(nu, i + 3); a++) for (int b = Math.Max(0, j - 2); b <= j; b++) { w |= wall[a, b]; f |= fill[a, b]; }
                        sb.Append(w ? '#' : f ? '.' : ' ');
                    }
                    Console.WriteLine(sb.ToString());
                }
            };
        foreach (var fl in floors) { current = fl.Level; foreach (var d in fl.Drawings) fl.Stairs.AddRange(StairFinder.Find(d, rules, clear, sleeve, fl.Notes)); }
        foreach (var fl in floors) fl.Stairs.Clear();
        StairFinder.Debug = null;
        var plan = StairFinder.Risers(floors, rules, clear, sleeve, 8);
        foreach (var fl in floors)
        {
            Console.WriteLine($"== {fl.Level}{(fl.Skip != null ? "  [no sleeve: " + fl.Skip + "]" : "")}");
            foreach (var n in fl.Notes) Console.WriteLine("   " + n);
            foreach (var s in fl.Stairs)
            {
                foreach (var n in s.Notes) Console.WriteLine("   note: " + n);
                foreach (var l in s.Landings)
                {
                    Console.WriteLine($"   {l.End} landing {l.Name} enclosed={l.Enclosed} area={l.Area:0.0}sf hint={l.Hint} doors={string.Join(" ", l.Doors.Select(d => $"[hinge ({d.Hx:0.00},{d.Hy:0.00}) R{d.R * 12:0} closed->({d.Cx:0.00},{d.Cy:0.00})]"))}");
                    foreach (var c in l.Corners) Console.WriteLine($"      corner ({c.X:0.000},{c.Y:0.000}) {c.Where}");
                }
            }
        }
        Console.WriteLine("== RISERS");
        foreach (var r in plan.Risers)
        {
            Console.WriteLine($"   {r.Name} {r.Kind} '{r.Label}' {r.Pipe}\" at ({r.X:0.000},{r.Y:0.000}) review={r.Review}  options={r.Options.Count}" + (r.Drain > 0 ? $"  drain {r.Drain}\" at ({r.DrainX:0.000},{r.DrainY:0.000})" : ""));
            foreach (var n in r.Notes) Console.WriteLine("      " + n);
            foreach (var o in r.Options.Take(8)) Console.WriteLine($"      option ({o.Corner.X:0.000},{o.Corner.Y:0.000}) floors={o.Floors} {o.Corner.Where}  blocked on: {string.Join(", ", floors.Where(f => f.Skip == null && f.Stairs.Count > 0 && !f.Stairs.Any(st => st.Allows(o.Corner.X, o.Corner.Y))).Select(f => f.Level))}");
        }
        foreach (var n in plan.Notes) Console.WriteLine("   " + n);
        int oi = Array.IndexOf(args, "--office");
        if (oi >= 0 && plan.Risers.Count > 0)
        {
            double ox = double.Parse(args[oi + 1]), oy = double.Parse(args[oi + 2]);
            var r = plan.Risers[0];
            Console.WriteLine($"== office sleeve ({ox},{oy}): {r.Name} is {Math.Sqrt(Math.Pow(r.X - ox, 2) + Math.Pow(r.Y - oy, 2)) * 12:0.0}\" away (dx {(r.X - ox) * 12:0.0}\", dy {(r.Y - oy) * 12:0.0}\")");
            foreach (var o in r.Options.Select((o, i) => (o, i)).OrderBy(x => Math.Pow(x.o.Corner.X - ox, 2) + Math.Pow(x.o.Corner.Y - oy, 2)).Take(1))
                Console.WriteLine($"   nearest option #{o.i + 1}: ({o.o.Corner.X:0.000},{o.o.Corner.Y:0.000}) {Math.Sqrt(Math.Pow(o.o.Corner.X - ox, 2) + Math.Pow(o.o.Corner.Y - oy, 2)) * 12:0.0}\" away");
        }
    }
}
