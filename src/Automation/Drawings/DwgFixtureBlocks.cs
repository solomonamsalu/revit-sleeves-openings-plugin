using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// The fixtures of an architect's DWG as the architect drew them: named blocks ("03435PLN" a toilet, "tub (2)" a tub,
    /// "van" a vanity). Read straight from the file (Revit's view of a DWG loses the block names). Each block insert on a
    /// fixture, kitchen or new-work layer is one fixture with its name, layer and lines in the file's own units, placed by
    /// its insert (point, rotation, mirroring, nested blocks). What the name means comes from <see cref="FixtureBlockMap"/>:
    /// a named block is that fixture, a block named "none" (a door, a range) is never one, an unnamed one is listed for
    /// the user to name. A block bigger than a fixture (a floor bound into one block) is looked into. Free of the Revit API.
    /// </summary>
    public static class DwgFixtureBlocks
    {
        /// <summary>A block this big (inches) or bigger is no fixture: a counter run or a whole floor; its blocks are looked at.</summary>
        private const double MaxFixtureInches = 100;
        /// <summary>A block smaller than this (inches) is a symbol (a tag, a tick), not a fixture.</summary>
        private const double MinFixtureInches = 6;
        private const int MaxDepth = 8;

        /// <summary>One line of a fixture's drawing, in the file's units and axes (blocks already placed).</summary>
        public class Raw
        {
            public List<(double X, double Y)> Pts = new List<(double X, double Y)>();
            public bool Curved, Closed, Round, Straight, Kitchen;
        }

        public class Found
        {
            /// <summary>The block's name (a dynamic block's own name, not its *U copy).</summary>
            public string Name;
            /// <summary>The layer it is inserted on.</summary>
            public string Layer;
            /// <summary>The fixture code its name means (WC, LAV...); null = not named yet.</summary>
            public string Code;
            /// <summary>The blocks inside it (a toilet block holding the bowl block).</summary>
            public List<string> Inner = new List<string>();
            /// <summary>The block inside it that is most of it and stands for it (the toilet in a toilet-and-cleanout block); null = none.</summary>
            public string Main;
            public List<Raw> Lines = new List<Raw>();
            /// <summary>Its insert point and direction (file units): +X of the block as drawn in the file.</summary>
            public double Ix, Iy, Dx = 1, Dy;
            public bool Mirrored;
            public double X0 = double.MaxValue, Y0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue;

            internal void Grow(IEnumerable<(double X, double Y)> pts)
            {
                foreach (var p in pts) { X0 = Math.Min(X0, p.X); Y0 = Math.Min(Y0, p.Y); X1 = Math.Max(X1, p.X); Y1 = Math.Max(Y1, p.Y); }
            }
            public double Size => Lines.Count == 0 ? 0 : Math.Max(X1 - X0, Y1 - Y0);
        }

        public class Result
        {
            public List<Found> Blocks = new List<Found>();
            /// <summary>$INSUNITS of the file ("Inches"...).</summary>
            public string Units;
            /// <summary>Inches per drawing unit (from the units; 1 when unknown).</summary>
            public double InchesPerUnit = 1;
            public string Error;
        }

        public class Layers
        {
            public Regex Fixture, New, Remove, Kitchen;
            internal bool Candidate(string layer) => Is(Fixture, layer) || Is(New, layer) || Is(Kitchen, layer);
            internal static bool Is(Regex r, string layer) => r != null && layer != null && r.IsMatch(layer);
        }

        private static readonly Dictionary<string, (DateTime Stamp, CadDocument Doc)> Cache = new Dictionary<string, (DateTime, CadDocument)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The file read once per change (a linked DWG Revit keeps open is read through a shared stream).</summary>
        private static CadDocument Load(string path)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Doc;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new DwgReader(fs))
                {
                    var doc = reader.Read();
                    Cache[path] = (stamp, doc);
                    return doc;
                }
            }
        }

        /// <param name="map">Block name to fixture code ("WC", "none"...), case-insensitive.</param>
        public static Result Read(string path, Layers layers, IDictionary<string, string> map)
        {
            var result = new Result();
            try
            {
                var doc = Load(path);
                result.Units = doc.Header.InsUnits.ToString();
                result.InchesPerUnit = InchesPerUnit(result.Units);
                Walk(doc.Entities, Aff.Identity, null, 0, null, result.Blocks, layers, map, result.InchesPerUnit);
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        public static double InchesPerUnit(string units)
        {
            switch ((units ?? "").ToLowerInvariant())
            {
                case "feet": return 12;
                case "millimeters": return 1 / 25.4;
                case "centimeters": return 1 / 2.54;
                case "meters": return 1 / 0.0254;
                default: return 1;      // inches, and unitless (US architects draw in inches)
            }
        }

        /// <summary>A block's own name: a dynamic block's copy (*U12) goes by the block it was made from.</summary>
        public static string NameOf(Insert ins)
        {
            string name = ins.Block?.Name ?? "";
            if (name.StartsWith("*")) name = ins.Block?.Source?.Name ?? name;
            return name;
        }

        private static string Code(IDictionary<string, string> map, string name) =>
            map != null && name != null && map.TryGetValue(name, out var c) && !string.IsNullOrWhiteSpace(c) ? c : null;

        /// <param name="into">The fixture whose lines these are (inside its block); null = looking for fixtures.</param>
        private static void Walk(IEnumerable<Entity> entities, Aff xf, string blockLayer, int depth, Found into, List<Found> output,
                                 Layers layers, IDictionary<string, string> map, double inches)
        {
            string LayerOf(Entity e)
            {
                string own = e.Layer?.Name ?? "";
                return (own == "" || own == "0") && blockLayer != null ? blockLayer : own;
            }
            foreach (var e in entities)
            {
                string layer = LayerOf(e);
                if (Layers.Is(layers.Remove, layer)) continue;                 // what the alteration removes

                if (e is Insert ins)
                {
                    if (ins.Block?.Entities == null || depth >= MaxDepth) continue;
                    string name = NameOf(ins);
                    var inner = xf.Then(ins);
                    string innerLayer = layer != "" && layer != "0" ? layer : blockLayer;
                    if (into != null)
                    {
                        // part of a fixture: its lines are the fixture's
                        into.Inner.Add(name);
                        Walk(ins.Block.Entities, inner, innerLayer, depth + 1, into, output, layers, map, inches);
                        continue;
                    }
                    string code = Code(map, name);
                    var f = new Found { Name = name, Layer = layer, Code = code };
                    (f.Ix, f.Iy) = inner.Apply(0, 0);
                    var (ex, ey) = inner.Apply(1, 0);
                    double len = Math.Sqrt((ex - f.Ix) * (ex - f.Ix) + (ey - f.Iy) * (ey - f.Iy));
                    if (len > 1e-9) { f.Dx = (ex - f.Ix) / len; f.Dy = (ey - f.Iy) / len; }
                    f.Mirrored = inner.Det < 0;
                    Walk(ins.Block.Entities, inner, innerLayer, depth + 1, f, output, layers, map, inches);
                    double size = f.Size * inches;

                    // the blocks it is made of, each on its own
                    var parts = new List<Found>();
                    foreach (var child in ins.Block.Entities.OfType<Insert>())
                    {
                        if (Layers.Is(layers.Remove, LayerOf(child)) || child.Block?.Entities == null) continue;
                        var part = new Found { Name = NameOf(child) };
                        Walk(new Entity[] { child }, inner, innerLayer, depth + 1, part, output, layers, map, inches);
                        if (part.Lines.Count > 0) parts.Add(part);
                    }
                    int fixtureParts = parts.Count(q => q.Size * inches >= MinFixtureInches && q.Size * inches < MaxFixtureInches);

                    // a group of fixtures (a whole bathroom as one block), a counter run or a floor bound into one block:
                    // its fixtures are the blocks inside, whatever the group is called
                    if (fixtureParts >= 2 || size >= MaxFixtureInches || ins.Block.Entities.Count() > 300)
                    {
                        Walk(ins.Block.Entities, inner, innerLayer, depth + 1, null, output, layers, map, inches);
                        continue;
                    }
                    if (string.Equals(code, "none", StringComparison.OrdinalIgnoreCase)) continue;
                    if (size < MinFixtureInches) continue;

                    // one fixture; when it holds a block that is most of it (the toilet inside a toilet-and-cleanout
                    // block), that block is the fixture: its lines place the sleeve and its name tells what it is
                    var main = parts.OrderByDescending(q => q.Size).FirstOrDefault();
                    if (main != null && main.Size >= 0.6 * f.Size && main.Size < f.Size - 0.5)
                    {
                        f.Lines = main.Lines;
                        f.X0 = main.X0; f.Y0 = main.Y0; f.X1 = main.X1; f.Y1 = main.Y1;
                        f.Main = main.Name;
                        if (f.Code == null) f.Code = Code(map, main.Name);
                        if (string.Equals(f.Code, "none", StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    if (f.Code != null || layers.Candidate(layer) || parts.Count > 0) output.Add(f);
                    continue;
                }

                if (into == null) continue;                                     // loose lines: not a block fixture
                var raw = ToRaw(e, xf);
                if (raw == null || raw.Pts.Count < 2) continue;
                raw.Kitchen = Layers.Is(layers.Kitchen, layer);
                into.Lines.Add(raw);
                into.Grow(raw.Pts);
            }
        }

        private static Raw ToRaw(Entity e, Aff xf)
        {
            var r = new Raw();
            // entities drawn in an object coordinate system facing down (normal -Z, a mirrored copy) have X reversed
            switch (e)
            {
                case Line ln:
                    r.Pts.Add(xf.Apply(ln.StartPoint.X, ln.StartPoint.Y));
                    r.Pts.Add(xf.Apply(ln.EndPoint.X, ln.EndPoint.Y));
                    r.Straight = true;
                    break;
                case LwPolyline pl when pl.Vertices.Count >= 2:
                    {
                        bool flip = pl.Normal.Z < 0;
                        var v = pl.Vertices.Select(q => (X: flip ? -q.Location.X : q.Location.X, Y: q.Location.Y, B: flip ? -q.Bulge : q.Bulge)).ToList();
                        r.Closed = pl.IsClosed;
                        r.Curved = v.Any(q => Math.Abs(q.B) > 1e-6);
                        int n = r.Closed ? v.Count : v.Count - 1;
                        for (int i = 0; i < n; i++)
                        {
                            var a = v[i]; var b = v[(i + 1) % v.Count];
                            r.Pts.Add(xf.Apply(a.X, a.Y));
                            if (Math.Abs(a.B) > 1e-6) foreach (var p in Bulge(a.X, a.Y, b.X, b.Y, a.B)) r.Pts.Add(xf.Apply(p.X, p.Y));
                        }
                        var last = v[r.Closed ? 0 : v.Count - 1];
                        r.Pts.Add(xf.Apply(last.X, last.Y));
                        break;
                    }
                case Arc a:
                    {
                        double s0 = a.StartAngle, s1 = a.EndAngle;
                        while (s1 <= s0) s1 += 2 * Math.PI;
                        bool flip = a.Normal.Z < 0;
                        for (int i = 0; i <= 24; i++)
                        {
                            double t = s0 + (s1 - s0) * i / 24, x = a.Center.X + a.Radius * Math.Cos(t), y = a.Center.Y + a.Radius * Math.Sin(t);
                            r.Pts.Add(xf.Apply(flip ? -x : x, y));
                        }
                        r.Curved = true;
                        r.Closed = r.Round = s1 - s0 >= 2 * Math.PI - 1e-3;
                        break;
                    }
                case Circle c:
                    {
                        bool flip = c.Normal.Z < 0;
                        for (int i = 0; i <= 24; i++)
                        {
                            double t = 2 * Math.PI * i / 24, x = c.Center.X + c.Radius * Math.Cos(t), y = c.Center.Y + c.Radius * Math.Sin(t);
                            r.Pts.Add(xf.Apply(flip ? -x : x, y));
                        }
                        r.Curved = r.Closed = r.Round = true;
                        break;
                    }
                case Ellipse el:
                    {
                        double s0 = el.StartParameter, s1 = el.EndParameter;
                        while (s1 <= s0) s1 += 2 * Math.PI;
                        double mx = el.MajorAxisEndPoint.X, my = el.MajorAxisEndPoint.Y, k = el.Normal.Z < 0 ? -1 : 1;
                        double nx = -my * el.RadiusRatio * k, ny = mx * el.RadiusRatio * k;
                        for (int i = 0; i <= 32; i++)
                        {
                            double t = s0 + (s1 - s0) * i / 32;
                            r.Pts.Add(xf.Apply(el.Center.X + mx * Math.Cos(t) + nx * Math.Sin(t), el.Center.Y + my * Math.Cos(t) + ny * Math.Sin(t)));
                        }
                        r.Curved = true;
                        r.Closed = r.Round = s1 - s0 >= 2 * Math.PI - 1e-3;
                        break;
                    }
                case Spline sp:
                    {
                        var pts = sp.FitPoints.Count > 1 ? sp.FitPoints.ToList() : sp.ControlPoints.ToList();
                        foreach (var p in pts) r.Pts.Add(xf.Apply(p.X, p.Y));
                        r.Curved = true;
                        r.Closed = sp.Flags.ToString().Contains("Closed");
                        break;
                    }
                case Polyline2D p2:
                    {
                        bool flip = p2.Normal.Z < 0;
                        foreach (var q in p2.Vertices) r.Pts.Add(xf.Apply(flip ? -q.Location.X : q.Location.X, q.Location.Y));
                        r.Closed = p2.IsClosed;
                        if (r.Closed && r.Pts.Count > 0) r.Pts.Add(r.Pts[0]);
                        break;
                    }
                default: return null;
            }
            if (!r.Closed && r.Pts.Count > 3)
            {
                var a = r.Pts[0]; var b = r.Pts[r.Pts.Count - 1];
                r.Closed = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 1e-3 * (1 + Math.Abs(a.X));
            }
            return r;
        }

        /// <summary>Points along a polyline segment's arc (bulge = tan of a quarter of its angle), the ends left out.</summary>
        private static IEnumerable<(double X, double Y)> Bulge(double ax, double ay, double bx, double by, double bulge)
        {
            double th = 4 * Math.Atan(bulge), chord = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (chord < 1e-9) yield break;
            double r = chord / (2 * Math.Sin(Math.Abs(th) / 2));
            double mx = (ax + bx) / 2, my = (ay + by) / 2, d = Math.Sqrt(Math.Max(0, r * r - chord * chord / 4));
            double nx = -(by - ay) / chord, ny = (bx - ax) / chord;
            double side = Math.Sign(bulge) * (Math.Abs(th) > Math.PI ? -1 : 1);
            double cx = mx + nx * d * side, cy = my + ny * d * side, a0 = Math.Atan2(ay - cy, ax - cx);
            for (int k = 1; k < 8; k++) yield return (cx + r * Math.Cos(a0 + th * k / 8), cy + r * Math.Sin(a0 + th * k / 8));
        }

        /// <summary>2D affine transform: block coordinates to the file's model space.</summary>
        internal struct Aff
        {
            public double A, B, C, D, Tx, Ty;
            public static Aff Identity => new Aff { A = 1, D = 1 };
            public double Det => A * D - B * C;
            public (double X, double Y) Apply(double x, double y) => (A * x + B * y + Tx, C * x + D * y + Ty);

            /// <summary>This transform after an insert: its base point, scale, rotation, insert point, and its OCS (normal -Z mirrors X).</summary>
            public Aff Then(Insert ins)
            {
                double r = ins.Rotation, sx = ins.XScale, sy = ins.YScale, cos = Math.Cos(r), sin = Math.Sin(r);
                var bp = ins.Block?.BlockEntity?.BasePoint ?? new CSMath.XYZ(0, 0, 0);
                // block point p -> insert OCS: R * S * (p - base) + insert
                var m = new Aff { A = sx * cos, B = -sy * sin, C = sx * sin, D = sy * cos };
                m.Tx = ins.InsertPoint.X - (m.A * bp.X + m.B * bp.Y);
                m.Ty = ins.InsertPoint.Y - (m.C * bp.X + m.D * bp.Y);
                if (ins.Normal.Z < 0) { m.A = -m.A; m.B = -m.B; m.Tx = -m.Tx; }       // OCS facing down: X reversed
                return new Aff
                {
                    A = A * m.A + B * m.C, B = A * m.B + B * m.D,
                    C = C * m.A + D * m.C, D = C * m.B + D * m.D,
                    Tx = A * m.Tx + B * m.Ty + Tx, Ty = C * m.Tx + D * m.Ty + Ty
                };
            }
        }
    }

    /// <summary>
    /// What each architect's block is (block name to fixture code: "03435PLN" = WC, "van" = LAV, "door-36" = none). Kept for
    /// the office in %APPDATA%\SleevesOpenings\fixture-blocks.json: architects reuse their blocks, so a name told once
    /// serves every floor and project. <see cref="Suggest"/> proposes a code from the name for the user to confirm.
    /// </summary>
    public static class FixtureBlockMap
    {
        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SleevesOpenings", "fixture-blocks.json");

        public static Dictionary<string, string> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var read = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(FilePath));
                    if (read != null) return new Dictionary<string, string>(read, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static void Save(Dictionary<string, string> map)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var sorted = map.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(FilePath, Newtonsoft.Json.JsonConvert.SerializeObject(sorted, Newtonsoft.Json.Formatting.Indented));
        }

        private static readonly (Regex Rx, string Code)[] Hints =
        {
            (new Regex(@"toilet|water.?closet|\bwc\b|commode", RegexOptions.IgnoreCase), "WC"),
            (new Regex(@"door|\bdr\b|window|wdw|range|oven|cook|stove|ref|fridge|dish|\bdw\b|micro|hood|chair|table|bed|sofa|desk|closet|shelf|tag|symbol|north|arrow|column|stair|elev", RegexOptions.IgnoreCase), "none"),
            (new Regex(@"\btubs?\b|bathtub|bath.?tub|\btub\s*\(", RegexOptions.IgnoreCase), "BT"),
            (new Regex(@"shower|\bsh\b", RegexOptions.IgnoreCase), "SH"),
            (new Regex(@"wash|dryer|w.?d\b|laundry", RegexOptions.IgnoreCase), "W/D"),
            (new Regex(@"van|lav|basin", RegexOptions.IgnoreCase), "LAV"),
            (new Regex(@"sink", RegexOptions.IgnoreCase), "KS"),
            (new Regex(@"floor.?drain|\bfd\b", RegexOptions.IgnoreCase), "FD"),
        };

        /// <summary>A code the name suggests (a hint, never enough alone), else null.</summary>
        public static string Suggest(string name)
        {
            foreach (var (rx, code) in Hints) if (rx.IsMatch(name ?? "")) return code;
            return null;
        }

        /// <summary>Two codes name the same kind of fixture: equal, any two sinks, tub and shower, the washers.</summary>
        public static bool Same(string a, string b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            var sinks = new[] { "LAV", "KS", "LS" };
            var wet = new[] { "BT", "SH" };
            var washers = new[] { "W/D", "WD" };
            return (sinks.Contains(a) && sinks.Contains(b)) || (wet.Contains(a) && wet.Contains(b)) || (washers.Contains(a) && washers.Contains(b));
        }
    }
}
