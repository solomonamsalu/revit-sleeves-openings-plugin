using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Setup
{
    public class SetupReport
    {
        public List<string> Lines { get; } = new List<string>();
        public int Warnings { get; private set; }
        public void Info(string s) => Lines.Add("  " + s);
        public void Warn(string s) { Warnings++; Lines.Add("! " + s); }
        public override string ToString() => string.Join("\n", Lines);
    }

    /// <summary>
    /// Feature 2: load required families and apply the manual's view range
    /// (Top = Level Above, offset 0; Bottom/View Depth = Associated Level, offset 0).
    /// All methods must be called inside an open Transaction.
    /// </summary>
    public static class ProjectSetup
    {
        public static Family FindFamily(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static void LoadFamilies(Document doc, RuleSet rules, SetupReport report)
        {
            foreach (var kv in rules.Families)
            {
                var fam = kv.Value;
                if (string.IsNullOrEmpty(fam.Family)) continue;

                if (FindFamily(doc, fam.Family) != null)
                {
                    report.Info($"Family present: {fam.Family}");
                    continue;
                }

                var path = RuleLoader.ResolveFamilyFile(fam);
                if (path == null || !File.Exists(path))
                {
                    report.Warn($"Family missing and no file to load: {fam.Family} (rules key '{kv.Key}', file '{fam.File}')");
                    continue;
                }

                if (doc.LoadFamily(path, out Family loaded))
                    report.Info($"Loaded family: {loaded.Name} from {path}");
                else
                    report.Warn($"Failed to load {path}");
            }
        }

        /// <summary>Applies the manual's view range to every non-template floor plan whose level is in the map.</summary>
        public static void ApplyViewRange(Document doc, RuleSet rules, LevelMap levels, SetupReport report,
                                          IEnumerable<ViewPlan> views = null)
        {
            var plans = views ?? new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null);

            foreach (var view in plans)
            {
                try
                {
                    ApplyViewRange(view, rules, levels);
                    report.Info($"View range set: {view.Name}");
                }
                catch (Exception ex)
                {
                    report.Warn($"View range NOT set on '{view.Name}': {ex.Message}");
                }
            }
        }

        public static void ApplyViewRange(ViewPlan view, RuleSet rules, LevelMap levels)
        {
            var level = view.GenLevel;
            var above = levels.Above(level);
            var vr = view.GetViewRange();

            double topOff = Units.InchesToFeet(rules.ViewRange.TopLevelAboveOffset);
            double botOff = Units.InchesToFeet(rules.ViewRange.BottomAssociatedLevelOffset);

            // Bottom and view depth on the associated level
            vr.SetLevelId(PlanViewPlane.ViewDepthPlane, level.Id);
            vr.SetOffset(PlanViewPlane.ViewDepthPlane, botOff);
            vr.SetLevelId(PlanViewPlane.BottomClipPlane, level.Id);
            vr.SetOffset(PlanViewPlane.BottomClipPlane, botOff);

            // Top on the level above (topmost level has nothing above -> leave unlimited/as-is)
            if (above != null)
            {
                vr.SetLevelId(PlanViewPlane.TopClipPlane, above.Level.Id);
                vr.SetOffset(PlanViewPlane.TopClipPlane, topOff);

                // Cut plane must sit between bottom and top: clamp if the view had a tall cut plane.
                double floorToFloor = above.Elevation - level.Elevation;
                double cut = vr.GetOffset(PlanViewPlane.CutPlane);
                if (vr.GetLevelId(PlanViewPlane.CutPlane) != level.Id || cut <= botOff || cut >= floorToFloor + topOff)
                {
                    vr.SetLevelId(PlanViewPlane.CutPlane, level.Id);
                    vr.SetOffset(PlanViewPlane.CutPlane, Math.Min(4.0, (floorToFloor + topOff) / 2));
                }
            }

            view.SetViewRange(vr);
        }
    }
}
