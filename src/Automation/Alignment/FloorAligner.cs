using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using SleevesOpenings.Automation.Drawings;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>A drawing of one floor that is already at Revit's position: imported/linked in the model, or an xref at the office 0,0.</summary>
    public class ReferenceDrawing
    {
        public const string Imported = "imported in the model", Linked = "linked in the model", XrefFolder = "xref at Revit 0,0";

        public string Floor;            // FloorKey; for a drawing in the model without a floor in its name, set from its level
        public string Level;            // level of the view it is placed in (or its base level); null for XrefFolder
        public string Name;             // file name, shown to the user
        public string Path;             // on disk; null when the file could not be found
        public string Method;           // Imported, Linked, XrefFolder

        /// <summary>
        /// From the model: the instance transform (rotation + shift in feet, and any scale of its own) and the instance's
        /// plan extent in feet. The drawing's units come from the file; the extent confirms them. Null for XrefFolder.
        /// </summary>
        public PlanMap Placement;
        public double ImportScale = 1;
        public double[] RevitBox;       // minX, minY, maxX, maxY (feet)
        public string Problem;

        public bool FromModel => Method != XrefFolder;

        /// <summary>A copy for one check, so a check never changes the collected list (floor/path are filled per check).</summary>
        public ReferenceDrawing Copy() => (ReferenceDrawing)MemberwiseClone();
    }

    /// <summary>What proves the Revit position: the office grid DWG (same 0,0 as the xrefs) and the Revit grids.</summary>
    public class GridInputs
    {
        public string DwgPath;               // null when no grid drawing was found
        public List<GridLine> Revit = new List<GridLine>();
        public string RevitSource;           // "this model" or the link the grids came from
    }

    /// <summary>An opening already in the model, for the position check.</summary>
    public class ExistingPoint
    {
        public string Level, Label;
        public double X, Y;             // feet
    }

    /// <summary>How one floor of the engineer DWG lands in Revit.</summary>
    public class FloorAlignment
    {
        public const string Confirmed = "confirmed", AgreesWithOthers = "agrees with the other floors",
                            DiffersFromOthers = "check: differs from the other floors", NotConfirmed = "not confirmed", NoReference = "no drawing at Revit's position";

        public string Floor, Level;
        public ReferenceDrawing Reference;
        public PlanMap Map;              // reference drawing -> Revit (feet)
        public FloorShift Shift;         // engineer floor -> reference drawing (drawing units)
        public string Status = NoReference;
        /// <summary>Openings may be placed from this floor (Phase 6 skips the others and reports them). A floor whose own
        /// match disagrees with the other floors is reported, not used.</summary>
        public bool Usable => Status == Confirmed || Status == AgreesWithOthers;
        public List<string> Notes = new List<string>();

        public int RisersChecked, RisersOnReference, RisersNearReference, RisersOnExisting;
        /// <summary>Tags that sit within 1" of the same tag on the floor below (<see cref="StackedOn"/>).</summary>
        public List<string> StackedTags = new List<string>();
        public string StackedOn;
        /// <summary>The Revit position of this floor's drawing is proven (grids, or existing openings).</summary>
        public bool RevitProven;

        /// <summary>Engineer DWG point -> Revit model point (feet). Null when the floor has no alignment.</summary>
        public (double X, double Y)? ToRevit(double x, double y) =>
            Map == null || Shift == null ? ((double, double)?)null : Map.Apply(x + Shift.Dx, y + Shift.Dy);
    }

    /// <summary>A riser whose Revit position is confirmed by something independent of the engineer DWG.</summary>
    public class AnchorRiser
    {
        public string Floor, Level, Tag;
        public double DwgX, DwgY;        // engineer DWG (drawing units)
        public double X, Y;              // Revit (feet)
        public double Distance;          // inches to the confirming item
        public string Evidence;
    }

    public class AlignmentResult
    {
        public List<FloorAlignment> Floors = new List<FloorAlignment>();
        public List<AnchorRiser> Anchors = new List<AnchorRiser>();
        public List<string> Messages = new List<string>();

        /// <summary>Plan section 10: three risers must land within 1" of an independent position, or nothing is placed.</summary>
        /// <summary>Three risers land on an independent position: the engineer DWG matches the drawing it was lined up with.</summary>
        public bool DrawingsMatch => Anchors.Count >= AnchorCount;
        /// <summary>That drawing really is at Revit's position: its grids fall on the Revit grids (or the anchors sit on existing openings).</summary>
        public bool RevitProven;
        public List<GridCheckResult> Grids = new List<GridCheckResult>();
        public string RevitSummary;
        /// <summary>
        /// Why nothing can be placed and what to do about it, in one sentence. The per-floor notes say what each test
        /// measured; this says the one thing to change. Null when the check passed.
        /// </summary>
        public string Diagnosis;
        /// <summary>Nothing is placed unless both hold.</summary>
        public bool Passed => DrawingsMatch && RevitProven;
        public const int AnchorCount = 3;
        public const double AnchorTolerance = 1.0;      // inches

        public FloorAlignment For(string floor) => Floors.FirstOrDefault(f => f.Floor == floor);
    }

    /// <summary>
    /// Phase 4: converts engineer DWG positions to Revit coordinates, floor by floor.
    /// 1. Each floor of the engineer DWG is matched to a drawing of the same floor that is already at Revit's
    ///    position (see <see cref="ReferenceDrawing"/>): the shift most same-named blocks agree on (<see cref="DwgAlignment"/>).
    /// 2. The floors check each other: a sheet set frames every floor the same way, so shift + plan-region corner is
    ///    the same on all floors. A weak floor that fits the shared value is accepted; one that doesn't is flagged.
    /// 3. Three anchor risers must land within 1" of an existing opening in the model or a riser symbol in the
    ///    reference drawing (a reference may be an older revision, so not every riser is expected to).
    /// Read-only and free of the Revit API: the model side is collected beforehand into ReferenceDrawing/ExistingPoint.
    /// </summary>
    public static class FloorAligner
    {
        private class Loaded
        {
            public List<BlockPoint> Blocks;
            public List<RiserSymbol> Risers;
            public (double MinX, double MinY, double MaxX, double MaxY)? Extents;
            public double? FeetPerUnit;
            public string Units;
        }

        public static AlignmentResult Run(CadDocument engineer, DwgSheetIndex index, DwgRiserResult risers, DwgProfile profile,
                                          IList<ReferenceDrawing> references, IDictionary<string, string> floorLevels,
                                          IList<ExistingPoint> existing, GridInputs grids = null, double riserTolerance = 6,
                                          Func<CadDocument, List<RiserSymbol>> referenceRisers = null)
        {
            var result = new AlignmentResult();
            var blocks = Points(engineer, profile);
            double? engineerFeet = PlanMap.FeetPerUnit(index.Units);
            var cache = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);

            Loaded Load(string path)
            {
                if (!cache.TryGetValue(path, out var l))
                {
                    var cad = DwgSheetIndex.Open(path);
                    cache[path] = l = new Loaded
                    {
                        Blocks = Points(cad, profile), Risers = referenceRisers?.Invoke(cad) ?? DwgRiserReader.Symbols(cad, profile), Extents = DwgAlignment.Extents(cad),
                        Units = cad.Header.InsUnits.ToString()
                    };
                    l.FeetPerUnit = PlanMap.FeetPerUnit(l.Units);
                }
                return l;
            }

            // Where the building is in Revit (feet): the Revit grids, else the office grid drawing at 0,0. A reference whose
            // match puts the floor's risers far outside it is not at Revit's 0,0 (e.g. a copy of the whole engineer set left
            // in the xref folder): it matches the engineer DWG perfectly and would send every opening miles away.
            var site = SiteBox(grids);
            bool OnSite(PlanMap map, FloorShift shift, string floor, out string where)
            {
                where = null;
                if (site == null) return true;
                var pts = risers.Risers.Where(r => r.Floor == floor).Select(r => map.Apply(r.X + shift.Dx, r.Y + shift.Dy)).ToList();
                if (pts.Count == 0) return true;
                var s = site.Value;
                int inside = pts.Count(p => p.X >= s.MinX - SiteMargin && p.X <= s.MaxX + SiteMargin && p.Y >= s.MinY - SiteMargin && p.Y <= s.MaxY + SiteMargin);
                if (inside * 5 >= pts.Count * 4) return true;
                var mid = (X: pts.Average(p => p.X), Y: pts.Average(p => p.Y));
                where = $"{Math.Sqrt(Math.Pow(mid.X - (s.MinX + s.MaxX) / 2, 2) + Math.Pow(mid.Y - (s.MinY + s.MaxY) / 2, 2)):0} ft from the grid lines";
                return false;
            }

            // ---- 1. each floor on its own
            var loadedFor = new Dictionary<FloorAlignment, Loaded>();
            var floorBlocks = new Dictionary<FloorAlignment, List<BlockPoint>>();
            var options = new Dictionary<FloorAlignment, List<(ReferenceDrawing R, PlanMap Map, Loaded L)>>();
            foreach (var floor in index.Floors)
            {
                var fa = new FloorAlignment { Floor = floor.Floor };
                floorLevels?.TryGetValue(floor.Floor, out fa.Level);
                result.Floors.Add(fa);
                if (!floor.HasRegion) { fa.Notes.Add("plan region unknown in the engineer DWG"); continue; }
                var mine = blocks.Where(b => b.X >= floor.MinX && b.X <= floor.MaxX && b.Y >= floor.MinY && b.Y <= floor.MaxY).ToList();
                floorBlocks[fa] = mine;

                var candidates = references.Where(r => r.Floor == floor.Floor).OrderBy(r => r.FromModel ? 0 : 1).ToList();
                if (candidates.Count == 0) { fa.Notes.Add("no imported/linked DWG or xref for this floor"); continue; }

                var problems = new List<string>();          // shown only when no drawing could be used
                var refOptions = options[fa] = new List<(ReferenceDrawing R, PlanMap Map, Loaded L)>();
                foreach (var r in candidates)
                {
                    if (r.Path == null || !File.Exists(r.Path)) { problems.Add($"{r.Name} ({r.Method}): {r.Problem ?? "file not found"}"); continue; }
                    Loaded l;
                    try { l = Load(r.Path); }
                    catch (Exception ex) { problems.Add($"{r.Name}: could not be read ({ex.Message})"); continue; }

                    if (l.FeetPerUnit == null || engineerFeet == null || Math.Abs(l.FeetPerUnit.Value - engineerFeet.Value) > 1e-9)
                    {
                        problems.Add($"{r.Name}: units {l.Units} differ from the engineer DWG ({index.Units}); not compared");
                        continue;
                    }
                    var map = MapFor(r, l, problems);
                    if (map == null) continue;
                    var shift = DwgAlignment.Shift(mine, l.Blocks);
                    if (shift.Found && !OnSite(map, shift, floor.Floor, out var where))
                    {
                        problems.Add($"{r.Name} ({r.Method}): it matches the engineer DWG, but puts this floor's risers {where}; it is not at Revit's 0,0 and is not used");
                        fa.Notes.Add($"{r.Name} not used: not at Revit's 0,0 (the risers land {where})");
                        continue;
                    }
                    refOptions.Add((r, map, l));
                    // model drawings before the xref folder; a better match replaces a weak one (e.g. an architectural
                    // background linked on the same level shares few blocks with the mechanical plan)
                    if (fa.Reference == null || (!fa.Shift.Found && (shift.Found || shift.Support > fa.Shift.Support)))
                    {
                        fa.Reference = r; fa.Map = map; fa.Shift = shift; loadedFor[fa] = l;
                    }
                    if (fa.Shift.Found) break;
                }
                if (fa.Reference == null) fa.Notes.AddRange(problems);
            }

            // ---- 2. floors check each other: the engineer stacks the floor plans in one layout, so the corner of every
            //      floor's plan region lands on the same Revit point. Compared in Revit coordinates, so a floor lined up
            //      with a linked drawing and one lined up with an xref-folder file still check each other.
            {
                var all = result.Floors.Where(f => f.Reference != null).ToList();
                var regions = all.ToDictionary(f => f, f => index.Floors.First(x => x.Floor == f.Floor));
                var strong = all.Where(f => f.Shift.Found).Select(f => f.Map.Apply(regions[f].MinX + f.Shift.Dx, regions[f].MinY + f.Shift.Dy)).ToList();
                (double X, double Y)? common = null;
                const double sameFt = 1.0 / 12;
                if (strong.Count >= 2)
                {
                    // the value most strong floors share (median is robust to one odd floor)
                    var mx = Median(strong.Select(s => s.X)); var my = Median(strong.Select(s => s.Y));
                    int agree = strong.Count(s => Math.Abs(s.X - mx) <= sameFt && Math.Abs(s.Y - my) <= sameFt);
                    if (agree >= 2 && agree * 2 > strong.Count) common = (mx, my);
                }

                foreach (var fa in all)
                {
                    var region = regions[fa];
                    var own = fa.Shift;
                    // the other floors' layout, tried on this floor's drawing; a floor with a weak match of its own tries
                    // each of its drawings (an architectural background shares few blocks, the ME xref more)
                    FloorShift Shared(PlanMap m, Loaded l)
                    {
                        var p = m.Invert(common.Value.X, common.Value.Y);
                        return DwgAlignment.Test(floorBlocks[fa], l.Blocks, p.X - region.MinX, p.Y - region.MinY);
                    }
                    FloorShift shared = null;
                    if (common.HasValue && own.Found) shared = Shared(fa.Map, loadedFor[fa]);
                    else if (common.HasValue)
                        foreach (var o in options[fa].OrderBy(o => o.R == fa.Reference ? 0 : 1))   // ties keep its own drawing
                        {
                            var s = Shared(o.Map, o.L);
                            if (shared == null || s.Support > shared.Support)
                            {
                                shared = s;
                                if (o.R != fa.Reference) { fa.Reference = o.R; fa.Map = o.Map; loadedFor[fa] = o.L; }
                            }
                        }
                    bool agrees = shared != null && own.Found && Math.Abs(own.Dx - shared.Dx) <= 1 && Math.Abs(own.Dy - shared.Dy) <= 1;

                    if (own.Found && (shared == null || agrees))
                        fa.Status = FloorAlignment.Confirmed;
                    else if (own.Found)
                    {
                        fa.Status = FloorAlignment.DiffersFromOthers;
                        fa.Notes.Add($"its own match is {Shift(own.Dx - shared.Dx, own.Dy - shared.Dy)} away from the other floors' layout");
                    }
                    else if (shared != null && shared.Support >= FloorShift.MinSupport && shared.Names >= 2)
                    {
                        fa.Shift = shared;
                        fa.Status = FloorAlignment.AgreesWithOthers;
                        fa.Notes.Add($"few blocks to compare on its own ({own.Support} agree); the other floors' layout fits {shared.Support}");
                    }
                    else
                    {
                        if (shared != null) fa.Shift = shared;       // shown and marked, not used for placing
                        fa.Status = FloorAlignment.NotConfirmed;
                        fa.Notes.Add(shared != null
                            ? $"only {shared.Support} block(s) fit the other floors' layout and its own match is weak ({own.Support} blocks, {own.Names} kind(s))"
                            : $"weak match ({own.Support} blocks of {own.Names} kind(s)) and no other floors to compare with");
                    }
                }
            }

            // ---- 2a. a floor with no drawing at Revit's position at all (no per-floor xref, nothing linked): risers stack,
            //      so its tagged risers drawn on the same tags of a lined-up floor next to it give its position. At least
            //      three different tags must agree within the anchor tolerance, twice as many as any other shift.
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var fa in result.Floors.Where(f => f.Reference == null && f.Shift == null).OrderBy(f => FloorKey.Order(f.Floor)))
                {
                    foreach (var n in Neighbours(result, fa).Where(n => n.Usable && n.Map != null && n.Shift != null))
                    {
                        var shift = StackShift(risers, fa.Floor, n, tagged: true) ?? StackShift(risers, fa.Floor, n, tagged: false);
                        if (shift == null) continue;
                        fa.Reference = n.Reference; fa.Map = n.Map; fa.Shift = shift; fa.Status = FloorAlignment.AgreesWithOthers;
                        fa.Notes.RemoveAll(x => x.StartsWith("no imported/linked DWG or xref"));
                        fa.Notes.Add($"no drawing of its own at Revit's position; lined up by {shift.Support} risers " +
                                     (shift.Tags.Count > 0 ? $"({string.Join(", ", shift.Tags)}) " : "(untagged) ") +
                                     $"drawn on the same risers of the {FloorKey.Describe(n.Floor)} (within {AlignmentResult.AnchorTolerance:0.#}\")");
                        grew = true;
                        break;
                    }
                }
            }

            // ---- 2b. risers run through floors: a floor whose tagged risers sit on the same tags of a lined-up
            //      floor next to it is confirmed by them (the roof plan often has too few blocks of its own).
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var fa in result.Floors.Where(f => f.Status == FloorAlignment.NotConfirmed && f.Shift != null))
                {
                    var best = Neighbours(result, fa).Where(n => n.Usable)
                        .Select(n => (N: n, Tags: StackedTags(risers, fa, n))).OrderByDescending(x => x.Tags.Count).FirstOrDefault();
                    if (best.N == null || best.Tags.Count < AlignmentResult.AnchorCount) continue;
                    fa.Status = FloorAlignment.AgreesWithOthers;
                    fa.Notes.Add($"{best.Tags.Count} risers ({string.Join(", ", best.Tags)}) sit within {AlignmentResult.AnchorTolerance:0.#}\" of the same risers on the {FloorKey.Describe(best.N.Floor)}");
                    changed = true;
                }
            }
            foreach (var fa in result.Floors.Where(f => f.Shift != null))
            {
                var below = Neighbours(result, fa).FirstOrDefault(n => FloorKey.Order(n.Floor) < FloorKey.Order(fa.Floor) && n.Shift != null);
                if (below != null) { fa.StackedTags = StackedTags(risers, fa, below); fa.StackedOn = below.Floor; }
            }

            // ---- 3. risers: on the reference drawing / on an existing opening
            var candidatesAnchors = new List<(AnchorRiser A, int Rank)>();
            foreach (var fa in result.Floors.Where(f => f.Map != null && f.Shift != null))
            {
                // a floor lined up by the risers of the floor next to it has no drawing of its own to check against
                var refRisers = loadedFor.TryGetValue(fa, out var own) ? own.Risers : new List<RiserSymbol>();
                var here = existing?.Where(e => fa.Level != null && e.Level == fa.Level).ToList() ?? new List<ExistingPoint>();
                foreach (var r in risers.Risers.Where(x => x.Floor == fa.Floor))
                {
                    var real = r.Symbols.Where(s => s.Block != DwgRiserReader.LineEnd).ToList();
                    var pos = fa.ToRevit(r.X, r.Y).Value;

                    double onExisting = here.Count == 0 ? double.MaxValue : here.Min(e => Math.Sqrt(Math.Pow(e.X - pos.X, 2) + Math.Pow(e.Y - pos.Y, 2))) * 12;
                    var nearest = here.OrderBy(e => Math.Pow(e.X - pos.X, 2) + Math.Pow(e.Y - pos.Y, 2)).FirstOrDefault();
                    if (onExisting <= riserTolerance) fa.RisersOnExisting++;

                    double onRef = double.MaxValue;
                    if (real.Count > 0)
                    {
                        fa.RisersChecked++;
                        onRef = real.Min(s => refRisers.Select(q => Math.Sqrt(Math.Pow(q.X - s.X - fa.Shift.Dx, 2) + Math.Pow(q.Y - s.Y - fa.Shift.Dy, 2)))
                                                        .DefaultIfEmpty(double.MaxValue).Min());
                        if (onRef <= AlignmentResult.AnchorTolerance) fa.RisersOnReference++;
                        if (onRef <= riserTolerance) fa.RisersNearReference++;
                    }
                    if (!fa.Usable) continue;

                    var a = new AnchorRiser { Floor = fa.Floor, Level = fa.Level, Tag = r.Tag, DwgX = r.X, DwgY = r.Y, X = pos.X, Y = pos.Y };
                    if (onExisting <= AlignmentResult.AnchorTolerance)
                    {
                        a.Distance = onExisting; a.Evidence = $"existing opening in the model ({nearest.Label})";
                        candidatesAnchors.Add((a, 0));
                    }
                    else if (onRef <= AlignmentResult.AnchorTolerance)
                    {
                        a.Distance = onRef; a.Evidence = $"riser symbol in {fa.Reference.Name}";
                        candidatesAnchors.Add((a, 1));
                    }
                }
            }

            // ---- 4. three anchors: strongest evidence first, tagged risers, different tags and floors, spread apart
            foreach (var c in candidatesAnchors.OrderBy(c => c.Rank).ThenBy(c => c.A.Tag == null ? 1 : 0).ThenBy(c => c.A.Distance))
            {
                if (result.Anchors.Count >= AlignmentResult.AnchorCount) break;
                if (result.Anchors.Any(x => x.Tag != null && x.Tag == c.A.Tag) || result.Anchors.Any(x => x.Floor == c.A.Floor)) continue;
                result.Anchors.Add(c.A);
            }
            foreach (var c in candidatesAnchors.OrderBy(c => c.Rank).ThenBy(c => c.A.Distance))     // fewer than 3 floors: allow repeats, still apart
            {
                if (result.Anchors.Count >= AlignmentResult.AnchorCount) break;
                if (result.Anchors.Contains(c.A) || result.Anchors.Any(x => x.Floor == c.A.Floor && Math.Sqrt(Math.Pow(x.X - c.A.X, 2) + Math.Pow(x.Y - c.A.Y, 2)) < 3)) continue;
                result.Anchors.Add(c.A);
            }

            // ---- 5. is the drawing each floor was lined up with really at Revit's position?
            ProveRevit(result, loadedFor, grids);

            // ---- messages
            int usable = result.Floors.Count(f => f.Usable);
            result.Messages.Add($"{usable} of {result.Floors.Count} floor(s) line up with a drawing placed at Revit's position.");
            foreach (var f in result.Floors.Where(f => !f.Usable))
                result.Messages.Add($"{FloorKey.Describe(f.Floor)}: {f.Status} — {string.Join("; ", f.Notes)}. Its risers will not be placed.");
            result.Messages.Add(result.DrawingsMatch
                ? $"Check risers: {result.Anchors.Count} land within {AlignmentResult.AnchorTolerance:0.#}\" of a known position (the engineer DWG matches those drawings)."
                : $"Check risers: only {result.Anchors.Count} land within {AlignmentResult.AnchorTolerance:0.#}\" of a known position (3 needed).");
            result.Messages.Add(result.RevitProven
                ? $"Revit position PROVEN: {result.RevitSummary}."
                : $"Revit position NOT proven: {result.RevitSummary}. Check the marks in the model before placing.");
            result.Messages.Add(result.Passed ? "Result: PASSED." : "Result: NOT PASSED — nothing will be placed.");
            if (!result.Passed)
                result.Diagnosis =
                    result.Floors.Count == 0 ? "No floor of the engineer's drawing could be matched to a level. Check the floor mapping above."
                    : references == null || references.Count == 0
                        ? "No reference drawing was found to line the engineer's drawing up against. Pick the office xref folder " +
                          "(the per-floor DWGs at Revit's 0,0) in the files above, or link them in the model."
                    : !result.DrawingsMatch
                        ? $"Only {result.Anchors.Count} riser(s) landed on the reference drawing, {AlignmentResult.AnchorCount} are needed. " +
                          "The engineer's drawing and the reference are probably different revisions of the building."
                        : "The reference drawing could not be proved to sit at Revit's position: its grids did not fall on the " +
                          "Revit grids. Check the grid DWG is the one for this building, or place one opening by hand as a reference.";
            return result;
        }

        /// <summary>Feet a floor's risers may lie outside the grid lines' extent (balconies, a cellar beyond the grid).</summary>
        private const double SiteMargin = 30;

        /// <summary>The building's extent in Revit feet: the Revit grids, else the office grid drawing (same 0,0 as the xrefs). Null when neither.</summary>
        private static (double MinX, double MinY, double MaxX, double MaxY)? SiteBox(GridInputs grids)
        {
            if (grids?.Revit != null && grids.Revit.Count >= 2)
                return (grids.Revit.Min(g => Math.Min(g.X1, g.X2)), grids.Revit.Min(g => Math.Min(g.Y1, g.Y2)),
                        grids.Revit.Max(g => Math.Max(g.X1, g.X2)), grids.Revit.Max(g => Math.Max(g.Y1, g.Y2)));
            if (grids?.DwgPath == null || !File.Exists(grids.DwgPath)) return null;
            try
            {
                var cad = DwgSheetIndex.Open(grids.DwgPath);
                var e = DwgAlignment.Extents(cad);
                var feet = PlanMap.FeetPerUnit(cad.Header.InsUnits.ToString());
                if (e == null || feet == null) return null;
                return (e.Value.MinX * feet.Value, e.Value.MinY * feet.Value, e.Value.MaxX * feet.Value, e.Value.MaxY * feet.Value);
            }
            catch { return null; }
        }

        /// <summary>
        /// What two drawings of a floor are matched on: the named blocks, plus (plumbing profile) every pipe circle named by
        /// its layer. A plumbing plan has few named blocks, but its circles are drawn the same in the per-floor xref.
        /// </summary>
        private static List<BlockPoint> Points(CadDocument cad, DwgProfile profile)
        {
            var list = DwgAlignment.Blocks(cad);
            if (!string.IsNullOrEmpty(profile?.RiserCircleLayers))
                list.AddRange(DwgRiserReader.Symbols(cad, profile).Where(s => s.Block == DwgRiserReader.PipeCircle)
                                            .Select(s => new BlockPoint { Name = "circle on " + s.Layer, X = s.X, Y = s.Y }));
            return list;
        }

        /// <summary>Reference drawing -> Revit. From the model: the instance placement with the units that make it cover the instance's extent in Revit.</summary>
        private static PlanMap MapFor(ReferenceDrawing r, Loaded l, List<string> notes)
        {
            if (!r.FromModel) return PlanMap.AtOrigin(l.FeetPerUnit.Value);
            var p = r.Placement;
            if (p == null) { notes.Add($"{r.Name}: no placement in the model"); return null; }

            var scales = new[] { l.FeetPerUnit.Value * r.ImportScale, l.FeetPerUnit.Value, r.ImportScale, 1.0 }.Distinct().ToList();
            PlanMap best = null; double bestFit = 0;
            foreach (var s in scales)
            {
                var m = new PlanMap { Scale = s * p.Scale, Cos = p.Cos, Sin = p.Sin, Ox = p.Ox, Oy = p.Oy };
                double fit = r.RevitBox == null || l.Extents == null ? 1 : Overlap(Box(m, l.Extents.Value), r.RevitBox);
                if (best == null || fit > bestFit) { best = m; bestFit = fit; }
            }
            if (r.RevitBox != null && bestFit < 0.5)
                notes.Add($"{r.Name}: the drawing's extent does not match where it sits in the model (overlap {bestFit:P0}); position not trusted");
            return r.RevitBox != null && bestFit < 0.5 ? null : best;
        }

        private static double[] Box(PlanMap m, (double MinX, double MinY, double MaxX, double MaxY) e)
        {
            var pts = new[] { m.Apply(e.MinX, e.MinY), m.Apply(e.MaxX, e.MinY), m.Apply(e.MinX, e.MaxY), m.Apply(e.MaxX, e.MaxY) };
            return new[] { pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X), pts.Max(q => q.Y) };
        }

        /// <summary>Intersection over union of two plan boxes.</summary>
        private static double Overlap(double[] a, double[] b)
        {
            double ix = Math.Max(0, Math.Min(a[2], b[2]) - Math.Max(a[0], b[0])), iy = Math.Max(0, Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]));
            double inter = ix * iy, union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter;
            return union <= 0 ? 0 : inter / union;
        }

        /// <summary>
        /// The grid DWG shares the xrefs' 0,0, so it is placed with each floor's map (same placement, its own units) and
        /// must fall on the Revit grids. Anchors on existing openings in the model prove the position too.
        /// </summary>
        private static void ProveRevit(AlignmentResult result, Dictionary<FloorAlignment, Loaded> loadedFor, GridInputs grids)
        {
            var usable = result.Floors.Where(f => f.Usable && f.Map != null).ToList();
            if (usable.Count == 0) { result.RevitSummary = "no floor lined up"; return; }

            List<GridLine> dwgGrids = null; double? gridFeet = null; string why = null;
            if (grids?.DwgPath == null || !File.Exists(grids.DwgPath)) why = "no grid-lines DWG found in the xref folders";
            else if (grids.Revit.Count == 0) why = "the model (and its Revit links) has no straight grids";
            else
            {
                try
                {
                    var cad = DwgSheetIndex.Open(grids.DwgPath);
                    dwgGrids = GridCheck.Read(cad);
                    gridFeet = PlanMap.FeetPerUnit(cad.Header.InsUnits.ToString());
                    if (dwgGrids.Count < 2) why = $"{Path.GetFileName(grids.DwgPath)} has no readable grid lines (line + bubble)";
                    else if (gridFeet == null) why = $"{Path.GetFileName(grids.DwgPath)} has unknown units";
                }
                catch (Exception ex) { why = $"{Path.GetFileName(grids.DwgPath)} could not be read ({ex.Message})"; }
            }

            if (why == null)
            {
                // one check per distinct placement (all xrefs at 0,0 share one; each linked DWG may have its own)
                foreach (var g in usable.GroupBy(f => $"{f.Map.Cos:0.######}|{f.Map.Sin:0.######}|{f.Map.Ox:0.####}|{f.Map.Oy:0.####}"))
                {
                    var m = g.First().Map;
                    var withDrawing = g.FirstOrDefault(loadedFor.ContainsKey);
                    if (withDrawing == null) continue;               // floors lined up by their neighbours' risers only: proven with that neighbour
                    double refFeet = loadedFor[withDrawing].FeetPerUnit.Value;
                    var gm = new PlanMap { Scale = m.Scale / refFeet * gridFeet.Value, Cos = m.Cos, Sin = m.Sin, Ox = m.Ox, Oy = m.Oy };
                    var check = GridCheck.Compare(dwgGrids, gm, grids.Revit, AlignmentResult.AnchorTolerance);
                    check.File = Path.GetFileName(grids.DwgPath);
                    result.Grids.Add(check);
                    foreach (var f in g) f.RevitProven = check.Passed;
                }
            }

            // three anchors on existing openings are direct proof in the model
            if (result.Anchors.Count(a => a.Evidence.StartsWith("existing opening")) >= AlignmentResult.AnchorCount)
                foreach (var f in usable) f.RevitProven = true;

            result.RevitProven = usable.All(f => f.RevitProven);
            if (result.RevitProven && result.Grids.Count > 0 && result.Grids.All(x => x.Passed))
                result.RevitSummary = $"{result.Grids[0].File} placed like the floor drawings: {string.Join("; ", result.Grids.Select(x => x.Summary))} ({grids.RevitSource})";
            else if (result.RevitProven)
                result.RevitSummary = "the check risers sit on openings already in the model";
            else if (why != null)
                result.RevitSummary = why + "; the floor drawings are assumed to be at Revit's position";
            else
                result.RevitSummary = $"{result.Grids[0].File} vs the Revit grids ({grids.RevitSource}): " + string.Join("; ", result.Grids.Where(x => !x.Passed).Select(x => x.Summary));
        }

        /// <summary>
        /// The shift that puts <paramref name="floor"/>'s tagged risers on the same risers of the lined-up floor
        /// <paramref name="n"/> (drawing units, in n's reference terms): every pair of same-kind symbols (same block, or
        /// pipe circles on the same layer) of two risers with one tag votes; the shift at least three tags agree on within
        /// the anchor tolerance, and twice as many as any other, wins. Null when the risers do not stack.
        /// </summary>
        /// <param name="tagged">false: untagged risers vote too, each one on its own (a roof plan often tags nothing); four must agree.</param>
        private static FloorShift StackShift(DwgRiserResult risers, string floor, FloorAlignment n, bool tagged)
        {
            bool Same(RiserSymbol a, RiserSymbol b) => a.Block == b.Block && (a.Block != DwgRiserReader.PipeCircle || a.Layer == b.Layer);
            var mine = risers.Risers.Where(r => r.Floor == floor && (!tagged || r.Tag != null)).ToList();
            var theirs = risers.Risers.Where(r => r.Floor == n.Floor && (!tagged || r.Tag != null)).ToList();
            var votes = new List<(double Dx, double Dy, string Tag)>();
            for (int i = 0; i < mine.Count; i++)
            {
                var r = mine[i];
                string key = r.Tag ?? $"#{i}";
                foreach (var q in theirs.Where(q => tagged ? q.Tag == r.Tag : (q.Tag == null || r.Tag == null || q.Tag == r.Tag)))
                    foreach (var s in r.Symbols.Where(DwgRiserReader.Drawn))
                        foreach (var u in q.Symbols.Where(DwgRiserReader.Drawn).Where(u => Same(s, u)))
                            votes.Add((u.X + n.Shift.Dx - s.X, u.Y + n.Shift.Dy - s.Y, key));
            }
            if (votes.Count == 0) return null;
            int need = tagged ? AlignmentResult.AnchorCount : AlignmentResult.AnchorCount + 1;

            double tol = AlignmentResult.AnchorTolerance;
            List<(double Dx, double Dy, string Tag)> Around((double Dx, double Dy) c) =>
                votes.Where(v => Math.Abs(v.Dx - c.Dx) <= tol && Math.Abs(v.Dy - c.Dy) <= tol).ToList();
            var scored = votes.Select(v => (C: (v.Dx, v.Dy), Tags: Around((v.Dx, v.Dy)).Select(x => x.Tag).Distinct().Count()))
                              .OrderByDescending(x => x.Tags).ToList();
            var best = scored[0];
            int runner = scored.Where(x => Math.Abs(x.C.Item1 - best.C.Item1) > 12 || Math.Abs(x.C.Item2 - best.C.Item2) > 12).Select(x => x.Tags).DefaultIfEmpty(0).Max();
            if (best.Tags < need || best.Tags < 2 * runner) return null;

            var fit = Around(best.C);
            return new FloorShift
            {
                Dx = fit.Average(v => v.Dx), Dy = fit.Average(v => v.Dy), Support = best.Tags, Names = best.Tags, RunnerUp = runner,
                Comparable = votes.Select(v => v.Tag).Distinct().Count(),
                Tags = fit.Select(v => v.Tag).Where(t => !t.StartsWith("#")).Distinct().OrderBy(t => t).ToList(),
                Residual = Math.Sqrt(fit.Average(v => Math.Pow(v.Dx - fit.Average(x => x.Dx), 2) + Math.Pow(v.Dy - fit.Average(x => x.Dy), 2)))
            };
        }

        /// <summary>The floors directly below and above (in drawing order).</summary>
        private static IEnumerable<FloorAlignment> Neighbours(AlignmentResult result, FloorAlignment fa)
        {
            var ordered = result.Floors.OrderBy(f => FloorKey.Order(f.Floor)).ToList();
            int i = ordered.IndexOf(fa);
            if (i > 0) yield return ordered[i - 1];
            if (i < ordered.Count - 1) yield return ordered[i + 1];
        }

        /// <summary>Tags found on both floors whose Revit positions are within the anchor tolerance.</summary>
        private static List<string> StackedTags(DwgRiserResult risers, FloorAlignment a, FloorAlignment b)
        {
            var there = risers.Risers.Where(r => r.Floor == b.Floor && r.Tag != null).Select(r => (r.Tag, P: b.ToRevit(r.X, r.Y).Value)).ToList();
            return risers.Risers.Where(r => r.Floor == a.Floor && r.Tag != null)
                .Where(r =>
                {
                    var p = a.ToRevit(r.X, r.Y).Value;
                    return there.Any(t => t.Tag == r.Tag && Math.Sqrt(Math.Pow(t.P.X - p.X, 2) + Math.Pow(t.P.Y - p.Y, 2)) * 12 <= AlignmentResult.AnchorTolerance);
                })
                .Select(r => r.Tag).Distinct().OrderBy(t => t).ToList();
        }

        private static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        private static string Shift(double dx, double dy) => $"{Math.Sqrt(dx * dx + dy * dy):0.#}\"";
    }
}
