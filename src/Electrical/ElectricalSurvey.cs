using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Electrical
{
    /// <summary>
    /// The architect's drawings read and tied to this model's levels: apartments per level and the electrical room
    /// as a Revit point, so the electrical riser starts from what the drawings say instead of a typed guess.
    /// </summary>
    public class ElectricalSurvey
    {
        public ElectricalReadResult Read = new ElectricalReadResult();
        /// <summary>Revit level name -> the architectural floor it was matched to.</summary>
        public Dictionary<string, FloorApartments> ByLevel = new Dictionary<string, FloorApartments>();
        /// <summary>Where the electrical room label sits, in model coordinates; null when the drawings name none.</summary>
        public XYZ Start;
        public string StartFloor;
        public List<string> Unmatched = new List<string>();

        public bool Any => Read.Any && ByLevel.Count > 0;

        public FloorApartments For(Level level) =>
            level != null && ByLevel.TryGetValue(level.Name, out var f) ? f : null;

        /// <summary>Reads the architectural xrefs next to the model and matches their floors to the model's levels.</summary>
        public static ElectricalSurvey Run(Document doc, RuleSet rules, ProjectState state, LevelMap levels, string folderOverride = null)
        {
            var survey = new ElectricalSurvey();
            var profile = rules.DwgProfile ?? new DwgProfile();

            // the folder picked for this project wins over the one found next to the model
            string folder = folderOverride ?? state.ElectricalXrefFolder;
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
                folder = ElectricalReader.FindFolder(ModelFiles.HomePath(doc), profile);
            survey.Read = ElectricalReader.Read(folder, profile);
            if (survey.Read.Floors.Count == 0) return survey;

            var matches = FloorMatcher.Run(survey.Read.Floors.Select(f => f.Floor), levels, state.Automation ?? new AutomationInputs());
            foreach (var m in matches)
            {
                var floor = survey.Read.Floors.FirstOrDefault(f => f.Floor == m.Floor);
                if (floor == null) continue;
                if (m.Level == null) { if (floor.Apartments > 0) survey.Unmatched.Add(FloorKey.Describe(m.Floor) + " (" + floor.Apartments + " apartments)"); continue; }
                survey.ByLevel[m.Level.Name] = floor;
            }

            var start = survey.Read.Start;
            if (start != null)
            {
                var at = survey.Read.StartPoint;
                survey.Start = new XYZ(at[0], at[1], 0);
                survey.StartFloor = FloorKey.Describe(start.Floor);
            }
            if (survey.Unmatched.Count > 0)
                survey.Read.Warnings.Add("No level matched " + string.Join(", ", survey.Unmatched) + "; type those counts.");
            return survey;
        }

        /// <summary>One line per floor for the summary dialog and the log.</summary>
        public string Describe() =>
            string.Join("\n", Read.Floors.Where(f => f.Apartments > 0).OrderBy(f => FloorKey.Order(f.Floor))
                .Select(f => FloorKey.Describe(f.Floor) + ": " + f.Apartments + " apartments" +
                             (f.NumbersText.Length > 0 ? " (" + f.NumbersText + ")" : "")));
    }
}
