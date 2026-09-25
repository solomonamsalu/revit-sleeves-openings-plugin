using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Placement
{
    /// <summary>Roles the add-in places; each maps to one FamilySymbol in the project.</summary>
    public static class FamilyRole
    {
        public const string RegularOpening = "regularOpening";
        public const string PipeReferenceOpening = "pipeReferenceOpening";
        public const string RoundSleeve = "roundSleeve";
        public const string ElectricalOpening = "electricalOpening";

        public static readonly string[] All = { RegularOpening, PipeReferenceOpening, RoundSleeve, ElectricalOpening };

        public static string Describe(string role)
        {
            switch (role)
            {
                case RegularOpening: return "Regular Opening (rectangular: exhaust, chute, damper)";
                case PipeReferenceOpening: return "Pipe Reference Opening (refrigeration lines)";
                case RoundSleeve: return "Round Sleeve (storm, condensate, standpipe, dryer, bathtub)";
                case ElectricalOpening: return "Electrical Opening (blue box)";
                default: return role;
            }
        }
    }

    /// <summary>Which family/type and which parameter names to drive, for one role.</summary>
    public class FamilyMapEntry
    {
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string WidthParam { get; set; }
        public string LengthParam { get; set; }
        public string DiameterParam { get; set; }
        public string NameParam { get; set; }
        public string DownHeightParam { get; set; }

        /// <summary>Checkbox-sized family (see FamilyRule.SizeToggles). Null = ordinary length parameters.</summary>
        public Dictionary<string, string> SizeToggles { get; set; }
        public string SizeTogglePattern { get; set; }

        public bool UsesSizeToggles => SizeToggles != null && SizeToggles.Count > 0;

        public static FamilyMapEntry FromRule(FamilyRule r) => r == null ? null : new FamilyMapEntry
        {
            FamilyName = r.Family, TypeName = r.Type, WidthParam = r.WidthParam, LengthParam = r.LengthParam,
            DiameterParam = r.DiameterParam, NameParam = r.NameParam, DownHeightParam = r.DownHeightParam,
            SizeToggles = r.SizeToggles, SizeTogglePattern = r.SizeTogglePattern
        };
    }

    /// <summary>Resolves roles to FamilySymbols: project mapping first, then rules.json, with parameter auto-guessing.</summary>
    public static class FamilyMapping
    {
        static readonly Regex RxWidth = new Regex(@"^(opening\s*)?(width|w|dim\s*x|x)$", RegexOptions.IgnoreCase);
        static readonly Regex RxLength = new Regex(@"^(opening\s*)?(length|depth|height|l|h|d|dim\s*y|y)$", RegexOptions.IgnoreCase);
        static readonly Regex RxDiameter = new Regex(@"(diameter|dia\b|radius|nominal)", RegexOptions.IgnoreCase);
        static readonly string[] NamePreference = { "Riser Number", "Name", "Label", "Tag", "Text", "Description", "Comments", "Mark" };
        static readonly Regex RxDownHeight = new Regex(@"down\s*height", RegexOptions.IgnoreCase);

        public static FamilyMapEntry Get(RuleSet rules, ProjectState state, string role)
        {
            var rule = rules.Family(role);
            if (state?.FamilyMap != null && state.FamilyMap.TryGetValue(role, out var e) && !string.IsNullOrEmpty(e.FamilyName))
            {
                // Size toggles live in rules.json only; carry them onto the project mapping when it is the same family.
                if (e.SizeToggles == null && rule?.SizeToggles != null &&
                    string.Equals(e.FamilyName, rule.Family, StringComparison.OrdinalIgnoreCase))
                {
                    e.SizeToggles = rule.SizeToggles;
                    e.SizeTogglePattern = rule.SizeTogglePattern;
                }
                return e;
            }
            return FamilyMapEntry.FromRule(rule);
        }

        public static IEnumerable<FamilySymbol> AllSymbols(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Family != null && !s.Family.IsInPlace)
                .OrderBy(s => s.Family.Name).ThenBy(s => s.Name);

        public static FamilySymbol FindSymbol(Document doc, FamilyMapEntry map)
        {
            if (map == null || string.IsNullOrEmpty(map.FamilyName)) return null;
            var candidates = AllSymbols(doc)
                .Where(s => s.Family.Name.Equals(map.FamilyName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) return null;
            if (!string.IsNullOrEmpty(map.TypeName))
            {
                var t = candidates.FirstOrDefault(s => s.Name.Equals(map.TypeName, StringComparison.OrdinalIgnoreCase));
                if (t != null) return t;
            }
            return candidates.First();
        }

        /// <summary>
        /// Parameter names available on the type and on an instance of the family.
        /// Instance parameters are read from an existing instance, or from a throw-away probe instance
        /// created and rolled back in a sub-transaction (must be called outside an open transaction group).
        /// </summary>
        public static void ParamNames(Document doc, FamilySymbol symbol, out List<string> lengths, out List<string> texts)
        {
            var len = new HashSet<string>();
            var txt = new HashSet<string> { "Comments", "Mark" };
            void Harvest(Element e)
            {
                foreach (Parameter p in e.Parameters)
                {
                    if (p.IsReadOnly || p.Definition == null) continue;
                    if (IsLength(p)) len.Add(p.Definition.Name);
                    else if (p.StorageType == StorageType.String) txt.Add(p.Definition.Name);
                }
            }
            Harvest(symbol);

            var existing = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .FirstOrDefault(i => i.Symbol?.Family?.Id == symbol.Family.Id);
            if (existing != null) Harvest(existing);
            else
            {
                using (var t = new Transaction(doc, "probe family parameters"))
                {
                    t.Start();
                    try
                    {
                        if (!symbol.IsActive) symbol.Activate();
                        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                        var probe = doc.Create.NewFamilyInstance(XYZ.Zero, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        Harvest(probe);
                    }
                    catch { /* hosted/face-based: cannot probe blindly; type params only */ }
                    t.RollBack();
                }
            }
            lengths = len.OrderBy(n => n).ToList();
            texts = txt.OrderBy(n => n).ToList();
        }

        static bool IsLength(Parameter p) =>
            p.StorageType == StorageType.Double && p.Definition.GetDataType() == SpecTypeId.Length;

        /// <summary>Fills in blank parameter names by matching common names on the symbol.</summary>
        public static void GuessParams(Document doc, FamilySymbol symbol, FamilyMapEntry map)
        {
            ParamNames(doc, symbol, out var lengths, out var texts);
            map.WidthParam = map.WidthParam ?? lengths.FirstOrDefault(n => RxWidth.IsMatch(n));
            map.LengthParam = map.LengthParam ?? lengths.FirstOrDefault(n => RxLength.IsMatch(n) && n != map.WidthParam);
            map.DiameterParam = map.DiameterParam ?? lengths.FirstOrDefault(n => RxDiameter.IsMatch(n));
            map.DownHeightParam = map.DownHeightParam ?? lengths.FirstOrDefault(n => RxDownHeight.IsMatch(n));
            // the family's own name parameter first: Comments/Mark only when it has none
            map.NameParam = map.NameParam ?? NamePreference.Select(p => texts.FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase)))
                                                           .FirstOrDefault(n => n != null) ?? "Comments";
        }

        /// <summary>Suggests a symbol for a role by family-name keywords when nothing is mapped yet.</summary>
        public static FamilySymbol Suggest(Document doc, string role)
        {
            string[] keys;
            switch (role)
            {
                case FamilyRole.RegularOpening: keys = new[] { "regular opening", "rect opening", "opening", "mech opening" }; break;
                case FamilyRole.PipeReferenceOpening: keys = new[] { "pipe reference", "pipe opening", "reference opening" }; break;
                case FamilyRole.RoundSleeve: keys = new[] { "round sleeve", "sleeve", "round opening", "pipe sleeve" }; break;
                case FamilyRole.ElectricalOpening: keys = new[] { "electrical opening", "electric", "elec" }; break;
                default: return null;
            }
            var all = AllSymbols(doc).ToList();
            foreach (var k in keys)
            {
                var hit = all.FirstOrDefault(s => s.Family.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
