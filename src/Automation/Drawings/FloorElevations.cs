using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A line of drawing text with its font size and position on the page (PDF points, Y up).</summary>
    public class TextLine
    {
        public string Text;
        public double Size, X, Y, Right;
    }

    /// <summary>
    /// The elevation a floor plan prints for its own floor: the architect's level datum tag ("SECOND FLR. (37.78')" over
    /// "+16'-7\"", "SIXTH FLOOR 83.20'"), a finished-floor note ("F.F.E. +28'-4\"", "T.O. SLAB 37'-8\"") or an elevation in
    /// the plan title ("3RD FLOOR PLAN  EL. +24'-0\""). Every datum printed for the floor is kept (absolute and relative):
    /// which one the Revit levels follow is worked out when the floors are matched (FloorSequence). Spot elevations
    /// (terrace, pit, landing, invert, top of steel) and tags naming another floor are not the floor's. Mechanical and
    /// plumbing sets rarely print any; architectural sets usually do.
    /// </summary>
    public static class FloorElevations
    {
        /// <summary>A page with more different values than this is a section or a site plan, not a floor's datum.</summary>
        private const int MaxValues = 4;

        // 14'-6"  +14'-6 1/2"  -6'-7"  ±0'-0"
        private static readonly Regex FeetInch = new Regex(
            @"(?<sign>[+\-±]\s*)?(?<!\d)(?<ft>\d{1,4})\s*'\s*-?\s*(?<in>\d{1,2})(?:\s+(?<num>\d{1,2})/(?<den>\d{1,2}))?\s*""");
        // 37.78'  (14.62')  +83.20'
        private static readonly Regex DecimalFeet = new Regex(@"(?<sign>[+\-±]\s*)?(?<!\d)(?<ft>\d{1,4}\.\d{1,3})\s*'");

        private static readonly Regex Spot = new Regex(
            @"TERRACE|STAIR|LANDING|\bPIT\b|PARAPET|STEEL|\bSTL\b|T\.?\s*O\.?\s*W\b|\bB\.?\s*O\.?\b|\bINV|SILL|\bHEAD\b|CEILING|\bCLG\b|GRADE|SIDEWALK|CURB|" +
            @"\bLOT\b|BUILDING|RAMP|BEAM|FOOTING|SCALE|MEZZ|BALCONY|PLATFORM|BULKHEAD ROOF|ROOF DECK|TOP OF|" +
            @"\d\s*/\s*\d+\s*""?\s*=|=\s*1\s*'",                                // a scale without the word: 1/4"=1'-0"
            RegexOptions.IgnoreCase);
        private static readonly Regex Datum = new Regex(
            @"\bF\.?\s*F\.?\s*E\b|FIN(ISHED|\.)?\s*FL(OO)?R|\bT\.?\s*O\.?\s*S\b\.?|T\.?\s*O\.?\s*SLAB|\bSLAB\s+EL", RegexOptions.IgnoreCase);
        private static readonly Regex ElLabel = new Regex(@"\bEL(EV)?\b\.?|\bELEVATION\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// Elevations (feet) printed on a plan page for its floor; empty when none is printed or the page has too many
        /// to tell which is the floor's. <paramref name="title"/> is the plan title line (null when unknown).
        /// </summary>
        /// <param name="namedOnly">A typical plan (one page for several floors): only tags naming this floor count; a
        /// finished-floor note or an EL under the title is any of them.</param>
        public static List<double> FromPage(IList<TextLine> lines, string floor, TextLine title, bool namedOnly = false)
        {
            var found = new List<double>();
            if (floor == null) return found;
            foreach (var line in lines)
            {
                var values = Values(line.Text);
                if (values.Count == 0 || !IsFloorTag(line, floor, title, namedOnly)) continue;
                found.AddRange(values);
                // The tag's second datum sits right above or below it, alone: "SECOND FLR. (37.78')" / "+16'-7\""
                var partner = lines.Where(o => o != line && Math.Abs(o.Y - line.Y) <= 2.2 * line.Size && Math.Abs(o.X - line.X) <= 4 * line.Size && Alone(o.Text))
                                   .OrderBy(o => Math.Abs(o.Y - line.Y)).FirstOrDefault();
                if (partner != null) found.AddRange(Values(partner.Text));
            }

            var distinct = new List<double>();
            foreach (var v in found)
                if (!distinct.Any(d => Math.Abs(d - v) < 0.05)) distinct.Add(v);
            return distinct.Count > MaxValues ? new List<double>() : distinct;
        }

        /// <summary>Every elevation-like value in the text (feet). A bare dimension ("10'-0\"") counts only with a sign: callers decide by the label.</summary>
        public static List<double> Values(string text)
        {
            var list = new List<double>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in DecimalFeet.Matches(text)) list.Add(Signed(m, double.Parse(m.Groups["ft"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            foreach (Match m in FeetInch.Matches(text))
            {
                double inches = int.Parse(m.Groups["in"].Value);
                if (m.Groups["num"].Success && int.Parse(m.Groups["den"].Value) > 0) inches += double.Parse(m.Groups["num"].Value) / double.Parse(m.Groups["den"].Value);
                if (inches >= 12) continue;
                list.Add(Signed(m, int.Parse(m.Groups["ft"].Value) + inches / 12));
            }
            return list;
        }

        /// <summary>"+14'-6\"" -> 14.5; null when the text is not one elevation.</summary>
        public static double? Parse(string text)
        {
            var v = Values(text);
            return v.Count == 1 ? v[0] : (double?)null;
        }

        private static double Signed(Match m, double value) =>
            m.Groups["sign"].Success && m.Groups["sign"].Value.Trim() == "-" ? -value : value;

        /// <summary>
        /// The line is this floor's datum: it names the floor ("SIXTH FLOOR 83.20'"), or says finished floor / top of slab,
        /// or is in or right under the plan title with an EL label or a sign. Spot elevations and tags of other floors are not.
        /// </summary>
        private static bool IsFloorTag(TextLine line, string floor, TextLine title, bool namedOnly)
        {
            string text = line.Text;
            if (Spot.IsMatch(text)) return false;
            // values out first: "SIXTH FLOOR 83.20'" must not read as floor 83
            var keys = FloorKey.Find(FeetInch.Replace(DecimalFeet.Replace(text, " "), " "));
            if (keys.Contains(floor)) return keys.Count == 1;
            if (keys.Count > 0 || namedOnly) return false;
            if (Datum.IsMatch(text)) return true;
            bool nearTitle = title != null && (line == title ||
                line.Y < title.Y && title.Y - line.Y <= 3 * title.Size && line.Right >= title.X - title.Size && line.X <= title.Right + title.Size);
            // a sign in front of a value ("+24'-0\""), not the hyphen of a feet-inch dimension ("1'-0\"")
            return nearTitle && (ElLabel.IsMatch(text) || Regex.IsMatch(text, @"(^|[\s(])[+\-±]\s*\d"));
        }

        /// <summary>A line holding one elevation and nothing else worth reading ("+16'-7\"", "(37.78')"); a dimension needs its sign.</summary>
        private static bool Alone(string text)
        {
            var m = DecimalFeet.Match(text);
            if (!m.Success)
            {
                m = FeetInch.Match(text);
                if (!m.Success || !m.Groups["sign"].Success) return false;
            }
            string rest = Regex.Replace(text.Remove(m.Index, m.Length), @"[\s()]", "");
            return rest.Length <= 2 && Values(text).Count == 1;
        }
    }
}
