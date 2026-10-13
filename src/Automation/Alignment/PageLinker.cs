using System;
using System.Collections.Generic;
using System.Linq;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>
    /// Two pages of one engineer's set share the architectural background (the dashed property line, area lines...):
    /// the same lines, drawn identically, only placed elsewhere on each sheet. The shift that lays one page's background
    /// lines on the other's is found by every pair of line ends on the same layer voting (to the quarter inch); a layer
    /// counts when nearly all its ends meet at one shift and no other shift comes close (a dashed line also meets itself
    /// shifted by one dash, on its lines in that direction only). Real inches in each sheet's frame; free of the Revit API.
    /// </summary>
    public static class PageLinker
    {
        public class Link
        {
            /// <summary>A point of the first page plus (Dx, Dy) is the same point of the second page.</summary>
            public double Dx, Dy;
            public int Meet, Of, Runner;
            public string Layer;
        }

        private const double Quarter = 0.25;
        private const int MinEnds = 30, MinMeet = 50, Cap = 400;
        /// <summary>A layer with more ends than this (a whole floor's walls, hatching) is not tried: slow, and not what links sheets.</summary>
        private const int MaxEnds = 3000;
        /// <summary>The same background lines have about as many ends on both pages; a layer far off is not the same lines.</summary>
        private const double SameSize = 0.5;
        /// <summary>Share of the smaller layer's ends that must meet, and how far the best shift must beat any other.</summary>
        private const double MinShare = 0.85, MinLead = 1.25;

        /// <summary>The shift from <paramref name="from"/> to <paramref name="to"/>; null when no shared layer gives a clear one.</summary>
        public static Link Find(PdfPlan from, PdfPlan to)
        {
            Link best = null;
            var layers = from.Background.Keys.Where(to.Background.ContainsKey)
                .Select(l => (Layer: l, A: Distinct(from.Background[l]), B: Distinct(to.Background[l])))
                .Where(x => x.A.Count >= MinEnds && x.B.Count >= MinEnds && x.A.Count <= MaxEnds && x.B.Count <= MaxEnds &&
                            Math.Min(x.A.Count, x.B.Count) >= SameSize * Math.Max(x.A.Count, x.B.Count))
                .OrderBy(x => x.A.Count + x.B.Count);                     // small layers first: cheap, and the property line is one
            foreach (var (layer, fromEnds, toEnds) in layers)
            {
                if (best != null) break;                              // the first clear layer is enough (smallest first)
                // one side sampled, the other whole: two samples of a dashed line would hold different dashes
                var a = Spread(fromEnds, Cap);
                var b = toEnds;
                var votes = new Dictionary<(long, long), int>();
                foreach (var p in a)
                    foreach (var q in b)
                    {
                        var key = ((long)Math.Round((q.X - p.X) / Quarter), (long)Math.Round((q.Y - p.Y) / Quarter));
                        votes[key] = votes.TryGetValue(key, out int v) ? v + 1 : 1;
                    }
                // the winner, then the best shift more than an inch from it (the vote is per quarter inch: the cells next to
                // the winner are the same shift); two passes, no sort
                var win = default(KeyValuePair<(long, long), int>);
                foreach (var kv in votes) if (kv.Value > win.Value) win = kv;
                int runner = 0;
                foreach (var kv in votes)
                    if (kv.Value > runner && (Math.Abs(kv.Key.Item1 - win.Key.Item1) > 4 || Math.Abs(kv.Key.Item2 - win.Key.Item2) > 4)) runner = kv.Value;
                int of = a.Count;
                if (win.Value < MinMeet || win.Value < MinShare * of || win.Value < MinLead * runner) continue;
                if (best == null || win.Value > best.Meet)
                    best = new Link { Dx = win.Key.Item1 * Quarter, Dy = win.Key.Item2 * Quarter, Meet = win.Value, Of = of, Runner = runner, Layer = layer };
            }
            return best;
        }

        private static List<(double X, double Y)> Distinct(List<(double X, double Y)> pts)
        {
            var seen = new HashSet<(long, long)>();
            return pts.Where(p => seen.Add(((long)Math.Round(p.X / Quarter), (long)Math.Round(p.Y / Quarter)))).ToList();
        }

        private static List<(double X, double Y)> Spread(List<(double X, double Y)> pts, int n)
        {
            if (pts.Count <= n) return pts;
            double step = (double)pts.Count / n;
            return Enumerable.Range(0, n).Select(i => pts[(int)(i * step)]).ToList();
        }
    }
}
