using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A riser symbol drawn at a duct's centre.</summary>
    public class RiserSymbol
    {
        public double X, Y;
        public string Layer, Block;
    }

    /// <summary>One riser position on one floor: a group of riser symbols side by side, with its tag and size labels.</summary>
    public class DwgRiser
    {
        public string Floor;                                   // FloorKey
        public double X, Y;                                    // group centre (drawing units)
        public double MinX, MinY, MaxX, MaxY;                  // extent of the symbol centres
        public List<RiserSymbol> Symbols = new List<RiserSymbol>();
        public List<string> Tags = new List<string>();         // from bubbles: "TX1", "ERV-SA"
        public List<RiserLabel> Labels = new List<RiserLabel>();
        public List<string> Evidence = new List<string>();     // how tags/labels were attached, for the report

        public string Tag => Tags.FirstOrDefault();
        public RiserLabel Label => Labels.FirstOrDefault(l => l.Down != null || l.Up != null) ?? Labels.FirstOrDefault();
        public bool Dryer => Labels.Any(l => l.Dryer);
    }

    /// <summary>A tag bubble that could not be tied to any riser symbol.</summary>
    public class LooseTag
    {
        public string Floor, Tag;
        public double X, Y;
    }

    public class DwgRiserResult
    {
        public List<DwgRiser> Risers = new List<DwgRiser>();
        public List<LooseTag> LooseTags = new List<LooseTag>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Reads the risers of every floor plan in a DWG (regions from <see cref="DwgSheetIndex"/>). Per floor: riser
    /// symbols (grouped when side by side), tag bubbles joined to a riser by a connector line (or, without one, by
    /// distance), and size / UP-DN labels from multileaders whose arrow points at a riser (or plain text next to it).
    /// Block, attribute and layer names come from <see cref="DwgProfile"/>.
    /// </summary>
    public static class DwgRiserReader
    {
        private class Bubble { public string Tag; public double X, Y, Radius; public bool Used; }
        private class Segment { public List<(double X, double Y)> Points = new List<(double, double)>(); }
        private class Note { public string Text; public double X, Y; public List<(double X, double Y)> Arrows = new List<(double, double)>(); }

        public static DwgRiserResult Read(string path, DwgSheetIndex sheets, DwgProfile profile) =>
            Read(DwgSheetIndex.Open(path), sheets, profile);

        public static DwgRiserResult Read(CadDocument doc, DwgSheetIndex sheets, DwgProfile profile)
        {
            var result = new DwgRiserResult();
            var riserRx = new Regex(profile.RiserBlocks, RegexOptions.IgnoreCase);
            var tagRx = new Regex(profile.TagBlocks, RegexOptions.IgnoreCase);
            var connRx = new Regex(profile.ConnectorLayers, RegexOptions.IgnoreCase);

            var symbols = new List<RiserSymbol>();
            var bubbles = new List<Bubble>();
            var segments = new List<Segment>();
            var notes = new List<Note>();
            Collect(doc.Entities, Xf.Identity, 0, riserRx, tagRx, connRx, profile, symbols, bubbles, segments, notes);

            foreach (var floor in sheets.Floors)
            {
                if (!floor.HasRegion)
                {
                    result.Warnings.Add($"{FloorKey.Describe(floor.Floor)}: plan region unknown, risers not read.");
                    continue;
                }
                bool In(double x, double y) => x >= floor.MinX && x <= floor.MaxX && y >= floor.MinY && y <= floor.MaxY;
                var fs = symbols.Where(s => In(s.X, s.Y)).ToList();
                var fb = bubbles.Where(b => In(b.X, b.Y)).Select(b => new Bubble { Tag = b.Tag, X = b.X, Y = b.Y, Radius = b.Radius }).ToList();
                var fseg = segments.Where(s => s.Points.Any(q => In(q.X, q.Y))).ToList();
                var fn = notes.Where(n => In(n.X, n.Y)).ToList();
                BuildFloor(floor.Floor, fs, fb, fseg, fn, profile, result);
            }
            return result;
        }

        // ------------------------------------------------------------------ collection (with block transforms)

        private struct Xf
        {
            public double Ox, Oy, Cos, Sin, Sx, Sy;
            public static Xf Identity => new Xf { Cos = 1, Sx = 1, Sy = 1 };
            public (double X, double Y) Apply(double x, double y) =>
                (Ox + (x * Sx) * Cos - (y * Sy) * Sin, Oy + (x * Sx) * Sin + (y * Sy) * Cos);
            public Xf Then(Insert ins)
            {
                var (ox, oy) = Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                double a = Math.Atan2(Sin, Cos) + ins.Rotation;
                return new Xf { Ox = ox, Oy = oy, Cos = Math.Cos(a), Sin = Math.Sin(a), Sx = Sx * ins.XScale, Sy = Sy * ins.YScale };
            }
        }

        private static void Collect(IEnumerable<Entity> entities, Xf xf, int depth, Regex riserRx, Regex tagRx, Regex connRx, DwgProfile profile,
                                    List<RiserSymbol> symbols, List<Bubble> bubbles, List<Segment> segments, List<Note> notes)
        {
            foreach (var e in entities)
            {
                switch (e)
                {
                    case Insert ins:
                        string name = ins.Block?.Name ?? "", source = ins.Block?.Source?.Name ?? name;
                        if (riserRx.IsMatch(name) || riserRx.IsMatch(source))
                        {
                            var (x, y) = xf.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                            symbols.Add(new RiserSymbol { X = x, Y = y, Layer = ins.Layer?.Name, Block = source });
                        }
                        else if (tagRx.IsMatch(name) || tagRx.IsMatch(source))
                        {
                            var (x, y) = xf.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                            double r = ins.Block.Entities.OfType<Circle>().Select(c => c.Radius).DefaultIfEmpty(10).Max() * Math.Abs(ins.XScale * xf.Sx);
                            string tag = BubbleTag(ins, profile);
                            if (tag != null) bubbles.Add(new Bubble { Tag = tag, X = x, Y = y, Radius = r });
                        }
                        else if (depth < 3 && ins.Block?.Entities != null && ins.Block.Entities.Count() > 200)
                        {
                            // A packed drawing (bound xref / per-floor block): look inside with its transform.
                            Collect(ins.Block.Entities, xf.Then(ins), depth + 1, riserRx, tagRx, connRx, profile, symbols, bubbles, segments, notes);
                        }
                        break;

                    case LwPolyline pl when connRx.IsMatch(pl.Layer?.Name ?? "") && pl.Vertices.Count >= 2:
                        {
                            var seg = new Segment();
                            foreach (var v in pl.Vertices) seg.Points.Add(xf.Apply(v.Location.X, v.Location.Y));
                            segments.Add(seg);
                            break;
                        }
                    case Line ln when connRx.IsMatch(ln.Layer?.Name ?? ""):
                        {
                            var seg = new Segment();
                            seg.Points.Add(xf.Apply(ln.StartPoint.X, ln.StartPoint.Y));
                            seg.Points.Add(xf.Apply(ln.EndPoint.X, ln.EndPoint.Y));
                            segments.Add(seg);
                            break;
                        }
                    case MultiLeader ml when ml.ContextData != null && !string.IsNullOrWhiteSpace(ml.ContextData.TextLabel):
                        {
                            var cd = ml.ContextData;
                            var at = xf.Apply(cd.TextLocation.X, cd.TextLocation.Y);
                            var note = new Note { Text = cd.TextLabel, X = at.X, Y = at.Y };
                            foreach (var root in cd.LeaderRoots)
                                foreach (var line in root.Lines)
                                    if (line.Points.Count > 0) note.Arrows.Add(xf.Apply(line.Points[0].X, line.Points[0].Y));
                            notes.Add(note);
                            break;
                        }
                    case MText mt:
                        {
                            var at = xf.Apply(mt.InsertPoint.X, mt.InsertPoint.Y);
                            notes.Add(new Note { Text = mt.PlainText, X = at.X, Y = at.Y });
                            break;
                        }
                    case TextEntity tx:
                        {
                            var at = xf.Apply(tx.InsertPoint.X, tx.InsertPoint.Y);
                            notes.Add(new Note { Text = tx.Value, X = at.X, Y = at.Y });
                            break;
                        }
                }
            }
        }

        /// <summary>"TX" + "1" -> "TX1"; "ERV" + "SA" -> "ERV-SA"; attributes missing -> the values that exist.</summary>
        private static string BubbleTag(Insert ins, DwgProfile profile)
        {
            var parts = profile.TagAttributes
                .Select(t => ins.Attributes.FirstOrDefault(a => string.Equals(a.Tag, t, StringComparison.OrdinalIgnoreCase))?.Value?.Trim())
                .Where(v => !string.IsNullOrEmpty(v)).ToList();
            if (parts.Count == 0) parts = ins.Attributes.Select(a => a.Value?.Trim()).Where(v => !string.IsNullOrEmpty(v)).ToList();
            if (parts.Count == 0) return null;
            string tag = parts[0];
            foreach (var p in parts.Skip(1)) tag += Regex.IsMatch(p, @"^\d") ? p : "-" + p;
            return tag.ToUpperInvariant();
        }

        // ------------------------------------------------------------------ one floor

        private static void BuildFloor(string floor, List<RiserSymbol> symbols, List<Bubble> bubbles, List<Segment> segments, List<Note> notes,
                                       DwgProfile profile, DwgRiserResult result)
        {
            symbols = symbols.ToList();

            // 1. Where each bubble's connector lines end. A connector may be drawn in pieces (followed end to end) and one
            //    bubble may have lines to several risers. A riser drawn as plain lines (no symbol block) is placed at the end.
            var anchors = new List<(Bubble B, double X, double Y, bool Line)>();
            foreach (var b in bubbles)
            {
                foreach (var (fx, fy) in ConnectorEnds(b, segments))
                {
                    anchors.Add((b, fx, fy, true));
                    if (!symbols.Any(o => Dist(o.X, o.Y, fx, fy) <= profile.PointTolerance))
                        symbols.Add(new RiserSymbol { X = fx, Y = fy, Block = "(end of the tag line)" });
                }
            }

            // 2. Group symbols side by side (single-link clustering).
            var groups = Group(floor, symbols, profile.GroupDistance);

            DwgRiser At(List<DwgRiser> gs, double x, double y, double tol) =>
                gs.Select(g => (G: g, D: g.Symbols.Min(o => Dist(o.X, o.Y, x, y)))).Where(t => t.D <= tol).OrderBy(t => t.D).Select(t => t.G).FirstOrDefault();

            // 3. Tags: by connector end; bubbles without a line go to the nearest riser.
            var tagAt = new List<(string Tag, double X, double Y, DwgRiser G, string How)>();
            foreach (var a in anchors)
            {
                var g = At(groups, a.X, a.Y, profile.PointTolerance);
                if (g != null) { tagAt.Add((a.B.Tag, a.X, a.Y, g, "bubble joined by a line")); a.B.Used = true; }
            }
            foreach (var b in bubbles.Where(b => !b.Used))
            {
                var g = At(groups, b.X, b.Y, profile.MaxTagDistance + b.Radius);
                if (g != null) { tagAt.Add((b.Tag, b.X, b.Y, g, "bubble next to the riser")); b.Used = true; }
                else result.LooseTags.Add(new LooseTag { Floor = floor, Tag = b.Tag, X = b.X, Y = b.Y });
            }

            // 4. A group reached by two different tags is two risers side by side: split it, each symbol to the nearest tag point.
            foreach (var g in groups.ToList())
            {
                var here = tagAt.Where(t => t.G == g).ToList();
                var distinct = here.Select(t => t.Tag).Distinct().ToList();
                if (distinct.Count < 2) continue;
                groups.Remove(g);
                foreach (var part in g.Symbols.GroupBy(s => here.OrderBy(t => Dist(t.X, t.Y, s.X, s.Y)).First().Tag))
                {
                    var ng = Group(floor, part.ToList(), double.MaxValue)[0];
                    groups.Add(ng);
                    for (int i = 0; i < tagAt.Count; i++)
                        if (tagAt[i].G == g && tagAt[i].Tag == part.Key) tagAt[i] = (tagAt[i].Tag, tagAt[i].X, tagAt[i].Y, ng, tagAt[i].How);
                }
            }
            foreach (var t in tagAt)
            {
                if (!t.G.Tags.Contains(t.Tag)) t.G.Tags.Add(t.Tag);
                t.G.Evidence.Add($"{t.Tag}: {t.How}");
            }

            // 5. Labels: a leader arrow pointing at a riser; else UP/DN text right next to it.
            foreach (var n in notes)
            {
                var label = RiserLabel.Parse(n.Text);
                if (label == null) continue;
                DwgRiser g = null;
                foreach (var a in n.Arrows) if ((g = At(groups, a.X, a.Y, profile.PointTolerance)) != null) break;
                string how = "leader";
                if (g == null && n.Arrows.Count == 0 && (label.GoesDown || label.GoesUp))
                {
                    g = At(groups, n.X, n.Y, profile.MaxTagDistance);
                    how = "text next to it";
                }
                if (g == null) continue;
                g.Labels.Add(label);
                g.Evidence.Add($"'{label.Text}': {how}");
            }

            result.Risers.AddRange(groups.OrderBy(g => g.Tag ?? "~").ThenBy(g => g.Y).ThenBy(g => g.X));
        }

        /// <summary>
        /// Far ends of every connector line leaving a bubble. Lines may continue in pieces (end to end) and branch off
        /// the middle of another line (one bubble tagging several risers); every loose end away from the bubble is a riser.
        /// </summary>
        private static List<(double X, double Y)> ConnectorEnds(Bubble b, List<Segment> segments)
        {
            const double join = 2;
            bool AtBubble((double X, double Y) q) => Dist(q.X, q.Y, b.X, b.Y) <= b.Radius + 4;
            bool OnLine((double X, double Y) q, Segment line)
            {
                for (int i = 1; i < line.Points.Count; i++)
                    if (ToSegment(q, line.Points[i - 1], line.Points[i]) <= join) return true;
                return false;
            }

            var tree = segments.Where(o => o.Points.Count >= 2 && AtBubble(o.Points[0]) != AtBubble(o.Points[o.Points.Count - 1])).ToList();
            if (tree.Count == 0) return new List<(double, double)>();
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var o in segments.Where(o => o.Points.Count >= 2 && !tree.Contains(o)).ToList())
                {
                    var a = o.Points[0]; var z = o.Points[o.Points.Count - 1];
                    if (tree.Any(t => OnLine(a, t) || OnLine(z, t) || OnLine(t.Points[0], o) || OnLine(t.Points[t.Points.Count - 1], o)))
                    { tree.Add(o); grew = true; }
                }
            }

            var ends = new List<(double, double)>();
            foreach (var o in tree)
                foreach (var q in new[] { o.Points[0], o.Points[o.Points.Count - 1] })
                    if (!AtBubble(q) && !tree.Any(t => t != o && OnLine(q, t)))
                        ends.Add(q);
            return ends;
        }

        private static double ToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len));
            return Dist(p.X, p.Y, a.X + t * dx, a.Y + t * dy);
        }

        private static List<DwgRiser> Group(string floor, List<RiserSymbol> symbols, double distance)
        {
            var groups = new List<DwgRiser>();
            foreach (var s in symbols)
            {
                var near = groups.Where(g => g.Symbols.Any(o => Dist(o.X, o.Y, s.X, s.Y) <= distance)).ToList();
                DwgRiser target;
                if (near.Count == 0) groups.Add(target = new DwgRiser { Floor = floor });
                else
                {
                    target = near[0];
                    foreach (var other in near.Skip(1)) { target.Symbols.AddRange(other.Symbols); groups.Remove(other); }
                }
                target.Symbols.Add(s);
            }
            foreach (var g in groups)
            {
                g.MinX = g.Symbols.Min(o => o.X); g.MaxX = g.Symbols.Max(o => o.X);
                g.MinY = g.Symbols.Min(o => o.Y); g.MaxY = g.Symbols.Max(o => o.Y);
                g.X = (g.MinX + g.MaxX) / 2; g.Y = (g.MinY + g.MaxY) / 2;
            }
            return groups;
        }

        private static double Dist(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    }
}
