using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>Per-floor drawings at Revit's 0,0 on disk: the office's xref folder next to the model ("Xref/Xref ME").</summary>
    public static class ReferenceFiles
    {
        /// <summary>Floor named by a per-floor file ("05.5-TH  FLOOR ME.dwg" -> F5, "08.ROF FLOOR ME.dwg" -> ROOF). Null when none or several.</summary>
        public static string FloorOf(string fileName)
        {
            var keys = FloorKey.Find(Path.GetFileNameWithoutExtension(fileName ?? "").Replace('.', ' ').Replace('-', ' ').Replace('_', ' '));
            return keys.Count == 1 ? keys[0] : null;
        }

        /// <summary>A folder whose name matches <paramref name="pattern"/> under the model's folder or its parent (3 levels deep); null if none.</summary>
        public static string FindFolder(string modelPath, string pattern)
        {
            if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(pattern)) return null;
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);
            var dir = Path.GetDirectoryName(modelPath);
            foreach (var root in new[] { dir, Path.GetDirectoryName(dir) }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
            {
                var hit = Folders(root, 3).FirstOrDefault(d => rx.IsMatch(Path.GetFileName(d)) && Directory.EnumerateFiles(d, "*.dwg").Any());
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>One reference per floor-named DWG in the folder (placed at Revit's 0,0 by office standard).</summary>
        public static List<ReferenceDrawing> FromFolder(string folder)
        {
            var list = new List<ReferenceDrawing>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
            foreach (var file in Directory.EnumerateFiles(folder, "*.dwg").OrderBy(f => f))
            {
                var floor = FloorOf(file);
                if (floor != null)
                    list.Add(new ReferenceDrawing { Floor = floor, Name = Path.GetFileName(file), Path = file, Method = ReferenceDrawing.XrefFolder });
            }
            return list;
        }

        /// <summary>
        /// The office grid-lines DWG (same 0,0 as the xrefs): a DWG whose name matches <paramref name="pattern"/> in the
        /// xref folder, its parent or grandparent (Xref/Xref GL/GRID LINES.dwg next to Xref/Xref ME). Null if none.
        /// </summary>
        public static string FindGridFile(string xrefFolder, string pattern)
        {
            if (string.IsNullOrEmpty(xrefFolder) || string.IsNullOrEmpty(pattern) || !Directory.Exists(xrefFolder)) return null;
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);
            var parent = Path.GetDirectoryName(xrefFolder);
            foreach (var root in new[] { xrefFolder, parent, parent == null ? null : Path.GetDirectoryName(parent) }.Where(d => !string.IsNullOrEmpty(d)))
                foreach (var d in new[] { root }.Concat(Folders(root, 2)))
                {
                    string hit;
                    try { hit = Directory.EnumerateFiles(d, "*.dwg").FirstOrDefault(f => rx.IsMatch(Path.GetFileNameWithoutExtension(f))); }
                    catch { continue; }
                    if (hit != null) return hit;
                }
            return null;
        }

        /// <summary>
        /// The office's Sleeves &amp; Openings set for this building: a PDF whose name matches <paramref name="pattern"/> in the
        /// project folder (two folders up from the engineer DWG, and below it, 3 levels deep), the newest one; outside "Archive"
        /// folders when there is one. Null if none.
        /// </summary>
        public static string FindReferenceSet(string near, string pattern)
        {
            if (string.IsNullOrEmpty(near) || string.IsNullOrEmpty(pattern)) return null;
            try
            {
                var rx = new Regex(pattern, RegexOptions.IgnoreCase);
                var start = File.Exists(near) ? Path.GetDirectoryName(near) : near;
                var root = Path.GetDirectoryName(start) ?? start;
                if (!Directory.Exists(root)) return null;
                var hits = new[] { root }.Concat(Folders(root, 3))
                    .SelectMany(d => { try { return Directory.EnumerateFiles(d, "*.pdf"); } catch { return Enumerable.Empty<string>(); } })
                    .Where(f => rx.IsMatch(Path.GetFileNameWithoutExtension(f))).ToList();
                return hits.OrderBy(f => f.IndexOf("ARCHIVE", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                           .ThenByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>A file by name under any of the folders (3 levels deep), for an imported DWG whose original path is gone.</summary>
        public static string Locate(string fileName, IEnumerable<string> roots)
        {
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var d in new[] { root }.Concat(Folders(root, 3)))
                {
                    var f = Path.Combine(d, fileName);
                    if (File.Exists(f)) return f;
                }
            return null;
        }

        private static IEnumerable<string> Folders(string root, int depth)
        {
            if (depth == 0) yield break;
            string[] subs;
            try { subs = Directory.GetDirectories(root); } catch { yield break; }
            foreach (var s in subs)
            {
                yield return s;
                foreach (var t in Folders(s, depth - 1)) yield return t;
            }
        }
    }
}
