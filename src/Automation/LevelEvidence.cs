using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Which floor each Revit level is, from what the model holds on it rather than the level's name (FloorSequence
    /// weighs it with the rest). Four independent sources, each a vote per level:
    ///   rooms   - room / space numbers on the level, in this model and every loaded link (501, 502, 5A -> 5th floor),
    ///             used only when the building follows that convention (the floors they give rise with the levels);
    ///   links   - linked models' levels at the same height (the architect names levels even when the MEP model does not);
    ///   xrefs   - per-floor DWGs placed on the level ("05.5-TH FLOOR.dwg");
    ///   views   - floor plan views of the level and the sheets they are on ("5TH FLOOR PLAN"), unless they only repeat the level name.
    /// A level's floor is the one most sources agree on; a tie between different floors gives nothing. Read-only; Revit's thread.
    /// </summary>
    public static class LevelEvidence
    {
        public class Result
        {
            /// <summary>Host level name -> the floor its contents say.</summary>
            public Dictionary<string, FloorSequence.LevelHint> Hints = new Dictionary<string, FloorSequence.LevelHint>(StringComparer.OrdinalIgnoreCase);
            public List<string> Notes = new List<string>();
        }

        private const string Rooms = "rooms", Links = "linked levels", Xrefs = "xrefs", Views = "views";
        private const double LinkTol = 0.5, RoomTol = 1.0;         // feet between a linked level / a room's level and the host level

        private class Vote { public string Kind, Floor, Why; }

        public static Result Collect(Document doc, LevelMap levels, IEnumerable<ReferenceDrawing> modelRefs)
        {
            var result = new Result();
            var real = levels.All;
            var votes = real.ToDictionary(l => l.Name, l => new List<Vote>(), StringComparer.OrdinalIgnoreCase);
            ClassifiedLevel Nearest(double z, double tol) =>
                real.Where(l => Math.Abs(l.Elevation - z) <= tol).OrderBy(l => Math.Abs(l.Elevation - z)).FirstOrDefault();

            var sources = new List<(Document Doc, Transform T, string Name)> { (doc, Transform.Identity, null) };
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document ldoc;
                try { ldoc = link.GetLinkDocument(); } catch { continue; }
                if (ldoc != null && !sources.Any(s => s.Name == ldoc.Title)) sources.Add((ldoc, link.GetTotalTransform(), ldoc.Title));
            }
            double HostZ(Transform t, double z) => t.OfPoint(new XYZ(0, 0, z)).Z;

            // ---- linked models' levels
            foreach (var (ldoc, t, name) in sources.Where(s => s.Name != null))
                foreach (var lv in new FilteredElementCollector(ldoc).OfClass(typeof(Level)).Cast<Level>())
                {
                    string key = FloorKey.FromLevelName(lv.Name);
                    double z = HostZ(t, lv.ProjectElevation);
                    var host = key == null ? null : Nearest(z, LinkTol);
                    if (host != null) votes[host.Name].Add(new Vote { Kind = Links, Floor = key, Why = $"level '{lv.Name}' in {name}" });
                    else
                    {
                        var near = real.OrderBy(l => Math.Abs(l.Elevation - z)).FirstOrDefault();
                        result.Notes.Add($"{name}: level '{lv.Name}' ({(key == null ? "no floor in its name" : FloorKey.Describe(key))}) at {z:0.##}' not used" +
                                         (near == null ? "" : $": nearest level {near.Name} is {Math.Round(z - near.Elevation, 2) + 0:+0.##;-0.##;0}' away ({LinkTol}' allowed)"));
                    }
                }

            // ---- room / space numbers, kept only when the building numbers its units by floor
            var rooms = real.ToDictionary(l => l.Name, l => new List<(int Floor, string Number)>(), StringComparer.OrdinalIgnoreCase);
            foreach (var (sdoc, t, name) in sources)
                foreach (var cat in new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces })
                    foreach (var se in new FilteredElementCollector(sdoc).OfCategory(cat).WhereElementIsNotElementType().OfType<SpatialElement>())
                    {
                        if (se.Area <= 0 || se.Level == null) continue;              // not placed
                        int? floor = UnitFloor(se.Number) ?? UnitFloor(se.Name);
                        var host = floor == null ? null : Nearest(HostZ(t, se.Level.ProjectElevation), RoomTol);
                        if (host != null) rooms[host.Name].Add((floor.Value, se.Number));
                    }
            var byRooms = new List<(ClassifiedLevel Level, int Floor, List<string> Numbers)>();
            foreach (var l in real)
            {
                var list = rooms[l.Name];
                if (list.Count < 2) continue;
                var top = list.GroupBy(r => r.Floor).OrderByDescending(g => g.Count()).First();
                if (top.Count() >= 2 && top.Count() >= 0.6 * list.Count)
                    byRooms.Add((l, top.Key, top.Select(r => r.Number).Distinct().OrderBy(n => n).ToList()));
            }
            int rising = 0;
            for (int i = 1; i < byRooms.Count; i++) if (byRooms[i].Floor > byRooms[i - 1].Floor) rising++;
            if (byRooms.Count >= 2 && rising >= 0.8 * (byRooms.Count - 1))
                foreach (var (l, floor, numbers) in byRooms)
                    votes[l.Name].Add(new Vote { Kind = Rooms, Floor = floor == 0 ? FloorKey.Cellar : "F" + floor,
                                                 Why = $"rooms {string.Join(", ", numbers.Take(4))}{(numbers.Count > 4 ? ", …" : "")}" });
            else if (byRooms.Count >= 2)
                result.Notes.Add($"Room numbers not used: they do not rise floor by floor ({string.Join(", ", byRooms.Select(r => $"{r.Level.Name}: {r.Floor}"))}).");

            // ---- per-floor DWGs placed on a level
            foreach (var r in modelRefs ?? Enumerable.Empty<ReferenceDrawing>())
                if (r.Floor != null && r.Level != null && votes.ContainsKey(r.Level))
                    votes[r.Level].Add(new Vote { Kind = Xrefs, Floor = r.Floor, Why = $"'{r.Name}' placed on it" });

            // ---- plan views of the level and their sheets, when they say more than the level name
            var sheetOf = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                .GroupBy(v => v.ViewId).ToDictionary(g => g.Key, g => doc.GetElement(g.First().SheetId) as ViewSheet);
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
            {
                if (v.IsTemplate || v.GenLevel == null || !votes.ContainsKey(v.GenLevel.Name)) continue;
                var names = new List<string> { v.Name };
                if (sheetOf.TryGetValue(v.Id, out var sheet) && sheet != null) names.Add(sheet.Name);
                foreach (var n in names)
                {
                    if (Echo(n, v.GenLevel.Name)) continue;
                    string key = FloorKey.FromLevelName(n);
                    if (key != null) votes[v.GenLevel.Name].Add(new Vote { Kind = Views, Floor = key, Why = $"'{n}'" });
                }
            }

            // ---- one vote per kind (its majority), then the floor most kinds agree on
            foreach (var l in real)
            {
                var kinds = votes[l.Name].GroupBy(v => v.Kind)
                    .Select(g =>
                    {
                        var floors = g.GroupBy(v => v.Floor).OrderByDescending(f => f.Count()).ToList();
                        bool clear = floors.Count == 1 || floors[0].Count() > floors[1].Count();
                        return clear ? (Kind: g.Key, Floor: floors[0].Key, Why: $"{g.Key}: {string.Join(", ", floors[0].Select(v => v.Why).Distinct().Take(3))}") : default;
                    })
                    .Where(k => k.Kind != null).ToList();
                if (kinds.Count == 0) continue;
                var ranked = kinds.GroupBy(k => k.Floor).OrderByDescending(g => g.Count()).ToList();
                int agree = ranked[0].Count(), against = kinds.Count - agree;
                if (ranked.Count > 1 && ranked[1].Count() == agree)
                {
                    result.Notes.Add($"{l.Name}: the model disagrees on its floor ({string.Join("; ", kinds.Select(k => $"{k.Why} -> {FloorKey.Describe(k.Floor)}"))}).");
                    continue;
                }
                var hint = new FloorSequence.LevelHint { Floor = ranked[0].Key, Sources = Math.Max(1, agree - against) };
                hint.Why.AddRange(ranked[0].Select(k => k.Why));
                if (against > 0) hint.Why.Add($"{against} other source(s) disagree");
                result.Hints[l.Name] = hint;
            }
            return result;
        }

        /// <summary>
        /// Floor of a unit number: "501", "1201", "502A" -> 5 / 12 / 5; "5A", "12-B" -> 5 / 12; "APT 5A" too. Null for
        /// anything else (corridors, stairs, "C1"). 0 is the cellar ("001", "0A").
        /// </summary>
        public static int? UnitFloor(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = Regex.Replace(text.Trim().ToUpperInvariant(), @"^(APT|APARTMENT|UNIT)\.?\s*#?\s*", "");
            var m = Regex.Match(t, @"^(\d{1,2})(\d{2})[A-Z]?$");
            if (!m.Success) m = Regex.Match(t, @"^(\d{1,2})\s*-?\s*[A-Z]{1,2}$");
            return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
        }

        /// <summary>The view / sheet name only repeats the level's name ("06.6-TH FLOOR Sleeves"): no new evidence.</summary>
        private static bool Echo(string name, string level)
        {
            string Norm(string s) => Regex.Replace(s ?? "", @"[^A-Z0-9]", "", RegexOptions.IgnoreCase).ToUpperInvariant();
            string l = Norm(level);
            return l.Length > 0 && Norm(name).Contains(l);
        }
    }
}
