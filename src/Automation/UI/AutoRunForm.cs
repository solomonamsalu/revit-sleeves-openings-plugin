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
        private DwgProfile _profile;
        private readonly DwgProfile _mechanicalProfile;
        private readonly IList<ReferenceDrawing> _modelRefs;
        private readonly IList<ExistingPoint> _existingPoints;
        private readonly string _modelPath;
        private readonly GridInputs _revitGrids;
        private readonly double _dryerSpacing;
        private readonly AutomationRules _automation;
        private readonly Func<Crossing, (double W, double L)?> _openingSize;
        private readonly Action<RiserAssembly> _layout;              // openings next to each other (ERV pairs, dryers on other openings)
        private string _discipline;                                  // AutomationInputs.Mechanical / Plumbing / Sprinkler: set by the drawings picked
        private PlumbingRules _plumbing;                             // plumbing or sprinkler: pipe groups instead of ducts
        /// <summary>The pipe disciplines' rules (plumbing, sprinkler), whichever this model may run.</summary>
        private readonly IDictionary<string, PlumbingRules> _pipeOptions;
        /// <summary>What the model is (HV / PL / FP, or null when nothing recognised it) and the disciplines it may run.</summary>
        private ModelKindResult _model;
        private List<string> _allowed;
        /// <summary>HV / PL / FP when the user said what this model is in this window (saved in the model); null otherwise.</summary>
        public string ChosenModelKind { get; private set; }
        private readonly RevitColumns _columns;                      // PDF only: the model's columns, to line the plans up
        private readonly PdfOnlyRules _pdfOnly;
        /// <summary>Settles the fixture labels against the model; the second argument is the ticked floors' Revit levels (null = all).</summary>
        private readonly Func<RiserAssembly, ICollection<string>, List<string>> _resolveFixtureSleeves;
        private List<ReferenceDrawing> _lastRefs = new List<ReferenceDrawing>();
        /// <summary>The floors whose architect's DWG the fixture step read (null = all): a floor ticked later needs the step again.</summary>
        private HashSet<string> _fixturesRead;
        private bool _filling, _checking, _rebuilding;
        /// <summary>Ticks settle before the openings are merged again (several ticks in a row: one rebuild).</summary>
        private readonly Timer _tickTimer = new Timer { Interval = 700 };
        private Label _floorsTicked;

        private bool Plumbing => _plumbing != null;
        /// <summary>Sprinkler / standpipe sleeves (rules.json "sprinkler"): the pipe path, read by <see cref="SprinklerReader"/>.</summary>
        private bool Sprinkler => _discipline == AutomationInputs.Sprinkler;
        private string DisciplineWord => Sprinkler ? "sprinkler" : Plumbing ? "plumbing" : "mechanical";
        /// <summary>The discipline the drawings turned out to be (sheet numbers P-, SP-, M-): the files are remembered under it.</summary>
        public string Discipline => _discipline;
        private GroupBox _inBox;

        private RadioButton _keep, _update;
        private TextBox _pdf, _dwg, _xrefs, _soSet, _messages;
        private DataGridView _floors, _tags, _risers, _align, _anchors, _openings, _so;
        private TabPage _tagsPage, _floorsPage, _risersPage, _alignPage, _openingsPage, _soPage;
        private Button _check, _ok;
        private Label _status, _alignSummary;
        private CheckBox _mark, _place, _details;
        private TabControl _tabs;
        private TabPage _found;
        private string _fullMessages = "";
        /// <summary>
        /// Details off: the window is not shown. The remembered drawings are checked and saved on their own; the window
        /// appears only when something needs the user (and why). Shift held while clicking Auto Run always shows it.
        /// </summary>
        private bool _quiet;
        /// <summary>Compact view (details off): no tabs, only the messages that need the user, the window cut to its content.</summary>
        private TableLayoutPanel _root, _bottom;
        private TextBox _compact;
        private const int ExistingRowHeight = 150, CompactMessageHeight = 130;

        /// <summary>Remembered between runs: the user last chose to see every extraction tab (off = results only).</summary>
        private static readonly string DetailsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "autorun-details.txt");

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
        /// <summary>PDF-only mode (no DWG): the plans read from the PDF's CAD layers; null when the DWG was used.</summary>
        public PdfPlanResult PdfPlans { get; private set; }
        /// <summary>The user wants the anchor risers marked in the model after Save.</summary>
        public bool MarkAnchors => _mark.Checked && Alignment?.Anchors.Count > 0;
        /// <summary>Phase 6: place the "place" openings after Save (only when the Revit position check passed).</summary>
        public bool PlaceNow => _place.Checked && Assembly != null && Alignment?.Passed == true;
        /// <summary>The drawing floors ticked in the Floors tab (FloorKey), set by Save; null = every floor.</summary>
        public HashSet<string> PlaceFloors { get; private set; }

        public AutoRunForm(ExistingReport existing, AutomationInputs inputs, LevelMap levels, LegendRules legendRules, DwgProfile profile,
                           IList<ReferenceDrawing> modelRefs, GridInputs revitGrids, string modelPath, double dryerSpacing = 8,
                           AutomationRules automation = null, Func<Crossing, (double W, double L)?> openingSize = null,
                           Action<RiserAssembly> layout = null, string discipline = AutomationInputs.Mechanical, PlumbingRules plumbing = null,
                           RevitColumns columns = null, PdfOnlyRules pdfOnly = null, Func<RiserAssembly, ICollection<string>, List<string>> resolveFixtureSleeves = null,
                           IDictionary<string, PlumbingRules> pipeOptions = null, ModelKindResult model = null, IList<string> allowed = null,
                           DwgProfile mechanicalProfile = null)
        {
            _pipeOptions = pipeOptions ?? new Dictionary<string, PlumbingRules>();
            _model = model ?? new ModelKindResult { Kind = plumbing == null ? ModelDiscipline.HV : ModelDiscipline.PL, Source = "caller", Reason = "-" };
            _allowed = allowed?.ToList() ?? (plumbing == null ? new List<string> { AutomationInputs.Mechanical } : _pipeOptions.Keys.ToList());
            _columns = columns ?? new RevitColumns();
            _pdfOnly = pdfOnly ?? new PdfOnlyRules();
            _resolveFixtureSleeves = resolveFixtureSleeves;
            _layout = layout ?? (a => { });
            _discipline = discipline ?? AutomationInputs.Mechanical;
            _plumbing = plumbing;
            _dryerSpacing = dryerSpacing;
            _automation = automation ?? new AutomationRules();
            _openingSize = openingSize ?? (c => null);
            _existing = existing; _inputs = inputs; _levels = levels; _legendRules = legendRules; _profile = profile ?? new DwgProfile();
            _mechanicalProfile = mechanicalProfile ?? (plumbing == null ? _profile : new DwgProfile());
            _modelRefs = modelRefs ?? new List<ReferenceDrawing>(); _modelPath = modelPath;
            _revitGrids = revitGrids ?? new GridInputs();
            _existingPoints = existing.Items.Where(i => i.Point != null)
                .Select(i => new ExistingPoint { Level = i.Level, X = i.Point.X, Y = i.Point.Y, Label = $"{i.System}, {i.Family}" }).ToList();
            Build();
        }

        private void Build()
        {
            Text = "Sleeves & Openings — Auto Run" + (Plumbing ? $" ({DisciplineWord})" : "");
            var screen = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(1100, screen.Width - 40); Height = Math.Min(1000, screen.Height - 40);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 600);
            Font = new Font("Segoe UI", 9f);

            var root = _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            // A long existing-model report used to expand this row until the result tabs and
            // the bottom actions were squeezed out of the window.  Keep the report in a
            // predictable area instead; its panel has its own vertical scrollbar.
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, ExistingRowHeight));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ---- 1. Existing sleeves/openings
            var exBox = new GroupBox { Text = "1. Sleeves and openings already in this model", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var exPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0)
            };
            exPanel.Controls.Add(new Label { Text = _existing.Summary(), AutoSize = true, MaximumSize = new Size(800, 0) });
            _keep = new RadioButton { Text = "Keep them and add only what is missing", AutoSize = true, Checked = _inputs.Existing != ExistingPolicy.Update };
            _update = new RadioButton { Text = "Update them (fix sizes that changed, add what is missing; moved ones are reported, nothing is deleted)", AutoSize = true, Checked = _inputs.Existing == ExistingPolicy.Update };
            _keep.Enabled = _update.Enabled = _existing.Any;
            exPanel.Controls.Add(_keep);
            exPanel.Controls.Add(_update);
            exBox.Controls.Add(exPanel);
            root.Controls.Add(exBox, 0, 0);

            // ---- 2. Drawings
            var inBox = _inBox = new GroupBox
            {
                Text = DrawingsTitle(),
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8)
            };
            // GrowAndShrink: a long status line while reading must not leave the box tall afterwards
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            var files = _inputs.For(_discipline);
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
            var checkRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            _check = new Button { Text = "Check drawings", AutoSize = true };
            _check.Click += async (s, e) => await CheckAsync();
            _status = new Label { AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
            _details = new CheckBox { Text = "Show this window (extraction details)", AutoSize = true, Checked = LoadDetails(), Padding = new Padding(12, 4, 0, 0) };
            new ToolTip().SetToolTip(_details, "On: this window with every tab (floors, tags, risers, Revit position, openings) and every message.\n" +
                                               "Off: Auto Run does not show this window: it checks the drawings used last time and saves and continues on its own.\n" +
                                               "The window still opens when something needs you (and says why). Hold Shift while clicking Auto Run to see it anyway.");
            _details.CheckedChanged += (s, e) => { SaveDetails(_details.Checked); ApplyView(); };
            checkRow.Controls.Add(_check);
            checkRow.Controls.Add(_details);
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
            // the floors to place on: the others are still read (the risers run through them) but get nothing and are not reported
            _floors.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Place", HeaderText = "Place", FillWeight = 22 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Floor", HeaderText = "Drawing floor", ReadOnly = true, FillWeight = 60 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pdf", HeaderText = "PDF page", ReadOnly = true, FillWeight = 60 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dwg", HeaderText = "DWG sheet", ReadOnly = true, FillWeight = 60 });
            var lv = new DataGridViewComboBoxColumn { Name = "Level", HeaderText = "Revit level", FillWeight = 90, FlatStyle = FlatStyle.Flat };
            lv.Items.Add(AutomationInputs.NotUsed);
            foreach (var l in _levels.All.AsEnumerable().Reverse()) lv.Items.Add(l.Name);
            _floors.Columns.Add(lv);
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "How", HeaderText = "Matched by", ReadOnly = true, FillWeight = 50 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", Visible = false });
            _floors.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_floors.IsCurrentCellDirty && _floors.CurrentCell is DataGridViewCheckBoxCell) _floors.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _floors.CellValueChanged += (s, e) =>
            {
                if (!_filling && e.RowIndex >= 0 && e.ColumnIndex == _floors.Columns["Place"].Index) FloorsTicked();
            };
            var floorBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
            var all = new Button { Text = "All", AutoSize = true };
            var none = new Button { Text = "None", AutoSize = true };
            all.Click += (s, e) => TickAll(true);
            none.Click += (s, e) => TickAll(false);
            _floorsTicked = new Label { AutoSize = true, Padding = new Padding(8, 7, 0, 0) };
            new ToolTip().SetToolTip(_floorsTicked, "Sleeves are placed only on the ticked floors, and the report covers only them.\n" +
                                                    "The whole PDF is still read so the risers line up through the building, and sleeves already\n" +
                                                    "placed on the other floors (an earlier run) are kept and lined up with.");
            floorBar.Controls.Add(all);
            floorBar.Controls.Add(none);
            floorBar.Controls.Add(_floorsTicked);
            _floorsPage.Controls.Add(_floors);
            _floorsPage.Controls.Add(floorBar);
            _tickTimer.Tick += async (s, e) => { _tickTimer.Stop(); await RebuildForFloorsAsync(); };
            FormClosed += (s, e) => _tickTimer.Dispose();

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
            var riserColumns = Plumbing
                ? new[] { ("Floor", 45), ("Group", 40), ("Services (text at the bubble)", 120), ("Down (this slab)", 50), ("Up (slab above)", 50), ("Pipes drawn", 30), ("DWG position (in)", 60), ("Result", 90), ("How it was read", 120) }
                : new[] { ("Floor", 45), ("Tag", 40), ("Meaning", 90), ("Size down (DN)", 55), ("Size up (UP)", 55), ("Ducts", 25), ("DWG position (in)", 60), ("Result", 90), ("How it was read", 120) };
            foreach (var (h, w) in riserColumns)
                _risers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _risersPage = new TabPage(Plumbing ? "Pipe groups (DWG)" : "Risers (DWG)");
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
            // floors on top get about two thirds; the check risers below keep a few rows visible
            split.SizeChanged += (s, e) => { if (split.Height > 120) split.SplitterDistance = Math.Max(60, split.Height * 3 / 5); };
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
            var tabs = _tabs = new TabControl { Dock = DockStyle.Fill };
            var found = _found = new TabPage("What was found");
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
            // details off: the tabs give way to a short message box (only what needs the user)
            _compact = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, Visible = false };
            var resultsHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            resultsHost.Controls.Add(tabs);
            resultsHost.Controls.Add(_compact);
            root.Controls.Add(resultsHost, 0, 2);
            ApplyView();

            // ---- Buttons
            // one line: options on the left, buttons on the right (they used to wrap onto two lines and squeeze the tabs)
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var options = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            _ok = new Button { Text = "Save and continue", AutoSize = true, Enabled = false };
            _ok.Click += async (s, e) =>
            {
                _tickTimer.Stop();
                await RebuildForFloorsAsync();          // a floor ticked since the check: its fixtures read first
                if (Save()) { DialogResult = DialogResult.OK; Close(); }
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            var export = new Button { Text = "Copy extraction", AutoSize = true };
            new ToolTip().SetToolTip(export, "Saves everything read from the drawings (all tabs + the raw DWG risers) to a text file and copies it, to paste for review.");
            export.Click += (s, e) => ExportExtraction();
            buttons.Controls.Add(export);
            _place = new CheckBox { Text = "Place the openings now (one undo)", AutoSize = true, Checked = true, Padding = new Padding(0, 4, 12, 0) };
            new ToolTip().SetToolTip(_place, "Places the green rows of the Openings tab. One undo removes them all.");
            options.Controls.Add(_place);
            _mark = new CheckBox { Text = "Mark the check risers in the model", AutoSize = true, Checked = true, Padding = new Padding(0, 4, 12, 0) };
            new ToolTip().SetToolTip(_mark, "Draws model lines (circle + cross) at the check risers to confirm the position by eye; undo removes them.");
            options.Controls.Add(_mark);
            bottom.Controls.Add(options, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);
            root.Controls.Add(bottom, 0, 3);
            _bottom = bottom;
            CancelButton = cancel;

            // Details off: nothing to see unless something needs the user. Checked behind an invisible window, then saved.
            bool remembered = File.Exists(_pdf.Text) || File.Exists(_dwg.Text);
            _quiet = !_details.Checked && remembered && (Control.ModifierKeys & Keys.Shift) == 0;
            if (_quiet) { Opacity = 0; ShowInTaskbar = false; _mark.Checked = false; }
            Load += (s, e) => ApplyView();         // sizes are known once the window exists: fit it (compact when details are off)
            Shown += async (s, e) =>
            {
                if (remembered) await CheckAsync();
                if (!_quiet) return;
                string why = NeedsYou();
                if (why == null && Save()) { App.Log("AutoRun: details off - checked and saved without showing the window"); DialogResult = DialogResult.OK; Close(); return; }
                _quiet = false;
                _fullMessages = $"Shown because {why ?? "the inputs could not be saved"} (\"Show this window\" is off: it opens only when something needs you).\n\n" + _fullMessages;
                ApplyView();
                ShowInTaskbar = true; Opacity = 1; Activate();
            };
        }

        /// <summary>Why the window must be shown after a quiet check; null when everything checked out.</summary>
        private string NeedsYou()
        {
            if (Assembly == null) return "the drawings could not be read or merged";
            if (Alignment?.Passed != true) return "the Revit position check did not pass (nothing would be placed)";
            var unmatched = _floors.Rows.Cast<DataGridViewRow>().Where(r => (string)r.Cells["Level"].Value == AutomationInputs.NotUsed)
                                   .Select(r => FloorKey.Describe((string)r.Cells["Key"].Value)).ToList();
            if (unmatched.Count > 0) return $"{string.Join(", ", unmatched)} {(unmatched.Count == 1 ? "is" : "are")} not matched to a Revit level";
            var ticked = TickedFloors();
            if (ticked.Count == 0) return "no floor is ticked to place (Floors tab)";
            if (!Assembly.Crossings.Any(c => c.Status == Crossing.Place && ticked.Contains(c.Floor)))
                return _floors.Rows.Count == ticked.Count ? "nothing was found to place" : "nothing was found to place on the ticked floors";
            return null;
        }

        /// <summary>
        /// Everything the drawings gave, as plain tab-separated text: every tab of this window plus the raw DWG risers
        /// (symbols, labels with their arrow points, notes). Saved under %LOCALAPPDATA%\SleevesOpenings\extraction and
        /// copied to the clipboard, so it can be pasted for review.
        /// </summary>
        private void ExportExtraction()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Sleeves & Openings - extraction");
            sb.AppendLine($"Model\t{_modelPath}");
            sb.AppendLine($"PDF\t{_pdf.Text}");
            sb.AppendLine($"DWG\t{_dwg.Text}");
            sb.AppendLine($"Xrefs\t{_xrefs.Text}");
            sb.AppendLine($"Saved\t{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            void Section(string title, DataGridView grid)
            {
                sb.AppendLine($"## {title} ({grid.Rows.Count} rows)");
                var cols = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
                sb.AppendLine(string.Join("\t", cols.Select(c => c.HeaderText)));
                foreach (DataGridViewRow row in grid.Rows)
                    sb.AppendLine(string.Join("\t", cols.Select(c => Clean(row.Cells[c.Index].FormattedValue?.ToString()))));
                sb.AppendLine();
            }
            Section("Floors -> Revit levels", _floors);
            Section("Tag meanings", _tags);
            Section("Risers (DWG)", _risers);
            Section("Revit position", _align);
            Section("Check risers", _anchors);
            Section("Openings", _openings);

            // Where each opening lands in Revit: compares two runs (PDF only against the DWG) position by position.
            if (Assembly != null)
            {
                var placed = Assembly.Crossings.Where(c => c.HasPosition).OrderBy(c => FloorKey.Order(c.Floor)).ThenBy(c => c.Tag ?? "~").ToList();
                sb.AppendLine($"## Openings: Revit positions ({placed.Count}); from the internal origin");
                sb.AppendLine("Slab of\tTag\tSystem\tResult\tRevit X\tRevit Y\tX (ft)\tY (ft)");
                foreach (var c in placed)
                    sb.AppendLine(string.Join("\t", FloorKey.Describe(c.Floor), c.Tag ?? "-", c.System, c.Status, Ft(c.X), Ft(c.Y), $"{c.X:0.0000}", $"{c.Y:0.0000}"));
                sb.AppendLine();
            }

            // The raw reader output: what each riser is made of, before any merging across floors.
            if (Risers != null)
            {
                sb.AppendLine($"## Raw DWG risers ({Risers.Risers.Count}); positions in drawing units (inches)");
                sb.AppendLine("Floor\tTags\tX\tY\tDucts\tSymbols (block @ x,y)\tLabels (text @ arrow x,y)\tText at the bubble\tNotes\tEvidence");
                foreach (var r in Risers.Risers.OrderBy(r => FloorKey.Order(r.Floor)).ThenBy(r => r.Tag ?? "~").ThenBy(r => r.Y))
                    sb.AppendLine(string.Join("\t",
                        FloorKey.Describe(r.Floor), string.Join(" ", r.Tags), $"{r.X:0.0}", $"{r.Y:0.0}", r.Ducts,
                        string.Join("; ", r.Symbols.Select(s => $"{s.Block}{(s.Layer != null && s.Block == DwgRiserReader.PipeCircle ? " " + s.Layer : "")}{(s.Outline ? " " + s.Size : "")}{(s.InOutline ? " (inside outline)" : "")} @ {s.X:0.0},{s.Y:0.0}")),
                        string.Join("; ", r.Labels.Select(l => $"'{Clean(l.Text)}' @ {l.X:0.0},{l.Y:0.0}")),
                        Clean(string.Join(" | ", r.TagTexts.Select(t => t.Replace("\n", " / ")))),
                        Clean(string.Join("; ", r.Notes)), Clean(string.Join("; ", r.Evidence))));
                sb.AppendLine();
                if (Risers.Fixtures.Count > 0)
                {
                    sb.AppendLine($"## Fixtures named on the plans ({Risers.Fixtures.Count})");
                    foreach (var x in Risers.Fixtures.OrderBy(x => FloorKey.Order(x.Floor)).ThenBy(x => x.Code)) sb.AppendLine($"{FloorKey.Describe(x.Floor)}\t{x.Code}\t{x.X:0.0}\t{x.Y:0.0}");
                    sb.AppendLine();
                }
                sb.AppendLine($"## Loose tags ({Risers.LooseTags.Count}): bubbles not tied to any riser");
                foreach (var t in Risers.LooseTags) sb.AppendLine($"{FloorKey.Describe(t.Floor)}\t{t.Tag}\t{t.X:0.0}\t{t.Y:0.0}");
                sb.AppendLine();
                if (Risers.Warnings.Count > 0)
                {
                    sb.AppendLine("## Reader warnings");
                    foreach (var w in Risers.Warnings) sb.AppendLine(w);
                    sb.AppendLine();
                }
            }
            sb.AppendLine("## What was found");
            sb.AppendLine(_fullMessages);

            string text = sb.ToString(), path = null;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleevesOpenings", "extraction");
                Directory.CreateDirectory(dir);
                string name = string.Concat((Path.GetFileNameWithoutExtension(_modelPath) ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                path = Path.Combine(dir, $"{name} {DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, text, Encoding.UTF8);
            }
            catch (Exception ex) { App.Log("AutoRun: extraction not saved: " + ex); }

            bool copied = false;
            try { Clipboard.SetText(text); copied = true; } catch (Exception ex) { App.Log("AutoRun: extraction not copied: " + ex); }
            App.Log($"AutoRun: extraction exported ({text.Length} chars) to {path ?? "(not saved)"}");
            MessageBox.Show(this,
                (copied ? "The extraction is copied: paste it where you need it.\n\n" : "") +
                (path != null ? $"Saved to:\n{path}" : "Could not save the file (see the log)."),
                "Copy extraction", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (path != null && !copied) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        /// <summary>
        /// Details on: every tab and message. Off: the result only - the openings (and the S&amp;O set when compared), the
        /// floors / Revit position tabs only when something there needs attention, and the top-level messages.
        /// </summary>
        private void ApplyView()
        {
            bool all = _details.Checked;
            bool floorsNeedLook = _floors.Rows.Cast<DataGridViewRow>().Any(r => r.DefaultCellStyle.BackColor == Color.MistyRose || r.DefaultCellStyle.BackColor == Color.LightYellow);
            bool alignNeedsLook = Alignment != null && !Alignment.Passed;
            var pages = new List<TabPage>();
            if (all || floorsNeedLook) pages.Add(_floorsPage);
            if (all) { pages.Add(_tagsPage); pages.Add(_risersPage); }
            if (all || alignNeedsLook) pages.Add(_alignPage);
            pages.Add(_openingsPage);
            if (_automation.ReferenceSet.Enabled) pages.Add(_soPage);
            pages.Add(_found);

            var selected = _tabs.SelectedTab;
            if (!pages.SequenceEqual(_tabs.TabPages.Cast<TabPage>()))
            {
                _tabs.SuspendLayout();
                _tabs.TabPages.Clear();
                foreach (var p in pages) _tabs.TabPages.Add(p);
                _tabs.ResumeLayout();
            }
            if (selected != null && pages.Contains(selected)) _tabs.SelectedTab = selected;
            else if (!all && Assembly != null) _tabs.SelectedTab = _openingsPage;

            if (all) { _found.Text = "What was found"; _messages.Text = _fullMessages; Fit(true); return; }
            // Results only: drop the extraction lines (what each file / tag / riser gave) and the indented details.
            string[] extraction = { "PDF:", "DWG:", "Tags:", "Services:", "Risers in the DWG", "Pipe groups in the DWG" };
            var lines = _fullMessages.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var kept = lines.Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && !extraction.Any(x => l.StartsWith(x, StringComparison.Ordinal))).ToList();
            int hidden = lines.Count(l => l.TrimStart().StartsWith("!", StringComparison.Ordinal));
            if (hidden > 0) kept.Add($"({hidden} warning(s) from reading the drawings are hidden - tick Show extraction details to see them.)");
            _found.Text = "Summary";
            _messages.Text = string.Join(Environment.NewLine, kept);
            _compact.Text = _messages.Text;
            Fit(false);
        }

        /// <summary>
        /// Details on: the full window with the result tabs. Off: no tabs; a short message box when there is something to
        /// read, and the window only as tall as its content.
        /// </summary>
        private void Fit(bool full)
        {
            if (_root == null || _bottom == null) return;
            bool message = !full && _compact.Text.Trim().Length > 0;
            bool wasFull = _tabs.Visible;
            // leaving the full window: remember its size (and place, once shown) to give it back exactly
            if (wasFull && !full) { _fullSize = Size; _fullLocation = Visible ? Location : (Point?)null; }
            _tabs.Visible = full;
            _compact.Visible = message;
            _root.SuspendLayout();
            _root.RowStyles[2] = full ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, message ? CompactMessageHeight : 0);
            _root.ResumeLayout(true);
            var screen = Screen.FromControl(this).WorkingArea;
            if (full)
            {
                if (wasFull) return;
                MinimumSize = new Size(800, 600);
                Size = _fullSize ?? new Size(Math.Min(1100, screen.Width - 40), Math.Min(1000, screen.Height - 40));
                var at = _fullLocation ?? new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
                Location = new Point(Math.Max(screen.Left, Math.Min(at.X, screen.Right - Width)), Math.Max(screen.Top, Math.Min(at.Y, screen.Bottom - Height)));
                return;
            }
            int Pref(Control c) => c.GetPreferredSize(new Size(_root.ClientSize.Width - _root.Padding.Horizontal, 0)).Height + c.Margin.Vertical;
            int h = _root.Padding.Vertical + ExistingRowHeight + Pref(_inBox) + (message ? CompactMessageHeight : 0) + Pref(_bottom) + 8;
            MinimumSize = new Size(800, 0);
            ClientSize = new Size(ClientSize.Width, Math.Min(h, screen.Height - 80));
            // the short window sits in the middle of the screen
            Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
        }

        private Size? _fullSize;
        private Point? _fullLocation;

        private static bool LoadDetails()
        {
            try { return !File.Exists(DetailsFile) || File.ReadAllText(DetailsFile).Trim() != "off"; }
            catch { return true; }
        }

        private static void SaveDetails(bool on)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(DetailsFile)); File.WriteAllText(DetailsFile, on ? "on" : "off"); }
            catch (Exception ex) { App.Log("AutoRun: view choice not saved: " + ex.Message); }
        }

        /// <summary>One line, no tabs: safe inside a tab-separated row.</summary>
        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s.Replace("\\P", " "), @"[\t\r\n]+", " ").Trim();

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
                using (var dlg = new FolderBrowserDialog { Description = Sprinkler ? "Folder of the per-floor sprinkler xrefs at Revit's 0,0 (e.g. Xref\\Xref SPR)"
                                                                       : Plumbing ? "Folder of the per-floor plumbing xrefs at Revit's 0,0 (e.g. Xref\\Xref PL)"
                                                                                  : "Folder of the per-floor mechanical xrefs at Revit's 0,0 (e.g. Xref\\Xref ME)" })
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
                using (var dlg = new OpenFileDialog { Filter = filter, Title = title ?? $"Select the {DisciplineWord} {label}" })
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
            if (pdfPath.Length == 0 && dwgPath.Length == 0) { _fullMessages = $"Select the {DisciplineWord} PDF and DWG."; ApplyView(); return; }

            _check.Enabled = _ok.Enabled = false;
            _checking = true;
            _tickTimer.Stop();                      // the check reads the ticks as they are now
            _status.Text = "Reading drawings… (a large PDF can take 20 seconds)";
            UseWaitCursor = true;
            PdfSheetIndex pdf = null; DwgSheetIndex dwg = null; DwgRiserResult risers = null; CadDocument cad = null;
            try
            {
                Task<(CadDocument, DwgSheetIndex)> Sheets(string path) => path.Length == 0
                    ? Task.FromResult<(CadDocument, DwgSheetIndex)>((null, null))
                    : Task.Run(() => { var doc = DwgSheetIndex.Open(path); return (doc, DwgSheetIndex.Read(doc, path)); });   // one read for floors, risers and alignment
                var tp = pdfPath.Length == 0 ? Task.FromResult<PdfSheetIndex>(null) : Task.Run(() => PdfSheetIndex.Read(pdfPath));
                var td = Sheets(dwgPath);
                try { pdf = await tp; } catch (Exception ex) { msg.AppendLine($"PDF could not be read: {ex.Message}"); App.Log("AutoRun PDF read failed: " + ex); }
                try { (cad, dwg) = await td; } catch (Exception ex) { msg.AppendLine($"DWG could not be read: {ex.Message}"); App.Log("AutoRun DWG read failed: " + ex); }

                // ---- which drawings these are: the sheet numbers say it (P- plumbing, SP- sprinkler, M- mechanical)
                string fromPdf = DisciplineOfSheets(pdf?.Discipline), fromDwg = DisciplineOfSheets(LayoutPrefix(dwg?.Layouts));
                string wanted = fromPdf ?? fromDwg;
                string sheets = fromPdf != null ? pdf.Discipline : fromDwg != null ? LayoutPrefix(dwg.Layouts) : null;
                // the drawings and the model disagree (P- drawings in the HV model): stop, unless the user says what the model really is
                if (wanted != null && !_allowed.Contains(wanted))
                {
                    var kinds = ModelDiscipline.KindsFor(wanted);
                    string kind = AskModelKind($"The drawings are the {Word(wanted)} set (sheet numbers {sheets}-), but this model is " +
                                               $"{ModelDiscipline.Word(_model.Kind)}, from {_model.Source}: {_model.Reason}.",
                                               $"Run {Word(wanted)} here only if this model really is a {string.Join(" or ", kinds)} model; " +
                                               "the answer is saved in the model. Otherwise stop and pick the right drawings or open the right model.", kinds);
                    if (kind == null)
                    {
                        msg.AppendLine($"Stopped: the drawings are the {Word(wanted)} set (sheets {sheets}-) but this model is {ModelDiscipline.Word(_model.Kind)} " +
                                       $"({_model.Source}: {_model.Reason}). Pick the {string.Join(" / ", _allowed.Select(Word))} drawings, or open the {Word(wanted)} model.");
                        _fullMessages = msg.ToString().TrimEnd();
                        ApplyView();
                        return;
                    }
                    SetModelKind(kind);
                }
                // nothing says what the model is and the sheet numbers say nothing either: ask once (saved in the model)
                else if (wanted == null && _model.Kind == null)
                {
                    var all = new List<string> { ModelDiscipline.HV, ModelDiscipline.PL, ModelDiscipline.FP };
                    string kind = AskModelKind($"Neither this model ({_model.Reason}) nor the drawings' sheet numbers say which discipline this is.",
                                               "Choose what this model is; the answer is saved in the model and not asked again.", all);
                    if (kind == null)
                    {
                        msg.AppendLine("Stopped: the model type is not known and the drawings' sheet numbers do not say it. Run Auto Run again and choose HV, PL or FP.");
                        _fullMessages = msg.ToString().TrimEnd();
                        ApplyView();
                        return;
                    }
                    SetModelKind(kind);
                    wanted = _allowed.First();
                }
                if (wanted != null && wanted != _discipline && _allowed.Contains(wanted))
                {
                    var before = _inputs.For(_discipline); var after = _inputs.For(wanted);
                    string oldProfileFolders = _profile.ReferenceFolders;
                    Switch(wanted);
                    msg.AppendLine($"Recognised the {DisciplineWord} drawings (sheet numbers {(fromPdf != null ? pdf.Discipline : LayoutPrefix(dwg.Layouts))}-): " +
                                   $"{DisciplineWord} sleeves.");
                    // the other boxes still hold the other discipline's remembered files: bring this discipline's instead
                    if (fromPdf != null && dwgPath.Length > 0 && dwgPath == (before.Dwg ?? "") && DisciplineOfSheets(LayoutPrefix(dwg?.Layouts)) != wanted)
                    {
                        dwgPath = after.Dwg ?? "";
                        _dwg.Text = dwgPath;
                        msg.AppendLine(dwgPath.Length > 0 ? $"  DWG: the {DisciplineWord} DWG used last time ({Path.GetFileName(dwgPath)})."
                                                          : $"  DWG: none picked for the {DisciplineWord} drawings yet: reading the PDF only.");
                        cad = null; dwg = null;
                        try { (cad, dwg) = await Sheets(dwgPath); } catch (Exception ex) { msg.AppendLine($"DWG could not be read: {ex.Message}"); App.Log("AutoRun DWG read failed: " + ex); }
                    }
                    if (xrefPath.Length == 0 || xrefPath == (before.Xrefs ?? "") ||
                        (!string.IsNullOrEmpty(oldProfileFolders) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(xrefPath.TrimEnd('\\', '/')), oldProfileFolders,
                                                                                                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                    {
                        xrefPath = after.Xrefs ?? ReferenceFiles.FindFolder(_modelPath, _profile.ReferenceFolders) ?? "";
                        _xrefs.Text = xrefPath;
                    }
                }
                string dwgSays = DisciplineOfSheets(LayoutPrefix(dwg?.Layouts));
                if (fromPdf != null && dwgSays != null && fromPdf != dwgSays)
                {
                    msg.AppendLine($"The PDF is the {Word(fromPdf)} set (sheets {pdf.Discipline}-) but the DWG is the {Word(dwgSays)} set (layouts {LayoutPrefix(dwg.Layouts)}-): " +
                                   $"pick the {Word(fromPdf)} DWG, or clear the DWG box to read the PDF only.");
                    _fullMessages = msg.ToString().TrimEnd();
                    ApplyView();
                    return;
                }
                // Not found next to the model (a copy saved elsewhere): look around the engineer DWG's project folder.
                if (xrefPath.Length == 0 && dwgPath.Length > 0)
                {
                    xrefPath = ReferenceFiles.FindFolder(dwgPath, _profile.ReferenceFolders) ?? "";
                    _xrefs.Text = xrefPath;
                }

                var profile = _profile; var pipeRules = _plumbing; bool sprinkler = Sprinkler;
                if (cad != null)
                {
                    var c0 = cad; var d0 = dwg;
                    try { risers = await Task.Run(() => sprinkler ? SprinklerReader.Read(c0, d0, pipeRules) : DwgRiserReader.Read(c0, d0, profile)); }
                    catch (Exception ex) { msg.AppendLine($"DWG could not be read: {ex.Message}"); App.Log("AutoRun DWG read failed: " + ex); cad = null; dwg = null; }
                }

                // PDF only (no DWG): the plans are read from the PDF's CAD layers and lined up by their columns
                PdfPlanResult plans = null;
                if (dwgPath.Length == 0 && pdf != null && _pdfOnly.Enabled)
                {
                    // plumbing (pipe circles), sprinkler (worded labels) or mechanical (duct section marks, tag bubbles, size labels)
                    _status.Text = "Reading the plans from the PDF (PDF only)…";
                    var pdfOnly = _pdfOnly;
                    try { plans = await Task.Run(() => PdfPlanReader.Read(pdfPath, pdf, profile, pdfOnly, sprinkler ? pipeRules : null, sprinkler ? null : pipeRules?.Fixtures)); dwg = plans.Index; risers = plans.Risers; }
                    catch (Exception ex) { msg.AppendLine($"The PDF plans could not be read: {ex.Message}"); App.Log("AutoRun PDF-only read failed: " + ex); }
                }

                msg.Insert(0, $"Model: {_model.Describe()}{Environment.NewLine}");
                Pdf = pdf; Dwg = dwg; Risers = risers; PdfPlans = plans; Alignment = null; Assembly = null; Diagram = null; SoSet = null; SoCompare = null;
                Describe(pdf, plans == null ? dwg : null, pdfPath, plans == null ? dwgPath : "-", msg, DisciplineOfSheets(pdf?.Discipline) == _discipline ? pdf.Discipline : Sprinkler ? "SP" : Plumbing ? "P" : "M", DisciplineWord);
                if (plans != null) DescribePdfOnly(plans, msg);
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
                    // sprinkler: the office xref's labelled risers are the known positions the engineer's risers are checked on
                    Func<CadDocument, List<RiserSymbol>> refRisers = sprinkler ? (c => SprinklerReader.Symbols(c, pipeRules)) : (Func<CadDocument, List<RiserSymbol>>)null;
                    try { Alignment = await Task.Run(() => FloorAligner.Run(cad, dwg, risers, profile, refs, floorLevels, existingPoints, grids, referenceRisers: refRisers)); }
                    catch (Exception ex) { msg.AppendLine($"Lining up with Revit failed: {ex.Message}"); App.Log("AutoRun alignment failed: " + ex); }
                }
                else if (plans != null)
                {
                    _status.Text = "Lining the PDF plans up with Revit by their columns…";
                    var floorLevels = FloorLevels(); var columns = _columns; var pdfOnly = _pdfOnly;
                    try { Alignment = await Task.Run(() => ColumnAligner.Run(plans, columns, floorLevels, pdfOnly)); }
                    catch (Exception ex) { msg.AppendLine($"Lining up with Revit failed: {ex.Message}"); App.Log("AutoRun PDF-only alignment failed: " + ex); }
                }
                FillAlignment(msg);

                if (dwg != null && risers != null && Alignment != null && Plumbing)
                {
                    // PL model: pipe groups -> sleeves; no duct label check, riser diagram or duct outlines
                    await AssemblePipesAsync(msg);
                    if (Sprinkler)
                        msg.AppendLine("  Sprinkler: pipe sizes are read from the labels (3\" SPRINKLER RISER, 4\" FDC); sleeve = pipe + " +
                                       $"{Units.FormatInches(_plumbing.SleeveOverPipe)} (rules.json sprinkler). Risers inside the trash chute get no sleeve.");
                    else if (pdf != null)
                        msg.AppendLine("  Plumbing: sizes are not on the plans; each sleeve uses rules.json plumbing.services pipe sizes (check the riser diagram" +
                                       (pdf.Sheets.Any(s => s.HasRiserDiagram) ? $", page {string.Join(", ", pdf.Sheets.Where(s => s.HasRiserDiagram).Select(s => s.Page))}" : "") + ").");
                }
                else if (dwg != null && risers != null && Alignment != null)
                {
                    _status.Text = "Merging the floors and checking the labels against the PDF…";
                    var floorLevels = FloorLevels();
                    var legend = pdf?.Legend ?? new SleevesOpenings.Automation.Legend.Legend();
                    var rules = _legendRules; var alignment = Alignment; var profile2 = _profile; var dryerSpacing = _dryerSpacing; var layout = _layout;
                    var pdfPlans = plans;
                    var warnings = new List<string>();
                    bool readDiagram = _automation.RiserDiagram;
                    bool trustUp = _automation.Decisions?.TrustUpFromBelow ?? false;
                    RiserDiagramResult diagram = null;
                    try
                    {
                        Assembly = await Task.Run(() =>
                        {
                            PdfCheckResult check = null;
                            // PDF only: the labels were read from this PDF, there is nothing to check them against
                            if (pdf != null && pdfPlans == null)
                                try { check = PdfRiserCheck.Run(pdfPath, pdf, risers); warnings.AddRange(check.Warnings); }
                                catch (Exception ex) { App.Log("AutoRun PDF label check failed: " + ex); warnings.Add("labels not checked against the PDF: " + ex.Message); }
                            var outlines = cad == null ? null : DuctOutlines.Read(cad, profile2);
                            var assembly = RiserAssembler.Run(dwg, risers, alignment, legend, rules, floorLevels, check, outlines, dryerSpacing,
                                                              trustUp: trustUp);
                            // Phase 8: tagged runs of the riser diagram that the plans do not show (GX-1, GX-2...)
                            if (pdf != null && readDiagram)
                                try
                                {
                                    diagram = RiserDiagram.Read(pdfPath, pdf, t =>
                                    {
                                        var m = legend.Meaning(t, rules);
                                        return m.Category == TagCategory.Opening || m.Category == TagCategory.Undefined;
                                    }, t => legend.Meaning(t, rules).LabelOnHost);
                                    DiagramCheck.Apply(assembly, diagram, legend, rules);
                                    if (diagram != null) warnings.AddRange(diagram.Warnings.Select(w => "riser diagram: " + w));
                                }
                                catch (Exception ex) { App.Log("AutoRun riser diagram failed: " + ex); warnings.Add("riser diagram not read: " + ex.Message); }
                            layout(assembly);
                            return assembly;
                        });
                        Diagram = diagram;
                        if (Assembly != null && plans != null) MarkPdfOnly(Assembly, plans);
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
                _checking = false;
                _status.Text = "";
            }
            _fullMessages = msg.ToString().TrimEnd();
            ApplyView();
            _ok.Enabled = (pdf != null || dwg != null) && _floors.Rows.Count > 0;
        }

        /// <summary>The discipline a sheet-number prefix stands for: P plumbing, SP / FP sprinkler, M / H mechanical; null for any other.</summary>
        internal static string DisciplineOfSheets(string prefix)
        {
            switch ((prefix ?? "").Trim().ToUpperInvariant())
            {
                case "P": case "PL": return AutomationInputs.Plumbing;
                case "SP": case "FP": case "FS": return AutomationInputs.Sprinkler;
                case "M": case "H": case "HV": case "MH": return AutomationInputs.Mechanical;
                default: return null;
            }
        }

        /// <summary>The most common sheet prefix of the DWG's layouts ("SP-001.00" -> SP); null when the layouts are not numbered.</summary>
        private static string LayoutPrefix(IEnumerable<string> layouts)
        {
            var prefixes = (layouts ?? Enumerable.Empty<string>())
                .Select(l => System.Text.RegularExpressions.Regex.Match(l ?? "", @"^\s*([A-Za-z]{1,3})\s*-?\s*\d"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value.ToUpperInvariant()).ToList();
            return prefixes.GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        }

        private static string Word(string discipline) =>
            discipline == AutomationInputs.Sprinkler ? "sprinkler" : discipline == AutomationInputs.Plumbing ? "plumbing" : "mechanical";

        private string DrawingsTitle() =>
            Sprinkler ? "2. Engineer drawings — Sprinkler / standpipe (riser and FDC sleeves; recognised by the SP- sheet numbers)"
          : Plumbing ? "2. Engineer drawings — Plumbing (pipe sleeves; pick the plumbing or the sprinkler set: the sheet numbers say which)"
                     : _model.Kind == null ? "2. Engineer drawings — model type not recognised: the sheet numbers (M-, P-, SP-) say which discipline this is"
                     : "2. Engineer drawings — Mechanical (HV model; a PL model gets the plumbing drawings)";

        /// <summary>The drawings picked are another pipe discipline's (sprinkler set in the PL model): run as that one.</summary>
        private void Switch(string discipline)
        {
            _discipline = discipline;
            _plumbing = _pipeOptions.TryGetValue(discipline, out var pipe) ? pipe : null;
            _profile = _plumbing?.Profile ?? _mechanicalProfile;
            Text = "Sleeves & Openings — Auto Run" + (Plumbing ? $" ({DisciplineWord})" : "");
            if (_inBox != null) _inBox.Text = DrawingsTitle();
            App.Log($"AutoRun: drawings recognised as {discipline}");
        }

        /// <summary>The user said what the model is: it runs that kind's disciplines from now on (saved by the command).</summary>
        private void SetModelKind(string kind)
        {
            ChosenModelKind = kind;
            _model = new ModelKindResult { Kind = kind, Source = "your choice", Reason = "chosen in Auto Run, saved in this model" };
            var pipe = _pipeOptions.Keys.ToList();                   // plumbing, then sprinkler when it is on
            if (kind == ModelDiscipline.FP) pipe.Reverse();          // the FP model opens on sprinkler
            _allowed = kind == ModelDiscipline.HV ? new List<string> { AutomationInputs.Mechanical } : pipe;
            if (_inBox != null) _inBox.Text = DrawingsTitle();
            App.Log($"AutoRun: model kind set to {kind} by the user");
        }

        /// <summary>Asks what the model is (one button per kind, and Stop); null = stop. Shows the window first when it was checking quietly.</summary>
        private string AskModelKind(string question, string detail, IList<string> kinds)
        {
            if (_quiet) { _quiet = false; ShowInTaskbar = true; Opacity = 1; Activate(); }
            using (var dlg = new Form
            {
                Text = "Auto Run — which model is this?", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = Font, Padding = new Padding(12)
            })
            {
                var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
                panel.Controls.Add(new Label { Text = question, AutoSize = true, MaximumSize = new Size(520, 0), Font = new Font(Font, FontStyle.Bold), UseMnemonic = false });
                panel.Controls.Add(new Label { Text = detail, AutoSize = true, MaximumSize = new Size(520, 0), Padding = new Padding(0, 6, 0, 10), UseMnemonic = false });
                var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                string chosen = null;
                foreach (var k in kinds)
                {
                    var b = new Button { Text = $"{ModelDiscipline.Word(k)} model", AutoSize = true };
                    b.Click += (s, e) => { chosen = k; dlg.DialogResult = DialogResult.OK; };
                    buttons.Controls.Add(b);
                }
                var stop = new Button { Text = "Stop", AutoSize = true, DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(stop);
                dlg.CancelButton = stop;
                panel.Controls.Add(buttons);
                dlg.Controls.Add(panel);
                return dlg.ShowDialog(this) == DialogResult.OK ? chosen : null;
            }
        }

        private static void Describe(PdfSheetIndex pdf, DwgSheetIndex dwg, string pdfPath, string dwgPath, StringBuilder msg, string letter, string word)
        {
            if (pdf != null)
            {
                msg.AppendLine($"PDF: {Path.GetFileName(pdfPath)} — {pdf.PageCount} pages, {pdf.FloorPlans.Count()} floor plan(s).");
                if (pdf.Discipline != null && !pdf.Discipline.Equals(letter, StringComparison.OrdinalIgnoreCase))
                    msg.AppendLine($"  ! Sheet numbers start with '{pdf.Discipline}-' — this does not look like the {word} set.");
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
            if (dwgPath == "-") return;                  // PDF only: described on its own

            if (pdf != null && dwg != null)
            {
                var onlyPdf = pdf.FloorPlans.Select(s => s.Floor).Except(dwg.Floors.Select(f => f.Floor)).ToList();
                var onlyDwg = dwg.Floors.Select(f => f.Floor).Except(pdf.FloorPlans.Select(s => s.Floor)).ToList();
                if (onlyPdf.Count > 0) msg.AppendLine("  ! Only in the PDF: " + string.Join(", ", onlyPdf.Select(FloorKey.Describe)));
                if (onlyDwg.Count > 0) msg.AppendLine("  ! Only in the DWG: " + string.Join(", ", onlyDwg.Select(FloorKey.Describe)));
            }
        }

        /// <summary>PDF only: what was read from each plan page.</summary>
        private void DescribePdfOnly(PdfPlanResult plans, StringBuilder msg)
        {
            var read = plans.Plans.Where(p => p.Problem == null).ToList();
            msg.AppendLine($"DWG: none — PDF only: {read.Count} of {plans.Plans.Count} floor plan(s) read from the PDF's CAD layers" +
                           (read.Count == 0 ? "." : $" ({string.Join(", ", read.GroupBy(p => p.ScaleText ?? PdfPlanReader.Ratio(p.Scale)).Select(g => g.Key))}); " +
                                                    $"columns drawn: {string.Join(", ", read.Select(p => $"{FloorKey.Describe(p.Floor)} {p.Columns.Count}"))}."));
            msg.AppendLine($"  Model columns to line the plans up with: {_columns.Columns.Count} ({_columns.Source ?? "none"}).");
            if (_columns.Columns.Count == 0) msg.AppendLine("  ! The model has no columns: load the structural link (Manage Links) and check again.");
            foreach (var w in plans.Warnings) msg.AppendLine("  ! " + w);
        }

        /// <summary>PDF only: every sleeve says where its position came from; with placeWhenPassed off, none is placed without review.</summary>
        private void MarkPdfOnly(RiserAssembly assembly, PdfPlanResult plans)
        {
            foreach (var c in assembly.Crossings)
            {
                if (c.FromModel) continue;                                   // fixture sleeves placed from the modelled fixture
                var fa = Alignment?.For(c.Floor);
                string how = fa?.Status == FloorAlignment.Confirmed ? "lined up by its columns"
                           : fa?.Usable == true ? (fa.Notes.Any(n => n.Contains("sheet frame")) ? "lined up in the sheet frame the other floors share"
                                                                                                  : "lined up by the pipes stacked on the floor next to it")
                           : "not lined up";
                c.Pdf = "PDF only";
                c.Notes.Add($"position from the PDF only (page {plans.For(c.Floor)?.Page}, {how})");
                if (!_pdfOnly.PlaceWhenPassed && c.Status == Crossing.Place)
                {
                    c.Status = Crossing.Review;
                    c.Notes.Add("PDF-only sleeves go to review (rules.json pdfOnly.placeWhenPassed = false)");
                }
            }
        }

        private static void Pages(StringBuilder msg, string what, IEnumerable<PdfSheet> sheets)
        {
            var list = sheets.ToList();
            msg.AppendLine(list.Count == 0 ? $"  {what}: not found" : $"  {what}: page {string.Join(", ", list.Select(s => s.Page + (s.SheetNumber != null ? $" ({s.SheetNumber})" : "")))}");
        }

        private void FillFloors(PdfSheetIndex pdf, DwgSheetIndex dwg)
        {
            _filling = true;
            _floors.Rows.Clear();
            var keys = (pdf?.FloorPlans.Select(s => s.Floor) ?? Enumerable.Empty<string>())
                .Concat(dwg?.Floors.Select(f => f.Floor) ?? Enumerable.Empty<string>()).Distinct();
            var skip = new HashSet<string>(_inputs.SkipFloors ?? new List<string>());
            foreach (var m in FloorMatcher.Run(keys, _levels, _inputs))
            {
                var page = pdf?.FloorPlans.FirstOrDefault(s => s.Floor == m.Floor);
                var sheet = dwg?.Floors.FirstOrDefault(f => f.Floor == m.Floor);
                int i = _floors.Rows.Add(
                    !skip.Contains(m.Floor),
                    FloorKey.Describe(m.Floor),
                    page == null ? "—" : $"p{page.Page} {page.SheetNumber}",
                    sheet == null ? "—" : sheet.Layout ?? "model space",
                    m.Level?.Name ?? AutomationInputs.NotUsed,
                    m.How ?? "not matched",
                    m.Floor);
                if (m.Level == null && m.How != "not used") _floors.Rows[i].DefaultCellStyle.BackColor = Color.MistyRose;
                else if (m.NeedsCheck) _floors.Rows[i].DefaultCellStyle.BackColor = Color.LightYellow;
            }
            _filling = false;
            ShowTicked();
        }

        /// <summary>The drawing floors ticked to place on (FloorKey).</summary>
        private HashSet<string> TickedFloors() =>
            new HashSet<string>(_floors.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["Place"].Value as bool? == true)
                                           .Select(r => (string)r.Cells["Key"].Value).Where(k => k != null));

        /// <summary>The Revit levels of the ticked floors; null when every floor is ticked (nothing left out).</summary>
        private List<string> TickedLevels()
        {
            var ticked = TickedFloors();
            if (ticked.Count == _floors.Rows.Count) return null;
            return FloorLevels().Where(kv => ticked.Contains(kv.Key)).Select(kv => kv.Value).Distinct().ToList();
        }

        private void TickAll(bool on)
        {
            _floors.EndEdit();
            _filling = true;
            foreach (DataGridViewRow row in _floors.Rows) row.Cells["Place"].Value = on;
            _filling = false;
            FloorsTicked();
        }

        private void ShowTicked()
        {
            int n = TickedFloors().Count, all = _floors.Rows.Count;
            _floorsTicked.Text = all == 0 ? "" : n == all ? $"Placing on every floor ({all})." : $"Placing on {n} of {all} floors: the others are read for the risers only.";
            _floorsTicked.ForeColor = n == 0 && all > 0 ? Color.DarkRed : SystemColors.ControlText;
        }

        /// <summary>A tick changed: a floor ticked that the fixture step did not read is read once the ticks settle.</summary>
        private void FloorsTicked()
        {
            ShowTicked();
            if (FixturesStale()) { _tickTimer.Stop(); _tickTimer.Start(); }
        }

        /// <summary>A ticked floor's architect's DWG was not read by the fixture step (plumbing only).</summary>
        private bool FixturesStale() =>
            Plumbing && !Sprinkler && _resolveFixtureSleeves != null && Assembly != null && _fixturesRead != null &&
            TickedFloors().Any(k => !_fixturesRead.Contains(k));

        /// <summary>
        /// Plumbing: the pipe groups merged into slab crossings, then the fixture labels settled against the model. The
        /// architect's DWG is read on the ticked floors only (the PDF's risers on every floor).
        /// </summary>
        private async Task AssemblePipesAsync(StringBuilder msg)
        {
            _status.Text = "Merging the floors (pipe groups)…";
            var dwg = Dwg; var risers = Risers; var plans = PdfPlans;
            var floorLevels = FloorLevels(); var alignment = Alignment; var rules = _plumbing;
            bool trustUp = _automation.Decisions?.TrustUpFromBelow ?? false;
            Assembly = null;
            try { Assembly = await Task.Run(() => PipeAssembler.Run(dwg, risers, alignment, floorLevels, rules, trustUp: trustUp)); }
            catch (Exception ex) { msg.AppendLine($"Merging the floors failed: {ex.Message}"); App.Log("AutoRun plumbing assembly failed: " + ex); }
            if (Assembly != null && _resolveFixtureSleeves != null && !Sprinkler)
            {
                var only = TickedLevels();
                _fixturesRead = only == null ? null : TickedFloors();
                if (only != null)
                {
                    _status.Text = "Reading the fixtures of the ticked floors…";
                    msg.AppendLine($"  Fixtures: the architect's DWG read on the ticked floors only ({string.Join(", ", only)}).");
                }
                try { foreach (var line in _resolveFixtureSleeves(Assembly, only)) msg.AppendLine("  " + line); }
                catch (Exception ex) { msg.AppendLine("  ! Fixture connector lookup failed; fixture sleeves remain review: " + ex.Message); App.Log("AutoRun fixture connector lookup failed: " + ex); }
            }
            if (Assembly != null && plans != null) MarkPdfOnly(Assembly, plans);
        }

        /// <summary>
        /// Floors ticked after the check: the openings merged again and the fixtures read on the floors ticked now (the
        /// fixture step changes the openings in place, so it is not run twice on one merge). Plumbing only; the drawings
        /// already read are reused.
        /// </summary>
        private async Task RebuildForFloorsAsync()
        {
            if (_checking || _rebuilding || !FixturesStale() || Dwg == null || Risers == null || Alignment == null) return;
            _rebuilding = true;
            _check.Enabled = _ok.Enabled = false;
            UseWaitCursor = true;
            var msg = new StringBuilder();
            try
            {
                msg.AppendLine("Floors ticked since the check: the floors merged again and the fixtures read on the floors ticked now.");
                await AssemblePipesAsync(msg);
                if (Assembly != null && SoSet != null)
                    try { var set = SoSet; var assembly = Assembly; var size = _openingSize; var cfg = _automation.ReferenceSet; SoCompare = await Task.Run(() => SoSetCompare.Run(set, assembly, size, cfg)); }
                    catch (Exception ex) { App.Log("AutoRun S&O set compare failed: " + ex); SoCompare = null; }
                FillOpenings(msg);
                FillSoSet(msg);
            }
            finally
            {
                _rebuilding = false;
                UseWaitCursor = false;
                _check.Enabled = true;
                _status.Text = "";
                _ok.Enabled = (Pdf != null || Dwg != null) && _floors.Rows.Count > 0;
            }
            _fullMessages = (_fullMessages + Environment.NewLine + Environment.NewLine + msg.ToString()).Trim();
            ApplyView();
        }

        /// <summary>Every tag the PDF defines (plus the office tags) and what it means for placement; openings first.</summary>
        private void FillTags(PdfSheetIndex pdf, StringBuilder msg)
        {
            _tags.Rows.Clear();
            if (Plumbing) { FillPlumbingTags(pdf, msg); return; }
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

        /// <summary>
        /// PL model: the service letters written next to the pipe groups and the fixtures named on the plans, what the
        /// office does with each (rules.json plumbing), and whether the PDF's legend defines the letters.
        /// </summary>
        private void FillPlumbingTags(PdfSheetIndex pdf, StringBuilder msg)
        {
            string Pdf(string code)
            {
                var e = pdf?.Legend.Entries.FirstOrDefault(x => string.Equals(x.Tag, code, StringComparison.OrdinalIgnoreCase));
                return e == null ? "" : $" — PDF: {e.Definition} (p{e.Page})";
            }
            foreach (var kv in _plumbing.Services.OrderBy(kv => kv.Value.Sleeve ? 0 : 1).ThenBy(kv => kv.Key))
            {
                var s = kv.Value;
                int i = _tags.Rows.Add(kv.Key, s.System + Pdf(kv.Key), "rules.json plumbing.services",
                                       s.Sleeve ? $"sleeve {Units.FormatInches(_plumbing.SleeveFor(s.Pipe))} (pipe {Units.FormatInches(s.Pipe)} + {Units.FormatInches(_plumbing.SleeveOverPipe)})" : "no sleeve");
                if (s.Sleeve) _tags.Rows[i].DefaultCellStyle.BackColor = Color.Honeydew;
                else _tags.Rows[i].DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            bool review = !string.Equals(_plumbing.FixtureSleeves, "off", StringComparison.OrdinalIgnoreCase);
            foreach (var kv in _plumbing.Fixtures.OrderBy(kv => kv.Key))
            {
                var f = kv.Value;
                string fixtureResult = !review ? "not listed"
                    : string.Equals(_plumbing.FixtureSleeves, "model", StringComparison.OrdinalIgnoreCase)
                        ? "sleeve already in the model, else Revit sanitary connector when uniquely matched; otherwise review by stack"
                        : "for review";
                int i = _tags.Rows.Add(kv.Key, f.Name + Pdf(kv.Key), "rules.json plumbing.fixtures",
                                       $"fixture sleeve {(f.Count > 1 ? f.Count + " x " : "")}{Units.FormatInches(_plumbing.SleeveFor(f.Pipe))}: {fixtureResult}");
                if (review) _tags.Rows[i].DefaultCellStyle.BackColor = Color.LightYellow;
            }
            int sleeved = _plumbing.Services.Count(kv => kv.Value.Sleeve);
            _tagsPage.Text = $"Tag meanings ({_plumbing.Services.Count} services, {sleeved} get a sleeve)";
            msg.AppendLine($"Services: {string.Join(", ", _plumbing.Services.Where(kv => kv.Value.Sleeve).Select(kv => kv.Key))} get sleeves; " +
                           $"{string.Join(", ", _plumbing.Services.Where(kv => !kv.Value.Sleeve).Select(kv => kv.Key))} do not (rules.json plumbing.services).");
        }

        /// <summary>PL model: one row per pipe group (P1, P2...) and what it becomes.</summary>
        private void FillPipeGroups(DwgRiserResult risers, StringBuilder msg)
        {
            int tagged = 0, listed = 0, bare = 0;
            foreach (var r in risers.Risers.OrderBy(r => FloorKey.Order(r.Floor)).ThenBy(r => r.Tag ?? "~"))
            {
                var lines = PipeAssembler.Parse(r.TagTexts, _plumbing);
                var drawn = r.Symbols.Where(s => s.Block == DwgRiserReader.PipeCircle).Select(s => _plumbing.ServiceForLayer(s.Layer) ?? "?").ToList();
                string down = string.Join(", ", lines.Where(l => l.Down || l.Through).Select(l => l.Service));
                string up = string.Join(", ", lines.Where(l => l.Up).Select(l => l.Service));
                var sleeves = lines.Where(l => _plumbing.Services[l.Service].Sleeve).Select(l => l.Service).ToList();
                string result; Color back;
                if (r.Tag != null) tagged++;
                if (lines.Count > 0)
                {
                    listed++;
                    result = sleeves.Count > 0 ? $"sleeves: {string.Join(", ", sleeves.Select(s => _plumbing.Name(s, r.Tag)))}" : "no sleeved service";
                    back = sleeves.Count > 0 ? Color.Honeydew : SystemColors.Window;
                }
                else { bare++; result = r.Tag == null ? "no tag and no UP/DN list - reported" : "no UP/DN list next to the bubble - reported"; back = Color.MistyRose; }
                int i = _risers.Rows.Add(FloorKey.Describe(r.Floor), r.Tag ?? "(none)",
                                         lines.Count > 0 ? string.Join(" / ", lines.Select(l => l.Text)) : $"pipes drawn: {string.Join(", ", drawn)}",
                                         down, up, drawn.Count, $"{r.X:0}, {r.Y:0}", result, string.Join("; ", r.Evidence));
                _risers.Rows[i].DefaultCellStyle.BackColor = back;
            }
            foreach (var t in risers.LooseTags)
            {
                int i = _risers.Rows.Add(FloorKey.Describe(t.Floor), t.Tag, "", "", "", 0, $"{t.X:0}, {t.Y:0}", "bubble with no leader to a pipe group - reported", "");
                _risers.Rows[i].DefaultCellStyle.BackColor = Color.LightYellow;
            }
            _risersPage.Text = $"Pipe groups (DWG) ({risers.Risers.Count}, {listed} with services)";
            msg.AppendLine($"Pipe groups in the DWG: {risers.Risers.Count} on {risers.Risers.Select(r => r.Floor).Distinct().Count()} floor(s) - {tagged} tagged, {listed} with a service list, " +
                           $"{bare} drawn without one (reported); {risers.LooseTags.Count} bubble(s) not connected; {risers.Fixtures.Count} fixture(s) named on the plans. See the Pipe groups tab.");
            foreach (var w in risers.Warnings) msg.AppendLine("  ! " + w);
        }

        /// <summary>What each riser found in the DWG will become: an opening, nothing, or a report item.</summary>
        private void FillRisers(PdfSheetIndex pdf, DwgRiserResult risers, StringBuilder msg)
        {
            _risers.Rows.Clear();
            if (risers == null) { _risersPage.Text = Plumbing ? "Pipe groups (DWG)" : "Risers (DWG)"; return; }
            if (Plumbing) { FillPipeGroups(risers, msg); return; }
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
                    r.Ducts, $"{r.X:0}, {r.Y:0}", result, string.Join("; ", r.Evidence));
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
                string shift = f.Shift == null ? "" : PdfPlans != null ? (f.Map == null ? "" : $"turned {Math.Atan2(f.Map.Sin, f.Map.Cos) * 180 / Math.PI:0.##}°")
                             : $"{f.Shift.Dx:0.##}, {f.Shift.Dy:0.##}";
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
                int i = _openings.Rows.Add(FloorKey.Describe(c.Floor), c.Level ?? "(not used)", c.Tag == null ? (c.System == "DryerExhaust" ? "(dryer)" : "-") : c.Tag + c.DamperSuffix, c.System,
                                           c.Size?.ToString() ?? (c.SizedByRules ? "by rules" : "?"), c.Ducts,
                                           c.MergedInto != null ? "in combined opening" : c.Status == Crossing.Place ? "place" : c.Status == Crossing.Review ? "review" : "not placed",
                                           c.Rotation.HasValue ? $"{c.Rotation.Value * 180 / Math.PI:0}°" : "-",
                                           c.Confidence, c.Pdf ?? "-", SoFor(c), string.Join(" + ", c.From), string.Join("; ", c.Notes));
                _openings.Rows[i].DefaultCellStyle.BackColor = c.Status == Crossing.Place || c.MergedInto != null ? Color.Honeydew : c.Status == Crossing.Review ? Color.LightYellow : Color.MistyRose;
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
            var files = _inputs.For(_discipline);
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

            // the floors to place on, remembered by the floors left out (a floor new to the drawings comes in ticked)
            var ticked = TickedFloors();
            if (!ticked.Any(k => FloorLevels().ContainsKey(k)))
            {
                MessageBox.Show(this, "No floor is ticked to place on (Floors tab, Place column).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var keys = _floors.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells["Key"].Value).Where(k => k != null).ToList();
            _inputs.SkipFloors = (_inputs.SkipFloors ?? new List<string>()).Where(k => !keys.Contains(k))      // floors not in these drawings: kept as they were
                                 .Concat(keys.Where(k => !ticked.Contains(k))).Distinct().ToList();
            PlaceFloors = ticked.Count == keys.Count ? null : ticked;

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
