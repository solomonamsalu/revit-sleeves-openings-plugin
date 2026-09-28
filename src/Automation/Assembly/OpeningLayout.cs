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
    /// sleeve spacing) gets one Regular Opening around all its ducts on floors (rule 74). A dryer opening that lands on another
    /// opening goes to review: rule 74 puts dryer circles inside a mechanical opening box, which needs a size decision.
    /// Free of the Revit API.
    /// </summary>
    public static class OpeningLayout
    {
        /// <param name="size">Opening size in inches across (Revit X) and along (Revit Y), clearances included; null = unknown.</param>
        /// <param name="pairReach">Inches: ERV openings farther apart than this are not one pair.</param>
        public static void Apply(RiserAssembly assembly, Func<Crossing, (double W, double L)?> size,
                                 double floorCenterToCenter = 12, double roofGap = 24, double dryerDiameter = 4, bool dryerShaftBox = true,
                                 double pairReach = 72, bool firstNumberEastWest = false)
        {
            if (firstNumberEastWest) EastWest(assembly);
            if (dryerShaftBox) ShaftBoxes(assembly, size);
            foreach (var slab in assembly.Crossings.Where(c => c.Status == Crossing.Place && c.HasPosition).GroupBy(c => c.Floor))
                ErvPairs(slab.Where(c => c.System == "ERV").ToList(), size, floorCenterToCenter, roofGap, pairReach);
            foreach (var slab in assembly.Crossings.Where(c => c.Status == Crossing.Place && c.HasPosition).GroupBy(c => c.Floor))
                Dryers(slab.ToList(), size, dryerDiameter);
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
        private static void ShaftBoxes(RiserAssembly assembly, Func<Crossing, (double W, double L)?> size)
        {
            foreach (var c in assembly.Crossings.Where(c => c.Shaft && c.Status == Crossing.Review && c.HasPosition && c.Points.Count > 1))
            {
                int i = c.Notes.FindIndex(n => n.StartsWith("dryer shaft:"));
                if (c.Roof)
                {
                    if (i >= 0) c.Notes[i] += " Roof: rule 78 needs a 6\"x6\" opening per duct, 8\" apart; lay them out by hand.";
                    continue;
                }
                if (c.Notes.Any(n => n.StartsWith("only the ") || n.StartsWith("sizes differ") || n.Contains("not on the PDF"))) continue;   // in review for another reason too
                c.X = (c.Points.Min(p => p[0]) + c.Points.Max(p => p[0])) / 2;
                c.Y = (c.Points.Min(p => p[1]) + c.Points.Max(p => p[1])) / 2;
                c.Status = Crossing.Place;
                var s = size(c);
                string note = $"dryer shaft: {c.Points.Count} ducts in one opening" + (s.HasValue ? $" ({s.Value.W:0.#}\" x {s.Value.L:0.#}\")" : "") + " (rule 74)";
                if (i >= 0) c.Notes[i] = note; else c.Notes.Add(note);
            }
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
        private static void Dryers(List<Crossing> slab, Func<Crossing, (double W, double L)?> size, double diameter, double maxNudge = 12)
        {
            double r = diameter / 2;
            var others = slab.Where(o => o.System != "DryerExhaust").Select(o => (O: o, S: size(o))).Where(x => x.S.HasValue).ToList();
            foreach (var c in slab.Where(c => c.System == "DryerExhaust"))
            {
                // a shaft box is checked as a whole; separate sleeves duct by duct
                var box = c.Shaft ? size(c) : null;
                double hw = box.HasValue ? box.Value.W / 2 : r, hl = box.HasValue ? box.Value.L / 2 : r;
                List<(double X, double Y)> Points() => box.HasValue || c.Points.Count == 0 ? new List<(double X, double Y)> { (c.X, c.Y) } : c.Points.Select(p => (X: p[0], Y: p[1])).ToList();
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
                        c.X = ox; c.Y = oy;                              // would land on something else: leave it for review
                    }
                }
                c.Status = Crossing.Review;
                c.Notes.Add($"the dryer opening(s) land on the {hit.O.Name} opening ({hit.S.Value.W:0.#}\" x {hit.S.Value.L:0.#}\"); rule 74 puts dryer " +
                            $"circles inside a mechanical opening box: enlarge the {hit.O.Name} opening to take them, or move them?");
            }
        }

        private static double Dist(Crossing a, Crossing b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) * 12;
    }
}
