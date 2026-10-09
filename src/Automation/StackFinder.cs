using System;
using System.Collections.Generic;
using System.Linq;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Extract Stacks: a floor's plumbing sleeves from the model alone (no engineer's drawing), each one only when that
    /// floor of the model shows what needs it (never by the floor's name: the floor above may differ). Only stacks and
    /// fixture drains go through the slab; the rest of a fixture's pipes run in the wall to its stack (24 Skillman S&amp;O
    /// set, manual toilet sleeves page):
    /// 1. a vertical pipe modelled in Revit -> its sleeve exactly on it, its own system;
    /// 2. the other fixtures grouped by room / wall (same room within 6 ft, or the same wall line, either face);
    /// 3. a group with a toilet -> a stack (only toilets make stacks):
    ///    - waste 6 in the wall behind the toilet, 4 1/4" off its centre line, on the outer side (manual); or in a pipe
    ///      chase drawn next to it (a small outline on the shaft layers with no chute or duct in it);
    ///    - vent 6 only where a fixture sits on the other face of that wall (back to back): between the two drains;
    ///    - hot 3 / cold 3 only where water shows on this floor (a Revit water pipe, a water riser circle of a plumbing
    ///      DWG on the level) near the stack;
    /// 4. every fixture's own sleeves (optional): its drain sleeve(s); a toilet's and a lavatory's 4" vent in the wall
    ///    beside them; a wall sink its drain only; an island sink its own row vent - hot - drain - cold (hot on the left
    ///    facing it, IRC P2722.2; water only where detected).
    /// What a stack would need but the floor does not show is listed (Missing), not placed. Free of the Revit API. Feet.
    /// </summary>
    public static class StackFinder
    {
        /// <summary>A toilet's sleeve is this far (feet) out from the wall behind it (manual 1'-1"), the wall's centre 2 1/2" further.</summary>
        private const double ToiletToWallFeet = 15.5 / 12;
        /// <summary>The waste stack this far (feet) along the wall from the toilet's centre line (manual, toilet sleeves page: 4 1/4").</summary>
        private const double StackFromToiletFeet = 4.25 / 12;
        /// <summary>The toilet's vent this far (feet) along the wall from its centre line, on the other side (manual: 5 1/4").</summary>
        private const double VentFromToiletFeet = 5.25 / 12;
        /// <summary>Two fixtures' walls are one when their centre lines are this close (feet): both faces of one wall.</summary>
        private const double SameWallFeet = 8.0 / 12;
        /// <summary>Fixtures along one wall this far apart (feet) at most are one wet group.</summary>
        private const double AlongWallFeet = 8.0;
        /// <summary>Fixtures this close (feet) are in one room (a toilet and its lavatory on the next wall).</summary>
        private const double RoomFeet = 6.0;
        /// <summary>A modelled pipe serves the fixtures this close (feet).</summary>
        private const double PipeServesFeet = 6.0;
        /// <summary>Modelled pipes this close (feet) are one stack.</summary>
        private const double PipeClusterFeet = 2.0;
        /// <summary>A chase this close (feet) to a toilet's wall is its stack's.</summary>
        private const double ChaseFeet = 3.0;
        /// <summary>A pipe chase: its short side 4" to 2 ft, its long side up to 4 ft (a duct shaft or a chute is bigger or holds an X / a circle).</summary>
        private const double ChaseMin = 4.0 / 12, ChaseShortMax = 2.0, ChaseLongMax = 4.0;
        /// <summary>Water shows for a stack or an island sink when a water pipe or riser circle is this close (feet).</summary>
        private const double WaterFeet = 3.0;
        /// <summary>How far (feet) a fixture vent may slide along the wall to clear the sleeves already there.</summary>
        private const double SlideFeet = 1.0;
        /// <summary>A fixture whose drain is this close (feet) to a vent stack is vented by it: no vent sleeve of its own.</summary>
        private const double VentStackServesFeet = 1.0;

        public class Fixture
        {
            public string Code, Name, Source, How;
            /// <summary>Its drain sleeve point (the mean of <see cref="Points"/>).</summary>
            public double X, Y;
            /// <summary>Its drain sleeve point(s): a tub has two.</summary>
            public List<(double X, double Y)> Points = new List<(double X, double Y)>();
            /// <summary>Unit direction from the fixture to the wall behind it; null = not known.</summary>
            public double[] Back;
            /// <summary>A sink with no wall behind it (its drawing says island).</summary>
            public bool Island;
            public bool WallHung => (Name ?? "").IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public class Pipe
        {
            public double X, Y;
            /// <summary>Sanitary, Vent, ColdWater, HotWater, Storm, Gas (the rules' system names).</summary>
            public string System;
            /// <summary>Inches.</summary>
            public double Diameter;
            public string Source;
        }

        /// <summary>A water pipe or riser circle the floor shows (evidence only: hot / cold sleeves are placed by the rule).</summary>
        public class Mark
        {
            public double X, Y;
            public string System, Source;
        }

        public class Sleeve
        {
            public string Service, System;
            /// <summary>Pipe and sleeve size, inches.</summary>
            public double Pipe, Size;
            public double X, Y;
            /// <summary>The fixture whose own sleeve this is (its code: WC, LAV...); null = a stack's sleeve.</summary>
            public string Fixture;
            /// <summary>Sleeves of one fixture and one service share this (a tub's two drain sleeves).</summary>
            public int Owner;
        }

        public class Stack
        {
            /// <summary>M1..; the row's kind: "toilet stack", "Revit pipe stack", "wall sink", "island sink", "fixtures".</summary>
            public string Id, Kind, Source, How;
            /// <summary>The waste stack's spot (or the group's first drain).</summary>
            public double X, Y;
            public List<Fixture> Serves = new List<Fixture>();
            public List<Sleeve> Sleeves = new List<Sleeve>();
            /// <summary>What the stack would carry but this floor of the model does not show: not placed.</summary>
            public List<string> Missing = new List<string>();
            public List<string> Notes = new List<string>();
            /// <summary>Laid out by the office rule, not on a modelled pipe: check it.</summary>
            public bool Check;
            public IEnumerable<Sleeve> StackSleeves => Sleeves.Where(s => s.Fixture == null);
            public IEnumerable<Sleeve> FixtureSleeves => Sleeves.Where(s => s.Fixture != null);
        }

        public class Result
        {
            public List<Stack> Stacks = new List<Stack>();
        }

        /// <param name="fixtures">The floor's fixtures (Extract Fixtures' reading), with the wall behind each where known.</param>
        /// <param name="shafts">Lines on the shaft / chase layers (feet).</param>
        /// <param name="pipes">Vertical pipes modelled through this floor's slab.</param>
        /// <param name="water">Water the floor shows besides <paramref name="pipes"/> (a plumbing DWG's water riser circles).</param>
        /// <param name="withFixtures">Also each fixture's own sleeves (drain, vents, an island sink's row).</param>
        public static Result Find(List<Fixture> fixtures, List<((double X, double Y) A, (double X, double Y) B)> shafts, List<Pipe> pipes,
                                  List<Mark> water, PlumbingRules rules, bool withFixtures)
        {
            var result = new Result();
            double gap = rules.SleeveGap / 12;
            double R(double pipe) => rules.SleeveFor(pipe) / 2 / 12;
            bool Is(Fixture f, string code) => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase);
            bool Sink(Fixture f) => Is(f, "KS") || Is(f, "LS");
            var all = fixtures.Where(f => rules.Fixtures.ContainsKey(f.Code) && !Is(f, "FD")).ToList();
            var waterAt = water.Concat(pipes.Where(p => p.System == "HotWater" || p.System == "ColdWater")
                                            .Select(p => new Mark { X = p.X, Y = p.Y, System = p.System, Source = p.Source })).ToList();
            var placed = new List<Sleeve>();
            int owners = 0;

            Sleeve Add(Stack s, string service, string system, double pipe, double x, double y, string fixture = null, int owner = 0)
            {
                var v = new Sleeve { Service = service, System = system, Pipe = pipe, Size = rules.SleeveFor(pipe), X = x, Y = y, Fixture = fixture, Owner = owner };
                s.Sleeves.Add(v); placed.Add(v);
                return v;
            }
            Sleeve Service(Stack s, string service, double x, double y)
            {
                var sv = rules.Services[service];
                return Add(s, service, sv.System, sv.Pipe, x, y);
            }
            bool Clear(double x, double y, double r) => placed.All(v => Dist(v.X, v.Y, x, y) >= v.Size / 24 + r + gap - 1e-6);
            string Letter(string system) => rules.Services.FirstOrDefault(kv => kv.Value.System == system).Key;
            bool Allowed(string system) { var l = Letter(system); return l != null && rules.StackServices.Contains(l); }

            // ---- 1. pipes modelled in Revit
            var served = new HashSet<Fixture>();
            foreach (var cluster in Clusters(pipes, PipeClusterFeet))
            {
                var s = new Stack { Kind = "Revit pipe stack", Source = "Revit pipe", X = cluster.Average(p => p.X), Y = cluster.Average(p => p.Y) };
                foreach (var p in cluster)
                    Add(s, Letter(p.System) ?? p.System, p.System, p.Diameter, p.X, p.Y);
                s.How = $"{cluster.Count} vertical pipe(s) modelled through the slab ({string.Join(", ", cluster.Select(p => p.Source).Distinct())}): sleeves on them";
                if (cluster.Any(p => p.System == "Sanitary"))
                    foreach (var f in all.Where(f => !served.Contains(f) && Dist(f.X, f.Y, s.X, s.Y) <= PipeServesFeet)) { s.Serves.Add(f); served.Add(f); }
                result.Stacks.Add(s);
            }

            // ---- 2. the rest grouped by room / wall
            (double X, double Y)? Wall(Fixture f)
            {
                if (f.Back == null) return null;
                double d = Is(f, "WC") && !f.WallHung ? ToiletToWallFeet : 0;
                return (f.X + f.Back[0] * d, f.Y + f.Back[1] * d);
            }
            var left = all.Where(f => !served.Contains(f)).ToList();
            var walled = left.Where(f => f.Back != null).ToList();
            var parent = walled.ToDictionary(f => f, f => f);
            Fixture Root(Fixture f) { while (parent[f] != f) f = parent[f] = parent[parent[f]]; return f; }
            for (int i = 0; i < walled.Count; i++)
                for (int j = i + 1; j < walled.Count; j++)
                {
                    var f = walled[i]; var g = walled[j];
                    // an island sink is a group of its own (nothing drains through its counter)
                    if (f.Island || g.Island) continue;
                    if (Dist(f.X, f.Y, g.X, g.Y) <= RoomFeet) { parent[Root(f)] = Root(g); continue; }
                    if (Math.Abs(f.Back[0] * g.Back[0] + f.Back[1] * g.Back[1]) < 0.95) continue;        // walls at an angle
                    var wf = Wall(f).Value; var wg = Wall(g).Value;
                    double dx = wg.X - wf.X, dy = wg.Y - wf.Y;
                    double across = Math.Abs(dx * f.Back[0] + dy * f.Back[1]), along = Math.Abs(-dx * f.Back[1] + dy * f.Back[0]);
                    if (across <= SameWallFeet && along <= AlongWallFeet) parent[Root(f)] = Root(g);
                }
            var groups = walled.GroupBy(Root).Select(g => g.ToList()).ToList();
            var loose = new List<Fixture>();
            foreach (var f in left.Where(f => f.Back == null))
            {
                // tubs and showers (no wall known): the group of their room
                var near = groups.Where(g => !g.Any(o => o.Island)).Select(g => (G: g, D: g.Min(o => Dist(o.X, o.Y, f.X, f.Y))))
                                 .Where(t => t.D <= RoomFeet).OrderBy(t => t.D).FirstOrDefault();
                if (near.G != null) near.G.Add(f);
                else loose.Add(f);
            }

            var chases = Chases(shafts);
            // ---- 3. one stack per group with a toilet; the other groups are their fixtures only
            var rows = new List<(Stack S, List<Fixture> G, Fixture Toilet, double[] A, int Side)>();
            foreach (var group in groups)
            {
                var toilet = group.Where(f => Is(f, "WC") && f.Back != null).OrderBy(f => group.Average(o => Dist(o.X, o.Y, f.X, f.Y))).FirstOrDefault();
                var s = new Stack { Check = true };
                s.Serves.AddRange(group);
                if (toilet == null)
                {
                    bool island = group.Count == 1 && group[0].Island;
                    s.Kind = island ? "island sink" : group.All(Sink) ? "wall sink" : "fixtures";
                    s.Source = "fixtures";
                    s.How = $"no toilet: no stack (only toilets make stacks); {(island ? "the island sink's own row" : "the fixtures' own sleeves")}";
                    var first = group[0];
                    s.X = first.X; s.Y = first.Y;
                    rows.Add((s, group, null, null, 0));
                    result.Stacks.Add(s);
                    continue;
                }

                var w = Wall(toilet).Value;
                double nx = toilet.Back[0], ny = toilet.Back[1], ax = -ny, ay = nx;          // a = along the wall
                double T((double X, double Y) p) => (p.X - w.X) * ax + (p.Y - w.Y) * ay;
                var others = group.Where(f => f != toilet).ToList();
                double mean = others.Count > 0 ? others.Average(f => T(Wall(f) ?? (f.X, f.Y))) : 0;
                int side = mean > 1e-6 ? -1 : 1;                                     // the outer side: away from the other fixtures
                (double X, double Y) At(double t) => (w.X + ax * t, w.Y + ay * t);
                s.Kind = "toilet stack";

                var chase = chases.Select(b => (B: b, D: BoxDist(b, w))).Where(t => t.D <= ChaseFeet).OrderBy(t => t.D).FirstOrDefault();
                var services = new List<string> { "Sanitary" };
                // hot / cold: only where the floor shows water near the stack
                var wet = waterAt.Where(m => Dist(m.X, m.Y, w.X, w.Y) <= WaterFeet).ToList();
                foreach (var sys in new[] { "HotWater", "ColdWater" })
                    if (wet.Any(m => m.System == sys)) services.Add(sys);
                    else if (Allowed(sys)) s.Missing.Add($"{Letter(sys)}: no {(sys == "HotWater" ? "hot" : "cold")} water pipe or riser in the model within {WaterFeet:0} ft on this floor");
                services = services.Where(Allowed).ToList();

                if (chase.B.X1 > chase.B.X0)
                {
                    var b = chase.B;
                    bool alongX = b.X1 - b.X0 >= b.Y1 - b.Y0;
                    double cx = (b.X0 + b.X1) / 2, cy = (b.Y0 + b.Y1) / 2;
                    var radii = services.Select(sys => R(rules.Services[Letter(sys)].Pipe)).ToList();
                    double total = radii.Sum(r => 2 * r) + gap * (radii.Count - 1), at = -total / 2;
                    for (int i = 0; i < services.Count; i++)
                    {
                        at += radii[i];
                        Service(s, Letter(services[i]), alongX ? cx + at : cx, alongX ? cy : cy + at);
                        at += radii[i] + gap;
                    }
                    s.Source = "chase";
                    s.How = $"in the pipe chase drawn on the shaft layers ({(b.X1 - b.X0) * 12:0}\" x {(b.Y1 - b.Y0) * 12:0}\"), {chase.D * 12:0}\" from the toilet's wall";
                }
                else
                {
                    double t = side * StackFromToiletFeet, prevR = 0;
                    foreach (var sys in services)
                    {
                        double r = R(rules.Services[Letter(sys)].Pipe);
                        if (prevR > 0) t += side * (prevR + r + gap);
                        var p = At(t); Service(s, Letter(sys), p.X, p.Y);
                        prevR = r;
                    }
                    s.Source = "wet wall";
                    s.How = $"in the wall behind the toilet, {StackFromToiletFeet * 12:0.##}\" off its centre line (manual), on the outer side of the room; by the office rule, check";
                }

                // vent stack: only where a fixture sits on the other face of the toilet's wall (back to back)
                var vent = rules.Services.FirstOrDefault(kv => kv.Value.System == "Vent");
                if (vent.Key != null && rules.StackServices.Contains(vent.Key))
                {
                    var back = all.Where(f => f != toilet && f.Back != null && !f.Island && f.Back[0] * nx + f.Back[1] * ny <= -0.95)
                                  .Select(f => (F: f, W: Wall(f).Value))
                                  .Where(x => Math.Abs((x.W.X - w.X) * nx + (x.W.Y - w.Y) * ny) <= SameWallFeet && Math.Abs(T(x.W)) <= AlongWallFeet)
                                  .OrderBy(x => Math.Abs(T(x.W))).FirstOrDefault();
                    if (back.F != null)
                    {
                        // between the back-to-back fixture's drain and the nearest drain on this face (else the toilet)
                        double tb = T(back.W);
                        var front = others.Where(f => f.Back != null && f.Back[0] * nx + f.Back[1] * ny >= 0.95 && !Is(f, "WC"))
                                          .Select(f => T(Wall(f).Value)).OrderBy(tf => Math.Abs(tf - tb)).Cast<double?>().FirstOrDefault() ?? 0;
                        int dir = front >= tb ? 1 : -1;
                        double r = R(vent.Value.Pipe), rb = Is(back.F, "WC") ? 0 : R(rules.Fixtures[back.F.Code].Pipe);
                        // clear of the stack's own sleeves: along the wall, on away from the waste stack, up to a foot
                        double t0 = tb + dir * (rb + r + gap);
                        int away = (t0 - side * StackFromToiletFeet) * dir >= 0 ? dir : -dir;
                        double? tv = null;
                        for (double extra = 0; extra <= SlideFeet + 1e-9 && tv == null; extra += 1.0 / 12)
                        {
                            var q = At(t0 + away * extra);
                            if (Clear(q.X, q.Y, r)) tv = t0 + away * extra;
                        }
                        var p = At(tv ?? t0);
                        Service(s, vent.Key, p.X, p.Y);
                        s.Notes.Add($"vent stack beside the {back.F.Code} on the other face of the wall (back to back)" +
                                    (tv == null ? "; it overlaps the stack's sleeves: move it by hand" : tv.Value != t0 ? $", moved {Math.Abs(tv.Value - t0) * 12:0}\" clear of the stack" : ""));
                    }
                    else s.Missing.Add($"{vent.Key}: no fixture back to back on the other face of the toilet's wall (no vent stack)");
                }

                var waste = s.StackSleeves.FirstOrDefault(v => v.System == "Sanitary");
                s.X = waste?.X ?? w.X; s.Y = waste?.Y ?? w.Y;
                rows.Add((s, group, toilet, new[] { ax, ay }, side));
                result.Stacks.Add(s);
            }
            foreach (var f in loose)
            {
                var s = new Stack { Kind = "fixtures", Source = "fixtures", Check = true, X = f.X, Y = f.Y, How = "no wall known and no room with a toilet: its own sleeves only" };
                s.Serves.Add(f);
                rows.Add((s, new List<Fixture> { f }, null, null, 0));
                result.Stacks.Add(s);
            }

            // ---- 4. each fixture's own sleeves
            if (withFixtures)
                foreach (var (s, group, toilet, a, side) in rows)
                    foreach (var f in group)
                    {
                        var fs = rules.Fixtures[f.Code];
                        int owner = ++owners;
                        var drains = f.Points.Count > 0 ? f.Points : new List<(double X, double Y)> { (f.X, f.Y) };
                        foreach (var d in drains) Add(s, f.Code, fs.System ?? "Sanitary", fs.Pipe, d.X, d.Y, f.Code, owner);
                        if (f.Back == null) continue;
                        double bx = f.Back[0], by = f.Back[1], lx = -by, ly = bx;                // l = to the left of someone facing the wall
                        double rd = R(fs.Pipe), rv = R(fs.Vent), rw = R(fs.Water);
                        if (Sink(f))
                        {
                            if (!f.Island) continue;                                                   // a wall sink: its drain only
                            // the island row as the office sets draw it: vent - hot - drain - cold, the vent and hot water on the
                            // plan-left of the drain looking at the faucet side (24 Skillman SL102)
                            lx = -lx; ly = -ly;
                            bool hot = fs.Water > 0 && waterAt.Any(m => m.System == "HotWater" && Dist(m.X, m.Y, f.X, f.Y) <= WaterFeet);
                            bool cold = fs.Water > 0 && waterAt.Any(m => m.System == "ColdWater" && Dist(m.X, m.Y, f.X, f.Y) <= WaterFeet);
                            double tl = rd;
                            if (hot) { tl += rw + gap; Add(s, "HW", "HotWater", fs.Water, f.X + lx * (tl), f.Y + ly * (tl), f.Code, ++owners); tl += rw; }
                            else if (fs.Water > 0) s.Missing.Add($"HW: no hot water pipe or riser within {WaterFeet:0} ft of the island sink on this floor");
                            if (fs.Vent > 0) { tl += rv + gap; Add(s, "V", "Vent", fs.Vent, f.X + lx * tl, f.Y + ly * tl, f.Code, ++owners); }
                            if (cold) { double tr = rd + rw + gap; Add(s, "CW", "ColdWater", fs.Water, f.X - lx * tr, f.Y - ly * tr, f.Code, ++owners); }
                            else if (fs.Water > 0) s.Missing.Add($"CW: no cold water pipe or riser within {WaterFeet:0} ft of the island sink on this floor");
                            continue;
                        }
                        if (fs.Vent <= 0) continue;
                        // a toilet's vent: in the wall, 5 1/4" off its centre line on the side away from its stack (manual);
                        // a lavatory's: beside its drain in the wall, toward the vent stack (its vent ties into it), else toward
                        // the waste stack; a fixture right beside a vent stack has none of its own (the stack serves it)
                        bool wc = Is(f, "WC");
                        var wall = Wall(f).Value;
                        var ventStack = placed.Where(v => v.Fixture == null && v.System == "Vent").OrderBy(v => Dist(v.X, v.Y, wall.X, wall.Y)).FirstOrDefault();
                        if (!wc && ventStack != null && Dist(ventStack.X, ventStack.Y, wall.X, wall.Y) <= VentStackServesFeet)
                        {
                            s.Notes.Add($"the {f.Code} beside the vent stack: no vent of its own (the stack serves it)");
                            continue;
                        }
                        int pref = 1;
                        if (f == toilet) pref = -side;
                        else if (toilet != null)
                        {
                            var st = s.StackSleeves.FirstOrDefault(v => v.System == "Vent") ?? s.StackSleeves.FirstOrDefault();
                            if (st != null) pref = (st.X - wall.X) * lx + (st.Y - wall.Y) * ly >= 0 ? 1 : -1;
                        }
                        double first = wc ? VentFromToiletFeet : rd + rv + gap;
                        if (f == toilet && a != null) { lx = a[0]; ly = a[1]; }                       // the stack's own axis
                        (double X, double Y)? spot = null;
                        for (double extra = 0; extra <= SlideFeet + 1e-9 && spot == null; extra += 1.0 / 12)
                            foreach (int sd in new[] { pref, -pref })
                            {
                                double x = wall.X + lx * sd * (first + extra), y = wall.Y + ly * sd * (first + extra);
                                if (Clear(x, y, rv)) { spot = (x, y); break; }
                            }
                        var at = spot ?? (wall.X + lx * pref * first, wall.Y + ly * pref * first);
                        Add(s, "V", "Vent", fs.Vent, at.X, at.Y, f.Code, ++owners);
                        if (spot == null) s.Notes.Add($"the {f.Code}'s vent overlaps a sleeve already there: move it by hand");
                    }

            int n = 0;
            foreach (var s in result.Stacks.OrderByDescending(s => Math.Round(s.Y)).ThenBy(s => s.X)) s.Id = "M" + ++n;
            result.Stacks = result.Stacks.OrderBy(s => int.Parse(s.Id.Substring(1))).ToList();
            return result;
        }

        private static List<List<Pipe>> Clusters(List<Pipe> pipes, double reach)
        {
            var groups = new List<List<Pipe>>();
            foreach (var p in pipes)
            {
                var touching = groups.Where(g => g.Any(o => Dist(o.X, o.Y, p.X, p.Y) <= reach)).ToList();
                var merged = new List<Pipe> { p };
                foreach (var g in touching) { merged.AddRange(g); groups.Remove(g); }
                groups.Add(merged);
            }
            return groups;
        }

        /// <summary>
        /// Pipe chases among the outlines the shaft lines draw (lines joined end to end within 1"): small (short side 4" to
        /// 2 ft, long side up to 4 ft) and holding no X (a duct shaft) and no circle (a chute): those are not plumbing.
        /// </summary>
        private static List<(double X0, double Y0, double X1, double Y1)> Chases(List<((double X, double Y) A, (double X, double Y) B)> lines)
        {
            var sets = new List<List<((double X, double Y) A, (double X, double Y) B)>>();
            const double join = 1.0 / 12;
            bool Touch(((double X, double Y) A, (double X, double Y) B) a, ((double X, double Y) A, (double X, double Y) B) b) =>
                new[] { a.A, a.B }.Any(p => new[] { b.A, b.B }.Any(q => Dist(p.X, p.Y, q.X, q.Y) <= join));
            foreach (var l in lines)
            {
                var touching = sets.Where(s => s.Any(o => Touch(o, l))).ToList();
                var merged = new List<((double X, double Y) A, (double X, double Y) B)> { l };
                foreach (var s in touching) { merged.AddRange(s); sets.Remove(s); }
                sets.Add(merged);
            }
            var boxes = new List<(double X0, double Y0, double X1, double Y1)>();
            foreach (var s in sets.Where(s => s.Count >= 3))
            {
                double x0 = s.Min(l => Math.Min(l.A.X, l.B.X)), y0 = s.Min(l => Math.Min(l.A.Y, l.B.Y));
                double x1 = s.Max(l => Math.Max(l.A.X, l.B.X)), y1 = s.Max(l => Math.Max(l.A.Y, l.B.Y));
                double shortSide = Math.Min(x1 - x0, y1 - y0), longSide = Math.Max(x1 - x0, y1 - y0);
                if (shortSide < ChaseMin || shortSide > ChaseShortMax || longSide > ChaseLongMax) continue;
                // an X (diagonal lines over half a foot) or a circle (many short pieces of a tessellated curve) inside
                bool x = s.Any(l => { double dx = Math.Abs(l.B.X - l.A.X), dy = Math.Abs(l.B.Y - l.A.Y); return dx > 0.34 * Math.Max(dx, dy) && dy > 0.34 * Math.Max(dx, dy) && Math.Sqrt(dx * dx + dy * dy) > 0.5; });
                bool round = s.Count(l => Dist(l.A.X, l.A.Y, l.B.X, l.B.Y) < 2.0 / 12) > 12;
                if (!x && !round) boxes.Add((x0, y0, x1, y1));
            }
            return boxes;
        }

        private static double BoxDist((double X0, double Y0, double X1, double Y1) b, (double X, double Y) p) =>
            Math.Sqrt(Math.Pow(Math.Max(Math.Max(b.X0 - p.X, 0), p.X - b.X1), 2) + Math.Pow(Math.Max(Math.Max(b.Y0 - p.Y, 0), p.Y - b.Y1), 2));

        private static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1));
    }
}
