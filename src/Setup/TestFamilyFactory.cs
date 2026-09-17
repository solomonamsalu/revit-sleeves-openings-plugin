using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SleevesOpenings.Placement;

namespace SleevesOpenings.Setup
{
    /// <summary>
    /// Builds minimal parametric Generic Model families for testing when the office families are
    /// not available: a rectangular opening (Width x Length, instance) and a round sleeve (Diameter, instance).
    /// Geometry is a 1' tall solid box / cylinder centred on the level so it is visible in plan.
    /// </summary>
    public static class TestFamilyFactory
    {
        /// <summary>Last step attempted; included in error messages.</summary>
        public static string Step = "";

        public const string OpeningFamilyName = "SO Test Opening";
        public const string SleeveFamilyName = "SO Test Sleeve";

        public static string TemplatePath(Application app)
        {
            var dir = app.FamilyTemplatePath;
            foreach (var candidate in new[]
            {
                Path.Combine(dir, "Generic Model.rft"),
                Path.Combine(dir, "English-Imperial", "Generic Model.rft"),
                Path.Combine(dir, "English", "Generic Model.rft"),
                Path.Combine(dir, "Metric Generic Model.rft"),
            })
                if (File.Exists(candidate)) return candidate;

            var found = Directory.EnumerateFiles(dir, "*Generic Model.rft", SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) throw new FileNotFoundException("Generic Model.rft not found under " + dir);
            return found;
        }

        /// <summary>Output folder for generated RFAs (add-in Families folder, else %APPDATA%).</summary>
        public static string OutputDir()
        {
            var primary = Path.Combine(Rules.RuleLoader.AddinDir, "Families");
            try { Directory.CreateDirectory(primary); File.WriteAllText(Path.Combine(primary, ".write-test"), ""); File.Delete(Path.Combine(primary, ".write-test")); return primary; }
            catch
            {
                var alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SleevesOpenings", "Families");
                Directory.CreateDirectory(alt);
                return alt;
            }
        }

        /// <summary>Creates both families, saves them, loads them into <paramref name="project"/> (call with NO open transaction on the project).</summary>
        public static (Family opening, Family sleeve, string dir) CreateAndLoad(Application app, Document project)
        {
            string template = TemplatePath(app);
            string dir = OutputDir();

            var opening = BuildRectOpening(app, template, Path.Combine(dir, OpeningFamilyName + ".rfa"), project);
            var sleeve = BuildRoundSleeve(app, template, Path.Combine(dir, SleeveFamilyName + ".rfa"), project);
            return (opening, sleeve, dir);
        }

        /// <summary>Family mapping that matches the generated families.</summary>
        public static Dictionary<string, FamilyMapEntry> Mapping() => new Dictionary<string, FamilyMapEntry>
        {
            [FamilyRole.RegularOpening] = new FamilyMapEntry { FamilyName = OpeningFamilyName, WidthParam = "Width", LengthParam = "Length", NameParam = "Comments" },
            [FamilyRole.PipeReferenceOpening] = new FamilyMapEntry { FamilyName = OpeningFamilyName, WidthParam = "Width", LengthParam = "Length", NameParam = "Comments" },
            [FamilyRole.ElectricalOpening] = new FamilyMapEntry { FamilyName = OpeningFamilyName, WidthParam = "Width", LengthParam = "Length", NameParam = "Comments" },
            [FamilyRole.RoundSleeve] = new FamilyMapEntry { FamilyName = SleeveFamilyName, DiameterParam = "Diameter", NameParam = "Comments" },
        };

        // ------------------------------------------------------------------ rectangular opening

        private static Family BuildRectOpening(Application app, string template, string path, Document project)
        {
            const double w = 2.0, l = 2.0, h = 1.0;   // feet: 24" x 24", 12" tall
            var fam = app.NewFamilyDocument(template);
            try
            {
                using (var t = new Transaction(fam, "Build test opening"))
                {
                    t.Start();
                    Step = "opening: parameters";
                    var view = PlanView(fam);
                    var fm = fam.FamilyManager;
                    if (fm.Types.Size == 0) fm.NewType("Default");

                    var pWidth = fm.AddParameter("Width", GroupTypeId.Geometry, SpecTypeId.Length, true);
                    var pLength = fm.AddParameter("Length", GroupTypeId.Geometry, SpecTypeId.Length, true);
                    fm.Set(pWidth, w);
                    fm.Set(pLength, l);

                    // Reference planes at the four edges + dimensions labelled with the parameters
                    Step = "opening: reference planes";
                    var centerLR = RefPlane(fam, "Center (Left/Right)");
                    var centerFB = RefPlane(fam, "Center (Front/Back)");
                    var left = NewRefPlane(fam, view, new XYZ(-w / 2, -5, 0), new XYZ(-w / 2, 5, 0), "Left");
                    var right = NewRefPlane(fam, view, new XYZ(w / 2, -5, 0), new XYZ(w / 2, 5, 0), "Right");
                    var front = NewRefPlane(fam, view, new XYZ(-5, -l / 2, 0), new XYZ(5, -l / 2, 0), "Front");
                    var back = NewRefPlane(fam, view, new XYZ(-5, l / 2, 0), new XYZ(5, l / 2, 0), "Back");
                    fam.Regenerate();   // new reference planes only get geometric references after regen

                    Step = "dimensions";
                    Label(fam, view, Line.CreateBound(new XYZ(-w / 2, 6, 0), new XYZ(w / 2, 6, 0)), pWidth, left, right);
                    Label(fam, view, Line.CreateBound(new XYZ(6, -l / 2, 0), new XYZ(6, l / 2, 0)), pLength, front, back);
                    Equal(fam, view, Line.CreateBound(new XYZ(-w / 2, 7, 0), new XYZ(w / 2, 7, 0)), left, centerLR, right);
                    Equal(fam, view, Line.CreateBound(new XYZ(7, -l / 2, 0), new XYZ(7, l / 2, 0)), front, centerFB, back);

                    // Box extrusion whose edges are locked to the reference planes
                    Step = "opening: extrusion";
                    var profile = new CurveArrArray();
                    var loop = new CurveArray();
                    var a = new XYZ(-w / 2, -l / 2, 0); var b = new XYZ(w / 2, -l / 2, 0);
                    var c = new XYZ(w / 2, l / 2, 0); var d = new XYZ(-w / 2, l / 2, 0);
                    loop.Append(Line.CreateBound(a, b)); loop.Append(Line.CreateBound(b, c));
                    loop.Append(Line.CreateBound(c, d)); loop.Append(Line.CreateBound(d, a));
                    profile.Append(loop);

                    var sp = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    var ext = fam.FamilyCreate.NewExtrusion(true, profile, sp, h);
                    ext.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM).Set(-h / 2);
                    ext.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM).Set(h / 2);
                    fam.Regenerate();

                    Step = "opening: alignments";
                    foreach (Curve curve in ext.Sketch.Profile.get_Item(0))
                    {
                        var mid = curve.Evaluate(0.5, true);
                        var dirv = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                        ReferencePlane target = Math.Abs(dirv.X) < 0.01
                            ? (mid.X < 0 ? left : right)      // vertical edge
                            : (mid.Y < 0 ? front : back);     // horizontal edge
                        fam.FamilyCreate.NewAlignment(view, target.GetReference(), curve.Reference);
                    }
                    t.Commit();
                }
                return SaveAndLoad(fam, path, project);
            }
            finally { fam.Close(false); }
        }

        // ------------------------------------------------------------------ round sleeve

        private static Family BuildRoundSleeve(Application app, string template, string path, Document project)
        {
            const double dia = 0.5, h = 1.0;   // feet: 6" diameter, 12" tall
            var fam = app.NewFamilyDocument(template);
            try
            {
                using (var t = new Transaction(fam, "Build test sleeve"))
                {
                    t.Start();
                    Step = "sleeve: parameters";
                    var view = PlanView(fam);
                    var fm = fam.FamilyManager;
                    if (fm.Types.Size == 0) fm.NewType("Default");

                    var pDia = fm.AddParameter("Diameter", GroupTypeId.Geometry, SpecTypeId.Length, true);
                    var pRad = fm.AddParameter("Radius", GroupTypeId.Geometry, SpecTypeId.Length, true);
                    fm.Set(pDia, dia);
                    fm.SetFormula(pRad, "Diameter / 2");

                    Step = "sleeve: extrusion";
                    var profile = new CurveArrArray();
                    var loop = new CurveArray();
                    loop.Append(Arc.Create(XYZ.Zero, dia / 2, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                    loop.Append(Arc.Create(XYZ.Zero, dia / 2, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    profile.Append(loop);

                    var sp = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    var ext = fam.FamilyCreate.NewExtrusion(true, profile, sp, h);
                    ext.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM).Set(-h / 2);
                    ext.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM).Set(h / 2);
                    fam.Regenerate();

                    // Radial dimension on the first arc, labelled with Radius (= Diameter / 2)
                    Step = "sleeve: radial dimension";
                    var arc = ext.Sketch.Profile.get_Item(0).get_Item(0);
                    var radialType = new FilteredElementCollector(fam).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                        .FirstOrDefault(dt => dt.StyleType == DimensionStyleType.Radial);
                    var dim = fam.FamilyCreate.NewRadialDimension(view, arc.Reference, new XYZ(dia * 0.5, dia * 0.5, 0), radialType);
                    dim.FamilyLabel = pRad;

                    // Keep the centre on the origin: lock the arc centre to the centre reference planes is not
                    // expressible directly, so the family relies on nothing else moving the sketch.
                    t.Commit();
                }
                return SaveAndLoad(fam, path, project);
            }
            finally { fam.Close(false); }
        }

        // ------------------------------------------------------------------ helpers

        private static ViewPlan PlanView(Document fam) =>
            new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
            ?? throw new InvalidOperationException("Family template has no floor plan view.");

        private static ReferencePlane RefPlane(Document fam, string name) =>
            new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .FirstOrDefault(r => r.Name == name)
            ?? throw new InvalidOperationException($"Template lacks reference plane '{name}'.");

        private static ReferencePlane NewRefPlane(Document fam, View view, XYZ a, XYZ b, string name)
        {
            var rp = fam.FamilyCreate.NewReferencePlane(a, b, XYZ.BasisZ, view);
            rp.Name = name;
            return rp;
        }

        private static void Label(Document fam, View view, Line line, FamilyParameter p, params ReferencePlane[] planes)
        {
            var refs = new ReferenceArray();
            foreach (var rp in planes) refs.Append(rp.GetReference());
            var dim = fam.FamilyCreate.NewDimension(view, line, refs);
            dim.FamilyLabel = p;
        }

        private static void Equal(Document fam, View view, Line line, params ReferencePlane[] planes)
        {
            var refs = new ReferenceArray();
            foreach (var rp in planes) refs.Append(rp.GetReference());
            var dim = fam.FamilyCreate.NewDimension(view, line, refs);
            dim.AreSegmentsEqual = true;
        }

        private static Family SaveAndLoad(Document fam, string path, Document project)
        {
            Step = "save " + Path.GetFileName(path);
            fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
            Step = "load " + Path.GetFileName(path);
            var loaded = fam.LoadFamily(project, new OverwriteLoadOptions());
            return loaded ?? ProjectSetup.FindFamily(project, Path.GetFileNameWithoutExtension(path));
        }

        private class OverwriteLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }
    }
}
