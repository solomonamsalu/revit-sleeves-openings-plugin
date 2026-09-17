using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using SleevesOpenings.Placement;
using OperationCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace SleevesOpenings.Commands
{
    // ------------------------------------------------------------------ Exhaust (rules 12-16, 56)

    [Transaction(TransactionMode.Manual)]
    public class PlaceExhaustCommand : PlaceCommandBase
    {
        protected override string Title => "Exhaust Opening";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.Exhaust;
            string roofNote = ctx.OnRoof ? $" This is a roof level: +{Units.FormatInches(r.RoofIncreaseTotal)} total is added (rule 56)." : "";
            var f = Ask(Title,
                $"Enter the duct size from the floor plan. {Units.FormatInches(r.ClearanceEachSide)} clearance is added on each side (rule 16).{roofNote}",
                Num("w", "Duct width (in)"), Num("h", "Duct height (in)"), Txt("riser", "Riser name", "KX-1"));
            if (f == null) return null;

            double extra = ctx.OnRoof ? r.RoofIncreaseTotal : 0;
            return OpeningSpec.Rect(SystemKind.Exhaust, FamilyRole.RegularOpening,
                r.OpeningSize(f.Inches("w")) + extra, r.OpeningSize(f.Inches("h")) + extra,
                ctx.Rules.Naming.ExhaustPattern.Replace("{riser}", f.Value("riser")));
        }
    }

    // ------------------------------------------------------------------ Garbage chute (rules 34-44)

    [Transaction(TransactionMode.Manual)]
    public class PlaceGarbageChuteCommand : PlaceCommandBase
    {
        protected override string Title => "Garbage Chute";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.GarbageChute;
            return OpeningSpec.Rect(SystemKind.GarbageChute, FamilyRole.RegularOpening, r.FixedWidth, r.FixedLength, "GARBAGE CHUTE");
        }
    }

    // ------------------------------------------------------------------ Dryer exhaust (rules 68-82)

    [Transaction(TransactionMode.Manual)]
    public class PlaceDryerExhaustCommand : PlaceCommandBase
    {
        protected override string Title => "Dryer Exhaust";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.DryerExhaust;
            if (ctx.OnRoof)   // rule 78: 6" x 6" opening per 4" dryer exhaust at the roof
                return OpeningSpec.Rect(SystemKind.DryerExhaust, FamilyRole.RegularOpening, r.RoofOpeningWidth, r.RoofOpeningLength, ctx.Rules.Naming.DryerExhaust);
            return OpeningSpec.Round(SystemKind.DryerExhaust, r.Diameter, ctx.Rules.Naming.DryerExhaust);
        }
    }

    // ------------------------------------------------------------------ Motorized damper (rules 83-87)

    [Transaction(TransactionMode.Manual)]
    public class PlaceMotorizedDamperCommand : PlaceCommandBase
    {
        protected override string Title => "Motorized Damper";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.MotorizedDamper;
            var f = Ask(Title, $"Damper size from the mechanical plan. {Units.FormatInches(r.ClearanceEachSide)} clearance each side (rule 84).",
                Num("w", "Damper width (in)"), Num("h", "Damper height (in)"));
            if (f == null) return null;
            return OpeningSpec.Rect(SystemKind.MotorizedDamper, FamilyRole.RegularOpening,
                r.OpeningSize(f.Inches("w")), r.OpeningSize(f.Inches("h")), "MOTORIZED DAMPER");
        }
    }

    // ------------------------------------------------------------------ Refrigeration (quick rules 7, 23)

    [Transaction(TransactionMode.Manual)]
    public class PlaceRefrigerationCommand : PlaceCommandBase
    {
        protected override string Title => "Refrigeration Opening";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.Refrigeration;
            string roofNote = ctx.OnRoof ? $" Roof: +{Units.FormatInches(r.RoofExtraWidth)} W and +{Units.FormatInches(r.RoofExtraLength)} L added for electrical conduits (rule 23)." : "";
            var f = Ask(Title, $"Pipe Reference Opening with Down Height = {Units.FormatInches(r.DownHeight)} (rule 7).{roofNote}",
                Num("w", "Opening width (in)"), Num("l", "Opening length (in)"), Num("n", "Refrigeration lines served", "2"), Txt("stack", "Apartment stack", "A"));
            if (f == null) return null;

            var spec = OpeningSpec.Rect(SystemKind.Refrigeration, FamilyRole.PipeReferenceOpening,
                f.Inches("w") + (ctx.OnRoof ? r.RoofExtraWidth : 0),
                f.Inches("l") + (ctx.OnRoof ? r.RoofExtraLength : 0),
                $"REF {f.Int("n")} LINES - STACK {f.Value("stack")}");
            spec.DownHeight = r.DownHeight;
            spec.Riser = "REF-" + f.Value("stack");
            return spec;
        }
    }

    // ------------------------------------------------------------------ Storm (quick rules 6, 11)

    [Transaction(TransactionMode.Manual)]
    public class PlaceStormCommand : PlaceCommandBase
    {
        protected override string Title => "Storm Sleeve";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.Storm;
            var f = Ask(Title, $"Sleeve size = pipe size + {Units.FormatInches(r.SleeveOverPipe)} (rule 11).",
                Num("pipe", "Storm pipe size (in)", "4"), Txt("riser", "Riser name", "ST-1"));
            if (f == null) return null;
            var spec = OpeningSpec.Round(SystemKind.Storm, r.SleeveSize(f.Inches("pipe")), $"STORM {f.Value("riser")}");
            spec.Riser = f.Value("riser");
            return spec;
        }
    }

    // ------------------------------------------------------------------ Area drain pair (quick rules 8-9 + note)

    [Transaction(TransactionMode.Manual)]
    public class PlaceAreaDrainCommand : PlaceCommandBase
    {
        protected override string Title => "Area Drain Sleeves";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.Storm;
            return OpeningSpec.Round(SystemKind.AreaDrain, r.AreaDrainDiameter, "AREA DRAIN");
        }

        protected override string PickPrompt(OpeningSpec spec) =>
            "Area Drain: click the primary drain, then click a point in the direction of the overflow drain. Esc to finish.";

        /// <summary>Always two 10" sleeves (primary + overflow) at 2'-0" c-c, even if the engineer shows one.</summary>
        protected override IList<FamilyInstance> PlaceAtClick(PlaceContext ctx, Placer placer, OpeningSpec spec,
                                                               FamilySymbol symbol, FamilyMapEntry map, XYZ pt)
        {
            var r = ctx.Rules.Systems.Storm;
            XYZ dir = XYZ.BasisX;
            try
            {
                var second = ctx.UiDoc.Selection.PickPoint(ObjectSnapTypes.None, "Click the direction of the overflow drain");
                var d = new XYZ(second.X - pt.X, second.Y - pt.Y, 0);
                if (d.GetLength() > 1e-6) dir = d.Normalize();
            }
            catch (OperationCanceledException) { /* default +X */ }

            double spacing = Units.InchesToFeet(r.AreaDrainSpacingCenterToCenter);
            var list = new List<FamilyInstance>();
            for (int i = 0; i < r.AreaDrainCount; i++)
            {
                var s = OpeningSpec.Round(SystemKind.AreaDrain, r.AreaDrainDiameter, i == 0 ? "AREA DRAIN (PRIMARY)" : "AREA DRAIN (OVERFLOW)");
                list.Add(placer.Place(s, symbol, map, ctx.Level, pt + dir * (spacing * i)));
            }
            return list;
        }
    }

    // ------------------------------------------------------------------ Condensate (quick rule 5)

    [Transaction(TransactionMode.Manual)]
    public class PlaceCondensateCommand : PlaceCommandBase
    {
        protected override string Title => "Condensate Sleeve";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            if (ctx.State.CondensateRequired == false)
            {
                var td = new Autodesk.Revit.UI.TaskDialog(Title)
                {
                    MainInstruction = "Project Setup says this building does NOT require condensate risers.",
                    MainContent = "Place anyway?",
                    CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Yes | Autodesk.Revit.UI.TaskDialogCommonButtons.No
                };
                if (td.Show() != Autodesk.Revit.UI.TaskDialogResult.Yes) return null;
            }
            var r = ctx.Rules.Systems.Condensate;
            return OpeningSpec.Round(SystemKind.Condensate, r.SleeveDiameter, "CONDENSATE");
        }
    }

    // ------------------------------------------------------------------ Standpipe (quick rules)

    [Transaction(TransactionMode.Manual)]
    public class PlaceStandpipeCommand : PlaceCommandBase
    {
        protected override string Title => "Standpipe Sleeve";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.Standpipe;
            var f = Ask(Title,
                $"Sleeve = pipe + {Units.FormatInches(r.SleeveOverPipe)}. Keep {Units.FormatInches(r.MinCenterToWall)} centerline-to-wall and {Units.FormatInches(r.MinCenterToCenter)} c-c between adjacent risers.",
                Num("pipe", "Pipe size (in)", "6"),
                new UI.InputForm.Field { Key = "kind", Label = "Riser type", Choices = new[] { "Standpipe Only", "Combined Standpipe/Sprinkler" }, Default = "Standpipe Only" },
                Txt("stair", "Stair", "A"));
            if (f == null) return null;
            string label = (f.Value("kind") == "Standpipe Only" ? "STANDPIPE" : "COMBINED SP/SPR") + " - STAIR " + f.Value("stair");
            var spec = OpeningSpec.Round(SystemKind.Standpipe, r.SleeveSize(f.Inches("pipe")), label);
            spec.Riser = "SP-" + f.Value("stair");
            return spec;
        }
    }

    // ------------------------------------------------------------------ Bathtub (page 18)

    [Transaction(TransactionMode.Manual)]
    public class PlaceBathtubCommand : PlaceCommandBase
    {
        protected override string Title => "Bathtub Sleeve";

        protected override OpeningSpec BuildSpec(PlaceContext ctx)
        {
            var r = ctx.Rules.Systems.Bathtub;
            string key = ctx.State.BathtubOption ?? r.Default;
            if (key == null || !r.Options.ContainsKey(key))
            {
                var choices = r.Options.Select(kv => $"{kv.Key}: {kv.Value.Count} x {Units.FormatInches(kv.Value.Diameter)}").ToArray();
                var f = Ask(Title, "The manual says to ask before starting. Choose how bathtub sleeves are done on this project (saved in Project Setup).",
                    new UI.InputForm.Field { Key = "opt", Label = "Bathtub sleeves", Choices = choices, Default = choices.FirstOrDefault() });
                if (f == null) return null;
                key = f.Value("opt").Split(':')[0];
            }
            var opt = r.Options[key];
            return OpeningSpec.Round(SystemKind.Bathtub, opt.Diameter, "BATHTUB");
        }

        protected override string PickPrompt(OpeningSpec spec)
        {
            var n = spec.Diameter == 6 ? "two 6\" sleeves — click once per sleeve" : "one 10\" sleeve";
            return $"Bathtub: {n}. Esc to finish.";
        }
    }
}
