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
            ["CW"] = new PipeService { System = "ColdWater", Layer = "COLD WATER", Sleeve = false, Pipe = 2 },
            ["HW"] = new PipeService { System = "HotWater", Layer = "HOT WATER(?! RETURN)", Sleeve = false, Pipe = 1.5 },
            ["HWR"] = new PipeService { System = "HotWaterReturn", Layer = "HOT WATER RETURN", Sleeve = false, Pipe = 1 }
        };

        /// <summary>Words after a service letter: which slab the pipe goes through (up = the slab above, down / through = this floor's slab).</summary>
        [JsonProperty("upWords", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> UpWords { get; set; } = new List<string> { "UP", "RISE", "RISER" };
        [JsonProperty("downWords", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> DownWords { get; set; } = new List<string> { "DN", "DOWN", "DROP" };
        [JsonProperty("throughWords", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> ThroughWords { get; set; } = new List<string> { "THRU", "THROUGH" };

        /// <summary>Fixtures named on the plans: the drain sleeve under each (pipe size, how many sleeves, their spacing).</summary>
        [JsonProperty("fixtures", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, FixtureSleeve> Fixtures { get; set; } = new Dictionary<string, FixtureSleeve>(StringComparer.OrdinalIgnoreCase)
        {
            ["WC"] = new FixtureSleeve { Name = "toilet", Pipe = 4 },
            ["LAV"] = new FixtureSleeve { Name = "lavatory", Pipe = 1.5 },
            ["BT"] = new FixtureSleeve { Name = "bathtub", Pipe = 4, Count = 2, Spacing = 6 },
            ["SH"] = new FixtureSleeve { Name = "shower", Pipe = 2 },
            ["KS"] = new FixtureSleeve { Name = "kitchen sink", Pipe = 2 },
            ["LS"] = new FixtureSleeve { Name = "laundry sink", Pipe = 2 },
            ["W/D"] = new FixtureSleeve { Name = "washer", Pipe = 2 },
            ["WD"] = new FixtureSleeve { Name = "washer", Pipe = 2 },
            ["FD"] = new FixtureSleeve { Name = "floor drain", Pipe = 2 }
        };

        /// <summary>
        /// What happens to fixture sleeves: "model" = resolve the nearby matching Revit fixture and its sanitary connector
        /// (ambiguous or connector-less fixtures stay in review); "review" = list at the plan text; "off" = not listed.
        /// </summary>
        [JsonProperty("fixtureSleeves")] public string FixtureSleeves { get; set; } = "model";

        /// <summary>Sleeve = pipe + this (inches), rounded up to the next size in <see cref="SleeveSizes"/> (storm and standpipe rule).</summary>
        [JsonProperty("sleeveOverPipe")] public double SleeveOverPipe { get; set; } = 2;
        [JsonProperty("sleeveSizes", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<double> SleeveSizes { get; set; } = new List<double> { 3, 4, 5, 6, 8, 10, 12 };

        /// <summary>Clear space (inches) kept between the sleeves of one pipe group; the engineer draws the pipes 5" apart, closer than the sleeves.</summary>
        [JsonProperty("sleeveGap")] public double SleeveGap { get; set; } = 1;

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

        public string Name(string service, string riser) =>
            (NamePattern ?? "{service}-{riser}").Replace("{service}", service ?? "?").Replace("{riser}", riser ?? "?");
    }

    public class PipeService
    {
        /// <summary>System written on the sleeve (Sanitary, Vent, Storm, Gas, ColdWater...): picks the MPI sleeve's size toggle.</summary>
        [JsonProperty("system")] public string System { get; set; }
        /// <summary>Regex on the DWG layer of this service's circles.</summary>
        [JsonProperty("layer")] public string Layer { get; set; }
        /// <summary>Gets a sleeve where it goes through a slab (the office sets sleeve sanitary, vent, storm and gas; not water).</summary>
        [JsonProperty("sleeve")] public bool Sleeve { get; set; }
        /// <summary>Pipe size (inches) used when the drawings give none (plans carry no sizes; the riser diagram is not read yet).</summary>
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
    }
}
