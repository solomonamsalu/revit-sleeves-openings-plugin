using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Assembly
{
    /// <summary>
    /// Plumbing counterpart of <see cref="RiserAssembler"/>: from the pipe groups on each floor plan to the sleeves to
    /// cut, slab by slab. A group (P3) is a row of circles, one per pipe, each on its system's layer; the text at its
    /// bubble lists the services with where they go ("S UP &amp; DN" = through this floor's slab and the one above,
    /// "V RISE &amp; DN", "ST DN", "V THRU ROOF"). Each sleeved service becomes a crossing named "S-P3" at its own circle;
    /// the floor above says the same thing from its side, so crossings are checked from both floors. The pipes of one
    /// group are drawn 5" apart, closer than their sleeves: the sleeves are spread along the row. Fixtures named on the
    /// plan (WC, LAV, BT...) are listed for review: their text sits next to the fixture, not on its drain.
    /// Free of the Revit API.
    /// </summary>
    public static class PipeAssembler
    {
        public const string NotSleeved = "not sleeved";
        private const string UnmarkedDir = "no UP/DN written: ends on this floor (rules.json unmarked)";

        /// <summary>One service line of a group's text: "S UP &amp; DN".</summary>
        public class ServiceLine
        {
            public string Service, Text;
            public bool Up, Down, Through;
            /// <summary>Pipe size written in the label ('3" SPRINKLER RISER UP/DN'), inches; null when none.</summary>
            public double? Pipe;
            /// <summary>No UP, DN or THRU written; read as this floor's slab (rules.json unmarked = "DN").</summary>
            public bool Unmarked;
        }

        /// <summary>
        /// The service lines of a riser's text; lines that do not start with a known service (or, for services written in
        /// words, match none of their 'match' patterns) are ignored.
        /// </summary>
        public static List<ServiceLine> Parse(IEnumerable<string> texts, PlumbingRules rules)
        {
            var list = new List<ServiceLine>();
            foreach (var line in texts.SelectMany(t => (t ?? "").Split('\n')).Select(l => Regex.Replace(l, @"\s+", " ").Trim().ToUpperInvariant()))
            {
                string svc, rest;
                var m = Regex.Match(line, @"^(?<svc>[A-Z]{1,4})\b\s*(?<rest>.*)$");
                if (m.Success && rules.Services.ContainsKey(m.Groups["svc"].Value)) { svc = m.Groups["svc"].Value; rest = m.Groups["rest"].Value; }
                else
                {
                    // written in words: the matched words are not direction words ("SPRINKLER RISER" is not "RISE(R)" = UP)
                    svc = rules.ServiceForLabel(line, out var words);
                    if (svc == null) continue;
                    rest = line.Remove(words.Index, words.Length).Insert(words.Index, " ");
                }
                bool Has(IEnumerable<string> ws) => ws.Any(w => Regex.IsMatch(rest, $@"(?<![\w-]){Regex.Escape(w)}\b"));
                var s = new ServiceLine { Service = svc, Text = line, Up = Has(rules.UpWords), Down = Has(rules.DownWords), Through = Has(rules.ThroughWords) };
                var size = Regex.Match(line, @"(?<![\d./-])(?<whole>\d{1,2})(?:[ -](?<num>\d)/(?<den>\d))?\s*(""|''|IN\b)");
                if (size.Success)
                    s.Pipe = int.Parse(size.Groups["whole"].Value) +
                             (size.Groups["num"].Success ? double.Parse(size.Groups["num"].Value) / double.Parse(size.Groups["den"].Value) : 0);
                if (!s.Up && !s.Down && !s.Through && string.Equals(rules.Unmarked, "DN", StringComparison.OrdinalIgnoreCase)) s.Down = s.Unmarked = true;
                if (!list.Any(x => x.Service == s.Service)) list.Add(s);
            }
            return list;
        }

        public static RiserAssembly Run(DwgSheetIndex index, DwgRiserResult risers, AlignmentResult alignment, IDictionary<string, string> floorLevels,
                                        PlumbingRules rules, double drift = 24)
        {
            var result = new RiserAssembly();
            var floors = index.Floors.Select(f => f.Floor).Distinct().OrderBy(FloorKey.Order).ToList();
            string Above(string floor) { int i = floors.IndexOf(floor); return i >= 0 && i + 1 < floors.Count ? floors[i + 1] : null; }
            string Below(string floor) { int i = floors.IndexOf(floor); return i > 0 ? floors[i - 1] : null; }
            string lowest = floors.FirstOrDefault();
            double far = drift / 12;

            // ---- 1. every sleeved service of every group, as the crossings its words describe
            var raw = new List<Crossing>();
            foreach (var r in risers.Risers.OrderBy(r => FloorKey.Order(r.Floor)))
            {
                var fa = alignment?.For(r.Floor);
                var lines = Parse(r.TagTexts, rules);
                var circles = r.Symbols.Where(s => s.Block == DwgRiserReader.PipeCircle)
                                       .Select(s => (S: s, Service: rules.ServiceForLayer(s.Layer))).Where(x => x.Service != null).ToList();
                string tag = r.Tag;
                (double X, double Y)? At(double x, double y) => fa?.ToRevit(x, y);

                if (lines.Count == 0)
                {
                    // pipes drawn with no service list: say what is drawn, sleeve nothing by guess
                    var drawn = circles.Select(c => c.Service).Distinct().ToList();
                    if (drawn.Count == 0) continue;
                    var sleeved = drawn.Where(sv => rules.Services[sv].Sleeve).ToList();
                    string what = $"{circles.Count} pipe(s) drawn ({string.Join(", ", drawn)})";
                    if (tag == null)
                        Issue(result, r.Floor, null, At(r.X, r.Y), AssemblyIssue.TagMissing, $"{what} with no tag bubble and no UP/DN list");
                    else
                        Issue(result, r.Floor, tag, At(r.X, r.Y), AssemblyIssue.NoSize, $"{tag}: {what}; no UP/DN list next to its bubble");
                    if (sleeved.Count == 0) result.NotRisers++;
                    continue;
                }

                foreach (var line in lines)
                {
                    var svc = rules.Services[line.Service];
                    string name = rules.Name(line.Service, tag);
                    if (!svc.Sleeve)
                    {
                        result.NotRisers++;
                        if (!string.IsNullOrEmpty(svc.Note)) Issue(result, r.Floor, name, At(r.X, r.Y), AssemblyIssue.NotSleeved, $"'{line.Text}': {svc.Note}");
                        continue;
                    }
                    // a pipe labelled on its own (sprinkler: one leader per riser) is the one symbol drawn there
                    var circle = circles.Where(c => c.Service == line.Service).Select(c => c.S).FirstOrDefault()
                                 ?? (lines.Count == 1 && r.Symbols.Count == 1 ? r.Symbols[0] : null);
                    var pos = circle != null ? At(circle.X, circle.Y) : At(r.X, r.Y);
                    Crossing Make(string slab, string dir)
                    {
                        var c = new Crossing
                        {
                            Floor = slab, Tag = name, System = svc.System, Size = new DuctSize { Diameter = line.Pipe ?? svc.Pipe },
                            X = pos?.X ?? 0, Y = pos?.Y ?? 0, HasPosition = pos.HasValue, Roof = slab == FloorKey.Roof
                        };
                        c.From.Add($"{FloorKey.Describe(r.Floor)}: {tag ?? "(no tag)"} '{line.Text}' ({dir})");
                        c.Plans.Add(r.Floor);
                        if (line.Pipe != null) c.Notes.Add($"pipe size {Units.FormatInches(line.Pipe.Value)} from the label");
                        if (circle == null) c.Notes.Add($"no {line.Service} circle drawn in the {tag} group on the {FloorKey.Describe(r.Floor)} plan; placed at the group's centre");
                        if (!pos.HasValue) c.Notes.Add($"{FloorKey.Describe(r.Floor)} is not lined up with Revit");
                        return c;
                    }
                    if (line.Down || line.Through) raw.Add(Make(r.Floor, line.Through ? "THRU" : line.Unmarked ? UnmarkedDir : "DN"));
                    if (line.Up)
                    {
                        var up = Above(r.Floor);
                        if (up != null) raw.Add(Make(up, "UP"));
                        else Issue(result, r.Floor, name, pos, AssemblyIssue.Top, $"'{line.Text}' goes up but no plan above the {FloorKey.Describe(r.Floor)} was found");
                    }
                    if (!line.Up && !line.Down && !line.Through)
                        Issue(result, r.Floor, name, pos, AssemblyIssue.NoSize, $"'{line.Text}': no UP, DN or THRU");
                }
            }

            // ---- 2. one crossing per pipe per slab: the floor below's UP and this floor's DN are the same sleeve
            foreach (var c in raw)
            {
                var same = result.Crossings.FirstOrDefault(o => o.Floor == c.Floor && o.Tag == c.Tag && o.System == c.System &&
                                                                (!o.HasPosition || !c.HasPosition || Near((o.X, o.Y), (c.X, c.Y), far)));
                if (same == null) { result.Crossings.Add(c); continue; }
                same.From.AddRange(c.From);
                // the slab's own plan says where the pipe goes through it
                if (c.Plans.Contains(c.Floor) && !same.Plans.Contains(same.Floor) && c.HasPosition) { same.X = c.X; same.Y = c.Y; same.HasPosition = true; }
                same.Plans.AddRange(c.Plans);
                foreach (var n in c.Notes) if (!same.Notes.Contains(n)) same.Notes.Add(n);
            }

            // ---- 3. how sure, and what is not placed
            foreach (var c in result.Crossings)
            {
                var fa = alignment?.For(c.Floor);
                floorLevels.TryGetValue(c.Floor, out c.Level);
                bool own = c.Plans.Contains(c.Floor), fromBelow = c.Plans.Contains(Below(c.Floor) ?? "");
                c.Confidence = own && fromBelow ? "high" : "medium";
                c.Pdf = "not checked";
                if (!c.Notes.Any(n => n.StartsWith("pipe size")))
                    c.Notes.Add($"pipe size {Units.FormatInches(c.Size.Diameter.Value)} from rules.json (the plans carry no sizes; check the riser diagram)");
                if (!own && fromBelow)
                {
                    c.Status = Crossing.Review;
                    c.Notes.Add($"only the {FloorKey.Describe(Below(c.Floor))} plan shows it (UP); the {FloorKey.Describe(c.Floor)} plan does not list it");
                }
                else if (own && !fromBelow && c.From.All(f => f.Contains(UnmarkedDir)))
                {
                    // a label with no UP/DN is trusted only as the top of a pipe the floor below sends up (a note about
                    // the FDC on the facade, a valve...)
                    c.Status = Crossing.Review;
                    c.Notes.Add($"the label says neither UP nor DN and the {FloorKey.Describe(Below(c.Floor) ?? "floor below")} plan does not send this pipe up");
                }
                else if (own && !fromBelow && Below(c.Floor) != null && c.Floor != lowest)
                    c.Notes.Add($"not listed UP on the {FloorKey.Describe(Below(c.Floor))} plan");

                if (rules.SkipLowestSlab && c.Floor == lowest)
                {
                    c.Status = Crossing.Skip;
                    c.Notes.Add($"lowest level ({FloorKey.Describe(c.Floor)}): the slab is on grade, the pipe runs underground (rules.json plumbing.skipLowestSlab)");
                }
                else if (!c.HasPosition || fa == null || !fa.Usable) { c.Status = Crossing.Skip; c.Notes.Add($"{FloorKey.Describe(c.Floor)} position not confirmed (Revit position tab)"); }
                else if (c.Level == null) { c.Status = Crossing.Skip; c.Notes.Add($"{FloorKey.Describe(c.Floor)} is not matched to a Revit level"); }
            }

            // ---- 4. the sleeves of one group are spread along its row so they do not overlap (pipes of the group that
            //      sit together only: an offset pipe drawn elsewhere keeps its place)
            foreach (var g in result.Crossings.Where(c => c.HasPosition).GroupBy(c => (c.Floor, Riser: RiserOf(c.Tag))))
                foreach (var row in Clusters(g.ToList(), 36.0 / 12))
                    Spread(row, rules);
            Overlaps(result.Crossings, rules);

            // ---- 5. fixtures named on the plans: for review (the text is next to the fixture, not on its drain)
            if (!string.Equals(rules.FixtureSleeves, "off", StringComparison.OrdinalIgnoreCase))
                foreach (var fx in risers.Fixtures.OrderBy(x => FloorKey.Order(x.Floor)))
                {
                    if (!rules.Fixtures.TryGetValue(fx.Code, out var fs)) continue;
                    if (rules.SkipLowestSlab && fx.Floor == lowest) continue;
                    var fa = alignment?.For(fx.Floor);
                    var pos = fa?.ToRevit(fx.X, fx.Y);
                    var c = new Crossing
                    {
                        Floor = fx.Floor, Tag = fx.Code, System = fs.System, Size = new DuctSize { Diameter = fs.Pipe },
                        X = pos?.X ?? 0, Y = pos?.Y ?? 0, HasPosition = pos.HasValue, Status = Crossing.Review, Confidence = "low", Pdf = "not checked"
                    };
                    floorLevels.TryGetValue(fx.Floor, out c.Level);
                    c.From.Add($"{FloorKey.Describe(fx.Floor)}: fixture '{fx.Code}' ({fs.Name})");
                    c.Plans.Add(fx.Floor);
                    c.Notes.Add($"{fs.Name}: {(fs.Count > 1 ? $"{fs.Count} x " : "")}{Units.FormatInches(rules.SleeveFor(fs.Pipe))} sleeve" +
                                (fs.Count > 1 && fs.Spacing > 0 ? $" {Units.FormatInches(fs.Spacing)} c-c" : "") +
                                " under the fixture's drain; the position is the engineer's fixture text, lay the sleeve out on the fixture (manual: toilet sleeves page)");
                    if (!pos.HasValue || fa == null || !fa.Usable) c.Notes.Add($"{FloorKey.Describe(fx.Floor)} position not confirmed (Revit position tab)");
                    result.Crossings.Add(c);
                }

            foreach (var t in risers.LooseTags)
                Issue(result, t.Floor, t.Tag, alignment?.For(t.Floor)?.ToRevit(t.X, t.Y), AssemblyIssue.Loose, "tag bubble with no leader to a pipe group");

            result.Crossings = result.Crossings.OrderBy(c => FloorKey.Order(c.Floor)).ThenBy(c => c.Status == Crossing.Review && c.Confidence == "low" ? 1 : 0)
                                               .ThenBy(c => c.Tag ?? "~").ToList();
            result.Issues = result.Issues.OrderBy(i => FloorKey.Order(i.Floor)).ThenBy(i => i.Type).ToList();
            foreach (var i in result.Issues) if (i.Floor != null) floorLevels.TryGetValue(i.Floor, out i.Level);
            return result;
        }

        /// <summary>"S-P3" -> "P3" (the group the sleeve belongs to, from the name pattern).</summary>
        private static string RiserOf(string name)
        {
            if (name == null) return "?";
            int i = name.IndexOf('-');
            return i >= 0 ? name.Substring(i + 1) : name;
        }

        /// <summary>Single-link clusters of crossings closer than <paramref name="reach"/> (feet).</summary>
        private static List<List<Crossing>> Clusters(List<Crossing> list, double reach)
        {
            var clusters = new List<List<Crossing>>();
            foreach (var c in list)
            {
                var near = clusters.Where(k => k.Any(o => Math.Sqrt(Math.Pow(o.X - c.X, 2) + Math.Pow(o.Y - c.Y, 2)) <= reach)).ToList();
                var target = near.FirstOrDefault() ?? new List<Crossing>();
                if (near.Count == 0) clusters.Add(target);
                foreach (var other in near.Skip(1)) { target.AddRange(other); clusters.Remove(other); }
                target.Add(c);
            }
            return clusters;
        }

        /// <summary>
        /// The sleeves of one group on one slab, drawn as pipes 5" apart: kept in the order drawn along the row, centred
        /// where the pipes are, spread so neighbours keep <see cref="PlumbingRules.SleeveGap"/> between them.
        /// </summary>
        private static void Spread(List<Crossing> group, PlumbingRules rules)
        {
            if (group.Count < 2) return;
            double cx = group.Average(c => c.X), cy = group.Average(c => c.Y);
            // the row's direction: the longer spread of the drawn positions
            double sx = group.Max(c => c.X) - group.Min(c => c.X), sy = group.Max(c => c.Y) - group.Min(c => c.Y);
            bool alongX = sx >= sy;
            var ordered = group.OrderBy(c => alongX ? c.X : c.Y).ToList();
            double R(Crossing c) => rules.SleeveFor(c.Size?.Diameter ?? 0) / 2 / 12;
            double gap = rules.SleeveGap / 12;
            bool overlap = false;
            for (int i = 1; i < ordered.Count; i++)
            {
                double d = Math.Sqrt(Math.Pow(ordered[i].X - ordered[i - 1].X, 2) + Math.Pow(ordered[i].Y - ordered[i - 1].Y, 2));
                if (d < R(ordered[i]) + R(ordered[i - 1]) + gap - 1e-6) overlap = true;
            }
            if (!overlap) return;
            var offsets = new List<double> { 0 };
            for (int i = 1; i < ordered.Count; i++) offsets.Add(offsets[i - 1] + R(ordered[i - 1]) + R(ordered[i]) + gap);
            double mid = (offsets.First() + offsets.Last()) / 2;
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                double t = offsets[i] - mid;
                double nx = alongX ? cx + t : cx, ny = alongX ? cy : cy + t;
                double moved = Math.Sqrt(Math.Pow(nx - c.X, 2) + Math.Pow(ny - c.Y, 2)) * 12;
                c.X = nx; c.Y = ny;
                c.Notes.Add($"the {RiserOf(c.Tag)} pipes are drawn closer than their sleeves: sleeves set in a row {Units.FormatInches(rules.SleeveGap)} apart" +
                            (moved >= 0.5 ? $" (moved {Units.FormatInches(Math.Round(moved * 2) / 2)})" : ""));
            }
        }

        /// <summary>Sleeves of different groups on one slab that still overlap: review.</summary>
        private static void Overlaps(List<Crossing> all, PlumbingRules rules)
        {
            var list = all.Where(c => c.HasPosition && c.Status == Crossing.Place).ToList();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (a.Floor != b.Floor || RiserOf(a.Tag) == RiserOf(b.Tag)) continue;
                    double need = (rules.SleeveFor(a.Size.Diameter.Value) + rules.SleeveFor(b.Size.Diameter.Value)) / 2 / 12;
                    double d = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    if (d >= need) continue;
                    foreach (var (c, o) in new[] { (a, b), (b, a) })
                    {
                        c.Status = Crossing.Review;
                        c.Notes.Add($"overlaps {o.Tag} ({Units.FormatInches(Math.Round(d * 12, 1))} c-c)");
                    }
                }
        }

        private static void Issue(RiserAssembly r, string floor, string tag, (double X, double Y)? at, string type, string detail) =>
            r.Issues.Add(new AssemblyIssue { Floor = floor, Tag = tag, X = at?.X, Y = at?.Y, Type = type, Detail = detail });

        private static bool Near((double X, double Y) a, (double X, double Y) b, double d) => Math.Abs(a.X - b.X) <= d && Math.Abs(a.Y - b.Y) <= d;
    }
}
