using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CadDocument = ACadSharp.CadDocument;
using SleevesOpenings.Automation.Alignment;
using SleevesOpenings.Automation.Assembly;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Legend;
using SleevesOpenings.Automation.Compare;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation.UI
{
    /// <summary>
    /// Auto Run steps 1-2: shows what is already in the model (and what to do with it), takes the engineer's
    /// PDF + DWG, reads them, matches every drawing floor to a Revit level and lines each floor up with Revit
    /// (Phase 4). Nothing in the model changes here.
    /// </summary>
    public class AutoRunForm : Form
    {
        private readonly ExistingReport _existing;
        private readonly AutomationInputs _inputs;
        private readonly LevelMap _levels;
        private readonly LegendRules _legendRules;
        private readonly DwgProfile _profile;
        private readonly IList<ReferenceDrawing> _modelRefs;
        private readonly IList<ExistingPoint> _existingPoints;
        private readonly string _modelPath;
        private readonly GridInputs _revitGrids;
        private readonly double _dryerSpacing;
        private readonly AutomationRules _automation;
        private readonly Func<Crossing, (double W, double L)?> _openingSize;
        private List<ReferenceDrawing> _lastRefs = new List<ReferenceDrawing>();

        private RadioButton _keep, _update;
        private TextBox _pdf, _dwg, _xrefs, _soSet, _messages;
        private DataGridView _floors, _tags, _risers, _align, _anchors, _openings, _so;
        private TabPage _tagsPage, _floorsPage, _risersPage, _alignPage, _openingsPage, _soPage;
        private Button _check, _ok;
        private Label _status, _alignSummary;
        private CheckBox _mark, _place;

        /// <summary>Results of the last successful check (for the next steps).</summary>
        public PdfSheetIndex Pdf { get; private set; }
        public DwgSheetIndex Dwg { get; private set; }
        public DwgRiserResult Risers { get; private set; }
        public AlignmentResult Alignment { get; private set; }
        /// <summary>Phase 5: the slab openings merged from every floor's labels and checked against the PDF.</summary>
        public RiserAssembly Assembly { get; private set; }
        /// <summary>Phase 8: the engineer's riser diagram (tagged runs), checked against the openings.</summary>
        public RiserDiagramResult Diagram { get; private set; }
        /// <summary>Phase 8: the office's S&amp;O set and how the openings compare with it (null when none was chosen).</summary>
        public SoSetResult SoSet { get; private set; }
        public SoCompareResult SoCompare { get; private set; }
        /// <summary>The user wants the anchor risers marked in the model after Save.</summary>
        public bool MarkAnchors => _mark.Checked && Alignment?.Anchors.Count > 0;
        /// <summary>Phase 6: place the "place" openings after Save (only when the Revit position check passed).</summary>
        public bool PlaceNow => _place.Checked && Assembly != null && Alignment?.Passed == true;

        public AutoRunForm(ExistingReport existing, AutomationInputs inputs, LevelMap levels, LegendRules legendRules, DwgProfile profile,
                           IList<ReferenceDrawing> modelRefs, GridInputs revitGrids, string modelPath, double dryerSpacing = 8,
                           AutomationRules automation = null, Func<Crossing, (double W, double L)?> openingSize = null)
        {
            _dryerSpacing = dryerSpacing;
            _automation = automation ?? new AutomationRules();
            _openingSize = openingSize ?? (c => null);
            _existing = existing; _inputs = inputs; _levels = levels; _legendRules = legendRules; _profile = profile ?? new DwgProfile();
            _modelRefs = modelRefs ?? new List<ReferenceDrawing>(); _modelPath = modelPath;
            _revitGrids = revitGrids ?? new GridInputs();
            _existingPoints = existing.Items.Where(i => i.Point != null)
                .Select(i => new ExistingPoint { Level = i.Level, X = i.Point.X, Y = i.Point.Y, Label = $"{i.System}, {i.Family}" }).ToList();
            Build();
        }

        private void Build()
        {
            Text = "Sleeves & Openings — Auto Run";
            var screen = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(1100, screen.Width - 40); Height = Math.Min(1000, screen.Height - 40);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ---- 1. Existing sleeves/openings
            var exBox = new GroupBox { Text = "1. Sleeves and openings already in this model", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
            var exPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            exPanel.Controls.Add(new Label { Text = _existing.Summary(), AutoSize = true, MaximumSize = new Size(800, 0) });
            _keep = new RadioButton { Text = "Keep them and add only what is missing", AutoSize = true, Checked = _inputs.Existing != ExistingPolicy.Update };
            _update = new RadioButton { Text = "Update them (fix sizes that changed, add what is missing; moved ones are reported, nothing is deleted)", AutoSize = true, Checked = _inputs.Existing == ExistingPolicy.Update };
            _keep.Enabled = _update.Enabled = _existing.Any;
            exPanel.Controls.Add(_keep);
            exPanel.Controls.Add(_update);
            exBox.Controls.Add(exPanel);
            root.Controls.Add(exBox, 0, 0);

            // ---- 2. Drawings
            var inBox = new GroupBox { Text = "2. Engineer drawings — Mechanical (plumbing and sprinkler come in a later phase)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            var files = _inputs.For(AutomationInputs.Mechanical);
            _pdf = FileRow(grid, 0, "PDF", files.Pdf, "PDF drawing set (*.pdf)|*.pdf");
            _dwg = FileRow(grid, 1, "DWG", files.Dwg, "AutoCAD drawing (*.dwg)|*.dwg");
            _xrefs = FolderRow(grid, 2, "Xrefs", files.Xrefs ?? ReferenceFiles.FindFolder(_modelPath, _profile.ReferenceFolders));
            // Testing only (rules.json automation.referenceSet.enabled): the S&O set is what Auto Run produces, so a normal
            // project has none; a finished past project's set can be compared with to tune the automation.
            if (_automation.ReferenceSet.Enabled)
            {
                _soSet = FileRow(grid, 3, "S&O set", files.Reference ?? ReferenceFiles.FindReferenceSet(files.Dwg ?? _modelPath, _automation.ReferenceSet.Files),
                                 "PDF drawing set (*.pdf)|*.pdf", "Select a finished Sleeves & Openings set to compare with (testing)");
                new ToolTip().SetToolTip(_soSet, "Testing: a finished Sleeves & Openings set (PDF) of this building. Its HVAC openings are compared with the ones " +
                                                 "from the engineer drawings, floor by floor (S&O set tab). Leave empty to skip.");
            }
            var checkRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            _check = new Button { Text = "Check drawings", AutoSize = true };
            _check.Click += async (s, e) => await CheckAsync();
            _status = new Label { AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
            checkRow.Controls.Add(_check);
            checkRow.Controls.Add(_status);
            grid.Controls.Add(checkRow, 1, _soSet != null ? 4 : 3);
            inBox.Controls.Add(grid);
            root.Controls.Add(inBox, 0, 1);

            // ---- Floors
            _floorsPage = new TabPage("Floors → Revit levels");
            _floors = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EditMode = DataGridViewEditMode.EditOnEnter
            };
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Floor", HeaderText = "Drawing floor", ReadOnly = true, FillWeight = 60 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pdf", HeaderText = "PDF page", ReadOnly = true, FillWeight = 60 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dwg", HeaderText = "DWG sheet", ReadOnly = true, FillWeight = 60 });
            var lv = new DataGridViewComboBoxColumn { Name = "Level", HeaderText = "Revit level", FillWeight = 90, FlatStyle = FlatStyle.Flat };
            lv.Items.Add(AutomationInputs.NotUsed);
            foreach (var l in _levels.All.AsEnumerable().Reverse()) lv.Items.Add(l.Name);
            _floors.Columns.Add(lv);
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "How", HeaderText = "Matched by", ReadOnly = true, FillWeight = 50 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", Visible = false });
            _floorsPage.Controls.Add(_floors);

            _messages = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
            _tags = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            _tags.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tag", FillWeight = 25 });
            _tags.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Meaning (from the drawings)", FillWeight = 120 });
            _tags.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Where", FillWeight = 60 });
            _tags.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", FillWeight = 70 });
            _risers = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            foreach (var (h, w) in new[] { ("Floor", 45), ("Tag", 40), ("Meaning", 90), ("Size down (DN)", 55), ("Size up (UP)", 55), ("Ducts", 25), ("DWG position (in)", 60), ("Result", 90), ("How it was read", 120) })
                _risers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _risersPage = new TabPage("Risers (DWG)");
            _risersPage.Controls.Add(_risers);

            _align = Grid();
            foreach (var (h, w) in new[] { ("Floor", 45), ("Revit level", 60), ("Lined up with", 90), ("Shift (in)", 55), ("Blocks agreeing", 40),
                                           ("Risers on that drawing (within 1\")", 50), ("Risers stacked on the floor below", 70), ("Result", 70), ("Notes", 150) })
                _align.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _anchors = Grid();
            foreach (var (h, w) in new[] { ("Check riser", 40), ("Floor", 45), ("Revit level", 60), ("Revit X, Y (from the internal origin)", 90), ("Off by", 30), ("Confirmed by", 150) })
                _anchors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _alignSummary = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), MaximumSize = new Size(1000, 0) };
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_align);
            split.Panel2.Controls.Add(_anchors);
            _alignPage = new TabPage("Revit position");
            _alignPage.Controls.Add(split);
            _alignPage.Controls.Add(_alignSummary);
            _openings = Grid();
            foreach (var (h, w) in new[] { ("Slab of", 45), ("Revit level", 60), ("Tag", 35), ("System", 50), ("Size", 35), ("Ducts", 25), ("Result", 40), ("Turned", 25),
                                           ("Sure", 30), ("PDF", 45), ("S&O set", 60), ("Read from", 150), ("Notes", 150) })
                _openings.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _openingsPage = new TabPage("Openings");
            _openingsPage.Controls.Add(_openings);
            _so = Grid();
            foreach (var (h, w) in new[] { ("Floor", 45), ("Result", 70), ("Ours", 45), ("Our opening", 50), ("Status here", 40), ("In the S&O set", 70),
                                           ("Its size", 50), ("Apart", 35), ("Details", 200) })
                _so.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _soPage = new TabPage("S&O set");
            _soPage.Controls.Add(_so);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var found = new TabPage("What was found");
            found.Controls.Add(_messages);
            _tagsPage = new TabPage("Tag meanings");
            _tagsPage.Controls.Add(_tags);
            tabs.TabPages.Add(_floorsPage);
            tabs.TabPages.Add(_tagsPage);
            tabs.TabPages.Add(_risersPage);
            tabs.TabPages.Add(_alignPage);
            tabs.TabPages.Add(_openingsPage);
            if (_automation.ReferenceSet.Enabled) tabs.TabPages.Add(_soPage);
            tabs.TabPages.Add(found);
            root.Controls.Add(tabs, 0, 2);

            // ---- Buttons
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            _ok = new Button { Text = "Save and continue", AutoSize = true, Enabled = false };
            _ok.Click += (s, e) => { if (Save()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            _mark = new CheckBox { Text = "Mark the check risers in the model (model lines; undo removes them)", AutoSize = true, Checked = true, Padding = new Padding(0, 4, 12, 0) };
            buttons.Controls.Add(_mark);
            _place = new CheckBox { Text = "Place the openings now (green rows; one undo)", AutoSize = true, Checked = true, Padding = new Padding(0, 4, 12, 0) };
            buttons.Controls.Add(_place);
            root.Controls.Add(buttons, 0, 3);
            CancelButton = cancel;

            Shown += async (s, e) => { if (File.Exists(_pdf.Text) || File.Exists(_dwg.Text)) await CheckAsync(); };
        }

        private static DataGridView Grid() => new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        private TextBox FolderRow(TableLayoutPanel grid, int row, string label, string value)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            var box = new TextBox { Dock = DockStyle.Fill, Text = value ?? "" };
            box.TextChanged += (s, e) => _ok.Enabled = false;
            grid.Controls.Add(box, 1, row);
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "Folder of the per-floor mechanical xrefs at Revit's 0,0 (e.g. Xref\\Xref ME)" })
                {
                    if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
            };
            grid.Controls.Add(browse, 2, row);
            new ToolTip().SetToolTip(box, "Per-floor drawings already at Revit's position (office xrefs). Used to line the DWG up with Revit " +
                                          "for floors that have no DWG imported or linked in the model.");
            return box;
        }

        private TextBox FileRow(TableLayoutPanel grid, int row, string label, string value, string filter, string title = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0), UseMnemonic = false }, 0, row);
            var box = new TextBox { Dock = DockStyle.Fill, Text = value ?? "" };
            box.TextChanged += (s, e) => _ok.Enabled = false;          // a changed path must be checked again
            grid.Controls.Add(box, 1, row);
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = filter, Title = title ?? "Select the mechanical " + label })
                {
                    if (File.Exists(box.Text)) dlg.InitialDirectory = Path.GetDirectoryName(box.Text);
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
                }
            };
            grid.Controls.Add(browse, 2, row);
            return box;
        }

        /// <summary>Reads both files off the UI thread (the readers do not touch the Revit API), then fills the floor grid.</summary>
        private async Task CheckAsync()
        {
            string pdfPath = _pdf.Text.Trim(), dwgPath = _dwg.Text.Trim(), xrefPath = _xrefs.Text.Trim(), soPath = _soSet?.Text.Trim() ?? "";
            var msg = new StringBuilder();
            if (pdfPath.Length == 0 && dwgPath.Length == 0) { _messages.Text = "Select the mechanical PDF and DWG."; return; }
            // Not found next to the model (a copy saved elsewhere): look around the engineer DWG's project folder.
            if (xrefPath.Length == 0 && dwgPath.Length > 0)
            {
                xrefPath = ReferenceFiles.FindFolder(dwgPath, _profile.ReferenceFolders) ?? "";
                _xrefs.Text = xrefPath;
            }

            _check.Enabled = _ok.Enabled = false;
            _status.Text = "Reading drawings… (a large PDF can take 20 seconds)";
            UseWaitCursor = true;
            PdfSheetIndex pdf = null; DwgSheetIndex dwg = null; DwgRiserResult risers = null; CadDocument cad = null;
            try
            {
                var tp = pdfPath.Length == 0 ? Task.FromResult<PdfSheetIndex>(null) : Task.Run(() => PdfSheetIndex.Read(pdfPath));
                var profile = _profile;
                var td = dwgPath.Length == 0 ? Task.FromResult<(CadDocument, DwgSheetIndex, DwgRiserResult)>((null, null, null)) : Task.Run(() =>
                {
                    var doc = DwgSheetIndex.Open(dwgPath);               // one read for floors, risers and alignment
                    var index = DwgSheetIndex.Read(doc, dwgPath);
                    return (doc, index, DwgRiserReader.Read(doc, index, profile));
                });
                try { pdf = await tp; } catch (Exception ex) { msg.AppendLine($"PDF could not be read: {ex.Message}"); App.Log("AutoRun PDF read failed: " + ex); }
                try { (cad, dwg, risers) = await td; } catch (Exception ex) { msg.AppendLine($"DWG could not be read: {ex.Message}"); App.Log("AutoRun DWG read failed: " + ex); }

                Pdf = pdf; Dwg = dwg; Risers = risers; Alignment = null; Assembly = null; Diagram = null; SoSet = null; SoCompare = null;
                Describe(pdf, dwg, pdfPath, dwgPath, msg);
                FillFloors(pdf, dwg);
                FillTags(pdf, msg);
                FillRisers(pdf, risers, msg);

                if (cad != null && risers != null)
                {
                    _status.Text = "Lining the drawings up with Revit…";
                    var floorLevels = FloorLevels();
                    var refs = _lastRefs = References(xrefPath, floorLevels);
                    var existingPoints = _existingPoints;
                    var grids = new GridInputs
                    {
                        DwgPath = ReferenceFiles.FindGridFile(xrefPath, profile.GridFiles), Revit = _revitGrids.Revit, RevitSource = _revitGrids.RevitSource
                    };
                    try { Alignment = await Task.Run(() => FloorAligner.Run(cad, dwg, risers, profile, refs, floorLevels, existingPoints, grids)); }
                    catch (Exception ex) { msg.AppendLine($"Lining up with Revit failed: {ex.Message}"); App.Log("AutoRun alignment failed: " + ex); }
                }
                FillAlignment(msg);

                if (dwg != null && risers != null && Alignment != null)
                {
                    _status.Text = "Merging the floors and checking the labels against the PDF…";
                    var floorLevels = FloorLevels();
                    var legend = pdf?.Legend ?? new SleevesOpenings.Automation.Legend.Legend();
                    var rules = _legendRules; var alignment = Alignment; var profile2 = _profile; var dryerSpacing = _dryerSpacing;
                    var warnings = new List<string>();
                    bool readDiagram = _automation.RiserDiagram;
                    RiserDiagramResult diagram = null;
                    try
                    {
                        Assembly = await Task.Run(() =>
                        {
                            PdfCheckResult check = null;
                            if (pdf != null)
                                try { check = PdfRiserCheck.Run(pdfPath, pdf, risers); warnings.AddRange(check.Warnings); }
                                catch (Exception ex) { App.Log("AutoRun PDF label check failed: " + ex); warnings.Add("labels not checked against the PDF: " + ex.Message); }
                            var outlines = cad == null ? null : DuctOutlines.Read(cad, profile2);
                            var assembly = RiserAssembler.Run(dwg, risers, alignment, legend, rules, floorLevels, check, outlines, dryerSpacing);
                            // Phase 8: tagged runs of the riser diagram that the plans do not show (GX-1, GX-2...)
                            if (pdf != null && readDiagram)
                                try
                                {
                                    diagram = RiserDiagram.Read(pdfPath, pdf, t =>
                                    {
                                        var m = legend.Meaning(t, rules);
                                        return m.Category == TagCategory.Opening || m.Category == TagCategory.Undefined;
                                    });
                                    DiagramCheck.Apply(assembly, diagram, legend, rules);
                                    if (diagram != null) warnings.AddRange(diagram.Warnings.Select(w => "riser diagram: " + w));
                                }
                                catch (Exception ex) { App.Log("AutoRun riser diagram failed: " + ex); warnings.Add("riser diagram not read: " + ex.Message); }
                            return assembly;
                        });
                        Diagram = diagram;
                    }
                    catch (Exception ex) { msg.AppendLine($"Merging the floors failed: {ex.Message}"); App.Log("AutoRun assembly failed: " + ex); }
                    foreach (var w in warnings) msg.AppendLine("  ! PDF check: " + w);
                }
                // Phase 8: the office's own S&O set for this building, compared floor by floor
                if (Assembly != null && soPath.Length > 0)
                {
                    if (!File.Exists(soPath)) msg.AppendLine($"S&O set: file not found ({soPath}).");
                    else
                    {
                        _status.Text = "Comparing with the S&O set...";
                        var grids = _revitGrids.Revit; var cfg = _automation.ReferenceSet; var assembly = Assembly; var size = _openingSize;
                        try
                        {
                            (SoSet, SoCompare) = await Task.Run(() =>
                            {
                                var set = SoSetReader.Read(soPath, grids, cfg);
                                return (set, SoSetCompare.Run(set, assembly, size, cfg));
                            });
                        }
                        catch (Exception ex) { msg.AppendLine($"S&O set could not be read: {ex.Message}"); App.Log("AutoRun S&O set failed: " + ex); }
                    }
                }
                FillOpenings(msg);
                FillSoSet(msg);
            }
            finally
            {
                UseWaitCursor = false;
                _check.Enabled = true;
                _status.Text = "";
            }
            _messages.Text = msg.ToString().TrimEnd();
            _ok.Enabled = (pdf != null || dwg != null) && _floors.Rows.Count > 0;
        }

        private static void Describe(PdfSheetIndex pdf, DwgSheetIndex dwg, string pdfPath, string dwgPath, StringBuilder msg)
        {
            if (pdf != null)
            {
                msg.AppendLine($"PDF: {Path.GetFileName(pdfPath)} — {pdf.PageCount} pages, {pdf.FloorPlans.Count()} floor plan(s).");
                if (pdf.Discipline != null && !pdf.Discipline.Equals("M", StringComparison.OrdinalIgnoreCase))
                    msg.AppendLine($"  ! Sheet numbers start with '{pdf.Discipline}-' — this does not look like the mechanical set.");
                Pages(msg, "Abbreviations / symbols", pdf.Sheets.Where(s => s.HasAbbreviations || s.HasSymbols));
                Pages(msg, "Riser diagram", pdf.Sheets.Where(s => s.HasRiserDiagram));
                Pages(msg, "Schedules", pdf.Sheets.Where(s => s.HasSchedule && s.Floor == null));
                if (!pdf.Sheets.Any(s => s.HasAbbreviations))
                    msg.AppendLine("  ! No ABBREVIATIONS table found — tag meanings will come from symbols and schedules only.");
                foreach (var w in pdf.Warnings) msg.AppendLine("  ! " + w);
            }
            else if (pdfPath.Length == 0) msg.AppendLine("PDF: not selected — sizes and tag meanings will come from the DWG only.");

            if (dwg != null)
            {
                msg.AppendLine($"DWG: {Path.GetFileName(dwgPath)} — {dwg.Version}, units {dwg.Units}, {dwg.Layouts.Count} sheet layout(s), {dwg.Floors.Count} floor plan(s).");
                if (!string.Equals(dwg.Units, "Inches", StringComparison.OrdinalIgnoreCase) && !string.Equals(dwg.Units, "Feet", StringComparison.OrdinalIgnoreCase))
                    msg.AppendLine($"  ! Drawing units are '{dwg.Units}' — expected inches; sizes will be checked against the PDF.");
                foreach (var w in dwg.Warnings) msg.AppendLine("  ! " + w);
            }
            else if (dwgPath.Length == 0) msg.AppendLine("DWG: not selected — locations will come from the PDF only (less exact).");

            if (pdf != null && dwg != null)
            {
                var onlyPdf = pdf.FloorPlans.Select(s => s.Floor).Except(dwg.Floors.Select(f => f.Floor)).ToList();
                var onlyDwg = dwg.Floors.Select(f => f.Floor).Except(pdf.FloorPlans.Select(s => s.Floor)).ToList();
                if (onlyPdf.Count > 0) msg.AppendLine("  ! Only in the PDF: " + string.Join(", ", onlyPdf.Select(FloorKey.Describe)));
                if (onlyDwg.Count > 0) msg.AppendLine("  ! Only in the DWG: " + string.Join(", ", onlyDwg.Select(FloorKey.Describe)));
            }
        }

        private static void Pages(StringBuilder msg, string what, IEnumerable<PdfSheet> sheets)
        {
            var list = sheets.ToList();
            msg.AppendLine(list.Count == 0 ? $"  {what}: not found" : $"  {what}: page {string.Join(", ", list.Select(s => s.Page + (s.SheetNumber != null ? $" ({s.SheetNumber})" : "")))}");
        }

        private void FillFloors(PdfSheetIndex pdf, DwgSheetIndex dwg)
        {
            _floors.Rows.Clear();
            var keys = (pdf?.FloorPlans.Select(s => s.Floor) ?? Enumerable.Empty<string>())
                .Concat(dwg?.Floors.Select(f => f.Floor) ?? Enumerable.Empty<string>()).Distinct();
            foreach (var m in FloorMatcher.Run(keys, _levels, _inputs))
            {
                var page = pdf?.FloorPlans.FirstOrDefault(s => s.Floor == m.Floor);
                var sheet = dwg?.Floors.FirstOrDefault(f => f.Floor == m.Floor);
                int i = _floors.Rows.Add(
                    FloorKey.Describe(m.Floor),
                    page == null ? "—" : $"p{page.Page} {page.SheetNumber}",
                    sheet == null ? "—" : sheet.Layout ?? "model space",
                    m.Level?.Name ?? AutomationInputs.NotUsed,
                    m.How ?? "not matched",
                    m.Floor);
                if (m.Level == null && m.How != "not used") _floors.Rows[i].DefaultCellStyle.BackColor = Color.MistyRose;
                else if (m.NeedsCheck) _floors.Rows[i].DefaultCellStyle.BackColor = Color.LightYellow;
            }
        }

        /// <summary>Every tag the PDF defines (plus the office tags) and what it means for placement; openings first.</summary>
        private void FillTags(PdfSheetIndex pdf, StringBuilder msg)
        {
            _tags.Rows.Clear();
            var entries = new List<LegendEntry>();
            foreach (var kv in _legendRules?.OfficeFixed ?? new Dictionary<string, string>())
                entries.Add(new LegendEntry { Tag = kv.Key, Definition = kv.Value, Source = "office" });
            if (pdf != null) entries.AddRange(pdf.Legend.Entries.Where(e => !entries.Any(x => x.Tag == e.Tag)).GroupBy(e => e.Tag).Select(g => pdf.Legend.Find(g.Key, _legendRules) ?? g.First()));

            var rows = entries.Select(e => (Entry: e, Meaning: Meaning(e)))
                              .OrderBy(r => r.Meaning.Category == TagCategory.Opening ? 0 : r.Meaning.LabelOnHost ? 1 : r.Meaning.Category == TagCategory.Ignore ? 2 : 3)
                              .ThenBy(r => r.Entry.Tag).ToList();
            foreach (var r in rows)
            {
                int i = _tags.Rows.Add(r.Entry.Tag, r.Entry.Definition, r.Entry.Source + (r.Entry.Page > 0 ? $", p{r.Entry.Page}" : ""), r.Meaning.Describe());
                if (r.Meaning.Category == TagCategory.Opening) _tags.Rows[i].DefaultCellStyle.BackColor = Color.Honeydew;
                else if (r.Meaning.Category == TagCategory.Other) _tags.Rows[i].DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            int openings = rows.Count(r => r.Meaning.Category == TagCategory.Opening), ignored = rows.Count(r => r.Meaning.Category == TagCategory.Ignore);
            _tagsPage.Text = $"Tag meanings ({rows.Count}, {openings} get an opening)";
            if (pdf != null)
                msg.AppendLine($"Tags: {rows.Count} defined — {openings} get an opening ({string.Join(", ", rows.Where(r => r.Meaning.Category == TagCategory.Opening).Select(r => r.Entry.Tag))}), " +
                               $"{ignored} need no opening, {rows.Count - openings - ignored} are not risers. See the Tag meanings tab.");
        }

        /// <summary>What each riser found in the DWG will become: an opening, nothing, or a report item.</summary>
        private void FillRisers(PdfSheetIndex pdf, DwgRiserResult risers, StringBuilder msg)
        {
            _risers.Rows.Clear();
            if (risers == null) { _risersPage.Text = "Risers (DWG)"; return; }
            var legend = pdf?.Legend ?? new SleevesOpenings.Automation.Legend.Legend();
            int openings = 0, missing = 0, undefined = 0, none = 0;
            foreach (var r in risers.Risers.OrderBy(r => FloorKey.Order(r.Floor)).ThenBy(r => r.Tag ?? "~"))
            {
                string meaning, result; Color? back = null;
                if (r.Tag != null)
                {
                    var m = legend.Meaning(r.Tag, _legendRules);
                    meaning = m.Entry == null ? "-" : $"{m.Entry.Definition} ({m.Entry.Source})";
                    result = m.Describe();
                    if (m.Category == TagCategory.Opening) { openings++; back = Color.Honeydew; }
                    else if (m.Category == TagCategory.Undefined) { undefined++; back = Color.MistyRose; result = "tag not defined in the PDF - reported"; }
                    else none++;
                }
                else if (r.Dryer) { meaning = "DRYER EXHAUST (from the leader text)"; result = "opening (DryerExhaust)"; openings++; back = Color.Honeydew; }
                else { meaning = "-"; result = "tag missing - reported, not placed"; missing++; back = Color.MistyRose; }

                var label = r.Label;
                int i = _risers.Rows.Add(FloorKey.Describe(r.Floor), r.Tag ?? (r.Dryer ? "(dryer)" : "(none)"), meaning,
                    label?.Down?.ToString() ?? (label?.GoesDown == true ? "?" : ""), label?.Up?.ToString() ?? (label?.GoesUp == true ? "?" : ""),
                    r.Symbols.Count, $"{r.X:0}, {r.Y:0}", result, string.Join("; ", r.Evidence));
                if (back.HasValue) _risers.Rows[i].DefaultCellStyle.BackColor = back.Value;
            }
            foreach (var t in risers.LooseTags)
            {
                int i = _risers.Rows.Add(FloorKey.Describe(t.Floor), t.Tag, "", "", "", 0, $"{t.X:0}, {t.Y:0}", "bubble not connected to any riser - reported", "");
                _risers.Rows[i].DefaultCellStyle.BackColor = Color.LightYellow;
            }
            _risersPage.Text = $"Risers (DWG) ({risers.Risers.Count}, {openings} get an opening)";
            msg.AppendLine($"Risers in the DWG: {risers.Risers.Count} on {risers.Risers.Select(r => r.Floor).Distinct().Count()} floor(s) - {openings} get an opening, " +
                           $"{none} need none, {missing} have no tag, {undefined} have an undefined tag; {risers.LooseTags.Count} tag bubble(s) not connected. See the Risers tab.");
            foreach (var w in risers.Warnings) msg.AppendLine("  ! " + w);
        }

        /// <summary>Drawing floor -> Revit level name, as the floor grid stands now (floors set to "not used" left out).</summary>
        private Dictionary<string, string> FloorLevels()
        {
            var map = new Dictionary<string, string>();
            foreach (DataGridViewRow row in _floors.Rows)
            {
                string key = (string)row.Cells["Key"].Value, level = (string)row.Cells["Level"].Value;
                if (key != null && level != null && level != AutomationInputs.NotUsed) map[key] = level;
            }
            return map;
        }

        /// <summary>Drawings at Revit's position: those in the model first (floor from the file name, else from their level), then the xref folder.</summary>
        private List<ReferenceDrawing> References(string xrefFolder, Dictionary<string, string> floorLevels)
        {
            var list = new List<ReferenceDrawing>();
            // Links whose path Revit lost (model copied elsewhere): look for the file by name in the xref folders
            // (the chosen folder and its siblings: Xref AR, Xref ME...). Copies, so each check starts from the model's values.
            var parent = string.IsNullOrEmpty(xrefFolder) ? null : Path.GetDirectoryName(xrefFolder);
            var roots = new[] { xrefFolder, parent, parent == null ? null : Path.GetDirectoryName(parent) };
            foreach (var r in _modelRefs.Select(m => m.Copy()))
            {
                if (r.Path == null || !File.Exists(r.Path))
                {
                    var found = ReferenceFiles.Locate(r.Name, roots);
                    if (found != null) { r.Path = found; r.Problem = null; }
                }
                if (r.Floor == null && r.Level != null) r.Floor = floorLevels.FirstOrDefault(kv => kv.Value == r.Level).Key;
                list.Add(r);
            }
            list.AddRange(ReferenceFiles.FromFolder(xrefFolder));
            return list;           // drawings with no floor are skipped by the aligner and listed as problems
        }

        /// <summary>Phase 4 result: how each floor lines up with Revit, and the three check risers.</summary>
        private void FillAlignment(StringBuilder msg)
        {
            _align.Rows.Clear(); _anchors.Rows.Clear();
            var a = Alignment;
            if (a == null) { _alignPage.Text = "Revit position"; _alignSummary.Text = "Select the DWG and check the drawings."; return; }

            foreach (var f in a.Floors.OrderBy(f => FloorKey.Order(f.Floor)))
            {
                string with = f.Reference == null ? "—" : $"{f.Reference.Name} ({f.Reference.Method})";
                string shift = f.Shift == null ? "" : $"{f.Shift.Dx:0.##}, {f.Shift.Dy:0.##}";
                string blocks = f.Shift == null ? "" : $"{f.Shift.Support} of {f.Shift.Comparable}";
                string onRef = f.RisersChecked == 0 ? "" : $"{f.RisersOnReference} of {f.RisersChecked}" + (f.RisersOnExisting > 0 ? $" ({f.RisersOnExisting} on existing openings)" : "");
                string stacked = f.StackedOn == null ? "" : $"{f.StackedTags.Count} on {FloorKey.Describe(f.StackedOn)}" + (f.StackedTags.Count > 0 ? $": {string.Join(", ", f.StackedTags)}" : "");
                int i = _align.Rows.Add(FloorKey.Describe(f.Floor), f.Level ?? "(not used)", with, shift, blocks, onRef, stacked, f.Status, string.Join("; ", f.Notes));
                _align.Rows[i].DefaultCellStyle.BackColor = f.Status == FloorAlignment.Confirmed ? Color.Honeydew
                    : f.Usable ? Color.LightYellow : Color.MistyRose;
            }
            foreach (var x in a.Anchors)
                _anchors.Rows.Add(x.Tag ?? "(no tag)", FloorKey.Describe(x.Floor), x.Level ?? "(not used)",
                                  $"{Ft(x.X)}, {Ft(x.Y)}", $"{x.Distance:0.##}\"", x.Evidence);

            int usable = a.Floors.Count(f => f.Usable);
            _alignPage.Text = $"Revit position ({usable}/{a.Floors.Count} floors, {(a.Passed ? "passed" : a.DrawingsMatch ? "Revit NOT proven" : "NOT passed")})";
            _alignSummary.Text = string.Join("\n", a.Messages) +
                (a.Anchors.Count > 0 ? "\nThe check risers below can be marked in the model (option at the bottom) to confirm the position by eye." : "");
            _alignSummary.ForeColor = a.Passed ? SystemColors.ControlText : Color.DarkRed;
            msg.AppendLine("Revit position: " + string.Join(" ", a.Messages.Take(1).Concat(a.Messages.Where(m => m.StartsWith("Check") || m.StartsWith("Revit") || m.StartsWith("Result")))) + " See the Revit position tab.");
            foreach (var r in _lastRefs.Where(r => r.FromModel && (r.Problem != null || r.Floor == null)))
                    msg.AppendLine($"  ! {r.Name} ({r.Method}): {r.Problem ?? "floor not recognised from its name or level"}");
        }

        /// <summary>Phase 5 result: one row per slab opening (place / review / not placed), then what is reported and not placed.</summary>
        private void FillOpenings(StringBuilder msg)
        {
            _openings.Rows.Clear();
            var a = Assembly;
            if (a == null) { _openingsPage.Text = "Openings"; return; }
            foreach (var c in a.Crossings)
            {
                int i = _openings.Rows.Add(FloorKey.Describe(c.Floor), c.Level ?? "(not used)", c.Tag ?? (c.System == "DryerExhaust" ? "(dryer)" : "-"), c.System,
                                           c.Size?.ToString() ?? (c.System == "DryerExhaust" ? "by rules" : "?"), c.Ducts,
                                           c.Status == Crossing.Place ? "place" : c.Status == Crossing.Review ? "review" : "not placed",
                                           c.Rotation.HasValue ? $"{c.Rotation.Value * 180 / Math.PI:0}°" : "-",
                                           c.Confidence, c.Pdf ?? "-", SoFor(c), string.Join(" + ", c.From), string.Join("; ", c.Notes));
                _openings.Rows[i].DefaultCellStyle.BackColor = c.Status == Crossing.Place ? Color.Honeydew : c.Status == Crossing.Review ? Color.LightYellow : Color.MistyRose;
            }
            foreach (var x in a.Issues)
            {
                int i = _openings.Rows.Add(FloorKey.Describe(x.Floor), "", x.Tag ?? "-", "", "", "", "reported", "", "", "", "", x.Type, x.Detail);
                _openings.Rows[i].DefaultCellStyle.BackColor = Color.MistyRose;
                _openings.Rows[i].DefaultCellStyle.ForeColor = Color.DimGray;
            }
            int place = a.Crossings.Count(c => c.Status == Crossing.Place), review = a.Crossings.Count(c => c.Status == Crossing.Review);
            _openingsPage.Text = $"Openings ({place} to place, {review} to review)";
            msg.AppendLine($"Openings: {place} to place, {review} to review, {a.Crossings.Count(c => c.Status == Crossing.Skip)} not placed; " +
                           $"{a.Issues.Count} item(s) reported ({string.Join(", ", a.Issues.GroupBy(x => x.Type).Select(g => $"{g.Count()} {g.Key}"))}). " +
                           $"PDF: {a.Crossings.Count(c => c.Pdf == "same")} same, {a.Crossings.Count(c => c.Pdf == "PDF size used")} PDF size used, " +
                           $"{a.Crossings.Count(c => c.Pdf == "not in the PDF")} not in the PDF. See the Openings tab.");
        }

        private string SoFor(Crossing c) => SoCompare?.Matches.FirstOrDefault(m => m.Ours == c)?.Status ?? (SoCompare == null ? "" : "-");

        /// <summary>Phase 8: every opening of ours and of the S&amp;O set, paired floor by floor.</summary>
        private void FillSoSet(StringBuilder msg)
        {
            _so.Rows.Clear();
            var cmp = SoCompare;
            if (cmp == null) { _soPage.Text = "S&O set"; return; }
            foreach (var m in cmp.Matches)
            {
                int i = _so.Rows.Add(FloorKey.Describe(m.Floor), m.Status, m.Ours?.Name ?? "", m.OurSize ?? "", m.Ours?.Status ?? "",
                                     m.Theirs?.Name ?? "", m.Theirs?.SizeText ?? "", m.Off.HasValue ? Units.FormatInches(Math.Round(m.Off.Value, 1)) : "", m.Detail);
                _so.Rows[i].DefaultCellStyle.BackColor = m.Agrees ? Color.Honeydew : m.Status == SoMatch.OnlyInSet || m.Status == SoMatch.NotInSet ? Color.MistyRose : Color.LightYellow;
            }
            _soPage.Text = $"S&O set ({cmp.Matches.Count(m => m.Agrees)} same, {cmp.Matches.Count(m => !m.Agrees)} differ)";
            msg.AppendLine($"S&O set: {Path.GetFileName(SoSet?.Path)} - {SoSet?.Sheets.Count(s => s.Ok)} of {SoSet?.Sheets.Count} floor sheet(s) lined up with the Revit grids; " +
                           $"{cmp.Summary()}. See the S&O set tab.");
            foreach (var n in cmp.NotCompared) msg.AppendLine("  ! S&O set not compared: " + n);
            foreach (var s in SoSet?.Sheets.Where(s => !s.Ok) ?? Enumerable.Empty<SoSheet>()) msg.AppendLine($"  ! S&O set page {s.Page} ({FloorKey.Describe(s.Floor)}): {s.Problem}");
            foreach (var w in SoSet?.Warnings ?? new List<string>()) msg.AppendLine("  ! S&O set: " + w);
        }

        private static string Ft(double feet) => (feet < 0 ? "-" : "") + Units.FormatInches(Math.Abs(feet) * 12);

        private TagMeaning Meaning(LegendEntry e)
        {
            var m = new TagMeaning { Tag = e.Tag, Entry = e };
            SleevesOpenings.Automation.Legend.Legend.Classify(m, e.Definition, _legendRules);
            return m;
        }

        /// <summary>Writes the choices into the inputs object (saved to the project by the command).</summary>
        private bool Save()
        {
            _floors.EndEdit();
            var files = _inputs.For(AutomationInputs.Mechanical);
            files.Pdf = _pdf.Text.Trim().Length == 0 ? null : _pdf.Text.Trim();
            files.Dwg = _dwg.Text.Trim().Length == 0 ? null : _dwg.Text.Trim();
            files.Xrefs = _xrefs.Text.Trim().Length == 0 ? null : _xrefs.Text.Trim();
            if (_soSet != null) files.Reference = _soSet.Text.Trim();     // "" = no set wanted: not searched for again
            _inputs.Existing = _update.Checked ? ExistingPolicy.Update : ExistingPolicy.KeepAndAddMissing;

            // Remember every floor whose level the user set or changed; automatic matches are recomputed next time.
            foreach (DataGridViewRow row in _floors.Rows)
            {
                string key = (string)row.Cells["Key"].Value, level = (string)row.Cells["Level"].Value;
                var auto = FloorMatcher.Run(new[] { key }, _levels, new AutomationInputs()).First().Level?.Name ?? AutomationInputs.NotUsed;
                if (level == auto) _inputs.FloorLevels.Remove(key);
                else _inputs.FloorLevels[key] = level;          // AutomationInputs.NotUsed is stored too: the user said skip this floor
            }

            if (_floors.Rows.Cast<DataGridViewRow>().All(r => (string)r.Cells["Level"].Value == AutomationInputs.NotUsed))
            {
                MessageBox.Show(this, "No drawing floor is matched to a Revit level.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // A level changed in the grid after the check: the check marks go on the level chosen now.
            if (Alignment != null)
            {
                var levels = FloorLevels();
                foreach (var f in Alignment.Floors) f.Level = levels.TryGetValue(f.Floor, out var l) ? l : null;
                foreach (var x in Alignment.Anchors) x.Level = levels.TryGetValue(x.Floor, out var l) ? l : null;
                foreach (var c in Assembly?.Crossings ?? new List<Crossing>())
                {
                    c.Level = levels.TryGetValue(c.Floor, out var l) ? l : null;
                    if (c.Level == null && c.Status != Crossing.Skip) { c.Status = Crossing.Skip; c.Notes.Add($"{FloorKey.Describe(c.Floor)} is not matched to a Revit level"); }
                }
            }
            return true;
        }

    }
}
