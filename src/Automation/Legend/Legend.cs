using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SleevesOpenings.Automation.Legend
{
    public enum TagCategory { Opening, Ignore, Other, Undefined, Review }

    /// <summary>One tag and what the drawings say it means.</summary>
    public class LegendEntry
    {
        public string Tag;            // "MD", "EF-1", "KX"
        public string Definition;     // "CLASS I MOTORIZED DAMPER W/ ACCESS DOOR"
        public string Source;         // "abbreviations", "symbols", "schedule: FAN SCHEDULE", "office"
        public int Page;              // 0 for office entries

        public override string ToString() => $"{Tag} = {Definition} ({Source}{(Page > 0 ? " p" + Page : "")})";
    }

    /// <summary>What a tag means for placement.</summary>
    public class TagMeaning
    {
        public string Tag;
        public LegendEntry Entry;     // null when the tag is defined nowhere
        public TagCategory Category;
        public string System;         // for Opening
        public bool LabelOnHost;      // FSD/GD: no opening, tag goes on the host duct opening's label
        public string Review;         // Review: why it waits for a decision

        public string Describe() =>
            Category == TagCategory.Opening ? $"opening ({System})" :
            Category == TagCategory.Ignore ? (LabelOnHost ? "no opening (label on the duct opening)" : "no opening") :
            Category == TagCategory.Undefined ? "not defined in the drawings" :
            Category == TagCategory.Review ? Review + " - reported, not placed" : "not a riser";
    }

    /// <summary>
    /// Tag definitions read from an engineer's PDF (abbreviations, symbols, schedules) plus the office-fixed tags.
    /// Lookup: office-fixed prefix (KX, TX) → exact tag (schedules, then symbols, then abbreviations) → prefix of a numbered tag
    /// (MD-3 → MD) in symbols, then abbreviations.
    /// </summary>
    public class Legend
    {
        public List<LegendEntry> Entries { get; } = new List<LegendEntry>();

        private static readonly Regex Prefix = new Regex(@"^([A-Z]+)", RegexOptions.IgnoreCase);

        public void Add(string tag, string definition, string source, int page)
        {
            tag = tag?.Trim().ToUpperInvariant();
            definition = Regex.Replace(definition ?? "", @"\s+", " ").Trim();
            if (string.IsNullOrEmpty(tag) || definition.Length == 0) return;
            if (Entries.Any(e => e.Tag == tag && e.Source == source)) return;
            Entries.Add(new LegendEntry { Tag = tag, Definition = definition, Source = source, Page = page });
        }

        public LegendEntry Find(string tag, LegendRules rules)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            tag = tag.Trim().ToUpperInvariant();
            string prefix = Prefix.Match(tag).Groups[1].Value;

            if (rules?.OfficeFixed != null)
            {
                var office = rules.OfficeFixed.FirstOrDefault(kv => string.Equals(kv.Key, prefix, StringComparison.OrdinalIgnoreCase));
                if (office.Key != null) return new LegendEntry { Tag = office.Key, Definition = office.Value, Source = "office" };
            }
            return Entries.FirstOrDefault(e => e.Tag == tag && e.Source.StartsWith("schedule"))
                ?? Entries.FirstOrDefault(e => e.Tag == tag && e.Source == "symbols")
                ?? Entries.FirstOrDefault(e => e.Tag == tag)
                ?? Entries.FirstOrDefault(e => e.Tag == prefix && e.Source == "symbols")
                ?? Entries.FirstOrDefault(e => e.Tag == prefix && e.Source.StartsWith("schedule"))
                ?? Entries.FirstOrDefault(e => e.Tag == prefix)
                ?? SamePrefix(prefix);
        }

        /// <summary>ERV-SA / GX-2 are not defined, but ERV-1 / GX-1 are (schedules): borrow that meaning and say so.</summary>
        private LegendEntry SamePrefix(string prefix)
        {
            var e = Entries.FirstOrDefault(x => x.Source.StartsWith("schedule") && Prefix.Match(x.Tag).Groups[1].Value == prefix && x.Tag.Length > prefix.Length);
            return e == null ? null : new LegendEntry { Tag = e.Tag, Definition = e.Definition, Source = $"same prefix as {e.Tag} ({e.Source})", Page = e.Page };
        }

        public TagMeaning Meaning(string tag, LegendRules rules)
        {
            var m = new TagMeaning { Tag = tag, Entry = Find(tag, rules) };
            if (m.Entry == null) { m.Category = TagCategory.Undefined; return m; }
            Classify(m, m.Entry.Definition, rules);
            return m;
        }

        public static void Classify(TagMeaning m, string definition, LegendRules rules)
        {
            m.Category = TagCategory.Other;
            foreach (var c in rules?.Categories ?? new List<LegendCategory>())
            {
                if (string.IsNullOrEmpty(c.Match) || !Regex.IsMatch(definition, c.Match, RegexOptions.IgnoreCase)) continue;
                if (!string.IsNullOrEmpty(c.Review)) { m.Category = TagCategory.Review; m.Review = c.Review; }
                else if (c.Ignore) { m.Category = TagCategory.Ignore; m.LabelOnHost = c.LabelOnHost; }
                else if (!string.IsNullOrEmpty(c.System)) { m.Category = TagCategory.Opening; m.System = c.System; }
                return;
            }
        }
    }
}
