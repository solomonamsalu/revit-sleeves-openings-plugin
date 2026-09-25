using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Compare
{
    /// <summary>One opening of ours and/or of the office's S&amp;O set, and how they compare.</summary>
    public class SoMatch
    {
        public const string Same = "same", SizeDiffers = "size differs", Moved = "position differs", SizeAndMoved = "size and position differ",
                            InShaft = "inside a shaft opening of the set", OnlyInSet = "only in the S&O set", NotInSet = "not in the S&O set";
        public string Floor;
        public string Status;
        public Crossing Ours;                 // null: only in the set
        public SoOpening Theirs;              // null: not in the set
        public double? Off;                   // inches, centre to centre
        public string OurSize;                // the opening we place (with clearances)
        public string Detail;

        public bool Agrees => Status == Same || Status == InShaft;
    }

    public class SoCompareResult
    {
        public List<SoMatch> Matches = new List<SoMatch>();
        public List<string> Floors = new List<string>();                  // compared
        public List<string> NotCompared = new List<string>();             // "BULKHEAD: grid bubbles not found"

        public int Count(string status) => Matches.Count(m => m.Status == status);

        public string Summary() =>
            Matches.Count == 0 ? "nothing compared" :
            $"{Count(SoMatch.Same)} same, {Count(SoMatch.InShaft)} inside a shaft opening, {Count(SoMatch.SizeDiffers) + Count(SoMatch.SizeAndMoved)} size differs, " +
            $"{Count(SoMatch.Moved) + Count(SoMatch.SizeAndMoved)} position differs, {Count(SoMatch.OnlyInSet)} only in the S&O set, {Count(SoMatch.NotInSet)} not in the S&O set " +
            $"({Floors.Count} floor(s) compared)";
    }

    /// <summary>
    /// Phase 8: the openings from the drawings against the office's own S&amp;O set for the same building, floor by floor.
    /// Each of ours is paired with the nearest opening of the set (within the match radius; a matching tag counts as
    /// closer), then compared on position and size. Ours that sit inside a larger opening of the set (a shaft drawn as one
    /// opening) say so; what is left on either side is "only in the S&amp;O set" / "not in the S&amp;O set". Free of the Revit API.
    /// </summary>
    public static class SoSetCompare
    {
        /// <param name="openingSize">The opening size (inches, with clearances) we place for a crossing; null when not known.</param>
        public static SoCompareResult Run(SoSetResult set, RiserAssembly assembly, Func<Crossing, (double W, double L)?> openingSize, ReferenceSetRules cfg)
        {
            cfg = cfg ?? new ReferenceSetRules();
            var result = new SoCompareResult();
            if (set == null || assembly == null) return result;
            var floors = assembly.Crossings.Select(c => c.Floor).Concat(set.Sheets.Select(s => s.Floor)).Distinct().OrderBy(FloorKey.Order).ToList();
            foreach (var floor in floors)
            {
                var sheets = set.Sheets.Where(s => s.Floor == floor).ToList();
                if (sheets.Count == 0) { if (assembly.Crossings.Any(c => c.Floor == floor && c.Status != Crossing.Skip)) result.NotCompared.Add($"{FloorKey.Describe(floor)}: no sheet in the S&O set"); continue; }
                if (!sheets.Any(s => s.Ok)) { result.NotCompared.Add($"{FloorKey.Describe(floor)}: {sheets[0].Problem}"); continue; }
                result.Floors.Add(floor);
                var ok = new HashSet<int>(sheets.Where(s => s.Ok).Select(s => s.Page));
                var theirs = set.Openings.Where(o => o.Floor == floor && ok.Contains(o.Page)).ToList();
                var ours = assembly.Crossings.Where(c => c.Floor == floor && c.Status != Crossing.Skip).ToList();

                // pairs, best first: distance, less a bonus for the same tag; one of ours inside theirs counts as close
                var pairs = (from c in ours
                             from o in theirs
                             let d = Dist(c, o)
                             let inside = Contains(o, c.X, c.Y, 3)
                             let agrees = TagAgrees(c, o)
                             let conflict = !agrees && o.Tags.Count > 0      // the set names it something else: only a near neighbour counts
                             let score = (inside ? Math.Min(d, cfg.MatchRadius / 2) : d) - (agrees ? cfg.MatchRadius / 2 : 0)
                             where d <= (conflict ? cfg.MatchRadius / 2 : cfg.MatchRadius) || (inside && !conflict)
                             orderby score
                             select (C: c, O: o, D: d)).ToList();
                var usedOurs = new HashSet<Crossing>(); var usedTheirs = new HashSet<SoOpening>();
                foreach (var (c, o, d) in pairs)
                {
                    if (usedOurs.Contains(c) || usedTheirs.Contains(o)) continue;
                    // a shaft of the set holding several of ours: pair only the one it is centred on, the rest are "inside"
                    if (Contains(o, c.X, c.Y, 3) && d > cfg.MatchRadius && ours.Count(x => Contains(o, x.X, x.Y, 3)) > 1) continue;
                    usedOurs.Add(c); usedTheirs.Add(o);
                    result.Matches.Add(Compare(floor, c, o, d, openingSize, cfg));
                }
                // the same tag on the same floor, further apart: the riser moved between the two sets
                var byTag = (from c in ours where !usedOurs.Contains(c)
                             from o in theirs where !usedTheirs.Contains(o) && TagAgrees(c, o)
                             orderby Dist(c, o)
                             select (C: c, O: o)).ToList();
                foreach (var (c, o) in byTag)
                {
                    if (usedOurs.Contains(c) || usedTheirs.Contains(o)) continue;
                    usedOurs.Add(c); usedTheirs.Add(o);
                    var m = Compare(floor, c, o, Dist(c, o), openingSize, cfg);
                    m.Detail = $"same tag {Units.FormatInches(Math.Round(Dist(c, o)))} away ({Direction(o, c)} in ours)" + (m.Detail.Length > 0 ? "; " + m.Detail : "");
                    result.Matches.Add(m);
                }
                foreach (var c in ours.Where(c => !usedOurs.Contains(c)))
                {
                    var host = theirs.Where(o => Contains(o, c.X, c.Y, 3)).OrderBy(o => o.Width * o.Length).FirstOrDefault();
                    var m = new SoMatch { Floor = floor, Ours = c, OurSize = Size(openingSize(c)) };
                    if (host != null)
                    {
                        usedTheirs.Add(host);
                        m.Status = SoMatch.InShaft; m.Theirs = host; m.Off = Dist(c, host);
                        m.Detail = $"the set draws one opening {host.SizeText} ({host.Name}) around it";
                    }
                    else
                    {
                        m.Status = SoMatch.NotInSet;
                        var near = theirs.Select(o => (O: o, D: Dist(c, o))).OrderBy(t => t.D).FirstOrDefault();
                        m.Detail = near.O == null ? "no HVAC opening on this sheet" : $"nearest opening of the set: {near.O.Name} {Units.FormatInches(Math.Round(near.D))} away";
                    }
                    result.Matches.Add(m);
                }
                foreach (var o in theirs.Where(o => !usedTheirs.Contains(o)))
                {
                    var near = ours.Select(c => (C: c, D: Dist(c, o))).OrderBy(t => t.D).FirstOrDefault();
                    result.Matches.Add(new SoMatch
                    {
                        Floor = floor, Status = SoMatch.OnlyInSet, Theirs = o,
                        Detail = $"{o.Name} {o.SizeText}" + (o.Inside != null ? $" (inside {o.Inside.Name})" : "") +
                                 (near.C == null ? "" : $"; nearest of ours: {near.C.Name} {Units.FormatInches(Math.Round(near.D))} away ({near.C.Status})")
                    });
                }
            }
            result.Matches = result.Matches.OrderBy(m => FloorKey.Order(m.Floor)).ThenBy(m => m.Agrees).ThenBy(m => m.Ours?.Tag ?? m.Theirs?.Name).ToList();
            return result;
        }

        private static SoMatch Compare(string floor, Crossing c, SoOpening o, double d, Func<Crossing, (double W, double L)?> openingSize, ReferenceSetRules cfg)
        {
            var size = openingSize(c);
            var m = new SoMatch { Floor = floor, Ours = c, Theirs = o, Off = d, OurSize = Size(size) };
            bool moved = d > cfg.PositionTolerance;
            bool sized = size.HasValue && !SameSize(size.Value, (o.Width, o.Length), cfg.SizeTolerance);
            m.Status = moved && sized ? SoMatch.SizeAndMoved : sized ? SoMatch.SizeDiffers : moved ? SoMatch.Moved : SoMatch.Same;
            var notes = new List<string>();
            if (sized) notes.Add($"ours {m.OurSize}, the set {o.SizeText}");
            if (moved) notes.Add($"{Units.FormatInches(Math.Round(d, 1))} apart");
            if (o.Tags.Count > 0 && !TagAgrees(c, o)) notes.Add($"tagged {o.Name} in the set");
            if (o.Inside != null) notes.Add($"drawn inside {o.Inside.Name} {o.Inside.SizeText}");
            m.Detail = string.Join("; ", notes);
            return m;
        }

        private static string Size((double W, double L)? s) =>
            s == null ? null : $"{Units.FormatInches(Math.Round(s.Value.W, 1))} x {Units.FormatInches(Math.Round(s.Value.L, 1))}";

        private static bool SameSize((double W, double L) a, (double W, double L) b, double tol) =>
            (Math.Abs(a.W - b.W) <= tol && Math.Abs(a.L - b.L) <= tol) || (Math.Abs(a.W - b.L) <= tol && Math.Abs(a.L - b.W) <= tol);

        private static string Direction(SoOpening from, Crossing to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            string ns = Math.Abs(dy) * 12 < 3 ? "" : dy > 0 ? "north" : "south", ew = Math.Abs(dx) * 12 < 3 ? "" : dx > 0 ? "east" : "west";
            return ns.Length > 0 && ew.Length > 0 ? $"{ns}-{ew}" : ns + ew;
        }

        private static double Dist(Crossing c, SoOpening o) => Math.Sqrt(Math.Pow(c.X - o.X, 2) + Math.Pow(c.Y - o.Y, 2)) * 12;

        /// <summary>Revit point (feet) inside the set's opening, grown by <paramref name="margin"/> inches (axis-aligned by its drawn size).</summary>
        private static bool Contains(SoOpening o, double x, double y, double margin) =>
            Math.Abs(x - o.X) * 12 <= o.Width / 2 + margin && Math.Abs(y - o.Y) * 12 <= o.Length / 2 + margin;

        /// <summary>The name part of a tag: "ERV-SA" → "ERV", "GX-1" → "GX1", "KX1" → "KX1".</summary>
        private static string Head(string t)
        {
            t = (t ?? "").ToUpperInvariant().Trim();
            var m = Regex.Match(t, @"^([A-Z]+)-?(\d+)");
            return m.Success ? m.Groups[1].Value + m.Groups[2].Value : Regex.Match(t, "^[A-Z]+").Value;
        }

        private static bool TagAgrees(Crossing c, SoOpening o)
        {
            var ours = Head(c.Tag ?? (c.System == "DryerExhaust" ? "DRYER" : null));
            return ours.Length > 0 && o.Tags.Any(t => Head(t) == ours);
        }
    }
}
