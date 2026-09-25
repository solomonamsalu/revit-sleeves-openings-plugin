using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>
    /// DWGs imported or linked in the model: where each one sits (transform, extent) and where its file is, so the
    /// engineer DWG can be lined up with it (<see cref="FloorAligner"/>). Read-only; call on Revit's thread.
    /// </summary>
    public static class CadReferences
    {
        public static List<ReferenceDrawing> Collect(Document doc, IEnumerable<string> searchFolders)
        {
            var list = new List<ReferenceDrawing>();
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(doc.PathName))
            {
                var dir = Path.GetDirectoryName(doc.PathName);
                roots.Add(dir);
                roots.Add(Path.GetDirectoryName(dir));
            }
            roots.AddRange(searchFolders ?? Enumerable.Empty<string>());

            foreach (var ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var type = doc.GetElement(ii.GetTypeId());
                string name = type?.Name ?? "";
                if (!name.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) continue;

                var r = new ReferenceDrawing { Name = name, Floor = ReferenceFiles.FloorOf(name), Method = ii.IsLinked ? ReferenceDrawing.Linked : ReferenceDrawing.Imported };

                View owner = ii.ViewSpecific ? doc.GetElement(ii.OwnerViewId) as View : null;
                var level = (owner as ViewPlan)?.GenLevel
                         ?? doc.GetElement(ii.get_Parameter(BuiltInParameter.IMPORT_BASE_LEVEL)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                r.Level = level?.Name;

                var t = ii.GetTotalTransform();
                double len = Math.Sqrt(t.BasisX.X * t.BasisX.X + t.BasisX.Y * t.BasisX.Y);
                if (len < 1e-9 || Math.Abs(t.BasisZ.Z) < 0.999)
                {
                    r.Problem = "placed tilted in 3D; not usable as a plan";
                    list.Add(r);
                    continue;
                }
                r.Placement = new PlanMap { Scale = len, Cos = t.BasisX.X / len, Sin = t.BasisX.Y / len, Ox = t.Origin.X, Oy = t.Origin.Y };
                double scale = type.get_Parameter(BuiltInParameter.IMPORT_SCALE)?.AsDouble() ?? 0;
                if (scale <= 0) scale = ii.get_Parameter(BuiltInParameter.IMPORT_INSTANCE_SCALE)?.AsDouble() ?? 0;
                r.ImportScale = scale > 0 ? scale : 1;

                var box = ii.get_BoundingBox(owner);
                if (box != null) r.RevitBox = new[] { box.Min.X, box.Min.Y, box.Max.X, box.Max.Y };

                r.Path = LinkedPath(type);
                if (r.Path == null || !File.Exists(r.Path))
                {
                    var found = ReferenceFiles.Locate(name, roots);
                    if (found == null) r.Problem = ii.IsLinked ? $"linked file not found ({r.Path ?? "no path"})" : "imported (not linked); a file of this name was not found next to the model";
                    r.Path = found;
                }
                list.Add(r);
            }
            return list;
        }

        private static string LinkedPath(Element type)
        {
            try
            {
                if (type == null || !type.IsExternalFileReference()) return null;
                var path = type.GetExternalFileReference().GetAbsolutePath();
                return path == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch { return null; }
        }
    }
}
