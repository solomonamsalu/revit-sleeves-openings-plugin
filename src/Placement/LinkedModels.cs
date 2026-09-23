using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;

namespace SleevesOpenings.Placement
{
    /// <summary>
    /// The host document plus every loaded Revit link (per clearances.includeLinkedModels / linkedModelMatch),
    /// each with the transform that brings its geometry into host coordinates. Structure (columns, beams,
    /// shear walls) usually lives in a linked model, so the guards and the auditor read all of them.
    /// </summary>
    public class LinkedModels
    {
        public class Source
        {
            public Document Doc;
            public Transform Transform;     // link -> host; Identity for the host itself
            public string Name;             // "" for the host, link name otherwise
            public bool IsHost => Transform.IsIdentity && Name.Length == 0;

            /// <summary>"123" for the host, "123 (24 Skillman Str)" for a link.</summary>
            public string Describe(ElementId id) => IsHost ? id.ToString() : $"{id} ({Name})";
        }

        public List<Source> Sources { get; } = new List<Source>();

        public LinkedModels(Document host, RuleSet rules)
        {
            Sources.Add(new Source { Doc = host, Transform = Transform.Identity, Name = "" });
            if (rules?.Clearances == null || !rules.Clearances.IncludeLinkedModels) return;

            var rx = string.IsNullOrEmpty(rules.Clearances.LinkedModelMatch) ? null
                   : new Regex(rules.Clearances.LinkedModelMatch, RegexOptions.IgnoreCase);
            foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document ldoc;
                try { ldoc = link.GetLinkDocument(); } catch { continue; }
                if (ldoc == null) continue;                              // unloaded link
                string name = ldoc.Title;
                if (rx != null && !rx.IsMatch(name) && !rx.IsMatch(link.Name)) continue;
                Sources.Add(new Source { Doc = ldoc, Transform = link.GetTotalTransform(), Name = name });
            }
        }

        /// <summary>Elements of a category from every source, with their bounding box in host coordinates.</summary>
        public IEnumerable<(Element Element, BoundingBoxXYZ Box, Source Source)> Collect(BuiltInCategory cat)
        {
            foreach (var s in Sources)
            {
                foreach (var e in new FilteredElementCollector(s.Doc).OfCategory(cat).WhereElementIsNotElementType())
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    yield return (e, TransformBox(bb, s.Transform), s);
                }
            }
        }

        /// <summary>Axis-aligned box in host coordinates that contains the transformed box.</summary>
        public static BoundingBoxXYZ TransformBox(BoundingBoxXYZ bb, Transform t)
        {
            if (t.IsIdentity) return bb;
            var corners = new[]
            {
                new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z),
            }.Select(c => bb.Transform.IsIdentity ? t.OfPoint(c) : t.OfPoint(bb.Transform.OfPoint(c))).ToList();
            return new BoundingBoxXYZ
            {
                Min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)),
                Max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z))
            };
        }
    }
}
