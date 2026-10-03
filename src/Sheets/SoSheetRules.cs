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
        /// <summary>Removed from the model name to get {project}: the discipline code and anything after it ("24 Skillman PL_detached" → "24 Skillman").</summary>
        [JsonProperty("projectNameStrip")] public string ProjectNameStrip { get; set; } = @"[\s_-]+(PL|HV|FP|SP|MEP|HVAC|MECH\w*|PLUMB\w*|SPRINK\w*)([\s_-].*)?$|[\s_-]*detached$";
    }

    /// <summary>A note in the sheets' notes table: the text and the sheets it goes on (empty = every S&amp;O sheet).</summary>
    public class SoNote
    {
        public string Text { get; set; }
        public List<string> Sheets { get; set; } = new List<string>();
    }
}
