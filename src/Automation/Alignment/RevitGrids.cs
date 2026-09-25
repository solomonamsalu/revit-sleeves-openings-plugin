using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SleevesOpenings.Automation.Alignment
{
    /// <summary>Straight grids of the model (feet), or of a loaded Revit link when the model has none (usually the structural model).</summary>
    public static class RevitGrids
    {
        public static GridInputs Collect(Document doc)
        {
            var input = new GridInputs { Revit = Read(doc, Transform.Identity), RevitSource = "grids in this model" };
            if (input.Revit.Count > 0) return input;

            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = link.GetLinkDocument();
                if (linked == null) continue;
                var grids = Read(linked, link.GetTotalTransform());
                if (grids.Count == 0) continue;
                input.Revit = grids;
                input.RevitSource = $"grids in the link {link.Name}";
                break;
            }
            return input;
        }

        private static List<GridLine> Read(Document doc, Transform t)
        {
            var list = new List<GridLine>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(g.Curve is Line line)) continue;
                var a = t.OfPoint(line.GetEndPoint(0)); var b = t.OfPoint(line.GetEndPoint(1));
                list.Add(new GridLine { Name = g.Name, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Source = doc.Title });
            }
            return list;
        }
    }
}
