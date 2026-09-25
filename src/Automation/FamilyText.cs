using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// The office opening families draw their name (Riser Number) with an opaque text type: a white box over the
    /// opening's lines. Makes every text/label type inside those families transparent and reloads them. Outside any
    /// transaction (the family is edited in its own document). Returns the families changed.
    /// </summary>
    public static class FamilyText
    {
        private const int Transparent = 1;       // BuiltInParameter.TEXT_BACKGROUND: 0 = opaque, 1 = transparent

        public static List<string> MakeTransparent(Document doc, IEnumerable<string> familyNames)
        {
            var changed = new List<string>();
            var names = new HashSet<string>(familyNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
            // names first: a reloaded family's Family object is no longer valid
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => names.Contains(f.Name) && f.IsEditable && !f.IsInPlace).Select(f => (f.Id, f.Name)).ToList();
            foreach (var (id, name) in families)
            {
                Document fam = null;
                try
                {
                    if (!(doc.GetElement(id) is Family family)) continue;
                    fam = doc.EditFamily(family);
                    var types = new FilteredElementCollector(fam).OfClass(typeof(ElementType)).Cast<ElementType>()
                        .Select(t => (Type: t, P: t.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)))
                        .Where(x => x.P != null && !x.P.IsReadOnly).ToList();
                    var opaque = types.Where(x => x.P.AsInteger() != Transparent).ToList();
                    App.Log($"AutoRun: '{name}' text types: " + string.Join(", ", types.Select(x => $"{x.Type.Name}={(x.P.AsInteger() == Transparent ? "transparent" : "opaque")}")));
                    if (opaque.Count == 0) continue;
                    using (var t = new Transaction(fam, "Transparent text"))
                    {
                        t.Start();
                        foreach (var x in opaque) x.P.Set(Transparent);
                        t.Commit();
                    }
                    fam.LoadFamily(doc, new KeepValues());
                    changed.Add(name);
                }
                catch (Exception ex) { App.Log($"AutoRun: text of '{name}' not made transparent: {ex}"); }
                finally { try { fam?.Close(false); } catch { } }
            }
            return changed;
        }

        /// <summary>Reload over the project's copy without touching instance values.</summary>
        private class KeepValues : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family; overwriteParameterValues = false; return true;
            }
        }
    }
}
