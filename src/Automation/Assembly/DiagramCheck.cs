using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Legend;

namespace SleevesOpenings.Automation.Assembly
{
    /// <summary>
    /// Phase 8: the riser diagram against the openings from the floor plans. A tagged run of the diagram (GX-1, ERV-1)
    /// that the plans show on none of its slabs is reported "only in the riser diagram" (a riser the plans do not draw, or
    /// draw without a tag); an undefined tag with a run (GX-2) is reported as undefined, with the slabs it passes. Where
    /// both show a crossing and the sizes differ, the crossing gets a note. Nothing is placed from the diagram: it has no
    /// plan positions.
    /// </summary>
    public static class DiagramCheck
    {
        public static void Apply(RiserAssembly assembly, RiserDiagramResult diagram, SleevesOpenings.Automation.Legend.Legend legend, LegendRules rules)
        {
            if (assembly == null || diagram == null) return;
            var runs = new HashSet<string>(diagram.Risers.Select(x => Norm(x.Tag)));
            foreach (var r in diagram.Risers)
            {
                var m = legend.Meaning(r.Tag, rules);
                string prefix = Prefix(r.Tag);
                // the same tag (GX-2 = GX2); else the same prefix (the diagram's ERV-1 is the plans' ERV-EX / ERV-SA), leaving
                // out crossings that another run of the diagram names exactly
                var ours = assembly.Crossings.Where(c => c.Tag != null && Norm(c.Tag) == Norm(r.Tag)).ToList();
                if (ours.Count == 0)
                    ours = assembly.Crossings.Where(c => c.Tag != null && Prefix(c.Tag) == prefix && !runs.Contains(Norm(c.Tag))).ToList();
                string first = r.Slabs[0].Slab;
                string where = $"the riser diagram (page {r.Page}) shows {r.Tag} {r.Describe()}";

                // a PDF label with no DWG riser that has the same size one floor down is most likely this run
                var pdfOnly = assembly.Issues.FirstOrDefault(i => i.Type == AssemblyIssue.OnlyPdf && r.Slabs.Any(s => s.Size != null && i.Detail.Contains($"'{s.Size}")));
                string hint = pdfOnly == null ? "" : $"; the PDF label on the {FloorKey.Describe(pdfOnly.Floor)} ('{Quoted(pdfOnly.Detail)}') is probably this riser";

                if (m.Category == TagCategory.Undefined)
                {
                    assembly.Issues.Add(new AssemblyIssue
                    {
                        Floor = first, Tag = r.Tag, Type = AssemblyIssue.Undefined,
                        Detail = $"{r.Tag} is not defined in the PDF (abbreviations, symbols, schedules); {where}{hint}"
                    });
                    continue;
                }
                if (m.Category != TagCategory.Opening) continue;

                var missing = r.Slabs.Where(s => !ours.Any(c => c.Floor == s.Slab)).ToList();
                if (missing.Count == r.Slabs.Count)
                    assembly.Issues.Add(new AssemblyIssue
                    {
                        Floor = first, Tag = r.Tag, Type = AssemblyIssue.OnlyDiagram,
                        Detail = $"{where} ({m.Entry?.Definition ?? m.System}); no {prefix} riser is drawn with a tag on the floor plans{hint}"
                    });
                else if (missing.Count > 0)
                    assembly.Issues.Add(new AssemblyIssue
                    {
                        Floor = missing[0].Slab, Tag = r.Tag, Type = AssemblyIssue.OnlyDiagram,
                        Detail = $"{where}; the floor plans show no {prefix} opening on the {string.Join(", ", missing.Select(s => FloorKey.Describe(s.Slab)))} slab(s)"
                    });

                // sizes: only when one crossing of that name is on the slab and none has the diagram's size (ERV supply and exhaust share a name)
                foreach (var (slab, size) in r.Slabs.Where(x => x.Size != null))
                {
                    var here = ours.Where(c => c.Floor == slab && c.Size != null).ToList();
                    if (here.Count == 1 && !Same(here[0].Size, size))
                    {
                        // the plan wins (rule 17), but roof sizes are to be verified against the riser diagram (rule 57)
                        here[0].Notes.Add($"the riser diagram shows {size} for {r.Tag} at this slab" + (here[0].Roof ? "; the plan size is used, verify it (rule 57)" : ""));
                        if (here[0].Roof) here[0].Check = true;
                    }
                }
            }
            Dampers(assembly, diagram, runs);
        }

        /// <summary>
        /// Dampers (MD, FSD, GD) sit where a duct leaves the building, on the slab line where its run ends. They go on the
        /// plan openings that end at that slab: runs named by a plan tag (GX-2) to that tag; runs under a unit (ERV-1) to
        /// the openings of that prefix (ERV-EX / ERV-SA); plain runs to the other exhaust openings. When every run of the
        /// group has the same damper, every opening gets it; otherwise runs and openings are paired by the closest size.
        /// A paired opening with no size takes the diagram's. A damper with no opening left is reported.
        /// </summary>
        private static void Dampers(RiserAssembly assembly, RiserDiagramResult diagram, HashSet<string> named)
        {
            var planTags = new HashSet<string>(assembly.Crossings.Where(c => c.Tag != null).Select(c => Norm(c.Tag)));
            bool Ends(Crossing c) => !assembly.Crossings.Any(o => o.Tag == c.Tag && FloorKey.Order(o.Floor) > FloorKey.Order(c.Floor));
            string Group(DiagramRiser r) =>
                r.Near == null ? "" : planTags.Contains(Norm(r.Near)) ? "=" + Norm(r.Near) : named.Contains(Norm(r.Near)) && Prefix(r.Near) != "ERV" ? "x" : Prefix(r.Near);
            bool InGroup(Crossing c, string g) =>
                g.StartsWith("=") ? Norm(c.Tag) == g.Substring(1)
                : g == "" ? Prefix(c.Tag) != "ERV" && Prefix(c.Tag) != "GX"
                : Prefix(c.Tag) == g;

            var withDampers = diagram.Runs.Where(r => r.Dampers.Count > 0).ToList();
            foreach (var bySlab in withDampers.SelectMany(r => r.Dampers.Select(d => (Run: r, d.Slab, d.Code))).GroupBy(x => x.Slab))
                foreach (var byGroup in bySlab.GroupBy(x => Group(x.Run)))
                {
                    string slab = bySlab.Key, group = byGroup.Key;
                    var runs = byGroup.GroupBy(x => x.Run).Select(g => (Run: g.Key, Codes: g.Select(x => x.Code).Distinct().ToList())).ToList();
                    if (group == "x") continue;      // a run the plans do not draw (GX-1): already reported "only in the riser diagram"
                    var openings = assembly.Crossings.Where(c => c.Floor == slab && c.Tag != null && c.System != "DryerExhaust" && c.Status != Crossing.Skip && Ends(c) && InGroup(c, group)).ToList();
                    if (openings.Count == 0)
                    {
                        foreach (var (run, codes) in runs) Report(assembly, slab, run, codes, "no opening on the floor plans ends at this slab");
                        continue;
                    }
                    var codeSets = runs.Select(x => string.Join("-", x.Codes)).Distinct().ToList();
                    if (codeSets.Count == 1 && runs.Count >= openings.Count)
                    {
                        // every run of the group carries the same damper(s): every opening ending here gets them
                        foreach (var c in openings) Put(c, runs[0].Codes, null, $"every duct of this group ends at the {FloorKey.Describe(slab)} with {codeSets[0]} on the riser diagram");
                        continue;
                    }
                    // pair by the closest size (openings without a size last, they take the diagram's)
                    var left = runs.ToList();
                    foreach (var c in openings.OrderBy(c => c.Size == null ? 1 : 0))
                    {
                        if (left.Count == 0) break;
                        var best = left.OrderBy(x => c.Size == null ? 0 : Math.Abs(Area(x.Run.SizeAt(slab)) - Area(c.Size))).First();
                        left.Remove(best);
                        Put(c, best.Codes, best.Run.SizeAt(slab),
                            $"paired with the {best.Run.SizeAt(slab)} duct of the riser diagram (closest size{(c.Size == null ? "; this opening has no size on the plans" : "")})");
                    }
                    foreach (var (run, codes) in left) Report(assembly, slab, run, codes, "more ducts end here on the riser diagram than on the floor plans");
                }
        }

        private static void Put(Crossing c, List<string> codes, DuctSize size, string how)
        {
            foreach (var code in codes) if (!c.Dampers.Contains(code)) c.Dampers.Add(code);
            c.Notes.Add($"{string.Join("-", codes)} on this duct ({how})");
            if (c.Size == null && size != null)
            {
                c.Size = size;
                c.Notes.Add($"size {size} from the riser diagram");
                if (c.Notes.Remove("no size on either floor's label") && c.Status == Crossing.Review) c.Status = Crossing.Place;
            }
        }

        private static void Report(RiserAssembly assembly, string slab, DiagramRiser run, List<string> codes, string why) =>
            assembly.Issues.Add(new AssemblyIssue
            {
                Floor = slab, Tag = run.Near, Type = AssemblyIssue.OnlyDiagram,
                Detail = $"the riser diagram (page {run.Page}) shows {string.Join("-", codes)} on a {run.SizeAt(slab)} duct{(run.Near != null ? $" of {run.Near}" : "")} at the {FloorKey.Describe(slab)}; {why}"
            });

        private static double Area(DuctSize s) => s == null ? 0 : s.Diameter.HasValue ? Math.PI * s.Diameter.Value * s.Diameter.Value / 4 : (s.Width ?? 0) * (s.Length ?? 0);

        private static string Prefix(string tag) => Regex.Match((tag ?? "").ToUpperInvariant(), "^[A-Z]+").Value;

        /// <summary>"GX-2" and "GX2" are the same tag.</summary>
        private static string Norm(string tag) => (tag ?? "").ToUpperInvariant().Replace("-", "").Replace(" ", "");

        private static string Quoted(string detail)
        {
            var m = Regex.Match(detail ?? "", "'([^']+)'");
            return m.Success ? m.Groups[1].Value : detail;
        }

        private static bool Same(DuctSize a, DuctSize b) =>
            a.Diameter.HasValue || b.Diameter.HasValue ? a.Diameter == b.Diameter
                : (a.Width == b.Width && a.Length == b.Length) || (a.Width == b.Length && a.Length == b.Width);
    }
}
