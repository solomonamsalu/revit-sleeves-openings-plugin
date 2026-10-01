using Newtonsoft.Json;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// rules.json "pdfOnly": Auto Run when the engineer sent only the PDF (no DWG, no xrefs at Revit's 0,0). The PDF must
    /// be a vector plot from CAD: the pipes, leaders and columns are read from its layers, and each floor page is lined up
    /// with Revit by its columns (drawn on the plans from the architectural background) against the model's columns.
    /// Distances in inches (real size, after the sheet scale).
    /// </summary>
    public class PdfOnlyRules
    {
        /// <summary>Leave the DWG empty to run from the PDF only (plumbing models).</summary>
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

        /// <summary>Mechanical: layers of the size labels' leaders (a line with a filled arrowhead pointing at the riser).</summary>
        [JsonProperty("leaderLayers")] public string LeaderLayers { get; set; } = "LEADER|LEDR";

        /// <summary>
        /// Mechanical: a crossed circle up to this diameter (inches) is a small round duct (dryer, vent) that the DWG draws
        /// as a centre block: it groups with its neighbours into one riser instead of being a section mark of its own.
        /// </summary>
        [JsonProperty("roundRiserMaxSize")] public double RoundRiserMaxSize { get; set; } = 6;

        /// <summary>Regex on the PDF layers holding the columns ("...|A-Collumns", "S-COLS").</summary>
        [JsonProperty("columnLayers")] public string ColumnLayers { get; set; } = @"COL+UMN|\bS-COL";
        /// <summary>Column rectangles outside this size range (each side) are not columns (a wall or beam drawn on the layer).</summary>
        [JsonProperty("columnMinSize")] public double ColumnMinSize { get; set; } = 6;
        [JsonProperty("columnMaxSize")] public double ColumnMaxSize { get; set; } = 48;

        /// <summary>Tag bubbles: circles in this radius range at the end of a leader.</summary>
        [JsonProperty("bubbleMinRadius")] public double BubbleMinRadius { get; set; } = 6;
        [JsonProperty("bubbleMaxRadius")] public double BubbleMaxRadius { get; set; } = 30;

        /// <summary>Real inches per sheet inch when a page shows no "SCALE: 1/4" = 1'-0"" text (48 = 1/4" = 1'-0").</summary>
        [JsonProperty("defaultScale")] public double DefaultScale { get; set; } = 48;

        /// <summary>A PDF column this close to a Revit column (after lining up) is the same column. The plans' architectural
        /// background and the model are often different revisions, 1-2" apart.</summary>
        [JsonProperty("columnTolerance")] public double ColumnTolerance { get; set; } = 2;
        /// <summary>Wider tolerance used while searching for the match (the final check uses columnTolerance).</summary>
        [JsonProperty("searchTolerance")] public double SearchTolerance { get; set; } = 3;
        /// <summary>Columns that must land on Revit columns, spread over both directions of the building, before a floor is used.</summary>
        [JsonProperty("minColumns")] public int MinColumns { get; set; } = 4;
        /// <summary>The matched columns must spread at least this far (feet) along X and along Y.</summary>
        [JsonProperty("minSpread")] public double MinSpread { get; set; } = 10;
        /// <summary>The columns' own fit may differ from the printed scale by this much (0.005 = 0.5%).</summary>
        [JsonProperty("scaleTolerance")] public double ScaleTolerance { get; set; } = 0.005;
        /// <summary>A floor with too few columns is lined up by its pipes stacked on a lined-up floor next to it: this many, within stackTolerance.</summary>
        [JsonProperty("minStacked")] public int MinStacked { get; set; } = 4;
        [JsonProperty("stackTolerance")] public double StackTolerance { get; set; } = 1;

        /// <summary>true = sleeves from a floor that passed every check are placed; false = every PDF-only sleeve goes to review.</summary>
        [JsonProperty("placeWhenPassed")] public bool PlaceWhenPassed { get; set; } = true;
    }
}
