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
            new LegendCategory { Match = @"DIFFUSER|GRILLE|REGISTER|LOUVER", Ignore = true },
            new LegendCategory { Match = @"\b(EXHAUST|SUPPLY|RETURN|OUTSIDE|RELIEF|TRANSFER) AIR\b", Ignore = true },
            new LegendCategory { Match = @"PARKING|GARAGE", System = "Exhaust" },
            new LegendCategory { Match = @"MOTORI[SZ]ED DAMPER|MOTOR OPERATED DAMPER", System = "MotorizedDamper" },
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
    }
}
