using Newtonsoft.Json;

namespace SleevesOpenings.Automation
{
    /// <summary>rules.json "automation": what Auto Run does after placing, and the checks against the office's own sets.</summary>
    public class AutomationRules
    {
        /// <summary>Run Final Check on the openings this run placed and put what it finds in the report.</summary>
        [JsonProperty("finalCheck")] public bool FinalCheck { get; set; } = true;
        /// <summary>Apply Final Check fixes with one right answer (size, name, riser id) to this run's openings. Never moves, copies or deletes.</summary>
        [JsonProperty("autoFix")] public bool AutoFix { get; set; } = true;
        /// <summary>Read the engineer's riser diagram and report risers it shows that the floor plans do not (GX-1, GX-2…).</summary>
        [JsonProperty("riserDiagram")] public bool RiserDiagram { get; set; } = true;
        [JsonProperty("referenceSet")] public ReferenceSetRules ReferenceSet { get; set; } = new ReferenceSetRules();
        /// <summary>Decisions Auto Run takes by rule instead of leaving the opening for review.</summary>
        [JsonProperty("decisions")] public DecisionRules Decisions { get; set; } = new DecisionRules();
    }

    /// <summary>
    /// rules.json automation.decisions: cases the manual or the engineer's general notes settle, decided automatically
    /// (each one switchable). Distances in inches.
    /// </summary>
    public class DecisionRules
    {
        /// <summary>A crossing only the floor below shows, labelled UP there: placed straight above it (manual 23, 45-51: risers stay straight).</summary>
        [JsonProperty("trustUpFromBelow")] public bool TrustUpFromBelow { get; set; } = true;
        /// <summary>Dryer ducts that land on another opening make that opening bigger to take them (rule 74: dryer circles inside a mechanical opening box).</summary>
        [JsonProperty("dryersIntoHost")] public bool DryersIntoHost { get; set; } = true;
        /// <summary>Roof dryer shafts: one roof opening per duct (rule 78), laid out in straight rows (rules 79-82).</summary>
        [JsonProperty("roofDryerGrid")] public bool RoofDryerGrid { get; set; } = true;
        /// <summary>Roof dryer openings per row.</summary>
        [JsonProperty("roofDryerPerRow")] public int RoofDryerPerRow { get; set; } = 6;
        /// <summary>Openings on one slab that overlap become one opening around both (manual 21: shafts and chases), named with both tags.</summary>
        [JsonProperty("mergeOverlaps")] public bool MergeOverlaps { get; set; } = true;
        /// <summary>
        /// An opening in a beam / shear wall (rules 27-28), too close to a column (rule 26) or on a wall edge is first reshaped
        /// (same duct area, engineer's note: "duct dimensions can be reconfigured up to 4:1 maintaining the same area"), then
        /// moved up to maxShift; skipped only when neither clears it.
        /// </summary>
        [JsonProperty("avoidStructure")] public bool AvoidStructure { get; set; } = true;
        [JsonProperty("maxAspect")] public double MaxAspect { get; set; } = 4;
        [JsonProperty("maxShift")] public double MaxShift { get; set; } = 24;
        [JsonProperty("shiftStep")] public double ShiftStep { get; set; } = 2;
    }

    /// <summary>
    /// The office's Sleeves &amp; Openings set (PDF, one sheet per floor) that the result is compared with (plan section 11,
    /// phase 8): its HVAC openings are the rectangles drawn in the colour its legend gives "HVAC OPENINGS"; each sheet is
    /// lined up with Revit by its grid bubbles. Distances in inches.
    /// </summary>
    public class ReferenceSetRules
    {
        /// <summary>
        /// Off in normal use: the S&amp;O set is the result of the automation, so a new project has none. Turn on to test
        /// the automation on a finished past project (its set is compared with what Auto Run finds).
        /// </summary>
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        /// <summary>Regex on PDF file names: found in the project folder when no set was chosen.</summary>
        [JsonProperty("files")] public string Files { get; set; } = @"SLEEVES\s*(&|AND)?\s*OPENINGS";
        /// <summary>The legend row whose swatch colour marks the openings to compare.</summary>
        [JsonProperty("legendText")] public string LegendText { get; set; } = "HVAC OPENINGS";
        /// <summary>Largest text (points) read as an opening's tag; room names and titles are bigger.</summary>
        [JsonProperty("maxTagSize")] public double MaxTagSize { get; set; } = 8;
        /// <summary>An opening of ours this close (centre to centre) to one in the set is the same opening.</summary>
        [JsonProperty("matchRadius")] public double MatchRadius { get; set; } = 24;
        /// <summary>Closer than this is "same position".</summary>
        [JsonProperty("positionTolerance")] public double PositionTolerance { get; set; } = 3;
        /// <summary>Sizes within this are the same.</summary>
        [JsonProperty("sizeTolerance")] public double SizeTolerance { get; set; } = 1;
        /// <summary>Every grid bubble must land this close to its Revit grid, or the sheet is not compared.</summary>
        [JsonProperty("gridTolerance")] public double GridTolerance { get; set; } = 6;
    }
}
