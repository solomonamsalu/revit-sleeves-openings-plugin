using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// A floor as drawings name it, normalised so a drawing title ("5TH FLOOR PLAN", "FIFTH FLOOR", "ROOF PLAN")
    /// and a Revit level name ("5TH FLOOR", "Level 5", "FOURTH FLR. (60.37')") can be matched.
    /// Keys: CELLAR, F1..Fn, ROOF, BULKHEAD.
    /// </summary>
    public static class FloorKey
    {
        public const string Cellar = "CELLAR", Roof = "ROOF", Bulkhead = "BULKHEAD";

        private static readonly string[] Words =
        {
            "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH", "EIGHTH", "NINTH", "TENTH",
            "ELEVENTH", "TWELFTH", "THIRTEENTH", "FOURTEENTH", "FIFTEENTH", "SIXTEENTH", "SEVENTEENTH", "EIGHTEENTH", "NINETEENTH", "TWENTIETH"
        };

        private static readonly Regex Ordinal = new Regex(@"\b(\d{1,2})\s*-?\s*(ST|ND|RD|TH)\b", RegexOptions.IgnoreCase);
        private static readonly Regex PlanTitle = new Regex(@"\b(FLOOR\s+)?PLAN\b", RegexOptions.IgnoreCase);
        /// <summary>A range of floors drawn once ("2ND TO 7TH FLOOR PLAN", "2ND-7TH"): every floor between the two is on it.</summary>
        private static readonly Regex Range = new Regex(@"\b(TO|THRU|THROUGH)\b|(?<=\d(ST|ND|RD|TH)\s?)-", RegexOptions.IgnoreCase);

        /// <summary>"F5" -> "5TH FLOOR", "ROOF" -> "ROOF" (for display).</summary>
        public static string Describe(string key)
        {
            if (key == null) return "?";
            if (!key.StartsWith("F") || !int.TryParse(key.Substring(1), out int n)) return key;
            string suffix = n % 100 >= 11 && n % 100 <= 13 ? "TH" : (n % 10) switch { 1 => "ST", 2 => "ND", 3 => "RD", _ => "TH" };
            return $"{n}{suffix} FLOOR";
        }

        /// <summary>Sort order: cellar, floors ascending, roof, bulkhead.</summary>
        public static int Order(string key)
        {
            if (key == Cellar) return -1;
            if (key == Roof) return 1000;
            if (key == Bulkhead) return 1001;
            return key != null && key.StartsWith("F") && int.TryParse(key.Substring(1), out int n) ? n : 999;
        }

        /// <summary>
        /// Every floor a plan title covers, lowest first: one floor ("MECHANICAL 5TH FLOOR PLAN" -> F5), a range of
        /// typical floors drawn once ("2ND TO 7TH FLOOR PLAN" -> F2..F7) or a list ("CELLAR AND 1ST FLOOR PLAN").
        /// Empty when the text is not a plan title or names no floor. A title naming one floor beside something that
        /// is not a floor ("CELLAR/FOUNDATION FLOOR PLAN") is that floor.
        /// </summary>
        public static List<string> PlanFloors(string title)
        {
            if (string.IsNullOrWhiteSpace(title) || !PlanTitle.IsMatch(title)) return new List<string>();
            var keys = Find(title);
            if (keys.Count <= 1) return keys;
            return Range.IsMatch(Before(title, PlanTitle)) ? Between(keys) : keys;
        }

        /// <summary>
        /// Floor of a single-floor plan title. Null when the text is not a plan title or covers several floors
        /// (<see cref="PlanFloors"/> gives them all).
        /// </summary>
        public static string FromPlanTitle(string title)
        {
            var floors = PlanFloors(title);
            return floors.Count == 1 ? floors[0] : null;
        }

        /// <summary>The floors from the lowest named to the highest, for a range title. The named ones when they cannot be counted through (a roof or bulkhead end).</summary>
        private static List<string> Between(List<string> keys)
        {
            int lo = keys.Min(Order), hi = keys.Max(Order);
            if (lo == 999 || hi >= 999) return keys;                  // unknown, roof or bulkhead: nothing to count through
            var all = new List<string>();
            for (int n = lo; n <= hi; n++)
            {
                if (n == -1) all.Add(Cellar);
                else if (n >= 1) all.Add("F" + n);                    // no key has order 0
            }
            return all.Count >= keys.Count ? all : keys;
        }

        /// <summary>Floor named anywhere in a level name. Null when none or ambiguous.</summary>
        public static string FromLevelName(string name)
        {
            var keys = Find(name ?? "");
            return keys.Count == 1 ? keys[0] : null;
        }

        /// <summary>Every floor mentioned in the text, in order, without duplicates.</summary>
        public static List<string> Find(string text)
        {
            var hits = new List<(int pos, string key)>();
            string up = text.ToUpperInvariant();

            foreach (Match m in Ordinal.Matches(up)) hits.Add((m.Index, "F" + int.Parse(m.Groups[1].Value)));
            for (int i = 0; i < Words.Length; i++)
                foreach (Match m in Regex.Matches(up, $@"\b{Words[i]}\b")) hits.Add((m.Index, "F" + (i + 1)));
            foreach (Match m in Regex.Matches(up, @"\b(SUB-?CELLAR|CELLAR|CELLER|BASEMENT)\b")) hits.Add((m.Index, Cellar));
            foreach (Match m in Regex.Matches(up, @"\b(BULKHEAD|BULK HEAD|PENTHOUSE)\b")) hits.Add((m.Index, Bulkhead));
            foreach (Match m in Regex.Matches(up, @"\bROO?F\b")) hits.Add((m.Index, Roof));
            // "LEVEL 5", "L5", "FLOOR 5"
            foreach (Match m in Regex.Matches(up, @"\b(?:LEVEL|LVL|FLOOR|FLR|L)\s*-?\s*(\d{1,2})\b")) hits.Add((m.Index, "F" + int.Parse(m.Groups[1].Value)));

            return hits.OrderBy(h => h.pos).Select(h => h.key).Distinct().ToList();
        }

        private static string Before(string text, Regex rx)
        {
            var m = rx.Match(text);
            return m.Success ? text.Substring(0, m.Index) : text;
        }
    }
}
