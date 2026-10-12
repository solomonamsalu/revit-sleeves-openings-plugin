using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// A floor as drawings name it, normalised so a drawing title ("5TH FLOOR PLAN", "FIFTH FLOOR", "ROOF PLAN")
    /// and a Revit level name ("5TH FLOOR", "Level 5", "FOURTH FLR. (60.37')") can be matched.
    /// Keys: CELLAR (also BASEMENT, UNDERGROUND), F1..Fn, ROOF, BULKHEAD.
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
        private static readonly Regex Combined = new Regex(@"\b(TO|THRU|THROUGH|AND)\b|&|/", RegexOptions.IgnoreCase);

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
        /// Floor of a single-floor plan title ("MECHANICAL 5TH FLOOR PLAN" -> F5). Null when the text is not a plan
        /// title or names several floors ("2ND TO 6TH FLOOR PLAN", "CELLAR AND 1ST FLOOR PLAN").
        /// </summary>
        public static string FromPlanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title) || !PlanTitle.IsMatch(title)) return null;
            var keys = Find(title);
            if (keys.Count != 1) return null;
            if (Combined.IsMatch(Before(title, PlanTitle))) return null;
            return keys[0];
        }

        private static readonly Regex Range = new Regex(@"\b(\d{1,2})\s*(?:ST|ND|RD|TH)\s*(?:-|–|TO|THRU|THROUGH)\s*(\d{1,2})\s*(?:ST|ND|RD|TH)\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// Floors of a plan title: one floor as <see cref="FromPlanTitle"/>, or a typical-floor range expanded
        /// ("2ND-7TH FLOOR PLAN", "2ND TO 7TH FLOOR PLAN" -> F2..F7: one plan drawn for every floor in it). Empty when the
        /// text is not a plan title or names floors that are not one range ("CELLAR AND 1ST FLOOR PLAN").
        /// </summary>
        public static List<string> FromPlanTitleAll(string title)
        {
            var one = FromPlanTitle(title);
            if (one != null) return new List<string> { one };
            if (string.IsNullOrWhiteSpace(title) || !PlanTitle.IsMatch(title) || Find(title).Count != 2) return new List<string>();
            var m = Range.Match(Before(title, PlanTitle));
            if (!m.Success) return new List<string>();
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            return a < b ? Enumerable.Range(a, b - a + 1).Select(n => "F" + n).ToList() : new List<string>();
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
            foreach (Match m in Regex.Matches(up, @"\b(SUB-?CELLAR|CELLAR|CELLER|BASEMENT|UNDERGROUND)\b")) hits.Add((m.Index, Cellar));
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
