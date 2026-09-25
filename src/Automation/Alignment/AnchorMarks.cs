using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>
    /// Draws a cross in a circle (model lines) on each anchor riser's level, so the alignment can be checked by eye
    /// against the drawings in Revit. Temporary: one undo removes them. Call inside a transaction.
    /// </summary>
    public static class AnchorMarks
    {
        private const double Radius = 0.5;       // feet (6")
        private const double Arm = 1.0;          // feet: the cross reaches past the circle

        public static List<ElementId> Draw(Document doc, IEnumerable<AnchorRiser> anchors, LevelMap levels) =>
            DrawAt(doc, anchors.Select(a => (a.Level, a.X, a.Y)), levels);

        /// <summary>The same mark at any spots (level name, Revit X/Y in feet): the review items of an Auto Run.</summary>
        public static List<ElementId> DrawAt(Document doc, IEnumerable<(string Level, double X, double Y)> spots, LevelMap levels)
        {
            var ids = new List<ElementId>();
            foreach (var group in spots.Where(a => a.Level != null).GroupBy(a => a.Level))
            {
                var level = levels.All.FirstOrDefault(l => l.Name == group.Key)?.Level;
                if (level == null) continue;
                var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.ProjectElevation)));
                foreach (var a in group)
                {
                    var c = new XYZ(a.X, a.Y, level.ProjectElevation);
                    ids.Add(doc.Create.NewModelCurve(Line.CreateBound(c + new XYZ(-Arm, -Arm, 0), c + new XYZ(Arm, Arm, 0)), sp).Id);
                    ids.Add(doc.Create.NewModelCurve(Line.CreateBound(c + new XYZ(-Arm, Arm, 0), c + new XYZ(Arm, -Arm, 0)), sp).Id);
                    ids.Add(doc.Create.NewModelCurve(Arc.Create(c, Radius, 0, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY), sp).Id);
                }
            }
            return ids;
        }
    }
}
