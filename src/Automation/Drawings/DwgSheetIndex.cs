using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>A floor plan found in a DWG and the part of model space it covers.</summary>
    public class DwgFloor
    {
        public string Floor;                      // FloorKey
        public string Title;
        public string Layout;                     // sheet layout it was found on ("M-305"), null when found in model space
        public int Scale;                         // model units per paper unit (48 = 1/4" = 1'-0"), 0 when unknown
        public double MinX, MinY, MaxX, MaxY;     // model-space region of the plan (drawing units); all 0 when unknown
        public bool HasRegion => MaxX > MinX && MaxY > MinY;
    }

    public class DwgSheetIndex
    {
        public string Path;
        public string Version;
        public string Units;                 // $INSUNITS, e.g. "Inches"
        public List<string> Layouts = new List<string>();
        public List<DwgFloor> Floors = new List<DwgFloor>();
        public List<string> Warnings = new List<string>();

        private static readonly Regex MTextCodes = new Regex(@"^(\\?[A-Za-z]\d*(\.\d+)?;)+");

        /// <summary>
        /// Finds the floor plans in a DWG. Primary: each sheet layout whose title block names one floor; its largest
        /// scaled viewport gives the plan's region in model space. Fallback (no layouts): plan titles in model space.
        /// </summary>
        public static DwgSheetIndex Read(string path) => Read(Open(path), path);

        public static CadDocument Open(string path)
        {
            using (var reader = new DwgReader(path)) return reader.Read();
        }

        public static DwgSheetIndex Read(CadDocument doc, string path)
        {
            var index = new DwgSheetIndex { Path = path };

            index.Version = doc.Header.Version.ToString();
            index.Units = doc.Header.InsUnits.ToString();

            foreach (var layout in doc.Layouts.Where(l => !string.Equals(l.Name, "Model", StringComparison.OrdinalIgnoreCase)).OrderBy(l => l.TabOrder))
            {
                index.Layouts.Add(layout.Name);
                var paper = layout.AssociatedBlock?.Entities?.ToList();
                if (paper == null) continue;

                var floors = Texts(paper, 0).Select(Clean).Select(t => (Text: t, Floor: FloorKey.FromPlanTitle(t)))
                                            .Where(t => t.Floor != null).ToList();
                var distinct = floors.Select(f => f.Floor).Distinct().ToList();
                if (distinct.Count != 1) continue;          // no plan, or a sheet with several plans (handled later if needed)

                var vp = PlanViewport(paper);
                var f = new DwgFloor { Floor = distinct[0], Title = floors[0].Text, Layout = layout.Name };
                if (vp != null)
                {
                    // ViewCenter is in display coordinates around the view target; in a plan view (looking down Z)
                    // the model-space centre is target + centre rotated by the twist angle.
                    double tw = vp.TwistAngle, cos = Math.Cos(tw), sin = Math.Sin(tw);
                    double cx = vp.ViewTarget.X + vp.ViewCenter.X * cos + vp.ViewCenter.Y * sin;
                    double cy = vp.ViewTarget.Y - vp.ViewCenter.X * sin + vp.ViewCenter.Y * cos;
                    double h = vp.ViewHeight, w = vp.ViewHeight * vp.Width / vp.Height;
                    f.MinX = cx - w / 2; f.MaxX = cx + w / 2;
                    f.MinY = cy - h / 2; f.MaxY = cy + h / 2;
                    f.Scale = (int)Math.Round(vp.ViewHeight / vp.Height);
                    if (Math.Abs(tw) > 1e-6)
                        index.Warnings.Add($"{layout.Name}: the plan viewport is rotated; its region is approximate.");
                    if (Math.Abs(vp.ViewDirection.X) + Math.Abs(vp.ViewDirection.Y) > 1e-6 * Math.Abs(vp.ViewDirection.Z))
                        index.Warnings.Add($"{layout.Name}: the plan viewport is not a top view; its region may be wrong.");
                }
                else index.Warnings.Add($"{layout.Name}: {f.Title} has no scaled viewport; its model-space region is unknown.");
                index.Floors.Add(f);
            }

            if (index.Floors.Count == 0) ModelSpaceFallback(doc, index);

            foreach (var dup in index.Floors.GroupBy(f => f.Floor).Where(g => g.Count() > 1))
                index.Warnings.Add($"{FloorKey.Describe(dup.Key)} is on several sheets ({string.Join(", ", dup.Select(f => f.Layout ?? "model"))}); the first is used.");
            index.Floors = index.Floors.GroupBy(f => f.Floor).Select(g => g.First()).OrderBy(f => FloorKey.Order(f.Floor)).ToList();
            if (index.Floors.Count == 0) index.Warnings.Add("No floor plans found (no layout title block or model-space title names a single floor).");
            return index;
        }

        /// <summary>The plan viewport of a sheet: the largest viewport by model-space area that is actually scaled (not the 1:1 sheet itself).</summary>
        private static Viewport PlanViewport(List<Entity> paper) =>
            paper.OfType<Viewport>()
                 .Where(v => v.Width > 0 && v.Height > 0 && v.ViewHeight / v.Height > 1.5)
                 .OrderByDescending(v => v.ViewHeight * v.ViewHeight * v.Width / v.Height)
                 .FirstOrDefault();

        /// <summary>No usable layouts: plan titles in model space, tallest size class only (index rows are small and start with a sheet number).</summary>
        private static void ModelSpaceFallback(CadDocument doc, DwgSheetIndex index)
        {
            var hits = new List<(string Floor, string Text, double Height)>();
            Heights(doc.Entities, 0, 1, hits);
            if (hits.Count > 0)
            {
                double tallest = hits.Max(x => x.Height);
                foreach (var g in hits.Where(x => x.Height >= tallest * 0.5).GroupBy(x => x.Floor))
                    index.Floors.Add(new DwgFloor { Floor = g.Key, Title = g.First().Text });
                index.Warnings.Add("Floors found from model-space titles (no sheet layouts); plan regions are unknown.");
                return;
            }

            // Single-floor drawing (a per-floor xref like "05.5-TH FLOOR ME.dwg"): the file name names the floor.
            var byName = FloorKey.Find(System.IO.Path.GetFileNameWithoutExtension(index.Path).Replace('.', ' ').Replace('-', ' '));
            if (byName.Count == 1)
            {
                index.Floors.Add(new DwgFloor { Floor = byName[0], Title = System.IO.Path.GetFileName(index.Path) });
                index.Warnings.Add("Floor taken from the file name; the whole drawing is treated as that floor.");
            }
        }

        /// <summary>Single-floor plan titles with their effective height, including text inside blocks (packed xrefs).</summary>
        private static void Heights(IEnumerable<Entity> entities, int depth, double scale, List<(string, string, double)> hits)
        {
            foreach (var e in entities)
            {
                switch (e)
                {
                    case TextEntity t: Hit(t.Value, t.Height * scale); break;
                    case MText m: Hit(m.PlainText, m.Height * scale); break;
                    case Insert ins:
                        foreach (var a in ins.Attributes) Hit(a.Value, a.Height);
                        if (depth < 3 && ins.Block?.Entities != null) Heights(ins.Block.Entities, depth + 1, scale * Math.Abs(ins.YScale), hits);
                        break;
                }
            }
            void Hit(string text, double height)
            {
                string c = Clean(text);
                if (c.Length == 0 || Regex.IsMatch(c, @"^[A-Z]{1,2}-\d{3}")) return;   // drawing-index row
                var fl = FloorKey.FromPlanTitle(c);
                if (fl != null) hits.Add((fl, c, Math.Abs(height)));
            }
        }

        /// <summary>Text, MText and attribute values on a sheet, including inside title-block inserts.</summary>
        private static IEnumerable<string> Texts(IEnumerable<Entity> entities, int depth)
        {
            foreach (var e in entities)
            {
                switch (e)
                {
                    case TextEntity t: yield return t.Value; break;
                    case MText m: yield return m.PlainText; break;
                    case Insert ins:
                        foreach (var a in ins.Attributes) yield return a.Value;
                        if (depth < 2 && ins.Block?.Entities != null)
                            foreach (var s in Texts(ins.Block.Entities, depth + 1)) yield return s;
                        break;
                }
            }
        }

        private static string Clean(string s) =>
            s == null ? "" : MTextCodes.Replace(Regex.Replace(s, @"\s+", " ").Trim(), "").Trim();
    }
}
