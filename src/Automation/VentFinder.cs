using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fixture = SleevesOpenings.Automation.StackFinder.Fixture;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Vent Placer: a floor's vent (yellow) sleeves from the model's fixtures alone, as the Standards Manual's toilet
    /// sleeves page draws each bathroom's wet-wall row (regular toilet: G6 | Y4 5 1/4" | Y4 7" | ... | G4 | 1'-2 3/4" | Y6):
    /// - a toilet: a vent touching its waste stack in the wall (5 1/4" c-c), on the side toward the rest of the row; the
    ///   waste stack is the sanitary sleeve already in the model there, else where Extract Fixtures puts it (4 1/4" off the
    ///   toilet's centre line, outer side);
    /// - a wall-hung toilet: the reserve waste and vent the manual draws, the vent 7" past the reserve vent (flagged);
    /// - a lavatory: 7" past the toilet's vent toward it, else 10" from its drain on the toilet's side; none when a vent
    ///   is already right there (the wall-hung row's vent serves both);
    /// - a kitchen / laundry sink against a wall: a vent touching its drain; an island sink: in its own row past the hot
    ///   water's place (24 Skillman SL102);
    /// - a tub: a vent in its row's wall at its drain; a washer: 2'-0" along the wall from its drain;
    /// - one vent stack per row with a toilet or a lavatory, past the last drain away from the toilet; two rows back to
    ///   back on one wall share it; it lines up with the same stack on the other floors and takes its name.
    /// A spot with a vent already in the model gets nothing; a vent slides along the wall to clear the sleeves there.
    /// Every sleeve is flagged for a check. Free of the Revit API. Feet.
    /// </summary>
    public static class VentFinder
    {
        /// <summary>The toilet's waste stack: this far (feet) along the wall from its centre line (manual: 4 1/4"), on the outer side.</summary>
        private const double WasteFromToiletFeet = 4.25 / 12;
        /// <summary>A sanitary sleeve already in the model this close (feet) to where the toilet's waste stack goes is that stack.</summary>
        private const double WasteFoundFeet = 10.0 / 12;
        /// <summary>Two rows' walls are one when their lines are this close (feet): both faces of one wall.</summary>
        private const double SameWallFeet = 8.0 / 12;
        /// <summary>Rows on one wall this far apart along it (feet) at most are back to back: one vent stack for both.</summary>
        private const double BackToBackFeet = 8.0;
        /// <summary>A vent sleeve in the model in a row's wall (this close across it, feet) is the row's vent stack.</summary>
        private const double InWallFeet = 1.0;
        /// <summary>A lavatory's vent no farther (feet) from its drain than this (the trap arm of a 1 1/2" drain, IPC table 906.1).</summary>
        private const double TrapArmFeet = 6.0;
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
            /// <summary>"vent stack", "toilet vent", "lavatory vent", "tub vent", "sink vent", "washer vent", "island sink vent", "reserve waste", "reserve vent".</summary>
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
            /// <summary>What it needs but this run does not place (a spot already sleeved, a tub too far from the wall).</summary>
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
            /// <summary>The toilet's outer side along the wall (its waste stack's side); the row lies on -Side.</summary>
            public int Side = 1;
            public double? ToiletVent;
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

            // what is there: the model's sleeves, the fixtures' drains (placed or not), the vents laid out here
            var taken = existing.Select(e => (e.X, e.Y, R: e.Size / 24)).ToList();
            foreach (var f in all)
                foreach (var p in f.Points.Count > 0 ? f.Points : new List<(double X, double Y)> { (f.X, f.Y) })
                    taken.Add((p.X, p.Y, R(rules.Fixtures[f.Code].Pipe)));
            // slack: closer than the sleeve gap allows (the manual's toilet vent touches its waste stack, 5 1/4" c-c)
            bool Clear(double x, double y, double r, double slack = 0) => taken.All(t => Dist(t.X, t.Y, x, y) >= t.R + r + gap - slack - 1e-6);
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

            // a vent at t along the row's wall, slid up to v.Slide along it (dir first) to clear what is there; null = one in the model already
            double? Along(Wet w, string kind, string fixture, double pipe, double t0, int dir, string how, string service = "V", string system = "Vent", string name = null,
                          double slack = 0)
            {
                var p0 = w.At(t0);
                var there = system == "Vent" ? VentAt(p0.X, p0.Y) : null;
                if (there != null)
                {
                    w.Row.Skipped.Add($"{kind} ({fixture}): a vent sleeve is already in the model there{(there.Name != null ? $" ({there.Name})" : "")}");
                    return w.T(there.X, there.Y);
                }
                double r = R(pipe);
                double? at = null;
                for (double extra = 0; extra <= v.Slide / 12 + 1e-9 && at == null; extra += 1.0 / 12)
                    foreach (int s in new[] { dir, -dir })
                    {
                        var q = w.At(t0 + s * extra);
                        if (Clear(q.X, q.Y, r, slack)) { at = t0 + s * extra; break; }
                        if (extra == 0) break;
                    }
                var p = w.At(at ?? t0);
                var vent = Add(w, kind, name ?? $"{service}-{fixture}", service, system, pipe, p.X, p.Y, fixture, how);
                if (at == null) vent.Notes.Add("overlaps a sleeve already there: move it by hand");
                else if (Math.Abs(at.Value - t0) > 1e-6) vent.Notes.Add($"moved {Math.Abs(at.Value - t0) * 12:0}\" along the wall to clear the sleeves there");
                return at ?? t0;
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
                    var others = g.Where(f => f != toilet).Select(f => StackFinder.WallPoint(f) ?? (f.X, f.Y)).ToList();
                    double mean = others.Count > 0 ? others.Average(p => w.T(p.X, p.Y)) : 0;
                    w.Side = mean > 1e-6 ? -1 : 1;                                       // outer side: away from the other fixtures
                }
                wets.Add(w);
            }

            foreach (var w in wets)
            {
                var row = w.Row;
                int inner = -w.Side;

                // ---- the toilet: its vent touches its waste stack in the wall
                if (w.Toilet != null)
                {
                    var t = w.Toilet;
                    if (t.WallHung)
                    {
                        // the waste goes in the wall on the centre line (its drain); the reserves the manual draws
                        row.Notes.Add("wall-hung toilet: verify the toilet type (manual: always verify, especially wall-hung)");
                        double tv = inner * v.ToiletVentFromWaste / 12;
                        if (v.WallHungReserve)
                        {
                            Along(w, "reserve waste", "WC", rules.Services.TryGetValue("S", out var s) ? s.Pipe : 4, w.Side * v.ReserveWasteOffset / 12, w.Side,
                                  $"reserve waste in the wall behind the wall-hung toilet, {v.ReserveWasteOffset:0.##}\" off its centre line (manual)",
                                  "S", "Sanitary", "S-WC-RES");
                            double tr = Along(w, "reserve vent", "WC", ventPipe, inner * v.ReserveVentOffset / 12, inner,
                                              $"reserve vent {v.ReserveVentOffset:0.##}\" off the wall-hung toilet's centre line (manual)", name: "V-WC-RES") ?? 0;
                            tv = tr + inner * v.LavVentPastToiletVent / 12;
                        }
                        w.ToiletVent = Along(w, "toilet vent", "WC", ventPipe, tv, inner, "in the wall behind the wall-hung toilet (manual: 7\" past the reserve vent)");
                    }
                    else
                    {
                        double tw = w.Side * WasteFromToiletFeet;
                        var at = w.At(tw);
                        var waste = existing.Where(e => e.System == "Sanitary" && Dist(e.X, e.Y, at.X, at.Y) <= WasteFoundFeet)
                                            .OrderBy(e => Dist(e.X, e.Y, at.X, at.Y)).FirstOrDefault();
                        string how;
                        if (waste != null)
                        {
                            tw = w.T(waste.X, waste.Y);
                            how = $"touching the toilet's waste stack{(waste.Name != null ? $" {waste.Name}" : "")} in the model, {v.ToiletVentFromWaste:0.##}\" c-c (manual)";
                        }
                        else
                        {
                            taken.Add((at.X, at.Y, R(rules.Services.TryGetValue("S", out var s) ? s.Pipe : 4)));
                            how = $"{v.ToiletVentFromWaste:0.##}\" from where the toilet's waste stack goes ({WasteFromToiletFeet * 12:0.##}\" off its centre line, manual)";
                            row.Notes.Add("the toilet's waste stack is not in the model yet (Extract Fixtures -> Place sleeves adds it): its vent is laid out from where it goes");
                        }
                        double touch = Math.Max(0, R(rules.Services.TryGetValue("S", out var sv) ? sv.Pipe : 4) + R(ventPipe) + gap - v.ToiletVentFromWaste / 12);
                        w.ToiletVent = Along(w, "toilet vent", "WC", ventPipe, tw + inner * v.ToiletVentFromWaste / 12, inner, how, slack: touch);
                    }
                }

                // ---- the other fixtures of the row
                foreach (var f in w.Group.Where(f => f != w.Toilet))
                {
                    var fs = rules.Fixtures[f.Code];
                    double dx = f.Points.Count > 0 ? f.Points.Average(p => p.X) : f.X, dy = f.Points.Count > 0 ? f.Points.Average(p => p.Y) : f.Y;
                    if (Tub(f))
                    {
                        double pipe = Is(f, "BT") ? v.TubPipe : v.ShowerPipe;
                        if (pipe <= 0) continue;
                        double across = Math.Abs(w.Across(dx, dy));
                        if (across > v.TubReach / 12)
                        {
                            row.Skipped.Add($"{f.Code} vent: its drain is {across * 12:0}\" from the row's wall (more than {v.TubReach:0}\"): add it by hand in the nearest wall");
                            continue;
                        }
                        int toward = w.ToiletVent.HasValue && w.ToiletVent.Value > w.T(dx, dy) ? 1 : -1;
                        Along(w, Is(f, "BT") ? "tub vent" : "shower vent", f.Code, pipe, w.T(dx, dy), toward,
                              $"in the row's wall at the {fs.Name}'s drain ({across * 12:0}\" out from it)");
                        continue;
                    }
                    if (f.Back == null)
                    {
                        row.Skipped.Add($"{f.Code} vent: the wall behind the {fs.Name} is not known: add it by hand");
                        continue;
                    }
                    if (Washer(f))
                    {
                        if (v.WasherPipe <= 0) continue;
                        double td = w.T(dx, dy);
                        int dir = w.ToiletVent.HasValue ? (w.ToiletVent.Value > td ? 1 : -1) : 1;
                        Along(w, "washer vent", f.Code, v.WasherPipe, td + dir * v.WasherAlong / 12, dir, $"{v.WasherAlong:0.##}\" along the wall from the washer's drain (Wallabout video)");
                        continue;
                    }
                    if (fs.Vent <= 0) continue;
                    double rd = R(fs.Pipe), rv = R(fs.Vent);
                    if (f.Island)
                    {
                        // its own row: vent - hot - drain - cold, the vent past the hot water's place (24 Skillman SL102, as Extract Stacks)
                        double lx = f.Back[1], ly = -f.Back[0];
                        double tl = rd + (fs.Water > 0 ? 2 * R(fs.Water) + gap : 0) + gap + rv;
                        double x = dx + lx * tl, y = dy + ly * tl;
                        if (VentAt(x, y) != null) { row.Skipped.Add($"{f.Code} vent: a vent sleeve is already in the model there"); continue; }
                        bool clear = Clear(x, y, rv);
                        var vent = Add(w, "island sink vent", $"V-{f.Code}", "V", "Vent", fs.Vent, x, y, f.Code, "in the island sink's own row, past the hot water's place (24 Skillman SL102)");
                        if (!clear) vent.Notes.Add("overlaps a sleeve already there: move it by hand");
                        continue;
                    }
                    // a sink or lavatory on another wall of the room: its vent in its own wall, beside its drain
                    bool sameWall = Math.Abs(w.Across(dx, dy)) <= SameWallFeet && Math.Abs(f.Back[0] * w.Nx + f.Back[1] * w.Ny) >= 0.95;
                    var own = sameWall ? w : new Wet { Row = row, Group = w.Group, W = (dx, dy), Nx = f.Back[0], Ny = f.Back[1], Ax = -f.Back[1], Ay = f.Back[0] };
                    double t0 = own.T(dx, dy);
                    int side = 1;
                    if (w.ToiletVent.HasValue && sameWall) side = w.ToiletVent.Value > t0 ? 1 : -1;
                    else if (w.Toilet != null)
                    {
                        var tp = StackFinder.WallPoint(w.Toilet).Value;
                        side = own.T(tp.X, tp.Y) > t0 ? 1 : -1;
                    }
                    if (Is(f, "LAV"))
                    {
                        // manual: 7" past the toilet's vent toward the lavatory, else 10" from its drain on the toilet's side
                        double near = rd + rv + gap;
                        if (sameWall && w.ToiletVent.HasValue)
                        {
                            double t1 = w.ToiletVent.Value - side * v.LavVentPastToiletVent / 12;
                            var q = own.At(t1);
                            if ((t1 - t0) * side >= near - 1e-6 && Math.Abs(t1 - t0) <= TrapArmFeet && Clear(q.X, q.Y, rv) && VentAt(q.X, q.Y) == null)
                            {
                                Along(own, "lavatory vent", f.Code, fs.Vent, t1, -side, $"{v.LavVentPastToiletVent:0.##}\" past the toilet's vent toward the lavatory (manual)");
                                continue;
                            }
                        }
                        double t2 = t0 + side * v.LavVentFromDrain / 12;
                        var q2 = own.At(t2);
                        if (!Clear(q2.X, q2.Y, rv))
                        {
                            var by = w.Row.Vents.FirstOrDefault(o => o.System == "Vent" && Dist(o.X, o.Y, q2.X, q2.Y) <= 1.0);
                            if (by != null) { row.Notes.Add($"the LAV's vent: the {by.Name} {Dist(by.X, by.Y, q2.X, q2.Y) * 12:0}\" from its spot serves it (manual, wall-hung row)"); continue; }
                        }
                        Along(own, "lavatory vent", f.Code, fs.Vent, t2, side, $"{v.LavVentFromDrain:0.##}\" from the lavatory's drain, on the toilet's side (manual)");
                        continue;
                    }
                    // a kitchen / laundry sink against a wall: its vent touching its drain
                    Along(own, "sink vent", f.Code, fs.Vent, t0 + side * (rd + rv + gap), side, $"beside the {fs.Name}'s drain in the wall behind it");
                }
            }

            // ---- one vent stack per row with a toilet or a lavatory; two rows back to back share one
            double stackPipe = v.StackPipe;
            var done = new List<(Wet W, Vent S)>();
            var shared = new List<(Row Row, Row With, string Name)>();
            foreach (var w in wets.Where(w => w.Toilet != null || w.Group.Any(f => Is(f, "LAV"))))
            {
                if (stackPipe <= 0) break;
                var row = w.Row;
                int inner = -w.Side;
                // the drains in its wall (the toilet's on its centre line) and the vents laid out there
                var lavs = w.Group.Where(f => Is(f, "LAV") && f.Back != null && Math.Abs(w.Across(f.X, f.Y)) <= SameWallFeet).Select(f => w.T(f.X, f.Y)).ToList();
                var ts = w.Group.Where(f => f.Back != null && Math.Abs(w.Across(f.X, f.Y)) <= SameWallFeet && !Tub(f)).Select(f => w.T(f.X, f.Y))
                          .Concat(row.Vents.Select(o => w.T(o.X, o.Y))).ToList();
                if (w.Toilet != null) ts.Add(0);
                // past the lavatory farthest from the toilet, away from it (manual: 10 1/4" to 1'-2 3/4" past the LAV's drain);
                // no lavatory in the wall: past the toilet's vent, on the row's side
                double last; int dir;
                if (lavs.Count > 0)
                {
                    last = w.Toilet != null ? lavs.OrderByDescending(Math.Abs).First() : lavs.Max();
                    dir = w.Toilet != null ? (last >= 0 ? 1 : -1) : 1;
                }
                else { last = w.ToiletVent ?? 0; dir = inner; }
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

                double t0 = last + dir * v.StackPastLastDrain / 12;
                var p0 = w.At(t0);
                // the same stack on the floors done before: line up with it (risers run straight, manual 22-23)
                var same = namer.Stacks.Where(s => s.Floor != floor && Dist(s.X, s.Y, p0.X, p0.Y) <= v.StackSameAs / 12).OrderBy(s => Dist(s.X, s.Y, p0.X, p0.Y)).FirstOrDefault();
                Vent stack = null;
                if (same != null && Clear(same.X, same.Y, R(stackPipe)))
                {
                    stack = Add(w, "vent stack", same.Name, "V", "Vent", stackPipe, same.X, same.Y, null,
                                $"in line with {same.Name} on {same.Floor} ({Dist(same.X, same.Y, p0.X, p0.Y) * 12:0}\" from this row's own spot)");
                }
                else
                {
                    int before = row.Vents.Count;
                    Along(w, "vent stack", null, stackPipe, t0, dir,
                          lavs.Count > 0 ? $"{v.StackPastLastDrain:0.##}\" past the lavatory's drain, away from the toilet (manual)"
                                         : $"{v.StackPastLastDrain:0.##}\" past the toilet's vent, on the row's side (no lavatory in this wall)", name: "?");
                    stack = row.Vents.Count > before ? row.Vents.Last() : null;
                    if (stack != null)
                    {
                        stack.Name = same?.Name ?? namer.Next();
                        if (same != null) stack.Notes.Add($"{same.Name} on {same.Floor} is {Dist(same.X, same.Y, stack.X, stack.Y) * 12:0}\" away but its spot is taken here: offset, check");
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
