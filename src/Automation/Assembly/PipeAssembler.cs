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
        /// <summary>The From line of a storm pipe drawn with no tag (a roof / terrace / area drain's leader).</summary>
        private const string LeaderMark = "storm pipe drawn with no tag";
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
                                        PlumbingRules rules, double drift = 24, bool trustUp = false)
        {
            var result = new RiserAssembly();
            var floors = index.Floors.Select(f => f.Floor).Distinct().OrderBy(FloorKey.Order).ToList();
            string Above(string floor) { int i = floors.IndexOf(floor); return i >= 0 && i + 1 < floors.Count ? floors[i + 1] : null; }
            string Below(string floor) { int i = floors.IndexOf(floor); return i > 0 ? floors[i - 1] : null; }
            string lowest = floors.FirstOrDefault();
            double far = drift / 12;

            // ---- 1. every sleeved service of every group, as the crossings its words describe
            var raw = new List<Crossing>();
            var leaders = new List<(DwgRiser R, List<RiserSymbol> Circles)>();
            var bare = new List<(AssemblyIssue Issue, List<string> Services)>();     // groups drawn with no tag: explained after merging
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
                    if (tag == null && rules.SkipLowestSlab && r.Floor == lowest) { }   // slab on grade: nothing to sleeve, nothing to report
                    else if (tag == null && drawn.All(sv => rules.Services[sv].System == "Storm" && rules.Services[sv].Sleeve))
                    {
                        // storm pipes alone with no bubble: the leaders of roof / terrace drains (C.F.R.D + O.D pairs) on their way
                        // down to the storm main (manual, storm sleeves 3-6 and 11): sleeved at each circle
                        leaders.Add((r, circles.Select(c => c.S).ToList()));
                        continue;
                    }
                    else if (tag == null)
                    {
                        Issue(result, r.Floor, null, At(r.X, r.Y), AssemblyIssue.TagMissing, $"{what} with no tag bubble and no UP/DN list");
                        bare.Add((result.Issues[result.Issues.Count - 1], sleeved));
                    }
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

            // ---- 1b. storm leaders: the same spot on several floors is one leader (named D1, D2... by position); each pipe of
            //      a drain pair keeps its own sleeve (ST-D1#1, ST-D1#2)
            raw.AddRange(Leaders(leaders, alignment, rules, far));

            // ---- 2. one crossing per pipe per slab: the floor below's UP and this floor's DN are the same sleeve
            foreach (var c in raw)
            {
                var same = result.Crossings.FirstOrDefault(o => o.Floor == c.Floor && o.Tag == c.Tag && o.System == c.System &&
                                                                (!o.HasPosition || !c.HasPosition || Near((o.X, o.Y), (c.X, c.Y), far)));
                // the floor below sends the pipe up at one spot and this floor's own plan shows it at another: the riser offsets
                // in the ceiling below, the slab is crossed where this floor's plan draws it (one sleeve, not two)
                if (same == null && c.Tag != null && !c.From.Any(f => f.Contains(LeaderMark)))
                {
                    same = result.Crossings.FirstOrDefault(o => o.Floor == c.Floor && o.Tag == c.Tag && o.System == c.System &&
                                                                o.Plans.Contains(o.Floor) != c.Plans.Contains(c.Floor));
                    if (same != null && same.HasPosition && c.HasPosition)
                    {
                        var below = c.Plans.Contains(c.Floor) ? same : c;
                        string note = $"the {FloorKey.Describe(Below(c.Floor) ?? "floor below")} plan sends it up {Math.Sqrt(Math.Pow(c.X - same.X, 2) + Math.Pow(c.Y - same.Y, 2)):0.#} ft from where the " +
                                      $"{FloorKey.Describe(c.Floor)} plan draws it: the riser offsets in the ceiling below; one sleeve, where this floor's plan shows it";
                        if (!same.Notes.Contains(note)) same.Notes.Add(note);
                        same.DrawnOffset = true;
                    }
                }
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
                if (!own && fromBelow && trustUp)
                    // the floor below lists it UP in words: the pipe goes on through this slab, straight (manual 23, 45-51)
                    c.Notes.Add($"only the {FloorKey.Describe(Below(c.Floor))} plan shows it; its list says UP, so it is placed straight above it " +
                                $"(manual 23, 45-51: risers stay straight); the {FloorKey.Describe(c.Floor)} plan does not list it");
                else if (!own && fromBelow)
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
                else if (own && !fromBelow && Below(c.Floor) != null && c.Floor != lowest && !c.From.Any(f => f.Contains(LeaderMark)))
                    c.Notes.Add($"not listed UP on the {FloorKey.Describe(Below(c.Floor))} plan");

                if (rules.SkipLowestSlab && c.Floor == lowest)
                {
                    c.Status = Crossing.Skip;
                    c.Notes.Add($"lowest level ({FloorKey.Describe(c.Floor)}): the slab is on grade, the pipe runs underground (rules.json plumbing.skipLowestSlab)");
                }
                else if (!c.HasPosition || fa == null || !fa.Usable) { c.Status = Crossing.Skip; c.Notes.Add($"{FloorKey.Describe(c.Floor)} position not confirmed (Revit position tab)"); }
                else if (c.Level == null) { c.Status = Crossing.Skip; c.Notes.Add($"{FloorKey.Describe(c.Floor)} is not matched to a Revit level"); }
            }

            // ---- 3b. the top of a stack: above its last fixture the sanitary stack carries on as the vent, through the roof as
            //      one VTR (sanitary riser diagram). A sanitary crossing only the floor below sends up, on the group's top slab
            //      where its vent also goes through, is that same pipe: no second sleeve
            foreach (var c in result.Crossings.Where(c => c.System == "Sanitary" && c.Status != Crossing.Skip && !c.Plans.Contains(c.Floor)))
            {
                string group = RiserOf(c.Tag), up = Above(c.Floor);
                if (up != null && result.Crossings.Any(o => o.Floor == up && RiserOf(o.Tag) == group)) continue;      // not the top
                var vent = result.Crossings.FirstOrDefault(o => o.Floor == c.Floor && o.System == "Vent" && RiserOf(o.Tag) == group && o.Status != Crossing.Skip);
                if (vent == null) continue;
                c.Status = Crossing.Skip; c.MergedInto = vent;
                c.Notes.Add($"top of the stack: the sanitary pipe goes on as {vent.Tag} through this slab (vent through roof), one sleeve; " +
                            $"the {FloorKey.Describe(c.Floor)} plan does not list {c.Tag}");
            }

            // ---- 4. the sleeves of one group are spread along its row so they do not overlap (pipes of the group that
            //      sit together only: an offset pipe drawn elsewhere keeps its place)
            //      in one order on every floor (the stack runs straight): the order drawn on the floors where every pipe of the
            //      group has its own circle, the most complete one first
            //      a pipe that ends at this slab (the cellar's storm pipe going up to the 1ST FLOOR only) goes at the end of the
            //      row, so the pipes that carry on sit as they do on the floors next to it
            var orders = RowOrders(result.Crossings);
            var floorsOf = result.Crossings.Where(c => c.Tag != null).GroupBy(c => c.Tag).ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(c => c.Floor)));
            bool Continues(Crossing c) => c.Tag != null && floorsOf[c.Tag] is var on && (on.Contains(Above(c.Floor) ?? "") || on.Contains(Below(c.Floor) ?? ""));
            foreach (var g in result.Crossings.Where(c => c.HasPosition).GroupBy(c => (c.Floor, Riser: RiserOf(c.Tag))))
                foreach (var row in Clusters(g.ToList(), 36.0 / 12))
                    Spread(row, rules, orders.TryGetValue(g.Key.Riser, out var order) ? order : null, Continues);
            // ---- 4b. each pipe straight up the building: drawn a few inches apart floor to floor, its sleeves are lined up
            Straighten(result.Crossings, rules, floors);
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
                    else if (fx.Drains.Count > 0)
                    {
                        // the fixture as the architect drew it on the plan gives the sleeve point(s); still for review until the
                        // model is checked (a sleeve already there, a modelled fixture's connector: FixtureSleeveLocator)
                        var drains = fx.Drains.Select(d => fa.ToRevit(d.X, d.Y)).Where(d => d.HasValue).Select(d => d.Value).ToList();
                        if (drains.Count == fx.Drains.Count)
                        {
                            c.X = drains.Average(d => d.X); c.Y = drains.Average(d => d.Y);
                            if (drains.Count > 1) { c.Points.AddRange(drains.Select(d => new[] { d.X, d.Y })); c.EachPoint = true; }
                            c.DrawnDrain = fx.DrainHow;
                        }
                    }
                    result.Crossings.Add(c);
                }

            // ---- 6. pipes drawn with no tag that are explained by a tagged group: the same pipe as a sleeve already placed on
            //      this floor, or the stack after its offset (the floor above shows the tagged group right there)
            foreach (var (issue, services) in bare.Where(b => b.Issue.X.HasValue && b.Services.Count > 0))
            {
                var at = (issue.X.Value, issue.Y.Value);
                var here = result.Crossings.FirstOrDefault(c => c.Floor == issue.Floor && c.HasPosition && c.Status != Crossing.Skip && Near((c.X, c.Y), at, 1.5) &&
                                                               services.Contains(c.Tag?.Split('-')[0] ?? ""));
                string up = Above(issue.Floor);
                var above = here != null || up == null ? null : result.Crossings.FirstOrDefault(c => c.Floor == up && c.HasPosition && c.Plans.Contains(up) &&
                                                               c.Tag != null && Near((c.X, c.Y), at, far) && services.Contains(c.Tag.Split('-')[0]));
                if (here != null)
                {
                    issue.Type = AssemblyIssue.Explained;
                    issue.Detail += $": the same pipe as {here.Tag}, already sleeved here";
                }
                else if (above != null)
                {
                    issue.Type = AssemblyIssue.Explained;
                    issue.Tag = RiserOf(above.Tag);
                    issue.Detail += $": the {RiserOf(above.Tag)} stack after its offset; the {FloorKey.Describe(up)} plan shows {RiserOf(above.Tag)} right here, its sleeve is in that slab";
                }
            }

            foreach (var t in risers.LooseTags)
                Issue(result, t.Floor, t.Tag, alignment?.For(t.Floor)?.ToRevit(t.X, t.Y), AssemblyIssue.Loose, "tag bubble with no leader to a pipe group");

            result.Crossings = result.Crossings.OrderBy(c => FloorKey.Order(c.Floor)).ThenBy(c => c.Status == Crossing.Review && c.Confidence == "low" ? 1 : 0)
                                               .ThenBy(c => c.Tag ?? "~").ToList();
            result.Issues = result.Issues.OrderBy(i => FloorKey.Order(i.Floor)).ThenBy(i => i.Type).ToList();
            foreach (var i in result.Issues) if (i.Floor != null) floorLevels.TryGetValue(i.Floor, out i.Level);
            return result;
        }

        /// <summary>"S-P3" -> "P3" (the group the sleeve belongs to, from the name pattern); "ST-D1#2" -> "D1".</summary>
        private static string RiserOf(string name)
        {
            if (name == null) return "?";
            int i = name.IndexOf('-');
            string riser = i >= 0 ? name.Substring(i + 1) : name;
            int hash = riser.IndexOf('#');
            return hash > 0 ? riser.Substring(0, hash) : riser;
        }

        /// <summary>
        /// Storm pipes drawn with no tag, as crossings of their own floor's slab (the drain sits on it, or the leader passes
        /// through it on the way down). Groups at one spot (within <paramref name="far"/> feet) on several floors are one
        /// leader and share its name, numbered from the top of the building down.
        /// </summary>
        private static List<Crossing> Leaders(List<(DwgRiser R, List<RiserSymbol> Circles)> leaders, AlignmentResult alignment, PlumbingRules rules, double far)
        {
            var placed = new List<(string Id, double X, double Y)>();
            var result = new List<Crossing>();
            int n = 0;
            foreach (var (r, circles) in leaders.OrderByDescending(l => FloorKey.Order(l.R.Floor)))
            {
                var fa = alignment?.For(r.Floor);
                var at = fa?.ToRevit(r.X, r.Y);
                string id = at == null ? null : placed.Where(p => Near((p.X, p.Y), at.Value, far)).Select(p => p.Id).FirstOrDefault();
                if (id == null) id = "D" + (++n);
                if (at != null) placed.Add((id, at.Value.X, at.Value.Y));
                var ordered = circles.OrderBy(c => c.X).ThenBy(c => c.Y).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var svc = rules.ServiceForLayer(ordered[i].Layer);
                    var pos = fa?.ToRevit(ordered[i].X, ordered[i].Y);
                    var c = new Crossing
                    {
                        Floor = r.Floor, Tag = rules.Name(svc, id) + (ordered.Count > 1 ? "#" + (i + 1) : ""), System = rules.Services[svc].System,
                        Size = new DuctSize { Diameter = rules.Services[svc].Pipe }, X = pos?.X ?? 0, Y = pos?.Y ?? 0, HasPosition = pos.HasValue,
                        Roof = r.Floor == FloorKey.Roof
                    };
                    c.From.Add($"{FloorKey.Describe(r.Floor)}: {LeaderMark} ({ordered.Count} pipe(s) here)");
                    c.Plans.Add(r.Floor);
                    c.Notes.Add($"{LeaderMark}: a roof / terrace drain leader going down to the storm main (manual, storm sleeves 3-6, 11); check the drain on the plan and the storm riser diagram");
                    if (!pos.HasValue) c.Notes.Add($"{FloorKey.Describe(r.Floor)} is not lined up with Revit");
                    result.Add(c);
                }
            }
            return result;
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
        private static void Spread(List<Crossing> group, PlumbingRules rules, List<string> order = null, Func<Crossing, bool> continues = null)
        {
            if (group.Count < 2) return;
            double cx = group.Average(c => c.X), cy = group.Average(c => c.Y);
            // the row's direction: the longer spread of the drawn positions
            double sx = group.Max(c => c.X) - group.Min(c => c.X), sy = group.Max(c => c.Y) - group.Min(c => c.Y);
            bool alongX = sx >= sy;
            var ordered = order != null && group.All(c => order.Contains(c.System))
                ? group.OrderBy(c => order.IndexOf(c.System)).ToList()
                : group.OrderBy(c => alongX ? c.X : c.Y).ToList();
            if (continues != null) ordered = ordered.OrderBy(c => continues(c) ? 0 : 1).ToList();     // stable: the others keep their order
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

        /// <summary>
        /// Per group (P6), the order of its pipes along the row (by system) as drawn on the floor that shows the most of them
        /// each at its own circle (a pipe placed at the group's centre does not count), the commonest such order on ties.
        /// </summary>
        private static Dictionary<string, List<string>> RowOrders(List<Crossing> all)
        {
            var result = new Dictionary<string, List<string>>();
            foreach (var g in all.Where(c => c.HasPosition && c.Tag != null).GroupBy(c => RiserOf(c.Tag)))
            {
                var seen = new List<List<string>>();
                foreach (var floor in g.GroupBy(c => c.Floor))
                {
                    var row = floor.ToList();
                    if (row.Count < 2 || row.Any(c => c.Notes.Any(n => n.Contains("placed at the group's centre")))) continue;
                    bool alongX = row.Max(c => c.X) - row.Min(c => c.X) >= row.Max(c => c.Y) - row.Min(c => c.Y);
                    seen.Add(row.OrderBy(c => alongX ? c.X : c.Y).Select(c => c.System).ToList());
                }
                var best = seen.GroupBy(o => string.Join(",", o)).OrderByDescending(o => o.First().Count).ThenByDescending(o => o.Count()).FirstOrDefault();
                if (best != null) result[g.Key] = best.First();
            }
            return result;
        }

        /// <summary>
        /// One pipe (S-P3) floor by floor: the plans draw it a few inches apart from floor to floor, and its group's row is
        /// spread on each floor on its own, so the sleeves step by an inch or a foot where the pipe runs straight (manual
        /// 22-23). Floors whose sleeves are within <see cref="PlumbingRules.StackSnap"/> of the next are one straight run,
        /// cut where the plans draw an offset; each run's sleeves go to the spot most of its floors share (only those within
        /// StackSnap of it, never onto a neighbour of its group). A step still left where both floors' own plans draw the pipe
        /// is the engineer's offset: <see cref="Crossing.DrawnOffset"/>.
        /// </summary>
        private static void Straighten(List<Crossing> all, PlumbingRules rules, List<string> floors)
        {
            double snap = rules.StackSnap / 12, tight = 1.0 / 12;
            double Dist(Crossing a, Crossing b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            double R(Crossing c) => rules.SleeveFor(c.Size?.Diameter ?? 0) / 2 / 12;
            bool Own(Crossing c) => c.Plans.Contains(c.Floor);
            var live = all.Where(c => c.HasPosition && c.Status != Crossing.Skip && c.MergedInto == null && c.Tag != null).ToList();
            var stacks = live.GroupBy(c => c.Tag).Select(p => p.OrderBy(c => floors.IndexOf(c.Floor)).ToList())
                             .Where(s => s.Count > 1 && s.Select(c => c.Floor).Distinct().Count() == s.Count).ToList();   // drawn twice on a floor: leave it
            // where each pipe should be: the spot most floors of its straight run already share (ties: the one closest to all)
            var want = new Dictionary<Crossing, (double X, double Y, string Floor)>();
            foreach (var stack in snap > 0 ? stacks : new List<List<Crossing>>())
            {
                var runs = new List<List<Crossing>> { new List<Crossing> { stack[0] } };
                for (int i = 1; i < stack.Count; i++)
                {
                    if (stack[i].DrawnOffset || Dist(stack[i], stack[i - 1]) > snap) runs.Add(new List<Crossing>());
                    runs[runs.Count - 1].Add(stack[i]);
                }
                foreach (var run in runs.Where(r => r.Count > 1))
                {
                    var target = run.OrderByDescending(t => run.Count(o => Dist(o, t) <= tight)).ThenBy(t => run.Sum(o => Dist(o, t))).First();
                    foreach (var c in run.Where(c => Dist(c, target) <= snap)) want[c] = (target.X, target.Y, target.Floor);
                }
            }

            var moved = new Dictionary<Crossing, (double X, double Y, string Note)>();
            void Move(Crossing c, double x, double y, string note)
            {
                moved[c] = (c.X, c.Y, note);
                c.X = x; c.Y = y;
                c.Notes.Add(note);
            }
            string Inches(double feet) => Units.FormatInches(Math.Round(feet * 12 * 2) / 2);
            const string Why = "the plans draw it a little apart from floor to floor, the riser runs straight (manual 22-23)";
            // a group's row on one slab moves as one when its pipes all need the same move (it keeps its spacing, and a pipe
            // that ends at this slab moves with the row); otherwise each pipe on its own
            foreach (var row in want.Keys.GroupBy(c => (c.Floor, Group: RiserOf(c.Tag))))
            {
                var members = row.ToList();
                var moves = members.Select(c => (C: c, Dx: want[c].X - c.X, Dy: want[c].Y - c.Y)).ToList();
                if (moves.All(m => Math.Sqrt(m.Dx * m.Dx + m.Dy * m.Dy) <= tight / 4)) continue;
                bool together = moves.All(a => moves.All(b => Math.Sqrt(Math.Pow(a.Dx - b.Dx, 2) + Math.Pow(a.Dy - b.Dy, 2)) <= tight));
                if (together)
                {
                    double mx = moves.Average(m => m.Dx), my = moves.Average(m => m.Dy);
                    var riders = live.Where(o => o.Floor == row.Key.Floor && RiserOf(o.Tag) == row.Key.Group && !want.ContainsKey(o)).ToList();
                    foreach (var c in members)
                        Move(c, c.X + mx, c.Y + my, $"lined up with {c.Tag} on the {FloorKey.Describe(want[c].Floor)} (moved {Inches(Math.Sqrt(mx * mx + my * my))}): {Why}");
                    foreach (var c in riders)
                        Move(c, c.X + mx, c.Y + my, $"moved {Inches(Math.Sqrt(mx * mx + my * my))} with its row so the pipes going on up stay straight (manual 22-23)");
                }
                else
                    foreach (var m in moves.Where(m => Math.Sqrt(m.Dx * m.Dx + m.Dy * m.Dy) > tight / 4))
                        Move(m.C, want[m.C].X, want[m.C].Y, $"lined up with {m.C.Tag} on the {FloorKey.Describe(want[m.C].Floor)} (moved {Inches(Math.Sqrt(m.Dx * m.Dx + m.Dy * m.Dy))}): {Why}");
            }

            // its group's other sleeves on the slab keep their gap: a sleeve lined up onto a neighbour goes back where it was
            bool Clashes(Crossing c) => live.Any(o => o != c && o.Floor == c.Floor && RiserOf(o.Tag) == RiserOf(c.Tag) && Dist(o, c) < R(o) + R(c) + rules.SleeveGap / 12 - 1e-6);
            for (var back = moved.Keys.FirstOrDefault(Clashes); back != null; back = moved.Keys.FirstOrDefault(Clashes))
            {
                var was = moved[back];
                back.X = was.X; back.Y = was.Y;
                back.Notes.Remove(was.Note);
                moved.Remove(back);
            }

            foreach (var stack in stacks)
                for (int i = 1; i < stack.Count; i++)
                    if (!stack[i].DrawnOffset && Dist(stack[i], stack[i - 1]) > tight && Own(stack[i]) && Own(stack[i - 1]))
                    {
                        stack[i].DrawnOffset = true;
                        stack[i].Notes.Add($"offset from the {FloorKey.Describe(stack[i - 1].Floor)} by {Units.FormatInches(Math.Round(Dist(stack[i], stack[i - 1]) * 12))}: " +
                                           "both floors' plans draw it there (check the riser diagram)");
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
