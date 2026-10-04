using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SleevesOpenings.Rules
{
    /// <summary>
    /// rules.json as layers: the shipped default, then the office file (%APPDATA%), then the project file
    /// (&lt;model&gt;.sleeves-rules.json). Each upper layer holds only what differs from the layers below it, so a
    /// change to a lower layer (a new add-in version, an office decision) still reaches every project. A full copy
    /// (as the old Edit Rules made) is a valid layer too.
    /// </summary>
    public static class RuleLayers
    {
        public enum Layer { Default, Office, Project }

        /// <summary>
        /// Tables whose rows the user adds and removes: an upper layer replaces the whole table, so a row deleted
        /// there stays deleted. Lists ([...]) are always replaced whole.
        /// </summary>
        public static readonly HashSet<string> WholeTables = new HashSet<string>(StringComparer.Ordinal)
        {
            "plumbing.services", "plumbing.fixtures", "sprinkler.services", "legend.officeFixed",
            "families.roundSleeve.sizeToggles", "adopt.togglePrefixToSystem", "systems.bathtub.options"
        };

        public static string PathOf(Layer layer, string modelPath)
        {
            switch (layer)
            {
                case Layer.Default: return RuleLoader.DefaultRulesPath;
                case Layer.Office: return RuleLoader.UserRulesPath;
                default: return RuleLoader.ProjectRulesPath(modelPath);
            }
        }

        public static JObject Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return JObject.Parse(File.ReadAllText(path));
        }

        /// <summary>The rules in force at <paramref name="upTo"/>: the default with every layer up to it on top.</summary>
        public static JObject Effective(string modelPath, Layer upTo, out List<string> sources)
        {
            sources = new List<string>();
            JObject result = null;
            foreach (Layer layer in Enum.GetValues(typeof(Layer)))
            {
                if (layer > upTo) break;
                var path = PathOf(layer, modelPath);
                var json = Read(path);
                if (json == null) continue;
                sources.Add(path);
                if (result == null) result = json;
                else Merge(result, json, "");
            }
            if (result == null) throw new FileNotFoundException("No rules.json found. Expected default at " + RuleLoader.DefaultRulesPath);
            return result;
        }

        public static JObject Effective(string modelPath, Layer upTo) => Effective(modelPath, upTo, out _);

        /// <summary>Puts <paramref name="upper"/> on top of <paramref name="target"/> (in place). Comments (_keys) stay the lower layer's.</summary>
        public static void Merge(JObject target, JObject upper, string path)
        {
            foreach (var prop in upper.Properties())
            {
                if (prop.Name.StartsWith("_") && target[prop.Name] != null) continue;
                var p = Join(path, prop.Name);
                if (prop.Value is JObject up && target[prop.Name] is JObject low && !WholeTables.Contains(p))
                    Merge(low, up, p);
                else
                    target[prop.Name] = prop.Value.DeepClone();
            }
        }

        /// <summary>What <paramref name="edited"/> changes over <paramref name="lower"/>: the content of an upper layer.</summary>
        public static JObject Diff(JObject edited, JObject lower, string path = "")
        {
            var diff = new JObject();
            foreach (var prop in edited.Properties())
            {
                if (prop.Name.StartsWith("_")) continue;
                var p = Join(path, prop.Name);
                var low = lower?[prop.Name];
                if (prop.Value is JObject e && low is JObject l && !WholeTables.Contains(p))
                {
                    var sub = Diff(e, l, p);
                    if (sub.HasValues) diff[prop.Name] = sub;
                }
                else if (!Same(prop.Value, low))
                    diff[prop.Name] = prop.Value.DeepClone();
            }
            return diff;
        }

        /// <summary>Equal values; 2 and 2.0 are the same number, a missing value and null are the same.</summary>
        public static bool Same(JToken a, JToken b)
        {
            bool nullA = a == null || a.Type == JTokenType.Null, nullB = b == null || b.Type == JTokenType.Null;
            if (nullA || nullB) return nullA && nullB;
            if (IsNumber(a) && IsNumber(b)) return Math.Abs(a.Value<double>() - b.Value<double>()) < 1e-9;
            if (a is JObject oa && b is JObject ob)
            {
                var keys = oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name));
                return keys.All(k => Same(oa[k], ob[k]));
            }
            if (a is JArray xa && b is JArray xb)
                return xa.Count == xb.Count && xa.Zip(xb, Same).All(s => s);
            return JToken.DeepEquals(a, b);
        }

        private static bool IsNumber(JToken t) => t.Type == JTokenType.Integer || t.Type == JTokenType.Float;

        /// <summary>
        /// Writes one layer (only what differs from the layers below). The file it replaces is kept as .bak.
        /// An empty difference deletes the layer's file: that layer then simply follows the one below.
        /// </summary>
        public static string Save(JObject edited, string modelPath, Layer layer)
        {
            if (layer == Layer.Default) throw new InvalidOperationException("The shipped default is not edited; save to the office or the project.");
            var path = PathOf(layer, modelPath);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Save the model first: project rules are kept next to the .rvt file.");

            var lower = Effective(modelPath, layer - 1);
            var diff = Diff(edited, lower);

            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            if (!diff.HasValues)
            {
                if (File.Exists(path)) File.Delete(path);
                return path;
            }

            var file = new JObject
            {
                ["version"] = edited["version"]?.DeepClone() ?? 1,
                ["_comment"] = (layer == Layer.Office ? "Office rules" : "This project's rules") +
                               ": only what differs from " + (layer == Layer.Office ? "the add-in's default" : "the office rules") +
                               ". Edit with Sleeves & Openings > Edit Rules. Lengths in inches."
            };
            foreach (var prop in diff.Properties())
                if (prop.Name != "version") file[prop.Name] = prop.Value;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, file.ToString(Formatting.Indented));
            return path;
        }

        public static JToken Get(JObject root, string path)
        {
            JToken t = root;
            foreach (var part in path.Split('.'))
            {
                t = (t as JObject)?[part];
                if (t == null) return null;
            }
            return t;
        }

        public static void Set(JObject root, string path, JToken value)
        {
            var parts = path.Split('.');
            var obj = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!(obj[parts[i]] is JObject next)) { next = new JObject(); obj[parts[i]] = next; }
                obj = next;
            }
            obj[parts.Last()] = value ?? JValue.CreateNull();
        }

        public static string Join(string path, string name) => string.IsNullOrEmpty(path) ? name : path + "." + name;
    }
}
