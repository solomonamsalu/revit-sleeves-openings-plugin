using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Commands;
using Color = System.Drawing.Color;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using TextBox = System.Windows.Forms.TextBox;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;
using ComboBox = System.Windows.Forms.ComboBox;
using Item = SleevesOpenings.Commands.WaterExtractor.Item;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Extract CW/HW: tick floors, list the domestic water the model shows (Revit pipes, fixture connectors, heaters, DWG
    /// water circles with their words) and each wet row's hot + cold pair by the office rule; double-click to zoom; mark in
    /// the plan, export CSV, place the office rule's sleeves.
    /// </summary>
    public class ExtractWaterForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly List<Level> _levels;
        private readonly CheckedListBox _floors;
        private readonly ComboBox _filter;
        private readonly Label _summary;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private WaterExtractor.Run _run;
        private List<Item> _shown = new List<Item>();

        private const string AllSources = "All sources";

        public ExtractWaterForm(UIDocument uidoc, Level start)
        {
            _uidoc = uidoc;
            _levels = FixtureExtractor.Levels(uidoc.Document).OrderByDescending(l => l.ProjectElevation).ToList();

            Text = "Sleeves & Openings — Extract CW/HW";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1260, 700);
            MinimumSize = new Size(900, 460);

            var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(12, 10, 6, 10) };
            var floorsLabel = new Label { Text = "Floors:", Dock = DockStyle.Top, Height = 24 };
            _floors = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (var l in _levels) _floors.Items.Add($"{l.Name}   ({FixtureExtractor.Ft(l.ProjectElevation)})", start != null && l.Id == start.Id);
            var pick = new Panel { Dock = DockStyle.Bottom, Height = 76 };
            var allBtn = new Button { Text = "All", Size = new Size(70, 28), Location = new Point(0, 4) };
            var noneBtn = new Button { Text = "None", Size = new Size(70, 28), Location = new Point(76, 4) };
            var extract = new Button { Text = "Extract", Size = new Size(146, 32), Location = new Point(0, 40) };
            pick.Controls.AddRange(new Control[] { allBtn, noneBtn, extract });
            left.Controls.Add(_floors);
            left.Controls.Add(floorsLabel);
            left.Controls.Add(pick);

            var how = new Label
            {
                Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 8, 12, 0), ForeColor = Color.DimGray,
                Text = "Office rule (plumbing.water): one hot + cold pair per wet row at its end — bathroom 9 1/2\" past the toilet's centre line away from the lavatory, sink / washer 11 3/4\" past its vent; " +
                       "5 1/4\" apart, hot on the plan's left / top. Also lists what the model shows: water pipes, fixture connectors, heaters, DWG water circles."
            };
            var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 4, 12, 0) };
            _summary = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Text = "Tick the floors and press Extract.", TextAlign = ContentAlignment.MiddleLeft };
            _filter = new ComboBox { Dock = DockStyle.Right, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            _filter.Items.Add(AllSources);
            _filter.SelectedIndex = 0;
            top.Controls.Add(_summary);
            top.Controls.Add(_filter);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, RowTemplate = { Height = 26 }
            };
            foreach (var (h, w) in new[] { ("Floor", 11), ("Source", 10), ("Kind", 14), ("Sys", 5), ("Size", 5), ("Location (grids)", 15), ("X", 7), ("Y", 7), ("Label", 16), ("From", 16) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Zoom(_shown[e.RowIndex]); };
            _grid.SelectionChanged += (s, e) => ShowDetail();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _shown.Count) return;
                switch (_shown[e.RowIndex].System)
                {
                    case "ColdWater": _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.Blue; break;
                    case "HotWater": _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.Firebrick; break;
                    case "HotWaterReturn": _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.DarkOrange; break;
                    default: _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.DimGray; break;
                }
            };

            _detail = new TextBox { Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12) };
            var zoom = new Button { Text = "Zoom to", Size = new Size(100, 32), Location = new Point(12, 10) };
            var mark = new Button { Text = "Mark in plan", Size = new Size(120, 32), Location = new Point(120, 10) };
            var csv = new Button { Text = "Export CSV…", Size = new Size(120, 32), Location = new Point(248, 10) };
            var place = new Button { Text = "Place sleeves…", Size = new Size(130, 32), Location = new Point(376, 10) };
            var hint = new Label { Text = "Double-click = zoom. Mark / Place use the selected rows (all shown rows when one or none is selected); Place puts in the office rule's rows only.", AutoSize = true,
                                   ForeColor = Color.DimGray, Location = new Point(516, 18) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            bottom.Controls.AddRange(new Control[] { zoom, mark, csv, place, hint, close });
            bottom.Resize += (s, e) => close.Location = new Point(bottom.ClientSize.Width - 112, 10);
            CancelButton = close;

            Controls.Add(_grid);
            Controls.Add(_detail);
            Controls.Add(bottom);
            Controls.Add(top);
            Controls.Add(how);
            Controls.Add(left);

            allBtn.Click += (s, e) => { for (int i = 0; i < _floors.Items.Count; i++) _floors.SetItemChecked(i, true); };
            noneBtn.Click += (s, e) => { for (int i = 0; i < _floors.Items.Count; i++) _floors.SetItemChecked(i, false); };
            extract.Click += (s, e) => Extract();
            _filter.SelectedIndexChanged += (s, e) => Fill();
            zoom.Click += (s, e) => { var r = Current(); if (r != null) Zoom(r); };
            mark.Click += (s, e) => Mark();
            csv.Click += (s, e) => Export();
            place.Click += (s, e) => Place();
        }

        private Item Current() =>
            _grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _shown.Count ? _shown[_grid.CurrentRow.Index] : null;

        /// <summary>The selected rows, or every shown row when one or none is selected.</summary>
        private List<Item> Picked()
        {
            var picked = _grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Index).Where(i => i >= 0 && i < _shown.Count).Select(i => _shown[i]).ToList();
            return picked.Count > 1 ? picked : _shown.ToList();
        }

        private void Extract()
        {
            var levels = _floors.CheckedIndices.Cast<int>().Select(i => _levels[i]).ToList();
            if (levels.Count == 0) { MessageBox.Show(this, "Tick at least one floor.", Text); return; }
            var doc = _uidoc.Document;
            Cursor = Cursors.WaitCursor;
            try
            {
                _run = WaterExtractor.Extract(doc, levels, App.Rules(doc).Plumbing);
                string keep = _filter.SelectedItem as string;
                _filter.Items.Clear();
                _filter.Items.Add(AllSources);
                foreach (var s in _run.Items.Select(i => i.Source).Distinct().OrderBy(s => s)) _filter.Items.Add(s);
                _filter.SelectedItem = keep != null && _filter.Items.Contains(keep) ? keep : AllSources;
                Fill();
            }
            catch (Exception ex)
            {
                App.Log("Extract CW/HW: " + ex);
                MessageBox.Show(this, "Could not extract: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void Fill()
        {
            if (_run == null) return;
            string source = _filter.SelectedItem as string;
            _shown = _run.Items.Where(i => source == null || source == AllSources || i.Source == source).ToList();
            _grid.Rows.Clear();
            foreach (var i in _shown)
                _grid.Rows.Add(i.Floor.Level.Name, i.Source, i.Kind, WaterExtractor.Short(i.System), i.Size > 0 ? $"{i.Size:0.##}\"" : "", i.Grid,
                               FixtureExtractor.Ft(i.X), FixtureExtractor.Ft(i.Y), i.Label, i.From);
            int Count(string sys) => _run.Items.Count(i => i.System == sys);
            _summary.Text = $"{_run.Floors.Count} floor(s): {_run.Items.Count} water item(s) — CW {Count("ColdWater")}, HW {Count("HotWater")}, HWR {Count("HotWaterReturn")}, " +
                            $"not known {Count("?")}" + (_shown.Count != _run.Items.Count ? $"  (showing {_shown.Count})" : "");
            _summary.ForeColor = _run.Items.Count == 0 ? Color.Firebrick : SystemColors.ControlText;
            ShowDetail();
        }

        private void ShowDetail()
        {
            var i = Current();
            if (i == null)
            {
                _detail.Text = _run == null ? "" : string.Join("\r\n", _run.Floors.SelectMany(f => f.Notes.Select(n => $"{f.Level.Name}: {n}")));
                return;
            }
            _detail.Text = $"{i.Floor.Level.Name}: {WaterExtractor.Short(i.System)} {i.Kind}" + (i.Size > 0 ? $" {i.Size:0.##}\"" : "") +
                           $" at X {FixtureExtractor.Ft(i.X)}, Y {FixtureExtractor.Ft(i.Y)} ({i.Grid})\r\n" +
                           $"Source: {i.Source} — {i.From}\r\n" +
                           (string.IsNullOrEmpty(i.Label) ? "" : $"Label: {i.Label}\r\n") +
                           string.Join("\r\n", i.Notes);
        }

        private void Zoom(Item i)
        {
            var doc = _uidoc.Document;
            try
            {
                var level = i.Floor.Level;
                var view = FixtureExtractor.PlanFor(doc, level, _uidoc.ActiveView);
                if (view == null) { MessageBox.Show(this, $"No floor plan of {level.Name} to zoom in.", Text); return; }
                if (_uidoc.ActiveView?.Id != view.Id)
                {
                    try { _uidoc.ActiveView = view; }
                    catch (Exception) { _uidoc.RequestViewChange(view); return; }
                }
                var ui = _uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                double z = level.ProjectElevation;
                ui?.ZoomAndCenterRectangle(new XYZ(i.X - 5, i.Y - 5, z), new XYZ(i.X + 5, i.Y + 5, z));
                if (i.Id != null) _uidoc.Selection.SetElementIds(new List<ElementId> { i.Id });
            }
            catch (Exception ex)
            {
                App.Log("Extract CW/HW zoom: " + ex);
                MessageBox.Show(this, "Could not zoom: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Mark()
        {
            var items = Picked();
            if (items.Count == 0) { MessageBox.Show(this, "Extract first.", Text); return; }
            var doc = _uidoc.Document;
            try
            {
                var done = new List<string>();
                int lines = 0;
                foreach (var g in items.GroupBy(i => i.Floor))
                {
                    var view = FixtureExtractor.PlanFor(doc, g.Key.Level, _uidoc.ActiveView);
                    if (view == null) { done.Add($"{g.Key.Level.Name}: no floor plan to draw in"); continue; }
                    lines += WaterExtractor.Mark(doc, view, g.Key.Level, g).Count;
                    done.Add($"{g.Key.Level.Name}: {g.Count()} item(s) in '{view.Name}'");
                }
                MessageBox.Show(this, string.Join("\n", done) + $"\n\n{lines} detail lines and texts: a circle at each item, a line along each pipe on the floor, CW / HW / HWR as text. Undo removes them.", Text);
            }
            catch (Exception ex)
            {
                App.Log("Extract CW/HW mark: " + ex);
                MessageBox.Show(this, "Could not mark: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Place()
        {
            var items = Picked().Where(i => i.Rule != null).ToList();
            if (items.Count == 0) { MessageBox.Show(this, _run == null ? "Extract first." : "No office rule rows among them: only those are placed.", Text); return; }
            if (MessageBox.Show(this, $"Place {items.Count} hot / cold water sleeve(s) on {items.Select(i => i.Floor).Distinct().Count()} floor(s)?\n\n" +
                                      "Office rule (plumbing.water), flagged for a check. Undo removes them.",
                                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var doc = _uidoc.Document;
            Cursor = Cursors.WaitCursor;
            try { MessageBox.Show(this, WaterExtractor.Place(doc, items), Text); }
            catch (Exception ex)
            {
                App.Log("Extract CW/HW place: " + ex);
                MessageBox.Show(this, "Could not place: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void Export()
        {
            if (_run == null) { MessageBox.Show(this, "Extract first.", Text); return; }
            string path = WaterExtractor.DefaultCsv(_uidoc.Document);
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileName(path), InitialDirectory = Path.GetDirectoryName(path) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { WaterExtractor.SaveCsv(dlg.FileName, _shown, _run); MessageBox.Show(this, $"{_shown.Count} item(s) saved to\n{dlg.FileName}", Text); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
    }
}
