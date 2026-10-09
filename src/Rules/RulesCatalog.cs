using System.Collections.Generic;

namespace SleevesOpenings.Rules
{
    /// <summary>How a rule's value is typed in the Edit Rules window, and how it is written to rules.json.</summary>
    public enum RuleKind
    {
        Bool,
        Inches,          // length in inches, required
        Number,          // plain number, required
        Int,             // whole number, required
        OptionalNumber,  // number, empty = not set (null / left out)
        Text,            // text, may be empty
        OptionalText,    // text, empty = null
        Regex,           // pattern (case-insensitive) checked before saving, may be empty
        Choice,          // one of Choices (Value null = "not set")
        Words,           // list of texts, typed comma-separated
        Lines,           // list of texts, one per line
        Numbers          // list of numbers, typed comma-separated
    }

    public class RuleChoice
    {
        public string Value, Label;
        public RuleChoice(string value, string label) { Value = value; Label = label; }
        public override string ToString() => Label;
    }

    /// <summary>One value of rules.json, by its dotted path ("systems.exhaust.clearanceEachSide").</summary>
    public class RuleField
    {
        public string Path, Label, Help;
        public RuleKind Kind;
        public List<RuleChoice> Choices;
        /// <summary>Choice: other values may be typed in (paper sizes).</summary>
        public bool AllowOther;
    }

    public class RuleColumn
    {
        public string Key, Header, Help;
        public RuleKind Kind = RuleKind.Text;
        public bool Required;
        public List<RuleChoice> Choices;
        public int Width = 110;
    }

    public enum TableShape
    {
        Keyed,   // { "S": { ...columns }, ... }: the first column is the key
        List,    // [ { ...columns }, ... ]: rows in order
        Map      // { "KX": "KITCHEN EXHAUST", ... }: key + one value column
    }

    /// <summary>A table of rules users can add rows to, edit, delete and (Ordered) reorder.</summary>
    public class RuleTable
    {
        public string Path, Title, Help, KeyHeader = "Code";
        public TableShape Shape;
        public bool Ordered;
        public RuleKind KeyKind = RuleKind.Text;
        public List<RuleColumn> Columns = new List<RuleColumn>();
    }

    public class RuleHeading
    {
        public string Text, Help;
    }

    /// <summary>One tab of the Edit Rules window.</summary>
    public class RulePage
    {
        public string Title, Intro;
        public bool Advanced;
        /// <summary>RuleHeading, RuleField or RuleTable, in order.</summary>
        public List<object> Items = new List<object>();
        /// <summary>
        /// Sections shown with every value they hold, labelled from their names and comments (the technical ones).
        /// Values a curated page already shows are left out.
        /// </summary>
        public List<string> AutoPaths = new List<string>();
        /// <summary>Tables inside the AutoPaths sections (lists of objects are found by themselves).</summary>
        public List<RuleTable> AutoTables = new List<RuleTable>();

        public RulePage H(string text, string help = null) { Items.Add(new RuleHeading { Text = text, Help = help }); return this; }

        public RulePage F(string path, string label, RuleKind kind, string help = null, params RuleChoice[] choices)
        {
            Items.Add(new RuleField { Path = path, Label = label, Kind = kind, Help = help, Choices = choices.Length > 0 ? new List<RuleChoice>(choices) : null });
            return this;
        }

        public RulePage T(RuleTable table) { Items.Add(table); return this; }

        /// <summary>The last field's choice list also takes typed-in values.</summary>
        public RulePage OrOther() { ((RuleField)Items[Items.Count - 1]).AllowOther = true; return this; }
    }

    /// <summary>
    /// The rules the Edit Rules window shows: plain-English pages for what the office changes, then Advanced pages for
    /// how the drawings are read. Every path is a rules.json path; a new rule needs one line here to be editable.
    /// </summary>
    public static class RulesCatalog
    {
        private static RuleChoice C(string value, string label) => new RuleChoice(value, label);

        private static RuleColumn Col(string key, string header, RuleKind kind = RuleKind.Text, bool required = false, int width = 110, string help = null, params RuleChoice[] choices) =>
            new RuleColumn { Key = key, Header = header, Kind = kind, Required = required, Width = width, Help = help, Choices = choices.Length > 0 ? new List<RuleChoice>(choices) : null };

        /// <summary>Systems the add-in knows (sizes in the manual, MPI sleeve toggles, the S&amp;O legend).</summary>
        public static readonly string[] KnownSystems =
        {
            "Exhaust", "ERV", "GarbageChute", "DryerExhaust", "MotorizedDamper", "Refrigeration", "Electrical",
            "Storm", "AreaDrain", "Condensate", "Standpipe", "Bathtub", "Toilet",
            "Sanitary", "Vent", "Gas", "ColdWater", "HotWater", "HotWaterReturn"
        };

        public static List<RulePage> Pages()
        {
            var pages = new List<RulePage>();
            const RuleKind In = RuleKind.Inches, B = RuleKind.Bool;

            pages.Add(new RulePage { Title = "Duct openings", Intro = "Sizes of the HVAC openings (Standards Manual). All lengths in inches." }
                .H("Exhaust (KX, TX, GX…)")
                .F("systems.exhaust.clearanceEachSide", "Clearance each side of the duct", In, "Opening = duct + this on every side.")
                .F("systems.exhaust.roofIncreaseTotal", "Extra on the roof (total)", In, "Roof opening = floor opening + this in each direction.")
                .F("systems.exhaust.firstNumberEastWest", "First duct number runs east-west", B, "16X8: the 16 runs left-right on the plan. Off = turn the opening the way the engineer drew the duct.")
                .F("systems.exhaust.floorPlanWinsOverRiserDiagram", "Floor plan wins over riser diagram", B, "When the two give different sizes, the floor plan's size is used.")
                .H("Garbage chute")
                .F("systems.garbageChute.fixedWidth", "Width", In)
                .F("systems.garbageChute.fixedLength", "Length", In)
                .F("systems.garbageChute.roofClearance", "Extra on the roof", In)
                .F("systems.garbageChute.allowOffset", "May be moved off the drawn position", B)
                .H("Dryer exhaust (DE)")
                .F("systems.dryerExhaust.radius", "Round opening radius", In, "2 = a 4\" round.")
                .F("systems.dryerExhaust.roofOpeningWidth", "Roof opening width", In)
                .F("systems.dryerExhaust.roofOpeningLength", "Roof opening length", In)
                .F("systems.dryerExhaust.minSpacing", "Minimum spacing between dryers", In, "Closer than this = a dryer shaft.")
                .F("systems.dryerExhaust.shaftOpening", "One opening around a dryer shaft", B, "Off = every dryer shaft goes to review.")
                .H("Motorized damper (MD)")
                .F("systems.motorizedDamper.clearanceEachSide", "Clearance each side", In)
                .H("Refrigeration")
                .F("systems.refrigeration.downHeight", "Down Height", In)
                .F("systems.refrigeration.linesPerIndoorUnitSplit", "Lines per indoor unit (split)", RuleKind.Int)
                .F("systems.refrigeration.inchesPerLine", "Inches per line", In)
                .F("systems.refrigeration.minLength", "Minimum opening length", In)
                .F("systems.refrigeration.roofExtraWidth", "Extra width on the roof", In)
                .F("systems.refrigeration.roofExtraLength", "Extra length on the roof", In)
                .H("Electrical (ELECTRIC)")
                .F("systems.electrical.conduitSpacingCenterToCenter", "Conduit spacing, centre to centre", In)
                .F("systems.electrical.circleDiameter", "Conduit circle diameter", In)
                .F("systems.electrical.extraCirclesForRoof", "Extra circles for the roof", RuleKind.Int)
                .F("systems.electrical.roofSleeveDiameter", "Roof sleeve diameter", In)
                .F("systems.electrical.openingMargin", "Margin around the circles", In));

            pages.Add(new RulePage { Title = "Pipe sleeves", Intro = "Sizes of the round sleeves the manual sets (storm, condensate, standpipe, bathtub). Inches." }
                .H("Storm and area drains")
                .F("systems.storm.sleeveOverPipe", "Added to the pipe for the sleeve", In)
                .F("systems.storm.areaDrainDiameter", "Area drain sleeve diameter", In)
                .F("systems.storm.areaDrainCount", "Sleeves per area drain", RuleKind.Int, "Primary + overflow = 2.")
                .F("systems.storm.areaDrainSpacingCenterToCenter", "Area drain sleeves, centre to centre", In)
                .F("systems.storm.detailDeckEdgeOffset", "Distance from the deck edge (detail)", In)
                .H("Condensate")
                .F("systems.condensate.sleeveDiameter", "Sleeve diameter", In)
                .H("Standpipe")
                .F("systems.standpipe.sleeveOverPipe", "Added to the pipe for the sleeve", In)
                .F("systems.standpipe.minCenterToWall", "Minimum centre to wall", In)
                .F("systems.standpipe.minCenterToCenter", "Minimum centre to centre", In)
                .H("Bathtub")
                .T(new RuleTable
                {
                    Path = "systems.bathtub.options", Title = "Bathtub sleeve options", Shape = TableShape.Keyed, KeyHeader = "Option name",
                    Help = "Each option is a way to sleeve a tub drain. Add a row for a new option.",
                    Columns = { Col("count", "Sleeves", RuleKind.Int, true, 80), Col("diameter", "Diameter (in)", In, true, 100) }
                })
                .F("systems.bathtub.default", "Default option", RuleKind.OptionalText, "An option name from the table above. Empty = asked on each project."));

            pages.Add(new RulePage { Title = "Clearances", Intro = "Distances an opening keeps from structure and from other openings. Inches." }
                .F("clearances.minFromColumn", "From a column", In)
                .F("clearances.minFromWallEdge", "From a wall edge", In)
                .F("clearances.roofMinFromWallOrCurb", "Roof: from a wall or curb", In)
                .F("clearances.roofMinBetweenOpenings", "Roof: between openings", In)
                .F("clearances.ervSpacingExact", "Roof: ERV supply to exhaust (edge to edge)", In)
                .F("clearances.ervFloorCenterToCenter", "Floor: ERV supply to exhaust (centre to centre)", In)
                .F("clearances.midRoomDistance", "Farther than this from walls = middle of a room", In, "0 turns the check off.")
                .F("clearances.includeLinkedModels", "Read structure from linked models", B, "The structural model is usually a link.")
                .F("clearances.linkedModelMatch", "Only links whose name matches", RuleKind.Regex, "Empty = every link.")
                .H("View range of the Sleeves views")
                .F("viewRange.topLevelAboveOffset", "Top: offset from the level above", In)
                .F("viewRange.bottomAssociatedLevelOffset", "Bottom: offset from the level", In));

            pages.Add(new RulePage { Title = "Names and views", Intro = "What the openings are called and the per-floor Sleeves views Auto Run makes." }
                .H("Opening names")
                .F("naming.electrical", "Electrical opening name", RuleKind.Text)
                .F("naming.dryerExhaust", "Dryer exhaust name", RuleKind.Text)
                .F("naming.exhaustPattern", "Exhaust name", RuleKind.Text, "{riser} = the riser tag (KX1).")
                .F("naming.damperPattern", "Duct opening with a damper", RuleKind.Text, "{riser} = the duct's tag, {damper} = the damper code: ERV-SA-FSD.")
                .H("Sleeves views")
                .F("sleeveViews.nameSuffix", "View name after the level name", RuleKind.Text, "' Sleeves' makes '3rd Floor Sleeves'.")
                .F("sleeveViews.subDiscipline", "Project Browser folder (Sub-Discipline)", RuleKind.Text)
                .F("sleeveViews.tag", "Tag every opening", B, "Off = the opening shows its own name (Riser Number).")
                .F("sleeveViews.transparentLabels", "Transparent name labels", B));

            pages.Add(new RulePage { Title = "Auto Run", Intro = "What Auto Run decides by itself and what it leaves for review." }
                .H("After placing")
                .F("automation.finalCheck", "Run Final Check on what was placed", B)
                .F("automation.autoFix", "Fix size / name / riser id automatically", B, "Never moves, copies or deletes.")
                .F("automation.riserDiagram", "Read the riser diagram", B, "Reports tagged risers the floor plans do not show.")
                .H("Decisions made by rule instead of review")
                .F("automation.decisions.trustUpFromBelow", "Trust UP from the floor below", B, "A riser only the floor below shows (labelled UP) is placed straight above it.")
                .F("automation.decisions.dryersIntoHost", "Dryers landing on an opening enlarge it", B)
                .F("automation.decisions.roofDryerGrid", "Roof dryer shafts as a grid of 6x6", B)
                .F("automation.decisions.roofDryerPerRow", "Roof dryers per row", RuleKind.Int)
                .F("automation.decisions.mergeOverlaps", "Merge overlapping openings", B, "One opening around both, named with both tags.")
                .F("automation.decisions.avoidStructure", "Reshape / move openings off structure", B, "Beam, shear wall, column or wall edge: reshape to the same area, then move.")
                .F("automation.decisions.maxAspect", "Reshape up to (long : short)", RuleKind.Number, "4 = up to 4:1, keeping the duct area.")
                .F("automation.decisions.maxShift", "Move up to", In)
                .F("automation.decisions.shiftStep", "Move in steps of", In)
                .H("PDF only (no DWG)")
                .F("pdfOnly.enabled", "Allow Auto Run from the PDF alone", B)
                .F("pdfOnly.placeWhenPassed", "Place sleeves read from the PDF", B, "Off = every PDF-only sleeve goes to review.")
                .F("pdfOnly.defaultScale", "Scale when the sheet prints none (1 : x)", RuleKind.Number, "48 = 1/4\" = 1'-0\"."));

            pages.Add(new RulePage { Title = "HV tags", Intro = "How a tag's meaning (from the engineer's ABBREVIATIONS / SYMBOLS) decides what opening it gets." }
                .T(new RuleTable
                {
                    Path = "legend.officeFixed", Title = "Tags the office always uses (PDFs do not define them)", Shape = TableShape.Map, KeyHeader = "Tag",
                    Columns = { Col("value", "Meaning", RuleKind.Text, true, 300) }
                })
                .T(new RuleTable
                {
                    Path = "legend.categories", Title = "Meaning → opening", Shape = TableShape.List, Ordered = true,
                    Help = "Tried top to bottom against the tag's meaning; the first row that matches wins, so put specific rows above general ones. " +
                           "Fill ONE of: System (gets an opening), No opening, Shaft name, or Review note.",
                    Columns =
                    {
                        Col("match", "Meaning contains (pattern)", RuleKind.Regex, true, 260, "Words separated by | (DRYER|LINT). Case is ignored."),
                        Col("system", "System", RuleKind.Text, false, 110, "Exhaust, ERV, DryerExhaust, GarbageChute, MotorizedDamper…"),
                        Col("ignore", "No opening", RuleKind.Bool, false, 75),
                        Col("labelOnHost", "Add tag to host duct", RuleKind.Bool, false, 90, "No opening; the tag goes on the duct opening's name (ERV-FSD)."),
                        Col("shaft", "Shaft name", RuleKind.OptionalText, false, 120, "The ducts here share one opening with this name."),
                        Col("label", "Opening name", RuleKind.OptionalText, false, 90),
                        Col("diameter", "Ø if none given (in)", RuleKind.OptionalNumber, false, 80),
                        Col("check", "Place, but flag with", RuleKind.OptionalText, false, 200),
                        Col("review", "Review note (not placed)", RuleKind.OptionalText, false, 200)
                    }
                }));

            pages.Add(new RulePage { Title = "Plumbing", Intro = "Auto Run in the PL model: what the letters on the plumbing plans mean and how their sleeves are sized." }
                .T(new RuleTable
                {
                    Path = "plumbing.services", Title = "Services written on the plans", Shape = TableShape.Keyed, KeyHeader = "Letters",
                    Help = "'S UP & DN' next to a bubble: S is the service. Add a row for a new service.",
                    Columns =
                    {
                        Col("system", "System", RuleKind.Text, true, 110, "Picks the MPI sleeve's size toggle (Families page)."),
                        Col("sleeve", "Gets a sleeve", RuleKind.Bool, false, 80),
                        Col("pipe", "Pipe size (in)", RuleKind.Number, true, 90, "Plans carry no sizes: this size is used."),
                        Col("layer", "DWG layer (pattern)", RuleKind.Regex, false, 200, "Part of the layer name of this service's circles.")
                    }
                })
                .T(new RuleTable
                {
                    Path = "plumbing.fixtures", Title = "Fixtures written on the plans", Shape = TableShape.Keyed, KeyHeader = "Code",
                    Columns =
                    {
                        Col("name", "Fixture", RuleKind.Text, true, 140),
                        Col("pipe", "Drain pipe (in)", RuleKind.Number, true, 90),
                        Col("system", "System", RuleKind.OptionalText, false, 100, "Empty = Sanitary."),
                        Col("count", "Sleeves", RuleKind.OptionalNumber, false, 70, "Empty = 1."),
                        Col("spacing", "Spacing c-c (in)", RuleKind.OptionalNumber, false, 90),
                        Col("vent", "Vent pipe (in)", RuleKind.OptionalNumber, false, 80, "Empty = no vent sleeve. A vent sleeve beside the drain, in the wall behind."),
                        Col("water", "Hot/cold pipe (in)", RuleKind.OptionalNumber, false, 90, "Empty = none. A sink with no stack nearby gets its own row: vent, hot, drain, cold."),
                        Col("stackReach", "Stack serves it within (in)", RuleKind.OptionalNumber, false, 110, "Empty = off. A stack sleeve this close serves the fixture: no sleeve of its own.")
                    }
                })
                .H("Sleeve size")
                .F("plumbing.sleeveOverPipe", "Added to the pipe for the sleeve", In, "Then rounded up to the next size below.")
                .F("plumbing.sleeveSizes", "Sleeve sizes available", RuleKind.Numbers, "Comma-separated, inches.")
                .F("plumbing.sleeveGap", "Clear gap between sleeves of one group", In)
                .F("plumbing.stackSnap", "Line up a riser's sleeves closer than", In,
                    "A sleeve this close to the same pipe's sleeve on the floors next to it is moved in line with them; farther = an offset the plans draw. 0 = off.")
                .F("plumbing.namePattern", "Sleeve name", RuleKind.Text, "{service} = S, V, ST…; {riser} = the bubble's tag (P3).")
                .F("plumbing.skipLowestSlab", "No sleeves in the lowest slab (on grade)", B)
                .F("plumbing.fixtureSleeves", "Fixture sleeves", RuleKind.Choice, null,
                    C("model", "Place at the Revit fixture"), C("review", "List for review"), C("off", "Ignore"))
                .F("plumbing.modelFixtures", "Sleeve modelled fixtures the plans do not label", B)
                .F("plumbing.fixtureGaps", "Fixture on the plans, not in the model", RuleKind.Choice, null,
                    C("place", "Place: floor above/below, else the drawing"), C("report", "Report, place nothing"))
                .F("plumbing.dwgFixtures", "Read fixtures from the architect's DWGs in the model", B)
                .F("plumbing.dwgFixtureLayers", "DWG fixture layers", RuleKind.Regex, "Layers with the toilets, tubs and sinks (Scan DWG Fixtures lists the layers).")
                .F("plumbing.dwgWallLayers", "DWG wall layers", RuleKind.Regex)
                .F("plumbing.dwgNewLayers", "DWG new-work layers", RuleKind.Regex, "Alteration drawings: the layer the new layout is drawn on (\"TO ADD\"); its curves and short lines are fixtures, its long lines walls.")
                .F("plumbing.dwgRemoveLayers", "DWG removal layers", RuleKind.Regex, "Alteration drawings: fixtures drawn or inserted on these layers (\"TO REMOVE\") get no sleeve.")
                .F("plumbing.dwgSlabEdgeLayers", "DWG slab edge layers", RuleKind.Regex, "A sink with no wall line behind it but the slab edge close behind: against the exterior wall, not an island.")
                .F("plumbing.dwgShaftLayers", "DWG shaft / chase layers", RuleKind.Regex, "Extract Stacks: a stack goes in the chase drawn on these layers next to its fixtures.")
                .H("Words on the plans")
                .F("plumbing.upWords", "Means up (slab above)", RuleKind.Words)
                .F("plumbing.downWords", "Means down (this slab)", RuleKind.Words)
                .F("plumbing.throughWords", "Means through (this slab)", RuleKind.Words)
                .F("plumbing.unmarked", "No UP / DN written", RuleKind.Choice, null, C(null, "Report, place nothing"), C("DN", "Ends on this floor (DN)"))
                .F("plumbing.modelMatch", "Plumbing model name contains (pattern)", RuleKind.Regex));

            pages.Add(new RulePage { Title = "Sprinkler", Intro = "Sprinkler / standpipe sleeves, run from the PL model. The engineer labels each pipe in words with its size." }
                .F("sprinkler.enabled", "Offer sprinkler / standpipe in Auto Run", B)
                .T(new RuleTable
                {
                    Path = "sprinkler.services", Title = "Pipe labels", Shape = TableShape.Keyed, KeyHeader = "Code",
                    Help = "'3\" SPRINKLER RISER UP/DN': the label is matched against each row; the size written in it is used.",
                    Columns =
                    {
                        Col("match", "Label contains (pattern)", RuleKind.Regex, true, 260),
                        Col("system", "System", RuleKind.Text, true, 100),
                        Col("sleeve", "Gets a sleeve", RuleKind.Bool, false, 80),
                        Col("pipe", "Pipe if none written (in)", RuleKind.Number, true, 110),
                        Col("note", "Why no sleeve (report)", RuleKind.OptionalText, false, 220)
                    }
                })
                .F("sprinkler.sleeveOverPipe", "Added to the pipe for the sleeve", In)
                .F("sprinkler.sleeveSizes", "Sleeve sizes available", RuleKind.Numbers, "Comma-separated, inches.")
                .F("sprinkler.namePattern", "Sleeve name", RuleKind.Text)
                .F("sprinkler.skipLowestSlab", "No sleeves in the lowest slab (on grade)", B)
                .F("sprinkler.unmarked", "No UP / DN written", RuleKind.Choice, null, C(null, "Report, place nothing"), C("DN", "Ends on this floor (DN)"))
                .F("sprinkler.modelMatch", "Sprinkler model name contains (pattern)", RuleKind.Regex));

            pages.Add(new RulePage { Title = "Fixtures to verify", Intro = "Manual p.2 'Always verify': fixtures found in the model by name and checked against the sleeves in Final Check." }
                .H("Shower / floor drain")
                .F("fixtures.showerDrain.match", "Family / type name contains", RuleKind.Regex)
                .F("fixtures.showerDrain.pipeSize", "Pipe size", In)
                .F("fixtures.showerDrain.sleeveRadius", "Sleeve within", In)
                .H("Toilet")
                .F("fixtures.toilet.match", "Family / type name contains", RuleKind.Regex)
                .F("fixtures.toilet.wallHungMatch", "Wall-hung if name contains", RuleKind.Regex)
                .F("fixtures.toilet.pipeSize", "Pipe size", In)
                .F("fixtures.toilet.sleeveRadius", "Sleeve within", In)
                .H("Medicine cabinet")
                .F("fixtures.medicineCabinet.match", "Family / type name contains", RuleKind.Regex)
                .F("fixtures.medicineCabinet.clearance", "Clearance", In)
                .H("Niche")
                .F("fixtures.niche.match", "Family / type name contains", RuleKind.Regex)
                .F("fixtures.niche.clearance", "Clearance", In));

            pages.Add(new RulePage { Title = "Levels and checklist", Intro = "How level names are read, and what the user confirms before starting." }
                .H("Level names", "Words separated by | (roof|rf). Tried in the order below; a level matching none is an apartment floor.")
                .F("levelClassification.ignore", "Reference levels (never a floor)", RuleKind.Regex, "Top of steel, parapet…")
                .F("levelClassification.bulkhead", "Bulkhead", RuleKind.Regex)
                .F("levelClassification.roof", "Roof", RuleKind.Regex)
                .F("levelClassification.setback", "Setback / terrace", RuleKind.Regex)
                .F("levelClassification.cellar", "Cellar", RuleKind.Regex)
                .H("Before any work")
                .F("generalRules.mustConfirm", "Statements to confirm", RuleKind.Lines, "One per line. A No stops the add-in.")
                .F("generalRules.reconfirmEveryDay", "Confirm again every day", B));

            pages.Add(new RulePage { Title = "S&O sheets", Intro = "The Sleeves & Openings set (SL101…) and its PDF. The S&O Set window can still change naming and PDF per project." }
                .F("soSheets.enabled", "Make the S&O set", B)
                .F("soSheets.afterAutoRun", "Make / complete sheets after Auto Run", B)
                .F("soSheets.pdfAfterAutoRun", "Print the PDF after Auto Run", B)
                .H("Sheets")
                .F("soSheets.numberPrefix", "Sheet number prefix", RuleKind.Text)
                .F("soSheets.firstNumber", "First sheet number", RuleKind.Int)
                .F("soSheets.patternSheet", "Pattern sheet", RuleKind.OptionalText, "Empty = the lowest prefix sheet that holds a Sleeves view.")
                .F("soSheets.template", "Template when the model has no S&O sheet", RuleKind.OptionalText, "A template's name (S&O Set > Templates) or a path to its .rvt. Empty = the add-in's 'SO Template'.")
                .F("soSheets.sheetName", "Sheet name", RuleKind.Text, "{Floor} = 1st Floor / Roof, {FLOOR} = upper case.")
                .F("soSheets.scheduleName", "Schedule name", RuleKind.Text, "{index} = 1st, 2nd…; {kind} = floor / roof / bulkhead / cellar.")
                .F("soSheets.floorLabel", "Floor label text", RuleKind.Text)
                .F("soSheets.floorLabelType", "Floor label text type", RuleKind.Text)
                .F("soSheets.copyTextTypes", "Also copy texts of type", RuleKind.Words)
                .F("soSheets.renameExisting", "Rename existing sheets", B)
                .F("soSheets.scheduleSizedOnly", "Schedules list sized sleeves only", B, "Rows with no size (rectangular openings) are left out.")
                .F("soSheets.unstackGrids", "Hide grid bubbles drawn on top of each other", B, "Grids of this model only; stacked grids of a link are reported.")
                .H("Notes")
                .F("soSheets.notesTag", "Notes are marked (Issued By)", RuleKind.Text)
                .F("soSheets.issueDateFormat", "Issue date format", RuleKind.Text, "MM/dd/yy")
                .H("PDF")
                .F("soSheets.pdfName", "PDF name", RuleKind.Text, "{project} = model name, {date} = today.")
                .F("soSheets.pdfDateFormat", "PDF date format", RuleKind.Text)
                .F("soSheets.pdfFolder", "Folder name (pattern)", RuleKind.Regex)
                .F("soSheets.paperSize", "Paper size", RuleKind.Choice, "Other Revit paper names can be typed.",
                    C("auto", "Auto (smallest that fits)"), C("sheet", "Sheet size"), C("ARCH_D", "ARCH D"), C("ARCH_E1", "ARCH E1"), C("ANSI_D", "ANSI D"), C("ISO_A1", "ISO A1"))
                .OrOther()
                .F("soSheets.projectNameStrip", "Removed from the model name (pattern)", RuleKind.Regex));

            var families = new RulePage { Title = "Families", Intro = "The office families Auto Run places (Map Families can still pick others per project). Empty parameter = detected from the family." };
            families.T(new RuleTable
            {
                Path = "families.roundSleeve.sizeToggles", Title = "Round sleeve: size toggle per system", Shape = TableShape.Map, KeyHeader = "System",
                Help = "The MPI sleeve has a Yes/No parameter per size ('Storm 6'). This names the prefix each system ticks. A new system needs a row here.",
                Columns = { Col("value", "Toggle prefix", RuleKind.Text, true, 160) }
            });
            families.AutoPaths.Add("families");
            pages.Add(families);

            // Advanced: how the drawings are read. Shown with every value, labelled from the comments in rules.json.
            pages.Add(Auto("Read HV drawings", "dwgProfile"));
            pages.Add(Auto("Read plumbing drawings", "plumbing.dwgProfile"));
            pages.Add(Auto("Read sprinkler drawings", "sprinkler.dwgProfile"));
            pages.Add(Auto("PDF only", "pdfOnly"));
            pages.Add(Auto("Model kind", "modelKind"));
            var adopt = Auto("Adopt existing", "adopt");
            adopt.AutoTables.Add(new RuleTable
            {
                Path = "adopt.matchers", Title = "Families placed by hand", Shape = TableShape.List, Ordered = true,
                Columns =
                {
                    Col("family", "Family name (pattern)", RuleKind.Regex, true, 220),
                    Col("type", "Type name (pattern)", RuleKind.OptionalText, false, 140),
                    Col("role", "Role", RuleKind.Choice, true, 160, null,
                        C("regularOpening", "Regular Opening"), C("pipeReferenceOpening", "Pipe Reference Opening"),
                        C("roundSleeve", "Round Sleeve"), C("electricalOpening", "Electrical Opening")),
                    Col("system", "Default system", RuleKind.OptionalText, false, 120)
                }
            });
            adopt.AutoTables.Add(new RuleTable
            {
                Path = "adopt.nameToSystem", Title = "Label → system", Shape = TableShape.List, Ordered = true,
                Columns = { Col("match", "Label (pattern)", RuleKind.Regex, true, 220), Col("system", "System", RuleKind.Text, true, 140) }
            });
            adopt.AutoTables.Add(new RuleTable
            {
                Path = "adopt.togglePrefixToSystem", Title = "Size toggle prefix → system", Shape = TableShape.Map, KeyHeader = "Prefix",
                Columns = { Col("value", "System", RuleKind.Text, true, 160) }
            });
            pages.Add(adopt);
            pages.Add(Auto("Reference set (testing)", "automation.referenceSet"));
            return pages;
        }

        private static RulePage Auto(string title, string path)
        {
            var page = new RulePage { Title = title, Advanced = true };
            page.AutoPaths.Add(path);
            return page;
        }
    }
}
