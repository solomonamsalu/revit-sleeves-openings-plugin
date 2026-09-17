using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Commands
{
    /// <summary>Re-reads rules.json (after the user edits it) and shows a summary.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ReloadRulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var doc = data.Application.ActiveUIDocument?.Document;
                var rules = App.ReloadRules(doc);
                var s = rules.Systems;
                var c = rules.Clearances;
                string F(double inches) => Units.FormatInches(inches);

                var summary =
                    $"Loaded from:\n{rules.SourcePath}\n\n" +
                    $"Exhaust: duct + {F(s.Exhaust.ClearanceEachSide)} each side; roof +{F(s.Exhaust.RoofIncreaseTotal)} total\n" +
                    $"Garbage chute: {F(s.GarbageChute.FixedWidth)} x {F(s.GarbageChute.FixedLength)} fixed\n" +
                    $"Dryer exhaust: {F(s.DryerExhaust.Diameter)} round '{rules.Naming.DryerExhaust}'; roof {F(s.DryerExhaust.RoofOpeningWidth)} x {F(s.DryerExhaust.RoofOpeningLength)}, min {F(s.DryerExhaust.MinSpacing)} apart\n" +
                    $"Motorized damper: + {F(s.MotorizedDamper.ClearanceEachSide)} each side\n" +
                    $"Refrigeration: Down Height {F(s.Refrigeration.DownHeight)}; {s.Refrigeration.LinesPerIndoorUnitSplit} lines/unit (split); roof +{F(s.Refrigeration.RoofExtraWidth)} W/L\n" +
                    $"Electrical: {F(s.Electrical.ConduitSpacingCenterToCenter)} c-c, +{s.Electrical.ExtraCirclesForRoof} for roof, roof sleeve {F(s.Electrical.RoofSleeveDiameter)} '{rules.Naming.Electrical}'\n" +
                    $"Storm: pipe + {F(s.Storm.SleeveOverPipe)}; AD {s.Storm.AreaDrainCount} x {F(s.Storm.AreaDrainDiameter)} @ {F(s.Storm.AreaDrainSpacingCenterToCenter)} c-c; deck edge {F(s.Storm.DetailDeckEdgeOffset)}\n" +
                    $"Condensate: {F(s.Condensate.SleeveDiameter)}\n" +
                    $"Standpipe: pipe + {F(s.Standpipe.SleeveOverPipe)}; {F(s.Standpipe.MinCenterToWall)} to wall; {F(s.Standpipe.MinCenterToCenter)} c-c\n\n" +
                    $"Clearances: column {F(c.MinFromColumn)}; roof wall/curb {F(c.RoofMinFromWallOrCurb)}; roof openings {F(c.RoofMinBetweenOpenings)}; ERV {F(c.ErvSpacingExact)}";

                new TaskDialog("Sleeves & Openings rules") { MainInstruction = "Rules reloaded", MainContent = summary }.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Opens the active rules.json in the default editor (creates the user copy if only the default exists).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class EditRulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var doc = data.Application.ActiveUIDocument?.Document;
                var rules = App.Rules(doc);
                var path = rules.SourcePath;

                if (string.Equals(path, RuleLoader.DefaultRulesPath, StringComparison.OrdinalIgnoreCase))
                {
                    var td = new TaskDialog("Edit rules")
                    {
                        MainInstruction = "You are using the shipped default rules.",
                        MainContent = "Create an editable copy?\n\n" + RuleLoader.UserRulesPath,
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                    };
                    if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;
                    Directory.CreateDirectory(Path.GetDirectoryName(RuleLoader.UserRulesPath));
                    File.Copy(RuleLoader.DefaultRulesPath, RuleLoader.UserRulesPath, overwrite: false);
                    path = RuleLoader.UserRulesPath;
                }

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
