using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A riser symbol: a block drawn at a duct's centre, or a drawn section mark (the duct outline itself).</summary>
    public class RiserSymbol
    {
        public double X, Y;
        public string Layer, Block;

        /// <summary>A section mark: rectangle or circle crossed by a diagonal. Its extent and size are the duct's.</summary>
        public bool Outline;
        public double MinX, MinY, MaxX, MaxY;
        public DuctSize Size;

        /// <summary>A centre block drawn inside a section mark: the same duct, not another one.</summary>
        public bool InOutline;

        /// <summary>Distance from a point to this symbol: to its outline (0 inside) or to its centre.</summary>
        public double DistanceTo(double x, double y)
        {
            if (!Outline) return Math.Sqrt((X - x) * (X - x) + (Y - y) * (Y - y));
            if (Size?.Diameter != null) return Math.Max(0, Math.Sqrt((X - x) * (X - x) + (Y - y) * (Y - y)) - Size.Diameter.Value / 2);
            double dx = Math.Max(0, Math.Max(MinX - x, x - MaxX)), dy = Math.Max(0, Math.Max(MinY - y, y - MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>One riser position on one floor: a group of riser symbols side by side, with its tag and size labels.</summary>
    public class DwgRiser
    {
        public string Floor;                                   // FloorKey
        public double X, Y;                                    // group centre (drawing units)
        public double MinX, MinY, MaxX, MaxY;                  // extent of the symbols (outlines included)
        public List<RiserSymbol> Symbols = new List<RiserSymbol>();
        public List<string> Tags = new List<string>();         // from bubbles: "TX1", "ERV-SA"
        public List<RiserLabel> Labels = new List<RiserLabel>();
        public List<string> Evidence = new List<string>();     // how tags/labels were attached, for the report

        public string Tag => Tags.FirstOrDefault();
        public RiserLabel Label => Labels.FirstOrDefault(l => l.Down != null || l.Up != null) ?? Labels.FirstOrDefault();

        /// <summary>Ducts in the group: a centre block inside a section mark is the same duct.</summary>
        public int Ducts => Symbols.Count(s => !s.InOutline);

        /// <summary>Size of the drawn section mark, when the riser has one.</summary>
        public DuctSize Drawn => Symbols.FirstOrDefault(s => s.Outline)?.Size;
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
    /// symbols (centre blocks, grouped when side by side, and drawn section marks), tag bubbles joined to a riser by a
    /// connector line (or, without one, by distance), labels from multileaders or classic leaders whose arrow points at a
    /// riser (or UP/DN text next to it), and, for a riser with neither, the tag or label of the riser it is joined to by
    /// duct lines. Block, attribute and layer names come from <see cref="DwgProfile"/>.
    /// </summary>
    public static class DwgRiserReader
    {
        private class Bubble { public string Tag; public double X, Y, Radius; public bool Used; }
        private class Segment { public List<(double X, double Y)> Points = new List<(double, double)>(); }
        private class Note { public string Text; public double X, Y; public bool FreeText; public List<(double X, double Y)> Arrows = new List<(double, double)>(); }
        private class LeaderLine { public Entity Annotation; public (double X, double Y) Arrow, Tail; }

        /// <summary>Everything collected from the drawing, in model coordinates.</summary>
        private class Found
        {
            public Regex Riser, Tag, Connector, Section, Duct;
            public List<RiserSymbol> Symbols = new List<RiserSymbol>();
            public List<Bubble> Bubbles = new List<Bubble>();
            public List<Segment> Connectors = new List<Segment>();
            public List<Note> Notes = new List<Note>();
            public List<LeaderLine> Leaders = new List<LeaderLine>();
            public Dictionary<Entity, List<Note>> TextNotes = new Dictionary<Entity, List<Note>>();
            public List<((double X, double Y)[] Corners, string Layer)> Rects = new List<((double, double)[], string)>();
            public List<(double X, double Y, double R, string Layer)> Circles = new List<(double, double, double, string)>();
            public List<Segment> Diagonals = new List<Segment>();     // open straight lines on section-mark layers
            public List<Segment> Ducts = new List<Segment>();         // open lines on duct layers
        }

        public static DwgRiserResult Read(string path, DwgSheetIndex sheets, DwgProfile profile) =>
            Read(DwgSheetIndex.Open(path), sheets, profile);

        public static DwgRiserResult Read(CadDocument doc, DwgSheetIndex sheets, DwgProfile profile)
        {
            var result = new DwgRiserResult();
            Regex Rx(string pattern) => string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase);
            var f = new Found
            {
                Riser = Rx(profile.RiserBlocks), Tag = Rx(profile.TagBlocks), Connector = Rx(profile.ConnectorLayers),
                Section = Rx(profile.SectionMarkLayers), Duct = Rx(profile.DuctLinkLayers)
            };
            Collect(doc.Entities, Xf.Identity, 0, profile, f);
            ResolveLeaders(f.Leaders, f.Notes, f.TextNotes, profile);
            SectionMarks(f, profile);

            foreach (var floor in sheets.Floors)
            {
                if (!floor.HasRegion)
                {
                    result.Warnings.Add($"{FloorKey.Describe(floor.Floor)}: plan region unknown, risers not read.");
                    continue;
                }
                bool In(double x, double y) => x >= floor.MinX && x <= floor.MaxX && y >= floor.MinY && y <= floor.MaxY;
                var fs = f.Symbols.Where(s => In(s.X, s.Y)).ToList();
                var fb = f.Bubbles.Where(b => In(b.X, b.Y)).Select(b => new Bubble { Tag = b.Tag, X = b.X, Y = b.Y, Radius = b.Radius }).ToList();
                var fseg = f.Connectors.Where(s => s.Points.Any(q => In(q.X, q.Y))).ToList();
                var fn = f.Notes.Where(n => In(n.X, n.Y)).ToList();
                var fd = f.Ducts.Where(s => s.Points.Any(q => In(q.X, q.Y))).ToList();
                BuildFloor(floor.Floor, fs, fb, fseg, fn, fd, profile, result);
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

        private static void Collect(IEnumerable<Entity> entities, Xf xf, int depth, DwgProfile profile, Found f)
        {
            void AddText(Entity e, string text, double x, double y)
            {
                var (tx, ty) = xf.Apply(x, y);
                var note = new Note { Text = text, X = tx, Y = ty, FreeText = true };
                f.Notes.Add(note);
                if (!f.TextNotes.TryGetValue(e, out var list)) f.TextNotes[e] = list = new List<Note>();
                list.Add(note);
            }
            bool On(Regex rx, Entity e) => rx != null && rx.IsMatch(e.Layer?.Name ?? "");

            // An open line: a riser connector, and/or a possible section-mark diagonal, and/or a duct run.
            void AddLine(Entity e, List<(double X, double Y)> points)
            {
                var seg = new Segment { Points = points };
                if (On(f.Connector, e)) { f.Connectors.Add(seg); return; }
                if (points.Count == 2 && On(f.Section, e)) f.Diagonals.Add(seg);
                if (On(f.Duct, e)) f.Ducts.Add(seg);
            }

            foreach (var e in entities)
            {
                switch (e)
                {
                    case Insert ins:
                        string name = ins.Block?.Name ?? "", source = ins.Block?.Source?.Name ?? name;
                        if (f.Riser != null && (f.Riser.IsMatch(name) || f.Riser.IsMatch(source)))
                        {
                            var (x, y) = xf.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                            f.Symbols.Add(new RiserSymbol { X = x, Y = y, Layer = ins.Layer?.Name, Block = source });
                        }
                        else if (f.Tag != null && (f.Tag.IsMatch(name) || f.Tag.IsMatch(source)))
                        {
                            var (x, y) = xf.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                            double r = ins.Block.Entities.OfType<Circle>().Select(c => c.Radius).DefaultIfEmpty(10).Max() * Math.Abs(ins.XScale * xf.Sx);
                            string tag = BubbleTag(ins, profile);
                            if (tag != null) f.Bubbles.Add(new Bubble { Tag = tag, X = x, Y = y, Radius = r });
                        }
                        else if (depth < 3 && ins.Block?.Entities != null && ins.Block.Entities.Count() > 200)
                        {
                            // A packed drawing (bound xref / per-floor block): look inside with its transform.
                            Collect(ins.Block.Entities, xf.Then(ins), depth + 1, profile, f);
                        }
                        break;

                    case LwPolyline pl when pl.Vertices.Count >= 2:
                        {
                            var pts = pl.Vertices.Select(v => xf.Apply(v.Location.X, v.Location.Y)).ToList();
                            if (On(f.Connector, pl)) { f.Connectors.Add(new Segment { Points = pts }); break; }
                            bool repeats = pts.Count > 2 && Dist(pts[0].X, pts[0].Y, pts[pts.Count - 1].X, pts[pts.Count - 1].Y) < 1e-6;
                            if (!pl.IsClosed && !repeats) AddLine(pl, pts);
                            else if (On(f.Section, pl) && (pts.Count == 4 || pts.Count == 5 && repeats))
                                f.Rects.Add((pts.Take(4).ToArray(), pl.Layer?.Name));
                            break;
                        }
                    case Line ln:
                        AddLine(ln, new List<(double, double)> { xf.Apply(ln.StartPoint.X, ln.StartPoint.Y), xf.Apply(ln.EndPoint.X, ln.EndPoint.Y) });
                        break;
                    case Circle c when !(c is Arc) && On(f.Section, c):
                        {
                            var (x, y) = xf.Apply(c.Center.X, c.Center.Y);
                            f.Circles.Add((x, y, c.Radius * Math.Abs(xf.Sx), c.Layer?.Name));
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
                            f.Notes.Add(note);
                            break;
                        }
                    case Leader ld when ld.Vertices.Count >= 2:
                        {
                            // Classic leader: the arrow is the first vertex; its note is separate text at the tail (resolved later).
                            var first = ld.Vertices[0]; var last = ld.Vertices[ld.Vertices.Count - 1];
                            f.Leaders.Add(new LeaderLine { Annotation = ld.AssociatedAnnotation, Arrow = xf.Apply(first.X, first.Y), Tail = xf.Apply(last.X, last.Y) });
                            break;
                        }
                    case MText mt:
                        AddText(mt, mt.PlainText, mt.InsertPoint.X, mt.InsertPoint.Y);
                        break;
                    case TextEntity tx:
                        AddText(tx, tx.Value, tx.InsertPoint.X, tx.InsertPoint.Y);
                        break;
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

        /// <summary>
        /// Turns each classic LEADER into a note pointing at its arrow. Its text is the annotation it is linked to, else the
        /// text nearest its tail (within <see cref="DwgProfile.LeaderTextDistance"/>). Text used by a leader is no longer
        /// a free-standing note, so it cannot also attach as "text next to" some other riser.
        /// </summary>
        private static void ResolveLeaders(List<LeaderLine> leaders, List<Note> notes, Dictionary<Entity, List<Note>> textNotes, DwgProfile profile)
        {
            var free = notes.Where(n => n.FreeText && !string.IsNullOrWhiteSpace(n.Text)).ToList();
            var claimed = new HashSet<Note>();
            foreach (var l in leaders)
            {
                double ToTail(Note n) => Dist(n.X, n.Y, l.Tail.X, l.Tail.Y);
                Note text = null;
                if (l.Annotation != null && textNotes.TryGetValue(l.Annotation, out var linked))
                    text = linked.OrderBy(ToTail).First();          // a block inserted twice holds the same entity twice
                if (text == null)
                    text = free.Select(n => (N: n, D: ToTail(n))).Where(t => t.D <= profile.LeaderTextDistance)
                               .OrderBy(t => t.D).Select(t => t.N).FirstOrDefault();
                if (text == null) continue;
                claimed.Add(text);
                var note = new Note { Text = text.Text, X = text.X, Y = text.Y };
                note.Arrows.Add(l.Arrow);
                notes.Add(note);
            }
            notes.RemoveAll(claimed.Contains);
        }

        /// <summary>
        /// Section marks: a rectangle crossed corner to corner, or a circle crossed through its centre, on a section-mark
        /// layer and within the size limits. Each becomes a riser symbol carrying its outline and drawn size.
        /// </summary>
        private static void SectionMarks(Found f, DwgProfile profile)
        {
            if (f.Section == null) return;
            bool SizeOk(double s) => s >= profile.SectionMarkMinSize && s <= profile.SectionMarkMaxSize;
            bool Near((double X, double Y) a, (double X, double Y) b, double tol) => Dist(a.X, a.Y, b.X, b.Y) <= tol;
            var marks = new List<RiserSymbol>();

            foreach (var (c, layer) in f.Rects)
            {
                double s1 = Dist(c[0].X, c[0].Y, c[1].X, c[1].Y), s2 = Dist(c[1].X, c[1].Y, c[2].X, c[2].Y);
                if (!SizeOk(s1) || !SizeOk(s2)) continue;
                double tol = Math.Max(0.5, 0.05 * Math.Min(s1, s2));
                bool crossed = f.Diagonals.Any(d =>
                {
                    var a = d.Points[0]; var b = d.Points[1];
                    return (Near(a, c[0], tol) && Near(b, c[2], tol)) || (Near(a, c[2], tol) && Near(b, c[0], tol))
                        || (Near(a, c[1], tol) && Near(b, c[3], tol)) || (Near(a, c[3], tol) && Near(b, c[1], tol));
                });
                if (!crossed) continue;
                // Width = the side running more along X, so a label "18X16" reads the same way as the drawing.
                bool firstAlongX = Math.Abs(c[1].X - c[0].X) >= Math.Abs(c[1].Y - c[0].Y);
                marks.Add(new RiserSymbol
                {
                    X = c.Average(p => p.X), Y = c.Average(p => p.Y), Layer = layer, Block = "(drawn section mark)", Outline = true,
                    MinX = c.Min(p => p.X), MaxX = c.Max(p => p.X), MinY = c.Min(p => p.Y), MaxY = c.Max(p => p.Y),
                    Size = new DuctSize { Width = Math.Round(firstAlongX ? s1 : s2, 1), Length = Math.Round(firstAlongX ? s2 : s1, 1) }
                });
            }

            foreach (var (x, y, r, layer) in f.Circles)
            {
                if (!SizeOk(2 * r)) continue;
                bool crossed = f.Diagonals.Any(d =>
                {
                    var a = d.Points[0]; var b = d.Points[1];
                    double len = Dist(a.X, a.Y, b.X, b.Y);
                    return len >= 1.6 * r && len <= 2.4 * r && ToSegment((x, y), a, b) <= 0.2 * r;
                });
                if (!crossed) continue;
                marks.Add(new RiserSymbol
                {
                    X = x, Y = y, Layer = layer, Block = "(drawn section mark)", Outline = true,
                    MinX = x - r, MaxX = x + r, MinY = y - r, MaxY = y + r, Size = new DuctSize { Diameter = Math.Round(2 * r, 1) }
                });
            }

            // The same mark drawn twice (overlapping copies) is one duct.
            foreach (var m in marks)
                if (!f.Symbols.Any(o => o.Outline && Dist(o.X, o.Y, m.X, m.Y) < 0.5 && o.Size.ToString() == m.Size.ToString()))
                    f.Symbols.Add(m);
        }

        // ------------------------------------------------------------------ one floor

        private static void BuildFloor(string floor, List<RiserSymbol> symbols, List<Bubble> bubbles, List<Segment> segments, List<Note> notes,
                                       List<Segment> ducts, DwgProfile profile, DwgRiserResult result)
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
                    if (!symbols.Any(o => o.DistanceTo(fx, fy) <= profile.PointTolerance))
                        symbols.Add(new RiserSymbol { X = fx, Y = fy, Block = "(end of the tag line)" });
                }
            }

            // 2. Group symbols: each section mark is one duct with the centre blocks inside it; other blocks side by side cluster.
            var groups = Group(floor, symbols, profile);

            // Nearest riser to a point, measured to outlines (0 inside) or centres.
            DwgRiser At(List<DwgRiser> gs, double x, double y, double tol) =>
                gs.Select(g => (G: g, D: g.Symbols.Min(o => o.DistanceTo(x, y)))).Where(t => t.D <= tol).OrderBy(t => t.D).Select(t => t.G).FirstOrDefault();

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
                foreach (var part in g.Symbols.GroupBy(s => here.OrderBy(t => s.DistanceTo(t.X, t.Y)).First().Tag))
                {
                    var ng = Make(floor, part);
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

            // 6. A riser that does not say what it is (no tag; no label, or only a size), joined by duct lines to one that
            //    does (the same riser offset on this floor, e.g. dryer ducts running from where they come up to the shaft),
            //    takes that riser's tag or descriptive label. Its own sizes stay.
            LinkByDucts(groups, ducts, profile);

            foreach (var g in groups.Where(g => g.Drawn != null))
                g.Evidence.Add($"drawn section {g.Drawn}");

            result.Risers.AddRange(groups.OrderBy(g => g.Tag ?? "~").ThenBy(g => g.Y).ThenBy(g => g.X));
        }

        private static void LinkByDucts(List<DwgRiser> groups, List<Segment> ducts, DwgProfile profile)
        {
            if (ducts.Count == 0) return;
            var known = groups.Where(g => g.Tags.Count > 0 || g.Labels.Any(l => l.Descriptive)).ToList();   // only first-hand tags/labels are passed on
            DwgRiser AtEnd((double X, double Y) q) =>
                groups.Select(g => (G: g, D: g.Symbols.Min(o => o.DistanceTo(q.X, q.Y)))).Where(t => t.D <= profile.DuctLinkTolerance)
                      .OrderBy(t => t.D).Select(t => t.G).FirstOrDefault();

            // Only risers that do not say what they are: no tag, and labels (if any) that are just sizes / UP-DN.
            foreach (var g in groups.Where(g => g.Tags.Count == 0 && !g.Labels.Any(l => l.Descriptive)).ToList())
            {
                var links = new Dictionary<DwgRiser, int>();
                foreach (var d in ducts)
                {
                    DwgRiser a = AtEnd(d.Points[0]), b = AtEnd(d.Points[d.Points.Count - 1]);
                    var other = a == g ? b : b == g ? a : null;
                    if (other == null || other == g || !known.Contains(other)) continue;
                    links[other] = links.TryGetValue(other, out int n) ? n + 1 : 1;
                }
                if (links.Count == 0) continue;
                var (to, count) = links.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).First();
                string via = $"joined by {count} duct line(s) to the riser at {to.X:0}, {to.Y:0}";
                foreach (var t in to.Tags) { g.Tags.Add(t); g.Evidence.Add($"{t}: {via}"); }
                if (to.Tags.Count == 0)
                    foreach (var l in to.Labels.Where(l => l.Descriptive))
                    {
                        // The words say what the riser is; the sizes belong to the other position, so they are not copied.
                        g.Labels.Add(new RiserLabel { Text = l.Text, GoesDown = l.GoesDown, GoesUp = l.GoesUp, Nominal = l.Nominal });
                        g.Evidence.Add($"'{l.Text}': {via}");
                    }
            }
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

        /// <summary>
        /// Each section mark is its own riser, with the centre blocks drawn inside it (same duct). The remaining centre
        /// blocks are grouped side by side (single-link clustering); they never join a section mark by distance alone.
        /// </summary>
        private static List<DwgRiser> Group(string floor, List<RiserSymbol> symbols, DwgProfile profile)
        {
            var outlines = symbols.Where(s => s.Outline).Select(o => new List<RiserSymbol> { o }).ToList();
            var clusters = new List<List<RiserSymbol>>();
            foreach (var s in symbols.Where(s => !s.Outline))
            {
                var host = outlines.Select(o => (O: o, D: o[0].DistanceTo(s.X, s.Y))).Where(t => t.D <= profile.OutlineTolerance)
                                   .OrderBy(t => t.D).Select(t => t.O).FirstOrDefault();
                if (host != null) { s.InOutline = true; host.Add(s); continue; }

                var near = clusters.Where(c => c.Any(o => Dist(o.X, o.Y, s.X, s.Y) <= profile.GroupDistance)).ToList();
                List<RiserSymbol> target;
                if (near.Count == 0) clusters.Add(target = new List<RiserSymbol>());
                else
                {
                    target = near[0];
                    foreach (var other in near.Skip(1)) { target.AddRange(other); clusters.Remove(other); }
                }
                target.Add(s);
            }
            return outlines.Concat(clusters).Select(c => Make(floor, c)).ToList();
        }

        private static DwgRiser Make(string floor, IEnumerable<RiserSymbol> symbols)
        {
            var g = new DwgRiser { Floor = floor };
            g.Symbols.AddRange(symbols);
            g.MinX = g.Symbols.Min(o => o.Outline ? o.MinX : o.X); g.MaxX = g.Symbols.Max(o => o.Outline ? o.MaxX : o.X);
            g.MinY = g.Symbols.Min(o => o.Outline ? o.MinY : o.Y); g.MaxY = g.Symbols.Max(o => o.Outline ? o.MaxY : o.Y);
            g.X = (g.MinX + g.MaxX) / 2; g.Y = (g.MinY + g.MaxY) / 2;
            return g;
        }

        private static double Dist(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
    }
}
