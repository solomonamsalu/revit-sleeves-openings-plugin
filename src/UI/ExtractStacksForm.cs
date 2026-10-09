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
using Stack = SleevesOpenings.Automation.StackFinder.Stack;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Extract Stacks: choose a floor, find its plumbing stacks from the model (Revit pipes, else the fixtures' wet walls and
    /// chases), see each one's sleeves and the fixtures it serves; double-click to zoom; mark in the plan, export CSV, place.
    /// </summary>
    public class ExtractStacksForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly List<Level> _levels;
        private readonly ComboBox _floor;
        private readonly CheckBox _fixtures;
        private readonly Label _summary;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private StackExtractor.Floor _result;
        private List<Stack> _shown = new List<Stack>();
        private Level _level;

        public ExtractStacksForm(UIDocument uidoc, Level start)
        {
            _uidoc = uidoc;
            _levels = FixtureExtractor.Levels(uidoc.Document);

            Text = "Sleeves & Openings — Extract Stacks";
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
            _fixtures = new CheckBox { Text = "Fixture sleeves too (drains, vents, island sink rows)", Checked = true, AutoSize = true, Location = new Point(340, 14) };
            var extract = new Button { Text = "Extract", Size = new Size(110, 30), Location = new Point(720, 9) };
            var how = new Label
            {
                Text = "From this floor of the model only: Revit pipes, else a stack per toilet (its wall or a pipe chase); vent / hot / cold only where the floor shows them.",
                AutoSize = true, ForeColor = Color.DimGray, Location = new Point(12, 50)
            };
            top.Controls.AddRange(new Control[] { floorLabel, _floor, _fixtures, extract, how });

            _summary = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 6, 12, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                                   Text = "Choose a floor and press Extract." };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, RowTemplate = { Height = 26 }
            };
            foreach (var (h, w) in new[] { ("Row", 5), ("Kind", 10), ("Source", 8), ("Location (grids)", 20), ("X", 7), ("Y", 7), ("Stack sleeves", 13), ("Fixture sleeves", 7), ("Serves", 13), ("Not placed", 14) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Zoom(_shown[e.RowIndex]); };
            _grid.SelectionChanged += (s, e) => ShowDetail();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _shown.Count) return;
                _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = _shown[e.RowIndex].Check ? Color.DarkGoldenrod : Color.Black;
            };

            _detail = new TextBox { Dock = DockStyle.Bottom, Height = 90, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };

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
            zoom.Click += (s, e) => { var r = Current(); if (r != null) Zoom(r); };
            mark.Click += (s, e) => Mark();
            csv.Click += (s, e) => Export();
            place.Click += (s, e) => Place();
        }

        private Stack Current() =>
            _grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _shown.Count ? _shown[_grid.CurrentRow.Index] : null;

        /// <summary>The selected rows, or every row when one or none is selected.</summary>
        private List<Stack> Picked()
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
                _result = StackExtractor.Extract(doc, _level, App.Rules(doc).Plumbing, _fixtures.Checked);
                _shown = _result.Stacks.ToList();
                _grid.Rows.Clear();
                foreach (var s in _shown)
                    _grid.Rows.Add(s.Id, s.Kind, s.Source, _result.Grids.TryGetValue(s.Id, out var g) ? g : "", FixtureExtractor.Ft(s.X), FixtureExtractor.Ft(s.Y),
                                   string.Join("  ", s.StackSleeves.Select(v => $"{v.Service} {v.Size:0}\"")), s.FixtureSleeves.Count(),
                                   string.Join(", ", s.Serves.GroupBy(f => f.Code).Select(x => x.Count() > 1 ? $"{x.Count()} {x.Key}" : x.Key)),
                                   string.Join("; ", s.Missing.Select(m => m.Split(':')[0])));
                var kinds = _result.Stacks.GroupBy(s => s.Kind).Select(x => $"{x.Count()} {x.Key}");
                _summary.Text = $"{_level.Name}: {_result.Stacks.Count} row(s)" + (_result.Stacks.Count > 0 ? $" ({string.Join(", ", kinds)})" : "") +
                                $"  —  {_result.Stacks.Sum(s => s.Sleeves.Count)} sleeve(s) from {_result.Fixtures} fixture(s), {_result.Pipes} modelled pipe(s), {_result.Water} water riser(s)" +
                                (_result.Dwgs.Count == 0 ? "  —  no architect's DWG on this level" : "");
                _summary.ForeColor = _result.Fixtures == 0 && _result.Pipes == 0 ? Color.Firebrick : SystemColors.ControlText;
                ShowDetail();
            }
            catch (Exception ex)
            {
                App.Log("Extract stacks: " + ex);
                MessageBox.Show(this, "Could not extract: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void ShowDetail()
        {
            var s = Current();
            if (s == null)
            {
                _detail.Text = _result == null ? "" : string.Join("\r\n", _result.Notes);
                return;
            }
            _detail.Text = $"{s.Id} {s.Kind} ({s.Source}): {s.How}\r\n" +
                           $"Sleeves: {string.Join(";  ", s.Sleeves.Select(v => $"{StackExtractor.Name(v)} {v.Size:0}\" at X {FixtureExtractor.Ft(v.X)}, Y {FixtureExtractor.Ft(v.Y)}"))}\r\n" +
                           (s.Missing.Count > 0 ? $"Not placed: {string.Join(";  ", s.Missing)}\r\n" : "") +
                           $"Serves: {string.Join(";  ", s.Serves.Select(f => $"{f.Code} ({f.Source})"))}" +
                           (s.Notes.Count > 0 ? "\r\n" + string.Join("\r\n", s.Notes) : "");
        }

        private void Zoom(Stack s)
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
                var xs = s.Sleeves.Select(v => v.X).Concat(s.Serves.Select(f => f.X)).DefaultIfEmpty(s.X).ToList();
                var ys = s.Sleeves.Select(v => v.Y).Concat(s.Serves.Select(f => f.Y)).DefaultIfEmpty(s.Y).ToList();
                var ui = _uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                double z = _level.ProjectElevation;
                ui?.ZoomAndCenterRectangle(new XYZ(xs.Min() - 4, ys.Min() - 4, z), new XYZ(xs.Max() + 4, ys.Max() + 4, z));
            }
            catch (Exception ex)
            {
                App.Log("Extract stacks zoom: " + ex);
                MessageBox.Show(this, "Could not zoom: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Mark()
        {
            var stacks = Picked();
            if (_level == null || stacks.Count == 0) { MessageBox.Show(this, "Extract a floor first.", Text); return; }
            var doc = _uidoc.Document;
            try
            {
                var view = FixtureExtractor.PlanFor(doc, _level, _uidoc.ActiveView);
                if (view == null) { MessageBox.Show(this, $"No floor plan of {_level.Name} to draw in.", Text); return; }
                var ids = StackExtractor.Mark(doc, view, _level, stacks);
                MessageBox.Show(this, $"{stacks.Count} stack(s) marked in '{view.Name}' ({ids.Count} detail lines and texts): a circle of each sleeve's size, " +
                                      "a line to each fixture it serves and its id. Undo removes them.", Text);
            }
            catch (Exception ex)
            {
                App.Log("Extract stacks mark: " + ex);
                MessageBox.Show(this, "Could not mark: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Export()
        {
            if (_level == null || _result == null) { MessageBox.Show(this, "Extract a floor first.", Text); return; }
            string path = StackExtractor.DefaultCsv(_uidoc.Document, _level);
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileName(path), InitialDirectory = Path.GetDirectoryName(path) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { StackExtractor.SaveCsv(dlg.FileName, _result); MessageBox.Show(this, $"{_result.Stacks.Count} stack(s) saved to\n{dlg.FileName}", Text); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void Place()
        {
            var stacks = Picked();
            if (_level == null || stacks.Count == 0) { MessageBox.Show(this, "Extract a floor first.", Text); return; }
            int sleeves = stacks.Sum(s => s.Sleeves.Count);
            if (MessageBox.Show(this, $"Place {sleeves} sleeve(s) of {stacks.Count} stack(s) on {_level.Name}?\n\n" +
                                      "Sizes from the rules (plumbing.services); a sleeve already within 6\" is left as it is. Undo removes them.",
                                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var doc = _uidoc.Document;
            Cursor = Cursors.WaitCursor;
            try { MessageBox.Show(this, StackExtractor.Place(doc, _level, stacks, App.Rules(doc).Plumbing), Text); }
            catch (Exception ex)
            {
                App.Log("Extract stacks place: " + ex);
                MessageBox.Show(this, "Could not place: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }
    }
}
