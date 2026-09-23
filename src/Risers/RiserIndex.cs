using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Risers
{
    /// <summary>One placed opening/sleeve the add-in knows about (has an OpeningData stamp).</summary>
    public class OpeningRecord
    {
        public FamilyInstance Instance;
        public OpeningData Data;
        public Level Level;
        public XYZ Point;
        public string Riser => Data.Riser;
        public string SizeText => Data.Diameter.HasValue
            ? Units.FormatInches(Data.Diameter.Value)
            : $"{Units.FormatInches(Data.Width ?? 0)} x {Units.FormatInches(Data.Length ?? 0)}";
    }

    /// <summary>All openings sharing a riser id, ordered by level elevation.</summary>
    public class RiserRecord
    {
        public string Riser;
        public string System;
        public List<OpeningRecord> Openings = new List<OpeningRecord>();

        public OpeningRecord Top => Openings.OrderByDescending(o => o.Level.Elevation).First();
        public OpeningRecord Bottom => Openings.OrderBy(o => o.Level.Elevation).First();
        public IEnumerable<string> Sizes => Openings.Select(o => o.SizeText).Distinct();

        /// <summary>Openings whose XY differs from the one above by more than 1" (manual: avoid offsets).</summary>
        public int OffsetCount
        {
            get
            {
                var ordered = Openings.OrderBy(o => o.Level.Elevation).ToList();
                int n = 0;
                for (int i = 1; i < ordered.Count; i++)
                    if (Dist(ordered[i].Point, ordered[i - 1].Point) > Units.InchesToFeet(1)) n++;
                return n;
            }
        }

        /// <summary>Levels between top and bottom (in the given map) that have no opening for this riser.</summary>
        public List<Level> Gaps(LevelMap levels)
        {
            double lo = Bottom.Level.Elevation, hi = Top.Level.Elevation;
            var present = new HashSet<ElementId>(Openings.Select(o => o.Level.Id));
            return levels.All.Where(l => l.Elevation > lo && l.Elevation < hi && !present.Contains(l.Level.Id))
                             .Select(l => l.Level).ToList();
        }

        public static double Dist(XYZ a, XYZ b) => new XYZ(a.X - b.X, a.Y - b.Y, 0).GetLength();
    }

    /// <summary>Scans the model for stamped openings and groups them by riser id.</summary>
    public static class RiserIndex
    {
        public static List<OpeningRecord> AllOpenings(Document doc)
        {
            var list = new List<OpeningRecord>();
            foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                var data = OpeningData.Read(inst);
                if (data == null) continue;
                var level = LevelOf(doc, inst);
                var pt = (inst.Location as LocationPoint)?.Point;
                if (level == null || pt == null) continue;
                list.Add(new OpeningRecord { Instance = inst, Data = data, Level = level, Point = pt });
            }
            return list;
        }

        public static List<RiserRecord> Risers(Document doc) => Group(AllOpenings(doc));

        public static List<RiserRecord> Group(IEnumerable<OpeningRecord> openings) =>
            openings.Where(o => !string.IsNullOrEmpty(o.Riser))
                .GroupBy(o => o.Riser)
                .Select(g => new RiserRecord { Riser = g.Key, System = g.First().Data.System, Openings = g.ToList() })
                .OrderBy(r => r.System).ThenBy(r => r.Riser)
                .ToList();

        public static Level LevelOf(Document doc, FamilyInstance inst)
        {
            if (inst.LevelId != ElementId.InvalidElementId) return doc.GetElement(inst.LevelId) as Level;
            var p = inst.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                 ?? inst.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)
                 ?? inst.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            return p != null ? doc.GetElement(p.AsElementId()) as Level : null;
        }

        /// <summary>Next free riser id for a system, e.g. "GC-3". Existing ids are scanned from the model.</summary>
        public static string NextRiserId(Document doc, string system)
        {
            string prefix = Prefix(system);
            var used = new HashSet<string>(AllOpenings(doc).Select(o => o.Riser).Where(r => r != null));
            for (int i = 1; ; i++)
                if (!used.Contains($"{prefix}-{i}")) return $"{prefix}-{i}";
        }

        public static string Prefix(string system)
        {
            switch (system)
            {
                case "Exhaust": return "EX";
                case "ERV": return "ERV";
                case "GarbageChute": return "GC";
                case "DryerExhaust": return "DE";
                case "MotorizedDamper": return "MD";
                case "Refrigeration": return "REF";
                case "Electrical": return "EL";
                case "Storm": return "ST";
                case "AreaDrain": return "AD";
                case "Condensate": return "CD";
                case "Standpipe": return "SP";
                case "Bathtub": return "TUB";
                case "Toilet": return "WC";
                // adopted systems outside the manual (Sanitary, Vent, ColdWater...): first three letters
                default: return string.IsNullOrEmpty(system) ? "R" : system.Substring(0, Math.Min(3, system.Length)).ToUpperInvariant();
            }
        }
    }
}
