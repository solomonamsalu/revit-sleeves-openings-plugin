using System;
using System.Collections.Generic;
using System.Linq;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>A column of the Revit model (host or linked), in feet: box centre, box sides, height range.</summary>
    public class ColumnPoint
    {
        public double X, Y, W, L, MinZ, MaxZ;
        public string Source;
    }

    /// <summary>The model's columns and level elevations (collected in Revit; the aligner itself is free of the Revit API).</summary>
    public class RevitColumns
    {
        public List<ColumnPoint> Columns = new List<ColumnPoint>();
        /// <summary>Level name -> model elevation (feet).</summary>
        public Dictionary<string, double> Levels = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Where the columns came from ("this model", link names).</summary>
        public string Source;

        /// <summary>Columns at a level (the level's plane cuts or touches them); all of them when the level is unknown. One per spot.</summary>
        public List<ColumnPoint> At(string level)
        {
            var list = level != null && Levels.TryGetValue(level, out double z)
                ? Columns.Where(c => c.MinZ - 1 <= z && c.MaxZ + 1 >= z).ToList()
                : Columns.ToList();
            var one = new List<ColumnPoint>();
            foreach (var c in list)
                if (!one.Any(o => Math.Abs(o.X - c.X) < 1.0 / 12 && Math.Abs(o.Y - c.Y) < 1.0 / 12)) one.Add(c);
            return one;
        }
    }

    /// <summary>
    /// PDF-only mode, Phase 3: lines each PDF floor plan up with Revit by its columns. The plans show the architectural
    /// background's columns; the model has the same columns (structural link or its own). Every pair of PDF columns is tried
    /// against every pair of Revit columns the same distance apart; the rotation + shift that puts the most PDF columns on
    /// Revit columns of the same shape (and keeps the page's columns inside the building) wins, then it is fitted on all of them. The scale is the sheet's printed scale; the columns' own fit
    /// must agree with it. A floor is used only when enough columns, spread over the building, land within the tolerance,
    /// and no other placement comes close. A floor with too few columns (a roof) is lined up by its pipes that stack on a
    /// lined-up floor next to it. Free of the Revit API.
    /// </summary>
    public static class ColumnAligner
    {
        public const string Method = "PDF columns on the model's columns";

        private class Fit
        {
            public double Cos, Sin, Tx, Ty;        // PDF inches -> Revit inches
            public List<(PdfColumn P, ColumnPoint R, double D)> Pairs = new List<(PdfColumn, ColumnPoint, double)>();
            public double Scale = 1;               // the columns' own fit against the printed scale
            public (double X, double Y) Apply(double x, double y) => (Tx + x * Cos - y * Sin, Ty + x * Sin + y * Cos);
            public double Angle => Math.Atan2(Sin, Cos);
        }

        public static AlignmentResult Run(PdfPlanResult plans, RevitColumns revit, IDictionary<string, string> floorLevels, PdfOnlyRules rules)
        {
            var result = new AlignmentResult();
            revit = revit ?? new RevitColumns();
            var fits = new Dictionary<FloorAlignment, Fit>();

            foreach (var plan in plans.Plans.OrderBy(p => FloorKey.Order(p.Floor)))
            {
                var fa = new FloorAlignment { Floor = plan.Floor };
                floorLevels?.TryGetValue(plan.Floor, out fa.Level);
                result.Floors.Add(fa);
                if (plan.Problem != null) { fa.Notes.Add(plan.Problem); continue; }
                fa.Reference = new ReferenceDrawing { Name = $"PDF page {plan.Page}", Method = Method, Floor = plan.Floor, Level = fa.Level };

                var model = revit.At(fa.Level);
                fa.RisersChecked = plan.Columns.Count;
                if (model.Count == 0) { fa.Notes.Add(revit.Columns.Count == 0 ? "the model has no columns (load the structural link)" : $"no model columns at level {fa.Level}"); continue; }
                if (plan.Columns.Count < rules.MinColumns) { fa.Notes.Add($"only {plan.Columns.Count} column(s) drawn on the page ({rules.MinColumns} needed)"); continue; }

                var (best, runner) = Search(plan.Columns, model, rules);
                if (best == null) { fa.Notes.Add($"no placement puts {rules.MinColumns} of its {plan.Columns.Count} columns on model columns"); continue; }
                Refine(best, plan.Columns, model, rules);

                var close = best.Pairs.Where(p => p.D <= rules.ColumnTolerance).ToList();
                double spreadX = close.Count == 0 ? 0 : (close.Max(p => p.R.X) - close.Min(p => p.R.X)), spreadY = close.Count == 0 ? 0 : (close.Max(p => p.R.Y) - close.Min(p => p.R.Y));
                fa.RisersOnReference = close.Count;
                fa.RisersNearReference = best.Pairs.Count;
                fa.Map = new PlanMap { Scale = 1.0 / 12, Cos = best.Cos, Sin = best.Sin, Ox = best.Tx / 12, Oy = best.Ty / 12 };
                fa.Shift = new FloorShift { Support = close.Count, Names = close.Count, RunnerUp = runner, Comparable = plan.Columns.Count,
                                            Residual = close.Count == 0 ? 0 : Math.Sqrt(close.Average(p => p.D * p.D)) };
                fits[fa] = best;

                string how = $"{close.Count} of its {plan.Columns.Count} columns on model columns within {rules.ColumnTolerance:0.#}\" " +
                             $"(worst {(close.Count == 0 ? 0 : close.Max(p => p.D)):0.##}\"), rotation {best.Angle * 180 / Math.PI:0.##}°, " +
                             $"the columns fit the printed scale {Ratio(plan)} within {Math.Abs(best.Scale - 1) * 100:0.##}%";
                var problems = new List<string>();
                if (close.Count < rules.MinColumns) problems.Add($"only {close.Count} column(s) within {rules.ColumnTolerance:0.#}\" ({rules.MinColumns} needed)");
                if (spreadX < rules.MinSpread || spreadY < rules.MinSpread) problems.Add($"the matching columns span only {spreadX:0.#}' x {spreadY:0.#}' ({rules.MinSpread:0}' each way needed)");
                if (Math.Abs(best.Scale - 1) > rules.ScaleTolerance) problems.Add($"the columns do not fit the printed scale ({(best.Scale - 1) * 100:+0.##;-0.##}%)");
                // a nearly symmetric column layout also fits turned 180° or shifted by a bay: the true placement must fit clearly more
                if (runner * 4 > best.Pairs.Count * 3) problems.Add($"another placement fits {runner} columns against {best.Pairs.Count}: ambiguous");
                if (problems.Count == 0)
                {
                    fa.Status = FloorAlignment.Confirmed;
                    fa.RevitProven = true;
                    fa.Notes.Add(how);
                }
                else
                {
                    fa.Status = FloorAlignment.NotConfirmed;
                    fa.Notes.Add(how);
                    fa.Notes.AddRange(problems);
                }
            }

            // ---- the floors check each other. One building: every floor is turned the same way (a floor whose columns fit
            //      turned otherwise matched a symmetric layout the wrong way round). And two confirmed floors whose pipes
            //      stack on the sheets must put those pipes on the same spot in Revit.
            void Reject(FloorAlignment f, string why)
            {
                f.Status = FloorAlignment.NotConfirmed; f.RevitProven = false; f.Notes.Add(why);
            }
            string PageOf(FloorAlignment f) => f.Reference?.Name ?? f.Floor;
            int Votes(IEnumerable<FloorAlignment> g) => g.Select(PageOf).Distinct().Count();      // a typical plan's floors are one page, one vote
            var turns = new List<List<FloorAlignment>>();
            foreach (var f in result.Floors.Where(fits.ContainsKey))          // every floor's own best fit votes, confirmed or not
            {
                var g = turns.FirstOrDefault(x => Math.Abs(Angle(fits[x[0]].Angle - fits[f].Angle)) <= Math.PI / 180);
                if (g == null) turns.Add(g = new List<FloorAlignment>());
                g.Add(f);
            }
            turns = turns.OrderByDescending(Votes).ToList();
            if (turns.Count > 1)
            {
                bool clear = Votes(turns[0]) >= 2 * Votes(turns[1]);
                double main = fits[turns[0][0]].Angle * 180 / Math.PI;
                foreach (var g in turns.Skip(clear ? 1 : 0))
                    foreach (var f in g)
                        Reject(f, clear ? $"its columns fit turned {fits[f].Angle * 180 / Math.PI:0.#}°, the other floors {main:0.#}°"
                                        : $"the floors' columns fit different turns ({string.Join(", ", turns.Select(t => $"{fits[t[0]].Angle * 180 / Math.PI:0.#}°: {string.Join(" ", t.Select(x => FloorKey.Describe(x.Floor)))}"))})");
            }
            foreach (var f in result.Floors.Where(f => f.Status == FloorAlignment.Confirmed).OrderBy(f => FloorKey.Order(f.Floor)).ToList())
                foreach (var n in Neighbours(result, f).Where(n => n.Status == FloorAlignment.Confirmed && FloorKey.Order(n.Floor) > FloorKey.Order(f.Floor)).ToList())
                {
                    var shift = Stack(plans.Risers, f.Floor, n.Floor, rules);
                    if (shift == null) continue;
                    var pipes = plans.Risers.Risers.Where(r => r.Floor == f.Floor).SelectMany(r => r.Symbols.Where(s => s.Block == DwgRiserReader.PipeCircle)).ToList();
                    double off = pipes.Average(s =>
                    {
                        var a = fits[f].Apply(s.X, s.Y); var b = fits[n].Apply(s.X + shift.Dx, s.Y + shift.Dy);
                        return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    });
                    if (off <= 2 * rules.ColumnTolerance) continue;
                    Reject(f, $"its pipes that stack on the {FloorKey.Describe(n.Floor)} land {off:0.#}\" from them in Revit: the two floors' columns disagree");
                    Reject(n, $"its pipes that stack on the {FloorKey.Describe(f.Floor)} land {off:0.#}\" from them in Revit: the two floors' columns disagree");
                }

            // ---- a floor that could not use its columns: lined up by its pipes that stack on a lined-up floor next to it
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var fa in result.Floors.Where(f => !f.Usable && f.Reference != null).OrderBy(f => FloorKey.Order(f.Floor)))
                {
                    foreach (var n in Neighbours(result, fa).Where(n => n.Usable && fits.ContainsKey(n)))
                    {
                        var shift = Stack(plans.Risers, fa.Floor, n.Floor, rules);
                        if (shift == null) continue;
                        var nf = fits[n];
                        // PDF(this) + shift = PDF(neighbour) -> Revit
                        var f = new Fit { Cos = nf.Cos, Sin = nf.Sin };
                        var o = nf.Apply(shift.Dx, shift.Dy);
                        f.Tx = o.X; f.Ty = o.Y;
                        fits[fa] = f;
                        fa.Map = new PlanMap { Scale = 1.0 / 12, Cos = f.Cos, Sin = f.Sin, Ox = f.Tx / 12, Oy = f.Ty / 12 };
                        fa.Shift = new FloorShift { Support = shift.Support, Names = shift.Support, RunnerUp = shift.RunnerUp, Tags = shift.Tags };
                        fa.Status = FloorAlignment.AgreesWithOthers;
                        fa.RevitProven = n.RevitProven;
                        fa.Notes.Add($"lined up by {shift.Support} pipes drawn on the same spot (within {rules.StackTolerance:0.#}\") as on the {FloorKey.Describe(n.Floor)}" +
                                     (shift.Tags.Count > 0 ? $" (stacks {string.Join(", ", shift.Tags)})" : ""));
                        grew = true;
                        break;
                    }
                }
            }

            // ---- a floor still not lined up (a roof page with no columns and too few pipes to stack: sprinkler): the
            //      engineer plots every floor in the same sheet frame. When most confirmed floors land the same way (same
            //      turn, page origin within the stack tolerance), that frame is this page's too.
            {
                var confirmed = result.Floors.Where(f => f.Status == FloorAlignment.Confirmed && fits.ContainsKey(f)).ToList();
                bool Same(Fit a, Fit b) => Math.Abs(Angle(a.Angle - b.Angle)) <= Math.PI / 1800 &&
                                           Math.Sqrt(Math.Pow(a.Tx - b.Tx, 2) + Math.Pow(a.Ty - b.Ty, 2)) <= rules.StackTolerance;
                // counted in pages: a typical plan's floors are one page
                var frame = confirmed.Select(f => (F: f, N: Votes(confirmed.Where(o => Same(fits[o], fits[f]))))).OrderByDescending(t => t.N).FirstOrDefault();
                if (frame.F != null && frame.N >= AlignmentResult.AnchorCount && frame.N * 3 >= Votes(confirmed) * 2)
                    foreach (var fa in result.Floors.Where(f => !f.Usable && f.Reference != null && f.Status != FloorAlignment.NotConfirmed))
                    {
                        var nf = fits[frame.F];
                        var f = new Fit { Cos = nf.Cos, Sin = nf.Sin, Tx = nf.Tx, Ty = nf.Ty };
                        fits[fa] = f;
                        fa.Map = new PlanMap { Scale = 1.0 / 12, Cos = f.Cos, Sin = f.Sin, Ox = f.Tx / 12, Oy = f.Ty / 12 };
                        fa.Shift = new FloorShift { Support = frame.N, Names = frame.N };
                        fa.Status = FloorAlignment.AgreesWithOthers;
                        fa.RevitProven = frame.F.RevitProven;
                        fa.Notes.Add($"lined up in the sheet frame {frame.N} of the {Votes(confirmed)} column-confirmed pages share " +
                                     $"(their pages land within {rules.StackTolerance:0.#}\" of each other): the engineer plotted every floor the same way");
                    }
            }
            foreach (var fa in result.Floors.Where(f => f.Map != null))
            {
                var below = Neighbours(result, fa).FirstOrDefault(n => FloorKey.Order(n.Floor) < FloorKey.Order(fa.Floor) && n.Map != null);
                if (below == null) continue;
                fa.StackedOn = below.Floor;
                fa.StackedTags = Stacked(plans.Risers, fa, below, rules);
            }

            // ---- check marks: one matched column per floor, the closest, on three different floors (pages: a typical
            //      plan's floors are one drawing)
            var marked = new HashSet<string>();
            foreach (var (fa, f) in fits.Where(kv => kv.Key.Status == FloorAlignment.Confirmed).Select(kv => (kv.Key, kv.Value))
                                        .OrderBy(kv => kv.Value.Pairs.Where(p => p.D <= rules.ColumnTolerance).Select(p => p.D).DefaultIfEmpty(99).Min()))
            {
                if (result.Anchors.Count >= AlignmentResult.AnchorCount) break;
                if (!marked.Add(PageOf(fa))) continue;
                var p = f.Pairs.OrderBy(x => x.D).First();
                var at = f.Apply(p.P.X, p.P.Y);
                result.Anchors.Add(new AnchorRiser
                {
                    Floor = fa.Floor, Level = fa.Level, Tag = "column", DwgX = p.P.X, DwgY = p.P.Y, X = at.X / 12, Y = at.Y / 12, Distance = p.D,
                    Evidence = $"column in the model ({p.R.Source ?? revit.Source})"
                });
            }

            var usable = result.Floors.Where(f => f.Usable).ToList();
            result.RevitProven = usable.Count > 0 && usable.All(f => f.RevitProven);
            result.RevitSummary = usable.Count == 0 ? "no floor lined up by its columns"
                : $"{usable.Count(f => f.Status == FloorAlignment.Confirmed)} floor(s) lined up by their columns on the model's columns ({revit.Source ?? "model"})" +
                  (usable.Any(f => f.Status == FloorAlignment.AgreesWithOthers) ? $", {usable.Count(f => f.Status == FloorAlignment.AgreesWithOthers)} by their stacked pipes or the shared sheet frame" : "");

            result.Messages.Add($"PDF only: {usable.Count} of {result.Floors.Count} floor(s) lined up with Revit by their columns.");
            foreach (var f in result.Floors.Where(f => !f.Usable))
                result.Messages.Add($"{FloorKey.Describe(f.Floor)}: {f.Status} — {string.Join("; ", f.Notes)}. Its pipes will not be placed.");
            result.Messages.Add(result.DrawingsMatch
                ? $"Check columns: {result.Anchors.Count} land within {rules.ColumnTolerance:0.#}\" of a model column."
                : $"Check columns: only {result.Anchors.Count} floor(s) confirmed by columns (3 needed).");
            result.Messages.Add(result.RevitProven ? $"Revit position PROVEN: {result.RevitSummary}." : $"Revit position NOT proven: {result.RevitSummary}.");
            result.Messages.Add(result.Passed ? "Result: PASSED." : "Result: NOT PASSED — nothing will be placed.");
            return result;
        }

        /// <summary>Height above a level where its floor plan cuts the columns (feet).</summary>
        private const double CutHeight = 4;

        /// <summary>
        /// For floor matching (FloorSequence): each plan's columns are placed once on all the model's columns (stacked
        /// columns count once, any size), then scored per level on the columns that level's plan cut passes through:
        /// 2 × matched / (page columns + level columns). Floors with the same columns fit alike; a cellar, a setback or a
        /// transfer floor fits its own level clearly better. Floor -> level name -> 0..1; a floor whose columns could not be
        /// placed is left out.
        /// </summary>
        public static Dictionary<string, Dictionary<string, double>> LevelFits(PdfPlanResult plans, RevitColumns revit, PdfOnlyRules rules)
        {
            var result = new Dictionary<string, Dictionary<string, double>>();
            if (plans == null || revit == null || revit.Columns.Count == 0) return result;
            var all = revit.At(null).Select(c => new ColumnPoint { X = c.X, Y = c.Y, MinZ = c.MinZ, MaxZ = c.MaxZ, Source = c.Source }).ToList();
            var cuts = revit.Levels.ToDictionary(kv => kv.Key, kv => Standing(revit, kv.Value + CutHeight), StringComparer.OrdinalIgnoreCase);

            var byPage = new Dictionary<int, Dictionary<string, double>>();         // a typical plan's floors share one page: placed once
            foreach (var plan in plans.Plans)
            {
                if (plan.Problem != null || plan.Columns.Count < rules.MinColumns || result.ContainsKey(plan.Floor)) continue;
                if (byPage.TryGetValue(plan.Page, out var same)) { result[plan.Floor] = same; continue; }
                var (best, _) = Search(plan.Columns, all, rules);
                if (best == null) continue;
                var fits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in cuts)
                {
                    int matched = plan.Columns.Count(p =>
                    {
                        var q = best.Apply(p.X, p.Y);
                        return kv.Value.Any(c => SameShape(p, c, best) && Math.Sqrt(Math.Pow(c.X * 12 - q.X, 2) + Math.Pow(c.Y * 12 - q.Y, 2)) <= rules.SearchTolerance);
                    });
                    fits[kv.Key] = kv.Value.Count == 0 ? 0 : 2.0 * matched / (plan.Columns.Count + kv.Value.Count);
                }
                result[plan.Floor] = byPage[plan.Page] = fits;
            }
            return result;
        }

        /// <summary>Columns a horizontal plane at <paramref name="z"/> passes through, one per spot.</summary>
        private static List<ColumnPoint> Standing(RevitColumns revit, double z)
        {
            var one = new List<ColumnPoint>();
            foreach (var c in revit.Columns.Where(c => c.MinZ <= z && c.MaxZ >= z))
                if (!one.Any(o => Math.Abs(o.X - c.X) < 1.0 / 12 && Math.Abs(o.Y - c.Y) < 1.0 / 12)) one.Add(c);
            return one;
        }

        private static string Ratio(PdfPlan plan) => plan.ScaleText ?? PdfPlanReader.Ratio(plan.Scale);

        /// <summary>
        /// The rotation + shift that puts the most PDF columns on model columns (search tolerance), from every pair of PDF
        /// columns matched to every pair of model columns the same distance apart. Also the support of the best placement
        /// that differs from it (more than 1° or 1 ft away), to spot a symmetric building.
        /// </summary>
        private static (Fit Best, int Runner) Search(List<PdfColumn> pdf, List<ColumnPoint> model, PdfOnlyRules rules)
        {
            var m = model.Select(c => (X: c.X * 12, Y: c.Y * 12, C: c)).ToList();
            double tol = rules.SearchTolerance;
            // the building: the model columns' extent, 5 ft around (a placement shifted by a bay of a regular grid puts
            // several columns on columns too, but throws the rest out of the building)
            double x0 = m.Min(c => c.X) - 60, x1 = m.Max(c => c.X) + 60, y0 = m.Min(c => c.Y) - 60, y1 = m.Max(c => c.Y) + 60;
            var tried = new List<(Fit F, int N, double Err)>();
            var seen = new HashSet<(long, long, long)>();
            for (int i = 0; i < pdf.Count; i++)
                for (int j = i + 1; j < pdf.Count; j++)
                {
                    double dx = pdf[j].X - pdf[i].X, dy = pdf[j].Y - pdf[i].Y, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 60) continue;                                            // short pairs give a poor angle
                    for (int k = 0; k < m.Count; k++)
                        for (int l = 0; l < m.Count; l++)
                        {
                            if (k == l) continue;
                            double ex = m[l].X - m[k].X, ey = m[l].Y - m[k].Y;
                            if (Math.Abs(Math.Sqrt(ex * ex + ey * ey) - d) > 2 * tol) continue;
                            double a = Math.Atan2(ey, ex) - Math.Atan2(dy, dx);
                            var f = new Fit { Cos = Math.Cos(a), Sin = Math.Sin(a) };
                            f.Tx = m[k].X - (pdf[i].X * f.Cos - pdf[i].Y * f.Sin);
                            f.Ty = m[k].Y - (pdf[i].X * f.Sin + pdf[i].Y * f.Cos);
                            if (!seen.Add(((long)Math.Round(a * 180 / Math.PI * 2), (long)Math.Round(f.Tx / 6), (long)Math.Round(f.Ty / 6)))) continue;
                            int inside = pdf.Count(p => { var q = f.Apply(p.X, p.Y); return q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1; });
                            if (inside < 0.8 * pdf.Count) continue;
                            int n = 0; double err = 0;
                            foreach (var p in pdf)
                            {
                                var q = f.Apply(p.X, p.Y);
                                double best = m.Where(r => SameShape(p, r.C, f)).Select(r => Math.Sqrt((r.X - q.X) * (r.X - q.X) + (r.Y - q.Y) * (r.Y - q.Y)))
                                               .DefaultIfEmpty(double.MaxValue).Min();
                                if (best <= tol) { n++; err += best; }
                            }
                            if (n >= Math.Min(rules.MinColumns, 3)) tried.Add((f, n, err));
                        }
                }
            if (tried.Count == 0) return (null, 0);
            // the strongest candidates, each fitted on its own columns: a candidate from the right columns with a slightly
            // wrong angle ends on the same placement; a real alternative moves the columns by more than 2 ft
            var refined = new List<Fit>();
            foreach (var t in tried.OrderByDescending(t => t.N).ThenBy(t => t.Err).Take(25))
            {
                Refine(t.F, pdf, model, rules);
                if (refined.Any(o => Moves(o, t.F, pdf) <= 24)) continue;
                refined.Add(t.F);
            }
            var ordered = refined.OrderByDescending(f => f.Pairs.Count).ThenBy(f => f.Pairs.Sum(p => p.D)).ToList();
            return (ordered[0], ordered.Count > 1 ? ordered[1].Pairs.Count : 0);
        }

        /// <summary>How far (inches) two placements put the same PDF column apart, at worst.</summary>
        private static double Moves(Fit a, Fit b, List<PdfColumn> pdf) =>
            pdf.Max(p => { var u = a.Apply(p.X, p.Y); var v = b.Apply(p.X, p.Y); return Math.Sqrt(Math.Pow(u.X - v.X, 2) + Math.Pow(u.Y - v.Y, 2)); });

        /// <summary>Least-squares rotation + shift on the matched columns (twice, re-pairing in between), and the columns' own scale.</summary>
        private static void Refine(Fit f, List<PdfColumn> pdf, List<ColumnPoint> model, PdfOnlyRules rules)
        {
            for (int round = 0; round < 3; round++)
            {
                Pair(f, pdf, model, rules.SearchTolerance);
                if (f.Pairs.Count < 2) return;
                var src = f.Pairs.Select(p => (X: p.P.X, Y: p.P.Y)).ToList();
                var dst = f.Pairs.Select(p => (X: p.R.X * 12, Y: p.R.Y * 12)).ToList();
                double sx = src.Average(p => p.X), sy = src.Average(p => p.Y), tx = dst.Average(p => p.X), ty = dst.Average(p => p.Y);
                double a = 0, b = 0, ss = 0;
                for (int i = 0; i < src.Count; i++)
                {
                    double px = src[i].X - sx, py = src[i].Y - sy, qx = dst[i].X - tx, qy = dst[i].Y - ty;
                    a += px * qx + py * qy; b += px * qy - py * qx; ss += px * px + py * py;
                }
                double ang = Math.Atan2(b, a);
                f.Cos = Math.Cos(ang); f.Sin = Math.Sin(ang);
                f.Scale = ss > 0 ? Math.Sqrt(a * a + b * b) / ss : 1;
                f.Tx = tx - (sx * f.Cos - sy * f.Sin);
                f.Ty = ty - (sx * f.Sin + sy * f.Cos);
            }
            Pair(f, pdf, model, rules.SearchTolerance);
        }

        /// <summary>Each PDF column with its nearest model column under the fit, when within the tolerance (inches).</summary>
        private static void Pair(Fit f, List<PdfColumn> pdf, List<ColumnPoint> model, double tol)
        {
            f.Pairs.Clear();
            foreach (var p in pdf)
            {
                var q = f.Apply(p.X, p.Y);
                var r = model.Where(c => SameShape(p, c, f)).Select(c => (C: c, D: Math.Sqrt(Math.Pow(c.X * 12 - q.X, 2) + Math.Pow(c.Y * 12 - q.Y, 2))))
                             .OrderBy(t => t.D).FirstOrDefault();
                if (r.C != null && r.D <= tol && !f.Pairs.Any(x => x.R == r.C)) f.Pairs.Add((p, r.C, r.D));
            }
        }

        /// <summary>
        /// The same column shape (1'x2' against 2'x1' is not): the PDF rectangle's sides, turned by the fit, against the
        /// model column's box. Unknown sizes (a column drawn turned, a turned fit) always pass.
        /// </summary>
        private static bool SameShape(PdfColumn p, ColumnPoint c, Fit f)
        {
            const double tol = 4;
            if (p.W <= 0 || p.L <= 0 || c.W <= 0 || c.L <= 0) return true;
            double w = c.W * 12, l = c.L * 12;
            if (Math.Abs(f.Sin) < 0.1) return Math.Abs(p.W - w) <= tol && Math.Abs(p.L - l) <= tol;
            if (Math.Abs(f.Cos) < 0.1) return Math.Abs(p.W - l) <= tol && Math.Abs(p.L - w) <= tol;
            return true;
        }

        /// <summary>
        /// The shift (PDF inches, this page -> the neighbour's page) that puts this floor's pipe circles on the same-layer
        /// circles of the neighbour: at least minStacked circles agree within the tolerance, twice as many as any other shift.
        /// </summary>
        private static FloorShift Stack(DwgRiserResult risers, string floor, string neighbour, PdfOnlyRules rules)
        {
            var mine = risers.Risers.Where(r => r.Floor == floor).SelectMany(r => r.Symbols.Where(s => s.Block == DwgRiserReader.PipeCircle).Select(s => (S: s, r.Tag))).ToList();
            var theirs = risers.Risers.Where(r => r.Floor == neighbour).SelectMany(r => r.Symbols.Where(s => s.Block == DwgRiserReader.PipeCircle).Select(s => (S: s, r.Tag))).ToList();
            var votes = new List<(double Dx, double Dy, int Id, string Tag)>();
            for (int i = 0; i < mine.Count; i++)
                foreach (var t in theirs.Where(t => t.S.Layer == mine[i].S.Layer))
                    votes.Add((t.S.X - mine[i].S.X, t.S.Y - mine[i].S.Y, i, mine[i].Tag));
            if (votes.Count == 0) return null;
            double tol = rules.StackTolerance;
            List<(double Dx, double Dy, int Id, string Tag)> Around(double dx, double dy) => votes.Where(v => Math.Abs(v.Dx - dx) <= tol && Math.Abs(v.Dy - dy) <= tol).ToList();
            var scored = votes.Select(v => (v.Dx, v.Dy, N: Around(v.Dx, v.Dy).Select(x => x.Id).Distinct().Count())).OrderByDescending(x => x.N).ToList();
            var best = scored[0];
            int runner = scored.Where(x => Math.Abs(x.Dx - best.Dx) > 12 || Math.Abs(x.Dy - best.Dy) > 12).Select(x => x.N).DefaultIfEmpty(0).Max();
            if (best.N < rules.MinStacked || best.N < 2 * runner) return null;
            var fit = Around(best.Dx, best.Dy);
            return new FloorShift
            {
                Dx = fit.Average(v => v.Dx), Dy = fit.Average(v => v.Dy), Support = best.N, RunnerUp = runner,
                Tags = fit.Select(v => v.Tag).Where(t => t != null && !t.StartsWith("?")).Distinct().OrderBy(t => t).ToList()
            };
        }

        /// <summary>Stacks (tags) whose pipes land within the tolerance of the same stack on the floor below, in Revit.</summary>
        private static List<string> Stacked(DwgRiserResult risers, FloorAlignment a, FloorAlignment b, PdfOnlyRules rules)
        {
            var there = risers.Risers.Where(r => r.Floor == b.Floor && r.Tag != null)
                              .SelectMany(r => r.Symbols.Where(s => s.Block == DwgRiserReader.PipeCircle).Select(s => (r.Tag, s.Layer, P: b.ToRevit(s.X, s.Y).Value))).ToList();
            return risers.Risers.Where(r => r.Floor == a.Floor && r.Tag != null)
                .Where(r => r.Symbols.Where(s => s.Block == DwgRiserReader.PipeCircle).Any(s =>
                {
                    var p = a.ToRevit(s.X, s.Y).Value;
                    return there.Any(t => t.Tag == r.Tag && t.Layer == s.Layer && Math.Sqrt(Math.Pow(t.P.X - p.X, 2) + Math.Pow(t.P.Y - p.Y, 2)) * 12 <= rules.StackTolerance);
                }))
                .Select(r => r.Tag).Distinct().OrderBy(t => t).ToList();
        }

        private static IEnumerable<FloorAlignment> Neighbours(AlignmentResult result, FloorAlignment fa)
        {
            var ordered = result.Floors.OrderBy(f => FloorKey.Order(f.Floor)).ToList();
            int i = ordered.IndexOf(fa);
            if (i > 0) yield return ordered[i - 1];
            if (i < ordered.Count - 1) yield return ordered[i + 1];
        }

        private static double Angle(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }
    }
}
