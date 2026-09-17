using System.Collections.Generic;
using Newtonsoft.Json;

namespace SleevesOpenings.Rules
{
    /// <summary>
    /// Strongly-typed view of rules.json. All lengths are in inches; convert with <see cref="Units"/>.
    /// </summary>
    public class RuleSet
    {
        [JsonProperty("version")] public int Version { get; set; }
        [JsonProperty("families")] public Dictionary<string, FamilyRule> Families { get; set; } = new Dictionary<string, FamilyRule>();
        [JsonProperty("naming")] public NamingRules Naming { get; set; } = new NamingRules();
        [JsonProperty("systems")] public SystemRules Systems { get; set; } = new SystemRules();
        [JsonProperty("clearances")] public ClearanceRules Clearances { get; set; } = new ClearanceRules();
        [JsonProperty("viewRange")] public ViewRangeRules ViewRange { get; set; } = new ViewRangeRules();
        [JsonProperty("levelClassification")] public LevelClassificationRules LevelClassification { get; set; } = new LevelClassificationRules();
        [JsonProperty("preflight")] public List<string> Preflight { get; set; } = new List<string>();

        /// <summary>Where this rule set was loaded from (for display/debugging).</summary>
        [JsonIgnore] public string SourcePath { get; set; }

        public FamilyRule Family(string key) =>
            Families.TryGetValue(key, out var f) ? f : null;
    }

    public class FamilyRule
    {
        [JsonProperty("family")] public string Family { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("file")] public string File { get; set; }
        [JsonProperty("widthParam")] public string WidthParam { get; set; }
        [JsonProperty("lengthParam")] public string LengthParam { get; set; }
        [JsonProperty("diameterParam")] public string DiameterParam { get; set; }
        [JsonProperty("nameParam")] public string NameParam { get; set; }
        [JsonProperty("downHeightParam")] public string DownHeightParam { get; set; }
    }

    public class NamingRules
    {
        [JsonProperty("electrical")] public string Electrical { get; set; } = "ELECTRIC";
        [JsonProperty("dryerExhaust")] public string DryerExhaust { get; set; } = "DE";
        [JsonProperty("exhaustPattern")] public string ExhaustPattern { get; set; } = "{riser}";
    }

    public class SystemRules
    {
        [JsonProperty("exhaust")] public ExhaustRules Exhaust { get; set; } = new ExhaustRules();
        [JsonProperty("garbageChute")] public GarbageChuteRules GarbageChute { get; set; } = new GarbageChuteRules();
        [JsonProperty("dryerExhaust")] public DryerExhaustRules DryerExhaust { get; set; } = new DryerExhaustRules();
        [JsonProperty("motorizedDamper")] public MotorizedDamperRules MotorizedDamper { get; set; } = new MotorizedDamperRules();
        [JsonProperty("refrigeration")] public RefrigerationRules Refrigeration { get; set; } = new RefrigerationRules();
        [JsonProperty("electrical")] public ElectricalRules Electrical { get; set; } = new ElectricalRules();
        [JsonProperty("storm")] public StormRules Storm { get; set; } = new StormRules();
        [JsonProperty("condensate")] public CondensateRules Condensate { get; set; } = new CondensateRules();
        [JsonProperty("standpipe")] public StandpipeRules Standpipe { get; set; } = new StandpipeRules();
        [JsonProperty("bathtub")] public BathtubRules Bathtub { get; set; } = new BathtubRules();
    }

    public class ExhaustRules
    {
        [JsonProperty("clearanceEachSide")] public double ClearanceEachSide { get; set; } = 2;
        [JsonProperty("roofIncreaseTotal")] public double RoofIncreaseTotal { get; set; } = 4;
        [JsonProperty("floorPlanWinsOverRiserDiagram")] public bool FloorPlanWinsOverRiserDiagram { get; set; } = true;

        /// <summary>Opening size for a duct of the given size (inches).</summary>
        public double OpeningSize(double ductSize) => ductSize + 2 * ClearanceEachSide;
    }

    public class GarbageChuteRules
    {
        [JsonProperty("fixedWidth")] public double FixedWidth { get; set; } = 28.5;
        [JsonProperty("fixedLength")] public double FixedLength { get; set; } = 28.5;
        [JsonProperty("roofClearance")] public double RoofClearance { get; set; } = 0;
        [JsonProperty("allowOffset")] public bool AllowOffset { get; set; } = false;
    }

    public class DryerExhaustRules
    {
        [JsonProperty("radius")] public double Radius { get; set; } = 2;
        [JsonProperty("roofOpeningWidth")] public double RoofOpeningWidth { get; set; } = 6;
        [JsonProperty("roofOpeningLength")] public double RoofOpeningLength { get; set; } = 6;
        [JsonProperty("minSpacing")] public double MinSpacing { get; set; } = 8;
        public double Diameter => Radius * 2;
    }

    public class MotorizedDamperRules
    {
        [JsonProperty("clearanceEachSide")] public double ClearanceEachSide { get; set; } = 2;
        public double OpeningSize(double damperSize) => damperSize + 2 * ClearanceEachSide;
    }

    public class RefrigerationRules
    {
        [JsonProperty("downHeight")] public double DownHeight { get; set; } = 0;
        [JsonProperty("linesPerIndoorUnitSplit")] public int LinesPerIndoorUnitSplit { get; set; } = 2;
        [JsonProperty("roofExtraWidth")] public double RoofExtraWidth { get; set; } = 3;
        [JsonProperty("roofExtraLength")] public double RoofExtraLength { get; set; } = 3;
    }

    public class ElectricalRules
    {
        [JsonProperty("conduitSpacingCenterToCenter")] public double ConduitSpacingCenterToCenter { get; set; } = 0.75;
        [JsonProperty("extraCirclesForRoof")] public int ExtraCirclesForRoof { get; set; } = 1;
        [JsonProperty("roofSleeveDiameter")] public double RoofSleeveDiameter { get; set; } = 2;

        /// <summary>Conduit circles needed at a floor = apartments served above that floor + roof.</summary>
        public int CirclesFor(int apartmentsAbove) => apartmentsAbove + ExtraCirclesForRoof;
    }

    public class StormRules
    {
        [JsonProperty("sleeveOverPipe")] public double SleeveOverPipe { get; set; } = 2;
        [JsonProperty("areaDrainDiameter")] public double AreaDrainDiameter { get; set; } = 10;
        [JsonProperty("areaDrainCount")] public int AreaDrainCount { get; set; } = 2;
        [JsonProperty("areaDrainSpacingCenterToCenter")] public double AreaDrainSpacingCenterToCenter { get; set; } = 24;
        [JsonProperty("detailDeckEdgeOffset")] public double DetailDeckEdgeOffset { get; set; } = 12;
        public double SleeveSize(double pipeSize) => pipeSize + SleeveOverPipe;
    }

    public class CondensateRules
    {
        [JsonProperty("sleeveDiameter")] public double SleeveDiameter { get; set; } = 3;
    }

    public class StandpipeRules
    {
        [JsonProperty("sleeveOverPipe")] public double SleeveOverPipe { get; set; } = 2;
        [JsonProperty("minCenterToWall")] public double MinCenterToWall { get; set; } = 5.5;
        [JsonProperty("minCenterToCenter")] public double MinCenterToCenter { get; set; } = 8;
        public double SleeveSize(double pipeSize) => pipeSize + SleeveOverPipe;
    }

    public class BathtubRules
    {
        [JsonProperty("options")] public Dictionary<string, BathtubOption> Options { get; set; } = new Dictionary<string, BathtubOption>();
        [JsonProperty("default")] public string Default { get; set; }
    }

    public class BathtubOption
    {
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("diameter")] public double Diameter { get; set; }
    }

    public class ClearanceRules
    {
        [JsonProperty("minFromColumn")] public double MinFromColumn { get; set; } = 12;
        [JsonProperty("minFromWallEdge")] public double MinFromWallEdge { get; set; } = 4;
        [JsonProperty("roofMinFromWallOrCurb")] public double RoofMinFromWallOrCurb { get; set; } = 12;
        [JsonProperty("roofMinBetweenOpenings")] public double RoofMinBetweenOpenings { get; set; } = 24;
        [JsonProperty("ervSpacingExact")] public double ErvSpacingExact { get; set; } = 24;
    }

    public class ViewRangeRules
    {
        [JsonProperty("topLevelAboveOffset")] public double TopLevelAboveOffset { get; set; } = 0;
        [JsonProperty("bottomAssociatedLevelOffset")] public double BottomAssociatedLevelOffset { get; set; } = 0;
    }

    public class LevelClassificationRules
    {
        [JsonProperty("roof")] public string Roof { get; set; } = "roof";
        [JsonProperty("bulkhead")] public string Bulkhead { get; set; } = "bulkhead";
        [JsonProperty("setback")] public string Setback { get; set; } = "setback|terrace";
        [JsonProperty("cellar")] public string Cellar { get; set; } = "cellar|basement";
    }
}
