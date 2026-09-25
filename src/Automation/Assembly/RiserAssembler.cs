using System;
using System.Collections.Generic;
using System.Linq;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Legend;

namespace SleevesOpenings.Automation.Assembly
{
    /// <summary>One duct passing through one floor slab: where an opening goes.</summary>
    public class Crossing
    {
        public const string Place = "place", Review = "review", Skip = "skip";

        public string Floor;                 // FloorKey of the slab (the floor whose slab is cut)
        public string Level;                 // Revit level of that floor
        public double X, Y;                  // Revit (feet)
        public string Tag;                   // "KX1", "ERV-SA"; null for dryer ducts
        public string System;                // Exhaust, ERV, DryerExhaust, ...
        public DuctSize Size;                // null when no label gives one (dryer ducts: sized by the rules in Phase 6)
        public int Ducts = 1;                // dryer shafts: ducts side by side
        public bool Roof;                    // the roof slab (roof opening rules)
        public string Status = Place;
        /// <summary>high = both floors show it (UP below + DN or a riser drawn here); medium = one floor's label only.</summary>
        public string Confidence;
        public List<string> From = new List<string>();     // "4TH FLOOR: '12X8 DN 16X8 UP' (UP)"
        public List<string> Notes = new List<string>();

        /// <summary>What the issued PDF says: "same", "PDF size used", "not in the PDF", "not checked".</summary>
        public string Pdf;

        internal List<(RiserLabel L, string Dir, string Plan)> Sources = new List<(RiserLabel, string, string)>();
        internal List<string> Plans = new List<string>();  // floors whose plan gave it
        internal bool HasPosition;

        public string Name => Tag ?? (System == "DryerExhaust" ? "dryer" : "?");

        /// <summary>Angle (radians, Revit plan) of the size's first number; null = no duct outline drawn (first number along X assumed).</summary>
        public double? Rotation;
        /// <summary>Dryer shafts: each duct's centre (Revit feet, [x, y]) as drawn on the plan the label is on.</summary>
        public List<double[]> Points = new List<double[]>();
    }

    /// <summary>Something the drawings show that will not be placed, with the reason.</summary>
    public class AssemblyIssue
    {
        public const string TagMissing = "tag missing", Undefined = "undefined tag", Decision = "needs a decision",
                            NoSize = "no size label", Loose = "bubble not connected", Top = "no floor above", OnlyPdf = "only in the PDF",
                            OnlyDiagram = "only in the riser diagram";
        public string Floor, Tag, Type, Detail;
        public double? X, Y;                 // Revit (feet), when the floor is lined up
    }

    public class RiserAssembly
    {
        public List<Crossing> Crossings = new List<Crossing>();
        public List<AssemblyIssue> Issues = new List<AssemblyIssue>();
        public int NotRisers;                // tagged risers that need no opening (fans, grilles…)

        public IEnumerable<Crossing> ToPlace => Crossings.Where(c => c.Status == Crossing.Place);
    }

    /// <summary>
    /// Phase 5: from the risers on each floor plan to the openings to cut, slab by slab.
    /// Each size label describes a slab crossing at the riser it points at: DN = this floor's slab, UP = the slab of the
    /// floor above (an offset riser goes down at one spot and up at another). The floor above says the same thing with
    /// its own DN label, so every crossing is checked from both sides; a riser symbol drawn there without a label also
    /// confirms it. Crossings that only one side shows, with nothing drawn on the other, are left for review.
    /// Free of the Revit API.
    /// </summary>
    public static class RiserAssembler
    {
        private class Item
        {
            public DwgRiser R;
            public FloorAlignment Fa;
            public (double X, double Y)? At;     // Revit (feet)
            public TagMeaning Meaning;
            public string Review;                // "needs a decision" text
        }

        /// <param name="tolerance">Inches: two labels this close on one slab are the same crossing.</param>
        /// <param name="drift">Inches: the same tag on one slab this close is the same riser drawn a little apart on two plans.</param>
        public static RiserAssembly Run(DwgSheetIndex index, DwgRiserResult risers, AlignmentResult alignment, SleevesOpenings.Automation.Legend.Legend legend,
                                        LegendRules rules, IDictionary<string, string> floorLevels, PdfCheckResult pdf = null,
                                        IList<DuctOutline> outlines = null, double dryerMinSpacing = 8, double tolerance = 3, double drift = 12)
        {
            var result = new RiserAssembly();
            var floors = index.Floors.Select(f => f.Floor).Distinct().OrderBy(FloorKey.Order).ToList();
            string Above(string floor) { int i = floors.IndexOf(floor); return i >= 0 && i + 1 < floors.Count ? floors[i + 1] : null; }
            string Below(string floor) { int i = floors.IndexOf(floor); return i > 0 ? floors[i - 1] : null; }
            double tol = tolerance / 12, far = drift / 12;

            // ---- 1. what each riser is
            var items = new List<Item>();
            foreach (var r in risers.Risers.OrderBy(r => FloorKey.Order(r.Floor)))
            {
                var it = new Item { R = r, Fa = alignment?.For(r.Floor) };
                it.At = it.Fa?.ToRevit(r.X, r.Y);
                if (r.Tag != null) it.Meaning = legend.Meaning(r.Tag, rules);
                else if (!r.Dryer && r.Labels.Count == 0)
                    foreach (var note in r.Notes)
                    {
                        var m = new TagMeaning();
                        SleevesOpenings.Automation.Legend.Legend.Classify(m, note, rules);
                        if (m.Category == TagCategory.Review) { it.Review = $"{m.Review}: '{Short(note)}'"; break; }
                    }
                items.Add(it);
            }
            // a shaft continues through floors: an untagged riser stacked on a "needs a decision" one is the same shaft
            bool spread = true;
            while (spread)
            {
                spread = false;
                foreach (var it in items.Where(i => i.Review == null && Bare(i) && i.At.HasValue))
                {
                    var src = items.FirstOrDefault(o => o.Review != null && o.At.HasValue && (o.R.Floor == Above(it.R.Floor) || o.R.Floor == Below(it.R.Floor)) &&
                                                        Near(o.At.Value, it.At.Value, far));
                    if (src == null) continue;
                    it.Review = $"{src.Review.Split(':')[0]} (same shaft as on the {FloorKey.Describe(src.R.Floor)})";
                    spread = true;
                }
            }

            // ---- 2. crossings from the labels
            var raw = new List<Crossing>();
            foreach (var it in items)
            {
                var r = it.R;
                if (it.Meaning != null && it.Meaning.Category != TagCategory.Opening) continue;   // reported below
                foreach (var label in r.Labels)
                {
                    bool dryer = label.Dryer;
                    string system = dryer ? "DryerExhaust" : it.Meaning?.System;     // null: untagged size label, named by the other floor
                    string tag = dryer ? null : r.Tag;
                    if (r.Tag == null && !dryer && r.Dryer) continue;               // size text inside a dryer shaft: not its own riser
                    var pos = it.Fa?.ToRevit(label.X, label.Y);
                    int ducts = dryer ? r.Symbols.Count(DwgRiserReader.Drawn) : 1;
                    var points = !dryer || it.Fa == null ? new List<double[]>()
                        : r.Symbols.Where(DwgRiserReader.Drawn).Select(sy => it.Fa.ToRevit(sy.X, sy.Y)).Where(q => q.HasValue).Select(q => new[] { q.Value.X, q.Value.Y }).ToList();
                    bool noWord = !label.GoesDown && !label.GoesUp;
                    if (label.Down != null || label.GoesDown)
                    {
                        var c = New(r.Floor, r.Floor, "DN", label, label.Down, tag, system, pos, ducts, noWord);
                        c.Points.AddRange(points); raw.Add(c);
                    }
                    if (label.Up != null || label.GoesUp)
                    {
                        var up = Above(r.Floor);
                        if (up == null) Issue(result, r.Floor, tag, pos, AssemblyIssue.Top, $"'{label.Text}' goes up from the top floor plan");
                        else
                        {
                            var c = New(up, r.Floor, "UP", label, label.Up, tag, system, pos, ducts, noWord);
                            c.Points.AddRange(points); raw.Add(c);
                        }
                    }
                }
            }

            // ---- 3. a tagged riser drawn at the same spot on two floors passes through the upper slab, even with no label
            foreach (var it in items.Where(i => i.R.Tag != null && i.Meaning?.Category == TagCategory.Opening && i.At.HasValue))
            {
                var up = Above(it.R.Floor);
                var there = items.FirstOrDefault(o => o.R.Floor == up && o.R.Tag == it.R.Tag && o.At.HasValue && Near(o.At.Value, it.At.Value, tol * 2));
                if (there == null) continue;
                if (raw.Any(c => c.Floor == up && c.Tag == it.R.Tag && c.HasPosition && Near((c.X, c.Y), there.At.Value, far))) continue;
                var c2 = new Crossing
                {
                    Floor = up, Tag = it.R.Tag, System = it.Meaning.System, X = there.At.Value.X, Y = there.At.Value.Y, HasPosition = true, Status = Crossing.Review
                };
                c2.From.Add($"{it.R.Tag} drawn on the {FloorKey.Describe(it.R.Floor)} and the {FloorKey.Describe(up)} at the same spot");
                c2.Plans.Add(it.R.Floor); c2.Plans.Add(up);
                c2.Notes.Add("no size on either floor's label");
                raw.Add(c2);
            }

            // ---- 4. merge: the same slab and spot (compatible system/tag), then the same tag drawn a little apart
            foreach (var c in raw)
            {
                var same = !c.HasPosition ? null : result.Crossings.FirstOrDefault(o => o.HasPosition && o.Floor == c.Floor && Near((o.X, o.Y), (c.X, c.Y), tol) &&
                                                                                      (o.System == null || c.System == null || o.System == c.System) &&
                                                                                      (o.Tag == null || c.Tag == null || o.Tag == c.Tag));
                if (same == null) result.Crossings.Add(c);
                else Merge(same, c, null);
            }
            bool changed = true;
            while (changed)
            {
                changed = false;
                // the DN and UP halves of one label are one riser: an untagged half takes the tag the other half got
                foreach (var c in result.Crossings.Where(c => c.System == null))
                {
                    var named = result.Crossings.FirstOrDefault(o => o.System != null && o.Tag != null && o.Sources.Select(x => x.L).Intersect(c.Sources.Select(x => x.L)).Any());
                    if (named == null) continue;
                    c.Tag = named.Tag; c.System = named.System; changed = true;
                }
                foreach (var c in result.Crossings.ToList())
                {
                    if (!result.Crossings.Contains(c) || !c.HasPosition || (c.Tag == null && c.System != null)) continue;
                    // the same tag; or an untagged size label next to one tagged riser (the tag is on the other floor)
                    var others = result.Crossings.Where(o => o != c && o.HasPosition && o.Floor == c.Floor && Near((o.X, o.Y), (c.X, c.Y), far) &&
                                                             !o.Plans.Intersect(c.Plans).Any() &&
                                                             (c.Tag != null ? o.Tag == c.Tag && o.System == c.System : o.Tag != null)).ToList();
                    if (others.Count != 1) continue;
                    var other = others[0];
                    // keep the position drawn on the slab's own floor plan
                    var keep = c.Plans.Contains(c.Floor) ? c : other.Plans.Contains(c.Floor) ? other : c;
                    var drop = keep == c ? other : c;
                    double d = Math.Sqrt(Math.Pow(keep.X - drop.X, 2) + Math.Pow(keep.Y - drop.Y, 2)) * 12;
                    Merge(keep, drop, $"the {FloorKey.Describe(drop.Plans[0])} plan shows it {d:0.#}\" away; the {FloorKey.Describe(keep.Plans[0])} position is used");
                    result.Crossings.Remove(drop);
                    changed = true;
                }
            }

            // ---- 5. the issued PDF: its size wins; a label it does not show is left for review
            foreach (var c in result.Crossings.Where(c => c.Sources.Count > 0))
            {
                if (pdf == null) { c.Pdf = "no PDF"; continue; }
                var checks = c.Sources.Select(x => (x.L, x.Dir, C: pdf.For(x.L))).Where(x => x.C != null).ToList();
                if (checks.Count == 0) { c.Pdf = "not checked"; continue; }
                var differs = checks.Where(x => x.C.Status == PdfLabelCheck.Differs).ToList();
                var missing = checks.Where(x => x.C.Status == PdfLabelCheck.Missing).ToList();
                foreach (var d in differs)
                {
                    var size = d.Dir == "DN" ? d.C.Pdf.Parsed.Down : d.C.Pdf.Parsed.Up;
                    c.Notes.Add($"the PDF shows '{d.C.Pdf.Text}' where the DWG has '{d.L.Text}'" + (size != null && (c.Size == null || !SameSize(size, c.Size)) ? $"; PDF size {size} used" : ""));
                    if (size != null) c.Size = size;
                }
                foreach (var m in missing) { c.Notes.Add($"'{m.L.Text}' is not on the PDF plan (removed in the issued set?)"); c.Status = Crossing.Review; }
                c.Pdf = differs.Count > 0 ? "PDF size used" : missing.Count > 0 ? "not in the PDF" :
                        checks.All(x => x.C.Status == PdfLabelCheck.Same) ? "same" : "not checked";
            }
            foreach (var p in pdf?.OnlyInPdf ?? new List<PdfLabel>())
            {
                var at = alignment?.For(p.Floor)?.ToRevit(p.DwgX, p.DwgY);
                Issue(result, p.Floor, null, at, AssemblyIssue.OnlyPdf, $"the PDF plan (page {p.Page}) shows '{p.Text}' with no matching riser label in the DWG; check it");
            }

            // ---- 6. finish each crossing: level, alignment, confidence
            foreach (var c in result.Crossings)
            {
                c.Level = floorLevels != null && floorLevels.TryGetValue(c.Floor, out var lv) ? lv : null;
                c.Roof = FloorKey.Order(c.Floor) >= FloorKey.Order("ROOF");
                var fa = alignment?.For(c.Floor);
                var plans = c.Plans.Distinct().ToList();
                bool fromBelow = plans.Any(p => p != c.Floor), own = plans.Contains(c.Floor);
                var drawnHere = items.Where(i => i.R.Floor == c.Floor && i.Fa != null).SelectMany(i => i.R.Symbols.Select(s => i.Fa.ToRevit(s.X, s.Y)))
                                     .Where(p => p.HasValue).Select(p => p.Value).ToList();
                bool drawn = drawnHere.Any(p => Near(p, (c.X, c.Y), tol * 2));
                c.Confidence = fromBelow && (own || drawn) ? "high" : "medium";
                if (fromBelow && !own)
                {
                    if (drawn) c.Notes.Add($"riser drawn on the {FloorKey.Describe(c.Floor)} plan (no label there)");
                    else
                    {
                        c.Status = Crossing.Review;
                        var near = drawnHere.Select(p => Math.Sqrt(Math.Pow(p.X - c.X, 2) + Math.Pow(p.Y - c.Y, 2)) * 12).Where(d => d <= 24).DefaultIfEmpty().Min();
                        c.Notes.Add($"only the {FloorKey.Describe(plans[0])} plan shows it (UP); nothing drawn here on the {FloorKey.Describe(c.Floor)} plan" +
                                    (near > 0 ? $" (a riser is drawn {near:0}\" away)" : ""));
                    }
                }
                if (own && !fromBelow && Below(c.Floor) != null) c.Notes.Add($"not labelled UP on the {FloorKey.Describe(Below(c.Floor))} plan");

                if (c.System == null)
                {
                    c.Status = Crossing.Skip;
                    Issue(result, c.Floor, null, (c.X, c.Y), AssemblyIssue.TagMissing, $"size label {string.Join(" + ", c.From)} but no tag on either floor");
                    continue;
                }
                if (c.Size == null && c.System != "DryerExhaust" && !c.Notes.Contains("no size on either floor's label"))
                {
                    c.Status = Crossing.Review; c.Notes.Add("no size on either floor's label");
                }
                if (!c.HasPosition || fa == null || !fa.Usable) { c.Status = Crossing.Skip; c.Notes.Add($"{FloorKey.Describe(c.Floor)} position not confirmed (Revit position tab)"); }
                else if (c.Level == null) { c.Status = Crossing.Skip; c.Notes.Add($"{FloorKey.Describe(c.Floor)} is not matched to a Revit level"); }
            }

            // ---- dryer shafts: ducts drawn closer than the sleeve spacing cannot each get a sleeve
            foreach (var c in result.Crossings.Where(c => c.System == "DryerExhaust" && c.Status == Crossing.Place && c.Points.Count > 1))
            {
                double closest = double.MaxValue;
                for (int i = 0; i < c.Points.Count; i++)
                    for (int j = i + 1; j < c.Points.Count; j++)
                        closest = Math.Min(closest, Math.Sqrt(Math.Pow(c.Points[i][0] - c.Points[j][0], 2) + Math.Pow(c.Points[i][1] - c.Points[j][1], 2)) * 12);
                if (closest >= dryerMinSpacing) continue;
                c.Status = Crossing.Review;
                c.Notes.Add($"dryer shaft: {c.Points.Count} ducts drawn {closest:0.#}\" apart (sleeves need {dryerMinSpacing:0.#}\"); one shaft opening or separate sleeves?");
            }

            // ---- 7. which way each rectangular duct runs (its outline on the plan the label is on)
            if (outlines != null)
                foreach (var c in result.Crossings.Where(c => c.Size != null && !c.Size.Diameter.HasValue))
                {
                    foreach (var (label, dir, plan) in c.Sources)
                    {
                        var size = dir == "DN" ? label.Down : label.Up;
                        if (size == null || !SameSize(size, c.Size)) continue;
                        var angle = DuctOutlines.Orientation(outlines, label.X, label.Y, c.Size.Width.Value, c.Size.Length.Value);
                        if (!angle.HasValue) continue;
                        var map = alignment?.For(plan)?.Map;
                        c.Rotation = angle.Value + (map == null ? 0 : Math.Atan2(map.Sin, map.Cos));
                        break;
                    }
                    if (!c.Rotation.HasValue && Math.Abs(c.Size.Width.Value - c.Size.Length.Value) >= 0.5)
                        c.Notes.Add($"duct outline not drawn: {c.Size.Width:0.##}\" side assumed east-west");
                }

            // ---- 8. what will not be placed
            foreach (var it in items)
            {
                var r = it.R;
                if (it.Meaning != null && it.Meaning.Category == TagCategory.Undefined)
                    Issue(result, r.Floor, r.Tag, it.At, AssemblyIssue.Undefined, $"{r.Tag} is not defined in the PDF (abbreviations, symbols, schedules)");
                else if (it.Meaning != null && it.Meaning.Category == TagCategory.Review)
                    Issue(result, r.Floor, r.Tag, it.At, AssemblyIssue.Decision, it.Meaning.Review);
                else if (it.Meaning != null && it.Meaning.Category != TagCategory.Opening)
                    result.NotRisers++;
                else if (Bare(it) && Explained(result, it, far))
                    continue;                           // named by the floor below (a dryer shaft continuing up)
                else if (it.Review != null)
                    Issue(result, r.Floor, null, it.At, AssemblyIssue.Decision, $"{it.Review} ({r.Symbols.Count} duct(s))");
                else if (Bare(it))
                    Issue(result, r.Floor, null, it.At, AssemblyIssue.TagMissing, $"riser drawn ({r.Symbols.Count} duct(s)) with no tag or label here or on the floor below" +
                                                                                (r.Notes.Count > 0 ? $"; note next to it: '{Short(r.Notes[0])}'" : ""));
                else if (r.Tag != null && r.Labels.Count == 0)
                {
                    bool covered = result.Crossings.Any(c => c.Tag == r.Tag && (c.Floor == r.Floor || c.Floor == Above(r.Floor)) && it.At.HasValue && Near((c.X, c.Y), it.At.Value, far));
                    if (!covered) Issue(result, r.Floor, r.Tag, it.At, AssemblyIssue.NoSize, $"{r.Tag} is drawn but has no UP/DN label here or on the floor below");
                }
            }
            foreach (var t in risers.LooseTags)
                Issue(result, t.Floor, t.Tag, alignment?.For(t.Floor)?.ToRevit(t.X, t.Y), AssemblyIssue.Loose, "tag bubble with no line to a riser");

            result.Crossings = result.Crossings.OrderBy(c => FloorKey.Order(c.Floor)).ThenBy(c => c.Tag ?? "~").ThenBy(c => c.X).ToList();
            result.Issues = result.Issues.OrderBy(i => FloorKey.Order(i.Floor)).ThenBy(i => i.Type).ToList();
            return result;
        }

        private static string Short(string text) => text.Length <= 90 ? text : text.Substring(0, 87).TrimEnd() + "...";

        private static bool Bare(Item it) => it.R.Tag == null && !it.R.Dryer && it.R.Labels.Count == 0;

        /// <summary>An untagged riser that a crossing on its floor already covers (a dryer shaft named by the floor below).</summary>
        private static bool Explained(RiserAssembly result, Item it, double far) =>
            it.Fa != null && result.Crossings.Any(c => c.Floor == it.R.Floor && c.HasPosition &&
                                                      it.R.Symbols.Any(s => { var p = it.Fa.ToRevit(s.X, s.Y); return p.HasValue && Near(p.Value, (c.X, c.Y), far); }));

        private static void Merge(Crossing keep, Crossing c, string note)
        {
            keep.From.AddRange(c.From); keep.Sources.AddRange(c.Sources); keep.Plans.AddRange(c.Plans);
            keep.Tag = keep.Tag ?? c.Tag; keep.System = keep.System ?? c.System;
            if (c.Ducts > keep.Ducts) { keep.Ducts = c.Ducts; keep.Points = c.Points; }
            foreach (var n in c.Notes) if (!keep.Notes.Contains(n)) keep.Notes.Add(n);
            if (note != null) keep.Notes.Add(note);
            if (keep.Size == null) { keep.Size = c.Size; if (c.Size != null) keep.Notes.Remove("no size on either floor's label"); }
            else if (c.Size != null && !SameSize(keep.Size, c.Size))
            {
                keep.Notes.Add($"sizes differ: {keep.Size} and {c.Size}; the larger is used");
                if (Area(c.Size) > Area(keep.Size)) keep.Size = c.Size;
                keep.Status = Crossing.Review;
            }
            if (c.Status == Crossing.Review && keep.Size == null) keep.Status = Crossing.Review;
            else if (keep.Size != null && !keep.Notes.Any(n => n.StartsWith("sizes differ"))) keep.Status = Crossing.Place;
        }

        private static Crossing New(string slab, string plan, string dir, RiserLabel label, DuctSize size, string tag, string system,
                                    (double X, double Y)? pos, int ducts, bool noWord)
        {
            var c = new Crossing
            {
                Floor = slab, Tag = tag, System = system, Size = size, Ducts = ducts,
                X = pos?.X ?? 0, Y = pos?.Y ?? 0, HasPosition = pos.HasValue
            };
            c.From.Add($"{FloorKey.Describe(plan)}: '{label.Text}' ({dir})");
            c.Sources.Add((label, dir, plan)); c.Plans.Add(plan);
            if (noWord) c.Notes.Add($"'{label.Text}' has no UP/DN; taken as both");
            if (!pos.HasValue) c.Notes.Add($"{FloorKey.Describe(plan)} is not lined up with Revit");
            return c;
        }

        private static void Issue(RiserAssembly r, string floor, string tag, (double X, double Y)? at, string type, string detail) =>
            r.Issues.Add(new AssemblyIssue { Floor = floor, Tag = tag, X = at?.X, Y = at?.Y, Type = type, Detail = detail });

        private static bool Near((double X, double Y) a, (double X, double Y) b, double d) => Math.Abs(a.X - b.X) <= d && Math.Abs(a.Y - b.Y) <= d;

        private static bool SameSize(DuctSize a, DuctSize b) =>
            a.Diameter.HasValue || b.Diameter.HasValue
                ? a.Diameter == b.Diameter
                : (a.Width == b.Width && a.Length == b.Length) || (a.Width == b.Length && a.Length == b.Width);

        private static double Area(DuctSize s) => s.Diameter.HasValue ? Math.PI * s.Diameter.Value * s.Diameter.Value / 4 : (s.Width ?? 0) * (s.Length ?? 0);
    }
}
