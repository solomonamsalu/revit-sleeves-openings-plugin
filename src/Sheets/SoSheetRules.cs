using System.Collections.Generic;
using Newtonsoft.Json;

namespace SleevesOpenings.Sheets
{
    /// <summary>
    /// rules.json "soSheets": the office's Sleeves &amp; Openings set (SL101…) made from a pattern sheet, and its PDF.
    /// Names: {ord} = 1st / 2nd…, {Floor} = "1st Floor" / "Roof", {FLOOR} = "1ST FLOOR" / "ROOF", {kind} = floor / roof /
    /// bulkhead / cellar, {index} = the sheet's place in the set (1st, 2nd… as words: "8th" for the roof on 24 Skillman).
    /// </summary>
    public class SoSheetRules
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        /// <summary>Sheet number prefix and the first number: SL101, SL102…</summary>
        [JsonProperty("numberPrefix")] public string NumberPrefix { get; set; } = "SL";
        [JsonProperty("firstNumber")] public int FirstNumber { get; set; } = 101;
        /// <summary>The sheet copied for every floor (its number); null = the lowest-numbered sheet with numberPrefix that holds a Sleeves view.</summary>
        [JsonProperty("patternSheet")] public string PatternSheet { get; set; }
        /// <summary>
        /// The S&amp;O template used when the model has no S&amp;O sheet yet: a template's name (S&amp;O Set window, Templates tab) or
        /// a path to an .rvt holding one S&amp;O sheet; null = the add-in's own "SO Template".
        /// </summary>
        [JsonProperty("template")] public string Template { get; set; }
        [JsonProperty("sheetName")] public string SheetName { get; set; } = "{Floor} Sleeves & Openings";
        [JsonProperty("scheduleName")] public string ScheduleName { get; set; } = "SL- {index} {kind}";
        /// <summary>The floor label text in each Sleeves view (the pattern's text of type floorLabelType, rewritten).</summary>
        [JsonProperty("floorLabel")] public string FloorLabel { get; set; } = "{FLOOR}";
        [JsonProperty("floorLabelType")] public string FloorLabelType { get; set; } = "Floor Notes";
        /// <summary>Other text types in the pattern plan copied unchanged to every floor (street names).</summary>
        [JsonProperty("copyTextTypes", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> CopyTextTypes { get; set; } = new List<string> { "street name" };
        /// <summary>Sheet Issue Date written on new sheets (.NET date format).</summary>
        [JsonProperty("issueDateFormat")] public string IssueDateFormat { get; set; } = "MM/dd/yy";

        /// <summary>Revisions the add-in writes as notes (the title block's NOTES TO ARCH, ENG, &amp; G.C. table) carry this in "Issued By".</summary>
        [JsonProperty("notesTag")] public string NotesTag { get; set; } = "S&O notes";

        /// <summary>Update the sheets at the end of Auto Run (PL / FP model) and print the PDF.</summary>
        [JsonProperty("afterAutoRun")] public bool AfterAutoRun { get; set; } = true;
        [JsonProperty("pdfAfterAutoRun")] public bool PdfAfterAutoRun { get; set; } = true;
        /// <summary>PDF name: {project} = the model's name without its discipline code; {date} = today in pdfDateFormat.</summary>
        [JsonProperty("pdfName")] public string PdfName { get; set; } = "{project} Sleeves & Openings {date}";
        [JsonProperty("pdfDateFormat")] public string PdfDateFormat { get; set; } = "M-d-yy";
        /// <summary>The folder the PDF goes in: a folder with this name near the model or the drawings Auto Run read.</summary>
        [JsonProperty("pdfFolder")] public string PdfFolder { get; set; } = "^Structural$";
        /// <summary>
        /// PDF paper: "auto" = the smallest standard paper the title block fits on, printed at 100% and centred (a 35½" × 23¼"
        /// title block → ARCH D 36" × 24", as the office's sets); "sheet" = Revit's "use sheet size" (the page is cut to the
        /// title block's lines); or a size by name: ARCH_D, ARCH_E1, ANSI_D, ISO_A1…
        /// </summary>
        [JsonProperty("paperSize")] public string PaperSize { get; set; } = "auto";
        /// <summary>Existing S&amp;O sheets whose name differs from sheetName are renamed (off: the office's own names are kept).</summary>
        [JsonProperty("renameExisting")] public bool RenameExisting { get; set; }
        /// <summary>Removed from the model name to get {project}: the discipline code and anything after it ("24 Skillman PL_detached" → "24 Skillman").</summary>
        [JsonProperty("projectNameStrip")] public string ProjectNameStrip { get; set; } = @"[\s_-]+(PL|HV|FP|SP|MEP|HVAC|MECH\w*|PLUMB\w*|SPRINK\w*)([\s_-].*)?$|[\s_-]*detached$";
    }

    /// <summary>A note in the sheets' notes table: the text, its date (empty = the day it is first written) and the sheets it goes on (empty = every S&amp;O sheet).</summary>
    public class SoNote
    {
        public string Text { get; set; }
        public string Date { get; set; }
        public List<string> Sheets { get; set; } = new List<string>();
    }

    /// <summary>
    /// This project's own S&amp;O settings (the S&amp;O Set window's Naming and PDF tabs), saved in the model. Null = rules.json.
    /// </summary>
    public class SoProjectSettings
    {
        public string NumberPrefix { get; set; }
        public int? FirstNumber { get; set; }
        public string SheetName { get; set; }
        public string ScheduleName { get; set; }
        public string FloorLabel { get; set; }
        public string PdfName { get; set; }
        public string PaperSize { get; set; }
        public bool? RenameExisting { get; set; }
        /// <summary>This project's S&amp;O template (a name from the Templates tab); null = the office default.</summary>
        public string Template { get; set; }

        /// <summary>rules.json soSheets with this project's settings on top.</summary>
        public static SoSheetRules Apply(SoSheetRules office, SoProjectSettings mine)
        {
            var cfg = JsonConvert.DeserializeObject<SoSheetRules>(JsonConvert.SerializeObject(office ?? new SoSheetRules()));
            if (mine == null) return cfg;
            if (!string.IsNullOrWhiteSpace(mine.NumberPrefix)) cfg.NumberPrefix = mine.NumberPrefix.Trim();
            if (mine.FirstNumber.HasValue) cfg.FirstNumber = mine.FirstNumber.Value;
            if (!string.IsNullOrWhiteSpace(mine.SheetName)) cfg.SheetName = mine.SheetName;
            if (!string.IsNullOrWhiteSpace(mine.ScheduleName)) cfg.ScheduleName = mine.ScheduleName;
            if (!string.IsNullOrWhiteSpace(mine.FloorLabel)) cfg.FloorLabel = mine.FloorLabel;
            if (!string.IsNullOrWhiteSpace(mine.PdfName)) cfg.PdfName = mine.PdfName;
            if (!string.IsNullOrWhiteSpace(mine.PaperSize)) cfg.PaperSize = mine.PaperSize;
            if (mine.RenameExisting.HasValue) cfg.RenameExisting = mine.RenameExisting.Value;
            if (!string.IsNullOrWhiteSpace(mine.Template)) cfg.Template = mine.Template.Trim();
            return cfg;
        }
    }

    /// <summary>
    /// One field of the sheets' title block or of the project, as the S&amp;O Set window shows it. Not saved: the model holds
    /// the values (new sheets copy them from the pattern sheet).
    /// </summary>
    public class SoField
    {
        public const string Project = "Project", Sheet = "Sheet", TitleBlock = "Title block", TitleBlockType = "Title block type";

        public string Owner { get; set; }
        public string Name { get; set; }
        /// <summary>The built-in parameter (Revit's own fields), else INVALID and found by name.</summary>
        public int BuiltIn { get; set; } = -1;
        public bool YesNo { get; set; }
        public bool Integer { get; set; }
        public string Value { get; set; }
        /// <summary>The value read from the model, to tell what the drafter changed.</summary>
        public string Original { get; set; }
        /// <summary>Written to every S&amp;O sheet (ticked by itself when the value is changed).</summary>
        public bool Write { get; set; }
    }
}
