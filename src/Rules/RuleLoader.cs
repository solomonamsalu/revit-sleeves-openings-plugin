using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace SleevesOpenings.Rules
{
    /// <summary>
    /// Loads rules.json. Lookup order (first found wins):
    ///   1. &lt;model&gt;.sleeves-rules.json next to the open RVT (project override)
    ///   2. %APPDATA%\SleevesOpenings\rules.json (user/office override)
    ///   3. Rules\rules.json next to the add-in DLL (shipped default)
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
            foreach (var candidate in new[] { ProjectRulesPath(modelPath), UserRulesPath, DefaultRulesPath })
            {
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
                var rules = JsonConvert.DeserializeObject<RuleSet>(File.ReadAllText(candidate));
                if (rules == null) continue;
                rules.SourcePath = candidate;
                return rules;
            }
            throw new FileNotFoundException("No rules.json found. Expected default at " + DefaultRulesPath);
        }

        /// <summary>Resolves a family file path from rules.json (relative paths are relative to the add-in folder).</summary>
        public static string ResolveFamilyFile(FamilyRule fam)
        {
            if (fam == null || string.IsNullOrEmpty(fam.File)) return null;
            return Path.IsPathRooted(fam.File) ? fam.File : Path.Combine(AddinDir, fam.File);
        }
    }
}
