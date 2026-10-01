using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// Sprinkler / standpipe plans (rules.json "sprinkler"). The fire protection engineer does not group pipes under a tag
    /// bubble: each pipe through a slab is a small riser symbol with a leader to a written label, size first:
    /// '3" SPRINKLER RISER UP/DN', '2" SPRINKLER CHUTE RISER UP/DN', '4" FDC DN', '3" SPRINKLER RISER W/ INSPECTOR TEST TEE'.
    /// Each label becomes one pipe "group" at the leader's arrow (snapped to the symbol drawn there) with the label as its
    /// service line, so <see cref="Assembly.PipeAssembler"/> merges the floors exactly as for plumbing. The services are
    /// recognised by their words (services.*.match). Free of the Revit API.
    /// </summary>
    public static class SprinklerReader
    {
        /// <summary>A label and its box (drawing units, or real inches on a PDF page).</summary>
        internal class Label { public string Text; public double MinX, MinY, MaxX, MaxY, X, Y; }
        /// <summary>A leader: the arrow's tip (at the riser) and its tail (at the label).</summary>
        internal class Arrow { public (double X, double Y) Tip, Tail; }

        /// <summary>One labelled pipe: the label's text and where its leader points (snapped to the symbol drawn there).</summary>
        internal class Pipe { public string Text, Service, Layer; public double X, Y, LabelX, LabelY; public bool OnSymbol; }

        public static DwgRiserResult Read(CadDocument doc, DwgSheetIndex sheets, PlumbingRules rules)
        {
            var result = new DwgRiserResult();
            var pipes = Pipes(doc, rules, result.Warnings);
            foreach (var floor in sheets.Floors)
            {
                if (!floor.HasRegion) { result.Warnings.Add($"{FloorKey.Describe(floor.Floor)}: plan region unknown, sprinkler risers not read."); continue; }
                bool In(double x, double y) => x >= floor.MinX && x <= floor.MaxX && y >= floor.MinY && y <= floor.MaxY;
                AddFloor(floor.Floor, pipes.Where(p => In(p.LabelX, p.LabelY)), result);
            }
            return result;
        }

        /// <summary>One floor's labelled pipes as pipe "groups" (one per label), numbered per kind from left to right.</summary>
        internal static void AddFloor(string floor, IEnumerable<Pipe> pipes, DwgRiserResult result, string evidence = null)
        {
            var found = new List<DwgRiser>();
            foreach (var p in pipes)
            {
                if (p.Service == null) { result.Warnings.Add($"{FloorKey.Describe(floor)}: '{p.Text}' has no leader to a riser; not placed"); continue; }
                var r = new DwgRiser { Floor = floor, X = p.X, Y = p.Y, MinX = p.X, MaxX = p.X, MinY = p.Y, MaxY = p.Y };
                r.Symbols.Add(new RiserSymbol { X = p.X, Y = p.Y, Layer = p.Layer, Block = DwgRiserReader.PipeCircle });
                r.TagTexts.Add(p.Text);
                r.Tags.Add(p.Service);                   // numbered below once the floor is read
                r.Evidence.Add(p.OnSymbol ? "riser symbol at the leader's arrow" : "leader arrow (no symbol drawn there)");
                if (evidence != null) r.Evidence.Add(evidence);
                found.Add(r);
            }
            // one label per pipe: number the pipes of one kind on the floor from left to right (SP-1, SP-2...)
            foreach (var g in found.GroupBy(r => r.Tags[0]))
            {
                int n = 1;
                foreach (var r in g.OrderBy(r => Math.Round(r.X / 12)).ThenBy(r => r.Y)) r.Tags[0] = (n++).ToString();
            }
            result.Risers.AddRange(found);
        }

        /// <summary>
        /// The labelled pipes of a whole drawing (an office xref at Revit's 0,0): what the alignment checks the engineer's
        /// risers against, as <see cref="DwgRiserReader.Symbols"/> does for blocks and circles.
        /// </summary>
        public static List<RiserSymbol> Symbols(CadDocument doc, PlumbingRules rules) =>
            Pipes(doc, rules, new List<string>()).Where(p => p.Service != null)
                .Select(p => new RiserSymbol { X = p.X, Y = p.Y, Layer = p.Layer, Block = DwgRiserReader.PipeCircle }).ToList();

        private static List<Pipe> Pipes(CadDocument doc, PlumbingRules rules, List<string> warnings)
        {
            var services = Patterns(rules);
            if (services.Count == 0) { warnings.Add("rules.json sprinkler.services: no service has a 'match'; nothing read."); return new List<Pipe>(); }

            var labels = new List<Label>(); var arrows = new List<Arrow>(); var symbols = new List<(double X, double Y, string Layer)>();
            Collect(doc.Entities, DwgXform.Identity, 0, services, labels, arrows, symbols);
            return Join(labels, arrows, symbols, rules);
        }

        /// <summary>The service patterns (services.*.match); empty when none is set.</summary>
        internal static List<Regex> Patterns(PlumbingRules rules) =>
            rules.Services.Where(kv => !string.IsNullOrEmpty(kv.Value.Match)).Select(kv => new Regex(kv.Value.Match, RegexOptions.IgnoreCase)).ToList();

        /// <summary>
        /// Each label to the leader whose tail lands on its box, and the leader's arrow to the riser symbol drawn there
        /// (a label with no leader comes back with no service: reported, not placed). Inches.
        /// </summary>
        internal static List<Pipe> Join(List<Label> labels, List<Arrow> arrows, List<(double X, double Y, string Layer)> symbols, PlumbingRules rules)
        {
            var list = new List<Pipe>();
            double reach = rules.Profile?.LeaderTextDistance > 0 ? rules.Profile.LeaderTextDistance * 2 : 24;   // leader tail to the label's box
            double snap = rules.Profile?.PointTolerance > 0 ? Math.Min(rules.Profile.PointTolerance, 8) : 6;     // arrow tip to the symbol drawn there
            foreach (var l in labels)
            {
                // the leader whose tail lands on this label (the arrow end is the one away from the text)
                var best = arrows.Select(a => (A: a, D: BoxDistance(l, a.Tail))).Where(t => t.D <= reach).OrderBy(t => t.D).FirstOrDefault();
                if (best.A == null) { list.Add(new Pipe { Text = l.Text, LabelX = l.X, LabelY = l.Y }); continue; }
                var tip = best.A.Tip;
                var sym = symbols.Select(q => (S: q, D: Math.Sqrt(Math.Pow(q.X - tip.X, 2) + Math.Pow(q.Y - tip.Y, 2))))
                                 .Where(t => t.D <= snap).OrderBy(t => t.D).Select(t => ((double X, double Y, string Layer)?)t.S).FirstOrDefault();
                list.Add(new Pipe
                {
                    Text = l.Text, Service = rules.ServiceForLabel(l.Text, out _) ?? "?", LabelX = l.X, LabelY = l.Y,
                    X = sym?.X ?? tip.X, Y = sym?.Y ?? tip.Y, Layer = sym?.Layer ?? "(leader arrow)", OnSymbol = sym != null
                });
            }
            return list;
        }

        private static void Collect(IEnumerable<Entity> entities, DwgXform xf, int depth, List<Regex> services,
                                    List<Label> labels, List<Arrow> arrows, List<(double X, double Y, string Layer)> symbols)
        {
            foreach (var e in entities)
            {
                switch (e)
                {
                    case MText m:
                    {
                        string text = DwgRiserReader.MTextLines(m.Value).Replace("\n", " ");
                        if (!services.Any(rx => rx.IsMatch(text))) break;
                        int lines = Math.Max(1, DwgRiserReader.MTextLines(m.Value).Split('\n').Length);
                        double h = Math.Max(m.Height, 1) * xf.Sx;
                        double w = m.RectangleWidth > 0 ? m.RectangleWidth * xf.Sx : text.Length * h * 0.9 / lines;
                        double ht = lines * h * 1.7;
                        var (x, y) = xf.Apply(m.InsertPoint.X, m.InsertPoint.Y);
                        int a = (int)m.AttachmentPoint;                         // 1-9: top/middle/bottom x left/centre/right
                        int col = (a - 1) % 3, row = (a - 1) / 3;
                        double minX = x - w * col / 2.0, maxY = y + ht * row / 2.0;
                        labels.Add(new Label { Text = Normal(text), MinX = minX, MaxX = minX + w, MaxY = maxY, MinY = maxY - ht, X = x, Y = y });
                        break;
                    }
                    case TextEntity t:
                    {
                        string text = (t.Value ?? "").Trim();
                        if (!services.Any(rx => rx.IsMatch(text))) break;
                        var (x, y) = xf.Apply(t.InsertPoint.X, t.InsertPoint.Y);
                        double h = Math.Max(t.Height, 1) * xf.Sx;
                        labels.Add(new Label { Text = Normal(text), MinX = x, MaxX = x + text.Length * h * 0.9, MinY = y, MaxY = y + h, X = x, Y = y });
                        break;
                    }
                    case Leader ld when ld.Vertices.Count >= 2:
                    {
                        var first = xf.Apply(ld.Vertices.First().X, ld.Vertices.First().Y);
                        var last = xf.Apply(ld.Vertices.Last().X, ld.Vertices.Last().Y);
                        arrows.Add(new Arrow { Tip = first, Tail = last });  // a leader starts at its arrowhead
                        break;
                    }
                    case Insert ins:
                    {
                        var (x, y) = xf.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                        symbols.Add((x, y, ins.Layer?.Name));
                        // an office xref wraps the whole floor in one block: look inside (not into small symbols)
                        if (depth < 2 && ins.Block != null && ins.Block.Entities.Count() > 50)
                            Collect(ins.Block.Entities, xf.Then(ins), depth + 1, services, labels, arrows, symbols);
                        break;
                    }
                    case Circle c:
                    {
                        var (x, y) = xf.Apply(c.Center.X, c.Center.Y);
                        if (c.Radius * Math.Abs(xf.Sx) <= 6) symbols.Add((x, y, c.Layer?.Name));
                        break;
                    }
                }
            }
        }

        /// <summary>One line, upper case, MText paragraph alignment codes the cleaner leaves ("\pxql;") removed.</summary>
        internal static string Normal(string s) => Regex.Replace(Regex.Replace(s ?? "", @"\\p[^;]*;", ""), @"\s+", " ").Trim().ToUpperInvariant();

        private static double BoxDistance(Label l, (double X, double Y) p)
        {
            double dx = Math.Max(0, Math.Max(l.MinX - p.X, p.X - l.MaxX)), dy = Math.Max(0, Math.Max(l.MinY - p.Y, p.Y - l.MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
