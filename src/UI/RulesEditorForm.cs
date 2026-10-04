using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SleevesOpenings.Rules;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Edit Rules: every rule of rules.json as labelled fields and tables (rows can be added, deleted and reordered,
    /// and imported from / exported to CSV for Excel). Saved as the office layer or this project's layer of
    /// <see cref="RuleLayers"/>, holding only what differs from the layer below.
    /// </summary>
    public class RulesEditorForm : System.Windows.Forms.Form
    {
        private abstract class RuleBinding
        {
            public string Path, Where;
            public int Page;
            public Label Caption;
            public abstract void Show(JToken value);
            /// <summary>The value to write; adds to <paramref name="errors"/> when it cannot be written.</summary>
            public abstract JToken Read(List<string> errors);
        }

        private class FieldBinding : RuleBinding
        {
            public RuleField Field;
            public Control Input;
            public bool EmptyIsNull;

            public override void Show(JToken v)
            {
                switch (Field.Kind)
                {
                    case RuleKind.Bool: ((CheckBox)Input).Checked = v != null && v.Type == JTokenType.Boolean && (bool)v; break;
                    case RuleKind.Choice:
                        var cb = (ComboBox)Input;
                        var value = IsNull(v) ? null : Str(v);
                        var choice = Field.Choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
                        if (choice == null && Field.AllowOther) { cb.SelectedIndex = -1; cb.Text = value ?? ""; break; }
                        if (choice == null) { choice = new RuleChoice(value, value ?? "(not set)"); cb.Items.Add(choice); }
                        cb.SelectedItem = choice;
                        break;
                    case RuleKind.Words: case RuleKind.Numbers:
                        Input.Text = v is JArray a ? string.Join(", ", a.Select(Str)) : ""; break;
                    case RuleKind.Lines:
                        Input.Text = v is JArray l ? string.Join(Environment.NewLine, l.Select(Str)) : ""; break;
                    default: Input.Text = Str(v); break;
                }
            }

            public override JToken Read(List<string> errors)
            {
                string text = Input.Text.Trim();
                switch (Field.Kind)
                {
                    case RuleKind.Bool: return ((CheckBox)Input).Checked;
                    case RuleKind.Inches: case RuleKind.Number: case RuleKind.Int: case RuleKind.OptionalNumber:
                        if (text.Length == 0 && Field.Kind == RuleKind.OptionalNumber) return JValue.CreateNull();
                        if (!TryNumber(text, out var d)) { errors.Add($"{Where}: '{text}' is not a number."); return null; }
                        if (Field.Kind == RuleKind.Int && Math.Abs(d - Math.Round(d)) > 1e-9) { errors.Add($"{Where}: needs a whole number."); return null; }
                        return Num(d);
                    case RuleKind.Choice:
                        var cb = (ComboBox)Input;
                        if (cb.SelectedItem is RuleChoice c) return c.Value == null ? JValue.CreateNull() : new JValue(c.Value);
                        return text.Length == 0 ? JValue.CreateNull() : new JValue(text);
                    case RuleKind.Words:
                        return new JArray(text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0));
                    case RuleKind.Lines:
                        return new JArray(Input.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0));
                    case RuleKind.Numbers:
                        var list = new JArray();
                        foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!TryNumber(part, out var n)) { errors.Add($"{Where}: '{part.Trim()}' is not a number."); return null; }
                            list.Add(Num(n));
                        }
                        return list;
                    case RuleKind.Regex:
                        if (!ValidPattern(text, out var why)) { errors.Add($"{Where}: the pattern is not valid ({why})."); return null; }
                        return text.Length == 0 && EmptyIsNull ? JValue.CreateNull() : new JValue(text);
                    case RuleKind.OptionalText:
                        return text.Length == 0 ? JValue.CreateNull() : new JValue(Input.Text);
                    default:
                        return text.Length == 0 && EmptyIsNull ? JValue.CreateNull() : new JValue(Input.Text);
                }
            }
        }

        private class TableBinding : RuleBinding
        {
            public RuleTable Table;
            public DataGridView Grid;
            public const string KeyColumn = "__key";

            public override void Show(JToken v)
            {
                Grid.Rows.Clear();
                if (Table.Shape == TableShape.List)
                {
                    foreach (var item in (v as JArray ?? new JArray()).OfType<JObject>()) AddRow(null, item);
                }
                else
                {
                    foreach (var prop in (v as JObject ?? new JObject()).Properties())
                        if (!prop.Name.StartsWith("_")) AddRow(prop.Name, prop.Value);
                }
                FitHeight();
            }

            public int AddRow(string key, JToken item)
            {
                int i = Grid.Rows.Add();
                var row = Grid.Rows[i];
                if (Table.Shape != TableShape.List) row.Cells[KeyColumn].Value = key;
                if (Table.Shape == TableShape.Map) row.Cells[Table.Columns[0].Key].Value = Str(item);
                else
                {
                    row.Tag = item;
                    foreach (var col in Table.Columns) row.Cells[col.Key].Value = CellValue(col, item?[col.Key]);
                }
                return i;
            }

            private object CellValue(RuleColumn col, JToken v)
            {
                if (col.Kind == RuleKind.Bool) return v != null && v.Type == JTokenType.Boolean && (bool)v;
                if (col.Kind == RuleKind.Choice) return ChoiceLabel(col, IsNull(v) ? null : Str(v));
                return Str(v);
            }

            public string ChoiceLabel(RuleColumn col, string value)
            {
                var c = col.Choices.FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase) ||
                                                        string.Equals(x.Label, value, StringComparison.OrdinalIgnoreCase));
                if (c != null) return c.Label;
                if (string.IsNullOrEmpty(value)) return "";
                var cbc = (DataGridViewComboBoxColumn)Grid.Columns[col.Key];
                if (!cbc.Items.Contains(value)) cbc.Items.Add(value);
                return value;
            }

            public void FitHeight()
            {
                int rows = Grid.Rows.Count;
                int h = Grid.ColumnHeadersHeight + (rows + 1) * Grid.RowTemplate.Height + 6;
                Grid.Height = Math.Max(110, Math.Min(380, h));
            }

            public override JToken Read(List<string> errors)
            {
                var obj = new JObject();
                var list = new JArray();
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int n = 0, before = errors.Count;
                foreach (DataGridViewRow row in Grid.Rows)
                {
                    if (row.IsNewRow || IsBlank(row)) continue;
                    n++;
                    string where = $"{Where}, row {n}";
                    string key = null;
                    if (Table.Shape != TableShape.List)
                    {
                        key = (row.Cells[KeyColumn].Value as string ?? "").Trim();
                        if (key.Length == 0) { errors.Add($"{where}: {Table.KeyHeader} is empty."); continue; }
                        if (!keys.Add(key)) { errors.Add($"{where}: {Table.KeyHeader} '{key}' is used twice."); continue; }
                    }
                    if (Table.Shape == TableShape.Map)
                    {
                        var col = Table.Columns[0];
                        var val = (row.Cells[col.Key].Value as string ?? "").Trim();
                        if (val.Length == 0 && col.Required) { errors.Add($"{where}: {col.Header} is empty."); continue; }
                        obj[key] = val;
                        continue;
                    }

                    var item = (row.Tag as JObject)?.DeepClone() as JObject ?? new JObject();
                    foreach (var col in Table.Columns)
                    {
                        var tok = CellToken(col, row.Cells[col.Key].Value, errors, where);
                        // an unticked box the row did not spell out stays left out (false is the default)
                        if (tok?.Type == JTokenType.Boolean && !(bool)tok && !col.Required && item[col.Key] == null) tok = null;
                        if (tok == null) item.Remove(col.Key);
                        else item[col.Key] = tok;
                    }
                    if (Table.Shape == TableShape.List) list.Add(item);
                    else obj[key] = item;
                }
                if (errors.Count > before) return null;
                return Table.Shape == TableShape.List ? (JToken)list : obj;
            }

            /// <summary>null = left out of the row.</summary>
            private JToken CellToken(RuleColumn col, object cell, List<string> errors, string where)
            {
                if (col.Kind == RuleKind.Bool) return cell is bool b && b;
                var text = (cell?.ToString() ?? "").Trim();
                if (text.Length == 0)
                {
                    if (col.Required) errors.Add($"{where}: {col.Header} is empty.");
                    return null;
                }
                switch (col.Kind)
                {
                    case RuleKind.Inches: case RuleKind.Number: case RuleKind.Int: case RuleKind.OptionalNumber:
                        if (!TryNumber(text, out var d)) { errors.Add($"{where}: {col.Header} '{text}' is not a number."); return null; }
                        if (col.Kind == RuleKind.Int && Math.Abs(d - Math.Round(d)) > 1e-9) { errors.Add($"{where}: {col.Header} needs a whole number."); return null; }
                        return Num(d);
                    case RuleKind.Regex:
                        if (!ValidPattern(text, out var why)) { errors.Add($"{where}: {col.Header} is not a valid pattern ({why})."); return null; }
                        return text;
                    case RuleKind.Choice:
                        var c = col.Choices.FirstOrDefault(x => x.Label == text || string.Equals(x.Value, text, StringComparison.OrdinalIgnoreCase));
                        return c == null ? text : c.Value;
                    default: return text;
                }
            }

            private bool IsBlank(DataGridViewRow row) =>
                row.Cells.Cast<DataGridViewCell>().All(c => c.Value == null || c.Value is bool b && !b || c.Value is string s && s.Trim().Length == 0);
        }

        private readonly string _modelPath;
        private readonly Action _onSaved;
        private readonly List<RulePage> _pages;
        private readonly List<Panel> _pagePanels = new List<Panel>();
        private readonly List<RuleBinding> _bindings = new List<RuleBinding>();
        private readonly HashSet<int> _changedPages = new HashSet<int>();
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };
        private RuleLayers.Layer _target;
        private JObject _effective, _lower;
        private bool _dirty, _loading;
        private int _currentPage = -1;

        private ListBox _nav;
        private CheckBox _showAdvanced;
        private ComboBox _targetBox;
        private Label _sources, _status;
        private Panel _host;
        private Button _reset;
        private readonly List<int> _navPages = new List<int>();

        /// <param name="modelPath">The open model's path (empty when it was never saved: then only office rules can be edited).</param>
        /// <param name="onSaved">Called after a save, to reload the rules the add-in uses.</param>
        public RulesEditorForm(string modelPath, Action onSaved)
        {
            _modelPath = string.IsNullOrEmpty(modelPath) ? null : modelPath;
            _onSaved = onSaved;
            _pages = RulesCatalog.Pages();
            var projectFile = RuleLayers.PathOf(RuleLayers.Layer.Project, _modelPath);
            _target = projectFile != null && File.Exists(projectFile) ? RuleLayers.Layer.Project : RuleLayers.Layer.Office;
            Build();
            LoadValues();
            ShowNav();
        }

        // ---------------------------------------------------------------- layout

        private void Build()
        {
            Text = "Sleeves & Openings — Rules";
            Width = 1240; Height = 820;
            MinimumSize = new Size(900, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 8, 10, 4) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.Controls.Add(new Label { UseMnemonic = false, Text = "Edit and save as:", AutoSize = true, Margin = new Padding(3, 7, 3, 3), Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            _targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
            _targetBox.Items.Add("Office rules (every project on this computer)");
            _targetBox.Items.Add(_modelPath == null ? "This project only (save the model first)" : "This project only (" + Path.GetFileName(_modelPath) + ")");
            _targetBox.SelectedIndex = _target == RuleLayers.Layer.Project ? 1 : 0;
            _targetBox.SelectedIndexChanged += (s, e) => OnTargetChanged();
            top.Controls.Add(_targetBox, 1, 0);
            _sources = new Label { UseMnemonic = false, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 7, 3, 3) };
            top.Controls.Add(_sources, 2, 0);
            var legend = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3),
                Text = "Rules stack up: the add-in's default, then the office rules, then this project's rules. " +
                       "Blue bold = different from the layer below (hover to see its value). Tables: type in the last empty row to add a rule."
            };
            top.Controls.Add(legend, 0, 1);
            top.SetColumnSpan(legend, 3);

            var left = new Panel { Dock = DockStyle.Left, Width = 210, Padding = new Padding(10, 6, 4, 6) };
            _nav = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Segoe UI", 10f), ItemHeight = 22 };
            _nav.SelectedIndexChanged += (s, e) => { if (_nav.SelectedIndex >= 0) ShowPage(_navPages[_nav.SelectedIndex]); };
            _showAdvanced = new CheckBox { Dock = DockStyle.Bottom, Text = "Show advanced (how the drawings are read)", AutoSize = false, Height = 40 };
            _showAdvanced.CheckedChanged += (s, e) => ShowNav();
            left.Controls.Add(_nav);
            left.Controls.Add(_showAdvanced);

            _host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 0) };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 6, 10, 10) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var leftButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            _reset = new Button { Text = "Reset this page", AutoSize = true };
            _reset.Click += (s, e) => ResetPage();
            var openFile = new Button { Text = "Open the file…", AutoSize = true };
            openFile.Click += (s, e) => OpenFile();
            _tips.SetToolTip(openFile, "For experts: open this layer's rules file in a text editor.");
            _status = new Label { UseMnemonic = false, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 8, 3, 3) };
            leftButtons.Controls.Add(_reset); leftButtons.Controls.Add(openFile); leftButtons.Controls.Add(_status);
            var rightButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var close = new Button { Text = "Close", Width = 90 };
            close.Click += (s, e) => Close();
            var save = new Button { Text = "Save", Width = 110, Font = new Font(Font, FontStyle.Bold) };
            save.Click += (s, e) => Save();
            rightButtons.Controls.Add(close); rightButtons.Controls.Add(save);
            bottom.Controls.Add(leftButtons, 0, 0);
            bottom.Controls.Add(rightButtons, 1, 0);

            Controls.Add(_host);
            Controls.Add(left);
            Controls.Add(bottom);
            Controls.Add(top);

            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in _pages)
                foreach (var item in page.Items)
                {
                    if (item is RuleField f) bound.Add(f.Path);
                    if (item is RuleTable t) bound.Add(t.Path);
                }
            var structure = RuleLayers.Effective(_modelPath, RuleLayers.Layer.Project);
            for (int i = 0; i < _pages.Count; i++)
            {
                var panel = BuildPage(_pages[i], i, structure, bound);
                _pagePanels.Add(panel);
                _host.Controls.Add(panel);
            }

            FormClosing += OnClosing;
            KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.S) { Save(); e.Handled = true; } };
        }

        private void ShowNav()
        {
            int keep = _currentPage;
            _nav.Items.Clear(); _navPages.Clear();
            for (int i = 0; i < _pages.Count; i++)
            {
                if (_pages[i].Advanced && !_showAdvanced.Checked) continue;
                _navPages.Add(i);
                _nav.Items.Add(NavText(i));
            }
            int at = _navPages.IndexOf(keep);
            _nav.SelectedIndex = at >= 0 ? at : 0;
        }

        private string NavText(int page) => (_changedPages.Contains(page) ? "● " : "    ") + (_pages[page].Advanced ? "⚙ " : "") + _pages[page].Title;

        private void ShowPage(int page)
        {
            if (page == _currentPage) return;
            if (_currentPage >= 0) _pagePanels[_currentPage].Visible = false;
            _currentPage = page;
            _pagePanels[page].Visible = true;
            _pagePanels[page].BringToFront();
        }

        private Panel BuildPage(RulePage page, int index, JObject structure, HashSet<string> bound)
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(4, 0, 20, 20) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddWide(grid, new Label { UseMnemonic = false, Text = page.Title, AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(3, 4, 3, 2) });
            if (!string.IsNullOrEmpty(page.Intro)) AddWide(grid, Note(page.Intro, 900));

            foreach (var item in page.Items)
            {
                if (item is RuleHeading h) AddHeading(grid, h.Text, h.Help);
                else if (item is RuleField f) AddField(grid, page, index, f, false);
                else if (item is RuleTable t) AddTable(grid, page, index, t);
            }
            foreach (var path in page.AutoPaths) AddAuto(grid, page, index, path, structure, bound, 0);

            panel.Controls.Add(grid);
            return panel;
        }

        private static void AddWide(TableLayoutPanel grid, Control c)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(c, 0, row);
            grid.SetColumnSpan(c, 3);
        }

        private static Label Note(string text, int width) =>
            new Label { UseMnemonic = false, Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 6) };

        private void AddHeading(TableLayoutPanel grid, string text, string help)
        {
            AddWide(grid, new Label { UseMnemonic = false, Text = text, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Color.FromArgb(40, 40, 40), Margin = new Padding(3, 14, 3, 2) });
            if (!string.IsNullOrEmpty(help)) AddWide(grid, Note(help, 900));
        }

        private void AddField(TableLayoutPanel grid, RulePage page, int index, RuleField f, bool emptyIsNull)
        {
            var caption = f.Label + (f.Kind == RuleKind.Inches ? " (in)" : "");
            var label = new Label { UseMnemonic = false, Text = caption, AutoSize = true, MaximumSize = new Size(260, 0), Margin = new Padding(3, 7, 3, 3) };
            Control input;
            switch (f.Kind)
            {
                case RuleKind.Bool: input = new CheckBox { AutoSize = true, Margin = new Padding(3, 6, 3, 3) }; break;
                case RuleKind.Choice:
                    var cb = new ComboBox { Width = 260, DropDownStyle = f.AllowOther ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
                    foreach (var c in f.Choices) cb.Items.Add(c);
                    input = cb; break;
                case RuleKind.Lines: input = new TextBox { Width = 300, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical }; break;
                case RuleKind.Inches: case RuleKind.Number: case RuleKind.Int: case RuleKind.OptionalNumber: input = new TextBox { Width = 90 }; break;
                default: input = new TextBox { Width = 300 }; break;
            }
            var help = new Label { UseMnemonic = false, Text = f.Help ?? "", AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 7, 3, 3) };

            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(input, 1, row);
            grid.Controls.Add(help, 2, row);

            var b = new FieldBinding { Path = f.Path, Field = f, Input = input, Caption = label, Page = index, EmptyIsNull = emptyIsNull, Where = $"{page.Title} › {f.Label.Trim()}" };
            _bindings.Add(b);
            if (input is CheckBox chk) chk.CheckedChanged += (s, e) => Changed(b);
            else if (input is ComboBox combo) { combo.SelectedIndexChanged += (s, e) => Changed(b); combo.TextChanged += (s, e) => Changed(b); }
            else input.TextChanged += (s, e) => Changed(b);
        }

        private void AddTable(TableLayoutPanel grid, RulePage page, int index, RuleTable t)
        {
            var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(3, 12, 3, 6) };
            var title = new Label { UseMnemonic = false, Text = t.Title, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 3, 2) };
            box.Controls.Add(title);
            if (!string.IsNullOrEmpty(t.Help)) box.Controls.Add(Note(t.Help, 900));

            var dgv = new DataGridView
            {
                Width = 920, Height = 160, AllowUserToAddRows = true, AllowUserToDeleteRows = true, AllowUserToResizeRows = false,
                RowHeadersWidth = 28, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2, SelectionMode = DataGridViewSelectionMode.CellSelect,
                ShowCellToolTips = true
            };
            dgv.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            if (t.Shape != TableShape.List)
                dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = TableBinding.KeyColumn, HeaderText = t.KeyHeader + " *", Width = 110, ToolTipText = "Must be unique." });
            foreach (var c in t.Columns)
            {
                DataGridViewColumn col;
                if (c.Kind == RuleKind.Bool) col = new DataGridViewCheckBoxColumn();
                else if (c.Kind == RuleKind.Choice)
                {
                    var cc = new DataGridViewComboBoxColumn { DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, FlatStyle = FlatStyle.Flat };
                    if (!c.Required) cc.Items.Add("");
                    foreach (var ch in c.Choices) cc.Items.Add(ch.Label);
                    col = cc;
                }
                else col = new DataGridViewTextBoxColumn();
                col.Name = c.Key;
                col.HeaderText = (t.Shape == TableShape.Map && string.IsNullOrEmpty(c.Header) ? "Value" : c.Header) + (c.Required ? " *" : "");
                col.Width = c.Width;
                col.ToolTipText = c.Help ?? "";
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                dgv.Columns.Add(col);
            }
            box.Controls.Add(dgv);

            var b = new TableBinding { Path = t.Path, Table = t, Grid = dgv, Caption = title, Page = index, Where = $"{page.Title} › {t.Title}" };
            _bindings.Add(b);

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
            Button Btn(string text, Action click, string tip = null)
            {
                var btn = new Button { Text = text, AutoSize = true };
                btn.Click += (s, e) => click();
                if (tip != null) _tips.SetToolTip(btn, tip);
                buttons.Controls.Add(btn);
                return btn;
            }
            Btn("Add rule", () => { int i = b.AddRow(null, null); dgv.CurrentCell = dgv.Rows[i].Cells[0]; dgv.BeginEdit(true); b.FitHeight(); Changed(b); });
            Btn("Delete selected", () => DeleteRows(b));
            if (t.Ordered)
            {
                Btn("Move up", () => MoveRow(b, -1), "Rows are tried top to bottom: the first match wins.");
                Btn("Move down", () => MoveRow(b, +1));
            }
            Btn("Import CSV…", () => ImportCsv(b), "Replace or add rows from a CSV file (e.g. saved from Excel). The first line must hold the column names.");
            Btn("Export CSV…", () => ExportCsv(b), "Save this table as a CSV file to edit in Excel.");
            box.Controls.Add(buttons);

            dgv.CellValueChanged += (s, e) => Changed(b);
            dgv.UserDeletedRow += (s, e) => { b.FitHeight(); Changed(b); };
            dgv.UserAddedRow += (s, e) => { b.FitHeight(); Changed(b); };
            dgv.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (dgv.IsCurrentCellDirty && (dgv.CurrentCell is DataGridViewCheckBoxCell || dgv.CurrentCell is DataGridViewComboBoxCell))
                    dgv.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            dgv.DataError += (s, e) => e.ThrowException = false;

            AddWide(grid, box);
        }

        /// <summary>A section shown with every value it holds, labelled from the names and comments in rules.json.</summary>
        private void AddAuto(TableLayoutPanel grid, RulePage page, int index, string path, JObject structure, HashSet<string> bound, int depth)
        {
            if (!(RuleLayers.Get(structure, path) is JObject obj)) return;
            if (depth == 0 && obj["_comment"]?.Type == JTokenType.String) AddWide(grid, Note((string)obj["_comment"], 900));

            foreach (var prop in obj.Properties().ToList())
            {
                if (prop.Name.StartsWith("_")) continue;
                var p = RuleLayers.Join(path, prop.Name);
                if (bound.Contains(p)) continue;
                var help = obj["_" + prop.Name]?.Type == JTokenType.String ? (string)obj["_" + prop.Name] : null;

                var spec = page.AutoTables.FirstOrDefault(t => t.Path == p);
                if (spec != null) { spec.Help = spec.Help ?? help; AddTable(grid, page, index, spec); bound.Add(p); continue; }

                switch (prop.Value)
                {
                    case JObject o:
                        if (!HasUnbound(p, o, bound)) continue;
                        AddHeading(grid, Pretty(prop.Name), help ?? (o["_comment"]?.Type == JTokenType.String ? (string)o["_comment"] : null));
                        AddAuto(grid, page, index, p, structure, bound, depth + 1);
                        break;
                    case JArray a when a.Count > 0 && a.All(x => x is JObject):
                        var cols = a.OfType<JObject>().SelectMany(x => x.Properties()).Where(x => !x.Name.StartsWith("_"))
                                    .GroupBy(x => x.Name).Select(g => new RuleColumn
                                    {
                                        Key = g.Key, Header = Pretty(g.Key), Width = 140,
                                        Kind = g.All(x => x.Value.Type == JTokenType.Boolean) ? RuleKind.Bool
                                             : g.All(x => x.Value.Type == JTokenType.Integer || x.Value.Type == JTokenType.Float) ? RuleKind.OptionalNumber
                                             : RuleKind.OptionalText
                                    }).ToList();
                        var table = new RuleTable { Path = p, Title = Pretty(prop.Name), Help = help, Shape = TableShape.List, Ordered = true };
                        table.Columns.AddRange(cols);
                        AddTable(grid, page, index, table);
                        break;
                    case JArray a:
                        var numbers = a.Count > 0 && a.All(x => x.Type == JTokenType.Integer || x.Type == JTokenType.Float);
                        AddField(grid, page, index, new RuleField { Path = p, Label = Pretty(prop.Name), Help = help ?? "Comma-separated.", Kind = numbers ? RuleKind.Numbers : RuleKind.Words }, false);
                        break;
                    default:
                        var v = prop.Value;
                        var kind = v.Type == JTokenType.Boolean ? RuleKind.Bool
                                 : v.Type == JTokenType.Integer || v.Type == JTokenType.Float ? RuleKind.Number
                                 : LooksLikePattern(prop.Name) ? RuleKind.Regex
                                 : RuleKind.Text;
                        AddField(grid, page, index, new RuleField { Path = p, Label = Pretty(prop.Name), Help = help, Kind = kind }, v.Type == JTokenType.Null);
                        break;
                }
                bound.Add(p);
            }
        }

        private static bool HasUnbound(string path, JObject o, HashSet<string> bound) =>
            o.Properties().Any(p =>
            {
                if (p.Name.StartsWith("_")) return false;
                var sub = RuleLayers.Join(path, p.Name);
                if (bound.Contains(sub)) return false;
                return !(p.Value is JObject so) || HasUnbound(sub, so, bound);
            });

        private static bool LooksLikePattern(string name) =>
            Regex.IsMatch(name, "(Layers|Blocks|Match|Folders|Files|Text|Pattern|Strip|IdFromName)$");

        /// <summary>riserCircleMinRadius -> "Riser circle min radius".</summary>
        private static string Pretty(string name)
        {
            var words = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
            words = Regex.Replace(words, @"\b(dwg|pdf|erv|hv|id|rf|fdc|sp)\b", m => m.Value.ToUpperInvariant());
            return char.ToUpperInvariant(words[0]) + words.Substring(1);
        }

        // ---------------------------------------------------------------- values

        private string LowerName => _target == RuleLayers.Layer.Office ? "add-in default" : "office rules";

        private void LoadValues()
        {
            _loading = true;
            try
            {
                _effective = RuleLayers.Effective(_modelPath, _target, out var sources);
                _lower = RuleLayers.Effective(_modelPath, _target - 1);
                foreach (var b in _bindings) b.Show(RuleLayers.Get(_effective, b.Path));
                var file = RuleLayers.PathOf(_target, _modelPath);
                _sources.Text = file == null ? "" : (File.Exists(file) ? "File: " : "Not saved yet: ") + file;
            }
            finally { _loading = false; }
            _dirty = false;
            _changedPages.Clear();
            foreach (var b in _bindings) Mark(b);
            RefreshNav();
            _reset.Text = "Reset this page to the " + LowerName;
            _status.Text = "";
        }

        private void Changed(RuleBinding b)
        {
            if (_loading || _lower == null) return;
            _dirty = true;
            Mark(b);
            RefreshNav();
            _status.Text = "Unsaved changes";
        }

        /// <summary>Blue bold caption = differs from the layer below.</summary>
        private void Mark(RuleBinding b)
        {
            var errors = new List<string>();
            var value = b.Read(errors);
            var lower = RuleLayers.Get(_lower, b.Path);
            bool changed = errors.Count == 0 && !RuleLayers.Same(value, lower);
            b.Caption.ForeColor = changed ? Color.MediumBlue : (b is TableBinding ? Color.FromArgb(40, 40, 40) : SystemColors.ControlText);
            var style = changed || b is TableBinding ? FontStyle.Bold : FontStyle.Regular;
            if (b.Caption.Font.Style != style) b.Caption.Font = new Font(b.Caption.Font, style);
            _tips.SetToolTip(b.Caption, changed ? $"Changed. The {LowerName}: {Describe(lower)}" : null);

            bool pageChanged = _bindings.Where(x => x.Page == b.Page).Any(x => x.Caption.ForeColor == Color.MediumBlue);
            if (pageChanged) _changedPages.Add(b.Page); else _changedPages.Remove(b.Page);
        }

        private static string Describe(JToken t)
        {
            if (IsNull(t)) return "(not set)";
            if (t is JArray a && a.All(x => x is JValue)) return string.Join(", ", a.Select(Str));
            if (t is JContainer c) return $"{c.Count} rule(s) — use Reset to go back to them";
            return Str(t);
        }

        private void RefreshNav()
        {
            for (int i = 0; i < _navPages.Count; i++)
            {
                var text = NavText(_navPages[i]);
                if ((string)_nav.Items[i] != text) _nav.Items[i] = text;
            }
        }

        private void OnTargetChanged()
        {
            var wanted = _targetBox.SelectedIndex == 1 ? RuleLayers.Layer.Project : RuleLayers.Layer.Office;
            if (wanted == _target) return;
            if (wanted == RuleLayers.Layer.Project && _modelPath == null)
            {
                MessageBox.Show(this, "Save the model first: a project's rules are kept next to its .rvt file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _targetBox.SelectedIndex = 0;
                return;
            }
            if (_dirty && !AskSave()) { _targetBox.SelectedIndex = _target == RuleLayers.Layer.Project ? 1 : 0; return; }
            _target = wanted;
            LoadValues();
        }

        /// <summary>false = the user cancelled.</summary>
        private bool AskSave()
        {
            var r = MessageBox.Show(this, "Save the changes first?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return Save();
            return true;
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_dirty && !AskSave()) e.Cancel = true;
        }

        private void ResetPage()
        {
            if (_currentPage < 0) return;
            _loading = true;
            try
            {
                foreach (var b in _bindings.Where(x => x.Page == _currentPage)) b.Show(RuleLayers.Get(_lower, b.Path));
            }
            finally { _loading = false; }
            foreach (var b in _bindings.Where(x => x.Page == _currentPage)) Changed(b);
        }

        private bool Save()
        {
            Validate();
            foreach (var g in _bindings.OfType<TableBinding>()) g.Grid.EndEdit();

            var errors = new List<string>();
            var edited = (JObject)_effective.DeepClone();
            int firstBad = -1;
            foreach (var b in _bindings)
            {
                int before = errors.Count;
                var value = b.Read(errors);
                if (errors.Count > before) { if (firstBad < 0) firstBad = b.Page; continue; }
                RuleLayers.Set(edited, b.Path, value);
            }
            if (errors.Count > 0)
            {
                GoTo(firstBad);
                MessageBox.Show(this, "Nothing was saved. Please fix:\n\n• " + string.Join("\n• ", errors.Take(15)) + (errors.Count > 15 ? $"\n… and {errors.Count - 15} more" : ""),
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try { RuleLoader.Parse(edited); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Nothing was saved: a value has the wrong type for the add-in.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var warnings = Warnings(edited);
            if (warnings.Count > 0 &&
                MessageBox.Show(this, "Please check:\n\n• " + string.Join("\n• ", warnings) + "\n\nSave anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            try
            {
                var path = RuleLayers.Save(edited, _modelPath, _target);
                _onSaved?.Invoke();
                LoadValues();
                _status.Text = File.Exists(path) ? "Saved. Auto Run uses these rules from now on." : "Saved: no differences left, this layer follows the " + LowerName + ".";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the rules:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void GoTo(int page)
        {
            if (page < 0) return;
            if (_pages[page].Advanced && !_showAdvanced.Checked) _showAdvanced.Checked = true;
            int i = _navPages.IndexOf(page);
            if (i >= 0) _nav.SelectedIndex = i;
        }

        /// <summary>Allowed, but probably not what the user meant: a system the sleeve family cannot size, an unknown bathtub option.</summary>
        private static List<string> Warnings(JObject rules)
        {
            var list = new List<string>();
            var toggles = new HashSet<string>((RuleLayers.Get(rules, "families.roundSleeve.sizeToggles") as JObject)?.Properties().Select(p => p.Name) ?? Enumerable.Empty<string>(),
                                              StringComparer.OrdinalIgnoreCase);
            void Sleeved(string path, string what, bool needsSleeveFlag)
            {
                if (!(RuleLayers.Get(rules, path) is JObject table)) return;
                foreach (var row in table.Properties())
                {
                    if (!(row.Value is JObject o)) continue;
                    if (needsSleeveFlag && !(o["sleeve"]?.Type == JTokenType.Boolean && (bool)o["sleeve"])) continue;
                    var system = IsNull(o["system"]) ? "Sanitary" : Str(o["system"]);
                    if (!toggles.Contains(system))
                        list.Add($"{what} '{row.Name}': system '{system}' has no size toggle on the round sleeve (Families page), so its sleeve cannot be sized.");
                }
            }
            Sleeved("plumbing.services", "Plumbing service", true);
            Sleeved("sprinkler.services", "Sprinkler label", true);
            Sleeved("plumbing.fixtures", "Plumbing fixture", false);

            if (RuleLayers.Get(rules, "legend.categories") is JArray cats)
                foreach (var c in cats.OfType<JObject>())
                {
                    var system = IsNull(c["system"]) ? null : Str(c["system"]);
                    if (system != null && !RulesCatalog.KnownSystems.Contains(system, StringComparer.OrdinalIgnoreCase))
                        list.Add($"HV tag '{Str(c["match"])}': '{system}' is not one of the add-in's systems ({string.Join(", ", RulesCatalog.KnownSystems.Take(7))}…). Check the spelling.");
                }

            var tub = RuleLayers.Get(rules, "systems.bathtub.default");
            if (!IsNull(tub) && !((RuleLayers.Get(rules, "systems.bathtub.options") as JObject)?.ContainsKey(Str(tub)) ?? false))
                list.Add($"Bathtub default '{Str(tub)}' is not one of the bathtub options.");
            return list;
        }

        private void OpenFile()
        {
            if (_dirty && !AskSave()) return;
            var path = RuleLayers.PathOf(_target, _modelPath);
            if (path == null) return;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\n  \"version\": 1,\n  \"_comment\": \"Only what differs from the layer below. Lengths in inches.\"\n}\n");
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { Process.Start("notepad.exe", "\"" + path + "\""); }
            _status.Text = "Changes made in the file are read when Edit Rules is opened again.";
        }

        // ---------------------------------------------------------------- table actions

        private void DeleteRows(TableBinding b)
        {
            var rows = b.Grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow)
                        .Concat(b.Grid.SelectedRows.Cast<DataGridViewRow>())
                        .Where(r => !r.IsNewRow).Distinct().ToList();
            if (rows.Count == 0) { _status.Text = "Click a cell of the row to delete first."; return; }
            if (MessageBox.Show(this, $"Delete {rows.Count} row(s)?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var r in rows) b.Grid.Rows.Remove(r);
            b.FitHeight();
            Changed(b);
        }

        private void MoveRow(TableBinding b, int step)
        {
            var cell = b.Grid.CurrentCell;
            if (cell == null || b.Grid.Rows[cell.RowIndex].IsNewRow) return;
            int from = cell.RowIndex, to = from + step;
            int last = b.Grid.Rows.Count - (b.Grid.AllowUserToAddRows ? 2 : 1);
            if (to < 0 || to > last) return;
            b.Grid.EndEdit();
            var row = b.Grid.Rows[from];
            b.Grid.Rows.RemoveAt(from);
            b.Grid.Rows.Insert(to, row);
            b.Grid.CurrentCell = b.Grid.Rows[to].Cells[cell.ColumnIndex];
            Changed(b);
        }

        private static string Header(DataGridViewColumn c) => c.HeaderText.TrimEnd('*', ' ');

        private void ExportCsv(TableBinding b)
        {
            using (var dlg = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = Regex.Replace(b.Table.Title, @"[^\w\- ]+", "") + ".csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder();
                var cols = b.Grid.Columns.Cast<DataGridViewColumn>().ToList();
                sb.AppendLine(string.Join(",", cols.Select(c => Csv(Header(c)))));
                foreach (DataGridViewRow row in b.Grid.Rows)
                {
                    if (row.IsNewRow) continue;
                    sb.AppendLine(string.Join(",", cols.Select(c =>
                    {
                        var v = row.Cells[c.Index].Value;
                        return Csv(v is bool x ? (x ? "yes" : "no") : v?.ToString() ?? "");
                    })));
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                _status.Text = "Exported " + Path.GetFileName(dlg.FileName);
            }
        }

        private void ImportCsv(TableBinding b)
        {
            using (var dlg = new OpenFileDialog { Filter = "CSV (Excel)|*.csv|All files|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                List<List<string>> lines;
                try { lines = ParseCsv(File.ReadAllText(dlg.FileName)); }
                catch (Exception ex) { MessageBox.Show(this, "Could not read the file: " + ex.Message, Text); return; }
                lines = lines.Where(l => l.Any(c => c.Trim().Length > 0)).ToList();
                if (lines.Count == 0) return;

                var cols = b.Grid.Columns.Cast<DataGridViewColumn>().ToList();
                var map = new Dictionary<int, DataGridViewColumn>();
                var unknown = new List<string>();
                for (int i = 0; i < lines[0].Count; i++)
                {
                    var h = lines[0][i].Trim().TrimEnd('*', ' ');
                    var col = cols.FirstOrDefault(c => string.Equals(Header(c), h, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, h, StringComparison.OrdinalIgnoreCase));
                    if (col != null) map[i] = col; else if (h.Length > 0) unknown.Add(h);
                }
                if (b.Table.Shape != TableShape.List && !map.Values.Any(c => c.Name == TableBinding.KeyColumn))
                {
                    MessageBox.Show(this, $"The first line must name the columns, including '{b.Table.KeyHeader}'.\nExpected: {string.Join(", ", cols.Select(Header))}", Text);
                    return;
                }

                var answer = MessageBox.Show(this,
                    $"{lines.Count - 1} row(s) read." + (unknown.Count > 0 ? $"\nIgnored columns: {string.Join(", ", unknown)}" : "") +
                    "\n\nYes = replace the table's rows\nNo = add them at the end", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;

                var oldTags = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (b.Table.Shape == TableShape.Keyed)
                    foreach (DataGridViewRow r in b.Grid.Rows)
                        if (!r.IsNewRow && r.Cells[TableBinding.KeyColumn].Value is string k && !oldTags.ContainsKey(k)) oldTags[k] = r.Tag;
                if (answer == DialogResult.Yes) b.Grid.Rows.Clear();

                foreach (var line in lines.Skip(1))
                {
                    int i = b.Grid.Rows.Add();
                    var row = b.Grid.Rows[i];
                    foreach (var kv in map)
                    {
                        if (kv.Key >= line.Count) continue;
                        var text = line[kv.Key].Trim();
                        var col = kv.Value;
                        if (col is DataGridViewCheckBoxColumn)
                            row.Cells[col.Index].Value = Regex.IsMatch(text, "^(yes|y|true|1|x|✓)$", RegexOptions.IgnoreCase);
                        else if (col is DataGridViewComboBoxColumn)
                        {
                            var rc = b.Table.Columns.First(c => c.Key == col.Name);
                            row.Cells[col.Index].Value = b.ChoiceLabel(rc, text);
                        }
                        else row.Cells[col.Index].Value = text;
                    }
                    if (b.Table.Shape == TableShape.Keyed && row.Cells[TableBinding.KeyColumn].Value is string key && oldTags.TryGetValue(key, out var tag)) row.Tag = tag;
                }
                b.FitHeight();
                Changed(b);
                _status.Text = "Imported — check the rows, then Save.";
            }
        }

        private static string Csv(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        /// <summary>CSV as Excel writes it: quoted fields, "" inside quotes; ';' as separator when the header uses it.</summary>
        private static List<List<string>> ParseCsv(string text)
        {
            var firstLine = text.Split('\n')[0];
            char sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); }
                else if (c != '\r') field.Append(c);
            }
            if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
            return rows;
        }

        // ---------------------------------------------------------------- helpers

        private static bool IsNull(JToken t) => t == null || t.Type == JTokenType.Null;

        private static string Str(JToken t)
        {
            if (IsNull(t)) return "";
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<double>().ToString("0.######", CultureInfo.InvariantCulture);
            if (t.Type == JTokenType.Boolean) return (bool)t ? "yes" : "no";
            return t is JValue v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : t.ToString();
        }

        private static JValue Num(double d) =>
            Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15 ? new JValue((long)Math.Round(d)) : new JValue(d);

        /// <summary>12, 12.5, 12", 1/2, 1 1/2 (inches).</summary>
        private static bool TryNumber(string s, out double d)
        {
            s = (s ?? "").Trim();
            if (s.EndsWith("\"")) s = s.Substring(0, s.Length - 1).Trim();
            else if (s.EndsWith("in", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 2).Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return true;
            var m = Regex.Match(s, @"^(?:(\d+)\s+)?(\d+)\s*/\s*(\d+)$");
            if (m.Success && int.Parse(m.Groups[3].Value) != 0)
            {
                d = (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0) + int.Parse(m.Groups[2].Value) / (double)int.Parse(m.Groups[3].Value);
                return true;
            }
            return false;
        }

        private static bool ValidPattern(string pattern, out string why)
        {
            why = null;
            try { _ = new Regex(pattern ?? "", RegexOptions.IgnoreCase); return true; }
            catch (ArgumentException ex) { why = ex.Message; return false; }
        }
    }
}
