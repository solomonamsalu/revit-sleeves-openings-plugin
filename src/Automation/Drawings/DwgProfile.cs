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

        /// <summary>A classic LEADER not linked to its note reads the text nearest its tail (last vertex), up to this far (inches).</summary>
        [JsonProperty("leaderTextDistance")] public double LeaderTextDistance { get; set; } = 12;

        /// <summary>A note this close (inches) to a riser with no tag and no label says what it is (e.g. combustion air shafts).</summary>
        [JsonProperty("maxNoteDistance")] public double MaxNoteDistance { get; set; } = 120;

        /// <summary>Regex on layer names where duct outlines are drawn (turns rectangular openings the way the duct runs).</summary>
        [JsonProperty("ductLayers")] public string DuctLayers { get; set; } = "DUCT";

        /// <summary>
        /// A riser may also be drawn on a duct layer as a section mark (rectangle or circle crossed by a diagonal). Marks
        /// outside this size range (inches, each side / diameter) are not risers (grilles, equipment).
        /// </summary>
        [JsonProperty("sectionMarkMinSize")] public double SectionMarkMinSize { get; set; } = 3;
        [JsonProperty("sectionMarkMaxSize")] public double SectionMarkMaxSize { get; set; } = 60;

        /// <summary>A centre block this close (inches) to a section mark, or inside it, is the same duct.</summary>
        [JsonProperty("outlineTolerance")] public double OutlineTolerance { get; set; } = 1;

        /// <summary>Regex on folder names holding the per-floor drawings at Revit's 0,0 (office xrefs), searched next to the model.</summary>
        [JsonProperty("referenceFolders")] public string ReferenceFolders { get; set; } = @"^XREF\s*-?\s*(ME|M|MECH|MECHANICAL|HVAC|HV)$";

        /// <summary>Regex on the file name of the office grid-lines DWG (same 0,0 as the xrefs), used to prove the Revit position against the Revit grids.</summary>
        [JsonProperty("gridFiles")] public string GridFiles { get; set; } = @"\bGRID";

        // ---- plumbing-style drawings (all off by default, so the mechanical profile reads as before)

        /// <summary>
        /// Regex on layer names where a riser is drawn as a plain circle (one per pipe: the plumbing engineer draws a
        /// circle on the sanitary, vent, storm... layer). The layer says which pipe it is. Null = no circle risers.
        /// </summary>
        [JsonProperty("riserCircleLayers")] public string RiserCircleLayers { get; set; }
        /// <summary>Circles outside this radius range (inches) on those layers are not risers (drains, equipment, bubbles).</summary>
        [JsonProperty("riserCircleMinRadius")] public double RiserCircleMinRadius { get; set; } = 1;
        [JsonProperty("riserCircleMaxRadius")] public double RiserCircleMaxRadius { get; set; } = 4;

        /// <summary>Classic LEADERs on a connector layer join a tag bubble to its riser (arrow at the riser), like connector lines.</summary>
        [JsonProperty("leadersAsConnectors")] public bool LeadersAsConnectors { get; set; }

        /// <summary>
        /// Free text this close (inches) to a tag bubble tied to a riser is that riser's service list ("S UP &amp; DN / V RISE
        /// &amp; DN"), kept in <see cref="DwgRiser.TagTexts"/>. 0 = off.
        /// </summary>
        [JsonProperty("tagTextDistance")] public double TagTextDistance { get; set; }

        /// <summary>Regex on single-word text naming a fixture at its spot ("WC", "LAV", "BT"); kept per floor in <see cref="DwgRiserResult.Fixtures"/>. Null = off.</summary>
        [JsonProperty("fixtureText")] public string FixtureText { get; set; }

        // ---- architectural drawings (electrical riser: how many apartments each floor holds, where the electrical room is)

        /// <summary>Regex on folder names holding the per-floor architectural xrefs at Revit's 0,0, searched next to the model.</summary>
        [JsonProperty("architecturalFolders")] public string ArchitecturalFolders { get; set; } = @"^XREF\s*-?\s*(AR|A|ARCH|ARCHITECTURAL)$";

        /// <summary>Regex on the block name (or a dynamic block's source name) of the apartment number tag. One insert = one apartment.</summary>
        [JsonProperty("apartmentTagBlocks")] public string ApartmentTagBlocks { get; set; } = @"^APT\s*TAG$";

        /// <summary>Regex on layer names carrying the apartment tags; a block on this layer counts even when its name does not match.</summary>
        [JsonProperty("apartmentTagLayers")] public string ApartmentTagLayers { get; set; } = "APARTMENT";

        /// <summary>Attribute tags holding the apartment number ("#" -> 401). Any attribute whose value reads as a number is used as a fallback.</summary>
        [JsonProperty("apartmentNumberAttributes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> ApartmentNumberAttributes { get; set; } = new List<string> { "#", "APT#", "APTNO", "NUMBER", "NO", "UNIT" };

        /// <summary>Regex on text naming the electrical/meter room, which is where the riser starts (electrical rule 3). Null = off.</summary>
        [JsonProperty("electricalRoomText")] public string ElectricalRoomText { get; set; } = @"^ELEC(TRIC|TRICAL)?\.?\s*(ROOM|RM\.?|CLOSET|CL\.?)?$|^METER\s*(ROOM|RM\.?|BANK)?$";

        /// <summary>
        /// Regex on text naming the mechanical room. The manual's note under the electrical rules: with no electrical
        /// room, the electrical services come from the mechanical room and the openings go as close to it as possible.
        /// </summary>
        [JsonProperty("mechanicalRoomText")] public string MechanicalRoomText { get; set; } = @"^MECH(ANICAL)?\.?\s*(ROOM|RM\.?|EQUIP(MENT)?)?$|^BOILER\s*(ROOM|RM\.?)?$";
    }
}
