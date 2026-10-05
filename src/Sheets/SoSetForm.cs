using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SleevesOpenings.Sheets
{
    /// <summary>
    /// S&amp;O Set: what will happen to each floor's sheet, the title block and project fields, the notes for the notes table,
    /// this project's sheet naming, the PDF (folder, name, paper) and the S&amp;O templates (what the set starts from when the
    /// model has no S&amp;O sheet). Notes and settings are saved in the model; the fields live in the model itself.
    /// </summary>
    public class SoSetForm : Form
    {
        private SoSheetRules _office;
        private readonly Func<SoProjectSettings, SoSetPlan> _replan;
        private readonly string _project;
        private SoSetPlan _plan;

        private readonly Label _head;
        private readonly DataGridView _floors, _fields, _notes;
        private readonly TextBox _filter;
        private readonly CheckBox _changedOnly;
        private readonly List<SoField> _allFields;
        private readonly TextBox _prefix, _first, _sheetName, _scheduleName, _floorLabel, _pdfPattern;
        private readonly CheckBox _rename, _print;
        private readonly TextBox _folder, _name;
        private readonly ComboBox _paper;
        private readonly List<(string Value, string Text)> _papers = SoSheets.PaperChoices();
        private string _namingShown;
        private bool _nameTyped;
        private readonly Button _ok;

        // Templates tab
        private readonly string _modelPath;
        private readonly Func<string, List<string>> _saveTemplate;
        private readonly Func<SoSheetRules> _officeChanged;
        private string _projectTemplate;
        private ListView _templates;
        private Label _templateStatus;

        public List<SoNote> Notes { get; private set; } = new List<SoNote>();
        public List<SoField> Fields => _allFields;
        public SoProjectSettings Settings { get; private set; }
        public bool PrintPdf => _print.Checked;
        public string PdfFolderPath => _folder.Text.Trim();
        public string PdfFileName => _name.Text.Trim();

        public SoSetForm(SoSetPlan plan, IList<SoNote> notes, List<SoField> fields, SoSheetRules office, SoProjectSettings mine,
                         string pdfFolder, Func<SoProjectSettings, SoSetPlan> replan,
                         string modelPath = null, Func<string, List<string>> saveTemplate = null, Func<SoSheetRules> officeChanged = null)
        {
            _plan = plan; _office = office ?? new SoSheetRules(); _replan = replan; _project = plan.ProjectName;
            _modelPath = string.IsNullOrEmpty(modelPath) ? null : modelPath;
            _saveTemplate = saveTemplate; _officeChanged = officeChanged;
            _projectTemplate = string.IsNullOrWhiteSpace(mine?.Template) ? null : mine.Template.Trim();
            _allFields = fields ?? new List<SoField>();
            var cfg = SoProjectSettings.Apply(_office, mine);

            Text = "Sleeves & Openings — S&O Set";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            Width = 980; Height = 760; MinimumSize = new Size(760, 560);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            root.Controls.Add(tabs, 0, 0);

            // ---------------------------------------------------------------- Sheets
            var sheetsPage = Page(tabs, "Sheets");
            var sheetsLayout = Stack(sheetsPage, 2);
            _head = Note("");
            sheetsLayout.Controls.Add(_head, 0, 0);
            _floors = Grid(true);
            _floors.Columns.Add("Level", "Level");
            _floors.Columns.Add("Sheet", "Sheet");
            _floors.Columns.Add("Name", "Sheet name");
            _floors.Columns.Add("Action", "What happens");
            _floors.Columns["Action"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            sheetsLayout.Controls.Add(_floors, 0, 1);
            FillFloors();

            // ---------------------------------------------------------------- Title block & project
            var fieldsPage = Page(tabs, "Title block & project");
            var fieldsLayout = Stack(fieldsPage, 3);
            fieldsLayout.Controls.Add(Note(
                "Everything the title block shows that Revit can change: the project's information (name, address, client…), the sheet's fields " +
                "(drawn / checked / designed by, issue date), and the title block family's own fields (contractor, revision lines…). " +
                "Change a value and it is ticked: it is written to every S&O sheet (Title block type = all sheets of that type; Project = the whole model). " +
                "Tick an unchanged row to push the pattern sheet's value to all sheets. Text that is drawn in the family itself (not a label) cannot be changed here."), 0, 0);
            var filterRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            filterRow.Controls.Add(new Label { Text = "Find", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            _filter = new TextBox { Width = 260 };
            filterRow.Controls.Add(_filter);
            _changedOnly = new CheckBox { Text = "Ticked only", AutoSize = true, Padding = new Padding(10, 3, 0, 0) };
            filterRow.Controls.Add(_changedOnly);
            fieldsLayout.Controls.Add(filterRow, 0, 1);
            _fields = Grid(false);
            _fields.AllowUserToAddRows = _fields.AllowUserToDeleteRows = false;
            _fields.RowHeadersVisible = false;
            _fields.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Write", HeaderText = "Write", Width = 50, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            _fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "Owner", HeaderText = "Where", ReadOnly = true });
            _fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field", ReadOnly = true });
            _fields.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _fields.CellValueChanged += FieldEdited;
            _fields.CurrentCellDirtyStateChanged += (s, e) => { if (_fields.CurrentCell is DataGridViewCheckBoxCell) _fields.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            fieldsLayout.Controls.Add(_fields, 0, 2);
            _filter.TextChanged += (s, e) => FillFields();
            _changedOnly.CheckedChanged += (s, e) => FillFields();
            FillFields();
            if (_allFields.Count == 0) { _filter.Enabled = _changedOnly.Enabled = false; _filter.Text = ""; }

            // ---------------------------------------------------------------- Notes
            var notesPage = Page(tabs, "Notes");
            var notesLayout = Stack(notesPage, 2);
            notesLayout.Controls.Add(Note(
                "The NOTES TO ARCH, ENG, & G.C. table (the title block's revision table): one note per row. Date = the date shown (empty = the day it is first written). " +
                "Sheets = which sheets it goes on (e.g. SL101, SL103), empty = all. Saved in the model. Revisions the office added by hand are not touched."), 0, 0);
            _notes = Grid(false);
            _notes.Columns.Add("Text", "Note");
            _notes.Columns.Add("Date", "Date");
            _notes.Columns.Add("Sheets", "Sheets (empty = all)");
            _notes.Columns["Text"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _notes.Columns["Date"].Width = 90;
            _notes.Columns["Sheets"].Width = 200;
            foreach (var n in notes ?? new List<SoNote>()) _notes.Rows.Add(n.Text, n.Date ?? "", string.Join(", ", n.Sheets ?? new List<string>()));
            notesLayout.Controls.Add(_notes, 0, 1);

            // ---------------------------------------------------------------- Naming
            var namingPage = Page(tabs, "Naming");
            var naming = Form2(namingPage);
            _prefix = Row(naming, "Sheet number prefix", cfg.NumberPrefix);
            _first = Row(naming, "First sheet number", cfg.FirstNumber.ToString());
            _sheetName = Row(naming, "Sheet name", cfg.SheetName);
            _scheduleName = Row(naming, "Schedule name", cfg.ScheduleName);
            _floorLabel = Row(naming, "Floor label (red text under the plan)", cfg.FloorLabel);
            _rename = new CheckBox { Text = "Rename sheets that already exist to this name (off: names typed by hand are kept)", AutoSize = true, Checked = cfg.RenameExisting };
            naming.Controls.Add(_rename, 1, naming.RowCount++);
            naming.Controls.Add(Note(
                "{Floor} = 1st Floor / Roof    {FLOOR} = 1ST FLOOR / ROOF    {ord} = 1st, 2nd…    {index} = the sheet's place in the set (1st, 2nd…)    " +
                "{kind} = floor / roof / bulkhead / cellar    {level} = the Revit level name.\n" +
                "Saved for this project only; empty = the office default (rules.json). Numbers already given to sheets stay; new sheets follow these. " +
                "The Sheets tab shows the result."), 1, naming.RowCount++);
            var reset = new Button { Text = "Office defaults", AutoSize = true };
            reset.Click += (s, e) =>
            {
                _prefix.Text = _office.NumberPrefix; _first.Text = _office.FirstNumber.ToString(); _sheetName.Text = _office.SheetName;
                _scheduleName.Text = _office.ScheduleName; _floorLabel.Text = _office.FloorLabel; _rename.Checked = _office.RenameExisting;
            };
            naming.Controls.Add(reset, 1, naming.RowCount++);
            _namingShown = NamingKey();

            // ---------------------------------------------------------------- PDF
            var pdfPage = Page(tabs, "PDF");
            var pdf = Form2(pdfPage);
            _print = new CheckBox { Text = "Print the set to one PDF", Checked = true, AutoSize = true };
            pdf.Controls.Add(_print, 1, pdf.RowCount++);
            _folder = Row(pdf, "Folder", pdfFolder ?? "");
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
            };
            pdf.Controls.Add(browse, 2, pdf.RowCount - 1);
            _pdfPattern = Row(pdf, "File name pattern", cfg.PdfName);
            _name = Row(pdf, "File name", SoSheets.PdfName(cfg, _project));
            pdf.Controls.Add(new Label { Text = ".pdf", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 2, pdf.RowCount - 1);
            _name.KeyPress += (s, e) => _nameTyped = true;
            _pdfPattern.TextChanged += (s, e) =>
            {
                if (!_nameTyped) _name.Text = SoSheets.PdfName(SoProjectSettings.Apply(_office, new SoProjectSettings { PdfName = _pdfPattern.Text }), _project);
            };
            pdf.Controls.Add(new Label { Text = "Paper", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, pdf.RowCount);
            _paper = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            foreach (var p in _papers) _paper.Items.Add(p.Text);
            int at = _papers.FindIndex(p => p.Value.Equals(cfg.PaperSize ?? "auto", StringComparison.OrdinalIgnoreCase));
            _paper.SelectedIndex = at >= 0 ? at : 0;
            pdf.Controls.Add(_paper, 1, pdf.RowCount++);
            pdf.Controls.Add(Note(
                "{project} = the model's name without its discipline code; {date} = today. " +
                "Auto paper: the smallest standard size the title block fits on, printed at 100% and centred (24 Skillman: ARCH D 24 x 36, as the office's sets)."), 1, pdf.RowCount++);
            _print.CheckedChanged += (s, e) => { _folder.Enabled = _name.Enabled = browse.Enabled = _pdfPattern.Enabled = _paper.Enabled = _print.Checked; };

            BuildTemplatesTab(Page(tabs, "Templates"));

            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedTab == sheetsPage) Replan(); };

            // ---------------------------------------------------------------- buttons
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = _ok = new Button { Text = "Make / update the sheets", AutoSize = true, Enabled = CanMake };
            ok.Click += (s, e) =>
            {
                _fields.EndEdit(); _notes.EndEdit();
                if (_print.Checked && (PdfFolderPath.Length == 0 || PdfFileName.Length == 0))
                {
                    tabs.SelectedTab = pdfPage;
                    MessageBox.Show(this, "Choose the PDF folder and file name, or untick Print.", Text);
                    return;
                }
                if (!int.TryParse(_first.Text.Trim(), out _))
                {
                    tabs.SelectedTab = namingPage;
                    MessageBox.Show(this, "The first sheet number must be a number (101).", Text);
                    return;
                }
                Settings = ReadSettings();
                Notes = _notes.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
                    .Select(r => new SoNote
                    {
                        Text = (r.Cells["Text"].Value as string ?? "").Trim(),
                        Date = (r.Cells["Date"].Value as string ?? "").Trim(),
                        Sheets = (r.Cells["Sheets"].Value as string ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).ToList()
                    })
                    .Where(n => n.Text.Length > 0).ToList();
                foreach (var n in Notes) if (n.Date.Length == 0) n.Date = null;
                DialogResult = DialogResult.OK;
                Close();
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons, 0, 1);
            AcceptButton = null; CancelButton = cancel;                                   // Enter edits grid cells, not OK
        }

        // ---------------------------------------------------------------- sheets tab

        private void FillFloors()
        {
            string head = _plan.Pattern != null ? $"Pattern: {_plan.Pattern.Describe()}"
                : _plan.Template != null ? $"No S&O sheet in this model yet: the first sheet is made from the template '{_plan.Template.Name}' ({_plan.Template.Where}), the others copy it.\n{_plan.Template.Path}"
                : "No pattern sheet and no template: nothing can be made.";
            if (_plan.Problems.Count > 0) head += "\n" + string.Join("\n", _plan.Problems.Select(p => "! " + p));
            _head.Text = head;
            _floors.Rows.Clear();
            foreach (var f in _plan.Floors)
            {
                int r = _floors.Rows.Add(f.Level.Name, f.Number, f.Name, f.Action + (f.IsPattern ? " (pattern)" : ""));
                if (f.Sheet == null) _floors.Rows[r].DefaultCellStyle.BackColor = Color.Honeydew;
                else if (f.Missing.Count > 0) _floors.Rows[r].DefaultCellStyle.BackColor = Color.LightYellow;
            }
        }

        private bool CanMake => (_plan.Pattern != null || _plan.Template != null) && _plan.Floors.Count > 0;

        private string NamingKey() => string.Join("|", _prefix.Text, _first.Text, _sheetName.Text, _scheduleName.Text, _floorLabel.Text, _rename.Checked, _projectTemplate);

        /// <summary>The naming changed: the sheets tab shows the set as it will be made with it.</summary>
        private void Replan()
        {
            if (_replan == null || NamingKey() == _namingShown || !int.TryParse(_first.Text.Trim(), out _)) return;
            _namingShown = NamingKey();
            try { _plan = _replan(ReadSettings()); FillFloors(); if (_ok != null) _ok.Enabled = CanMake; }
            catch (Exception ex) { App.Log("S&O set: replan failed: " + ex); }
        }

        /// <summary>Only what differs from rules.json is kept, so office changes still reach the project.</summary>
        private SoProjectSettings ReadSettings()
        {
            string Own(string mine, string office) => string.IsNullOrWhiteSpace(mine) || mine == office ? null : mine;
            int.TryParse(_first.Text.Trim(), out int first);
            string paper = _papers[Math.Max(0, _paper.SelectedIndex)].Value;
            return new SoProjectSettings
            {
                NumberPrefix = Own(_prefix.Text.Trim(), _office.NumberPrefix),
                FirstNumber = first > 0 && first != _office.FirstNumber ? first : (int?)null,
                SheetName = Own(_sheetName.Text, _office.SheetName),
                ScheduleName = Own(_scheduleName.Text, _office.ScheduleName),
                FloorLabel = Own(_floorLabel.Text, _office.FloorLabel),
                PdfName = Own(_pdfPattern.Text, _office.PdfName),
                PaperSize = Own(paper, _office.PaperSize ?? "auto"),
                RenameExisting = _rename.Checked != _office.RenameExisting ? _rename.Checked : (bool?)null,
                Template = _projectTemplate
            };
        }

        // ---------------------------------------------------------------- templates tab

        private void BuildTemplatesTab(TabPage page)
        {
            var layout = Stack(page, 4);
            layout.Controls.Add(Note(
                "A template is a small Revit file holding one S&O sheet: the title block, a Sleeves plan, the legend, the SL- schedule and the floor label. " +
                "It is used when this model has no S&O sheet yet: the first sheet is built from it (title block, legend and schedule copied into the model), " +
                "the others copy that sheet. A model that already has an S&O sheet always uses its own.\n" +
                "Built-in = installed with the add-in (every computer). Office = this computer's template folder. This project = the 'SO Templates' folder " +
                "next to the model (everyone opening the project has it)."), 0, 0);
            _templateStatus = Note("");
            layout.Controls.Add(_templateStatus, 0, 1);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var tips = new ToolTip();
            Button Btn(string text, Action click, string tip = null)
            {
                var b = new Button { Text = text, AutoSize = true };
                b.Click += (s, e) =>
                {
                    try { click(); }
                    catch (Exception ex) { App.Log("S&O templates: " + ex); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                if (tip != null) tips.SetToolTip(b, tip);
                buttons.Controls.Add(b);
                return b;
            }
            Btn("Use for this project", UseForProject, "This project starts from the selected template. An office template is copied next to the model so everyone opening the project has it.");
            Btn("Follow the office default", () => { _projectTemplate = null; TemplatesChanged(); });
            Btn("Make office default", MakeOfficeDefault, "Every project without its own choice uses the selected template.");
            var save = Btn("Save this model's S&O sheet as template…", SaveTemplate, "A new template from this model's S&O sheet (SL101): title block, legend, schedule, floor label, positions.");
            save.Enabled = _saveTemplate != null;
            Btn("Add template file…", AddTemplate, "Add an .rvt holding one S&O sheet (sent by someone else) to the office templates.");
            Btn("Remove", RemoveTemplate);
            Btn("Open folder", () => { Directory.CreateDirectory(SoTemplates.OfficeFolder); Process.Start(new ProcessStartInfo(SoTemplates.OfficeFolder) { UseShellExecute = true }); });
            layout.Controls.Add(buttons, 0, 2);

            _templates = new ListView { Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            _templates.Columns.Add("Template", 240);
            _templates.Columns.Add("Where", 100);
            _templates.Columns.Add("Used", 200);
            _templates.Columns.Add("File", 600);
            _templates.DoubleClick += (s, e) => UseForProject();
            layout.Controls.Add(_templates, 0, 3);
            FillTemplates();
        }

        private SoTemplate Selected => _templates.SelectedItems.Count > 0 ? _templates.SelectedItems[0].Tag as SoTemplate : null;

        private SoTemplate SelectedOrSay()
        {
            var t = Selected;
            if (t == null) MessageBox.Show(this, "Select a template in the list first.", Text);
            return t;
        }

        private void FillTemplates()
        {
            string keep = Selected?.Path;
            var used = SoTemplates.Resolve(_projectTemplate ?? _office.Template, _modelPath, out string why);
            var officeDefault = SoTemplates.Resolve(_office.Template, _modelPath, out _);
            _templates.Items.Clear();
            foreach (var t in SoTemplates.List(_modelPath))
            {
                var marks = new List<string>();
                bool here = used != null && Same(used.Path, t.Path);
                if (here) marks.Add("● this project");
                if (officeDefault != null && Same(officeDefault.Path, t.Path)) marks.Add("office default");
                var item = new ListViewItem(new[] { t.Name, t.Where, string.Join(", ", marks), t.Path }) { Tag = t };
                if (here) item.Font = new Font(_templates.Font, FontStyle.Bold);
                _templates.Items.Add(item);
                if (keep != null && Same(keep, t.Path)) item.Selected = true;
            }
            string whose = _projectTemplate != null ? "chosen for this project" : "the office default";
            _templateStatus.Text = (_plan.Pattern != null ? $"This model has its own S&O sheet ({_plan.Pattern.Sheet.SheetNumber}): it is the pattern, no template is needed.\n" : "") +
                                   (used != null ? $"Template used here ({whose}): {used.Name} ({used.Where})" : "No template: " + why);
        }

        private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private void TemplatesChanged()
        {
            FillTemplates();
            Replan();
        }

        private void UseForProject()
        {
            var t = SelectedOrSay();
            if (t == null) return;
            if (t.Where == SoTemplate.Office && SoTemplates.ProjectFolder(_modelPath) is string folder)
            {
                // next to the model: everyone opening the project has it, not only this computer
                Directory.CreateDirectory(folder);
                File.Copy(t.Path, Path.Combine(folder, Path.GetFileName(t.Path)), true);
            }
            _projectTemplate = t.Name;
            TemplatesChanged();
        }

        private void MakeOfficeDefault()
        {
            var t = SelectedOrSay();
            if (t == null) return;
            if (t.Where == SoTemplate.Project)
            {
                Directory.CreateDirectory(SoTemplates.OfficeFolder);
                File.Copy(t.Path, Path.Combine(SoTemplates.OfficeFolder, Path.GetFileName(t.Path)), true);
            }
            bool builtInDefault = t.Where == SoTemplate.BuiltIn && t.Name.Equals(SoTemplates.DefaultName, StringComparison.OrdinalIgnoreCase);
            var rules = Rules.RuleLayers.Effective(_modelPath, Rules.RuleLayers.Layer.Office);
            Rules.RuleLayers.Set(rules, "soSheets.template", builtInDefault ? null : new Newtonsoft.Json.Linq.JValue(t.Name));
            Rules.RuleLayers.Save(rules, _modelPath, Rules.RuleLayers.Layer.Office);
            _office = _officeChanged?.Invoke() ?? _office;
            if (!builtInDefault && _office.Template != t.Name)
                MessageBox.Show(this, "Saved as the office default, but this project's own rules name another template (Edit Rules > This project only > S&O sheets).", Text);
            TemplatesChanged();
        }

        private void SaveTemplate()
        {
            if (_saveTemplate == null) return;
            string name = Ask("Save as template", "Template name:", string.IsNullOrEmpty(_project) ? SoTemplates.DefaultName : _project + " S&O");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = string.Join("_", name.Trim().Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(SoTemplates.OfficeFolder, name + ".rvt");
            if (File.Exists(path) && MessageBox.Show(this, $"'{name}' already exists. Replace it?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            List<string> problems;
            Cursor = Cursors.WaitCursor;
            try { problems = _saveTemplate(path); }
            finally { Cursor = Cursors.Default; }
            FillTemplates();
            foreach (ListViewItem item in _templates.Items) item.Selected = Same(((SoTemplate)item.Tag).Path, path);
            var msg = $"Template saved:\n{path}" + (problems.Count > 0 ? "\n\nPlease check:\n• " + string.Join("\n• ", problems) : "") +
                      "\n\nMake it the office default (projects with no S&O sheet start from it)?";
            if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information) == DialogResult.Yes)
                MakeOfficeDefault();
        }

        private void AddTemplate()
        {
            using (var dlg = new OpenFileDialog { Filter = "Revit project (*.rvt)|*.rvt", Title = "An .rvt holding one S&O sheet" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Directory.CreateDirectory(SoTemplates.OfficeFolder);
                var to = Path.Combine(SoTemplates.OfficeFolder, Path.GetFileName(dlg.FileName));
                if (File.Exists(to) && MessageBox.Show(this, $"'{Path.GetFileNameWithoutExtension(to)}' already exists. Replace it?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                File.Copy(dlg.FileName, to, true);
                FillTemplates();
            }
        }

        private void RemoveTemplate()
        {
            var t = SelectedOrSay();
            if (t == null) return;
            if (t.Where == SoTemplate.BuiltIn) { MessageBox.Show(this, "The built-in template comes with the add-in and cannot be removed.", Text); return; }
            if (MessageBox.Show(this, $"Delete the template '{t.Name}' ({t.Where})?\n{t.Path}", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            File.Delete(t.Path);
            if (_projectTemplate != null && !SoTemplates.List(_modelPath).Any(x => x.Name.Equals(_projectTemplate, StringComparison.OrdinalIgnoreCase)))
                _projectTemplate = null;
            TemplatesChanged();
        }

        private string Ask(string title, string prompt, string value)
        {
            using (var f = new Form { Text = title, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Width = 460, Height = 160 })
            {
                var label = new Label { Text = prompt, Left = 12, Top = 14, AutoSize = true };
                var box = new TextBox { Left = 12, Top = 38, Width = 420, Text = value };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 266, Top = 74, Width = 80 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 352, Top = 74, Width = 80 };
                f.Controls.AddRange(new Control[] { label, box, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
            }
        }

        // ---------------------------------------------------------------- fields tab

        private bool _filling;

        private void FillFields()
        {
            _filling = true;
            _fields.Rows.Clear();
            string find = _filter.Text.Trim();
            foreach (var f in _allFields)
            {
                if (_changedOnly.Checked && !f.Write) continue;
                if (find.Length > 0 && f.Name.IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0 && (f.Value ?? "").IndexOf(find, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int r = _fields.Rows.Add(f.Write, f.Owner, f.Name, f.Value);
                var row = _fields.Rows[r];
                row.Tag = f;
                if (f.YesNo || f.Integer) row.Cells["Value"].ToolTipText = f.YesNo ? "Yes / No" : "A whole number";
                Shade(row);
            }
            _filling = false;
        }

        private void FieldEdited(object sender, DataGridViewCellEventArgs e)
        {
            if (_filling || e.RowIndex < 0 || !(_fields.Rows[e.RowIndex].Tag is SoField f)) return;
            var row = _fields.Rows[e.RowIndex];
            if (_fields.Columns[e.ColumnIndex].Name == "Value")
            {
                f.Value = row.Cells["Value"].Value as string ?? "";
                if (f.YesNo) f.Value = System.Text.RegularExpressions.Regex.IsMatch(f.Value.Trim(), "^(yes|y|true|1|on|x)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "Yes" : "No";
                f.Write = f.Value != f.Original || f.Write;
                _filling = true;
                row.Cells["Value"].Value = f.Value;
                row.Cells["Write"].Value = f.Write;
                _filling = false;
            }
            else if (_fields.Columns[e.ColumnIndex].Name == "Write")
                f.Write = row.Cells["Write"].Value is bool b && b;
            Shade(row);
        }

        private static void Shade(DataGridViewRow row)
        {
            var f = (SoField)row.Tag;
            row.DefaultCellStyle.BackColor = f.Write ? Color.LightYellow : SystemColors.Window;
        }

        // ---------------------------------------------------------------- layout helpers

        private static TabPage Page(TabControl tabs, string text)
        {
            var page = new TabPage(text) { Padding = new Padding(8) };
            tabs.TabPages.Add(page);
            return page;
        }

        /// <summary>Label rows on top, the last row (a grid) taking the rest.</summary>
        private static TableLayoutPanel Stack(TabPage page, int rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows };
            for (int i = 0; i < rows - 1; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(t);
            return t;
        }

        /// <summary>Label | box | extra, one setting per row.</summary>
        private static TableLayoutPanel Form2(TabPage page)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, RowCount = 0 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            page.Controls.Add(t);
            return t;
        }

        private static TextBox Row(TableLayoutPanel t, string label, string value)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, t.RowCount);
            var box = new TextBox { Dock = DockStyle.Fill, Text = value ?? "" };
            t.Controls.Add(box, 1, t.RowCount);
            t.RowCount++;
            return box;
        }

        private static Label Note(string text) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new Size(900, 0), Padding = new Padding(0, 4, 0, 6), UseMnemonic = false };

        private static DataGridView Grid(bool readOnly) => new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = readOnly, AllowUserToAddRows = !readOnly, AllowUserToDeleteRows = !readOnly,
            RowHeadersVisible = !readOnly, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells, BackgroundColor = SystemColors.Window,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
    }
}
