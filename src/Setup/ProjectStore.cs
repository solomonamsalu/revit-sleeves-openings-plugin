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
        /// <summary>"PTAC", "Split" or "VRF" once the refrigeration planner has been run.</summary>
        public string AcSystem { get; set; }
    }

    public class RiserEnds
    {
        public string Top { get; set; }       // level name where the riser legitimately ends going up (null = roof expected)
        public string Bottom { get; set; }    // level name where it legitimately ends going down (null = lowest expected)
        public string Note { get; set; }      // e.g. "KX-3 tap-out", "to bulkhead"
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
