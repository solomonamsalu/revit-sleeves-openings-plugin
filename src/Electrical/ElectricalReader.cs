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
        /// <summary>Room names on this floor (Revit feet): where the riser may go and where it may not (rules 17, 20-21).</summary>
        public List<SpaceLabel> Spaces = new List<SpaceLabel>();

        /// <summary>Wall lines on the architect's wall layers (Revit feet): what the riser goes against (rules 20-21).</summary>
        public List<(double X0, double Y0, double X1, double Y1)> Walls = new List<(double X0, double Y0, double X1, double Y1)>();
        /// <summary>Door lines and swings (Revit feet): a riser is never in a doorway, and a room ends at its door.</summary>
        public List<(double X0, double Y0, double X1, double Y1)> DoorLines = new List<(double X0, double Y0, double X1, double Y1)>();
        /// <summary>Around each door hinge, a leaf's length every way (Revit feet): the floor the door sweeps and the floor just inside the doorway.</summary>
        public List<(double X0, double Y0, double X1, double Y1)> Doors = new List<(double X0, double Y0, double X1, double Y1)>();
        /// <summary>How the apartments were counted: tag blocks, or the apartment names written as text.</summary>
        public string CountedBy;

        public string NumbersText => Numbers.Count == 0 ? "" : string.Join(", ", Numbers);

        /// <summary>The same drawing read as another of the typical floors it is drawn for.</summary>
        public FloorApartments As(string floor)
        {
            var copy = (FloorApartments)MemberwiseClone();
            copy.Floor = floor;
            copy.Notes = new List<string>(Notes) { "typical floor: " + File + " is drawn once for several floors" };
            return copy;
        }
    }

    /// <summary>A room name in the architect's drawing: a riser space (closet, shaft…) or a living space (bedroom…).</summary>
    public class SpaceLabel
    {
        public double X, Y;                         // Revit feet
        public string Text;
        public bool RiserSpace;                     // false = a living space the riser must not go through
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
        private static readonly Regex BareApartment = new Regex(@"(?:APT|APART(?:MENT)?)\.?\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex RoomLike =new Regex(@"ELEC|MECH|METER|BOILER|\bROOM\b|\bRM\b|CLOSET", RegexOptions.IgnoreCase);

        /// <summary>Diagnostic trace (the command points it at App.Log); null = silent, so the reader stays Revit-free.</summary>
        public static Action<string> Trace;
        private static void T(string msg) => Trace?.Invoke("ELEC| " + msg);

        /// <summary>The architectural xref folder next to the model; null when the office keeps a different layout.</summary>
        public static string FindFolder(string modelPath, DwgProfile profile) =>
            ReferenceFiles.FindFolder(modelPath, profile?.ArchitecturalFolders);

        public static ElectricalReadResult Read(string folder, DwgProfile profile)
        {
            var result = new ElectricalReadResult { Folder = folder };
            T("READ folder = " + (folder ?? "(none)") + (folder != null && !Directory.Exists(folder) ? " [DOES NOT EXIST]" : ""));
            T("READ patterns: tagBlocks='" + profile.ApartmentTagBlocks + "' tagLayers='" + profile.ApartmentTagLayers +
              "' elecRoom='" + profile.ElectricalRoomText + "' mechRoom='" + profile.MechanicalRoomText +
              "' numberAttrs=[" + string.Join(",", profile.ApartmentNumberAttributes ?? new List<string>()) + "]");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                result.Warnings.Add("No architectural xref folder next to the model; type the apartment counts.");
                return result;
            }

            int skipped = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.dwg").OrderBy(f => f))
            {
                // one floor, or the typical floors drawn once ("03.3 TH TO 7 TH FLOOR.dwg" -> F3..F7): read once, used for each
                var floors = FloorKey.FileFloors(Path.GetFileName(file));
                if (floors.Count == 0)
                {
                    T("FILE SKIPPED " + Path.GetFileName(file) + ": no floor in the name");
                    skipped++; continue;
                }
                T("FILE " + Path.GetFileName(file) + " -> floor(s) " + string.Join(", ", floors));
                try
                {
                    var read = ReadFloor(file, floors[0], profile, result.Warnings);
                    result.Floors.Add(read);
                    foreach (var other in floors.Skip(1)) result.Floors.Add(read.As(other));
                }
                catch (Exception ex)
                {
                    T("FILE FAILED " + Path.GetFileName(file) + ": " + ex);
                    result.Warnings.Add(Path.GetFileName(file) + ": could not be read (" + ex.Message + ")");
                }
            }
            CheckNumbers(result);
            T("READ done: " + result.Floors.Count + " floor(s) read, " + skipped + " file(s) skipped, " + result.Total + " apartments total; start = " +
              (result.Start == null ? "none" : result.Start.Floor + " '" + result.Start.RoomText + "' at (" +
                  string.Join(", ", result.StartPoint.Select(v => v.ToString("0.###"))) + ") ft" + (result.StartIsMechanical ? " [MECHANICAL]" : "")));

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
            // the typical floors share one drawing and so its numbers: one file, not a unit tagged twice
            var seen = result.Floors.SelectMany(f => f.Numbers.Select(n => new { Floor = f, Number = n }))
                                    .GroupBy(x => x.Number, StringComparer.OrdinalIgnoreCase)
                                    .Where(g => g.Select(x => x.Floor.File).Distinct().Count() > 1);
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
            var riserSpace = Rx(profile.RiserSpaceText);
            var livingSpace = Rx(profile.LivingSpaceText);
            var wallLayers = Rx(profile.ArchitecturalWallLayers);
            var doorLayers = Rx(profile.ArchitecturalDoorLayers);
            var aptLabel = Rx(profile.ApartmentLabelText);
            var labelled = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);     // apartments named in text

            var tags = new List<(double X, double Y, string Number)>();
            var found = new List<(double X, double Y, string Text)>();
            var mechFound = new List<(double X, double Y, string Text)>();
            // diagnostics only: what the drawing holds, so a missed or extra tag can be traced to its block/layer
            var inserts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tagHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var roomLike = new List<string>();

            // door swings: hinge and the arc's two ends (one is the closed leaf's tip, on the wall; the other the open leaf's)
            var swings = new List<((double X, double Y) Hinge, (double X, double Y) A, (double X, double Y) B)>();

            // A line, polyline or arc as straight pieces (drawing units); a door's swing is kept to find its doorway.
            void Geometry(Entity e, DwgXform at, List<(double X0, double Y0, double X1, double Y1)> into, bool door)
            {
                var pts = new List<(double X, double Y)>();
                switch (e)
                {
                    case Line ln:
                        pts.Add(at.Apply(ln.StartPoint.X, ln.StartPoint.Y)); pts.Add(at.Apply(ln.EndPoint.X, ln.EndPoint.Y));
                        break;
                    case LwPolyline pl:
                        foreach (var v in pl.Vertices) pts.Add(at.Apply(v.Location.X, v.Location.Y));
                        if (pl.IsClosed && pts.Count > 2) pts.Add(pts[0]);
                        break;
                    case Arc arc:
                        double a0 = arc.StartAngle, a1 = arc.EndAngle;
                        if (a1 < a0) a1 += 2 * Math.PI;
                        for (int k = 0; k <= 8; k++)
                        {
                            double a = a0 + (a1 - a0) * k / 8;
                            pts.Add(at.Apply(arc.Center.X + arc.Radius * Math.Cos(a), arc.Center.Y + arc.Radius * Math.Sin(a)));
                        }
                        if (door) swings.Add((at.Apply(arc.Center.X, arc.Center.Y), pts[0], pts[pts.Count - 1]));
                        break;
                    default: return;
                }
                for (int k = 1; k < pts.Count; k++) into.Add((pts[k - 1].X, pts[k - 1].Y, pts[k].X, pts[k].Y));
            }

            // AutoCAD: what a block draws on layer 0 takes the layer the block is inserted on (a door block on A-DOOR)
            void Walk(IEnumerable<Entity> entities, int depth, DwgXform at, string blockLayer = null)
            {
                if (entities == null || depth > 6) return;
                foreach (var e in entities)
                {
                    string own = e.Layer?.Name ?? "";
                    string onLayer = (own == "" || own == "0") && blockLayer != null ? blockLayer : own;
                    if (!(e is Insert) && (wallLayers != null || doorLayers != null))
                    {
                        bool wall = wallLayers != null && wallLayers.IsMatch(onLayer), door = !wall && doorLayers != null && doorLayers.IsMatch(onLayer);
                        if (wall || door) Geometry(e, at, wall ? info.Walls : info.DoorLines, door);
                    }

                    if (e is Insert ins)
                    {
                        string name = ins.Block?.Source?.Name ?? ins.Block?.Name ?? "";
                        string layer = onLayer;
                        string key = "block '" + name + "' on layer '" + layer + "' (depth " + depth + ")";
                        inserts[key] = inserts.TryGetValue(key, out var c) ? c + 1 : 1;
                        if ((blocks != null && blocks.IsMatch(name)) || (layers != null && layers.IsMatch(layer)))
                        {
                            var (tx, ty) = at.Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
                            tags.Add((tx, ty, Number(ins, profile)));
                            string why = (blocks != null && blocks.IsMatch(name) ? "name" : "") + (layers != null && layers.IsMatch(layer) ? "+layer" : "");
                            tagHits[key + " matched by " + why.TrimStart('+')] = tagHits.TryGetValue(key + " matched by " + why.TrimStart('+'), out var h) ? h + 1 : 1;
                            continue;               // the tag's own text is its prompt ("APT", "#"), not another tag
                        }
                        Walk(ins.Block?.Entities, depth + 1, at.Then(ins), layer);
                        continue;
                    }

                    double x, y;
                    string text;
                    if (e is TextEntity t) { text = t.Value; x = t.InsertPoint.X; y = t.InsertPoint.Y; }
                    else if (e is MText m) { text = m.PlainText; x = m.InsertPoint.X; y = m.InsertPoint.Y; }
                    else continue;

                    text = Clean(text);
                    if (text.Length == 0) continue;

                    if (aptLabel != null)
                    {
                        bool any = false;
                        foreach (Match am in aptLabel.Matches(text))
                            if (am.Groups["n"].Success) { labelled.Add(am.Groups["n"].Value.ToUpperInvariant()); any = true; }
                        // "L2 BEDROOM APART." with its number in another text: still one apartment, told apart by where it is
                        if (!any && BareApartment.IsMatch(text))
                        {
                            var (bx, by) = at.Apply(x, y);
                            labelled.Add("?@" + Math.Round(bx) + "," + Math.Round(by));
                        }
                    }

                    // room names: where the riser may go and where it may not; a name matching both is a riser space
                    bool isRiser = riserSpace != null && riserSpace.IsMatch(text);
                    if (text.Length <= 40 && (isRiser || (livingSpace != null && livingSpace.IsMatch(text))))
                    {
                        var (sx, sy) = at.Apply(x, y);
                        info.Spaces.Add(new SpaceLabel { X = sx, Y = sy, Text = text, RiserSpace = isRiser });
                    }
                    if (rooms != null && rooms.IsMatch(text)) { var (rx, ry) = at.Apply(x, y); found.Add((rx, ry, text)); }
                    else if (mech != null && mech.IsMatch(text)) { var (mx, my) = at.Apply(x, y); mechFound.Add((mx, my, text)); }
                    else if (RoomLike.IsMatch(text) && roomLike.Count < 40)
                    {
                        var (ux, uy) = at.Apply(x, y);
                        roomLike.Add("'" + text + "' at (" + (ux * feet).ToString("0.##") + ", " + (uy * feet).ToString("0.##") + ") ft");
                    }
                }
            }

            foreach (var layout in cad.Layouts.Where(l => string.Equals(l.Name, "Model", StringComparison.OrdinalIgnoreCase)))
                Walk(layout.AssociatedBlock?.Entities, 0, DwgXform.Identity);
            foreach (var sw in swings) info.Doors.Add(Doorway(sw.Hinge, sw.A, sw.B, info.DoorLines, (2.0 / 12) / feet));
            foreach (var s in info.Spaces) { s.X *= feet; s.Y *= feet; }
            (double, double, double, double) Feet((double X0, double Y0, double X1, double Y1) s) => (s.X0 * feet, s.Y0 * feet, s.X1 * feet, s.Y1 * feet);
            info.Walls = info.Walls.Select(Feet).ToList();
            info.DoorLines = info.DoorLines.Select(Feet).ToList();
            info.Doors = info.Doors.Select(Feet).ToList();

            // Two tags at the same spot are one tag drawn twice.
            var unique = new List<(double X, double Y, string Number)>();
            foreach (var tag in tags)
                if (!unique.Any(u => Math.Abs(u.X - tag.X) < 1 && Math.Abs(u.Y - tag.Y) < 1)) unique.Add(tag);

            info.Apartments = unique.Count;
            info.Numbers = unique.Select(u => u.Number).Where(n => n != null)
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            info.CountedBy = unique.Count > 0 ? "tags" : null;
            if (unique.Count == 0 && labelled.Count > 0)
            {
                // no tag blocks: the apartments as the drawing names them in text ("L2 BEDROOM APART. 1C")
                info.Apartments = labelled.Count;
                info.Numbers = labelled.Where(n => !n.StartsWith("?")).ToList();
                if (labelled.Any(n => n.StartsWith("?")))
                    info.Notes.Add(labelled.Count(n => n.StartsWith("?")) + " apartment name(s) without a number — counted, check the floor's count");
                info.CountedBy = "names";
            }

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

            if (Trace != null)
            {
                T("  " + info.File + ": units '" + units + "' (" + feet.ToString("0.#####") + " ft/unit), " + inserts.Values.Sum() + " block insert(s), " +
                  tags.Count + " tag insert(s) -> " + unique.Count + " after same-spot merge -> APARTMENTS = " + info.Apartments +
                  "; distinct numbers = " + info.Numbers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                foreach (var kv in tagHits.OrderByDescending(k => k.Value)) T("    TAG   x" + kv.Value + " " + kv.Key);
                foreach (var kv in inserts.Where(k => !tagHits.Keys.Any(t => t.StartsWith(k.Key))).OrderByDescending(k => k.Value).Take(25))
                    T("    other x" + kv.Value + " " + kv.Key);
                T("    numbers: [" + info.NumbersText + "]");
                foreach (var u in unique) T("    tag at (" + (u.X * feet).ToString("0.##") + ", " + (u.Y * feet).ToString("0.##") + ") ft #" + (u.Number ?? "(none)"));
                foreach (var f in found) T("    ELEC ROOM text '" + f.Text + "' at (" + (f.X * feet).ToString("0.###") + ", " + (f.Y * feet).ToString("0.###") + ") ft");
                foreach (var f in mechFound) T("    MECH ROOM text '" + f.Text + "' at (" + (f.X * feet).ToString("0.###") + ", " + (f.Y * feet).ToString("0.###") + ") ft");
                foreach (var r in roomLike) T("    room-like text NOT matched: " + r);
                T("    counted by " + (info.CountedBy ?? "nothing") + "; apartment names in text: [" + string.Join(", ", labelled) + "]; " +
                  info.Walls.Count + " wall line(s), " + info.DoorLines.Count + " door line(s), " + info.Doors.Count + " door swing(s)");
                T("    spaces: " + info.Spaces.Count(s => s.RiserSpace) + " riser space(s) [" +
                  string.Join(", ", info.Spaces.Where(s => s.RiserSpace).Select(s => s.Text + " (" + s.X.ToString("0.#") + "," + s.Y.ToString("0.#") + ")")) + "], " +
                  info.Spaces.Count(s => !s.RiserSpace) + " living space(s) [" + string.Join(", ", info.Spaces.Where(s => !s.RiserSpace).Select(s => s.Text).Distinct()) + "]");
                foreach (var n in info.Notes) T("    note: " + n);
            }
            return info;
        }

        /// <summary>
        /// The floor a riser must keep off at a door (drawing units): the doorway's width, a leaf deep, on both sides of the
        /// wall — the floor the door sweeps and the way in on the other side (a closet's doors swing out, but its doorway is
        /// still where you stand). Which arc end is the open leaf is told by the leaf drawn from the hinge to it; when no
        /// leaf is drawn, the square a leaf's length around the hinge.
        /// </summary>
        private static (double X0, double Y0, double X1, double Y1) Doorway((double X, double Y) h, (double X, double Y) a, (double X, double Y) b,
                                                                           List<(double X0, double Y0, double X1, double Y1)> doorLines, double tol)
        {
            double leaf = Math.Max(Dist(h, a), Dist(h, b));
            bool leafA = HasLeaf(h, a, doorLines, tol), leafB = HasLeaf(h, b, doorLines, tol);
            if (leafA == leafB) return (h.X - leaf, h.Y - leaf, h.X + leaf, h.Y + leaf);
            var (open, closed) = leafA ? (a, b) : (b, a);
            double ux = closed.X - h.X, uy = closed.Y - h.Y, vx = open.X - h.X, vy = open.Y - h.Y;
            var corners = new[] { (h.X, h.Y), (h.X + ux, h.Y + uy), (h.X + vx, h.Y + vy), (h.X + ux + vx, h.Y + uy + vy), (h.X - vx, h.Y - vy), (h.X + ux - vx, h.Y + uy - vy) };
            return (corners.Min(p => p.Item1), corners.Min(p => p.Item2), corners.Max(p => p.Item1), corners.Max(p => p.Item2));
        }

        /// <summary>A door line runs along hinge→tip (within tol) for at least half its length: the leaf drawn open.</summary>
        private static bool HasLeaf((double X, double Y) h, (double X, double Y) tip, List<(double X0, double Y0, double X1, double Y1)> lines, double tol)
        {
            double len = Dist(h, tip);
            if (len < 1e-9) return false;
            double dx = (tip.X - h.X) / len, dy = (tip.Y - h.Y) / len;
            foreach (var l in lines)
            {
                double Off(double x, double y) => Math.Abs((x - h.X) * dy - (y - h.Y) * dx);
                double Along(double x, double y) => (x - h.X) * dx + (y - h.Y) * dy;
                if (Off(l.X0, l.Y0) > tol || Off(l.X1, l.Y1) > tol) continue;
                double s0 = Along(l.X0, l.Y0), s1 = Along(l.X1, l.Y1);
                if (Math.Min(s0, s1) > -tol && Math.Max(s0, s1) < len + tol && Math.Abs(s1 - s0) >= len / 2) return true;
            }
            return false;
        }

        private static double Dist((double X, double Y) p, (double X, double Y) q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));

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
