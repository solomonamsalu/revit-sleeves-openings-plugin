using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fixture = SleevesOpenings.Automation.StackFinder.Fixture;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Vent Placer: a floor's vent (yellow) sleeves from the model's fixtures alone. The model decides where (each vent
    /// beside its own fixture's drain sleeve, in the wall behind that fixture), the rules how close (touching: the two
    /// sleeves' radii + plumbing.sleeveGap), and the Standards Manual's toilet sleeves page only which side:
    /// - a toilet: its vent touches its waste stack (the sanitary sleeve in the model there, else where Extract Fixtures
    ///   puts it), on the side toward the lavatory;
    /// - a wall-hung toilet: the waste on its centre line in the wall; reserve waste touching it on the side away from the
    ///   lavatory, reserve vent touching it toward the lavatory, the vent touching the reserve vent (manual);
    /// - a lavatory, a kitchen / laundry sink against a wall, a washer: the vent touches its drain on the toilet's side;
    /// - an island sink: in its own row past the hot water's place (24 Skillman SL102);
    /// - tubs and showers: none unless the rules give them a pipe (office 2026-10-12);
    /// - one vent stack per row with a toilet or a lavatory: touching the lavatory's drain on the side away from the toilet
    ///   (the end of the row); two rows back to back on one wall share it; it lines up with the same stack on the other
    ///   floors and takes its name.
    /// A spot with a vent already in the model gets nothing; a vent blocked by another sleeve slides on along its side,
    /// then tries the other side. Free of the Revit API. Feet.
    /// </summary>
    public static class VentFinder
    {
        /// <summary>Where Extract Fixtures puts a toilet's waste stack: this far (feet) along the wall from its centre line, on the outer side.</summary>
        private const double WasteFromToiletFeet = 4.25 / 12;
        /// <summary>A sanitary sleeve already in the model this close (feet) to where the toilet's waste stack goes is that stack.</summary>
        private const double WasteFoundFeet = 10.0 / 12;
        /// <summary>Two rows' walls are one when their lines are this close (feet): both faces of one wall.</summary>
        private const double SameWallFeet = 8.0 / 12;
        /// <summary>Rows on one wall this far apart along it (feet) at most are back to back: one vent stack for both.</summary>
        private const double BackToBackFeet = 8.0;
        /// <summary>A vent sleeve in the model in a row's wall (this close across it, feet) is the row's vent stack.</summary>
        private const double InWallFeet = 1.0;
        /// <summary>A fixture vent already in the model is named V-WC, V-LAV... (Auto Run, an earlier Vent Placer): not a vent stack.</summary>
        private static readonly Regex FixtureVentName = new Regex(@"^V-(WC|LAV|KS|LS|BT|SH|W/?D|UR|MS|SK)\b", RegexOptions.IgnoreCase);

        /// <summary>A sleeve already in the model on this floor.</summary>
        public class Existing
        {
            public double X, Y;
            /// <summary>Sanitary, Vent... ("?" when not known).</summary>
            public string System;
            /// <summary>Inches (assumed when the family does not say).</summary>
            public double Size = 4;
            /// <summary>The riser it was placed under (S-T1, V-WC); null = hand-placed.</summary>
            public string Name;
        }

        /// <summary>A vent stack laid out or found on another floor: this floor's lines up with it.</summary>
        public class StackSpot
        {
            public double X, Y;
            public string Name, Floor;
        }

        /// <summary>Vent stack names across the floors of one run: one name per spot up the building (V-T1, V-T2...).</summary>
        public class Namer
        {
            public List<StackSpot> Stacks = new List<StackSpot>();
            /// <summary>Numbers already taken by stacks in the model (S-T3 / V-T3): new stacks number past them.</summary>
            public int Last;

            public string Next() => "V-T" + ++Last;
        }

        public class Vent
        {
            /// <summary>The opening name: V-T1 (vent stack), V-WC, V-LAV, S-WC-RES (a wall-hung toilet's reserve waste).</summary>
            public string Name;
            /// <summary>"vent stack", "toilet vent", "lavatory vent", "sink vent", "washer vent", "island sink vent", "reserve waste", "reserve vent".</summary>
            public string Kind;
            public string Service, System;
            /// <summary>Pipe and sleeve size, inches.</summary>
            public double Pipe, Size;
            public double X, Y;
            /// <summary>The fixture it is for (WC, LAV...); null = the row's vent stack.</summary>
            public string Fixture;
            public string How;
            public List<string> Notes = new List<string>();
            public bool IsStack => Kind == "vent stack";
        }

        public class Row
        {
            /// <summary>R1..; "bathroom", "back to back", "lavatory", "kitchen sink", "island sink", "fixtures".</summary>
            public string Id, Kind;
            public double X, Y;
            public List<Fixture> Serves = new List<Fixture>();
            public List<Vent> Vents = new List<Vent>();
            public List<string> Notes = new List<string>();
            /// <summary>What it needs but this run does not place (a spot already sleeved, no wall known).</summary>
            public List<string> Skipped = new List<string>();
        }

        public class Result
        {
            public List<Row> Rows = new List<Row>();
            public IEnumerable<Vent> Vents => Rows.SelectMany(r => r.Vents);
        }

        private class Wet
        {
            public Row Row;
            public List<Fixture> Group;
            public Fixture Toilet, Axis;
            public (double X, double Y) W;
            public double Nx, Ny, Ax, Ay;
            /// <summary>The toilet's outer side along the wall (where Extract Fixtures puts its waste stack).</summary>
            public int Side = 1;
            /// <summary>Along the wall: the toilet's waste stack and its vent, once laid out.</summary>
            public double? ToiletWaste, ToiletVent;
            public double T(double x, double y) => (x - W.X) * Ax + (y - W.Y) * Ay;
            public double Across(double x, double y) => (x - W.X) * Nx + (y - W.Y) * Ny;
            public (double X, double Y) At(double t) => (W.X + Ax * t, W.Y + Ay * t);
        }

        /// <param name="fixtures">The floor's fixtures (Extract Fixtures' reading), with the wall behind each where known.</param>
        /// <param name="existing">Every sleeve already in the model on this floor.</param>
        /// <param name="namer">Vent stacks of the floors already done in this run (top down) and the names taken.</param>
        public static Result Find(List<Fixture> fixtures, List<Existing> existing, PlumbingRules rules, Namer namer, string floor = null)
        {
            var v = rules.Vents ?? new VentRules();
            var result = new Result();
            double gap = rules.SleeveGap / 12;
            double R(double pipe) => rules.SleeveFor(pipe) / 24;
            bool Is(Fixture f, string code) => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase);
            bool Sink(Fixture f) => Is(f, "KS") || Is(f, "LS");
            bool Tub(Fixture f) => Is(f, "BT") || Is(f, "SH");
            bool Washer(Fixture f) => Is(f, "W/D") || Is(f, "WD");
            var all = fixtures.Where(f => f.Code != null && rules.Fixtures.ContainsKey(f.Code) && !Is(f, "FD")).ToList();
            double ventPipe = rules.Fixtures.TryGetValue("WC", out var wcRule) && wcRule.Vent > 0 ? wcRule.Vent : 2;
            double wastePipe = rules.Services.TryGetValue("S", out var sRule) ? sRule.Pipe : 4;
            (double X, double Y) Drain(Fixture f) => f.Points.Count > 0 ? (f.Points.Average(p => p.X), f.Points.Average(p => p.Y)) : (f.X, f.Y);

            // what is there: the model's sleeves, the fixtures' drains (placed or not), the vents laid out here
            var taken = existing.Select(e => (e.X, e.Y, R: e.Size / 24)).ToList();
            foreach (var f in all)
                foreach (var p in f.Points.Count > 0 ? f.Points : new List<(double X, double Y)> { (f.X, f.Y) })
                    taken.Add((p.X, p.Y, R(rules.Fixtures[f.Code].Pipe)));
            bool Clear(double x, double y, double r) => taken.All(t => Dist(t.X, t.Y, x, y) >= t.R + r + gap - 1e-6);
            var vents = existing.Where(e => e.System == "Vent").ToList();
            Existing VentAt(double x, double y) => vents.FirstOrDefault(e => Dist(e.X, e.Y, x, y) <= v.ExistingWithin / 12);

            Vent Add(Wet w, string kind, string name, string service, string system, double pipe, double x, double y, string fixture, string how)
            {
                var vent = new Vent
                {
                    Name = name, Kind = kind, Service = service, System = system, Pipe = pipe, Size = rules.SleeveFor(pipe),
                    X = x, Y = y, Fixture = fixture, How = how
                };
                w.Row.Vents.Add(vent);
                taken.Add((x, y, vent.Size / 24));
                return vent;
            }

            // a sleeve touching the one at anchor (radius ra, along the row's wall) on its dir side; blocked = it slides on
            // along that side up to v.Slide, then the other side; null = a vent is already in the model there
            double? Touching(Wet w, string kind, string fixture, double pipe, double anchor, double ra, int dir, string how,
                             string service = "V", string system = "Vent", string name = null)
            {
                double r = R(pipe), touch = ra + r + gap;
                foreach (int s in new[] { dir, -dir })
                {
                    var q0 = w.At(anchor + s * touch);
                    var there = system == "Vent" ? VentAt(q0.X, q0.Y) : null;
                    if (there != null)
                    {
                        w.Row.Skipped.Add($"{kind} ({fixture ?? "row"}): a vent sleeve is already in the model there{(there.Name != null ? $" ({there.Name})" : "")}");
                        return w.T(there.X, there.Y);
                    }
                }
                foreach (int s in new[] { dir, -dir })
                    for (double extra = 0; extra <= v.Slide / 12 + 1e-9; extra += 1.0 / 12)
                    {
                        double t = anchor + s * (touch + extra);
                        var q = w.At(t);
                        if (!Clear(q.X, q.Y, r)) continue;
                        var vent = Add(w, kind, name ?? $"{service}-{fixture}", service, system, pipe, q.X, q.Y, fixture, how);
                        if (s != dir) vent.Notes.Add("its side is taken by other sleeves: placed on the other side");
                        if (extra > 1e-6) vent.Notes.Add($"moved {extra * 12:0}\" along the wall to clear the sleeves there");
                        return t;
                    }
                var p = w.At(anchor + dir * touch);
                Add(w, kind, name ?? $"{service}-{fixture}", service, system, pipe, p.X, p.Y, fixture, how).Notes.Add("overlaps a sleeve already there: move it by hand");
                return anchor + dir * touch;
            }

            // ---- the rows: the wet groups Extract Stacks makes, each on the wall of its toilet (else its first fixture with a wall)
            var groups = StackFinder.WetGroups(all, out var loose);
            var wets = new List<Wet>();
            foreach (var g in groups)
            {
                var toilet = g.Where(f => Is(f, "WC") && f.Back != null).OrderBy(f => g.Average(o => Dist(o.X, o.Y, f.X, f.Y))).FirstOrDefault();
                var axis = toilet ?? g.FirstOrDefault(f => f.Back != null);
                if (axis == null) { loose.AddRange(g); continue; }
                var wp = StackFinder.WallPoint(axis).Value;
                var w = new Wet { Group = g, Toilet = toilet, Axis = axis, W = wp, Nx = axis.Back[0], Ny = axis.Back[1], Ax = -axis.Back[1], Ay = axis.Back[0] };
                bool island = g.Count == 1 && g[0].Island;
                w.Row = new Row
                {
                    Kind = toilet != null ? "bathroom" : island ? "island sink" : g.Any(f => Is(f, "LAV")) ? "lavatory" : g.All(Sink) ? "kitchen sink" : "fixtures",
                    X = wp.X, Y = wp.Y
                };
                w.Row.Serves.AddRange(g);
                if (toilet != null)
                {
                    // the side Extract Fixtures puts the waste stack on: away from the other fixtures (StackFinder)
                    var others = g.Where(f => f != toilet).Select(f => StackFinder.WallPoint(f) ?? (f.X, f.Y)).ToList();
                    double mean = others.Count > 0 ? others.Average(p => w.T(p.X, p.Y)) : 0;
                    w.Side = mean > 1e-6 ? -1 : 1;
                }
                wets.Add(w);
            }

            // the lavatories in a row's wall, along it
            List<double> Lavs(Wet w) => w.Group.Where(f => Is(f, "LAV") && f.Back != null)
                                              .Select(Drain).Where(d => Math.Abs(w.Across(d.X, d.Y)) <= SameWallFeet).Select(d => w.T(d.X, d.Y)).ToList();

            foreach (var w in wets)
            {
                var row = w.Row;
                var lavs = Lavs(w);

                // ---- the toilet: its vent touches its waste stack, on the side toward the lavatory (manual)
                if (w.Toilet != null)
                {
                    var t = w.Toilet;
                    double rs = R(wastePipe);
                    double tw;
                    if (t.WallHung) tw = 0;                                              // its drain in the wall, on the centre line
                    else
                    {
                        tw = w.Side * WasteFromToiletFeet;
                        var at = w.At(tw);
                        var waste = existing.Where(e => e.System == "Sanitary" && Dist(e.X, e.Y, at.X, at.Y) <= WasteFoundFeet)
                                            .OrderBy(e => Dist(e.X, e.Y, at.X, at.Y)).FirstOrDefault();
                        if (waste != null) { tw = w.T(waste.X, waste.Y); rs = waste.Size / 24; }
                        else
                        {
                            taken.Add((at.X, at.Y, rs));
                            row.Notes.Add("the toilet's waste stack is not in the model yet (Extract Fixtures -> Place sleeves adds it): its vent is laid out from where it goes");
                        }
                    }
                    w.ToiletWaste = tw;
                    // toward the lavatory (the nearest in the wall); none: toward the rest of the row
                    int dir = lavs.Count > 0 ? (lavs.OrderBy(l => Math.Abs(l - tw)).First() >= tw ? 1 : -1) : -w.Side;
                    if (t.WallHung)
                    {
                        row.Notes.Add("wall-hung toilet: its waste in the wall on its centre line");
                        if (v.WallHungReserve)
                        {
                            Touching(w, "reserve waste", "WC", wastePipe, tw, rs, -dir, "reserve waste touching the wall-hung toilet's waste, away from the lavatory (manual)",
                                     "S", "Sanitary", "S-WC-RES");
                            double tr = Touching(w, "reserve vent", "WC", ventPipe, tw, rs, dir, "reserve vent touching the wall-hung toilet's waste, toward the lavatory (manual)",
                                                 name: "V-WC-RES") ?? tw;
                            w.ToiletVent = Touching(w, "toilet vent", "WC", ventPipe, tr, R(ventPipe), dir, "touching the reserve vent, toward the lavatory (manual)");
                        }
                        else w.ToiletVent = Touching(w, "toilet vent", "WC", ventPipe, tw, rs, dir, "touching the wall-hung toilet's waste, toward the lavatory (manual)");
                    }
                    else w.ToiletVent = Touching(w, "toilet vent", "WC", ventPipe, tw, rs, dir, "touching the toilet's waste stack, toward the lavatory (manual)");
                }

                // ---- the other fixtures of the row: each vent touches its own drain, on the toilet's side
                foreach (var f in w.Group.Where(f => f != w.Toilet))
                {
                    var fs = rules.Fixtures[f.Code];
                    var (dx, dy) = Drain(f);
                    if (Tub(f))
                    {
                        // office 2026-10-12: no tub vent unless the office standard or the engineer's drawing asks for one
                        double pipe = Is(f, "BT") ? v.TubPipe : v.ShowerPipe;
                        if (pipe <= 0) continue;
                        double across = Math.Abs(w.Across(dx, dy));
                        if (across > v.TubReach / 12)
                        {
                            row.Skipped.Add($"{f.Code} vent: its drain is {across * 12:0}\" from the row's wall (more than {v.TubReach:0}\"): add it by hand in the nearest wall");
                            continue;
                        }
                        Touching(w, Is(f, "BT") ? "tub vent" : "shower vent", f.Code, pipe, w.T(dx, dy), -R(pipe) - gap, 1, $"in the row's wall at the {fs.Name}'s drain");
                        continue;
                    }
                    if (f.Back == null)
                    {
                        if (fs.Vent > 0 || Washer(f)) row.Skipped.Add($"{f.Code} vent: the wall behind the {fs.Name} is not known: add it by hand");
                        continue;
                    }
                    double rd = R(fs.Pipe);
                    if (f.Island)
                    {
                        if (fs.Vent <= 0) continue;
                        // its own row: vent - hot - drain - cold, the vent past the hot water's place (24 Skillman SL102, as Extract Stacks)
                        double rv = R(fs.Vent), lx = f.Back[1], ly = -f.Back[0];
                        double tl = rd + (fs.Water > 0 ? 2 * R(fs.Water) + gap : 0) + gap + rv;
                        double x = dx + lx * tl, y = dy + ly * tl;
                        if (VentAt(x, y) != null) { row.Skipped.Add($"{f.Code} vent: a vent sleeve is already in the model there"); continue; }
                        bool clear = Clear(x, y, rv);
                        var vent = Add(w, "island sink vent", $"V-{f.Code}", "V", "Vent", fs.Vent, x, y, f.Code, "in the island sink's own row, past the hot water's place (24 Skillman SL102)");
                        if (!clear) vent.Notes.Add("overlaps a sleeve already there: move it by hand");
                        continue;
                    }
                    double pipeV = Washer(f) ? v.WasherPipe : fs.Vent;
                    if (pipeV <= 0) continue;
                    // in the row's wall, else (a sink on another wall of the room) in its own wall
                    bool sameWall = Math.Abs(w.Across(dx, dy)) <= SameWallFeet && Math.Abs(f.Back[0] * w.Nx + f.Back[1] * w.Ny) >= 0.95;
                    var own = sameWall ? w : new Wet { Row = row, Group = w.Group, W = (dx, dy), Nx = f.Back[0], Ny = f.Back[1], Ax = -f.Back[1], Ay = f.Back[0] };
                    double t0 = own.T(dx, dy);
                    // the toilet's side (its centre line); no toilet: toward the row's other fixtures
                    int side = 1;
                    if (w.Toilet != null)
                    {
                        var tp = StackFinder.WallPoint(w.Toilet).Value;
                        side = own.T(tp.X, tp.Y) >= t0 ? 1 : -1;
                    }
                    else
                    {
                        var others = w.Group.Where(o => o != f).Select(Drain).ToList();
                        if (others.Count > 0) side = others.Average(o => own.T(o.X, o.Y)) >= t0 ? 1 : -1;
                    }
                    string kind = Is(f, "LAV") ? "lavatory vent" : Washer(f) ? "washer vent" : "sink vent";
                    Touching(own, kind, f.Code, pipeV, t0, rd, side, $"touching the {fs.Name}'s drain in the wall behind it, on the toilet's side (manual)");
                }
            }

            // ---- one vent stack per row with a toilet or a lavatory, closing the row; two rows back to back share one
            double stackPipe = v.StackPipe;
            var done = new List<(Wet W, Vent S)>();
            var shared = new List<(Row Row, Row With, string Name)>();
            foreach (var w in wets.Where(w => w.Toilet != null || w.Group.Any(f => Is(f, "LAV"))))
            {
                if (stackPipe <= 0) break;
                var row = w.Row;
                var lavs = Lavs(w);
                var ts = w.Group.Where(f => f.Back != null && !Tub(f)).Select(Drain).Where(d => Math.Abs(w.Across(d.X, d.Y)) <= SameWallFeet)
                          .Select(d => w.T(d.X, d.Y)).Concat(row.Vents.Select(o => w.T(o.X, o.Y))).ToList();
                if (w.ToiletWaste.HasValue) ts.Add(w.ToiletWaste.Value);
                if (ts.Count == 0) continue;

                // touching the lavatory's drain farthest from the toilet, on the side away from it (manual: the row's end);
                // no lavatory: touching the toilet's vent, on the row's side
                double anchor, ra; int dir;
                if (lavs.Count > 0)
                {
                    double tToilet = w.Toilet != null ? 0 : lavs.Average();
                    anchor = w.Toilet != null ? lavs.OrderByDescending(l => Math.Abs(l - tToilet)).First() : lavs.Max();
                    dir = w.Toilet != null ? (anchor >= tToilet ? 1 : -1) : 1;
                    if (w.Toilet == null)
                    {
                        // away from the lavatory's own vent
                        var lv = row.Vents.FirstOrDefault(o => o.Kind == "lavatory vent");
                        if (lv != null) dir = w.T(lv.X, lv.Y) > anchor ? -1 : 1;
                    }
                    ra = R(rules.Fixtures["LAV"].Pipe);
                }
                else
                {
                    anchor = w.ToiletVent ?? w.ToiletWaste ?? 0;
                    dir = w.ToiletVent.HasValue && w.ToiletWaste.HasValue ? (w.ToiletVent.Value >= w.ToiletWaste.Value ? 1 : -1) : -w.Side;
                    ra = R(ventPipe);
                }
                double lo = ts.Min() - 2, hi = ts.Max() + 2;

                // a vent stack already in the model in this wall (not a fixture's vent)
                var inModel = vents.Where(e => (e.Name == null || !FixtureVentName.IsMatch(e.Name)) && Math.Abs(w.Across(e.X, e.Y)) <= InWallFeet)
                                   .Where(e => { double t = w.T(e.X, e.Y); return t >= lo && t <= hi; }).FirstOrDefault();
                if (inModel != null) { row.Notes.Add($"vent stack already in the model in this wall{(inModel.Name != null ? $" ({inModel.Name})" : "")}"); continue; }

                // back to back: the other face of this wall already has its stack
                var pair = done.FirstOrDefault(d => d.W.Nx * w.Nx + d.W.Ny * w.Ny <= -0.95 && Math.Abs(w.Across(d.S.X, d.S.Y)) <= SameWallFeet + InWallFeet &&
                                                    w.T(d.S.X, d.S.Y) >= ts.Min() - BackToBackFeet && w.T(d.S.X, d.S.Y) <= ts.Max() + BackToBackFeet);
                if (pair.S != null)
                {
                    row.Kind = pair.W.Row.Kind = "back to back";
                    shared.Add((row, pair.W.Row, pair.S.Name));
                    pair.S.Notes.Add("shared by the bathrooms on both faces of the wall (back to back)");
                    continue;
                }

                var p0 = w.At(anchor + dir * (ra + R(stackPipe) + gap));
                // the same stack on the floors done before: line up with it (risers run straight, manual 22-23)
                var same = namer.Stacks.Where(s => s.Floor != floor && Dist(s.X, s.Y, p0.X, p0.Y) <= v.StackSameAs / 12).OrderBy(s => Dist(s.X, s.Y, p0.X, p0.Y)).FirstOrDefault();
                Vent stack = null;
                if (same != null && Clear(same.X, same.Y, R(stackPipe)))
                    stack = Add(w, "vent stack", same.Name, "V", "Vent", stackPipe, same.X, same.Y, null,
                                $"in line with {same.Name} on {same.Floor} ({Dist(same.X, same.Y, p0.X, p0.Y) * 12:0}\" from this row's own spot)");
                else
                {
                    int before = row.Vents.Count;
                    Touching(w, "vent stack", null, stackPipe, anchor, ra, dir,
                             lavs.Count > 0 ? "touching the lavatory's drain, on the side away from the toilet: the end of the row (manual)"
                                            : "touching the toilet's vent, at the end of the row (no lavatory in this wall)", name: "?");
                    stack = row.Vents.Count > before ? row.Vents.Last() : null;
                    if (stack != null)
                    {
                        stack.Name = same?.Name ?? namer.Next();
                        if (same != null) stack.Notes.Add($"{same.Name} on {same.Floor} is {Dist(same.X, same.Y, stack.X, stack.Y) * 12:0}\" away but its spot is taken here: offset");
                    }
                }
                if (stack == null) continue;
                if (!namer.Stacks.Any(s => s.Name == stack.Name && s.Floor == floor))
                    namer.Stacks.Add(new StackSpot { X = stack.X, Y = stack.Y, Name = stack.Name, Floor = floor });
                done.Add((w, stack));
            }

            // ---- fixtures with no row: nothing to place against
            var rest = new Row { Kind = "fixtures", X = loose.FirstOrDefault()?.X ?? 0, Y = loose.FirstOrDefault()?.Y ?? 0 };
            foreach (var f in loose.Where(f => rules.Fixtures[f.Code].Vent > 0 || (Is(f, "BT") && v.TubPipe > 0) || (Is(f, "SH") && v.ShowerPipe > 0)))
            {
                rest.Serves.Add(f);
                rest.Skipped.Add($"{f.Code} vent: no wall known and no room with a wall: add it by hand");
            }

            int n = 0;
            var rows = wets.Select(w => w.Row).Concat(rest.Serves.Count > 0 ? new[] { rest } : new Row[0])
                           .OrderByDescending(r => Math.Round(r.Y)).ThenBy(r => r.X).ToList();
            foreach (var r in rows) r.Id = "R" + ++n;
            foreach (var (r, with, name) in shared)
            {
                r.Notes.Add($"back to back with {with.Id}: shares its vent stack {name} (manual, double toilet)");
                with.Notes.Add($"back to back with {r.Id}: its vent stack {name} serves both");
            }
            result.Rows = rows;
            return result;
        }

        private static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1));
    }
}
