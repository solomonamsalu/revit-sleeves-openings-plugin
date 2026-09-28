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

        /// <summary>
        /// A dynamic riser block holds one duct shape per size (8X8, 12X8, 16X6...), all in the block whatever the visible
        /// state: each closed rectangle as placed in the drawing, its side along X and along Y (drawing units).
        /// </summary>
        public List<(double AlongX, double AlongY)> States = new List<(double, double)>();

        /// <summary>Radius (drawing units) when the symbol is a riser circle (plumbing profile); 0 otherwise.</summary>
        public double Radius;

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
        /// <summary>For a riser with no tag and no label: the notes written next to it, nearest first ("COMBUSTION AIR AND TYPE B FUEL VENT UP IN SHAFT").</summary>
        public List<string> Notes = new List<string>();
        /// <summary>
        /// For a riser with no tag bubble: a tag written as plain text next to it ("GX-2 2000 CFM"), nearest first, with its
        /// distance (drawing units). Whether it names the riser is decided by the legend (only tags that get an opening).
        /// </summary>
        public List<(string Tag, double Distance)> TextTags = new List<(string, double)>();

        /// <summary>Text written next to the riser's tag bubble (plumbing: the service list "S UP &amp; DN / V RISE &amp; DN"), one entry per text.</summary>
        public List<string> TagTexts = new List<string>();

        public string Tag => Tags.FirstOrDefault();
        public RiserLabel Label => Labels.FirstOrDefault(l => l.Down != null || l.Up != null) ?? Labels.FirstOrDefault();
        public bool Dryer => Labels.Any(l => l.Dryer);

        /// <summary>Ducts in the group: a centre block inside a section mark is the same duct.</summary>
        public int Ducts => Symbols.Count(s => !s.InOutline);

        /// <summary>Size of the drawn section mark, when the riser has one.</summary>
        public DuctSize Drawn => Symbols.FirstOrDefault(s => s.Outline)?.Size;

        /// <summary>
        /// The shape a dynamic riser block draws for <paramref name="size"/> (Width = the side along X), or null when no
        /// block of this riser has a shape of that size.
        /// </summary>
        public DuctSize StateFor(DuctSize size)
        {
            if (size?.Width == null || size.Length == null) return null;
            bool Is(double a, double b) => Math.Abs(a - b) <= 0.6;
            var st = Symbols.SelectMany(s => s.States).FirstOrDefault(s => (Is(s.AlongX, size.Width.Value) && Is(s.AlongY, size.Length.Value)) ||
                                                                           (Is(s.AlongX, size.Length.Value) && Is(s.AlongY, size.Width.Value)));
            return st == default ? null : new DuctSize { Width = st.AlongX, Length = st.AlongY };
        }
    }

    /// <summary>A tag bubble that could not be tied to any riser symbol.</summary>
    public class LooseTag
    {
        public string Floor, Tag;
        public double X, Y;
    }

    /// <summary>A fixture named on the plan ("WC", "LAV"), at the text's position.</summary>
    public class FixtureMark
    {
        public string Floor, Code;
        public double X, Y;
    }

    public class DwgRiserResult
    {
        public List<DwgRiser> Risers = new List<DwgRiser>();
        public List<LooseTag> LooseTags = new List<LooseTag>();
        /// <summary>Fixtures named on the plans (profile fixtureText), per floor.</summary>
        public List<FixtureMark> Fixtures = new List<FixtureMark>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Reads the risers of every floor plan in a DWG (regions from <see cref="DwgSheetIndex"/>). Per floor: riser
    /// symbols (centre blocks grouped when side by side, and drawn section marks, each its own riser), tag bubbles joined to a riser by a connector line (or, without one, by
    /// distance), and size / UP-DN labels from multileaders whose arrow points at a riser (or plain text next to it).
    /// Block, attribute and layer names come from <see cref="DwgProfile"/>.
    /// </summary>
    public static class DwgRiserReader
    {
        /// <summary>Block name given to a riser placed at the end of a tag line (no symbol drawn there).</summary>
        public const string LineEnd = "(end of the tag line)";
        /// <summary>Block name given to a riser placed where an UP/DN label's arrow points (no symbol drawn there).</summary>
        public const string ArrowTip = "(UP/DN label arrow)";

        /// <summary>A riser position with no symbol drawn (placed from a tag line or an UP/DN arrow), or a centre block inside a section mark (the same duct).</summary>
        public static bool Drawn(RiserSymbol s) => s.Block != LineEnd && s.Block != ArrowTip && !s.InOutline;

        /// <summary>Block name given to a riser read from a drawn section mark.</summary>
        public const string SectionMark = "(drawn section mark)";

        /// <summary>Block name given to a riser drawn as a plain circle on a pipe layer (plumbing profile).</summary>
        public const string PipeCircle = "(pipe circle)";

        private class Bubble { public string Tag; public double X, Y, Radius; public bool Used; }
        private class Segment { public List<(double X, double Y)> Points = new List<(double, double)>(); }
        private class Note { public string Text, Raw; public double X, Y; public bool FreeText; public List<(double X, double Y)> Arrows = new List<(double, double)>(); }
        private class LeaderLine { public Entity Annotation; public (double X, double Y) Arrow, Tail; }

        /// <summary>Everything collected from the drawing, in model coordinates.</summary>
        private class Found
        {
            public Regex Riser, Tag, Connector, Section, PipeLayers, Fixture;
            public bool LeaderConnectors;
            public double MinRadius, MaxRadius;
            public List<FixtureMark> Fixtures = new List<FixtureMark>();
            public List<RiserSymbol> Symbols = new List<RiserSymbol>();
            public List<Bubble> Bubbles = new List<Bubble>();
            public List<Segment> Connectors = new List<Segment>();
            public List<Note> Notes = new List<Note>();
            public List<LeaderLine> Leaders = new List<LeaderLine>();
            public Dictionary<Entity, List<Note>> TextNotes = new Dictionary<Entity, List<Note>>();   // text entity -> its notes (a block inserted twice holds it twice)
            public List<((double X, double Y)[] Corners, string Layer)> Rects = new List<((double, double)[], string)>();
            public List<(double X, double Y, double R, string Layer)> Circles = new List<(double, double, double, string)>();
            public List<Segment> Diagonals = new List<Segment>();     // straight lines on duct layers
        }

        private static Found Start(DwgProfile profile)
        {
            Regex Rx(string pattern) => string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase);
            return new Found
            {
                Riser = Rx(profile.RiserBlocks), Tag = Rx(profile.TagBlocks), Connector = Rx(profile.ConnectorLayers), Section = Rx(profile.DuctLayers),
                PipeLayers = Rx(profile.RiserCircleLayers), Fixture = Rx(profile.FixtureText), LeaderConnectors = profile.LeadersAsConnectors,
                MinRadius = profile.RiserCircleMinRadius, MaxRadius = profile.RiserCircleMaxRadius
            };
        }

        /// <summary>
        /// MText as one plain string per line: formatting codes (\W0.7; \L \H..; braces) removed, paragraphs (\P and line
        /// breaks) kept as '\n'. ACadSharp's PlainText leaves the letters of some codes ("\LWC" -> "LWC").
        /// </summary>
        public static string MTextLines(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Replace("\\P", "\n").Replace("\r", "\n");
            s = Regex.Replace(s, @"\\[ACcFfHhQqTtWwp][^;\\{}]*;", "");       // codes with a value: \W0.7; \H2.5; \fArial|b0;
            s = Regex.Replace(s, @"\\[LlOoKkNn]", "");                          // on/off codes: underline, overline, strike
            s = Regex.Replace(s, @"\\S([^;]*)\^([^;]*);", "$1/$2");               // stacked fractions \S1^2;
            s = s.Replace("{", "").Replace("}", "").Replace("\\~", " ").Replace("%%C", "Ø").Replace("%%c", "Ø");
            return string.Join("\n", s.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 0));
        }

        public static DwgRiserResult Read(string path, DwgSheetIndex sheets, DwgProfile profile) =>
            Read(DwgSheetIndex.Open(path), sheets, profile);

        public static DwgRiserResult Read(CadDocument doc, DwgSheetIndex sheets, DwgProfile profile)
        {
            var result = new DwgRiserResult();
            var f = Start(profile);
            Collect(doc.Entities, DwgXform.Identity, 0, profile, f);
            ResolveLeaders(f, profile);
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
                BuildFloor(floor.Floor, fs, fb, fseg, fn, profile, result);
                result.Fixtures.AddRange(f.Fixtures.Where(x => In(x.X, x.Y)).Select(x => new FixtureMark { Floor = floor.Floor, Code = x.Code, X = x.X, Y = x.Y }));
            }
            return result;
        }

        /// <summary>Every riser symbol block in the drawing, with no floor split (a per-floor drawing used to check alignment).</summary>
        public static List<RiserSymbol> Symbols(CadDocument doc, DwgProfile profile)
        {
            var f = Start(profile);
            f.Section = null;                                        // alignment compares centre blocks only
            Collect(doc.Entities, DwgXform.Identity, 0, profile, f);
            return f.Symbols;
        }

        /// <summary>
        /// One floor from shapes read off a vector PDF page (<see cref="PdfPlanReader"/>), grouped exactly as a DWG floor:
        /// symbols side by side, bubbles joined by their connector lines, the service text at each bubble.
        /// </summary>
        public static void ReadFloor(string floor, List<RiserSymbol> symbols, IEnumerable<(string Tag, double X, double Y, double Radius)> bubbles,
                                     IEnumerable<List<(double X, double Y)>> connectors, IEnumerable<(string Text, double X, double Y)> texts,
                                     DwgProfile profile, DwgRiserResult result)
        {
            BuildFloor(floor, symbols,
                       bubbles.Select(b => new Bubble { Tag = b.Tag, X = b.X, Y = b.Y, Radius = b.Radius }).ToList(),
                       connectors.Where(c => c.Count >= 2).Select(c => new Segment { Points = c.ToList() }).ToList(),
                       texts.Select(t => new Note { Text = t.Text, Raw = t.Text, X = t.X, Y = t.Y, FreeText = true }).ToList(),
                       profile, result);
        }

        // ------------------------------------------------------------------ collection (with block transforms)

        private static void Collect(IEnumerable<Entity> entities, DwgXform xf, int depth, DwgProfile profile, Found f)
        {
            bool On(Regex rx, Entity e) => rx != null && rx.IsMatch(e.Layer?.Name ?? "");
            void AddText(Entity e, string text, double x, double y, string raw = null)
            {
                var (tx, ty) = xf.Apply(x, y);
                // a fixture named at its spot ("WC", "LAV"): kept apart, it is not a note about a riser
                string plain = raw != null ? MTextLines(raw) : (text ?? "").Trim();
                if (f.Fixture != null && !plain.Contains("\n") && f.Fixture.IsMatch(plain.ToUpperInvariant()))
                {
                    f.Fixtures.Add(new FixtureMark { Code = plain.ToUpperInvariant(), X = tx, Y = ty });
                    return;
                }
                var note = new Note { Text = text, Raw = plain, X = tx, Y = ty, FreeText = true };
                f.Notes.Add(note);
                if (!f.TextNotes.TryGetValue(e, out var list)) f.TextNotes[e] = list = new List<Note>();
                list.Add(note);
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
                            var sym = new RiserSymbol { X = x, Y = y, Layer = ins.Layer?.Name, Block = source };
                            if (ins.Block?.Entities != null)
                            {
                                var bx = xf.Then(ins);
                                foreach (var pl in ins.Block.Entities.OfType<LwPolyline>().Where(p => p.IsClosed && p.Vertices.Count == 4))
                                {
                                    var c = pl.Vertices.Select(v => bx.Apply(v.Location.X, v.Location.Y)).ToArray();
                                    double s1 = Dist(c[0].X, c[0].Y, c[1].X, c[1].Y), s2 = Dist(c[1].X, c[1].Y, c[2].X, c[2].Y);
                                    bool firstAlongX = Math.Abs(c[1].X - c[0].X) >= Math.Abs(c[1].Y - c[0].Y);
                                    sym.States.Add((Math.Round(firstAlongX ? s1 : s2, 1), Math.Round(firstAlongX ? s2 : s1, 1)));
                                }
                            }
                            f.Symbols.Add(sym);
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
                            if (!On(f.Section, pl)) break;
                            bool repeats = pts.Count > 2 && Dist(pts[0].X, pts[0].Y, pts[pts.Count - 1].X, pts[pts.Count - 1].Y) < 1e-6;
                            if (pts.Count == 2) f.Diagonals.Add(new Segment { Points = pts });
                            else if ((pl.IsClosed && pts.Count == 4) || (repeats && pts.Count == 5)) f.Rects.Add((pts.Take(4).ToArray(), pl.Layer?.Name));
                            break;
                        }
                    case Line ln:
                        {
                            var seg = new Segment();
                            seg.Points.Add(xf.Apply(ln.StartPoint.X, ln.StartPoint.Y));
                            seg.Points.Add(xf.Apply(ln.EndPoint.X, ln.EndPoint.Y));
                            if (On(f.Connector, ln)) f.Connectors.Add(seg);
                            else if (On(f.Section, ln)) f.Diagonals.Add(seg);
                            break;
                        }
                    case Circle c when (!(c is Arc) || Sweep((Arc)c) >= 1.5 * Math.PI) && On(f.PipeLayers, c)
                                       && c.Radius * Math.Abs(xf.Sx) >= f.MinRadius && c.Radius * Math.Abs(xf.Sx) <= f.MaxRadius:
                        {
                            // plumbing: every pipe through the slab is a small circle on its system's layer (a circle trimmed
                            // where a leader crosses it is an arc: nearly full ones count)
                            var (x, y) = xf.Apply(c.Center.X, c.Center.Y);
                            f.Symbols.Add(new RiserSymbol { X = x, Y = y, Layer = c.Layer?.Name, Block = PipeCircle, Radius = c.Radius * Math.Abs(xf.Sx) });
                            break;
                        }
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
                    case Leader ld when f.LeaderConnectors && ld.Vertices.Count >= 2 && On(f.Connector, ld):
                        // plumbing: the leader from a riser group to its tag bubble (arrow at the riser, tail at the bubble)
                        f.Connectors.Add(new Segment { Points = ld.Vertices.Select(v => xf.Apply(v.X, v.Y)).ToList() });
                        break;
                    case Leader ld when ld.Vertices.Count >= 2:
                        {
                            // Classic leader: the arrow is the first vertex; its note is separate text at the tail (paired in ResolveLeaders).
                            var first = ld.Vertices[0]; var last = ld.Vertices[ld.Vertices.Count - 1];
                            f.Leaders.Add(new LeaderLine { Annotation = ld.AssociatedAnnotation, Arrow = xf.Apply(first.X, first.Y), Tail = xf.Apply(last.X, last.Y) });
                            break;
                        }
                    case MText mt:
                        AddText(mt, mt.PlainText, mt.InsertPoint.X, mt.InsertPoint.Y, mt.Value);
                        break;
                    case TextEntity tx:
                        AddText(tx, tx.Value, tx.InsertPoint.X, tx.InsertPoint.Y);
                        break;
                }
            }
        }

        /// <summary>
        /// Turns each classic LEADER into a note pointing at its arrow. Its text is the annotation it is linked to, else the
        /// text nearest its tail (within <see cref="DwgProfile.LeaderTextDistance"/>). Text used by a leader is no longer
        /// free-standing, so it cannot also attach as "text next to" some other riser.
        /// </summary>
        private static void ResolveLeaders(Found f, DwgProfile profile)
        {
            var free = f.Notes.Where(n => n.FreeText && !string.IsNullOrWhiteSpace(n.Text)).ToList();
            var claimed = new HashSet<Note>();
            foreach (var l in f.Leaders)
            {
                double ToTail(Note n) => Dist(n.X, n.Y, l.Tail.X, l.Tail.Y);
                Note text = null;
                if (l.Annotation != null && f.TextNotes.TryGetValue(l.Annotation, out var linked))
                    text = linked.OrderBy(ToTail).First();
                if (text == null)
                    text = free.Select(n => (N: n, D: ToTail(n))).Where(t => t.D <= profile.LeaderTextDistance).OrderBy(t => t.D).Select(t => t.N).FirstOrDefault();
                if (text == null) continue;
                claimed.Add(text);
                var note = new Note { Text = text.Text, X = text.X, Y = text.Y };
                note.Arrows.Add(l.Arrow);
                f.Notes.Add(note);
            }
            f.Notes.RemoveAll(claimed.Contains);
        }

        /// <summary>Text that starts with a tag: "GX-2 2000 CFM", "MD-3".</summary>
        private static readonly Regex TextTag = new Regex(@"^([A-Z]{1,4}-\d{1,2}[A-Z]?)\b");

        /// <summary>One line of note text.</summary>
        private static string Flat(string text) => Regex.Replace((text ?? "").Replace("\\P", " "), @"\s+", " ").Trim();

        /// <summary>
        /// Section marks: a rectangle crossed corner to corner, or a circle crossed through its centre, on a duct layer and
        /// riser-sized. Each becomes a riser symbol carrying its outline and drawn size, so a duct drawn this way next to a
        /// shaft of centre blocks (TX1 beside the dryer ducts) is its own riser instead of joining the shaft.
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
                    X = c.Average(p => p.X), Y = c.Average(p => p.Y), Layer = layer, Block = SectionMark, Outline = true,
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
                    X = x, Y = y, Layer = layer, Block = SectionMark, Outline = true,
                    MinX = x - r, MaxX = x + r, MinY = y - r, MaxY = y + r, Size = new DuctSize { Diameter = Math.Round(2 * r, 1) }
                });
            }

            // The same mark drawn twice (overlapping copies) is one duct.
            foreach (var m in marks)
                if (!f.Symbols.Any(o => o.Outline && Dist(o.X, o.Y, m.X, m.Y) < 0.5 && o.Size.ToString() == m.Size.ToString()))
                    f.Symbols.Add(m);
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
                {                    anchors.Add((b, fx, fy, true));
                    if (!symbols.Any(o => o.DistanceTo(fx, fy) <= profile.PointTolerance))
                        symbols.Add(new RiserSymbol { X = fx, Y = fy, Block = LineEnd });
                }
            }

            // 1b. An UP/DN label's arrow marks a riser even where no symbol is drawn (a duct moved into a shaft).
            foreach (var n in notes.Where(n => n.Arrows.Count > 0))
            {
                var label = RiserLabel.Parse(n.Text);
                if (label == null || !label.IsCrossingLabel || !(label.GoesDown || label.GoesUp) || label.Dryer) continue;
                foreach (var a in n.Arrows.Take(1))
                    if (!symbols.Any(o => o.DistanceTo(a.X, a.Y) <= profile.PointTolerance))
                        symbols.Add(new RiserSymbol { X = a.X, Y = a.Y, Block = ArrowTip });
            }

            // 2. Group symbols: each section mark is one duct with the centre blocks inside it; other symbols side by side cluster.
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

            // 4b. Plumbing: the service list is written next to the bubble ("HW DROP / CW UP & DN / S UP & DN"), not next to
            //     the pipes. Each such text goes to the riser of the nearest used bubble and is no longer a free note.
            if (profile.TagTextDistance > 0)
            {
                var used = bubbles.Where(b => b.Used).ToList();
                foreach (var n in notes.Where(n => n.FreeText && n.Arrows.Count == 0 && !string.IsNullOrWhiteSpace(n.Raw)).ToList())
                {
                    var b = used.Select(x => (B: x, D: Dist(x.X, x.Y, n.X, n.Y))).Where(x => x.D <= profile.TagTextDistance + x.B.Radius)
                                .OrderBy(x => x.D).Select(x => x.B).FirstOrDefault();
                    if (b == null) continue;
                    // the group this bubble's own line reaches (two bubbles with one tag may point at two groups: UP list, DN list)
                    var ends = anchors.Where(x => x.B == b).Select(x => (x.X, x.Y)).Concat(new[] { (b.X, b.Y) }).ToList();
                    var g = tagAt.Where(t => t.Tag == b.Tag && ends.Any(e => Math.Abs(e.Item1 - t.X) < 1e-6 && Math.Abs(e.Item2 - t.Y) < 1e-6))
                                 .Select(t => t.G).FirstOrDefault();
                    if (g == null) continue;
                    g.TagTexts.Add(n.Raw);
                    g.Evidence.Add($"'{n.Raw.Replace("\n", " / ")}': text at the {b.Tag} bubble");
                    notes.Remove(n);
                }
            }

            // 5. Labels: a leader arrow pointing at a riser; else UP/DN text right next to it. A description pointing at a
            //    riser ("COMBUSTION AIR … UP IN SHAFT", "8"Ø GAS METER VENT") is not a crossing: it says what the riser is.
            var pointed = new Dictionary<DwgRiser, List<string>>();
            foreach (var n in notes)
            {
                var label = RiserLabel.Parse(n.Text);
                bool crossing = label != null && label.IsCrossingLabel;
                if (!crossing && n.Arrows.Count == 0) continue;           // free text: only UP/DN labels attach by distance
                DwgRiser g = null;
                foreach (var a in n.Arrows) if ((g = At(groups, a.X, a.Y, profile.PointTolerance)) != null) break;
                string how = "leader";
                if (g == null && n.Arrows.Count == 0 && (label.GoesDown || label.GoesUp))
                {
                    g = At(groups, n.X, n.Y, profile.MaxTagDistance);
                    how = "text next to it";
                }
                if (g == null) continue;
                if (!crossing)
                {
                    string text = Flat(n.Text);
                    if (!pointed.TryGetValue(g, out var list)) pointed[g] = list = new List<string>();
                    if (!list.Contains(text)) list.Add(text);
                    g.Evidence.Add($"note '{text}': {how}");
                    continue;
                }
                // the symbol it points at (a shaft holds several ducts), else the group centre
                var tip = n.Arrows.Count > 0 ? n.Arrows.OrderBy(a => g.Symbols.Min(o => o.DistanceTo(a.X, a.Y))).First() : (X: g.X, Y: g.Y);
                var at = g.Symbols.OrderBy(o => o.DistanceTo(tip.X, tip.Y)).First();
                label.X = g.Symbols.Count == 1 || n.Arrows.Count > 0 ? at.X : g.X;
                label.Y = g.Symbols.Count == 1 || n.Arrows.Count > 0 ? at.Y : g.Y;
                label.TextX = n.X; label.TextY = n.Y;
                g.Labels.Add(label);
                g.Evidence.Add($"'{label.Text}': {how}");
            }

            // 5b. Dryer ducts drawn next to another duct (TX1 beside two dryer ducts): a group with both a dryer label and a
            //     size label is two risers. Each size label takes the symbol its arrow points at, with the tags; the other
            //     symbols are the dryer ducts.
            foreach (var g in groups.ToList())
            {
                var sized = g.Labels.Where(l => !l.Dryer).ToList();
                if (g.Symbols.Count < 2 || sized.Count == 0 || !g.Labels.Any(l => l.Dryer)) continue;
                var taken = sized.Select(l => g.Symbols.OrderBy(o => o.DistanceTo(l.X, l.Y)).First()).Distinct().ToList();
                if (taken.Count >= g.Symbols.Count) continue;
                var duct = Make(floor, taken);
                duct.Tags.AddRange(g.Tags);
                duct.Labels.AddRange(sized);
                duct.Evidence.AddRange(g.Evidence.Where(e => g.Tags.Any(t => e.StartsWith(t + ":")) || sized.Any(l => e.StartsWith($"'{l.Text}'"))));
                var dryer = Make(floor, g.Symbols.Except(taken));
                dryer.Labels.AddRange(g.Labels.Where(l => l.Dryer));
                dryer.Evidence.AddRange(g.Evidence.Except(duct.Evidence));
                dryer.Evidence.Add($"split from {string.Join(" ", g.Tags.DefaultIfEmpty("the duct"))} drawn beside it ({string.Join(", ", sized.Select(l => l.Text))})");
                if (pointed.TryGetValue(g, out var gn)) { pointed.Remove(g); pointed[dryer] = gn; }
                groups.Remove(g); groups.Add(duct); groups.Add(dryer);
            }

            // 6. Notes: those whose leader points at the riser come first; a riser with neither tag nor label also keeps
            //    the notes written next to it, so the report can say what it is.
            foreach (var g in groups)
            {
                g.Notes = pointed.TryGetValue(g, out var own) ? own.ToList() : new List<string>();
                if (g.Tags.Count > 0) continue;
                g.TextTags = notes.Where(n => n.Arrows.Count == 0).Select(n => (M: TextTag.Match(Flat(n.Text).ToUpperInvariant()), N: n))
                                  .Where(t => t.M.Success).Select(t => (Tag: t.M.Groups[1].Value, D: g.Symbols.Min(o => o.DistanceTo(t.N.X, t.N.Y))))
                                  .Where(t => t.D <= profile.MaxNoteDistance).OrderBy(t => t.D).ToList();
                if (g.Labels.Count > 0) continue;
                g.Notes.AddRange(notes.Where(n => n.Arrows.Count == 0 && n.Text.Trim().Length >= 10)
                                      .Select(n => (N: n, D: g.Symbols.Min(o => o.DistanceTo(n.X, n.Y))))
                                      .Where(t => t.D <= profile.MaxNoteDistance).OrderBy(t => t.D)
                                      .Select(t => Flat(t.N.Text)).Where(t => !g.Notes.Contains(t)).Distinct());
            }

            foreach (var g in groups.Where(g => g.Drawn != null))
                g.Evidence.Add($"drawn section {g.Drawn}");

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

        /// <summary>Angle an arc covers (radians, 0..2π).</summary>
        private static double Sweep(Arc a)
        {
            double s = a.EndAngle - a.StartAngle;
            while (s <= 0) s += 2 * Math.PI;
            while (s > 2 * Math.PI) s -= 2 * Math.PI;
            return s;
        }

        private static double ToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len));
            return Dist(p.X, p.Y, a.X + t * dx, a.Y + t * dy);
        }

        /// <summary>
        /// Each section mark is its own riser, with the symbols drawn inside it (same duct). The remaining symbols are
        /// grouped side by side (single-link clustering); they never join a section mark by distance alone.
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
