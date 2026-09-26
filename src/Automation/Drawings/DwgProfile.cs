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

        /// <summary>
        /// A classic LEADER not hard-linked to its note reads the text nearest its tail (last vertex), up to this far (inches).
        /// </summary>
        [JsonProperty("leaderTextDistance")] public double LeaderTextDistance { get; set; } = 12;

        /// <summary>
        /// Regex on layer names where a riser may be drawn as a section mark (rectangle or circle crossed by a diagonal)
        /// instead of a centre block. Empty = section marks are not read.
        /// </summary>
        [JsonProperty("sectionMarkLayers")] public string SectionMarkLayers { get; set; } = "DUCT";

        /// <summary>Section marks outside this size range (inches, each side / diameter) are not risers (grilles, equipment).</summary>
        [JsonProperty("sectionMarkMinSize")] public double SectionMarkMinSize { get; set; } = 3;
        [JsonProperty("sectionMarkMaxSize")] public double SectionMarkMaxSize { get; set; } = 60;

        /// <summary>A centre block this close (inches) to a section mark, or inside it, is the same duct.</summary>
        [JsonProperty("outlineTolerance")] public double OutlineTolerance { get; set; } = 1;

        /// <summary>
        /// Regex on layer names of duct runs. A riser with no tag and no label, joined by such a line to a riser that has
        /// them (both line ends within <see cref="DuctLinkTolerance"/>), takes its tag or label. Empty = not used.
        /// </summary>
        [JsonProperty("ductLinkLayers")] public string DuctLinkLayers { get; set; } = "DUCT";
        [JsonProperty("ductLinkTolerance")] public double DuctLinkTolerance { get; set; } = 3;
    }
}
