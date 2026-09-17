using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SleevesOpenings.Placement
{
    /// <summary>
    /// Creates one family instance for an <see cref="OpeningSpec"/> at a point on a level,
    /// sizes it via the mapped parameters, labels it, and stamps <see cref="OpeningData"/>.
    /// Must be called inside an open Transaction.
    /// </summary>
    public class Placer
    {
        private readonly Document _doc;
        private readonly View _view;

        public Placer(Document doc, View view) { _doc = doc; _view = view; }

        public FamilyInstance Place(OpeningSpec spec, FamilySymbol symbol, FamilyMapEntry map, Level level, XYZ point)
        {
            if (!symbol.IsActive) symbol.Activate();

            var pt = new XYZ(point.X, point.Y, level.Elevation);
            var inst = CreateInstance(symbol, level, pt);

            if (spec.RotationRadians != 0)
                ElementTransformUtils.RotateElement(_doc, inst.Id, Line.CreateBound(pt, pt + XYZ.BasisZ), spec.RotationRadians);

            ApplySizes(inst, symbol, map, spec);

            if (!string.IsNullOrEmpty(spec.Label))
                SetText(inst, map.NameParam, spec.Label);

            var data = OpeningData.From(spec, level);
            data.WriteTo(inst);
            SharedParams.Write(inst, data);
            return inst;
        }

        /// <summary>Re-applies sizes and label to an existing instance and updates its stamp (used by Final Check fixes).</summary>
        public void Resize(FamilyInstance inst, FamilyMapEntry map, OpeningData data, double? width, double? length, double? diameter)
        {
            var spec = new OpeningSpec
            {
                System = (SystemKind)Enum.Parse(typeof(SystemKind), data.System), Width = width, Length = length, Diameter = diameter,
                Label = data.Label, Riser = data.Riser
            };
            ApplySizes(inst, inst.Symbol, map, spec);
            data.Width = width; data.Length = length; data.Diameter = diameter;
            data.WriteTo(inst);
            SharedParams.Write(inst, data);
        }

        public void Relabel(FamilyInstance inst, FamilyMapEntry map, OpeningData data, string label)
        {
            SetText(inst, map?.NameParam, label);
            data.Label = label;
            data.WriteTo(inst);
            SharedParams.Write(inst, data);
        }

        // ---- instance creation per family placement type ----

        private FamilyInstance CreateInstance(FamilySymbol symbol, Level level, XYZ pt)
        {
            switch (symbol.Family.FamilyPlacementType)
            {
                case FamilyPlacementType.OneLevelBased:
                    return _doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.NonStructural);

                case FamilyPlacementType.OneLevelBasedHosted:
                {
                    var floor = FindFloorAt(level, pt, out _);
                    if (floor == null) throw new InvalidOperationException("This family needs a floor host and no floor was found under the point.");
                    return _doc.Create.NewFamilyInstance(pt, symbol, floor, level, StructuralType.NonStructural);
                }

                case FamilyPlacementType.WorkPlaneBased:
                {
                    // Face-based: prefer the top face of the floor under the point, else a plane at the level.
                    var floor = FindFloorAt(level, pt, out Reference topFace);
                    if (floor != null && topFace != null)
                        return _doc.Create.NewFamilyInstance(topFace, pt, XYZ.BasisX, symbol);

                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation));
                    var sp = SketchPlane.Create(_doc, plane);
                    return _doc.Create.NewFamilyInstance(sp.GetPlaneReference(), pt, XYZ.BasisX, symbol);
                }

                case FamilyPlacementType.ViewBased:
                    return _doc.Create.NewFamilyInstance(pt, symbol, _view);

                case FamilyPlacementType.TwoLevelsBased:
                    return _doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.NonStructural);

                default:
                    throw new NotSupportedException($"Family '{symbol.Family.Name}' has placement type {symbol.Family.FamilyPlacementType}, which this tool cannot place by point.");
            }
        }

        /// <summary>Finds a floor on the level whose top face contains the XY point (returns the face reference too).</summary>
        public Floor FindFloorAt(Level level, XYZ pt, out Reference topFaceRef)
        {
            topFaceRef = null;
            var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Coarse };
            var floors = new FilteredElementCollector(_doc).OfClass(typeof(Floor)).Cast<Floor>()
                .Where(f => f.LevelId == level.Id || Math.Abs(ElevationOf(f) - level.Elevation) < 3.0);

            foreach (var floor in floors)
            {
                var bb = floor.get_BoundingBox(null);
                if (bb == null || pt.X < bb.Min.X || pt.X > bb.Max.X || pt.Y < bb.Min.Y || pt.Y > bb.Max.Y) continue;

                foreach (var solid in floor.get_Geometry(opt).OfType<Solid>().Where(s => s.Volume > 0))
                foreach (Face face in solid.Faces)
                {
                    if (!(face is PlanarFace pf) || pf.FaceNormal.Z < 0.9) continue;
                    var proj = face.Project(new XYZ(pt.X, pt.Y, pf.Origin.Z));
                    if (proj != null && proj.Distance < 0.01) { topFaceRef = face.Reference; return floor; }
                }
            }
            return null;
        }

        private double ElevationOf(Floor f)
        {
            var lvl = _doc.GetElement(f.LevelId) as Level;
            var off = f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
            return (lvl?.Elevation ?? 0) + off;
        }

        // ---- sizing ----

        /// <summary>
        /// Instance-driven size parameters are set directly. Type-driven ones get a type named after the
        /// size (found or duplicated) so other instances of the family are not resized.
        /// </summary>
        private void ApplySizes(FamilyInstance inst, FamilySymbol symbol, FamilyMapEntry map, OpeningSpec spec)
        {
            var typeDriven = new List<(string name, double inches)>();
            foreach (var (name, val) in SizePairs(map, spec))
            {
                if (string.IsNullOrEmpty(name)) continue;
                var ip = inst.LookupParameter(name);
                if (ip != null && !ip.IsReadOnly) SetLength(inst, name, val);
                else if (symbol.LookupParameter(name) != null) typeDriven.Add((name, val));
            }
            if (typeDriven.Count == 0) return;

            string typeName = spec.SizeText;
            var target = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Id == symbol.Family.Id && s.Name == typeName);
            if (target == null)
            {
                target = symbol.Duplicate(typeName) as FamilySymbol;
                foreach (var (name, val) in typeDriven) SetLength(target, name, val);
            }
            if (!target.IsActive) target.Activate();
            if (target.Id != inst.Symbol.Id) inst.Symbol = target;
        }

        private static IEnumerable<(string, double)> SizePairs(FamilyMapEntry map, OpeningSpec spec)
        {
            if (spec.Width.HasValue) yield return (map.WidthParam, spec.Width.Value);
            if (spec.Length.HasValue) yield return (map.LengthParam, spec.Length.Value);
            if (spec.Diameter.HasValue) yield return (map.DiameterParam, spec.Diameter.Value);
            if (spec.DownHeight.HasValue) yield return (map.DownHeightParam, spec.DownHeight.Value);
        }

        private static void SetLength(Element e, string paramName, double inches)
        {
            var p = e.LookupParameter(paramName);
            if (p == null || p.IsReadOnly) return;
            // A "Radius" parameter gets half the diameter.
            double value = paramName.IndexOf("radius", StringComparison.OrdinalIgnoreCase) >= 0 ? inches / 2 : inches;
            p.Set(Units.InchesToFeet(value));
        }

        private static void SetText(Element e, string paramName, string text)
        {
            var p = !string.IsNullOrEmpty(paramName) ? e.LookupParameter(paramName) : null;
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
                p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p != null && !p.IsReadOnly) p.Set(text);
        }
    }
}
