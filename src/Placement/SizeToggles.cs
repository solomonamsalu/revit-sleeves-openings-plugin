using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace SleevesOpenings.Placement
{
    /// <summary>
    /// "Checkbox" sleeve families (the office's MPI - Sleeves Family): every size of every system is pre-drawn
    /// and shown by a Yes/No instance parameter named "&lt;prefix&gt; &lt;size&gt;" ("Storm 6", "Fire 4", "Condensate 3").
    /// Placing a sleeve means switching every such toggle off and exactly one on.
    /// </summary>
    public static class SizeToggles
    {
        public const string DefaultPattern = @"^(?<prefix>[A-Za-z][A-Za-z ]*?)\s+(?<size>\d+(?:\.\d+)?)$";

        public class Toggle
        {
            public Parameter Param;
            public string Prefix;
            public double Size;
        }

        /// <summary>All size toggles on the instance (Yes/No parameters whose name matches the pattern).</summary>
        public static List<Toggle> Find(Element inst, FamilyMapEntry map)
        {
            var rx = new Regex(string.IsNullOrEmpty(map.SizeTogglePattern) ? DefaultPattern : map.SizeTogglePattern,
                               RegexOptions.IgnoreCase);
            var list = new List<Toggle>();
            foreach (Parameter p in inst.Parameters)
            {
                if (p.IsReadOnly || p.StorageType != StorageType.Integer || p.Definition == null) continue;
                var m = rx.Match(p.Definition.Name);
                if (!m.Success) continue;
                if (!double.TryParse(m.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)) continue;
                list.Add(new Toggle { Param = p, Prefix = m.Groups["prefix"].Value.Trim(), Size = size });
            }
            return list;
        }

        /// <summary>Prefix for a system per rules.json, e.g. Storm -> "Storm", Standpipe -> "Fire". Null if not mapped.</summary>
        public static string PrefixFor(FamilyMapEntry map, SystemKind system) =>
            map.SizeToggles != null && map.SizeToggles.TryGetValue(system.ToString(), out var p) && !string.IsNullOrEmpty(p) ? p : null;

        /// <summary>
        /// Switches on the toggle for the system at the requested size (or the next size up that the family has)
        /// and every other toggle off. Returns the size actually shown, or null when the family has no toggle
        /// for this system at all (caller falls back to ordinary parameters / reports it).
        /// </summary>
        public static double? Apply(Element inst, FamilyMapEntry map, SystemKind system, double inches, out string note)
        {
            note = null;
            var prefix = PrefixFor(map, system);
            if (prefix == null) { note = $"rules.json has no size toggle prefix for {system}"; return null; }

            var toggles = Find(inst, map);
            var mine = toggles.Where(t => t.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Size).ToList();
            if (mine.Count == 0) { note = $"family has no '{prefix} <size>' toggles"; return null; }

            var pick = mine.FirstOrDefault(t => Math.Abs(t.Size - inches) < 0.01)
                    ?? mine.FirstOrDefault(t => t.Size > inches);
            if (pick == null)
            {
                pick = mine.Last();
                note = $"{prefix} {Fmt(inches)} not in family; largest available is {Fmt(pick.Size)}";
            }
            else if (Math.Abs(pick.Size - inches) >= 0.01)
                note = $"{prefix} {Fmt(inches)} not in family; using next size up {Fmt(pick.Size)}";

            foreach (var t in toggles) if (t.Param.AsInteger() != 0 && t != pick) t.Param.Set(0);
            if (pick.Param.AsInteger() != 1) pick.Param.Set(1);
            return pick.Size;
        }

        /// <summary>The size currently switched on for the system's prefix, or null if none (audit).</summary>
        public static double? Current(Element inst, FamilyMapEntry map, SystemKind system)
        {
            var prefix = PrefixFor(map, system);
            if (prefix == null) return null;
            var on = Find(inst, map).Where(t => t.Param.AsInteger() == 1).ToList();
            var mine = on.FirstOrDefault(t => t.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            // Another system's toggle is on instead: report it as the wrong size so Final Check flags and fixes it.
            return mine?.Size ?? (on.Count > 0 ? (double?)-1 : null);
        }

        static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "\"";
    }
}
