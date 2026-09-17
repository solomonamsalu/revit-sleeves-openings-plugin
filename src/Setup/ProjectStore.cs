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
        public Dictionary<string, bool> Preflight { get; set; } = new Dictionary<string, bool>();
        public string BathtubOption { get; set; }            // "two6" | "one10"
        public bool? CondensateRequired { get; set; }
        public DateTime? LastSetup { get; set; }
        /// <summary>Per-project family/parameter mapping chosen in "Map Families" (overrides rules.json families).</summary>
        public Dictionary<string, Placement.FamilyMapEntry> FamilyMap { get; set; } = new Dictionary<string, Placement.FamilyMapEntry>();
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
