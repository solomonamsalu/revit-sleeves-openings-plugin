using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Placement
{
    /// <summary>
    /// Schedulable/taggable copies of the opening stamp: "SO System", "SO Riser", "SO Size".
    /// Family parameters cannot be scheduled, so these are shared parameters bound as instance
    /// parameters to the categories of the mapped families. The definition file is generated with
    /// fixed GUIDs so every machine produces the same parameters.
    /// </summary>
    public static class SharedParams
    {
        public const string System = "SO System";
        public const string Riser = "SO Riser";
        public const string Size = "SO Size";
        private const string GroupName = "SleevesOpenings";

        private static readonly (string name, Guid guid, string desc)[] Defs =
        {
            (System, new Guid("A1F0C2D3-4E5B-4C6D-8E7F-90A1B2C3D4E5"), "Opening system (Exhaust, Storm, ...)"),
            (Riser,  new Guid("B2E1D3C4-5F6A-4D7E-9F80-A1B2C3D4E5F6"), "Riser id shared by all floors of one riser"),
            (Size,   new Guid("C3D2E4B5-6A7B-4E8F-A091-B2C3D4E5F607"), "Opening size as placed"),
        };

        public static string DefinitionFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SleevesOpenings", "SleevesOpenings.SharedParameters.txt");

        /// <summary>Categories of every family currently mapped (plus Generic Models as a safe default).</summary>
        public static List<Category> MappedCategories(Document doc, RuleSet rules, ProjectState state)
        {
            var cats = new Dictionary<long, Category>();
            void Add(Category c) { if (c != null) cats[c.Id.Value] = c; }
            Add(Category.GetCategory(doc, BuiltInCategory.OST_GenericModel));
            foreach (var role in FamilyRole.All)
                Add(FamilyMapping.FindSymbol(doc, FamilyMapping.Get(rules, state, role))?.Category);
            return cats.Values.ToList();
        }

        /// <summary>Ensures the three parameters exist and are bound to the categories. Inside a transaction.</summary>
        public static void EnsureBound(Document doc, IEnumerable<Category> categories)
        {
            var app = doc.Application;
            var defs = OpenDefinitions(app);
            var map = doc.ParameterBindings;

            foreach (var def in defs)
            {
                var existing = FindBinding(map, def.Name, out Definition boundDef);
                var set = app.Create.NewCategorySet();
                bool changed = false;
                if (existing != null)
                    foreach (Category c in existing.Categories) set.Insert(c);
                foreach (var c in categories)
                    if (!set.Contains(c)) { set.Insert(c); changed = true; }

                if (existing == null)
                    map.Insert(def, app.Create.NewInstanceBinding(set), GroupTypeId.IdentityData);
                else if (changed)
                    map.ReInsert(boundDef, app.Create.NewInstanceBinding(set), GroupTypeId.IdentityData);
            }
        }

        /// <summary>Writes the stamp values to the element's shared parameters (if bound). Inside a transaction.</summary>
        public static void Write(Element e, OpeningData data)
        {
            Set(e, System, data.System);
            Set(e, Riser, data.Riser);
            Set(e, Size, data.Diameter.HasValue
                ? Units.FormatInches(data.Diameter.Value)
                : $"{Units.FormatInches(data.Width ?? 0)} x {Units.FormatInches(data.Length ?? 0)}");
        }

        private static void Set(Element e, string name, string value)
        {
            var p = e.LookupParameter(name);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value ?? "");
        }

        private static InstanceBinding FindBinding(BindingMap map, string name, out Definition def)
        {
            var it = map.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                if (it.Key.Name == name && it.Current is InstanceBinding ib) { def = it.Key; return ib; }
            }
            def = null;
            return null;
        }

        private static List<ExternalDefinition> OpenDefinitions(Application app)
        {
            var path = DefinitionFile;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>
                {
                    "# This is a Revit shared parameter file.",
                    "# Do not edit manually.",
                    "*META\tVERSION\tMINVERSION",
                    "META\t2\t1",
                    "*GROUP\tID\tNAME",
                    $"GROUP\t1\t{GroupName}",
                    "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE",
                };
                foreach (var d in Defs)
                    lines.Add($"PARAM\t{d.guid}\t{d.name}\tTEXT\t\t1\t1\t{d.desc}\t1\t0");
                File.WriteAllLines(path, lines);
            }

            string previous = app.SharedParametersFilename;
            app.SharedParametersFilename = path;
            var file = app.OpenSharedParameterFile();
            if (file == null) throw new InvalidOperationException("Could not open shared parameter file " + path);
            var group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);

            var result = new List<ExternalDefinition>();
            foreach (var d in Defs)
            {
                var def = group.Definitions.get_Item(d.name) as ExternalDefinition;
                if (def == null)
                {
                    var opts = new ExternalDefinitionCreationOptions(d.name, SpecTypeId.String.Text) { GUID = d.guid, Description = d.desc, UserModifiable = true };
                    def = group.Definitions.Create(opts) as ExternalDefinition;
                }
                result.Add(def);
            }
            if (!string.IsNullOrEmpty(previous) && File.Exists(previous)) app.SharedParametersFilename = previous;
            return result;
        }
    }
}
