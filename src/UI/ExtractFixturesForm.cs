using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Automation;
using SleevesOpenings.Commands;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using TextBox = System.Windows.Forms.TextBox;
using CheckBox = System.Windows.Forms.CheckBox;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;
using Row = SleevesOpenings.Commands.FixtureExtractor.Row;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Extract Fixtures: choose a floor, extract its fixtures (architect's DWG and Revit families), see each one's type
    /// and location; double-click to zoom; mark them in the floor's plan, export CSV, place their sleeves.
    /// </summary>
    public class ExtractFixturesForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly List<Level> _levels;
        private readonly ComboBox _floor;
        private readonly CheckBox _dwg, _revit, _onlySleeved, _allShapes;
        private List<DwgFixtureCatalog.Item> _blocks = new List<DwgFixtureCatalog.Item>();
        private readonly Label _summary;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private List<Row> _rows = new List<Row>();
        private List<Row> _shown = new List<Row>();
        private Level _level;

        public ExtractFixturesForm(UIDocument uidoc, Level start)
        {
            _uidoc = uidoc;
            _levels = FixtureExtractor.Levels(uidoc.Document);

            Text = "Sleeves & Openings — Extract Fixtures";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 640);
            MinimumSize = new Size(860, 420);

            var top = new Panel { Dock = DockStyle.Top, Height = 82, Padding = new Padding(12, 10, 12, 0) };
            var floorLabel = new Label { Text = "Floor:", AutoSize = true, Location = new Point(12, 15) };
            _floor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(62, 11), Width = 260 };
            foreach (var l in _levels) _floor.Items.Add($"{l.Name}   ({FixtureExtractor.Ft(l.ProjectElevation)})");
            int at = start == null ? -1 : _levels.FindIndex(l => l.Id == start.Id);
            if (_floor.Items.Count > 0) _floor.SelectedIndex = at >= 0 ? at : 0;
            _dwg = new CheckBox { Text = "Architect's DWG", Checked = true, AutoSize = true, Location = new Point(340, 14) };
            _revit = new CheckBox { Text = "Revit fixtures (model + links)", Checked = true, AutoSize = true, Location = new Point(480, 14) };
            var extract = new Button { Text = "Extract", Size = new Size(110, 30), Location = new Point(720, 9) };
            var name = new Button { Text = "Override names…", Size = new Size(130, 30), Location = new Point(840, 9) };
            _onlySleeved = new CheckBox { Text = "Only sleeved types", Checked = false, AutoSize = true, Location = new Point(340, 48) };
            _allShapes = new CheckBox { Text = "Also guess toilets and sinks from loose lines (not blocks) by shape", Checked = false, AutoSize = true, Location = new Point(500, 48) };
            var how = new Label { Text = "Fixtures = the DWG's blocks, identified automatically.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(12, 50) };
            top.Controls.AddRange(new Control[] { floorLabel, _floor, _dwg, _revit, extract, name, how, _onlySleeved, _allShapes });

            _summary = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 6, 12, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                                   Text = "Choose a floor and press Extract." };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, RowTemplate = { Height = 26 }
            };
            foreach (var (h, w) in new[] { ("#", 4), ("Type", 7), ("What", 16), ("Location (grids)", 26), ("X", 9), ("Y", 9), ("Size", 9), ("Sleeves", 6), ("Source", 14) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Zoom(_shown[e.RowIndex]); };
            _grid.SelectionChanged += (s, e) => ShowDetail();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _shown.Count) return;
                var r = _shown[e.RowIndex];
                _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = r.Code == "?" ? Color.DimGray : r.How?.StartsWith("no wall") == true ? Color.DarkGoldenrod : Color.Black;
            };

            _detail = new TextBox { Dock = DockStyle.Bottom, Height = 70, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12) };
            var zoom = new Button { Text = "Zoom to", Size = new Size(100, 32), Location = new Point(12, 10) };
            var mark = new Button { Text = "Mark in plan", Size = new Size(120, 32), Location = new Point(120, 10) };
            var csv = new Button { Text = "Export CSV…", Size = new Size(120, 32), Location = new Point(248, 10) };
            var place = new Button { Text = "Place sleeves…", Size = new Size(130, 32), Location = new Point(376, 10) };
            var hint = new Label { Text = "Double-click = zoom. Mark / Place use the selected rows (all rows when one or none is selected).", AutoSize = true,
                                   ForeColor = Color.DimGray, Location = new Point(516, 18) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            bottom.Controls.AddRange(new Control[] { zoom, mark, csv, place, hint, close });
            bottom.Resize += (s, e) => close.Location = new Point(bottom.ClientSize.Width - 112, 10);
            CancelButton = close;

            Controls.Add(_grid);
            Controls.Add(_detail);
            Controls.Add(bottom);
            Controls.Add(_summary);
            Controls.Add(top);

            extract.Click += (s, e) => Extract();
            name.Click += (s, e) => NameBlocks();
            _onlySleeved.CheckedChanged += (s, e) => Refill();
            zoom.Click += (s, e) => { var r = Current(); if (r != null) Zoom(r); };
            mark.Click += (s, e) => Mark();
            csv.Click += (s, e) => Export();
            place.Click += (s, e) => Place();
        }

        private Row Current() =>
            _grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _shown.Count ? _shown[_grid.CurrentRow.Index] : null;

        /// <summary>The selected rows, or every shown row when one or none is selected.</summary>
        private List<Row> Picked()
        {
            var picked = _grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Index).Where(i => i >= 0 && i < _shown.Count).Select(i => _shown[i]).ToList();
            return picked.Count > 1 ? picked : _shown.ToList();
        }

        private void Extract()
        {
            if (_floor.SelectedIndex < 0) return;
            _level = _levels[_floor.SelectedIndex];
            var doc = _uidoc.Document;
            Cursor = Cursors.WaitCursor;
            try
            {
                var rules = App.Rules(doc).Plumbing;
                _rows = FixtureExtractor.Extract(doc, _level, rules, _dwg.Checked, _revit.Checked, _allShapes.Checked, out var plans, out _blocks, out var notes);
                Refill();
                var kinds = _rows.GroupBy(r => r.Code).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
                string dwgs = !_dwg.Checked ? "" : plans.Count == 0 ? "  —  no architect's DWG on this level" : $"  —  DWG: {string.Join(", ", plans.Select(p => p.Source).Distinct())}";
                _summary.Text = $"{_level.Name}: {_rows.Count} fixture(s)" + (_rows.Count > 0 ? ": " + string.Join(", ", kinds) : "") + dwgs;
                var warnings = plans.Select(p => p.Warning).Where(w => w != null).Distinct().ToList();
                _summary.ForeColor = warnings.Count > 0 ? Color.Firebrick : SystemColors.ControlText;
                if (warnings.Count > 0)
                {
                    _summary.Text += "  —  ! DWG placed twice, see the warning";
                    MessageBox.Show(this, string.Join("\n\n", warnings), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                App.Log($"Extract fixtures {_level.Name}: {_rows.Count} found ({string.Join(", ", kinds)})");
                // nothing to answer: blocks are identified on their own; only the unclear ones are said (and never placed)
                var unclear = notes.Where(n => n.StartsWith("Block '")).Distinct().ToList();
                if (unclear.Count > 0) _summary.Text += $"  —  {unclear.Count} block kind(s) unclear, not placed";
                var flagged = _rows.Count(r => r.Name?.EndsWith("(check)") == true);
                if (flagged > 0) _summary.Text += $"  —  {flagged} identified from one sign only (check)";
                foreach (var n in notes) App.Log("Extract fixtures: " + n);
            }
            catch (Exception ex)
            {
                App.Log("Extract fixtures: " + ex);
                MessageBox.Show(this, "Could not extract: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        /// <summary>Opens the block naming list for the blocks of the last extraction; on save, extracts again.</summary>
        private void NameBlocks()
        {
            if (_level == null || _blocks.Count == 0) { MessageBox.Show(this, "Extract a floor whose DWG has fixture blocks first.", Text); return; }
            using (var f = new BlockNamesForm(_blocks, names => MarkBlocks(names)))
                if (f.ShowDialog(this) == DialogResult.OK) Extract();
        }

        /// <summary>Marks every insert of the named blocks in the floor's plan (to see what a block is).</summary>
        private void MarkBlocks(List<string> names)
        {
            var doc = _uidoc.Document;
            try
            {
                var view = FixtureExtractor.PlanFor(doc, _level, _uidoc.ActiveView);
                if (view == null) { MessageBox.Show(this, $"No floor plan of {_level.Name} to draw in.", Text); return; }
                var rows = _blocks.Where(b => names.Contains(b.Name) || b.Inner.Any(names.Contains)).Select(b => new Row
                {
                    Code = "?", Name = b.Name, X0 = b.X0 / 12, Y0 = b.Y0 / 12, X1 = b.X1 / 12, Y1 = b.Y1 / 12,
                    Points = new List<XYZ> { new XYZ(b.Cx / 12, b.Cy / 12, 0) }
                }).ToList();
                var ids = FixtureExtractor.Mark(doc, view, _level, rows);
                if (_uidoc.ActiveView?.Id != view.Id) { try { _uidoc.ActiveView = view; } catch (Exception) { } }
                MessageBox.Show(this, $"{rows.Count} insert(s) of {string.Join(", ", names)} marked in '{view.Name}' with a box and the block name. Undo removes them.", Text);
            }
            catch (Exception ex)
            {
                App.Log("Extract fixtures mark blocks: " + ex);
                MessageBox.Show(this, "Could not mark: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Refill()
        {
            var rules = App.Rules(_uidoc.Document).Plumbing;
            _shown = _rows.Where(r => !_onlySleeved.Checked || r.Sleeved(rules)).ToList();
            _grid.Rows.Clear();
            int n = 0;
            foreach (var r in _shown)
            {
                double w = (r.X1 - r.X0) * 12, l = (r.Y1 - r.Y0) * 12;
                var p = r.Points.Count > 0 ? new XYZ(r.Points.Average(q => q.X), r.Points.Average(q => q.Y), 0) : new XYZ(r.Cx, r.Cy, 0);
                _grid.Rows.Add(++n, r.Code, r.Name, r.Grid, FixtureExtractor.Ft(p.X), FixtureExtractor.Ft(p.Y),
                               w < 0.5 && l < 0.5 ? "" : $"{w:0}\" x {l:0}\"", r.Points.Count, r.Source);
            }
            ShowDetail();
        }

        private void ShowDetail()
        {
            var r = Current();
            if (r == null) { _detail.Text = ""; return; }
            _detail.Text = $"{r.Code}  {r.Name}  on {r.Level}  —  from {r.Source}\r\n" +
                           $"Sleeve point(s): {string.Join(";  ", r.Points.Select(p => $"X {FixtureExtractor.Ft(p.X)}, Y {FixtureExtractor.Ft(p.Y)}"))}  ({r.How})\r\n" +
                           $"Box: X {FixtureExtractor.Ft(r.X0)} to {FixtureExtractor.Ft(r.X1)}, Y {FixtureExtractor.Ft(r.Y0)} to {FixtureExtractor.Ft(r.Y1)}" +
                           (string.IsNullOrEmpty(r.Grid) ? "" : $"   |   {r.Grid}");
        }

        /// <summary>Opens a plan of the floor (the active one when it is) and zooms to the fixture; selects it when it is this model's family.</summary>
        private void Zoom(Row r)
        {
            if (_level == null) return;
            var doc = _uidoc.Document;
            try
            {
                var view = FixtureExtractor.PlanFor(doc, _level, _uidoc.ActiveView);
                if (view == null) { MessageBox.Show(this, $"No floor plan of {_level.Name} to zoom in.", Text); return; }
                if (_uidoc.ActiveView?.Id != view.Id)
                {
                    try { _uidoc.ActiveView = view; }
                    catch (Exception) { _uidoc.RequestViewChange(view); return; }
                }
                if (r.Id != null && doc.GetElement(r.Id) != null) _uidoc.Selection.SetElementIds(new List<ElementId> { r.Id });
                var ui = _uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                double z = _level.ProjectElevation;
                ui?.ZoomAndCenterRectangle(new XYZ(Math.Min(r.X0, r.Points.Min(p => p.X)) - 4, Math.Min(r.Y0, r.Points.Min(p => p.Y)) - 4, z),
                                           new XYZ(Math.Max(r.X1, r.Points.Max(p => p.X)) + 4, Math.Max(r.Y1, r.Points.Max(p => p.Y)) + 4, z));
            }
            catch (Exception ex)
            {
                App.Log("Extract fixtures zoom: " + ex);
                MessageBox.Show(this, "Could not zoom: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Mark()
        {
            var rows = Picked();
            if (_level == null || rows.Count == 0) { MessageBox.Show(this, "Extract a floor first.", Text); return; }
            var doc = _uidoc.Document;
            try
            {
                var view = FixtureExtractor.PlanFor(doc, _level, _uidoc.ActiveView);
                if (view == null) { MessageBox.Show(this, $"No floor plan of {_level.Name} to draw in.", Text); return; }
                var ids = FixtureExtractor.Mark(doc, view, _level, rows);
                MessageBox.Show(this, $"{rows.Count} fixture(s) marked in '{view.Name}' ({ids.Count} detail lines and texts): a box, a circle at each sleeve point and the type. " +
                                      "Undo removes them.", Text);
            }
            catch (Exception ex)
            {
                App.Log("Extract fixtures mark: " + ex);
                MessageBox.Show(this, "Could not mark: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Export()
        {
            if (_level == null || _shown.Count == 0) { MessageBox.Show(this, "Extract a floor first.", Text); return; }
            string path = FixtureExtractor.DefaultCsv(_uidoc.Document, _level);
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileName(path), InitialDirectory = Path.GetDirectoryName(path) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { FixtureExtractor.SaveCsv(dlg.FileName, _shown); MessageBox.Show(this, $"{_shown.Count} fixture(s) saved to\n{dlg.FileName}", Text); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void Place()
        {
            var doc = _uidoc.Document;
            var rules = App.Rules(doc).Plumbing;
            var rows = Picked().Where(r => r.Sleeved(rules)).ToList();
            if (_level == null || rows.Count == 0) { MessageBox.Show(this, "No fixture here is a type the rules sleeve (plumbing.fixtures).", Text); return; }
            var kinds = string.Join(", ", rows.GroupBy(r => r.Code).Select(g => $"{g.Count()} {g.Key}"));
            if (MessageBox.Show(this, $"Place sleeves on {_level.Name} at {rows.Count} fixture(s) ({kinds})?\n\n" +
                                      "Sizes from the rules; a fixture with a sleeve within 1'-6\" is left as it is. Undo removes them.",
                                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cursor = Cursors.WaitCursor;
            try { MessageBox.Show(this, FixtureExtractor.Place(doc, _level, rows, rules), Text); }
            catch (Exception ex)
            {
                App.Log("Extract fixtures place: " + ex);
                MessageBox.Show(this, "Could not place: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }
    }
}
