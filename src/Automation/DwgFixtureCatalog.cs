using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// The fixtures of the architect's DWGs from their blocks (<see cref="DwgFixtureBlocks"/>), in model coordinates, with
    /// no one naming anything. Three steps:
    /// 1. <see cref="Read"/>: each floor's file read directly and placed where Revit shows the DWG (transform, units and
    ///    import scale, checked against the lines Revit shows); each block judged by its own clean drawing (shape).
    /// 2. <see cref="Identify"/>: each kind of block decided once over all its copies, from the plumbing plans' fixture
    ///    labels next to them (each label votes for its nearest block), its shape and its name (a hint only). Labels and
    ///    shape agreeing (or name and shape) = certain, saved to the office table (<see cref="FixtureBlockMap"/>) for
    ///    every later floor and project; one sign alone = used and flagged for a check; nothing clear = not a fixture or
    ///    reported, never placed. A name in the table (said by a person or learned) wins.
    /// 3. <see cref="Locate"/>: each identified block's sleeve point(s) by the office rules (<see cref="FixtureDrains.LocateKnown"/>).
    /// Call on Revit's thread.
    /// </summary>
    public static class DwgFixtureCatalog
    {
        /// <summary>Share of the blocks that must fall on Revit's fixture lines for the file to be trusted to line up.</summary>
        private const double MinFit = 0.5;
        /// <summary>A label this far (inches) from a block's box can vote for it.</summary>
        private const double VoteInches = 36;

        public class Item
        {
            public string Name, Layer, How, Level;
            /// <summary>The fixture code (WC, LAV...); null = not a fixture or not identified.</summary>
            public string Code;
            /// <summary>What its drawing alone says it is.</summary>
            public string Guess;
            /// <summary>Identified from one sign only: place it, flag it for a check.</summary>
            public bool Check;
            /// <summary>How its kind was decided (for the list and the report).</summary>
            public string Why;
            public List<string> Inner = new List<string>();
            /// <summary>Box and sleeve point(s), model inches; Points null until located (or no fixture).</summary>
            public double X0, Y0, X1, Y1;
            public List<(double X, double Y)> Points;
            /// <summary>Unit direction (model axes) from the fixture to the wall behind it; null = not known.</summary>
            public (double X, double Y)? Back;
            public bool Mirrored;
            /// <summary>The name in the office table when the read began (a person's or a learned name).</summary>
            internal bool Named;
            internal List<FixtureDrains.Stroke> Strokes;
            internal DwgFixtureReader.Plan Plan;
            public double Cx => (X0 + X1) / 2;
            public double Cy => (Y0 + Y1) / 2;
        }

        public class Result
        {
            /// <summary>The file was read and lines up with what Revit shows: its blocks are the floor's fixtures.</summary>
            public bool Used;
            /// <summary>Why the blocks were not used (for the log and the dialog).</summary>
            public string Note;
            public List<Item> Items = new List<Item>();
            public string File;
            public DwgFixtureReader.Plan Plan;
        }

        /// <summary>A fixture label of the plumbing plans: its code and where it is (feet).</summary>
        public class Label
        {
            public string Level, Tag;
            public double X, Y;
        }

        /// <summary>Read, identify and locate one floor's blocks (Extract Fixtures).</summary>
        public static Result ForPlan(DwgFixtureReader.Plan plan, PlumbingRules rules, IList<Label> labels, List<string> notes)
        {
            var map = FixtureBlockMap.Load();
            var res = Read(plan, rules, map);
            if (res.Used)
            {
                notes?.AddRange(Identify(new List<Result> { res }, labels, map));
                Locate(res, rules);
            }
            return res;
        }

        // ------------------------------------------------------------------ 1. read

        public static Result Read(DwgFixtureReader.Plan plan, PlumbingRules rules, IDictionary<string, string> map)
        {
            var res = new Result { File = plan.FilePath, Plan = plan };
            if (plan.FilePath == null) { res.Note = $"{plan.Source}: its file was not found (not linked, and not next to the model): fixtures by shape only."; return res; }
            if (plan.Transform == null) { res.Note = $"{plan.Source}: no placement in the model."; return res; }

            var (fixture, _, @new, remove) = DwgFixtureReader.Patterns(rules);
            var layers = new DwgFixtureBlocks.Layers { Fixture = fixture, New = @new, Remove = remove, Kitchen = DwgFixtureReader.KitchenLayers };
            var read = DwgFixtureBlocks.Read(plan.FilePath, layers, map);
            if (read.Error != null) { res.Note = $"{plan.Source}: the file could not be read ({read.Error}): fixtures by shape only."; return res; }
            if (read.Blocks.Count == 0) { res.Note = $"{plan.Source}: no fixture blocks in the file: fixtures by shape only."; return res; }

            // the file's units to model feet: the scale under which its blocks land on the lines Revit shows
            double fpu = read.InchesPerUnit / 12;
            var scales = new[] { fpu * plan.ImportScale, fpu, plan.ImportScale, 1.0 / 12, 1.0 }.Where(s => s > 0).Distinct().ToList();
            double bestScale = scales[0], bestFit = -1;
            foreach (var s in scales)
            {
                double fit = Fit(read.Blocks, plan, s);
                if (fit > bestFit + 1e-6) { bestFit = fit; bestScale = s; }
            }
            App.Log($"DWG fixture blocks: {plan.Source} on {plan.Level}: {read.Blocks.Count} block(s) in {plan.FilePath} (units {read.Units}); " +
                    $"{bestFit:P0} fall on the lines Revit shows at {bestScale * 12:0.####} in/unit");
            if (bestFit < MinFit)
            {
                res.Note = $"{plan.Source}: the file does not line up with the drawing Revit shows (only {bestFit:P0} of its blocks fall on it; is the link older or moved?): fixtures by shape only.";
                return res;
            }
            res.Used = true;

            foreach (var b in read.Blocks)
            {
                var strokes = Strokes(b, plan, bestScale);
                if (strokes.Count == 0) continue;
                res.Items.Add(new Item
                {
                    Name = b.Name, Layer = b.Layer, Code = b.Code, Named = b.Code != null, Inner = b.Inner.Distinct().ToList(), Mirrored = b.Mirrored,
                    Level = plan.Level, Plan = plan, Strokes = strokes, Guess = FixtureDrains.Guess(strokes),
                    X0 = strokes.Min(s => s.X0), Y0 = strokes.Min(s => s.Y0), X1 = strokes.Max(s => s.X1), Y1 = strokes.Max(s => s.Y1),
                    Why = b.Code != null ? "named in the office table" : null
                });
            }
            return res;
        }

        // ------------------------------------------------------------------ 2. identify

        /// <summary>
        /// Decides each kind of block not in the office table from all its copies on the floors read; saves the certain
        /// ones to the table. Returns what to tell the user (the kinds left unidentified).
        /// </summary>
        public static List<string> Identify(List<Result> results, IList<Label> labels, IDictionary<string, string> map)
        {
            var notes = new List<string>();
            var items = results.Where(r => r.Used).SelectMany(r => r.Items).ToList();
            labels = labels ?? new List<Label>();

            // each label votes for the nearest block on its floor (a toilet's label is nearer the toilet than its door)
            var votes = new Dictionary<Item, List<string>>();
            foreach (var l in labels.Where(l => l.Tag != null))
            {
                var near = items.Where(i => AutoPlacer.SameFloor(i.Level, l.Level))
                                .Select(i => (I: i, D: Gap(i, l.X * 12, l.Y * 12))).Where(t => t.D <= VoteInches)
                                .OrderBy(t => t.D).FirstOrDefault();
                if (near.I == null) continue;
                if (!votes.TryGetValue(near.I, out var list)) votes[near.I] = list = new List<string>();
                list.Add(l.Tag.ToUpperInvariant());
            }
            var labelledFloors = new HashSet<string>(labels.Select(l => l.Level).Where(x => x != null), StringComparer.OrdinalIgnoreCase);

            bool learned = false;
            foreach (var kind in items.Where(i => !i.Named).GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var copies = kind.ToList();
                string name = kind.Key;

                // the labels: the code most of its labelled copies carry
                var tags = copies.Where(votes.ContainsKey).Select(c => votes[c].GroupBy(t => t).OrderByDescending(g => g.Count()).First().Key).ToList();
                var topTag = tags.GroupBy(t => t).OrderByDescending(g => g.Count()).FirstOrDefault();
                string label = topTag != null && topTag.Count() >= Math.Max(1, (tags.Count + 1) / 2) ? topTag.Key : null;
                int labelled = tags.Count;
                bool onLabelledFloors = copies.Any(c => labelledFloors.Any(f => AutoPlacer.SameFloor(f, c.Level)));

                // the shape: what most copies' drawings are
                var topShape = copies.Select(c => c.Guess).Where(g => g != null).GroupBy(g => g).OrderByDescending(g => g.Count()).FirstOrDefault();
                string shape = topShape != null && topShape.Count() * 2 >= copies.Count ? topShape.Key : null;

                // the name (and the name of the block it stands for): a hint
                string hint = FixtureBlockMap.Suggest(name) ?? copies.Select(c => c.Inner.Select(FixtureBlockMap.Suggest).FirstOrDefault(h => h != null)).FirstOrDefault(h => h != null);

                string code = null, why; bool certain = false;
                if (label != null && FixtureBlockMap.Same(label, shape))
                { code = label; certain = true; why = $"{labelled} of {copies.Count} copies labelled {label} on the plumbing plans, and its drawing is a {Word(shape)}"; }
                else if (label != null && FixtureBlockMap.Same(label, hint) && shape == null)
                { code = label; certain = true; why = $"labelled {label} on the plumbing plans ({labelled} of {copies.Count}) and named like one"; }
                else if (shape != null && FixtureBlockMap.Same(shape, hint))
                { code = shape; certain = true; why = $"its drawing is a {Word(shape)} and its name says so"; }
                else if (label != null && shape == null && labelled >= 2)
                { code = label; why = $"labelled {label} on the plumbing plans ({labelled} of {copies.Count}); its drawing does not show it: check"; }
                else if (shape != null && label == null && !(onLabelledFloors && labelled == 0 && copies.Count >= 3 && hint == "none"))
                { code = shape; why = $"its drawing is a {Word(shape)}" + (onLabelledFloors ? " (no plumbing label next to it)" : " (no plumbing labels to confirm it)") + ": check"; }
                else if (shape == null && label == null)
                {
                    why = hint == "none" ? "not a fixture (name and drawing)" : "not a fixture by its drawing, no plumbing label next to it";
                    if (hint == "none") { map[name] = "none"; learned = true; }
                }
                else
                {
                    why = $"unclear: labels say {label ?? "nothing"}, its drawing {(shape == null ? "nothing" : "a " + Word(shape))}";
                    notes.Add($"Block '{name}' ({copies.Count} cop{(copies.Count == 1 ? "y" : "ies")}): {why}. Not placed.");
                }

                // kitchen or laundry sinks: the kitchen layer says KS where the labels and shape only say "a sink"
                if (code == "LAV" && copies.Count(c => c.Guess == "KS") * 2 > copies.Count) code = "KS";
                foreach (var c in copies) { c.Code = code; c.Check = code != null && !certain; c.Why = why; }
                if (certain) { map[name] = code; learned = true; }
                App.Log($"DWG fixture blocks: '{name}' x{copies.Count} -> {code ?? "not a fixture"} ({(certain ? "certain" : code == null ? "-" : "check")}): {why}");
            }
            if (learned)
            {
                try { FixtureBlockMap.Save(map.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)); }
                catch (Exception ex) { App.Log("DWG fixture blocks: the office table not saved: " + ex.Message); }
            }
            return notes;
        }

        private static string Word(string code)
        {
            switch (code)
            {
                case "WC": return "toilet";
                case "LAV": return "lavatory";
                case "KS": return "kitchen sink";
                case "LS": return "laundry sink";
                case "BT": return "tub";
                case "SH": return "shower";
                case "W/D": case "WD": return "washer";
                default: return code?.ToLowerInvariant();
            }
        }

        // ------------------------------------------------------------------ 3. locate

        /// <summary>The sleeve point(s) of every identified block (the rules' fixtures only).</summary>
        public static void Locate(Result res, PlumbingRules rules)
        {
            (int, double) Sleeves(string code) => rules.Fixtures.TryGetValue(code, out var fs) ? (fs.Count, fs.Spacing) : (1, 0);
            foreach (var item in res.Items)
            {
                item.Points = null;
                if (item.Code == null || !rules.Fixtures.ContainsKey(item.Code)) continue;
                var (points, how, back) = FixtureDrains.LocateKnown(item.Code, item.Strokes, res.Plan.Walls, Sleeves(item.Code), 13, res.Plan.Edges);
                if (points == null) { points = new List<(double X, double Y)> { (item.Cx, item.Cy) }; how = "its drawing gives no spot: its centre (check)"; item.Check = true; }
                item.Points = points; item.How = how; item.Back = back;
            }
        }

        /// <summary>The fixture labels of this model's latest Auto Run (risers.json), for Extract Fixtures run on its own.</summary>
        public static List<Label> LabelsFromLastRun(Document doc, PlumbingRules rules)
        {
            var labels = new List<Label>();
            try
            {
                string safe = string.Concat((doc.Title ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "runs", safe);
                if (!Directory.Exists(root)) return labels;
                var file = Directory.GetDirectories(root).OrderByDescending(d => d).Select(d => Path.Combine(d, "risers.json")).FirstOrDefault(File.Exists);
                if (file == null) return labels;
                var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                foreach (var o in json["openings"] ?? new Newtonsoft.Json.Linq.JArray())
                {
                    string tag = (string)o["tag"], level = (string)o["level"];
                    double? x = (double?)o["x"], y = (double?)o["y"];
                    if (tag == null || level == null || x == null || y == null || !rules.Fixtures.ContainsKey(tag)) continue;
                    if (!((o["from"] as Newtonsoft.Json.Linq.JArray)?.Any(f => ((string)f ?? "").Contains("fixture '")) ?? false)) continue;
                    labels.Add(new Label { Level = level, Tag = tag, X = x.Value, Y = y.Value });
                }
                App.Log($"DWG fixture blocks: {labels.Count} plumbing fixture label(s) from {file}");
            }
            catch (Exception ex) { App.Log("DWG fixture blocks: labels of the last run not read: " + ex.Message); }
            return labels;
        }

        /// <summary>
        /// The lines Revit shows that belong to no block fixture (loose lines: showers and tubs drawn line by line), for
        /// the shape reader.
        /// </summary>
        public static List<FixtureDrains.Stroke> Loose(DwgFixtureReader.Plan plan, Result res) =>
            plan.Strokes.Where(s =>
            {
                double cx = (s.X0 + s.X1) / 2, cy = (s.Y0 + s.Y1) / 2;
                return !res.Items.Any(i => cx >= i.X0 - 1 && cx <= i.X1 + 1 && cy >= i.Y0 - 1 && cy <= i.Y1 + 1);
            }).ToList();

        private static double Gap(Item i, double x, double y) =>
            Math.Sqrt(Math.Pow(Math.Max(Math.Max(i.X0 - x, 0), x - i.X1), 2) + Math.Pow(Math.Max(Math.Max(i.Y0 - y, 0), y - i.Y1), 2));

        /// <summary>A file point (units) to model inches at the given feet per unit.</summary>
        private static (double X, double Y) Map(DwgFixtureReader.Plan plan, double feetPerUnit, double x, double y)
        {
            var q = plan.Transform.OfPoint(new XYZ(x * feetPerUnit, y * feetPerUnit, 0));
            return (q.X * 12, q.Y * 12);
        }

        /// <summary>Share of the blocks (up to 80) with at least two of Revit's fixture lines inside their box.</summary>
        private static double Fit(List<DwgFixtureBlocks.Found> blocks, DwgFixtureReader.Plan plan, double feetPerUnit)
        {
            if (plan.Strokes.Count == 0) return 0;
            int n = 0, hit = 0;
            foreach (var b in blocks.Take(80))
            {
                var c = new[] { Map(plan, feetPerUnit, b.X0, b.Y0), Map(plan, feetPerUnit, b.X1, b.Y0), Map(plan, feetPerUnit, b.X0, b.Y1), Map(plan, feetPerUnit, b.X1, b.Y1) };
                double x0 = c.Min(p => p.X) - 2, y0 = c.Min(p => p.Y) - 2, x1 = c.Max(p => p.X) + 2, y1 = c.Max(p => p.Y) + 2;
                n++;
                int inside = 0;
                foreach (var s in plan.Strokes)
                {
                    double cx = (s.X0 + s.X1) / 2, cy = (s.Y0 + s.Y1) / 2;
                    if (cx >= x0 && cx <= x1 && cy >= y0 && cy <= y1 && ++inside >= 2) break;
                }
                if (inside >= 2) hit++;
            }
            return n == 0 ? 0 : (double)hit / n;
        }

        /// <summary>A block's lines as the shape reader's strokes, in model inches.</summary>
        private static List<FixtureDrains.Stroke> Strokes(DwgFixtureBlocks.Found b, DwgFixtureReader.Plan plan, double feetPerUnit)
        {
            var list = new List<FixtureDrains.Stroke>();
            foreach (var l in b.Lines)
            {
                var pts = l.Pts.Select(p => Map(plan, feetPerUnit, p.X, p.Y)).ToList();
                double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y), x1 = pts.Max(p => p.X), y1 = pts.Max(p => p.Y), w = x1 - x0, h = y1 - y0;
                bool diagonal = false;
                if (l.Straight && pts.Count == 2)
                {
                    double dx = pts[1].X - pts[0].X, dy = pts[1].Y - pts[0].Y, len = Math.Sqrt(dx * dx + dy * dy);
                    diagonal = len > 12 && Math.Abs(dx) / len > 0.34 && Math.Abs(dy) / len > 0.34;
                }
                list.Add(new FixtureDrains.Stroke
                {
                    X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Curved = l.Curved, Closed = l.Closed, Diagonal = diagonal, Kitchen = l.Kitchen,
                    Circle = l.Round && Math.Abs(w - h) <= 0.15 * Math.Max(w, h), Group = 1
                });
            }
            return list;
        }
    }
}
