using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp.Entities;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Electrical
{
    /// <summary>What one architectural floor plan says: how many apartments it holds and where its electrical room is.</summary>
    public class FloorApartments
    {
        public string Floor;                        // FloorKey
        public string File;
        public int Apartments;
        public List<string> Numbers = new List<string>();
        /// <summary>Electrical room label in Revit feet (the xrefs share Revit's 0,0); null when this floor names none.</summary>
        public double[] ElectricalRoom;
        /// <summary>Mechanical room label, used only when the building names no electrical room (the manual's note under the electrical rules).</summary>
        public double[] MechanicalRoom;
        public string RoomText;
        public List<string> Notes = new List<string>();

        public string NumbersText => Numbers.Count == 0 ? "" : string.Join(", ", Numbers);
    }

    /// <summary>Apartment counts and the electrical room, read off the architect's per-floor xrefs.</summary>
    public class ElectricalReadResult
    {
        public string Folder;
        public List<FloorApartments> Floors = new List<FloorApartments>();
        public List<string> Warnings = new List<string>();

        /// <summary>
        /// Lowest floor naming an electrical room: where the riser starts (electrical rule 3). With no electrical room
        /// anywhere, the lowest mechanical room, which is where the manual says the services then come from.
        /// </summary>
        public FloorApartments Start =>
            Floors.Where(f => f.ElectricalRoom != null).OrderBy(f => FloorKey.Order(f.Floor)).FirstOrDefault()
            ?? Floors.Where(f => f.MechanicalRoom != null).OrderBy(f => FloorKey.Order(f.Floor)).FirstOrDefault();

        /// <summary>The riser starts at a mechanical room because the drawings name no electrical room.</summary>
        public bool StartIsMechanical => Start != null && Start.ElectricalRoom == null;

        /// <summary>Where the riser starts, in Revit feet; null when the drawings name neither room.</summary>
        public double[] StartPoint => Start == null ? null : (Start.ElectricalRoom ?? Start.MechanicalRoom);

        public int Total => Floors.Sum(f => f.Apartments);
        public bool Any => Floors.Any(f => f.Apartments > 0);
    }

    /// <summary>
    /// Reads the architect's per-floor xrefs for the two numbers the electrical riser needs: how many apartments each
    /// floor holds (one conduit each, electrical rules 5-7) and the electrical room the riser starts from (rule 3).
    /// Revit-API-free, so it can be run against the DWGs outside Revit.
    /// </summary>
    public static class ElectricalReader
    {
        private static readonly Regex Numeric = new Regex(@"^[A-Z]{0,3}[ -]?\d{1,4}[A-Z]?$", RegexOptions.IgnoreCase);
        private static readonly Regex MTextCodes = new Regex(@"^(\\?[A-Za-z]\d*(\.\d+)?;)+");

        /// <summary>The architectural xref folder next to the model; null when the office keeps a different layout.</summary>
        public static string FindFolder(string modelPath, DwgProfile profile) =>
            ReferenceFiles.FindFolder(modelPath, profile?.ArchitecturalFolders);

        public static ElectricalReadResult Read(string folder, DwgProfile profile)
        {
            var result = new ElectricalReadResult { Folder = folder };
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                result.Warnings.Add("No architectural xref folder next to the model; type the apartment counts.");
                return result;
            }

            int skipped = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.dwg").OrderBy(f => f))
            {
                var floor = ReferenceFiles.FloorOf(file);
                if (floor == null) { skipped++; continue; }
                try { result.Floors.Add(ReadFloor(file, floor, profile, result.Warnings)); }
                catch (Exception ex) { result.Warnings.Add(Path.GetFileName(file) + ": could not be read (" + ex.Message + ")"); }
            }
            CheckNumbers(result);

            if (result.Floors.Count == 0)
                result.Warnings.Add(skipped > 0
                    ? "None of the " + skipped + " DWG(s) in " + folder + " is named after one floor, so the drawings could not be " +
                      "read per floor (a whole-building drawing is one file, not one per floor); type the apartment counts."
                    : "No floor-named DWGs in " + folder + "; type the apartment counts.");
            else if (!result.Any)
                result.Warnings.Add("The architectural drawings carry no apartment tags (block '" + profile.ApartmentTagBlocks +
                                    "', layer '" + profile.ApartmentTagLayers + "'); type the counts.");
            if (result.Any && result.Start == null)
                result.Warnings.Add("No electrical or mechanical room labelled in the drawings; click the riser location yourself (rule 3).");
            else if (result.StartIsMechanical)
                result.Warnings.Add("No electrical room in the drawings, so the riser starts at the mechanical room on " +
                                    FloorKey.Describe(result.Start.Floor) + " — the manual's note under the electrical rules.");
            return result;
        }

        /// <summary>
        /// Every unit should be tagged once in the whole building, so the same number on two floors is a drawing
        /// error, not two apartments — and it would add a conduit that does not exist. Counted, never corrected.
        /// </summary>
        private static void CheckNumbers(ElectricalReadResult result)
        {
            var seen = result.Floors.SelectMany(f => f.Numbers.Select(n => new { Floor = f, Number = n }))
                                    .GroupBy(x => x.Number, StringComparer.OrdinalIgnoreCase)
                                    .Where(g => g.Select(x => x.Floor).Distinct().Count() > 1);
            foreach (var g in seen)
                result.Warnings.Add("Unit " + g.Key + " is tagged on " +
                                    string.Join(" and ", g.Select(x => FloorKey.Describe(x.Floor.Floor)).Distinct()) +
                                    "; one of them is probably a different unit, so check that floor's count.");
        }

        private static FloorApartments ReadFloor(string path, string floor, DwgProfile profile, List<string> warnings)
        {
            var cad = DwgSheetIndex.Open(path);
            var info = new FloorApartments { Floor = floor, File = Path.GetFileName(path) };

            string units = cad.Header.InsUnits.ToString();
            double feet = PlanMap.FeetPerUnit(units) ?? (1.0 / 12);
            if (PlanMap.FeetPerUnit(units) == null)
                warnings.Add(info.File + ": units are '" + units + "'; read as inches.");

            var blocks = Rx(profile.ApartmentTagBlocks);
            var layers = Rx(profile.ApartmentTagLayers);
            var rooms = Rx(profile.ElectricalRoomText);
            var mech = Rx(profile.MechanicalRoomText);

            var tags = new List<(double X, double Y, string Number)>();
            var found = new List<(double X, double Y, string Text)>();
            var mechFound = new List<(double X, double Y, string Text)>();

            void Walk(IEnumerable<Entity> entities, int depth, DwgXform at)
            {
                if (entities == null || depth > 6) return;
                foreach (var e in entities)
                {
                    if (e is Insert ins)
                    {
                        string name = ins.Block?.Source?.Name ?? ins.Block?.Name ?? "";
                        string layer = ins.Layer?.Name ?? "";
                        if ((blocks != null && blocks.IsMatch(name)) || (layers != null && layers.IsMatch(layer)))
                        {
                            var (tx, ty) = at.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                            tags.Add((tx, ty, Number(ins, profile)));
                            continue;               // the tag's own text is its prompt ("APT", "#"), not another tag
                        }
                        Walk(ins.Block?.Entities, depth + 1, at.Then(ins));
                        continue;
                    }

                    if (rooms == null && mech == null) continue;
                    double x, y;
                    string text;
                    if (e is TextEntity t) { text = t.Value; x = t.InsertPoint.X; y = t.InsertPoint.Y; }
                    else if (e is MText m) { text = m.PlainText; x = m.InsertPoint.X; y = m.InsertPoint.Y; }
                    else continue;

                    text = Clean(text);
                    if (text.Length == 0) continue;
                    if (rooms != null && rooms.IsMatch(text)) { var (rx, ry) = at.Apply(x, y); found.Add((rx, ry, text)); }
                    else if (mech != null && mech.IsMatch(text)) { var (mx, my) = at.Apply(x, y); mechFound.Add((mx, my, text)); }
                }
            }

            foreach (var layout in cad.Layouts.Where(l => string.Equals(l.Name, "Model", StringComparison.OrdinalIgnoreCase)))
                Walk(layout.AssociatedBlock?.Entities, 0, DwgXform.Identity);

            // Two tags at the same spot are one tag drawn twice.
            var unique = new List<(double X, double Y, string Number)>();
            foreach (var tag in tags)
                if (!unique.Any(u => Math.Abs(u.X - tag.X) < 1 && Math.Abs(u.Y - tag.Y) < 1)) unique.Add(tag);

            info.Apartments = unique.Count;
            info.Numbers = unique.Select(u => u.Number).Where(n => n != null)
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

            var twice = info.Numbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (twice.Count > 0)
                info.Notes.Add("the drawing labels " + string.Join(" and ", twice) + " twice — counted as " + info.Apartments);
            if (info.Apartments > info.Numbers.Count)
                info.Notes.Add((info.Apartments - info.Numbers.Count) + " tag(s) carry no number");

            if (found.Count > 0)
            {
                info.ElectricalRoom = new[] { found[0].X * feet, found[0].Y * feet };
                info.RoomText = found[0].Text;
                if (found.Count > 1) info.Notes.Add(found.Count + " electrical room labels; took the first");
            }
            else if (mechFound.Count > 0)
            {
                info.MechanicalRoom = new[] { mechFound[0].X * feet, mechFound[0].Y * feet };
                info.RoomText = mechFound[0].Text;
            }
            return info;
        }

        private static string Number(Insert ins, DwgProfile profile)
        {
            var attributes = ins.Attributes?.Select(a => (Tag: (a.Tag ?? "").Trim(), Value: Clean(a.Value))).ToList();
            if (attributes == null || attributes.Count == 0) return null;

            foreach (var want in profile.ApartmentNumberAttributes ?? new List<string>())
            {
                var hit = attributes.FirstOrDefault(a => string.Equals(a.Tag, want, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(hit.Value) && hit.Value.Any(char.IsDigit)) return hit.Value;
            }
            return attributes.Select(a => a.Value)
                             .FirstOrDefault(v => !string.IsNullOrEmpty(v) && v.Any(char.IsDigit) && Numeric.IsMatch(v));
        }

        private static Regex Rx(string pattern) =>
            string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase);

        private static string Clean(string text) =>
            Regex.Replace(MTextCodes.Replace(text ?? "", ""), @"\s+", " ").Trim();
    }
}
