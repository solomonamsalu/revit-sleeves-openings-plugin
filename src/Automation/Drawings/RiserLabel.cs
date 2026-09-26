using System.Globalization;
using System.Text.RegularExpressions;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>Duct or pipe size read from a label: width x length, or a diameter.</summary>
    public class DuctSize
    {
        public double? Width, Length, Diameter;    // inches
        public override string ToString() =>
            Diameter.HasValue ? $"{Diameter:0.##}\"Ø" : $"{Width:0.##}X{Length:0.##}";
    }

    /// <summary>
    /// A riser label such as "14X10 DN 18X10 UP", "12X8 DN", "16X8 UP", "12X6 DN &amp; UP", "20X10", "DRYER EXHAUST UP",
    /// or a plain note such as "4" DRYER EXHAUST CONNECT TO THE DRYERS". DN = the riser continues down from this floor
    /// (this floor's slab gets the DN size); UP = it continues up (the floor above gets the UP size). A size with neither
    /// word applies to both. What the label means (which system) is decided from <see cref="Text"/> by the legend rules.
    /// </summary>
    public class RiserLabel
    {
        public string Text;
        public DuctSize Down, Up;
        public bool GoesDown, GoesUp;

        /// <summary>
        /// A bare inch size in a note ("4" DRYER EXHAUST…"), when the text has no WxL / Ø size. Not applied to Down/Up:
        /// it is only a size when the text names a service that gets an opening, which the caller decides.
        /// </summary>
        public DuctSize Nominal;

        /// <summary>The text says more than sizes and UP/DN ("DRYER EXHAUST UP", not "5%%C UP"): it names what the riser is.</summary>
        public bool Descriptive => Regex.IsMatch(Filler.Replace(Direction.Replace(Size.Replace(Text ?? "", " "), " "), " "), "[A-Z]{2,}");
        private static readonly Regex Filler = new Regex(@"\bAND\b|%%C|\bDIA\b", RegexOptions.IgnoreCase);

        private static readonly Regex Size = new Regex(@"(?<w>\d{1,2}(?:\.\d+)?)\s*[X×]\s*(?<l>\d{1,2}(?:\.\d+)?)|(?<d>\d{1,2}(?:\.\d+)?)\s*(?:""|''|IN\b)?\s*(?:Ø|%%C|DIA\b)", RegexOptions.IgnoreCase);
        private static readonly Regex Direction = new Regex(@"\b(DN|DOWN|UP)\b", RegexOptions.IgnoreCase);
        private static readonly Regex BareInches = new Regex(@"(?<![\d.'-])(?<d>\d{1,2}(?:\.\d+)?)\s*(?:""|'')(?!\s*-)");

        /// <summary>Null only for empty text; any other text is kept so its words can be classified.</summary>
        public static RiserLabel Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = Regex.Replace(text.Replace("\\P", " ").Replace('\n', ' '), @"\s+", " ").Trim().ToUpperInvariant();
            if (t.Length == 0) return null;
            var label = new RiserLabel { Text = t };

            // Walk the text: each size is followed by the direction word(s) it belongs to.
            DuctSize pending = null;
            int pos = 0;
            while (pos < t.Length)
            {
                var ms = Size.Match(t, pos);
                var md = Direction.Match(t, pos);
                if (!ms.Success && !md.Success) break;
                if (ms.Success && (!md.Success || ms.Index < md.Index))
                {
                    if (pending != null) { label.Down = label.Down ?? pending; label.Up = label.Up ?? pending; }
                    pending = ms.Groups["d"].Success
                        ? new DuctSize { Diameter = Num(ms.Groups["d"].Value) }
                        : new DuctSize { Width = Num(ms.Groups["w"].Value), Length = Num(ms.Groups["l"].Value) };
                    pos = ms.Index + ms.Length;
                }
                else
                {
                    bool down = md.Value != "UP";
                    if (down) { label.GoesDown = true; if (pending != null) label.Down = pending; }
                    else { label.GoesUp = true; if (pending != null) label.Up = pending; }
                    // "12X6 DN & UP": the same size both ways
                    if (!Regex.IsMatch(t.Substring(md.Index + md.Length), @"^\s*(&|AND)\s*(DN|DOWN|UP)\b")) pending = null;
                    pos = md.Index + md.Length;
                }
            }
            if (pending != null) { label.Down = label.Down ?? pending; label.Up = label.Up ?? pending; }
            if (label.Down == null && label.Up == null)
            {
                var bare = BareInches.Match(t);
                if (bare.Success) label.Nominal = new DuctSize { Diameter = Num(bare.Groups["d"].Value) };
            }
            return label;
        }

        private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    }
}
