using System.Collections.Generic;

namespace SleevesOpenings.Automation
{
    /// <summary>What to do with sleeves/openings already in the model when Auto Run places new ones.</summary>
    public static class ExistingPolicy
    {
        public const string KeepAndAddMissing = "keepAndAddMissing";
        public const string Update = "update";
    }

    /// <summary>The engineer drawings picked for one discipline.</summary>
    public class DisciplineFiles
    {
        public string Pdf { get; set; }
        public string Dwg { get; set; }
        /// <summary>Folder of per-floor drawings at Revit's 0,0 (office xrefs), used to line the DWG up with Revit.</summary>
        public string Xrefs { get; set; }
        /// <summary>The office's Sleeves &amp; Openings set (PDF) to compare the result with; "" = none wanted (not searched for again).</summary>
        public string Reference { get; set; }
    }

    /// <summary>Auto Run inputs, saved in the project (ProjectState) so the next run fills them in.</summary>
    public class AutomationInputs
    {
        public const string Mechanical = "Mechanical";
        public const string Plumbing = "Plumbing";
        /// <summary>Sprinkler / standpipe sleeves: run from the PL model (the office has no FP model) or a model matching rules.json sprinkler.modelMatch.</summary>
        public const string Sprinkler = "Sprinkler";

        /// <summary>Saved as a floor's level when the user chose not to use that drawing floor.</summary>
        public const string NotUsed = "(not used)";

        /// <summary>Discipline ("Mechanical", later "Plumbing", "Sprinkler") -> files.</summary>
        public Dictionary<string, DisciplineFiles> Files { get; set; } = new Dictionary<string, DisciplineFiles>();

        /// <summary>Drawing floor (FloorKey: CELLAR, F1.., ROOF, BULKHEAD) -> Revit level name, where the user overrode the automatic match.</summary>
        public Dictionary<string, string> FloorLevels { get; set; } = new Dictionary<string, string>();

        public string Existing { get; set; } = ExistingPolicy.KeepAndAddMissing;

        /// <summary>The discipline of the last run in this model (PL model: plumbing or sprinkler): the window opens on its files.</summary>
        public string LastDiscipline { get; set; }

        /// <summary>HV / PL / FP when the user chose what this model is (its name and contents did not say, or the drawings disagreed); null otherwise.</summary>
        public string ModelKind { get; set; }

        public DisciplineFiles For(string discipline)
        {
            if (!Files.TryGetValue(discipline, out var f)) Files[discipline] = f = new DisciplineFiles();
            return f;
        }
    }
}
