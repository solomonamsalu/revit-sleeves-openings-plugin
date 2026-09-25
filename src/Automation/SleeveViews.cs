using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using SleevesOpenings.Risers;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation
{
    /// <summary>rules.json "sleeveViews": the per-floor review views Auto Run makes (the office's "Sleeves" folder, as in PL).</summary>
    public class SleeveViewRules
    {
        /// <summary>Written to the view's Sub-Discipline parameter: the Project Browser folder.</summary>
        [JsonProperty("subDiscipline")] public string SubDiscipline { get; set; } = "Sleeves";
        /// <summary>View name = level name + this ("05.5-TH FLOOR Sleeves").</summary>
        [JsonProperty("nameSuffix")] public string NameSuffix { get; set; } = " Sleeves";
        /// <summary>Tag every add-in opening in the view (needs a tag family for the opening's category).</summary>
        [JsonProperty("tag")] public bool Tag { get; set; }
        /// <summary>The name text inside the opening families gets a transparent background (no white box over the lines).</summary>
        [JsonProperty("transparentLabels")] public bool TransparentLabels { get; set; } = true;
    }

    public class SleeveViewResult
    {
        public List<string> Created = new List<string>(), Reused = new List<string>();
        public int Tagged;
        public HashSet<string> NoTagFamily = new HashSet<string>();
        /// <summary>Opening family → the text parameter its tag shows (found by trying them); null = the tag already shows something, or none worked.</summary>
        public Dictionary<ElementId, string> LabelParams = new Dictionary<ElementId, string>();
        public int Labelled;
        public int Named;
        public int Untagged;                 // tags of an earlier run removed (rules.json sleeveViews.tag = false)                    // openings whose own name parameter was empty ("?") and got the riser name
        public List<string> Problems = new List<string>();

        public override string ToString()
        {
            if (Created.Count + Reused.Count == 0) return "Sleeve views: none (no openings on any level)." + (Problems.Count > 0 ? " " + string.Join("; ", Problems) : "");
            var s = $"Sleeve views ('{string.Join("', '", Created.Concat(Reused).Take(2))}'…): {Created.Count} created, {Reused.Count} already there" +
                    (Tagged > 0 ? $"; {Tagged} tag(s) placed." : ".");
            if (NoTagFamily.Count > 0) s += $" No tag family loaded for {string.Join(", ", NoTagFamily)}: load a tag for it and run Tag Openings in those views.";
            if (Named > 0) s += $" {Named} opening(s) showed '?' (empty Riser Number): the riser name was written.";
            if (Untagged > 0) s += $" {Untagged} separate tag(s) from an earlier run removed: the openings show their name themselves.";
            var found = LabelParams.Values.Where(v => v != null).Distinct().ToList();
            if (found.Count > 0) s += $" The tags show the '{string.Join("', '", found)}' parameter: the riser name was written there on {Labelled} opening(s).";
            if (Problems.Count > 0) s += " " + string.Join("; ", Problems);
            return s;
        }
    }

    /// <summary>
    /// One plan per floor that has sleeves/openings, named "&lt;level&gt; Sleeves" with Sub-Discipline "Sleeves" (the PL
    /// model's layout): a duplicate of that level's own plan (mechanical first), the manual's view range, every add-in
    /// opening tagged. An existing view of that name is reused and only gets the missing tags. One transaction.
    /// </summary>
    public static class SleeveViews
    {
        private const int Mechanical = 4;       // BuiltInParameter.VIEW_DISCIPLINE value

        public static SleeveViewResult Ensure(Document doc, RuleSet rules, LevelMap levels, SleeveViewRules cfg)
        {
            cfg = cfg ?? new SleeveViewRules();
            var result = new SleeveViewResult();
            var openings = RiserIndex.AllOpenings(doc);
            var byLevel = openings.GroupBy(o => o.Level.Id).ToDictionary(g => g.Key, g => g.ToList());
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate).ToList();

            using (var t = new Transaction(doc, "Sleeves & Openings: sleeve views"))
            {
                t.Start();
                foreach (var cl in levels.All.Where(l => byLevel.ContainsKey(l.Level.Id)))
                {
                    var level = cl.Level;
                    string name = level.Name + cfg.NameSuffix;
                    var view = plans.FirstOrDefault(v => v.Name == name);
                    try
                    {
                        if (view == null)
                        {
                            var source = plans.Where(v => v.GenLevel?.Id == level.Id && v.ViewType == ViewType.FloorPlan && v.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                                              .OrderBy(v => v.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE)?.AsInteger() == Mechanical ? 0 : 1)
                                              .ThenBy(v => v.Name.Length).FirstOrDefault();
                            if (source == null) { result.Problems.Add($"{level.Name}: no floor plan to copy"); continue; }
                            view = doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate)) as ViewPlan;
                            view.Name = Unique(plans, name);
                            plans.Add(view);
                            result.Created.Add(view.Name);
                        }
                        else result.Reused.Add(view.Name);

                        // the Project Browser folder; a view template that locks Sub-Discipline is copied onto the view instead
                        var sub = view.LookupParameter("Sub-Discipline");
                        if (sub != null && sub.IsReadOnly && view.ViewTemplateId != ElementId.InvalidElementId)
                        {
                            var template = doc.GetElement(view.ViewTemplateId) as View;
                            view.ViewTemplateId = ElementId.InvalidElementId;
                            if (template != null) view.ApplyViewTemplateParameters(template);
                            sub = view.LookupParameter("Sub-Discipline");
                        }
                        if (sub == null) result.Problems.Add($"{view.Name}: this model has no Sub-Discipline parameter, so the view stays in its discipline's folder");
                        else if (sub.IsReadOnly || sub.StorageType != StorageType.String) result.Problems.Add($"{view.Name}: Sub-Discipline cannot be set");
                        else if (sub.AsString() != cfg.SubDiscipline) sub.Set(cfg.SubDiscipline);

                        // the manual's view range, unless a view template decides it
                        if (view.ViewTemplateId == ElementId.InvalidElementId)
                            try { ProjectSetup.ApplyViewRange(view, rules, levels); } catch (Exception ex) { result.Problems.Add($"{view.Name}: view range not set ({ex.Message})"); }

                        Name(byLevel[level.Id], result);
                        if (cfg.Tag) Tag(doc, view, byLevel[level.Id], result);
                        else Untag(doc, view, byLevel[level.Id], result);
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add($"{name}: {ex.Message}");
                        App.Log($"AutoRun: sleeve view {name} failed: {ex}");
                    }
                }
                t.Commit();
            }
            return result;
        }

        /// <summary>
        /// Tags each opening not yet tagged in the view, beside the opening (the category's default tag), then makes sure
        /// every tag shows the opening's name (see <see cref="Label"/>).
        /// </summary>
        /// <summary>Openings placed before their family's own name parameter was filled show "?" in their corner: fill it.</summary>
        private static void Name(List<OpeningRecord> openings, SleeveViewResult result)
        {
            foreach (var o in openings.Where(o => !string.IsNullOrEmpty(o.Data.Label)))
            {
                bool empty = Placement.Placer.LabelParams.Any(n => { var p = o.Instance.LookupParameter(n); return p != null && !p.IsReadOnly && p.StorageType == StorageType.String && string.IsNullOrEmpty(p.AsString()); });
                if (!empty) continue;
                Placement.Placer.WriteLabel(o.Instance, null, o.Data.Label);
                result.Named++;
            }
        }

        /// <summary>
        /// No separate tags (the opening shows its name itself): removes the tags of add-in openings in this Sleeves view,
        /// and the Mark an earlier run copied the name into for those tags (a Mark equal to the name).
        /// </summary>
        private static void Untag(Document doc, ViewPlan view, List<OpeningRecord> openings, SleeveViewResult result)
        {
            var ours = openings.ToDictionary(o => o.Instance.Id);
            foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().ToList())
            {
                var hits = tag.GetTaggedLocalElementIds().Where(ours.ContainsKey).ToList();
                if (hits.Count == 0) continue;
                doc.Delete(tag.Id);
                result.Untagged++;
                foreach (var id in hits)
                {
                    var o = ours[id];
                    var mark = o.Instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (mark != null && !mark.IsReadOnly && !string.IsNullOrEmpty(o.Data.Label) && mark.AsString() == o.Data.Label) mark.Set("");
                }
            }
        }

        private static void Tag(Document doc, ViewPlan view, List<OpeningRecord> openings, SleeveViewResult result)
        {
            var tags = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().ToList();
            var tagged = new HashSet<ElementId>(tags.SelectMany(x => x.GetTaggedLocalElementIds()));
            foreach (var o in openings.Where(o => !tagged.Contains(o.Instance.Id)))
            {
                string category = o.Instance.Category?.Name ?? "?";
                if (result.NoTagFamily.Contains(category)) continue;
                double hw = Units.InchesToFeet((o.Data.Width ?? o.Data.Diameter ?? 0) / 2);
                var head = new XYZ(o.Point.X + hw + 1.0, o.Point.Y + 1.0, o.Point.Z);
                try
                {
                    tags.Add(IndependentTag.Create(doc, view.Id, new Reference(o.Instance), true, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, head));
                    result.Tagged++;
                }
                catch (Exception) { result.NoTagFamily.Add(category); }
            }
            foreach (var tag in tags)
            {
                var id = tag.GetTaggedLocalElementIds().FirstOrDefault();
                var o = openings.FirstOrDefault(x => x.Instance.Id == id);
                if (o != null && !string.IsNullOrEmpty(o.Data.Label)) Label(doc, tag, o, result);
            }
        }

        /// <summary>
        /// A tag shows "?" when the parameter its label reads is empty, and the add-in writes the opening's name to the
        /// family's mapped name parameter (often Comments), which the office tag may not read. Once per family: if the tag
        /// shows "?", each empty text parameter of the opening is tried until the tag shows the name; that parameter is
        /// then filled on every opening of the family. Inside the caller's transaction.
        /// </summary>
        private static void Label(Document doc, IndependentTag tag, OpeningRecord o, SleeveViewResult result)
        {
            var inst = o.Instance;
            var family = inst.Symbol?.Family?.Id ?? ElementId.InvalidElementId;
            string label = o.Data.Label;
            if (!result.LabelParams.TryGetValue(family, out var name))
            {
                result.LabelParams[family] = name = Probe(doc, tag, inst, label);
                if (name != null) result.Labelled++;                       // the probe left the name on this opening
                if (name == null && Shows(doc, tag) == null)
                    result.Problems.Add($"tags of '{inst.Symbol?.Family?.Name}' show '?': none of its empty text parameters is the one the tag reads");
                App.Log($"AutoRun: tag label parameter for '{inst.Symbol?.Family?.Name}': {name ?? "(tag already shows text, or not found)"}; tag shows '{Shows(doc, tag) ?? "?"}'");
                // what the family offers, to map its own label (the "?" drawn inside the opening) in rules.json
                App.Log($"AutoRun: text parameters of '{inst.Symbol?.Family?.Name}': " + string.Join(", ",
                    inst.Parameters.Cast<Parameter>().Concat(inst.Symbol.Parameters.Cast<Parameter>())
                        .Where(x => x.Definition != null && x.StorageType == StorageType.String)
                        .Select(x => $"{x.Definition.Name}{(x.Element is FamilySymbol ? " (type)" : "")}{(x.IsReadOnly ? " (read-only)" : "")}='{x.AsString()}'")));
            }
            if (name == null) return;
            var p = inst.LookupParameter(name);
            if (p != null && !p.IsReadOnly && string.IsNullOrEmpty(p.AsString())) { p.Set(label); result.Labelled++; }
        }

        /// <summary>The parameter that makes the tag show <paramref name="label"/>, or null (the tag already shows something, or none does).</summary>
        private static string Probe(Document doc, IndependentTag tag, FamilyInstance inst, string label)
        {
            if (Shows(doc, tag) != null) return null;
            foreach (Parameter p in inst.Parameters)
            {
                if (p.IsReadOnly || p.StorageType != StorageType.String || p.Definition == null || !string.IsNullOrEmpty(p.AsString())) continue;
                try
                {
                    p.Set(label);
                    if ((Shows(doc, tag) ?? "").Contains(label)) return p.Definition.Name;
                    p.Set("");
                }
                catch (Exception) { /* not settable here */ }
            }
            return null;
        }

        /// <summary>The tag's text, or null when it shows nothing or "?".</summary>
        private static string Shows(Document doc, IndependentTag tag)
        {
            try
            {
                doc.Regenerate();
                var t = tag.TagText?.Trim();
                return string.IsNullOrEmpty(t) || t == "?" ? null : t;
            }
            catch (Exception) { return null; }
        }

        private static string Unique(List<ViewPlan> plans, string name)
        {
            string n = name;
            for (int i = 2; plans.Any(v => v.Name == n); i++) n = $"{name} ({i})";
            return n;
        }
    }
}
