using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// rules.json "plumbing": Auto Run for the PL model. The plumbing engineer draws each pipe that goes through a slab
    /// as a small circle on its system's layer, groups them in a chase with a tag bubble (P1, P2...) and writes the
    /// services next to the bubble, one per line ("S UP &amp; DN", "V RISE &amp; DN", "ST DN"). Sizes are not on the plans.
    /// </summary>
    public class PlumbingRules
    {
        /// <summary>sprinkler only: false = the PL model's Auto Run does not offer sprinkler / standpipe sleeves.</summary>
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

        /// <summary>Regex on the model name: a model matching it gets plumbing Auto Run ("24 Skillman PL.rvt").</summary>
        [JsonProperty("modelMatch")] public string ModelMatch { get; set; } = @"(\bPL(\b|_)|\bPLUMB)";

        /// <summary>How the plumbing engineer draws risers (circle layers, bubble block, leader layer, fixture codes).</summary>
        [JsonProperty("dwgProfile")] public DwgProfile Profile { get; set; } = new DwgProfile
        {
            RiserBlocks = null, TagBlocks = "^P4-CIRCL$", TagAttributes = new List<string> { "SP", "1" }, ConnectorLayers = "LEADER",
            LeadersAsConnectors = true, TagTextDistance = 48, DuctLayers = null,
            RiserCircleLayers = "^P-(SANITARY|VENT|STORM|GAS|COLD WATER|HOT WATER|HOT WATER RETURN)(-N)?$",
            FixtureText = @"^(WC|LAV|BT|SH|KS|LS|W/D|WD|FD|UR|MS|DW|SK)$",
            ReferenceFolders = @"^XREF\s*-?\s*(PL|P|PLUMB|PLUMBING)$"
        };

        /// <summary>Service letters as written on the plans ("S", "V", "ST"...): what each is and whether it gets a sleeve.</summary>
        [JsonProperty("services", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, PipeService> Services { get; set; } = new Dictionary<string, PipeService>(StringComparer.OrdinalIgnoreCase)
        {
            ["S"] = new PipeService { System = "Sanitary", Layer = "SANITARY", Sleeve = true, Pipe = 4 },
            ["V"] = new PipeService { System = "Vent", Layer = "VENT", Sleeve = true, Pipe = 4 },
            ["ST"] = new PipeService { System = "Storm", Layer = "STORM", Sleeve = true, Pipe = 4 },
            ["G"] = new PipeService { System = "Gas", Layer = "GAS", Sleeve = true, Pipe = 1 },
            // the office S&O sets draw a 3" hot and a 3" cold sleeve beside each sanitary stack (24 Skillman 7-20-26): pipe 1" + 2"
            ["CW"] = new PipeService { System = "ColdWater", Layer = "COLD WATER", Sleeve = true, Pipe = 1 },
            ["HW"] = new PipeService { System = "HotWater", Layer = "HOT WATER(?! RETURN)", Sleeve = true, Pipe = 1 },
            ["HWR"] = new PipeService { System = "HotWaterReturn", Layer = "HOT WATER RETURN", Sleeve = false, Pipe = 1 }
        };

        /// <summary>Words after a service letter: which slab the pipe goes through (up = the slab above, down / through = this floor's slab).</summary>
        [JsonProperty("upWords", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> UpWords { get; set; } = new List<string> { "UP", "RISE", "RISER" };
        [JsonProperty("downWords", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> DownWords { get; set; } = new List<string> { "DN", "DOWN", "DROP" };
        [JsonProperty("throughWords", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> ThroughWords { get; set; } = new List<string> { "THRU", "THROUGH" };

        /// <summary>
        /// A label with no UP, DN or THRU: null = reported, nothing placed (plumbing); "DN" = the pipe ends on this floor and
        /// goes through its slab (sprinkler: the roof plan's '3" SPRINKLER RISER W/ INSPECTOR TEST TEE').
        /// </summary>
        [JsonProperty("unmarked")] public string Unmarked { get; set; }

        /// <summary>Fixtures named on the plans: the drain sleeve under each (pipe size, how many sleeves, their spacing).</summary>
        [JsonProperty("fixtures", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, FixtureSleeve> Fixtures { get; set; } = new Dictionary<string, FixtureSleeve>(StringComparer.OrdinalIgnoreCase)
        {
            ["WC"] = new FixtureSleeve { Name = "toilet", Pipe = 4, Vent = 2 },
            ["LAV"] = new FixtureSleeve { Name = "lavatory", Pipe = 1.5, Vent = 2 },
            // two 6" sleeves, 1" clear, for a tub running left-right on the floor plan; one 10" for one running up-down
            ["BT"] = new FixtureSleeve { Name = "bathtub", Pipe = 4, Count = 2, Spacing = 7, Vertical = new FixtureLayout { Count = 1, Sleeve = 10 } },
            ["SH"] = new FixtureSleeve { Name = "shower", Pipe = 4 },                                // 6" sleeve (office S&O sets)
            ["KS"] = new FixtureSleeve { Name = "kitchen sink", Pipe = 2, Vent = 2, Water = 1, StackReach = 60 },
            ["LS"] = new FixtureSleeve { Name = "laundry sink", Pipe = 2, Vent = 2, Water = 1, StackReach = 60 },
            ["W/D"] = new FixtureSleeve { Name = "washer", Pipe = 3 },                               // 5" sleeve (office S&O sets)
            ["WD"] = new FixtureSleeve { Name = "washer", Pipe = 3 },
            ["FD"] = new FixtureSleeve { Name = "floor drain", Pipe = 2 }
        };

        /// <summary>
        /// What happens to fixture sleeves: "model" = resolve the nearby matching Revit fixture and its sanitary connector
        /// (ambiguous or connector-less fixtures stay in review); "review" = list at the plan text; "off" = not listed.
        /// </summary>
        [JsonProperty("fixtureSleeves")] public string FixtureSleeves { get; set; } = "model";

        /// <summary>With fixtureSleeves "model": fixtures in the model (or its links) that no plan label names get their sleeves from the model too, flagged for a check.</summary>
        [JsonProperty("modelFixtures")] public bool ModelFixtures { get; set; } = true;

        /// <summary>
        /// A fixture label with no Revit fixture (the architecture only in DWG links): "place" = the same fixture's sleeve on
        /// the floor above/below at that spot, else the fixture drawn on the plan, flagged for a check; "report" = model/PDF gap, not placed.
        /// </summary>
        [JsonProperty("fixtureGaps")] public string FixtureGaps { get; set; } = "place";

        /// <summary>
        /// Read the fixtures from the architect's DWGs imported/linked on each level (the architecture is not a Revit model):
        /// a labelled fixture takes its drawing's drain spot, and toilets, tubs and showers drawn with no label are found by shape.
        /// </summary>
        [JsonProperty("dwgFixtures")] public bool DwgFixtures { get; set; } = true;
        /// <summary>Regex on the DWG layers holding the fixtures (toilets, tubs, sinks).</summary>
        [JsonProperty("dwgFixtureLayers")] public string DwgFixtureLayers { get; set; } = @"FIXT|PLUMB|PLMB|TOILET|SANIT|KITCHEN|BATH|LAV\b";
        /// <summary>Regex on the DWG layers holding the walls (a sink's sleeve goes in the wall behind it; a toilet's 1'-1" from it).</summary>
        [JsonProperty("dwgWallLayers")] public string DwgWallLayers { get; set; } = @"WALL";
        /// <summary>
        /// Regex on the DWG layers of an alteration drawing that hold the new work (fixtures, walls and everything else of the
        /// new layout on one layer, "TO ADD"): their curves and short lines are read as fixtures, their long straight lines as walls.
        /// </summary>
        [JsonProperty("dwgNewLayers")] public string DwgNewLayers { get; set; } = @"TO ADD|NEW WORK|NEW-WORK";
        /// <summary>Regex on the DWG layers holding the slab edge (a sink against an exterior wall drawn as a hatch: its sleeve halfway to the slab edge).</summary>
        [JsonProperty("dwgSlabEdgeLayers")] public string DwgSlabEdgeLayers { get; set; } = @"EDGE.?OF.?SLAB|SLAB.?EDGE";
        /// <summary>Regex on the DWG layers holding pipe shafts and chases (Extract Stacks: a stack goes in the chase next to its fixtures).</summary>
        [JsonProperty("dwgShaftLayers")] public string DwgShaftLayers { get; set; } = @"SHAFT|CHASE";
        /// <summary>
        /// Extract Stacks: the services of a stack found from the model's fixtures alone (no engineer's drawing), by their
        /// letters in <see cref="Services"/>: one sleeve each, in a touching row in the wet wall (office S&amp;O sets: vent 6,
        /// waste 6, hot 3, cold 3). A stack from a Revit pipe takes the pipe's own system instead.
        /// </summary>
        [JsonProperty("stackServices", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> StackServices { get; set; } = new List<string> { "S", "V", "HW", "CW" };
        /// <summary>
        /// Auto Run: a toilet no engineer's riser serves (no sanitary riser of the plans within <see cref="ToiletStackReach"/>)
        /// gets a stack by the Extract Stacks rule (wet wall or chase, <see cref="StackServices"/>), flagged for a check.
        /// The engineer's P# risers always come first.
        /// </summary>
        [JsonProperty("toiletStacks")] public bool ToiletStacks { get; set; } = true;
        /// <summary>Inches: an engineer's sanitary riser this close to a toilet serves it (no stack of the office rule there).</summary>
        [JsonProperty("toiletStackReach")] public double ToiletStackReach { get; set; } = 96;
        /// <summary>Vent Placer: where the vent (yellow) sleeves go in each bathroom's wet wall (Standards Manual, toilet sleeves page).</summary>
        [JsonProperty("vents")] public VentRules Vents { get; set; } = new VentRules();
        /// <summary>Regex on the DWG layers of what an alteration removes ("TO REMOVE"): a fixture block inserted on one is never sleeved.</summary>
        [JsonProperty("dwgRemoveLayers")] public string DwgRemoveLayers { get; set; } = @"TO REMOVE|REMOVE|DEMO";

        /// <summary>Sleeve = pipe + this (inches), rounded up to the next size in <see cref="SleeveSizes"/> (storm and standpipe rule).</summary>
        [JsonProperty("sleeveOverPipe")] public double SleeveOverPipe { get; set; } = 2;
        [JsonProperty("sleeveSizes", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<double> SleeveSizes { get; set; } = new List<double> { 3, 4, 5, 6, 8, 10, 12 };

        /// <summary>Clear space (inches) kept between the sleeves of one pipe group; the engineer draws the pipes 5" apart, closer than the sleeves.</summary>
        [JsonProperty("sleeveGap")] public double SleeveGap { get; set; } = 1;

        /// <summary>
        /// A riser's sleeve this close (inches) to where the same pipe's sleeve sits on the floors next to it is lined up with
        /// them (the plans draw one pipe a few inches apart floor to floor; manual 22-23: risers run straight). Farther = an
        /// offset the plans draw, kept. 0 = off.
        /// </summary>
        [JsonProperty("stackSnap")] public double StackSnap { get; set; } = 12;

        /// <summary>The lowest level's slab is on grade (the pipes run underground below it): no sleeves there.</summary>
        [JsonProperty("skipLowestSlab")] public bool SkipLowestSlab { get; set; } = true;

        /// <summary>Opening name: {service} = the letters on the plan (S, V, ST), {riser} = the group's tag (P3).</summary>
        [JsonProperty("namePattern")] public string NamePattern { get; set; } = "{service}-{riser}";

        /// <summary>Systems the plumbing side places (round sleeves sized from the pipe).</summary>
        public bool IsPipeSystem(string system) => system != null && Services.Values.Any(s => s.System == system);

        /// <summary>Sleeve diameter (inches) for a pipe: pipe + sleeveOverPipe, rounded up to the next listed size.</summary>
        public double SleeveFor(double pipe)
        {
            double need = pipe + SleeveOverPipe;
            var sizes = SleeveSizes.OrderBy(s => s).ToList();
            return sizes.Where(s => s >= need - 0.01).Select(s => (double?)s).FirstOrDefault() ?? need;
        }

        /// <summary>The service a circle's layer stands for ("P-SANITARY-N" -> S); null when none matches.</summary>
        public string ServiceForLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return null;
            foreach (var kv in Services.OrderByDescending(kv => kv.Value.Layer?.Length ?? 0))
                if (!string.IsNullOrEmpty(kv.Value.Layer) && Regex.IsMatch(layer, kv.Value.Layer, RegexOptions.IgnoreCase)) return kv.Key;
            return null;
        }

        /// <summary>
        /// rules.json "sprinkler": Auto Run for sprinkler / standpipe sleeves, run from the PL model (no FP model in the
        /// office's projects) or a model matching its own modelMatch. Same pipeline as plumbing, read by
        /// <see cref="SprinklerReader"/>: the risers are labelled in words with their size ('3" SPRINKLER RISER UP/DN').
        /// </summary>
        public static PlumbingRules SprinklerDefaults() => new PlumbingRules
        {
            ModelMatch = @"(\bFP(\b|_)|\bSP(\b|_)|SPRINK|FIRE\s*PROT)",
            Profile = new DwgProfile
            {
                RiserBlocks = null, TagBlocks = null, TagAttributes = new List<string>(), ConnectorLayers = @"^SPR$|LEADER", DuctLayers = null,
                RiserCircleLayers = null, FixtureText = null, LeaderTextDistance = 12, PointTolerance = 6,
                ReferenceFolders = @"^XREF\s*-?\s*(SPR|SP|FP|SPRINK\w*|FIRE\w*)$"
            },
            Services = new Dictionary<string, PipeService>(StringComparer.OrdinalIgnoreCase)
            {
                ["CHUTE"] = new PipeService { System = "Standpipe", Match = @"CHUTE\s+RISER|SPRINKLER\s+CHUTE", Sleeve = false, Pipe = 1,
                                              Note = "sprinkler riser inside the trash chute: it goes through the chute opening, no sleeve" },
                ["SP"] = new PipeService { System = "Standpipe", Match = @"(SPRINKLER|STANDPIPE|COMBINED|COMBINATION)\s+(&\s+STANDPIPE\s+)?RISER|\bSTANDPIPE\b", Sleeve = true, Pipe = 3 },
                ["FDC"] = new PipeService { System = "Standpipe", Match = @"\bFDC\b", Sleeve = true, Pipe = 4 }
            },
            Fixtures = new Dictionary<string, FixtureSleeve>(StringComparer.OrdinalIgnoreCase),
            FixtureSleeves = "off", Unmarked = "DN", SleeveSizes = new List<double> { 3, 4, 5, 6, 8, 10 }
        };

        /// <summary>The service a label written in words stands for ('3" SPRINKLER RISER UP/DN' -> SP); null when none matches.</summary>
        public string ServiceForLabel(string text, out Match match)
        {
            match = null;
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var kv in Services)
            {
                if (string.IsNullOrEmpty(kv.Value.Match)) continue;
                var m = Regex.Match(text, kv.Value.Match, RegexOptions.IgnoreCase);
                if (m.Success) { match = m; return kv.Key; }
            }
            return null;
        }

        public string Name(string service, string riser) =>
            (NamePattern ?? "{service}-{riser}").Replace("{service}", service ?? "?").Replace("{riser}", riser ?? "?");
    }

    public class PipeService
    {
        /// <summary>System written on the sleeve (Sanitary, Vent, Storm, Gas, ColdWater...): picks the MPI sleeve's size toggle.</summary>
        [JsonProperty("system")] public string System { get; set; }
        /// <summary>Regex on the DWG layer of this service's circles.</summary>
        [JsonProperty("layer")] public string Layer { get; set; }
        /// <summary>
        /// Regex on a label written in words instead of a service letter (sprinkler: 'SPRINKLER\s+RISER' for
        /// '3" SPRINKLER RISER UP/DN'). A size written in the label ('3"') is used instead of <see cref="Pipe"/>.
        /// </summary>
        [JsonProperty("match")] public string Match { get; set; }
        /// <summary>Why this service gets no sleeve, written in the report (sprinkler chute riser: inside the chute opening).</summary>
        [JsonProperty("note")] public string Note { get; set; }
        /// <summary>Gets a sleeve where it goes through a slab (the office sets sleeve sanitary, vent, storm, gas, hot and cold water; not the hot water return).</summary>
        [JsonProperty("sleeve")] public bool Sleeve { get; set; }
        /// <summary>Pipe size (inches) used when the drawings give none (plumbing plans carry no sizes; the riser diagram is not read yet).</summary>
        [JsonProperty("pipe")] public double Pipe { get; set; }
    }

    public class FixtureSleeve
    {
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>Drain pipe size (inches); the sleeve is pipe + 2" rounded up.</summary>
        [JsonProperty("pipe")] public double Pipe { get; set; }
        [JsonProperty("system")] public string System { get; set; } = "Sanitary";
        /// <summary>Sleeves per fixture (bathtub: two 6" sleeves 6" c-c, as the office sets draw them).</summary>
        [JsonProperty("count")] public int Count { get; set; } = 1;
        [JsonProperty("spacing")] public double Spacing { get; set; }
        /// <summary>
        /// Vent pipe (inches; 0 = none): a vent sleeve beside the drain sleeve, in the wall behind the fixture (office S&amp;O
        /// sets: a 4" yellow next to each lavatory, one in the wall behind each toilet). A sink with its own row (no stack
        /// within <see cref="StackReach"/>) gets it past its hot water sleeve.
        /// </summary>
        [JsonProperty("vent")] public double Vent { get; set; }
        /// <summary>
        /// Hot and cold water pipe (inches; 0 = none) of a sink with no stack within <see cref="StackReach"/> (an island,
        /// a sink on its own wall): its own row behind it, vent - hot - drain - cold (hot on the left facing it, IRC P2722.2).
        /// </summary>
        [JsonProperty("water")] public double Water { get; set; }
        /// <summary>
        /// Inches (0 = off): a sanitary or vent stack sleeve this close serves the fixture. It drains sideways in the wall to
        /// the stack, only the stacks go through the slab (24 Skillman S&amp;O: kitchen sinks have no sleeve of their own).
        /// </summary>
        [JsonProperty("stackReach")] public double StackReach { get; set; }
        /// <summary>
        /// Tubs: the sleeves when the tub runs up-down on the floor plan (office rule 2026-10-12: left-right = the two 6"
        /// above, up-down = one 10"); null = the same as left-right.
        /// </summary>
        [JsonProperty("vertical")] public FixtureLayout Vertical { get; set; }
    }

    /// <summary>
    /// rules.json "plumbing.vents": the Vent Placer's layout of each bathroom's wet-wall row, as the Standards Manual's
    /// toilet sleeves page draws it (regular: G6 | Y4 5 1/4" | Y4 7" | ... | G4 | 1'-2 3/4" | Y6). Pipe sizes are inches
    /// of pipe (the sleeve is pipe + sleeveOverPipe rounded up); distances are inches centre to centre along the wall.
    /// </summary>
    public class VentRules
    {
        /// <summary>The toilet's vent touches its waste stack in the wall: this far c-c, on the side toward the rest of the row.</summary>
        [JsonProperty("toiletVentFromWaste")] public double ToiletVentFromWaste { get; set; } = 5.25;
        /// <summary>A lavatory's vent: this far past the toilet's vent toward the lavatory...</summary>
        [JsonProperty("lavVentPastToiletVent")] public double LavVentPastToiletVent { get; set; } = 7;
        /// <summary>...else (no toilet in the row, or that spot is taken) this far from the lavatory's drain, on the toilet's side.</summary>
        [JsonProperty("lavVentFromDrain")] public double LavVentFromDrain { get; set; } = 10;
        /// <summary>Vent stack pipe (6" sleeve); 0 = no vent stack.</summary>
        [JsonProperty("stackPipe")] public double StackPipe { get; set; } = 4;
        /// <summary>The vent stack: this far past the lavatory's drain (the last drain of the row), away from the toilet (manual 10 1/4" to 1'-2 3/4").</summary>
        [JsonProperty("stackPastLastDrain")] public double StackPastLastDrain { get; set; } = 12;
        /// <summary>A vent stack on another floor this close is the same stack: this floor's lines up with it (risers run straight).</summary>
        [JsonProperty("stackSameAs")] public double StackSameAs { get; set; } = 18;
        /// <summary>Tub vent pipe (0 = none): in the wall of its row, at the drain end.</summary>
        [JsonProperty("tubPipe")] public double TubPipe { get; set; } = 2;
        /// <summary>Shower vent pipe (0 = none; the manual shows none).</summary>
        [JsonProperty("showerPipe")] public double ShowerPipe { get; set; }
        /// <summary>Washer vent pipe (0 = none) and how far along the wall from its drain (Wallabout video: 6 + 6 about 2'-0" apart).</summary>
        [JsonProperty("washerPipe")] public double WasherPipe { get; set; } = 4;
        [JsonProperty("washerAlong")] public double WasherAlong { get; set; } = 24;
        /// <summary>A tub this far (inches) from its row's wall at most gets its vent there; farther = add it by hand.</summary>
        [JsonProperty("tubReach")] public double TubReach { get; set; } = 48;
        /// <summary>Wall-hung toilets: the reserve waste and vent sleeves the manual draws (flagged: always verify the toilet type).</summary>
        [JsonProperty("wallHungReserve")] public bool WallHungReserve { get; set; } = true;
        /// <summary>Wall-hung: the reserve waste this far off the toilet's centre line (outer side), the reserve vent this far on the other side, the vent 7" past it.</summary>
        [JsonProperty("reserveWasteOffset")] public double ReserveWasteOffset { get; set; } = 13;
        [JsonProperty("reserveVentOffset")] public double ReserveVentOffset { get; set; } = 16;
        /// <summary>A vent sleeve already in the model this close (inches) to a spot is that vent: nothing more there.</summary>
        [JsonProperty("existingWithin")] public double ExistingWithin { get; set; } = 4;
        /// <summary>How far (inches) a vent may slide along the wall to clear the sleeves already there.</summary>
        [JsonProperty("slide")] public double Slide { get; set; } = 12;
    }

    /// <summary>Another layout of a fixture's sleeves: how many, how far apart (c-c inches) and their size (inches).</summary>
    public class FixtureLayout
    {
        [JsonProperty("count")] public int Count { get; set; } = 1;
        [JsonProperty("spacing")] public double Spacing { get; set; }
        [JsonProperty("sleeve")] public double Sleeve { get; set; }
    }
}
