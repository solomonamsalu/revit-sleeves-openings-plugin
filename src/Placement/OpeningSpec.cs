using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;

namespace SleevesOpenings.Placement
{
    public enum SystemKind
    {
        Exhaust, GarbageChute, DryerExhaust, MotorizedDamper, Refrigeration, Electrical,
        Storm, AreaDrain, Condensate, Standpipe, Bathtub, Toilet
    }

    /// <summary>What to place: role + size (inches) + label. Built by the commands from rules.json.</summary>
    public class OpeningSpec
    {
        public SystemKind System { get; set; }
        public string Role { get; set; }                 // FamilyRole.*
        public double? Width { get; set; }               // inches (rectangular)
        public double? Length { get; set; }              // inches (rectangular)
        public double? Diameter { get; set; }            // inches (round)
        public double? DownHeight { get; set; }          // inches (pipe reference opening)
        public string Label { get; set; }                // written to the name parameter
        public string Riser { get; set; }                // riser id for propagation/audit (F4/F8)
        public double RotationRadians { get; set; }

        public bool IsRound => Diameter.HasValue;

        public string SizeText => IsRound
            ? Units.FormatInches(Diameter.Value)
            : $"{Units.FormatInches(Width ?? 0)} x {Units.FormatInches(Length ?? 0)}";

        public static OpeningSpec Rect(SystemKind sys, string role, double w, double l, string label = null) =>
            new OpeningSpec { System = sys, Role = role, Width = w, Length = l, Label = label };

        public static OpeningSpec Round(SystemKind sys, double dia, string label = null) =>
            new OpeningSpec { System = sys, Role = FamilyRole.RoundSleeve, Diameter = dia, Label = label };
    }

    /// <summary>
    /// Data stamped on every element the add-in places (extensible storage) so later features
    /// (propagation, roof generator, auditor) know what each opening is.
    /// </summary>
    public class OpeningData
    {
        public string System { get; set; }
        public string Riser { get; set; }
        public string Label { get; set; }
        public double? Width { get; set; }
        public double? Length { get; set; }
        public double? Diameter { get; set; }
        public string Level { get; set; }
        public string PlacedBy { get; set; }
        public DateTime Placed { get; set; }

        private static readonly Guid SchemaGuid = new Guid("3D5E7F91-2B4C-4A6D-8E9F-0A1B2C3D4E5F");
        private const string Field = "Json";

        private static Schema GetSchema()
        {
            var s = Schema.Lookup(SchemaGuid);
            if (s != null) return s;
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName("SleevesOpeningsOpening");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(Field, typeof(string));
            return b.Finish();
        }

        public static OpeningData From(OpeningSpec spec, Level level) => new OpeningData
        {
            System = spec.System.ToString(), Riser = spec.Riser, Label = spec.Label,
            Width = spec.Width, Length = spec.Length, Diameter = spec.Diameter,
            Level = level?.Name, PlacedBy = Environment.UserName, Placed = DateTime.Now
        };

        /// <summary>Inside a transaction.</summary>
        public void WriteTo(Element e)
        {
            var ent = new Entity(GetSchema());
            ent.Set(Field, JsonConvert.SerializeObject(this));
            e.SetEntity(ent);
        }

        public static OpeningData Read(Element e)
        {
            var s = Schema.Lookup(SchemaGuid);
            if (s == null) return null;
            var ent = e.GetEntity(s);
            if (!ent.IsValid()) return null;
            var json = ent.Get<string>(Field);
            return string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<OpeningData>(json);
        }
    }
}
