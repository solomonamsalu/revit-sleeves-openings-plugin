using System;
using System.Collections.Generic;
using System.Linq;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>
    /// PDF-only mode, when a plan has no columns to line up with (the engineer's copy of the architectural background left
    /// them out): the plan's walls (PDF wall layer) are placed on the walls of the architect's DWG as it sits in the model on
    /// that level. Both are the same architectural drawing, so their wall lines coincide once the page is turned (0, 90,
    /// 180 or 270°) and shifted. Every pair of a page wall end and a model wall end votes for a shift; the strongest shifts
    /// are scored by how many page wall ends land on a model wall end (within 1"): corners on corners. Wall lines alone
    /// are not enough - a plan of repeated apartments also lies on its walls shifted by one apartment, but its corners do
    /// not. The placement is used only when a fair share of the corners meet and it clearly beats every other placement:
    /// another floor's or an older background matches weakly or evenly, and is refused rather than guessed. Inches
    /// throughout; free of the Revit API.
    /// </summary>
    public static class WallAligner
    {
        public class Result
        {
            /// <summary>Page inches -> model inches: model = R(page) + T.</summary>
            public double Cos = 1, Sin, Tx, Ty;
            public int Degrees;
            /// <summary>Page wall ends on a model wall end, of the ends tried; the best other placement's count.</summary>
            public int Hits, Probes, Runner;
            /// <summary>Page wall ends that land on a model wall end (page point, model point, distance), closest first: check marks.</summary>
            public List<(double PX, double PY, double MX, double MY, double D)> Corners = new List<(double, double, double, double, double)>();
            public (double X, double Y) Apply(double x, double y) => (Tx + x * Cos - y * Sin, Ty + x * Sin + y * Cos);
        }

        private const double OnLine = 1.0;          // a page wall point within this of a model wall line is on it
        private const double Bin = 1.0;             // shift votes are counted per inch
        private const int Sample = 200, ProbeCount = 800, Candidates = 60, MaxVoters = 3000;
        /// <summary>Share of page wall points that must land on a model wall, and how far the best must beat any other placement.</summary>
        public const double MinShare = 0.10, MinLead = 2.0, NearLead = 1.15;
        private const int MinHits = 40;
        /// <summary>Placements within this (inches) of the best are the same placement (the vote is counted per inch).</summary>
        private const double NearSame = 3;
        /// <summary>Placements further than this (inches) from the best are "far" (a bay or an apartment off): they must lose by MinLead.</summary>
        private const double NearFar = 24;

        /// <summary>The placement of <paramref name="page"/> walls on <paramref name="model"/> walls; null with <paramref name="why"/> when none is clear.</summary>
        public static Result Match(IList<((double X, double Y) A, (double X, double Y) B)> page,
                                   IList<((double X, double Y) A, (double X, double Y) B)> model, out string why)
        {
            why = null;
            var pageEnds = Ends(page);
            var modelEnds = Ends(model);
            if (pageEnds.Count < MinHits) { why = $"only {pageEnds.Count} wall ends on the page"; return null; }
            if (modelEnds.Count < MinHits) { why = $"only {modelEnds.Count} wall ends in the drawing"; return null; }

            var ends = EndCells(modelEnds);
            var sample = Spread(modelEnds, Sample);
            var probes = Spread(pageEnds, ProbeCount);
            var voters = Spread(pageEnds, MaxVoters);                 // a very full page votes with a spread of its corners

            var scored = new List<(int Deg, double Tx, double Ty, int Hits)>();
            foreach (int deg in new[] { 0, 90, 180, 270 })
            {
                double c = Math.Round(Math.Cos(deg * Math.PI / 180)), s = Math.Round(Math.Sin(deg * Math.PI / 180));
                var turned = voters.Select(p => (X: p.X * c - p.Y * s, Y: p.X * s + p.Y * c)).ToList();
                var votes = new Dictionary<(long, long), int>();
                foreach (var m in sample)
                    foreach (var p in turned)
                    {
                        var key = ((long)Math.Round((m.X - p.X) / Bin), (long)Math.Round((m.Y - p.Y) / Bin));
                        votes[key] = votes.TryGetValue(key, out int v) ? v + 1 : 1;
                    }
                foreach (var kv in votes.OrderByDescending(kv => kv.Value).Take(Candidates))
                {
                    double tx = kv.Key.Item1 * Bin, ty = kv.Key.Item2 * Bin;
                    int hits = probes.Count(p => NearEnd(ends, tx + p.X * c - p.Y * s, ty + p.X * s + p.Y * c));
                    scored.Add((deg, tx, ty, hits));
                }
            }
            if (scored.Count == 0) { why = "no placement"; return null; }
            var best = scored.OrderByDescending(x => x.Hits).First();
            // The best other placement far away (turned, or shifted by a bay / an apartment) must lose clearly; one close by
            // (a few inches to 2 ft: a double-line wall shifted by its thickness puts one face's corners on the other's) must
            // lose too - a page of repeated corners (a roof's parapet joints) fits a few inches off almost as well, and a
            // placement a few inches off is as wrong as one a bay off.
            bool Other(int deg, double tx, double ty) => deg != best.Deg || Math.Abs(tx - best.Tx) > NearSame || Math.Abs(ty - best.Ty) > NearSame;
            bool Far(int deg, double tx, double ty) => deg != best.Deg || Math.Abs(tx - best.Tx) > NearFar || Math.Abs(ty - best.Ty) > NearFar;
            int runner = scored.Where(x => Far(x.Deg, x.Tx, x.Ty)).Select(x => x.Hits).DefaultIfEmpty(0).Max();
            int runnerNear = scored.Where(x => Other(x.Deg, x.Tx, x.Ty) && !Far(x.Deg, x.Tx, x.Ty)).Select(x => x.Hits).DefaultIfEmpty(0).Max();
            if (best.Hits < MinHits || best.Hits < MinShare * probes.Count)
            {
                why = $"at best {best.Hits} of {probes.Count} wall corners meet its corners ({MinShare:P0} and {MinHits} needed): not the same background";
                return null;
            }
            if (best.Hits < NearLead * runnerNear)
            {
                why = $"a placement a few inches away fits about as well ({best.Hits} and {runnerNear} of {probes.Count} wall corners): its position is not sure to the inch";
                return null;
            }
            if (best.Hits < MinLead * runner)
            {
                why = $"two placements fit about as well ({best.Hits} and {runner} of {probes.Count} wall corners): ambiguous";
                return null;
            }

            var r = new Result
            {
                Degrees = best.Deg, Cos = Math.Round(Math.Cos(best.Deg * Math.PI / 180)), Sin = Math.Round(Math.Sin(best.Deg * Math.PI / 180)),
                Tx = best.Tx, Ty = best.Ty, Hits = best.Hits, Probes = probes.Count, Runner = Math.Max(runner, runnerNear)
            };
            // refine on the wall ends that meet (the vote is rounded to the inch)
            var pairs = Pairs(pageEnds, modelEnds, r);
            if (pairs.Count >= 3)
            {
                r.Tx += pairs.Average(p => p.MX - (r.Tx + p.PX * r.Cos - p.PY * r.Sin));
                r.Ty += pairs.Average(p => p.MY - (r.Ty + p.PX * r.Sin + p.PY * r.Cos));
                pairs = Pairs(pageEnds, modelEnds, r);
            }
            r.Corners = pairs.OrderBy(p => p.D).ToList();
            return r;
        }

        /// <summary>Page wall ends within 1" of a model wall end under the placement.</summary>
        private static List<(double PX, double PY, double MX, double MY, double D)> Pairs(List<(double X, double Y)> page, List<(double X, double Y)> model, Result r)
        {
            var cells = new Dictionary<(int, int), List<(double X, double Y)>>();
            foreach (var m in model)
            {
                var key = ((int)Math.Floor(m.X / 6), (int)Math.Floor(m.Y / 6));
                if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<(double, double)>();
                l.Add(m);
            }
            var list = new List<(double, double, double, double, double)>();
            foreach (var p in page)
            {
                var q = r.Apply(p.X, p.Y);
                int cx = (int)Math.Floor(q.X / 6), cy = (int)Math.Floor(q.Y / 6);
                (double X, double Y)? hit = null; double best = OnLine;
                for (int gx = cx - 1; gx <= cx + 1; gx++)
                    for (int gy = cy - 1; gy <= cy + 1; gy++)
                        if (cells.TryGetValue((gx, gy), out var l))
                            foreach (var m in l)
                            {
                                double d = Math.Sqrt((m.X - q.X) * (m.X - q.X) + (m.Y - q.Y) * (m.Y - q.Y));
                                if (d <= best) { best = d; hit = m; }
                            }
                if (hit.HasValue) list.Add((p.X, p.Y, hit.Value.X, hit.Value.Y, best));
            }
            return list;
        }

        /// <summary>Distinct wall ends (to the half inch).</summary>
        private static List<(double X, double Y)> Ends(IList<((double X, double Y) A, (double X, double Y) B)> walls)
        {
            var seen = new HashSet<(long, long)>();
            var list = new List<(double X, double Y)>();
            foreach (var w in walls)
                foreach (var p in new[] { w.A, w.B })
                    if (seen.Add(((long)Math.Round(p.X * 2), (long)Math.Round(p.Y * 2)))) list.Add(p);
            return list;
        }

        /// <summary>At most <paramref name="n"/> points spread over the list (every k-th, so the result does not depend on chance).</summary>
        private static List<(double X, double Y)> Spread(List<(double X, double Y)> pts, int n)
        {
            if (pts.Count <= n) return pts;
            double step = (double)pts.Count / n;
            return Enumerable.Range(0, n).Select(i => pts[(int)(i * step)]).ToList();
        }

        private const double Cell = 6;

        /// <summary>Model wall ends by 6" cell.</summary>
        private static Dictionary<(int, int), List<(double X, double Y)>> EndCells(List<(double X, double Y)> ends)
        {
            var cells = new Dictionary<(int, int), List<(double X, double Y)>>();
            foreach (var e in ends)
            {
                var key = ((int)Math.Floor(e.X / Cell), (int)Math.Floor(e.Y / Cell));
                if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<(double X, double Y)>();
                l.Add(e);
            }
            return cells;
        }

        private static bool NearEnd(Dictionary<(int, int), List<(double X, double Y)>> cells, double x, double y)
        {
            int cx = (int)Math.Floor(x / Cell), cy = (int)Math.Floor(y / Cell);
            for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gy = cy - 1; gy <= cy + 1; gy++)
                    if (cells.TryGetValue((gx, gy), out var l))
                        foreach (var e in l)
                            if ((e.X - x) * (e.X - x) + (e.Y - y) * (e.Y - y) <= OnLine * OnLine) return true;
            return false;
        }
    }
}
