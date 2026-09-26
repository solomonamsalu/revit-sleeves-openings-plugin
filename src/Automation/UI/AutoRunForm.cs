using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SleevesOpenings.Automation.Drawings;
using SleevesOpenings.Automation.Legend;
using SleevesOpenings.Setup;

namespace SleevesOpenings.Automation.UI
{
    /// <summary>
    /// Auto Run steps 1-2: shows what is already in the model (and what to do with it), takes the engineer's
    /// PDF + DWG, reads them and matches every drawing floor to a Revit level. Nothing in the model changes here.
    /// </summary>
    public class AutoRunForm : Form
    {
        private readonly ExistingReport _existing;
        private readonly AutomationInputs _inputs;
        private readonly LevelMap _levels;
        private readonly LegendRules _legendRules;
        private readonly DwgProfile _profile;

        private RadioButton _keep, _update;
        private TextBox _pdf, _dwg, _messages;
        private DataGridView _floors, _tags, _risers;
        private TabPage _tagsPage, _floorsPage, _risersPage;
        private Button _check, _ok;
        private Label _status;

        /// <summary>Results of the last successful check (for the next steps).</summary>
        public PdfSheetIndex Pdf { get; private set; }
        public DwgSheetIndex Dwg { get; private set; }
        public DwgRiserResult Risers { get; private set; }

        public AutoRunForm(ExistingReport existing, AutomationInputs inputs, LevelMap levels, LegendRules legendRules, DwgProfile profile)
        {
            _existing = existing; _inputs = inputs; _levels = levels; _legendRules = legendRules; _profile = profile ?? new DwgProfile();
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
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            var files = _inputs.For(AutomationInputs.Mechanical);
            _pdf = FileRow(grid, 0, "PDF", files.Pdf, "PDF drawing set (*.pdf)|*.pdf");
            _dwg = FileRow(grid, 1, "DWG", files.Dwg, "AutoCAD drawing (*.dwg)|*.dwg");
            var checkRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            _check = new Button { Text = "Check drawings", AutoSize = true };
            _check.Click += async (s, e) => await CheckAsync();
            _status = new Label { AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
            checkRow.Controls.Add(_check);
            checkRow.Controls.Add(_status);
            grid.Controls.Add(checkRow, 1, 2);
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
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var found = new TabPage("What was found");
            found.Controls.Add(_messages);
            _tagsPage = new TabPage("Tag meanings");
            _tagsPage.Controls.Add(_tags);
            tabs.TabPages.Add(_floorsPage);
            tabs.TabPages.Add(_tagsPage);
            tabs.TabPages.Add(_risersPage);
            tabs.TabPages.Add(found);
            root.Controls.Add(tabs, 0, 2);

            // ---- Buttons
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            _ok = new Button { Text = "Save and continue", AutoSize = true, Enabled = false };
            _ok.Click += (s, e) => { if (Save()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            root.Controls.Add(buttons, 0, 3);
            CancelButton = cancel;

            Shown += async (s, e) => { if (File.Exists(_pdf.Text) || File.Exists(_dwg.Text)) await CheckAsync(); };
        }

        private TextBox FileRow(TableLayoutPanel grid, int row, string label, string value, string filter)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            var box = new TextBox { Dock = DockStyle.Fill, Text = value ?? "" };
            box.TextChanged += (s, e) => _ok.Enabled = false;          // a changed path must be checked again
            grid.Controls.Add(box, 1, row);
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = filter, Title = "Select the mechanical " + label })
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
            string pdfPath = _pdf.Text.Trim(), dwgPath = _dwg.Text.Trim();
            var msg = new StringBuilder();
            if (pdfPath.Length == 0 && dwgPath.Length == 0) { _messages.Text = "Select the mechanical PDF and DWG."; return; }

            _check.Enabled = _ok.Enabled = false;
            _status.Text = "Reading drawings… (a large PDF can take 20 seconds)";
            UseWaitCursor = true;
            PdfSheetIndex pdf = null; DwgSheetIndex dwg = null; DwgRiserResult risers = null;
            try
            {
                var tp = pdfPath.Length == 0 ? Task.FromResult<PdfSheetIndex>(null) : Task.Run(() => PdfSheetIndex.Read(pdfPath));
                var profile = _profile;
                var td = dwgPath.Length == 0 ? Task.FromResult<(DwgSheetIndex, DwgRiserResult)>((null, null)) : Task.Run(() =>
                {
                    var cad = DwgSheetIndex.Open(dwgPath);               // one read for floors and risers
                    var index = DwgSheetIndex.Read(cad, dwgPath);
                    return (index, DwgRiserReader.Read(cad, index, profile));
                });
                try { pdf = await tp; } catch (Exception ex) { msg.AppendLine($"PDF could not be read: {ex.Message}"); App.Log("AutoRun PDF read failed: " + ex); }
                try { (dwg, risers) = await td; } catch (Exception ex) { msg.AppendLine($"DWG could not be read: {ex.Message}"); App.Log("AutoRun DWG read failed: " + ex); }
            }
            finally
            {
                UseWaitCursor = false;
                _check.Enabled = true;
                _status.Text = "";
            }

            Pdf = pdf; Dwg = dwg; Risers = risers;
            Describe(pdf, dwg, pdfPath, dwgPath, msg);
            FillFloors(pdf, dwg);
            FillTags(pdf, msg);
            FillRisers(pdf, risers, msg);
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
                string meaning, result, tag = r.Tag, why = null; Color? back = null;
                DuctSize nominal = null;
                var fromLabels = r.Tag == null ? SleevesOpenings.Automation.Legend.Legend.FromLabels(r.Labels, l => l.Text, _legendRules) : null;
                if (r.Tag != null)
                {
                    var m = legend.Meaning(r.Tag, _legendRules);
                    meaning = m.Entry == null ? "-" : $"{m.Entry.Definition} ({m.Entry.Source})";
                    result = m.Describe();
                    if (m.Category == TagCategory.Opening) { openings++; back = Color.Honeydew; }
                    else if (m.Category == TagCategory.Undefined) { undefined++; back = Color.MistyRose; result = "tag not defined in the PDF - reported"; }
                    else none++;
                }
                else if (fromLabels != null)
                {
                    var (from, m) = fromLabels.Value;
                    // No bubble: the leader text says what it is, by the same legend categories as tag definitions.
                    // Meaning shows only what the rule decided (its name) and the directions the note states; the
                    // full note and the words that matched are under How it was read.
                    tag = "(from label)";
                    meaning = $"{m.Name ?? m.Matched}{Directions(from)} (note)";
                    why = $"'{m.Matched}' in the note = {m.Name ?? m.Matched}";
                    result = m.Describe();
                    if (m.Category == TagCategory.Opening)
                    {
                        openings++; back = Color.Honeydew; nominal = from.Nominal;
                        var systems = r.Labels.Select(l => SleevesOpenings.Automation.Legend.Legend.MeaningOfText(l.Text, _legendRules))
                                              .Where(x => x.Category == TagCategory.Opening).Select(x => x.System).Distinct().ToList();
                        if (systems.Count > 1) result += " - labels disagree: " + string.Join(", ", systems);
                    }
                    else none++;
                }
                else
                {
                    tag = "(none)";
                    // Nothing decided: say so, without guessing from the words. The note itself is under How it was read.
                    meaning = r.Labels.Count == 0 ? "-" : r.Labels.Any(l => l.Descriptive) ? "note not recognised" : "size only, no service named";
                    result = "tag missing - reported, not placed"; missing++; back = Color.MistyRose;
                }

                var label = r.Label;
                string down = label?.Down?.ToString() ?? (label?.GoesDown == true ? "?" : ""), up = label?.Up?.ToString() ?? (label?.GoesUp == true ? "?" : "");
                if (nominal != null && label?.Down == null && label?.Up == null) down = up = nominal.ToString();
                else if (down.Length == 0 && up.Length == 0 && r.Drawn != null) down = up = $"{r.Drawn} (drawn)";
                int i = _risers.Rows.Add(FloorKey.Describe(r.Floor), tag, meaning, down, up,
                    r.Ducts, $"{r.X:0}, {r.Y:0}", result, string.Join("; ", why == null ? r.Evidence : new[] { why }.Concat(r.Evidence)));
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

        /// <summary>The directions a note states, as the engineer wrote them: " DN & UP", " UP", " DN", or nothing.</summary>
        private static string Directions(RiserLabel l) =>
            l.GoesDown && l.GoesUp ? " DN & UP" : l.GoesUp ? " UP" : l.GoesDown ? " DN" : "";

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
            return true;
        }

    }
}
