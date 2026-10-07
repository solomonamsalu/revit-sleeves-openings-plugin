using System;
using System.Collections.Generic;
using System.Linq;

namespace SleevesOpenings.Automation.Assembly
{
    /// <summary>
    /// Openings next to each other on one slab, once their sizes are known (after the riser diagram, before the S&amp;O
    /// comparison and placing). The ERV supply and exhaust are one group (rule 54): on floors they sit floorCenterToCenter
    /// apart (the office's S&amp;O sets draw them 12" c-c) and never overlap; on the roof exactly roofGap apart (rule 55).
    /// Both openings move the same distance, so the pair stays centred on the drawing. A dryer shaft (ducts closer than the
    /// sleeve spacing) gets one Regular Opening around all its ducts on floors (rule 74), and on the roof a 6"x6" per duct in
    /// straight rows (rules 78-82). A dryer opening that lands on another opening makes that opening bigger to take it (rule 74:
    /// dryer circles inside a mechanical opening box). Openings that still overlap become one opening around both (manual 21).
    /// Each decision can be switched off (rules.json automation.decisions); switched off, the case goes to review.
    /// Free of the Revit API.
    /// </summary>
    public static class OpeningLayout
    {
        /// <param name="size">Opening size in inches across (Revit X) and along (Revit Y), clearances included; null = unknown.</param>
        /// <param name="pairReach">Inches: ERV openings farther apart than this are not one pair.</param>
        public static void Apply(RiserAssembly assembly, Func<Crossing, (double W, double L)?> size,
                                 double floorCenterToCenter = 12, double roofGap = 24, double dryerDiameter = 4, bool dryerShaftBox = true,
                                 double pairReach = 72, bool firstNumberEastWest = false, DecisionRules decisions = null,
                                 double roofDryerWidth = 6, double roofDryerLength = 6, double dryerGap = 8)
        {
            var d = decisions ?? new DecisionRules { DryersIntoHost = false, RoofDryerGrid = false, MergeOverlaps = false };
            if (firstNumberEastWest) EastWest(assembly);
            if (dryerShaftBox) ShaftBoxes(assembly, size, d.RoofDryerGrid ? (roofDryerWidth, roofDryerLength, dryerGap, Math.Max(1, d.RoofDryerPerRow)) : ((double, double, double, int)?)null);
            foreach (var slab in assembly.Crossings.Where(c => c.Status == Crossing.Place && c.HasPosition).GroupBy(c => c.Floor))
                ErvPairs(slab.Where(c => c.System == "ERV").ToList(), size, floorCenterToCenter, roofGap, pairReach);
            foreach (var slab in assembly.Crossings.Where(c => c.Status == Crossing.Place && c.HasPosition).GroupBy(c => c.Floor))
                Dryers(slab.ToList(), size, d.DryersIntoHost);
            if (d.MergeOverlaps)
                foreach (var slab in assembly.Crossings.Where(c => c.HasPosition).GroupBy(c => c.Floor))
                    MergeOverlaps(slab.ToList(), size);
        }

        /// <summary>
        /// Office rule: the first number of the label runs east-west (16X8 -> 16" + clearances across), whichever way the
        /// engineer drew the duct. Replaces the orientation read from the drawing.
        /// </summary>
        private static void EastWest(RiserAssembly assembly)
        {
            foreach (var c in assembly.Crossings.Where(c => c.Size?.Width != null && c.Size.Length != null))
            {
                c.Notes.RemoveAll(n => n.StartsWith("turned like ") || n.StartsWith("duct outline not drawn"));
                bool turned = c.Rotation.HasValue && Math.Abs(Math.Sin(c.Rotation.Value)) > 0.7;
                c.Rotation = 0;
                if (Math.Abs(c.Size.Width.Value - c.Size.Length.Value) >= 0.5)
                    c.Notes.Add($"{c.Size.Width:0.##}\" side east-west as labelled" + (turned ? " (the plan draws it the other way)" : ""));
            }
        }

        /// <summary>
        /// Dryer ducts drawn closer than the sleeve spacing: one Regular Opening around all of them, named DE, centred on the
        /// ducts (rule 74, as the office sets draw it). Roof: rule 78 wants a 6"x6" opening per duct 8" apart, which the
        /// drawn spacing cannot give; those stay for review.
        /// </summary>
        private static void ShaftBoxes(RiserAssembly assembly, Func<Crossing, (double W, double L)?> size, (double W, double L, double Gap, int PerRow)? roofGrid)
        {
            foreach (var c in assembly.Crossings.Where(c => c.Shaft && c.Status == Crossing.Review && c.HasPosition && c.Points.Count > 1))
            {
                int i = c.Notes.FindIndex(n => n.StartsWith("dryer shaft:"));
                if (c.Notes.Any(n => (n.StartsWith("only the ") && !n.Contains("placed straight above")) || n.StartsWith("sizes differ") || n.Contains("not on the PDF")))
                    continue;                                            // in review for another reason too
                if (c.Roof)
                {
                    if (roofGrid == null)
                    {
                        if (i >= 0) c.Notes[i] += " Roof: rule 78 needs a 6\"x6\" opening per duct, 8\" apart; lay them out by hand.";
                        continue;
                    }
                    RoofGrid(c, roofGrid.Value.W, roofGrid.Value.L, roofGrid.Value.Gap, roofGrid.Value.PerRow, i);
                    continue;
                }
                c.X = (c.Points.Min(p => p[0]) + c.Points.Max(p => p[0])) / 2;
                c.Y = (c.Points.Min(p => p[1]) + c.Points.Max(p => p[1])) / 2;
                c.Status = Crossing.Place;
                var s = size(c);
                string note = $"dryer shaft: {c.Points.Count} ducts in one opening" + (s.HasValue ? $" ({s.Value.W:0.#}\" x {s.Value.L:0.#}\")" : "") + " (rule 74)";
                if (i >= 0) c.Notes[i] = note; else c.Notes.Add(note);
            }
        }

        /// <summary>
        /// Roof dryer shaft: one roof opening per duct (rule 78), gap clear between them (rule 79), in straight rows along the
        /// way the ducts are drawn (rules 80-82), centred on the drawn ducts. Placed duct by duct like spaced dryer sleeves.
        /// </summary>
        private static void RoofGrid(Crossing c, double w, double l, double gap, int perRow, int noteIndex)
        {
            double minX = c.Points.Min(p => p[0]), maxX = c.Points.Max(p => p[0]), minY = c.Points.Min(p => p[1]), maxY = c.Points.Max(p => p[1]);
            bool alongX = maxX - minX >= maxY - minY;
            int n = c.Points.Count, cols = Math.Min(n, perRow), rows = (n + cols - 1) / cols;
            double pitchAlong = ((alongX ? w : l) + gap) / 12, pitchAcross = ((alongX ? l : w) + gap) / 12;
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            var grid = new List<double[]>();
            for (int k = 0; k < n; k++)
            {
                int row = k / cols, col = k % cols, inRow = Math.Min(cols, n - row * cols);
                double a = (col - (inRow - 1) / 2.0) * pitchAlong, b = (row - (rows - 1) / 2.0) * pitchAcross;
                grid.Add(alongX ? new[] { cx + a, cy + b } : new[] { cx + b, cy + a });
            }
            c.Points = grid;
            c.X = cx; c.Y = cy;
            c.Shaft = false;
            c.Status = Crossing.Place;
            string note = $"roof: {n} dryer opening(s) {w:0.#}\" x {l:0.#}\", one per duct, {gap:0.#}\" apart in {rows} straight row(s), centred on the drawn ducts (rules 78-82)";
            if (noteIndex >= 0) c.Notes[noteIndex] = note; else c.Notes.Add(note);
        }

        private static void ErvPairs(List<Crossing> ervs, Func<Crossing, (double W, double L)?> size, double floorCc, double roofGap, double reach)
        {
            // closest pairs first; each opening is in one pair at most
            var pairs = (from i in Enumerable.Range(0, ervs.Count) from j in Enumerable.Range(0, ervs.Count) where i < j
                         let d = Dist(ervs[i], ervs[j]) where d <= reach orderby d select (A: ervs[i], B: ervs[j])).ToList();
            var used = new HashSet<Crossing>();
            foreach (var (a, b) in pairs)
            {
                if (used.Contains(a) || used.Contains(b)) continue;
                used.Add(a); used.Add(b);
                var sa = size(a); var sb = size(b);
                if (sa == null || sb == null) continue;

                double cc = Dist(a, b);
                double ux = 0, uy = 1;                                   // same spot: side by side north-south
                if (cc > 0.01) { ux = (b.X - a.X) * 12 / cc; uy = (b.Y - a.Y) * 12 / cc; }
                double Half((double W, double L) s) => Math.Abs(ux) * s.W / 2 + Math.Abs(uy) * s.L / 2;
                double reachEdges = Half(sa.Value) + Half(sb.Value);

                bool roof = a.Roof;
                double need = roof ? reachEdges + roofGap : Math.Max(floorCc, reachEdges);
                if (!roof && cc >= need) continue;                       // floors: only push apart, never pull together
                double move = (need - cc) / 2;                           // inches, each opening
                if (Math.Abs(move) < 0.25) continue;

                a.X -= ux * move / 12; a.Y -= uy * move / 12;
                b.X += ux * move / 12; b.Y += uy * move / 12;
                string why = roof ? $"exactly {roofGap:0.#}\" between ERV openings (rule 55)"
                                  : $"ERV openings grouped {need:0.#}\" c-c, as the office sets draw them (rule 54)";
                a.Notes.Add($"moved {Math.Abs(move):0.#}\" {(move > 0 ? "away from" : "towards")} {b.Name}: {why}");
                b.Notes.Add($"moved {Math.Abs(move):0.#}\" {(move > 0 ? "away from" : "towards")} {a.Name}: {why}");
            }
        }

        /// <summary>
        /// Dryer openings on another opening. A shaft box drawn in the same shaft as an exhaust duct (the 4" ducts beside the
        /// duct, as on 24 Skillman TX1) is moved out along the line between them until the two boxes touch, as the office
        /// sets draw them (duct box and dryer box stacked); up to maxNudge inches. Anything else goes to review.
        /// </summary>
        private static void Dryers(List<Crossing> slab, Func<Crossing, (double W, double L)?> size, bool intoHost, double maxNudge = 12)
        {
            foreach (var c in slab.Where(c => c.System == "DryerExhaust" && c.Status == Crossing.Place && c.MergedInto == null))
            {
                var others = slab.Where(o => o.System != "DryerExhaust" && o.Status == Crossing.Place && o.MergedInto == null)
                                 .Select(o => (O: o, S: size(o))).Where(x => x.S.HasValue).ToList();
                // a shaft box is checked as a whole; separate sleeves (or roof openings) duct by duct
                var size0 = size(c);
                if (size0 == null) continue;
                var box = c.Shaft ? size0 : null;
                double hw = size0.Value.W / 2, hl = size0.Value.L / 2;
                // A single dryer crossing follows its resolved slab position. Its Points collection can still hold the
                // symbol position from the floor below, while c.X/c.Y have been updated to the position shown on this
                // floor's plan. Only a multi-duct dryer group needs every individual symbol point.
                List<(double X, double Y)> Points() => box.HasValue || c.Ducts <= 1 || c.Points.Count == 0
                    ? new List<(double X, double Y)> { (c.X, c.Y) }
                    : c.Points.Select(p => (X: p[0], Y: p[1])).ToList();
                (Crossing O, (double W, double L)? S) Hit() => others.FirstOrDefault(x => Points().Any(p => Math.Abs(p.X - x.O.X) * 12 < x.S.Value.W / 2 + hw &&
                                                                                                        Math.Abs(p.Y - x.O.Y) * 12 < x.S.Value.L / 2 + hl));
                var hit = Hit();
                if (hit.O == null) continue;
                if (box.HasValue)
                {
                    // slide along the main direction between the two centres until the edges meet
                    double dx = (c.X - hit.O.X) * 12, dy = (c.Y - hit.O.Y) * 12;
                    bool alongY = Math.Abs(dy) >= Math.Abs(dx);
                    double need = alongY ? hit.S.Value.L / 2 + hl : hit.S.Value.W / 2 + hw;
                    double move = need - Math.Abs(alongY ? dy : dx) + 0.05, sign = (alongY ? dy : dx) >= 0 ? 1 : -1;
                    if (move <= maxNudge)
                    {
                        double ox = c.X, oy = c.Y;
                        if (alongY) c.Y += sign * move / 12; else c.X += sign * move / 12;
                        if (Hit().O == null)
                        {
                            c.Notes.Add($"moved {move:0.#}\" {(alongY ? (sign > 0 ? "north" : "south") : (sign > 0 ? "east" : "west"))} to sit against the {hit.O.Name} opening " +
                                        "(dryer box next to the duct's box, rule 74)");
                            continue;
                        }
                        c.X = ox; c.Y = oy;                              // would land on something else
                    }
                }
                if (intoHost && !c.Roof)
                {
                    int ducts = Math.Max(1, c.Points.Count);
                    Combine(hit.O, c, size, null, $"takes the {ducts} dryer duct(s) drawn on it (rule 74: dryer circles inside a mechanical opening box)");
                    continue;
                }
                c.Status = Crossing.Review;
                c.Notes.Add($"the dryer opening(s) land on the {hit.O.Name} opening ({hit.S.Value.W:0.#}\" x {hit.S.Value.L:0.#}\"); rule 74 puts dryer " +
                            $"circles inside a mechanical opening box: enlarge the {hit.O.Name} opening to take them, or move them?");
            }
        }

        /// <summary>
        /// Openings of one slab that overlap (clearances included) become one opening around both, named with both tags
        /// (manual 21: shafts and chases; the office sets draw neighbouring ducts in one box). Never a garbage chute (fixed
        /// size), an ERV pair (kept apart by rules 54-55) or a roof dryer opening (6"x6" each, rule 78).
        /// </summary>
        private static void MergeOverlaps(List<Crossing> slab, Func<Crossing, (double W, double L)?> size)
        {
            for (bool merged = true; merged;)
            {
                merged = false;
                var live = slab.Where(c => c.Status == Crossing.Place && c.MergedInto == null).Select(c => (C: c, R: Rects(c, size))).Where(x => x.R.Count > 0).ToList();
                for (int i = 0; i < live.Count && !merged; i++)
                    for (int j = i + 1; j < live.Count && !merged; j++)
                    {
                        var (a, ra) = live[i]; var (b, rb) = live[j];
                        if (!CanMerge(a, b) || !ra.Any(x => rb.Any(y => Overlap(x, y)))) continue;
                        // the bigger opening (not a dryer) takes the other
                        bool aHosts = (a.System == "DryerExhaust") == (b.System == "DryerExhaust") ? Area(ra) >= Area(rb) : b.System == "DryerExhaust";
                        var host = aHosts ? a : b; var guest = aHosts ? b : a;
                        Combine(host, guest, size, $"{NameOf(host)}/{NameOf(guest)}", $"combined with {NameOf(guest)}: their openings overlapped (manual 21: one opening for ducts side by side)");
                        merged = true;
                    }
            }
        }

        private static bool CanMerge(Crossing a, Crossing b) =>
            a.System != "GarbageChute" && b.System != "GarbageChute" && !(a.System == "ERV" && b.System == "ERV") &&
            !(a.Roof && (a.System == "DryerExhaust" || b.System == "DryerExhaust"));

        /// <summary>
        /// <paramref name="guest"/>'s opening goes into <paramref name="host"/>'s: the host becomes a box around both (whole even
        /// inches), the guest places nothing and points to the host.
        /// </summary>
        private static void Combine(Crossing host, Crossing guest, Func<Crossing, (double W, double L)?> size, string label, string note)
        {
            var all = Rects(host, size).Concat(Rects(guest, size)).ToList();
            double x0 = all.Min(r => r.X - r.W / 2), x1 = all.Max(r => r.X + r.W / 2), y0 = all.Min(r => r.Y - r.L / 2), y1 = all.Max(r => r.Y + r.L / 2);
            double Even(double v) => Math.Ceiling(Math.Round(v, 2) / 2) * 2;
            host.BoxW = Even(x1 - x0); host.BoxL = Even(y1 - y0);
            host.X = (x0 + x1) / 2 / 12; host.Y = (y0 + y1) / 2 / 12;
            host.Rotation = 0;
            if (label != null) host.Label = label;
            host.Notes.Add($"{note}; one opening {host.BoxW:0}\" x {host.BoxL:0}\"");
            guest.MergedInto = host;
            guest.Status = Crossing.Skip;
            guest.Notes.Add($"in the {host.Label ?? NameOf(host)} opening (combined, {host.BoxW:0}\" x {host.BoxL:0}\")");
        }

        /// <summary>The name an opening gets: its label, else tag + dampers (KX2-MD).</summary>
        private static string NameOf(Crossing c) => c.Label ?? c.Name + (c.Dampers.Count > 0 ? "-" + string.Join("-", c.Dampers) : "");

        /// <summary>The rectangles an opening covers (inches, centre + size): one, or one per duct for separate dryer openings.</summary>
        private static List<(double X, double Y, double W, double L)> Rects(Crossing c, Func<Crossing, (double W, double L)?> size)
        {
            var s = size(c);
            var list = new List<(double X, double Y, double W, double L)>();
            if (!s.HasValue) return list;
            if (c.System == "DryerExhaust" && !c.Shaft && !c.BoxW.HasValue && c.Points.Count > 1)
                list.AddRange(c.Points.Select(p => (p[0] * 12, p[1] * 12, s.Value.W, s.Value.L)));
            else list.Add((c.X * 12, c.Y * 12, s.Value.W, s.Value.L));
            return list;
        }

        private static bool Overlap((double X, double Y, double W, double L) a, (double X, double Y, double W, double L) b) =>
            Math.Abs(a.X - b.X) < (a.W + b.W) / 2 - 0.05 && Math.Abs(a.Y - b.Y) < (a.L + b.L) / 2 - 0.05;

        private static double Area(List<(double X, double Y, double W, double L)> rects) => rects.Sum(r => r.W * r.L);

        private static double Dist(Crossing a, Crossing b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) * 12;
    }
}
