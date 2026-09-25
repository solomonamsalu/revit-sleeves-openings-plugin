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
            foreach (var r in diagram.Risers)
            {
                var m = legend.Meaning(r.Tag, rules);
                string prefix = Prefix(r.Tag);
                var ours = assembly.Crossings.Where(c => c.Tag != null && Prefix(c.Tag) == prefix).ToList();
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
                    if (here.Count == 1 && !Same(here[0].Size, size)) here[0].Notes.Add($"the riser diagram shows {size} for {r.Tag} at this slab");
                }
            }
        }

        private static string Prefix(string tag) => Regex.Match((tag ?? "").ToUpperInvariant(), "^[A-Z]+").Value;

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
