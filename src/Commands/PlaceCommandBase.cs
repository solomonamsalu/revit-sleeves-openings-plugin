using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SleevesOpenings.Placement;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;
using SleevesOpenings.UI;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace SleevesOpenings.Commands
{
    /// <summary>Everything a placement command needs about the current session.</summary>
    public class PlaceContext
    {
        public UIDocument UiDoc;
        public Document Doc;
        public ViewPlan View;
        public Level Level;
        public RuleSet Rules;
        public ProjectState State;
        public LevelMap Levels;
        public bool OnRoof => Levels.All.FirstOrDefault(l => l.Level.Id == Level.Id)?.Role == LevelRole.Roof;
    }

    /// <summary>
    /// Shared flow for every "Place ..." button:
    ///   build spec from rules (+ small input dialog) → resolve family → click points until Esc →
    ///   guard check → place inside a transaction. Subclasses only describe *what* to place.
    /// </summary>
    public abstract class PlaceCommandBase : IExternalCommand
    {
        protected abstract string Title { get; }

        /// <summary>Return the spec to place, or null to cancel. May show a dialog.</summary>
        protected abstract OpeningSpec BuildSpec(PlaceContext ctx);

        /// <summary>Prompt shown in the status bar while picking.</summary>
        protected virtual string PickPrompt(OpeningSpec spec) => $"{Title}: click location for {spec.SizeText} ({spec.Label}). Esc to finish.";

        /// <summary>Places for one click. Default: one instance at the point. Returns instances created.</summary>
        protected virtual IList<FamilyInstance> PlaceAtClick(PlaceContext ctx, Placer placer, OpeningSpec spec,
                                                               FamilySymbol symbol, FamilyMapEntry map, XYZ pt) =>
            new[] { placer.Place(spec, symbol, map, ctx.Level, pt) };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            var doc = uidoc.Document;

            try
            {
                if (!(doc.ActiveView is ViewPlan plan) || plan.GenLevel == null)
                {
                    TaskDialog.Show(Title, "Open a floor plan (or roof plan) view first — openings are placed on the view's level.");
                    return Result.Cancelled;
                }

                var ctx = new PlaceContext
                {
                    UiDoc = uidoc, Doc = doc, View = plan, Level = plan.GenLevel,
                    Rules = App.Rules(doc), State = ProjectStore.Load(doc)
                };
                ctx.Levels = LevelClassifier.Classify(doc, ctx.Rules, ctx.State);

                if (!WorkGate.Ensure(doc, ctx.Rules, ctx.State)) return Result.Cancelled;

                var spec = BuildSpec(ctx);
                if (spec == null) return Result.Cancelled;

                var map = ResolveFamily(ctx, spec.Role, out FamilySymbol symbol);
                if (symbol == null) return Result.Cancelled;

                EnsureWorkPlane(ctx);
                EnsureSharedParams(ctx.Doc, ctx.Rules, ctx.State);

                var guard = new PlacementGuard(doc, ctx.Rules, ctx.Level);
                var placer = new Placer(doc, plan);
                int placed = 0;

                // One click session = one undo step.
                using (var session = new TransactionGroup(doc, $"Place {Title}"))
                {
                session.Start();
                while (true)
                {
                    XYZ pt;
                    try { pt = uidoc.Selection.PickPoint(ObjectSnapTypes.None, PickPrompt(spec)); }
                    catch (OperationCanceledException) { break; }

                    double hw = (spec.Width ?? spec.Diameter ?? 0) / 2, hl = (spec.Length ?? spec.Diameter ?? 0) / 2;
                    var warnings = guard.Check(pt, hw, hl, spec.System);
                    if (warnings.Count > 0 && !ConfirmWarnings(warnings)) continue;

                    using (var t = new Transaction(doc, $"Place {Title}"))
                    {
                        t.Start();
                        try
                        {
                            placed += PlaceAtClick(ctx, placer, spec, symbol, map, pt).Count;
                            t.Commit();
                        }
                        catch (Exception ex)
                        {
                            t.RollBack();
                            App.Log($"{Title} failed: {ex}");
                            var td = new TaskDialog(Title) { MainInstruction = "Could not place", MainContent = ex.Message,
                                CommonButtons = TaskDialogCommonButtons.Retry | TaskDialogCommonButtons.Cancel };
                            if (td.Show() != TaskDialogResult.Retry) break;
                        }
                    }
                }
                if (placed > 0) session.Assimilate(); else session.RollBack();
                }

                App.Log($"{Title}: placed {placed} on {ctx.Level.Name}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                App.Log($"{Title} error: {ex}");
                return Result.Failed;
            }
        }

        /// <summary>Family/type for the role; opens Map Families if nothing usable is mapped yet.</summary>
        private FamilyMapEntry ResolveFamily(PlaceContext ctx, string role, out FamilySymbol symbol)
        {
            var map = FamilyMapping.Get(ctx.Rules, ctx.State, role);
            symbol = FamilyMapping.FindSymbol(ctx.Doc, map);
            if (symbol != null) return map;

            var td = new TaskDialog(Title)
            {
                MainInstruction = $"No family mapped for: {FamilyRole.Describe(role)}",
                MainContent = map?.FamilyName != null
                    ? $"rules.json expects family '{map.FamilyName}' but it is not loaded in this project.\n\nPick a loaded family now?"
                    : "Pick a loaded family for this role now?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
            };
            if (td.Show() != TaskDialogResult.Yes) return null;

            if (!MapFamiliesCommand.ShowAndSave(ctx.Doc, ctx.Rules, ctx.State)) return null;
            map = FamilyMapping.Get(ctx.Rules, ctx.State, role);
            symbol = FamilyMapping.FindSymbol(ctx.Doc, map);
            if (symbol == null) TaskDialog.Show(Title, "Still no family for this role — load one into the project and try again.");
            return map;
        }

        /// <summary>Binds SO System / SO Riser / SO Size to the mapped categories (idempotent).</summary>
        public static void EnsureSharedParams(Document doc, RuleSet rules, ProjectState state)
        {
            using (var t = new Transaction(doc, "Sleeves & Openings: shared parameters"))
            {
                t.Start();
                try { SharedParams.EnsureBound(doc, SharedParams.MappedCategories(doc, rules, state)); t.Commit(); }
                catch (Exception ex) { t.RollBack(); App.Log("Shared params not bound: " + ex.Message); }
            }
        }

        /// <summary>PickPoint needs a work plane; plan views normally have one, but make sure.</summary>
        private static void EnsureWorkPlane(PlaceContext ctx)
        {
            if (ctx.View.SketchPlane != null) return;
            using (var t = new Transaction(ctx.Doc, "Set work plane"))
            {
                t.Start();
                var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, ctx.Level.Elevation));
                ctx.View.SketchPlane = SketchPlane.Create(ctx.Doc, plane);
                t.Commit();
            }
        }

        private bool ConfirmWarnings(List<string> warnings)
        {
            bool hard = warnings.Any(PlacementGuard.IsHard);
            var td = new TaskDialog(Title)
            {
                MainInstruction = hard ? "This location is NOT allowed by the manual" : "This location breaks a rule from the manual",
                MainContent = "- " + string.Join(Environment.NewLine + "- ", warnings.Select(PlacementGuard.Clean)) +
                              Environment.NewLine + Environment.NewLine +
                              (hard ? "Place anyway? (You will have to justify this to the reviewer.)" : "Place anyway?"),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            return td.Show() == TaskDialogResult.Yes;
        }

        // ---- helpers for subclasses ----

        protected static InputForm.Field Num(string key, string label, string def = null) =>
            new InputForm.Field { Key = key, Label = label, Default = def };

        protected static InputForm.Field Txt(string key, string label, string def = null) =>
            new InputForm.Field { Key = key, Label = label, Default = def, Numeric = false };

        protected static InputForm Ask(string title, string info, params InputForm.Field[] fields)
        {
            var f = new InputForm(title, info, fields);
            return f.ShowDialog() == DialogResult.OK ? f : null;
        }
    }
}
