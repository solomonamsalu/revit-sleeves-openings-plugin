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
        /// <summary>A typical plan ("2ND THRU 7TH FLOOR PLAN"): every floor it is drawn for; null for a one-floor plan.
        /// The index holds one DwgFloor per floor of the range, all with the same drawing.</summary>
        public List<string> Typical;
        /// <summary>The drawing it was read from: two floors with the same Source are one drawing (a typical plan's copies).</summary>
        public string Source => $"{Layout}|{Title}|{MinX:0.#},{MinY:0.#}";
    }

    public class DwgSheetIndex
    {
        public string Path;
        public string Version;
        public string Units;                 // $INSUNITS, e.g. "Inches"
        public List<string> Layouts = new List<string>();
        public List<DwgFloor> Floors = new List<DwgFloor>();
        public List<string> Warnings = new List<string>();

        public DwgFloor For(string floor) => Floors.FirstOrDefault(f => f.Floor == floor);

        /// <summary>
        /// Both floors come from one typical plan: their agreement is one drawing repeating itself, not two plans
        /// confirming each other (confidence, alignment votes and check risers count it once).
        /// </summary>
        public bool SameDrawing(string a, string b)
        {
            if (a == null || b == null || a == b) return false;
            var fa = For(a); var fb = For(b);
            return fa?.Typical != null && fb?.Typical != null && fa.Source == fb.Source;
        }

        /// <summary>"from the typical plan for 2ND–7TH FLOOR (M-301)" for a floor read from one; null otherwise.</summary>
        public string TypicalNote(string floor)
        {
            var f = For(floor);
            return f?.Typical == null ? null : $"from the typical plan for {FloorKey.DescribeRange(f.Typical)} ({f.Layout ?? f.Title}): the same drawing on each of those floors";
        }

        /// <summary>One DwgFloor per floor of a typical plan, each a copy of <paramref name="f"/>.</summary>
        private static IEnumerable<DwgFloor> PerFloor(DwgFloor f, List<string> floors)
        {
            if (floors.Count == 1) { f.Floor = floors[0]; yield return f; yield break; }
            foreach (var key in floors)
                yield return new DwgFloor { Floor = key, Title = f.Title, Layout = f.Layout, Scale = f.Scale, MinX = f.MinX, MinY = f.MinY, MaxX = f.MaxX, MaxY = f.MaxY, Typical = floors };
        }

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

                // one floor, or a typical plan's range ("2ND THRU 7TH FLOOR PLAN")
                var floors = Texts(paper, 0).Select(Clean).Select(t => (Text: t, Floors: FloorKey.PlanFloors(t)))
                                            .Where(t => t.Floors.Count > 0).ToList();
                var distinct = floors.Select(f => string.Join(",", f.Floors)).Distinct().ToList();
                if (distinct.Count != 1) continue;          // no plan, or a sheet with several plans (handled later if needed)

                var vp = PlanViewport(paper);
                var f = new DwgFloor { Floor = floors[0].Floors[0], Title = floors[0].Text, Layout = layout.Name };
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
                if (floors[0].Floors.Count > 1)
                    index.Warnings.Add($"{layout.Name} is a typical plan ('{f.Title}'): used for each of {FloorKey.DescribeRange(floors[0].Floors)}.");
                index.Floors.AddRange(PerFloor(f, floors[0].Floors));
            }

            if (index.Floors.Count == 0) ModelSpaceFallback(doc, index);

            // a floor drawn on its own sheet wins over the same floor in a typical plan's range; said, as it can be a title
            // typo (an 8th floor sheet titled 6TH leaves the 8th floor without a plan)
            foreach (var g in index.Floors.GroupBy(f => f.Floor).Where(g => g.Any(x => x.Typical == null) && g.Any(x => x.Typical != null)))
                index.Warnings.Add($"{FloorKey.Describe(g.Key)} has its own sheet ({g.First(x => x.Typical == null).Layout ?? "model"}) and is also in the typical plan " +
                                   $"{g.First(x => x.Typical != null).Layout ?? "model"} ({FloorKey.DescribeRange(g.First(x => x.Typical != null).Typical)}): its own sheet is used. Check that sheet's title.");
            foreach (var dup in index.Floors.GroupBy(f => f.Floor).Where(g => g.Count(x => x.Typical == null) > 1))
                index.Warnings.Add($"{FloorKey.Describe(dup.Key)} is on several sheets ({string.Join(", ", dup.Where(x => x.Typical == null).Select(f => f.Layout ?? "model"))}); the first is used.");
            index.Floors = index.Floors.GroupBy(f => f.Floor).Select(g => g.OrderBy(x => x.Typical == null ? 0 : 1).First()).OrderBy(f => FloorKey.Order(f.Floor)).ToList();
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
                {
                    var range = FloorKey.PlanFloors(g.First().Text);
                    index.Floors.Add(new DwgFloor { Floor = g.Key, Title = g.First().Text, Typical = range.Count > 1 ? range : null });
                }
                index.Warnings.Add("Floors found from model-space titles (no sheet layouts); plan regions are unknown.");
                return;
            }

            // Single-floor drawing (a per-floor xref like "05.5-TH FLOOR ME.dwg"): the file name names the floor.
            var byName = FloorKey.FileFloors(index.Path);
            if (byName.Count > 0)
            {
                index.Floors.AddRange(PerFloor(new DwgFloor { Title = System.IO.Path.GetFileName(index.Path) }, byName));
                index.Warnings.Add(byName.Count == 1 ? "Floor taken from the file name; the whole drawing is treated as that floor."
                                                     : $"Floors taken from the file name: the whole drawing is the typical plan of {FloorKey.DescribeRange(byName)}.");
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
                foreach (var fl in FloorKey.PlanFloors(c)) hits.Add((fl, c, Math.Abs(height)));
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
