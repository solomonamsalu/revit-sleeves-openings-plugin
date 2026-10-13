using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// rules.json "sprinklerPlacer": the Sprinkler Placer lays out the standpipe / sprinkler riser sleeves from the stairs
    /// alone (no pipes are modelled before the sleeves). Clearances come from the manual (systems.standpipe: 5 1/2" pipe
    /// centre to wall, 8" c-c); the sleeve from sprinkler.sleeveOverPipe / sleeveSizes.
    /// </summary>
    public class SprinklerPlacerRules
    {
        /// <summary>Sprinkler-only riser pipe (inches): 24 Skillman, 1299 Findlay 3" (5" sleeves).</summary>
        [JsonProperty("pipe")] public double Pipe { get; set; } = 3;
        /// <summary>Standpipe building: each stair's riser pipe (2258 Morris: 4" combined and 4" standpipe, 6" sleeves).</summary>
        [JsonProperty("standpipePipe")] public double StandpipePipe { get; set; } = 4;
        /// <summary>The combined riser's drain (FCVA drain), its own sleeve "at the back" (2258 Morris: 2", Fire 4); 0 = none.</summary>
        [JsonProperty("drainPipe")] public double DrainPipe { get; set; } = 2;
        /// <summary>"auto" (taller than standpipeHeight), "yes", "no": a standpipe in every stair (manual p16: one Combined, one Standpipe Only).</summary>
        [JsonProperty("standpipe")] public string Standpipe { get; set; } = "auto";
        /// <summary>Feet from the ground floor to the top occupied floor above which the building has standpipes (NYC: 75 ft).</summary>
        [JsonProperty("standpipeHeight")] public double StandpipeHeight { get; set; } = 75;
        /// <summary>Levels below grade (not counted in the height): cellar, basement.</summary>
        [JsonProperty("belowGrade")] public string BelowGrade { get; set; } = @"CEL|BASEMENT|\bSUB";
        /// <summary>Which corner of the landing: "awayFromDoor" (24 Skillman, 2258 Morris video) or "nearDoor" (1299 Findlay: beside the door, toward the corridor).</summary>
        [JsonProperty("corner")] public string Corner { get; set; } = "awayFromDoor";
        /// <summary>Layers whose parallel lines of one length are a flight's treads.</summary>
        [JsonProperty("stairLayers")] public string StairLayers { get; set; } = @"STAIR|STRS|TREAD";
        [JsonProperty("wallLayers")] public string WallLayers { get; set; } = @"WALL(?!.*(PATT|HATCH))|FNDN|CONC|SHEAR";
        [JsonProperty("doorLayers")] public string DoorLayers { get; set; } = @"DOOR";
        [JsonProperty("columnLayers")] public string ColumnLayers { get; set; } = @"COL";
        /// <summary>Words on the architect's plan marking the fire riser's landing (not F.P.S.C.: that is a fireproof self-closing door).</summary>
        [JsonProperty("hintWords")] public string HintWords { get; set; } = @"STAND\s*PIPE|SPRINKLER|FIRE\s*RISER|F\.?D\.?V|HOSE\s*VALVE";
        /// <summary>A stair's name on the plan ("STAIR A", "STAIR 1").</summary>
        [JsonProperty("stairNames")] public string StairNames { get; set; } = @"\bSTAIRS?\s*(#\s*)?[A-Z0-9]{1,2}\b";
        /// <summary>Levels that get no riser sleeve: the bulkhead roof (the riser ends under it).</summary>
        [JsonProperty("skipLevels")] public string SkipLevels { get; set; } = @"BULK\s*HEAD|\bB\.?H\.?\b";
        /// <summary>Inches clear of a column face (structural engineer: no slab penetration within 1'-0").</summary>
        [JsonProperty("columnClear")] public double ColumnClear { get; set; } = 12;
        /// <summary>Inches clear of a door's swing.</summary>
        [JsonProperty("doorClear")] public double DoorClear { get; set; } = 1;
        /// <summary>A landing is looked for this far past the flights (inches).</summary>
        [JsonProperty("maxLanding")] public double MaxLanding { get; set; } = 144;
        /// <summary>No walls drawn round the landing: it is taken this deep (inches), flagged for review.</summary>
        [JsonProperty("fallbackLanding")] public double FallbackLanding { get; set; } = 60;
    }

    /// <summary>
    /// Finds the stairs on a floor's drawings (DWG lines, Revit stairs turned into lines) and the corners of their landings
    /// a riser sleeve can take (Standards Manual p16: in the stairwell, 5 1/2" pipe centre to any wall, same spot top to
    /// bottom), then the risers of the building over all floors. No Revit API: the harness runs it on the DWG files.
    /// Units: feet, plan coordinates.
    /// </summary>
    public static class StairFinder
    {
        public class Seg { public string Layer; public double X0, Y0, X1, Y1; }
        /// <summary>An arc: centre, radius and its two end points (from which way it turns, whatever the drawing's mirroring).</summary>
        public class Swing { public string Layer; public double Cx, Cy, R, Ax, Ay, Bx, By; }
        public class Txt { public string Layer, Text; public double X, Y; }

        public class Drawing
        {
            public string Source;
            public List<Seg> Lines = new List<Seg>();
            public List<Swing> Arcs = new List<Swing>();
            public List<Txt> Texts = new List<Txt>();
        }

        /// <summary>Plan axes of a stair: u across the flights (along a tread), v along the run.</summary>
        public class Frame
        {
            public double Cos, Sin;
            public (double U, double V) Local(double x, double y) => (x * Cos + y * Sin, -x * Sin + y * Cos);
            public (double X, double Y) World(double u, double v) => (u * Cos - v * Sin, u * Sin + v * Cos);
        }

        public class Flight { public double U0, U1, V0, V1, Rise; public int Treads; }

        public class Door
        {
            /// <summary>Hinge (the arc's centre), radius, the leaf closed across the opening (to Cx, Cy) and the swing's other end.</summary>
            public double Hx, Hy, R, Cx, Cy, Ox, Oy;
            public double Mx => (Hx + Cx) / 2;
            public double My => (Hy + Cy) / 2;
        }

        public class Corner
        {
            public double X, Y;
            /// <summary>Inches from the centre to the two walls of the corner.</summary>
            public double ToWallU, ToWallV;
            public Landing Landing;
            /// <summary>Feet to the nearest door opening of its landing (-1 = no door).</summary>
            public double ToDoor = -1;
            public string Where;
        }

        public class Landing
        {
            public Stair Stair;
            /// <summary>"low" or "high" end of the run.</summary>
            public string End;
            public string Name;
            /// <summary>Walls close it all round (else a rectangle FallbackLanding deep, flagged).</summary>
            public bool Enclosed;
            public List<Door> Doors = new List<Door>();
            /// <summary>Words marking the fire riser here (F.P.S.C.), and where.</summary>
            public string Hint;
            public double HintX, HintY;
            public List<Corner> Corners = new List<Corner>();
            // the free area, 1" cells in the stair's frame: a riser centre may sit on an ok cell
            internal double U0, V0;
            internal int Nu, Nv;
            internal bool[,] Ok, Fill;
            public double Area;

            /// <summary>Feet from a point to the landing's floor (0 on it; large when far).</summary>
            internal double Distance(double x, double y)
            {
                var (u, v) = Stair.Frame.Local(x, y);
                int i = (int)Math.Round((u - U0) / Cell), j = (int)Math.Round((v - V0) / Cell);
                const int reach = 24;
                double best = 99;
                for (int a = Math.Max(0, i - reach); a <= Math.Min(Nu - 1, i + reach); a++)
                    for (int b = Math.Max(0, j - reach); b <= Math.Min(Nv - 1, j + reach); b++)
                        if (Fill[a, b]) best = Math.Min(best, Math.Sqrt((a - i) * (a - i) + (b - j) * (b - j)) * Cell);
                return best;
            }

            internal bool Allows(double x, double y)
            {
                var (u, v) = Stair.Frame.Local(x, y);
                int i = (int)Math.Round((u - U0) / Cell), j = (int)Math.Round((v - V0) / Cell);
                for (int di = -1; di <= 1; di++)
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        int a = i + di, b = j + dj;
                        if (a >= 0 && b >= 0 && a < Nu && b < Nv && Ok[a, b]) return true;
                    }
                return false;
            }
        }

        public class Stair
        {
            public Frame Frame;
            public double U0, U1, V0, V1;
            public List<Flight> Flights = new List<Flight>();
            public List<Landing> Landings = new List<Landing>();
            public string Name;
            public List<string> Notes = new List<string>();
            /// <summary>Its run's box in plan (feet): to match the same stair floor to floor.</summary>
            public double MinX, MinY, MaxX, MaxY;
            public bool Allows(double x, double y) => Landings.Any(l => l.Allows(x, y));
            public string Describe() => $"{(Name ?? "stair")} ({Flights.Count} flight(s), {Math.Round((U1 - U0) * 12):0}\" x {Math.Round((V1 - V0) * 12):0}\", " +
                                         $"{string.Join(", ", Landings.Select(l => $"{l.End} landing{(l.Name != null ? " " + l.Name : "")} {(l.Enclosed ? "walled" : "no walls drawn")}, {l.Doors.Count} door(s), {l.Corners.Count} corner(s)"))})";
        }

        private const double Cell = 1.0 / 12;
        /// <summary>Test bench: sees each landing's walls and fill before the corners are taken.</summary>
        public static Action<Stair, Landing, bool[,], bool[,], bool> Debug;
        private const double Tol = 2.0 / 12;

        /// <param name="clear">Manual: pipe centre to any wall (inches).</param>
        /// <param name="sleeve">The sleeve's diameter (inches).</param>
        public static List<Stair> Find(Drawing d, SprinklerPlacerRules rules, double clear, double sleeve, List<string> notes)
        {
            var stairLayers = Rx(rules.StairLayers); var wallLayers = Rx(rules.WallLayers); var doorLayers = Rx(rules.DoorLayers); var colLayers = Rx(rules.ColumnLayers);
            var stairs = new List<Stair>();
            foreach (var group in TreadLines(d, stairLayers).GroupBy(t => t.Angle))
            {
                var frame = new Frame { Cos = Math.Cos(group.Key), Sin = Math.Sin(group.Key) };
                var flights = Flights(group.Select(t => t.Seg).ToList(), frame);
                foreach (var cluster in ClusterFlights(flights))
                {
                    var s = new Stair
                    {
                        Frame = frame, Flights = cluster,
                        U0 = cluster.Min(f => f.U0), U1 = cluster.Max(f => f.U1), V0 = cluster.Min(f => f.V0), V1 = cluster.Max(f => f.V1)
                    };
                    var corners = new[] { frame.World(s.U0, s.V0), frame.World(s.U1, s.V0), frame.World(s.U0, s.V1), frame.World(s.U1, s.V1) };
                    s.MinX = corners.Min(c => c.X); s.MaxX = corners.Max(c => c.X); s.MinY = corners.Min(c => c.Y); s.MaxY = corners.Max(c => c.Y);
                    stairs.Add(s);
                }
            }
            // the same stair found at two angles (a 90 degree pair of tread groups) or twice: keep the one with more treads
            stairs = stairs.OrderByDescending(s => s.Flights.Sum(f => f.Treads)).ToList();
            for (int i = stairs.Count - 1; i >= 0; i--)
                if (stairs.Take(i).Any(o => Overlap(o, stairs[i]) > 0.5)) stairs.RemoveAt(i);

            var hint = Rx(rules.HintWords); var names = Rx(rules.StairNames);
            foreach (var s in stairs)
            {
                foreach (var end in new[] { "low", "high" })
                {
                    var l = FindLanding(d, s, end, rules, wallLayers, doorLayers, colLayers, clear, sleeve);
                    if (l != null) s.Landings.Add(l);
                }
                foreach (var l in s.Landings)
                {
                    // the words on the landing: its stair's name, the fire riser's mark
                    foreach (var t in d.Texts.OrderBy(t => l.Distance(t.X, t.Y)))
                    {
                        if (l.Distance(t.X, t.Y) > 1.5) break;
                        var n = names?.Match(t.Text ?? "");
                        if (n != null && n.Success && l.Name == null) l.Name = Regex.Replace(n.Value.ToUpperInvariant(), @"\s+", " ");
                        if (hint != null && hint.IsMatch(t.Text ?? "") && l.Hint == null) { l.Hint = t.Text.Trim(); l.HintX = t.X; l.HintY = t.Y; }
                    }
                }
                var stairNames = s.Landings.Select(l => l.Name).Where(n => n != null).Distinct().ToList();
                if (stairNames.Count == 0)
                    foreach (var t in d.Texts)
                    {
                        var (u, v) = s.Frame.Local(t.X, t.Y);
                        var n = names?.Match(t.Text ?? "");
                        if (n != null && n.Success && u > s.U0 - 3 && u < s.U1 + 3 && v > s.V0 - 3 && v < s.V1 + 3) { stairNames.Add(Regex.Replace(n.Value.ToUpperInvariant(), @"\s+", " ")); break; }
                    }
                if (stairNames.Count > 0)
                    s.Name = stairNames.Count == 1 ? stairNames[0] : "STAIR " + string.Join("/", stairNames.Select(n => Regex.Replace(n, @"^STAIRS?\s*(#\s*)?", "")));
            }
            notes?.Add(stairs.Count == 0 ? $"{d.Source}: no stair found (no run of treads on the stair layers '{rules.StairLayers}')"
                                         : $"{d.Source}: {stairs.Count} stair(s): " + string.Join("; ", stairs.Select(s => s.Describe())));
            return stairs;
        }

        private static Regex Rx(string p) => string.IsNullOrWhiteSpace(p) ? null : new Regex(p, RegexOptions.IgnoreCase);

        /// <summary>Lines on the stair layers a tread long (28" to 6'), by their angle (to 1 degree, 0..180).</summary>
        private static List<(double Angle, Seg Seg)> TreadLines(Drawing d, Regex stairLayers)
        {
            var list = new List<(double, Seg)>();
            if (stairLayers == null) return list;
            foreach (var s in d.Lines)
            {
                if (!stairLayers.IsMatch(s.Layer ?? "")) continue;
                double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 28.0 / 12 || len > 72.0 / 12) continue;
                double a = Math.Atan2(dy, dx);
                if (a < 0) a += Math.PI;
                if (a >= Math.PI - 1e-9) a -= Math.PI;
                a = Math.Round(a / (Math.PI / 180)) * (Math.PI / 180);
                if (a >= Math.PI - 1e-9) a = 0;
                list.Add((a, s));
            }
            return list;
        }

        /// <summary>Treads in a row: the same span across, 7" to 14" apart along the run; runs of one flight joined (a break line between two layers' treads).</summary>
        private static List<Flight> Flights(List<Seg> treads, Frame f)
        {
            var lines = new List<(double U0, double U1, double V)>();
            foreach (var t in treads)
            {
                var a = f.Local(t.X0, t.Y0); var b = f.Local(t.X1, t.Y1);
                double u0 = Math.Min(a.U, b.U), u1 = Math.Max(a.U, b.U), v = (a.V + b.V) / 2;
                // drawn twice (a block over its copy): once
                if (lines.Any(l => Math.Abs(l.V - v) < 0.25 / 12 && Math.Abs(l.U0 - u0) < Tol && Math.Abs(l.U1 - u1) < Tol)) continue;
                lines.Add((u0, u1, v));
            }
            var runs = new List<Flight>();
            foreach (var g in lines.GroupBy(l => (Math.Round(l.U0 * 6), Math.Round(l.U1 * 6))))     // 2" bins of the span
            {
                var row = g.OrderBy(l => l.V).ToList();
                var cur = new List<(double U0, double U1, double V)>();
                void Close()
                {
                    if (cur.Count >= 4)
                        runs.Add(new Flight { U0 = cur.Min(c => c.U0), U1 = cur.Max(c => c.U1), V0 = cur[0].V, V1 = cur[cur.Count - 1].V, Treads = cur.Count, Rise = (cur[cur.Count - 1].V - cur[0].V) / (cur.Count - 1) });
                    cur.Clear();
                }
                foreach (var l in row)
                {
                    if (cur.Count > 0)
                    {
                        double step = l.V - cur[cur.Count - 1].V;
                        if (step < 6.5 / 12 || step > 14.0 / 12) Close();
                    }
                    cur.Add(l);
                }
                Close();
            }
            // runs of one flight: overlapping spans across, touching or overlapping along the run
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < runs.Count && !merged; i++)
                    for (int j = i + 1; j < runs.Count && !merged; j++)
                    {
                        var a = runs[i]; var b = runs[j];
                        double across = Math.Min(a.U1, b.U1) - Math.Max(a.U0, b.U0);
                        double gap = Math.Max(a.V0, b.V0) - Math.Min(a.V1, b.V1);
                        if (across < 0.7 * Math.Min(a.U1 - a.U0, b.U1 - b.U0) || gap > 30.0 / 12) continue;
                        runs[i] = new Flight { U0 = Math.Min(a.U0, b.U0), U1 = Math.Max(a.U1, b.U1), V0 = Math.Min(a.V0, b.V0), V1 = Math.Max(a.V1, b.V1), Treads = a.Treads + b.Treads, Rise = (a.Rise + b.Rise) / 2 };
                        runs.RemoveAt(j);
                        merged = true;
                    }
            }
            return runs.Where(r => r.Treads >= 5).ToList();
        }

        /// <summary>Flights side by side (a scissor stair, a switchback) within 2 ft across and overlapping along the run: one stair.</summary>
        private static List<List<Flight>> ClusterFlights(List<Flight> flights)
        {
            var clusters = new List<List<Flight>>();
            foreach (var f in flights.OrderBy(f => f.U0))
            {
                var c = clusters.FirstOrDefault(cl => cl.Any(o => Math.Max(o.U0, f.U0) - Math.Min(o.U1, f.U1) <= 2 &&
                                                                  Math.Min(o.V1, f.V1) - Math.Max(o.V0, f.V0) > 0.3 * Math.Min(o.V1 - o.V0, f.V1 - f.V0)));
                if (c == null) clusters.Add(new List<Flight> { f }); else c.Add(f);
            }
            return clusters;
        }

        private static double Overlap(Stair a, Stair b)
        {
            double w = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX), h = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (w <= 0 || h <= 0) return 0;
            double small = Math.Min((a.MaxX - a.MinX) * (a.MaxY - a.MinY), (b.MaxX - b.MinX) * (b.MaxY - b.MinY));
            return small <= 0 ? 0 : w * h / small;
        }

        /// <summary>The search box of a landing (feet, stair frame): the stair's width and 6 ft each side, MaxLanding past the flights.</summary>
        private static (double U0, double U1, double V0, double V1) Box(Stair s, string end, SprinklerPlacerRules rules)
        {
            double depth = rules.MaxLanding / 12;
            return end == "low" ? (s.U0 - 6, s.U1 + 6, s.V0 - depth, s.V0) : (s.U0 - 6, s.U1 + 6, s.V1, s.V1 + depth);
        }

        private static bool InLandingBox(Stair s, Landing l, double u, double v, SprinklerPlacerRules rules)
        {
            var b = Box(s, l.End, rules);
            return u >= b.U0 && u <= b.U1 && v >= b.V0 && v <= b.V1;
        }

        /// <summary>
        /// One end's landing: the floor area walled off past the flights (flood fill on a 1" grid from the head of the
        /// flights; the door openings closed by their leaves), its doors, the cells a riser centre may take (clear of the
        /// walls and the flights by the manual's clearance, clear of door swings, door paths and columns), and its corners.
        /// </summary>
        private static Landing FindLanding(Drawing d, Stair s, string end, SprinklerPlacerRules rules, Regex wallLayers, Regex doorLayers, Regex colLayers, double clearIn, double sleeveIn)
        {
            var f = s.Frame;
            var box = Box(s, end, rules);
            var l = new Landing { Stair = s, End = end, U0 = box.U0, V0 = box.V0 };
            l.Nu = (int)Math.Ceiling((box.U1 - box.U0) / Cell) + 1;
            l.Nv = (int)Math.Ceiling((box.V1 - box.V0) / Cell) + 1;
            int nu = l.Nu, nv = l.Nv;
            var wall = new bool[nu, nv];
            void Mark(double ax, double ay, double bx, double by)
            {
                var a = f.Local(ax, ay); var b = f.Local(bx, by);
                if (Math.Max(a.U, b.U) < box.U0 - Cell || Math.Min(a.U, b.U) > box.U1 + Cell || Math.Max(a.V, b.V) < box.V0 - Cell || Math.Min(a.V, b.V) > box.V1 + Cell) return;
                double len = Math.Sqrt(Math.Pow(b.U - a.U, 2) + Math.Pow(b.V - a.V, 2));
                int steps = Math.Max(1, (int)Math.Ceiling(len / (Cell / 3)));
                for (int k = 0; k <= steps; k++)
                {
                    double u = a.U + (b.U - a.U) * k / steps, v = a.V + (b.V - a.V) * k / steps;
                    int i = (int)Math.Round((u - box.U0) / Cell), j = (int)Math.Round((v - box.V0) / Cell);
                    if (i >= 0 && j >= 0 && i < nu && j < nv) wall[i, j] = true;
                }
            }

            // walls, door frames (short lines on the door layers; a leaf drawn open is no wall), columns
            var columns = new List<Seg>();
            foreach (var seg in d.Lines)
            {
                string layer = seg.Layer ?? "";
                double len = Math.Sqrt(Math.Pow(seg.X1 - seg.X0, 2) + Math.Pow(seg.Y1 - seg.Y0, 2));
                if (wallLayers != null && wallLayers.IsMatch(layer)) Mark(seg.X0, seg.Y0, seg.X1, seg.Y1);
                else if (doorLayers != null && doorLayers.IsMatch(layer) && len < 1) Mark(seg.X0, seg.Y0, seg.X1, seg.Y1);
                else if (colLayers != null && colLayers.IsMatch(layer)) { Mark(seg.X0, seg.Y0, seg.X1, seg.Y1); columns.Add(seg); }
            }

            // doors: quarter arcs 2' to 4' on the door layers; the leaf closed across the opening
            // is the radius whose far end meets the jamb (wall / frame line ends there)
            var ends = d.Lines.Where(x => (wallLayers != null && wallLayers.IsMatch(x.Layer ?? "")) || (doorLayers != null && doorLayers.IsMatch(x.Layer ?? "")))
                              .SelectMany(x => new[] { (x.X0, x.Y0), (x.X1, x.Y1) }).ToList();
            foreach (var a in d.Arcs)
            {
                if (a.R < 2 || a.R > 4) continue;
                double sweep = Angle(a.Ax - a.Cx, a.Ay - a.Cy, a.Bx - a.Cx, a.By - a.Cy);
                if (sweep < 70 || sweep > 110) continue;
                var hl = f.Local(a.Cx, a.Cy);
                if (!(hl.U > box.U0 && hl.U < box.U1 && hl.V > box.V0 - 1 && hl.V < box.V1 + 1)) continue;
                if (doorLayers == null || !doorLayers.IsMatch(a.Layer ?? "")) continue;
                int Near(double x, double y) => ends.Count(e => Math.Abs(e.Item1 - x) < 8.0 / 12 && Math.Abs(e.Item2 - y) < 8.0 / 12);
                bool aClosed = Near(a.Ax, a.Ay) >= Near(a.Bx, a.By);
                var door = new Door
                {
                    Hx = a.Cx, Hy = a.Cy, R = a.R,
                    Cx = aClosed ? a.Ax : a.Bx, Cy = aClosed ? a.Ay : a.By, Ox = aClosed ? a.Bx : a.Ax, Oy = aClosed ? a.By : a.Ay
                };
                Mark(door.Hx, door.Hy, door.Cx, door.Cy);
                l.Doors.Add(door);
            }

            // the landing: flood fill from the head of the flights
            var fill = new bool[nu, nv];
            int edgeJ = end == "low" ? nv - 1 : 0, inward = end == "low" ? -1 : 1;
            bool leak = false; int count = 0;
            foreach (var fl in s.Flights.OrderBy(x => Math.Abs((x.U0 + x.U1) / 2 - (s.U0 + s.U1) / 2)))
            {
                int si = (int)Math.Round(((fl.U0 + fl.U1) / 2 - box.U0) / Cell);
                int sj = edgeJ + inward * 3;
                if (si < 0 || si >= nu || sj < 0 || sj >= nv || wall[si, sj] || fill[si, sj]) continue;
                var stack = new Stack<(int, int)>();
                stack.Push((si, sj)); fill[si, sj] = true;
                while (stack.Count > 0)
                {
                    var (i, j) = stack.Pop();
                    count++;
                    if (i == 0 || i == nu - 1 || j == (end == "low" ? 0 : nv - 1)) leak = true;
                    foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int a = i + di, b = j + dj;
                        if (a < 0 || b < 0 || a >= nu || b >= nv || wall[a, b] || fill[a, b]) continue;
                        fill[a, b] = true; stack.Push((a, b));
                    }
                }
            }
            if (count == 0) return null;
            Debug?.Invoke(s, l, wall, fill, leak);
            if (leak)
            {
                // no walls round it in the drawing: the stair's width, FallbackLanding deep, its sides the flights' outer edges
                fill = new bool[nu, nv]; wall = new bool[nu, nv];
                double depth = rules.FallbackLanding / 12;
                double v0 = end == "low" ? s.V0 - depth : s.V1, v1 = end == "low" ? s.V0 : s.V1 + depth;
                for (int i = 0; i < nu; i++)
                    for (int j = 0; j < nv; j++)
                    {
                        double u = box.U0 + i * Cell, v = box.V0 + j * Cell;
                        bool inside = u > s.U0 + 1e-6 && u < s.U1 - 1e-6 && v > v0 + 1e-6 && v < v1 - 1e-6;
                        bool edge = !inside && u >= s.U0 - Cell && u <= s.U1 + Cell && v >= v0 - Cell && v <= v1 + Cell;
                        fill[i, j] = inside;
                        wall[i, j] = edge && (end == "low" ? v < v1 - Cell / 2 : v > v0 + Cell / 2);
                    }
                l.Enclosed = false;
                s.Notes.Add($"{end} landing: no walls drawn round it, taken {rules.FallbackLanding / 12:0.#}' deep between the flights' outer edges (check)");
            }
            else l.Enclosed = true;
            l.Fill = fill;
            l.Area = fill.Cast<bool>().Count(x => x) * Cell * Cell;

            // where a riser centre may go: clear of anything not landing by the manual's clearance (walls, the flights' edge)
            double clear = clearIn / 12, r = sleeveIn / 24;
            int reach = (int)Math.Ceiling(clear / Cell);
            l.Ok = new bool[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    if (!fill[i, j]) continue;
                    bool ok = true;
                    for (int a = i - reach; a <= i + reach && ok; a++)
                        for (int b = j - reach; b <= j + reach && ok; b++)
                        {
                            if ((a - i) * (a - i) + (b - j) * (b - j) > (clear / Cell) * (clear / Cell) + 1e-9) continue;
                            if (a < 0 || b < 0 || a >= nu || b >= nv || !fill[a, b]) ok = false;
                        }
                    if (!ok) continue;
                    var (x, y) = f.World(box.U0 + i * Cell, box.V0 + j * Cell);
                    if (l.Doors.Any(dr => InSwing(dr, x, y, r + rules.DoorClear / 12) || InPath(dr, x, y))) continue;
                    if (columns.Any(c => DistToSeg(x, y, c) < rules.ColumnClear / 12 + r)) continue;
                    l.Ok[i, j] = true;
                }

            // corners: a cell the walls hem in across and along, within the clearance + 1" (not the flights' edge)
            var found = new List<(int I, int J, double Du, double Dv)>();
            double near = clear + 1.0 / 12;
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    if (!l.Ok[i, j]) continue;
                    double du = Math.Min(Ray(fill, wall, i, j, -1, 0), Ray(fill, wall, i, j, 1, 0));
                    double dv = Math.Min(Ray(fill, wall, i, j, 0, -1), Ray(fill, wall, i, j, 0, 1));
                    if (du <= near && dv <= near) found.Add((i, j, du, dv));
                }
            foreach (var c in found.OrderBy(c => c.Du + c.Dv))
            {
                var (x, y) = f.World(box.U0 + c.I * Cell, box.V0 + c.J * Cell);
                if (l.Corners.Any(o => Math.Abs(o.X - x) < 4.0 / 12 && Math.Abs(o.Y - y) < 4.0 / 12)) continue;
                var corner = new Corner { X = x, Y = y, ToWallU = Math.Round(c.Du * 12, 1), ToWallV = Math.Round(c.Dv * 12, 1), Landing = l };
                if (l.Doors.Count > 0) corner.ToDoor = l.Doors.Min(dr => Math.Sqrt(Math.Pow(dr.Mx - x, 2) + Math.Pow(dr.My - y, 2)));
                corner.Where = $"{(l.Name ?? end + " landing")}: corner {corner.ToWallU:0.#}\" and {corner.ToWallV:0.#}\" off the walls" +
                               (corner.ToDoor >= 0 ? (corner.ToDoor <= 5 ? $", beside the door ({corner.ToDoor * 12:0}\" to its opening)" : $", {corner.ToDoor:0.0}' from the door") : "") +
                               (l.Enclosed ? "" : " (walls not drawn: check)");
                l.Corners.Add(corner);
            }
            return l;
        }

        /// <summary>Feet from cell (i, j) to the first wall cell that way; a large number when the ray leaves the landing other than at a wall (the flights' edge, the box).</summary>
        private static double Ray(bool[,] fill, bool[,] wall, int i, int j, int di, int dj)
        {
            int nu = fill.GetLength(0), nv = fill.GetLength(1);
            for (int k = 1; ; k++)
            {
                int a = i + di * k, b = j + dj * k;
                if (a < 0 || b < 0 || a >= nu || b >= nv) return 999;
                if (wall[a, b]) return k * Cell;
                if (!fill[a, b]) return 999;
            }
        }

        /// <summary>Degrees between two vectors (0..180).</summary>
        private static double Angle(double ax, double ay, double bx, double by)
        {
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-9 || lb < 1e-9) return 0;
            return Math.Acos(Math.Max(-1, Math.Min(1, (ax * bx + ay * by) / (la * lb)))) * 180 / Math.PI;
        }

        /// <summary>Within the door's swing (the quarter circle between the closed and open leaf) grown by pad.</summary>
        private static bool InSwing(Door d, double x, double y, double pad)
        {
            double px = x - d.Hx, py = y - d.Hy, dist = Math.Sqrt(px * px + py * py);
            if (dist > d.R + pad) return false;
            if (dist <= pad) return true;
            double ax = d.Cx - d.Hx, ay = d.Cy - d.Hy, bx = d.Ox - d.Hx, by = d.Oy - d.Hy;
            double whole = Angle(ax, ay, bx, by), toA = Angle(ax, ay, px, py), toB = Angle(bx, by, px, py);
            double grow = Math.Asin(Math.Min(1, pad / Math.Max(dist, 1e-6))) * 180 / Math.PI;
            if (toA + toB <= whole + 1 + 2 * grow) return true;
            // within pad of either leaf
            return DistToSeg(x, y, new Seg { X0 = d.Hx, Y0 = d.Hy, X1 = d.Cx, Y1 = d.Cy }) <= pad || DistToSeg(x, y, new Seg { X0 = d.Hx, Y0 = d.Hy, X1 = d.Ox, Y1 = d.Oy }) <= pad;
        }

        /// <summary>In the way through the door: the opening's width, 3 ft each side of it.</summary>
        private static bool InPath(Door d, double x, double y)
        {
            double ux = d.Cx - d.Hx, uy = d.Cy - d.Hy, w = Math.Sqrt(ux * ux + uy * uy);
            if (w < 1e-6) return false;
            ux /= w; uy /= w;
            double along = (x - d.Hx) * ux + (y - d.Hy) * uy, across = -(x - d.Hx) * uy + (y - d.Hy) * ux;
            return along > 0 && along < w && Math.Abs(across) < 3;
        }

        private static double DistToSeg(double x, double y, Seg s)
        {
            double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - s.X0) * dx + (y - s.Y0) * dy) / l2));
            return Math.Sqrt(Math.Pow(s.X0 + t * dx - x, 2) + Math.Pow(s.Y0 + t * dy - y, 2));
        }

        // ------------------------------------------------------------------ the building's risers

        public class Floor
        {
            public string Level;
            public double Elevation;
            public List<Drawing> Drawings = new List<Drawing>();
            /// <summary>Gets no sleeve (on grade, bulkhead roof): its stairs still count for the spot.</summary>
            public string Skip;
            public List<Stair> Stairs = new List<Stair>();
            public List<string> Notes = new List<string>();
        }

        public class Riser
        {
            public string Name, Kind;
            /// <summary>The stair's name ("STAIR B") or "stair 1".</summary>
            public string Stair;
            public double X, Y;
            public double Pipe;
            /// <summary>Its drain's pipe (the combined riser's FCVA drain), 0 = none; the drain's own sleeve at DrainX/DrainY.</summary>
            public double Drain;
            public double DrainX = double.NaN, DrainY = double.NaN;
            /// <summary>The spots it can take, best first (Next corner steps through them); Option = the one used.</summary>
            public List<(Corner Corner, int Floors)> Options = new List<(Corner, int)>();
            public int Option;
            public bool Review;
            public List<string> Notes = new List<string>();
            /// <summary>Floors (level names) whose drawings show this stair.</summary>
            public List<string> Shown = new List<string>();
            public Corner Corner => Options.Count == 0 ? null : Options[Math.Max(0, Math.Min(Option, Options.Count - 1))].Corner;
            public string Label => (Kind == Standpipe ? "STANDPIPE" : Kind == Sprinkler ? "SPRINKLER RISER" : "COMBINED SP/SPR") + (Stair != null ? " - " + Stair : "");
        }

        public const string Combined = "Combined Standpipe/Sprinkler", Standpipe = "Standpipe Only", Sprinkler = "Sprinkler Riser";

        /// <summary>
        /// The riser at its corner; with a drain (2258 Morris video: "the drain always goes in the back - the riser in the
        /// front, the drain at the back") the drain takes the corner and the riser sits cc in front of it, toward the flights
        /// (else toward the middle of the stair), both on the landing's free floor.
        /// </summary>
        public static void Spot(Riser r, double cc)
        {
            r.DrainX = r.DrainY = double.NaN;
            r.Notes.RemoveAll(n => n.StartsWith("drain "));
            var c = r.Corner;
            if (c == null) return;
            r.X = c.X; r.Y = c.Y;
            if (r.Drain <= 0) return;
            var l = c.Landing; var f = l.Stair.Frame;
            var (u, v) = f.Local(c.X, c.Y);
            double step = cc / 12, front = l.End == "low" ? 1 : -1, mid = (l.Stair.U0 + l.Stair.U1) / 2 > u ? 1 : -1;
            foreach (var (du, dv) in new[] { (0.0, front), (mid, 0.0) })
            {
                var p = f.World(u + du * step, v + dv * step);
                if (!l.Allows(p.X, p.Y)) continue;
                r.DrainX = c.X; r.DrainY = c.Y; r.X = p.X; r.Y = p.Y;
                r.Notes.Add($"drain {r.Drain:0.#}\" at the back (in the corner), the riser {cc:0.#}\" c-c in front of it");
                return;
            }
            var q = f.World(u, v + front * step);
            r.DrainX = q.X; r.DrainY = q.Y;
            r.Notes.Add($"drain {r.Drain:0.#}\" set {cc:0.#}\" c-c from the riser: no room behind it on the landing - check");
            r.Review = true;
        }

        public class Plan
        {
            public List<Floor> Floors = new List<Floor>();
            public List<Riser> Risers = new List<Riser>();
            public List<string> Notes = new List<string>();
        }

        /// <summary>
        /// The building's risers (manual p16): one per stair enclosure (a scissor stair is one) - the first Combined
        /// Standpipe/Sprinkler, the second Standpipe Only, more listed as additional standpipes to check against the riser
        /// diagram - each at the landing corner free on the most floors, the same spot top to bottom.
        /// </summary>
        public static Plan Risers(List<Floor> floors, SprinklerPlacerRules rules, double clear, double sleeve, double cc = 8)
        {
            var plan = new Plan { Floors = floors };
            // a standpipe building (taller than 75 ft): a riser in every stair, the first Combined (with its drain), the
            // others Standpipe Only; else one sprinkler riser
            bool standpipe;
            string mode = (rules.Standpipe ?? "auto").Trim().ToLowerInvariant();
            if (mode == "yes" || mode == "true") { standpipe = true; plan.Notes.Add("Standpipe building (sprinklerPlacer.standpipe = yes)."); }
            else if (mode == "no" || mode == "false") { standpipe = false; plan.Notes.Add("Sprinkler riser only (sprinklerPlacer.standpipe = no)."); }
            else
            {
                var below = Rx(rules.BelowGrade); var skipTop = new Regex(@"ROOF|BULK", RegexOptions.IgnoreCase);
                var ground = floors.Where(f => below == null || !below.IsMatch(f.Level)).OrderBy(f => f.Elevation).FirstOrDefault();
                var topFloor = floors.Where(f => !skipTop.IsMatch(f.Level) && f.Skip == null).OrderByDescending(f => f.Elevation).FirstOrDefault();
                double height = ground != null && topFloor != null ? topFloor.Elevation - ground.Elevation : 0;
                standpipe = height > rules.StandpipeHeight;
                plan.Notes.Add(ground == null || topFloor == null ? "Building height not known from the ticked floors: sprinkler riser only (set sprinklerPlacer.standpipe)."
                    : $"{ground.Level} to {topFloor.Level}: {height:0.#} ft, " + (standpipe
                        ? $"over {rules.StandpipeHeight:0} ft: a standpipe building - a riser in every stair (Combined + its drain, then Standpipe Only), {rules.StandpipePipe:0.#}\" pipes."
                        : $"{rules.StandpipeHeight:0} ft or less: one sprinkler riser, {rules.Pipe:0.#}\" pipe (no standpipe)."));
            }
            foreach (var fl in floors)
                foreach (var d in fl.Drawings)
                    fl.Stairs.AddRange(Find(d, rules, clear, sleeve, fl.Notes));

            // the same stair floor to floor: its run's box overlaps
            var groups = new List<List<(Floor F, Stair S)>>();
            foreach (var fl in floors.OrderByDescending(f => f.Stairs.Count))
                foreach (var s in fl.Stairs)
                {
                    var g = groups.FirstOrDefault(x => x.Any(o => o.F != fl && Overlap(o.S, s) > 0.5) && !x.Any(o => o.F == fl));
                    if (g == null) groups.Add(new List<(Floor, Stair)> { (fl, s) }); else g.Add((fl, s));
                }
            // the building's stairs run floor to floor: a duplex apartment's stair (two floors), the stoop or an areaway
            // stair (one) is no stairwell
            int drawn = floors.Count(f => f.Drawings.Count > 0);
            var dropped = groups.Where(g => drawn >= 3 && g.Count < Math.Max(2, Math.Ceiling(drawn * 0.6))).ToList();
            if (dropped.Count > 0)
                plan.Notes.Add($"{dropped.Count} other stair(s) left out (shown on too few floors to be a stairwell: duplex, stoop, areaway): " +
                               string.Join("; ", dropped.Select(g => $"{g[0].S.Name ?? "stair"} at ({(g[0].S.MinX + g[0].S.MaxX) / 2:0.0}, {(g[0].S.MinY + g[0].S.MaxY) / 2:0.0}) on {string.Join(", ", g.Select(x => x.F.Level))}")));
            groups = groups.Except(dropped).ToList();
            if (groups.Count == 0)
            {
                plan.Notes.Add("No stair found on any ticked floor's drawings: place the riser by hand (Add riser), in the stairwell (manual p16).");
                return plan;
            }

            bool near = !string.Equals(rules.Corner, "awayFromDoor", StringComparison.OrdinalIgnoreCase);
            var risers = new List<(Riser R, double Rank)>();
            int n = 0;
            foreach (var g in groups)
            {
                n++;
                // a standpipe building's scissor stair (doors or names on both landings): each of its two stairs gets its riser
                bool scissor = standpipe && g.Count(x => x.S.Landings.Count(l => l.Doors.Count > 0 || l.Name != null) >= 2) * 2 >= g.Count;
                foreach (var only in scissor ? new[] { "low", "high" } : new string[] { null })
                {
                var r = new Riser { Pipe = standpipe ? rules.StandpipePipe : rules.Pipe, Shown = g.Select(x => x.F.Level).ToList() };
                r.Stair = g.Select(x => x.S.Name).Where(x => x != null).GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
                // a landing drawn without walls on one floor (AEC walls the drawing does not show as lines) takes the walls
                // another floor draws for that end of the same stair
                var walled = new Dictionary<string, Landing>();
                foreach (var end in new[] { "low", "high" })
                {
                    var w = g.OrderBy(x => x.F.Skip == null ? 0 : 1).SelectMany(x => x.S.Landings).FirstOrDefault(l => l.End == end && l.Enclosed);
                    if (w != null) walled[end] = w;
                }
                bool Allows((Floor F, Stair S) x, double px, double py) =>
                    x.S.Landings.Any(l => !l.Enclosed && walled.TryGetValue(l.End, out var w) ? w.Allows(px, py) : l.Allows(px, py));
                if (g.Any(x => x.S.Landings.Any(l => !l.Enclosed && walled.ContainsKey(l.End))))
                    r.Notes.Add("landings drawn without walls on some floors take the walls of " + string.Join(", ", walled.Values.Select(l => l.End + " landing on the floor that draws them")));
                // every corner found on any floor, scored on all of them
                var spots = new List<Corner>();
                foreach (var c in g.SelectMany(x => x.S.Landings).Where(l => (only == null || l.End == only) && (l.Enclosed || !walled.ContainsKey(l.End))).SelectMany(l => l.Corners))
                    if (!spots.Any(o => Math.Abs(o.X - c.X) < 2.0 / 12 && Math.Abs(o.Y - c.Y) < 2.0 / 12)) spots.Add(c);
                bool anyHint = g.Any(x => x.S.Landings.Any(l => l.Hint != null));
                // the floors that get the sleeve decide the spot (the slab on grade and the bulkhead roof only show the stair)
                var sleeved = g.Where(x => x.F.Skip == null).ToList();
                if (sleeved.Count == 0) sleeved = g;
                // the top floor (the roof: a bulkhead door, the stair ending) does not move the riser; the spot is flagged there
                var top = sleeved.Count > 1 ? sleeved.OrderByDescending(x => x.F.Elevation).First().F : null;
                var scored = spots.Select(c =>
                {
                    int ok = sleeved.Count(x => x.F != top && Allows(x, c.X, c.Y));
                    bool topOk = sleeved.Any(x => x.F == top && Allows(x, c.X, c.Y));
                    var l = c.Landing;
                    double score = 0;
                    if (!l.Enclosed) score += 2000;
                    if (anyHint && l.Hint == null) score += 1000;
                    if (l.Doors.Count == 0) score += 500;
                    else score += near ? c.ToDoor : -c.ToDoor;
                    return (Corner: c, Floors: ok, Score: score, Top: topOk);
                }).OrderByDescending(x => x.Floors).ThenBy(x => x.Score).ThenByDescending(x => x.Top).ToList();
                r.Options = scored.Select(x => (x.Corner, x.Floors + (x.Top ? 1 : 0))).ToList();
                if (r.Corner != null) { r.X = r.Corner.X; r.Y = r.Corner.Y; if (r.Corner.Landing.Name != null) r.Stair = r.Corner.Landing.Name; }
                else
                {
                    var s0 = g[0].S;
                    var (cx, cy) = s0.Frame.World((s0.U0 + s0.U1) / 2, s0.V0);
                    r.X = cx; r.Y = cy; r.Review = true;
                    r.Notes.Add("no landing corner keeps the manual's clearances on these drawings: placed at the head of the flights - move it by hand");
                }
                if (r.Corner != null)
                {
                    int best = r.Options[0].Floors;
                    r.Notes.Add($"{r.Corner.Where}; free on {best} of {sleeved.Count} sleeved floor(s) showing this stair");
                    if (best < sleeved.Count)
                    {
                        r.Review = true;
                        r.Notes.Add("not free on " + string.Join(", ", sleeved.Where(x => !Allows(x, r.X, r.Y)).Select(x => x.F.Level)) + " (a wall or door there differs): check");
                    }
                    if (!r.Corner.Landing.Enclosed) r.Review = true;
                    if (r.Corner.Landing.Hint != null) r.Notes.Add($"the plan marks this landing '{r.Corner.Landing.Hint}'");
                }
                if (sleeved.Count < floors.Count(f => f.Skip == null)) r.Notes.Add($"stair shown on {sleeved.Count} of {floors.Count(f => f.Skip == null)} sleeved floor(s); the same spot is used on the others (manual: same location top to bottom)");
                double rank = (anyHint ? 0 : 1000) - g.Count * 10 - g.Max(x => x.S.Landings.Sum(l => l.Area)) / 100 + (only == "high" ? 0.5 : 0);
                if (r.Corner == null && only != null) continue;               // that stair of the pair has no corner on any floor
                risers.Add((r, rank));
                }
            }
            int k = 0;
            var ordered = risers.OrderBy(x => x.Rank).Select(x => x.R).ToList();
            if (!standpipe && ordered.Count > 1)
            {
                plan.Notes.Add($"{ordered.Count - 1} other stair(s) get no riser (no standpipe): " + string.Join(", ", ordered.Skip(1).Select(r => r.Stair ?? "stair")) +
                               " - Add riser if the riser diagram shows one there.");
                ordered = ordered.Take(1).ToList();
            }
            foreach (var r in ordered)
            {
                k++;
                r.Name = "SP-" + k;
                r.Kind = !standpipe ? Sprinkler : k == 1 ? Combined : Standpipe;
                if (r.Kind == Combined) r.Drain = rules.DrainPipe;
                if (r.Stair == null) r.Stair = "STAIR " + k;
                if (k >= 3) { r.Review = true; r.Notes.Add("a third stair: an additional standpipe (manual: large buildings may have more) - check the riser diagram"); }
                Spot(r, cc);
                plan.Risers.Add(r);
            }
            if (standpipe && plan.Risers.Count == 1)
                plan.Notes.Add("A standpipe building with one stair found: one Combined riser (manual p16: most buildings have two stairwells, one Standpipe Only - check the riser diagram, Add riser).");
            plan.Notes.Add("Bulkhead roof: no riser sleeve by the rule - check the sprinkler drawing for a pipe coming down from the bulkhead (2258 Morris: a 1\" sprinkler pipe DN in the HWH room got its own sleeve; Add riser).");
            plan.Notes.Add("FDC: not placed by the rule (the manual does not cover it) - check the sprinkler drawing for a 4\" FDC (6\" sleeve, 1st floor by the street wall).");
            plan.Notes.Add("Trash chute sprinkler: no sleeve (it runs inside the chute opening).");
            return plan;
        }
    }
}
