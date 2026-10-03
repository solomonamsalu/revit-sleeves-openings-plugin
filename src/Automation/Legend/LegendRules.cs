using System.Collections.Generic;
using Newtonsoft.Json;

namespace SleevesOpenings.Automation.Legend
{
    /// <summary>
    /// rules.json "legend": how a tag's definition (read from the engineer's PDF) decides whether it needs an opening.
    /// Categories are tried in order against the definition text; the first match wins.
    /// </summary>
    public class LegendRules
    {
        private const string GasCheck = "gas vent: confirm the gas scope with the engineer before cutting (the plumbing set may say the water heaters are electric)";

        /// <summary>Office-wide tags that engineer PDFs do not define. Only KX and TX are fixed across the office.</summary>
        [JsonProperty("officeFixed", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, string> OfficeFixed { get; set; } = new Dictionary<string, string>
        {
            ["KX"] = "KITCHEN EXHAUST",
            ["TX"] = "TOILET EXHAUST"
        };

        [JsonProperty("categories", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<LegendCategory> Categories { get; set; } = new List<LegendCategory>
        {
            new LegendCategory { Match = @"GAS METER VENT", System = "Exhaust", Label = "GMV", Diameter = 8, Check = GasCheck },
            new LegendCategory { Match = @"COMBUSTION AIR|FUEL VENT|GAS FLUE", Shaft = "COMB AIR/B-VENT", Check = GasCheck },
            new LegendCategory { Match = @"DIFFUSER|GRILLE|REGISTER|LOUVER", Ignore = true },
            new LegendCategory { Match = @"\b(EXHAUST|SUPPLY|RETURN|OUTSIDE|RELIEF|TRANSFER) AIR\b", Ignore = true },
            new LegendCategory { Match = @"PARKING|GARAGE", System = "Exhaust" },
            new LegendCategory { Match = @"MOTORI[SZ]ED DAMPER|MOTOR OPERATED DAMPER", Ignore = true, LabelOnHost = true },   // on the duct opening (ERV-MD); set system "MotorizedDamper" for an opening of its own
            new LegendCategory { Match = @"ENERGY RECOVERY|\bERV\b", System = "ERV" },
            new LegendCategory { Match = @"DRYER", System = "DryerExhaust" },
            new LegendCategory { Match = @"CHUTE", System = "GarbageChute" },
            new LegendCategory { Match = @"FIRE SMOKE DAMPER|GRAVITY|BACKDRAFT", Ignore = true, LabelOnHost = true },
            new LegendCategory { Match = @"\bFANS?\b|DETECTOR|ACCESS DOOR|HEATER|\bUNIT\b|PUMP|BOILER|DAMPER|COIL|THERMOSTAT", Ignore = true },
            new LegendCategory { Match = @"EXHAUST", System = "Exhaust" }
        };
    }

    public class LegendCategory
    {
        /// <summary>Regex (case-insensitive) on the definition text.</summary>
        [JsonProperty("match")] public string Match { get; set; }
        /// <summary>System that gets an opening (Exhaust, ERV, MotorizedDamper, DryerExhaust, GarbageChute…).</summary>
        [JsonProperty("system")] public string System { get; set; }
        /// <summary>Not an opening (fans, diffusers, grilles, detectors…).</summary>
        [JsonProperty("ignore")] public bool Ignore { get; set; }
        /// <summary>No opening of its own, but its tag is added to the label of the duct opening it sits on (ERV-FSD).</summary>
        [JsonProperty("labelOnHost")] public bool LabelOnHost { get; set; }
        /// <summary>Not decided yet: reported with this text, not placed (plan section 14, left tasks).</summary>
        [JsonProperty("review")] public string Review { get; set; }
        /// <summary>
        /// Notes next to untagged risers: the ducts drawn there go up in a shaft and get ONE opening around them all, named
        /// this (combustion air / Type B vents), on every slab the shaft passes.
        /// </summary>
        [JsonProperty("shaft")] public string Shaft { get; set; }
        /// <summary>Notes next to an untagged riser with a <see cref="System"/>: the opening's name (GMV for "8Ø GAS METER VENT").</summary>
        [JsonProperty("label")] public string Label { get; set; }
        /// <summary>Duct size (inches round) when the note gives none.</summary>
        [JsonProperty("diameter")] public double Diameter { get; set; } = 6;
        /// <summary>Placed, but flagged with this note for the drafter (e.g. a scope question for the engineer).</summary>
        [JsonProperty("check")] public string Check { get; set; }
    }
}
