using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SleevesOpenings.Rules
{
    /// <summary>
    /// Loads rules.json as layers (see <see cref="RuleLayers"/>), each on top of the one before:
    ///   1. Rules\rules.json next to the add-in DLL (shipped default)
    ///   2. %APPDATA%\SleevesOpenings\rules.json (office rules)
    ///   3. &lt;model&gt;.sleeves-rules.json next to the open RVT (this project's rules)
    /// </summary>
    public static class RuleLoader
    {
        public static string AddinDir =>
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public static string UserRulesPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "SleevesOpenings", "rules.json");

        public static string DefaultRulesPath => Path.Combine(AddinDir, "Rules", "rules.json");

        public static string ProjectRulesPath(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return null;
            var dir = Path.GetDirectoryName(modelPath) ?? "";
            var name = Path.GetFileNameWithoutExtension(modelPath);
            return Path.Combine(dir, name + ".sleeves-rules.json");
        }

        public static RuleSet Load(string modelPath = null)
        {
            var json = RuleLayers.Effective(modelPath, RuleLayers.Layer.Project, out var sources);
            var rules = Parse(json);
            rules.Sources = sources;
            rules.SourcePath = sources.Last();
            return rules;
        }

        /// <summary>The rule set a merged rules JSON describes (throws when a value has the wrong type).</summary>
        public static RuleSet Parse(JObject json) =>
            JsonConvert.DeserializeObject<RuleSet>(json.ToString(Formatting.None))
            ?? throw new InvalidDataException("The rules are empty.");

        /// <summary>Resolves a family file path from rules.json (relative paths are relative to the add-in folder).</summary>
        public static string ResolveFamilyFile(FamilyRule fam)
        {
            if (fam == null || string.IsNullOrEmpty(fam.File)) return null;
            return Path.IsPathRooted(fam.File) ? fam.File : Path.Combine(AddinDir, fam.File);
        }
    }
}
