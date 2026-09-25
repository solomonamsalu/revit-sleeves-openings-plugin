using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Compare;

namespace SleevesOpenings.Automation.Assembly
{
    /// <summary>
    /// risers.json (plan section 6.7): the merged list of slab openings with where each came from, saved in the run
    /// folder (%LOCALAPPDATA%\SleevesOpenings\runs\&lt;model&gt;\&lt;time&gt;). Phase 6 places from it; it is also the
    /// record of what was read when.
    /// </summary>
    public static class RiserListFile
    {
        public static string RunFolder(string modelTitle, DateTime at)
        {
            string safe = string.Concat((modelTitle ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "runs", safe, at.ToString("yyyyMMdd-HHmmss"));
        }

        public static string Write(string folder, string project, DisciplineFiles inputs, AlignmentResult alignment, RiserAssembly assembly,
                                   SleevesOpenings.Automation.Legend.Legend legend, SleevesOpenings.Automation.Legend.LegendRules rules,
                                   SoCompareResult so = null, RiserDiagramResult diagram = null)
        {
            Directory.CreateDirectory(folder);
            var tags = assembly.Crossings.Select(c => c.Tag).Where(t => t != null).Distinct().OrderBy(t => t);
            var doc = new
            {
                project,
                run = DateTime.Now.ToString("s"),
                inputs = new { pdf = inputs?.Pdf, dwg = inputs?.Dwg, xrefs = inputs?.Xrefs, soSet = string.IsNullOrEmpty(inputs?.Reference) ? null : inputs.Reference },
                alignment = alignment == null ? null : new
                {
                    passed = alignment.Passed,
                    revitProven = alignment.RevitProven,
                    revit = alignment.RevitSummary,
                    floors = alignment.Floors.Select(f => new { floor = f.Floor, level = f.Level, status = f.Status, usable = f.Usable, via = f.Reference?.Name })
                },
                legend = tags.Select(t =>
                {
                    var m = legend?.Meaning(t, rules);
                    return new { tag = t, definition = m?.Entry?.Definition, source = m?.Entry?.Source, system = m?.System };
                }),
                summary = new
                {
                    place = assembly.Crossings.Count(c => c.Status == Crossing.Place),
                    review = assembly.Crossings.Count(c => c.Status == Crossing.Review),
                    skip = assembly.Crossings.Count(c => c.Status == Crossing.Skip),
                    issues = assembly.Issues.Count
                },
                openings = assembly.Crossings.Select(c => new
                {
                    floor = c.Floor, level = c.Level, x = Math.Round(c.X, 4), y = Math.Round(c.Y, 4),       // feet, Revit internal coordinates
                    tag = c.Tag, system = c.System, size = c.Size?.ToString(),
                    width = c.Size?.Width, length = c.Size?.Length, diameter = c.Size?.Diameter,
                    ducts = c.Ducts, roof = c.Roof, status = c.Status, confidence = c.Confidence, pdf = c.Pdf,
                    from = c.From, notes = c.Notes
                }),
                issues = assembly.Issues.Select(i => new
                {
                    floor = i.Floor, type = i.Type, tag = i.Tag,
                    x = i.X.HasValue ? Math.Round(i.X.Value, 4) : (double?)null, y = i.Y.HasValue ? Math.Round(i.Y.Value, 4) : (double?)null,
                    detail = i.Detail
                }),
                riserDiagram = diagram == null ? null : new
                {
                    page = diagram.Page, floors = diagram.Floors, runs = diagram.Columns,
                    tagged = diagram.Risers.Select(r => new { tag = r.Tag, slabs = r.Slabs.Select(x => new { slab = x.Slab, size = x.Size?.ToString() }) })
                },
                soSet = so == null ? null : new
                {
                    summary = so.Summary(), floors = so.Floors, notCompared = so.NotCompared,
                    matches = so.Matches.Select(m => new
                    {
                        floor = m.Floor, status = m.Status, ours = m.Ours?.Name, ourStatus = m.Ours?.Status, ourOpening = m.OurSize,
                        theirs = m.Theirs?.Name, theirSize = m.Theirs?.SizeText,
                        x = m.Theirs != null ? Math.Round(m.Theirs.X, 4) : (double?)null, y = m.Theirs != null ? Math.Round(m.Theirs.Y, 4) : (double?)null,
                        apart = m.Off.HasValue ? Math.Round(m.Off.Value, 1) : (double?)null, detail = m.Detail
                    })
                }
            };
            string path = Path.Combine(folder, "risers.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(doc, Formatting.Indented));
            return path;
        }
    }
}
