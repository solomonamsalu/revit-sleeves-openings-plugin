using System.Collections.Generic;
using Newtonsoft.Json;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>
    /// rules.json "dwgProfile": how an engineer draws risers in the DWG. Defaults match 24 Skillman's mechanical
    /// engineer (Precision Engineering Group); another engineer gets different block/layer names, not new code.
    /// </summary>
    public class DwgProfile
    {
        /// <summary>Regex on the block name (or a dynamic block's source name) of the symbol drawn at a riser's centre.</summary>
        [JsonProperty("riserBlocks")] public string RiserBlocks { get; set; } = "^Riser Center$";

        /// <summary>Regex on the block name of the riser tag bubble.</summary>
        [JsonProperty("tagBlocks")] public string TagBlocks { get; set; } = "^DUCT RISER$";

        /// <summary>Attribute tags of the bubble, joined in this order to form the riser name (TX + 1 -> TX1, ERV + SA -> ERV-SA).</summary>
        [JsonProperty("tagAttributes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> TagAttributes { get; set; } = new List<string> { "RISERTAG", "RISERCOUNT" };

        /// <summary>Regex on layer names of the lines drawn from a bubble to its riser.</summary>
        [JsonProperty("connectorLayers")] public string ConnectorLayers { get; set; } = "RISER";

        /// <summary>Symbols closer than this (inches) belong to one riser group (ducts side by side in a shaft).</summary>
        [JsonProperty("groupDistance")] public double GroupDistance { get; set; } = 12;

        /// <summary>A leader arrow or connector end this close (inches) to a riser symbol points at it.</summary>
        [JsonProperty("pointTolerance")] public double PointTolerance { get; set; } = 15;

        /// <summary>Without a connector, a tag bubble or size text this close (inches) to a riser belongs to it.</summary>
        [JsonProperty("maxTagDistance")] public double MaxTagDistance { get; set; } = 36;

        /// <summary>A note this close (inches) to a riser with no tag and no label says what it is (e.g. combustion air shafts).</summary>
        [JsonProperty("maxNoteDistance")] public double MaxNoteDistance { get; set; } = 120;

        /// <summary>Regex on layer names where duct outlines are drawn (turns rectangular openings the way the duct runs).</summary>
        [JsonProperty("ductLayers")] public string DuctLayers { get; set; } = "DUCT";

        /// <summary>Regex on folder names holding the per-floor drawings at Revit's 0,0 (office xrefs), searched next to the model.</summary>
        [JsonProperty("referenceFolders")] public string ReferenceFolders { get; set; } = @"^XREF\s*-?\s*(ME|M|MECH|MECHANICAL|HVAC|HV)$";

        /// <summary>Regex on the file name of the office grid-lines DWG (same 0,0 as the xrefs), used to prove the Revit position against the Revit grids.</summary>
        [JsonProperty("gridFiles")] public string GridFiles { get; set; } = @"\bGRID";
    }
}
