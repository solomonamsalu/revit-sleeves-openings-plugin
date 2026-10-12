using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;

namespace SleevesOpenings.Setup
{
    /// <summary>
    /// Per-project state saved inside the RVT via extensible storage:
    /// level classification and preflight answers. One JSON blob on ProjectInformation.
    /// </summary>
    public class ProjectState
    {
        public Dictionary<string, string> LevelRoles { get; set; } = new Dictionary<string, string>();   // level name -> role
        /// <summary>Last confirmation of the manual's general rules (who, when). Null = never confirmed.</summary>
        public WorkConfirmation Confirmation { get; set; }
        public string BathtubOption { get; set; }            // "two6" | "one10"
        public bool? CondensateRequired { get; set; }
        public DateTime? LastSetup { get; set; }
        /// <summary>Per-project family/parameter mapping chosen in "Map Families" (overrides rules.json families).</summary>
        public Dictionary<string, Placement.FamilyMapEntry> FamilyMap { get; set; } = new Dictionary<string, Placement.FamilyMapEntry>();
        /// <summary>Where each riser is meant to end (tap-outs, bulkheads, setbacks) so the auditor does not flag them.</summary>
        public Dictionary<string, RiserEnds> RiserEnds { get; set; } = new Dictionary<string, RiserEnds>();
        /// <summary>Riser offsets the drawings show (Auto Run): Final Check notes them instead of warning while both sleeves stay put.</summary>
        public List<DrawnOffset> DrawnOffsets { get; set; } = new List<DrawnOffset>();
        /// <summary>"PTAC", "Split" or "VRF" once the refrigeration planner has been run.</summary>
        public string AcSystem { get; set; }
        /// <summary>Auto Run: drawing files, floor-to-level overrides, existing-openings choice.</summary>
        public SleevesOpenings.Automation.AutomationInputs Automation { get; set; } = new SleevesOpenings.Automation.AutomationInputs();
        /// <summary>S&amp;O set: the notes the drafter wrote for the sheets' notes table (written as revisions on every run).</summary>
        public List<SleevesOpenings.Sheets.SoNote> SoNotes { get; set; } = new List<SleevesOpenings.Sheets.SoNote>();
        /// <summary>S&amp;O set: the PDF folder the user chose (null = the project's Structural folder).</summary>
        public string SoPdfFolder { get; set; }
        /// <summary>Electrical riser: the architect's xref folder the user picked (null = the one found next to the model).</summary>
        public string ElectricalXrefFolder { get; set; }
        /// <summary>S&amp;O set: this project's naming and PDF settings (null fields = rules.json).</summary>
        public SleevesOpenings.Sheets.SoProjectSettings SoSettings { get; set; } = new SleevesOpenings.Sheets.SoProjectSettings();
    }

    public class RiserEnds
    {
        public string Top { get; set; }       // level name where the riser legitimately ends going up (null = roof expected)
        public string Bottom { get; set; }    // level name where it legitimately ends going down (null = lowest expected)
        public string Note { get; set; }      // e.g. "KX-3 tap-out", "to bulkhead"
    }

    /// <summary>One step of a riser that the plans draw: the sleeve on <see cref="Level"/> and the one on the floor below (Revit feet).</summary>
    public class DrawnOffset
    {
        public string Riser { get; set; }
        public string Level { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double BelowX { get; set; }
        public double BelowY { get; set; }

        /// <summary>The two sleeves are still where Auto Run put them (within 1").</summary>
        public bool Matches(XYZ below, XYZ here)
        {
            double tol = 1.0 / 12;
            return Math.Abs(here.X - X) <= tol && Math.Abs(here.Y - Y) <= tol && Math.Abs(below.X - BelowX) <= tol && Math.Abs(below.Y - BelowY) <= tol;
        }
    }

    public class WorkConfirmation
    {
        public string User { get; set; }
        public DateTime Date { get; set; }
        public string FileName { get; set; }
    }

    public static class ProjectStore
    {
        private static readonly Guid SchemaGuid = new Guid("B2F1C6D4-7A3E-4E8B-9C5D-1F2A3B4C5D6E");
        private const string FieldName = "StateJson";

        private static Schema GetSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName("SleevesOpeningsState");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(FieldName, typeof(string));
            return b.Finish();
        }

        public static ProjectState Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new ProjectState();
            var ent = doc.ProjectInformation.GetEntity(schema);
            if (!ent.IsValid()) return new ProjectState();
            var json = ent.Get<string>(FieldName);
            return string.IsNullOrEmpty(json)
                ? new ProjectState()
                : JsonConvert.DeserializeObject<ProjectState>(json) ?? new ProjectState();
        }

        /// <summary>Must be called inside an open Transaction.</summary>
        public static void Save(Document doc, ProjectState state)
        {
            var schema = GetSchema();
            var ent = new Entity(schema);
            ent.Set(FieldName, JsonConvert.SerializeObject(state));
            doc.ProjectInformation.SetEntity(ent);
        }

        public static Level FindLevel(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => l.Name == name);
    }
}
