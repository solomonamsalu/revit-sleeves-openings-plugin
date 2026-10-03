using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// rules.json "modelKind": how Auto Run tells an HV model from a PL or FP model when the file name does not say.
    /// The name decides first (plumbing.modelMatch, sprinkler.modelMatch, hvMatch); then what the model holds.
    /// </summary>
    public class ModelKindRules
    {
        /// <summary>Regex on the model name for the HV model ("24 Skillman HV.rvt", "Project_MECH.rvt").</summary>
        [JsonProperty("hvMatch")] public string HvMatch { get; set; } = @"(\bHV(\b|_)|HVAC|\bMECH)";
        /// <summary>Fewest ducts / pipes / fixtures / sprinkler heads in the model for its contents to decide.</summary>
        [JsonProperty("minElements")] public int MinElements { get; set; } = 20;
        /// <summary>The winning side must have at least this many times the other's elements (HV vs pipe).</summary>
        [JsonProperty("ratio")] public double Ratio { get; set; } = 2;
    }

    /// <summary>What the model is and why: HV, PL, FP, or null when nothing decided it.</summary>
    public class ModelKindResult
    {
        public string Kind;
        /// <summary>"your choice", "name", "contents", "model signs", or null.</summary>
        public string Source;
        public string Reason;

        public string Describe() => Kind == null
            ? $"not recognised ({Reason}): the drawings' sheet numbers decide"
            : $"{Kind} (from {Source}: {Reason})";
    }

    /// <summary>
    /// Which kind of model this is, in order: the user's saved answer (only written when they chose), the file name,
    /// the elements it holds (ducts vs pipes by system type, plumbing fixtures, sprinkler heads), then other signs
    /// (view disciplines, worksets, sheet numbers, sleeves already placed). Null when none decides: the drawings picked
    /// decide, and when they do not either the user is asked once.
    /// </summary>
    public static class ModelDiscipline
    {
        public const string HV = "HV", PL = "PL", FP = "FP";

        public static ModelKindResult Detect(Document doc, RuleSet rules, ProjectState state, ExistingReport existing)
        {
            var cfg = rules.ModelKind ?? new ModelKindRules();

            string saved = state?.Automation?.ModelKind;
            if (saved == HV || saved == PL || saved == FP)
                return new ModelKindResult { Kind = saved, Source = "your choice", Reason = "saved in this model" };

            // 1. the file name; "_" counts as a separator ("24 Skillman_PL")
            string name = System.IO.Path.GetFileNameWithoutExtension(doc.PathName ?? "");
            if (string.IsNullOrEmpty(name)) name = doc.Title ?? "";
            var byName = new List<string>();
            if (NameMatches(name, rules.Plumbing?.ModelMatch)) byName.Add(PL);
            if (rules.Sprinkler != null && rules.Sprinkler.Enabled && NameMatches(name, rules.Sprinkler.ModelMatch)) byName.Add(FP);
            if (NameMatches(name, cfg.HvMatch)) byName.Add(HV);
            if (byName.Count == 1)
                return new ModelKindResult { Kind = byName[0], Source = "name", Reason = $"'{name}'" };
            var why = new List<string> { byName.Count == 0 ? $"name '{name}' has no HV / PL / FP code" : $"name '{name}' matches {string.Join(" and ", byName)}" };

            // 2. what the model holds (this model only, not its links)
            var c = Contents(doc);
            int hv = c.Ducts + c.Terminals + c.HydronicPipes, pl = c.PlumbingPipes + c.Fixtures, fp = c.FirePipes + c.Sprinklers;
            string counts = $"{c.Ducts} duct(s), {c.Terminals} air terminal(s), {c.HydronicPipes} hydronic pipe(s); " +
                            $"{c.PlumbingPipes} plumbing pipe(s), {c.Fixtures} plumbing fixture(s); {c.FirePipes} fire pipe(s), {c.Sprinklers} sprinkler head(s)";
            int pipe = pl + fp;
            if (Math.Max(hv, pipe) >= cfg.MinElements)
            {
                if (hv >= cfg.Ratio * pipe)
                    return new ModelKindResult { Kind = HV, Source = "contents", Reason = counts };
                if (pipe >= cfg.Ratio * hv)
                    return new ModelKindResult { Kind = fp > pl ? FP : PL, Source = "contents", Reason = counts };
                why.Add($"contents mixed ({counts})");
            }
            else why.Add(hv + pipe == 0 ? "no ducts, pipes, fixtures or sprinkler heads" : $"too few elements ({counts})");

            // 3. other signs: each one votes for the kind most of it points to
            var votes = new List<(string Kind, string Sign)>();
            void Vote(string sign, Dictionary<string, int> tally)
            {
                var top = tally.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
                if (top.Count == 0) return;
                // HV against the pipe kinds must be clear; PL against FP only picks which pipe model
                int hvN = tally.TryGetValue(HV, out var h) ? h : 0, pipeN = tally.Where(kv => kv.Key != HV).Sum(kv => kv.Value);
                if (hvN > 0 && pipeN > 0 && Math.Max(hvN, pipeN) < 2 * Math.Min(hvN, pipeN)) return;
                string kind = hvN > pipeN ? HV : (tally.TryGetValue(FP, out var f) ? f : 0) > (tally.TryGetValue(PL, out var p) ? p : 0) ? FP : PL;
                votes.Add((kind, sign));
            }
            Vote("view disciplines", ViewDisciplines(doc));
            Vote("worksets", Worksets(doc));
            Vote("sheet numbers", SheetNumbers(doc));
            Vote("sleeves already placed", PlacedSystems(existing));
            if (votes.Count > 0)
            {
                int hvVotes = votes.Count(v => v.Kind == HV), pipeVotes = votes.Count - hvVotes;
                if (hvVotes == 0 || pipeVotes == 0)
                {
                    string kind = hvVotes > 0 ? HV : votes.Count(v => v.Kind == FP) > votes.Count(v => v.Kind == PL) ? FP : PL;
                    return new ModelKindResult { Kind = kind, Source = "model signs", Reason = string.Join(", ", votes.Select(v => $"{v.Sign} say {v.Kind}")) };
                }
                why.Add("signs disagree (" + string.Join(", ", votes.Select(v => $"{v.Sign}: {v.Kind}")) + ")");
            }
            else why.Add("no view disciplines, worksets, sheet numbers or placed sleeves to go by");

            return new ModelKindResult { Kind = null, Reason = string.Join("; ", why) };
        }

        /// <summary>The disciplines a kind of model runs: HV mechanical; PL and FP plumbing and sprinkler (the office puts sprinkler sleeves in the PL model); unknown all.</summary>
        public static List<string> Disciplines(string kind, RuleSet rules)
        {
            bool sprinkler = rules.Sprinkler != null && rules.Sprinkler.Enabled;
            var pipe = new List<string> { AutomationInputs.Plumbing };
            if (sprinkler) pipe.Add(AutomationInputs.Sprinkler);
            if (kind == HV) return new List<string> { AutomationInputs.Mechanical };
            if (kind == PL) return pipe;
            if (kind == FP) { pipe.Reverse(); return pipe; }          // FP model opens on sprinkler
            return new List<string> { AutomationInputs.Mechanical }.Concat(pipe).ToList();
        }

        /// <summary>The kinds of model that run a discipline (to offer when the drawings and the model disagree).</summary>
        public static List<string> KindsFor(string discipline) =>
            discipline == AutomationInputs.Mechanical ? new List<string> { HV }
          : discipline == AutomationInputs.Sprinkler ? new List<string> { FP, PL }
          : new List<string> { PL, FP };

        public static string Word(string kind) =>
            kind == HV ? "HV (mechanical)" : kind == PL ? "PL (plumbing)" : kind == FP ? "FP (fire protection)" : "unknown";

        /// <summary>The name or, failing that, the name with "_" read as a space (\b does not see "_PL" as a word).</summary>
        internal static bool NameMatches(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(name)) return false;
            try
            {
                return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase) ||
                       Regex.IsMatch(name.Replace('_', ' '), pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }

        private class Counts { public int Ducts, Terminals, HydronicPipes, PlumbingPipes, FirePipes, Fixtures, Sprinklers; }

        private static Counts Contents(Document doc)
        {
            int Count(BuiltInCategory cat) =>
                new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().GetElementCount();
            var c = new Counts
            {
                Ducts = Count(BuiltInCategory.OST_DuctCurves) + Count(BuiltInCategory.OST_FlexDuctCurves),
                Terminals = Count(BuiltInCategory.OST_DuctTerminal),
                Fixtures = Count(BuiltInCategory.OST_PlumbingFixtures),
                Sprinklers = Count(BuiltInCategory.OST_Sprinklers)
            };
            // pipes by their system type: classification first, then the type's name (storm and gas are "Other" in Revit)
            var types = new Dictionary<ElementId, string>();
            foreach (var pipe in new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>())
            {
                var id = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                if (!types.TryGetValue(id, out var kind)) types[id] = kind = PipeKind(doc.GetElement(id) as PipingSystemType);
                if (kind == HV) c.HydronicPipes++;
                else if (kind == PL) c.PlumbingPipes++;
                else if (kind == FP) c.FirePipes++;
            }
            return c;
        }

        private static string PipeKind(PipingSystemType type)
        {
            if (type == null) return null;
            string name = (type.Name ?? "").ToUpperInvariant();
            if (Regex.IsMatch(name, @"SPRINK|STANDPIPE|\bFDC\b|FIRE")) return FP;
            if (Regex.IsMatch(name, @"STORM|\bGAS\b|SANIT|\bVENT\b|WASTE|DOMESTIC")) return PL;
            switch (type.SystemClassification)
            {
                case MEPSystemClassification.FireProtectWet:
                case MEPSystemClassification.FireProtectDry:
                case MEPSystemClassification.FireProtectPreaction:
                case MEPSystemClassification.FireProtectOther: return FP;
                case MEPSystemClassification.Sanitary:
                case MEPSystemClassification.Vent:
                case MEPSystemClassification.DomesticColdWater:
                case MEPSystemClassification.DomesticHotWater: return PL;
                case MEPSystemClassification.SupplyHydronic:
                case MEPSystemClassification.ReturnHydronic: return HV;
                default: return null;
            }
        }

        /// <summary>Plan views by their Discipline (Mechanical / Plumbing); templates left out.</summary>
        private static Dictionary<string, int> ViewDisciplines(Document doc)
        {
            var tally = new Dictionary<string, int>();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate))
            {
                var p = v.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE);
                if (p == null || !p.HasValue) continue;
                switch (p.AsInteger())
                {
                    case (int)ViewDiscipline.Mechanical: Add(tally, HV); break;
                    case (int)ViewDiscipline.Plumbing: Add(tally, PL); break;
                }
            }
            return tally;
        }

        private static Dictionary<string, int> Worksets(Document doc)
        {
            var tally = new Dictionary<string, int>();
            if (!doc.IsWorkshared) return tally;
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                string kind = KindOfWords(w.Name);
                if (kind != null) Add(tally, kind);
            }
            return tally;
        }

        /// <summary>Sheet numbers in the model: M- / H- HV, P- PL, SP- / FP- FP.</summary>
        private static Dictionary<string, int> SheetNumbers(Document doc)
        {
            var tally = new Dictionary<string, int>();
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                var m = Regex.Match(s.SheetNumber ?? "", @"^\s*([A-Za-z]{1,3})\s*-?\s*\d");
                if (!m.Success) continue;
                string d = UI.AutoRunForm.DisciplineOfSheets(m.Groups[1].Value);
                string kind = d == AutomationInputs.Mechanical ? HV : d == AutomationInputs.Plumbing ? PL : d == AutomationInputs.Sprinkler ? FP : null;
                if (kind != null) Add(tally, kind);
            }
            return tally;
        }

        /// <summary>The systems of the sleeves / openings already in the model (KX, TX, ERV... HV; sanitary, vent... PL).</summary>
        private static Dictionary<string, int> PlacedSystems(ExistingReport existing)
        {
            var tally = new Dictionary<string, int>();
            foreach (var i in existing?.Items ?? new List<ExistingItem>())
            {
                string kind = KindOfWords(i.System);
                if (kind != null) Add(tally, kind);
            }
            return tally;
        }

        private static string KindOfWords(string text)
        {
            string t = (text ?? "").ToUpperInvariant();
            if (Regex.IsMatch(t, @"SPRINK|STANDPIPE|\bFDC\b|FIRE\s*PROT|\bFP\b")) return FP;
            if (Regex.IsMatch(t, @"PLUMB|SANIT|VENT\b|STORM|\bGAS\b|WATER|\bPL\b")) return PL;
            if (Regex.IsMatch(t, @"HVAC|MECH|DUCT|EXHAUST|SUPPLY|\bERV\b|DRYER|\bKX|\bTX|OUTSIDE\s*AIR|REFRIG|\bHV\b")) return HV;
            return null;
        }

        private static void Add(Dictionary<string, int> tally, string kind) => tally[kind] = (tally.TryGetValue(kind, out var n) ? n : 0) + 1;
    }
}
