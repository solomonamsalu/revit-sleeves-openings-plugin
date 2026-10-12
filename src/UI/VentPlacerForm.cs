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
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using TextBox = System.Windows.Forms.TextBox;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;
using Item = SleevesOpenings.Commands.VentExtractor.Item;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Vent Placer: tick floors, lay out their vent sleeves from the model (each fixture's vent in its wet wall, one vent
    /// stack per bathroom row), see each one and why it is there; double-click to zoom; mark in the plan, export CSV, place.
    /// </summary>
    public class VentPlacerForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly List<Level> _levels;
        private readonly CheckedListBox _floors;
        private readonly Label _summary;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private VentExtractor.Run _run;
        private List<Item> _shown = new List<Item>();

        public VentPlacerForm(UIDocument uidoc, Level start)
        {
            _uidoc = uidoc;
            _levels = FixtureExtractor.Levels(uidoc.Document).OrderByDescending(l => l.ProjectElevation).ToList();

            Text = "Sleeves & Openings — Vent Placer";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1220, 700);
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
                Text = "From the model only (the fixtures Extract Fixtures finds, the sleeves already placed), by the manual's toilet sleeves page: " +
                       "each toilet's vent touching its waste stack, the lavatory's 7\" past it, tub / sink / washer vents, one vent stack per bathroom row (shared back to back), in line floor to floor."
            };
            _summary = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 6, 12, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                                   Text = "Tick the floors and press Extract." };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, RowTemplate = { Height = 26 }
            };
            foreach (var (h, w) in new[] { ("Floor", 12), ("Row", 5), ("Row kind", 9), ("Name", 9), ("Kind", 11), ("Size", 5), ("Location (grids)", 18), ("X", 7), ("Y", 7), ("Notes", 17) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Zoom(_shown[e.RowIndex]); };
            _grid.SelectionChanged += (s, e) => ShowDetail();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _shown.Count) return;
                _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = _shown[e.RowIndex].Vent.Notes.Count > 0 ? Color.DarkGoldenrod : Color.Black;
            };

            _detail = new TextBox { Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };

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
            Controls.Add(how);
            Controls.Add(left);

            allBtn.Click += (s, e) => { for (int i = 0; i < _floors.Items.Count; i++) _floors.SetItemChecked(i, true); };
            noneBtn.Click += (s, e) => { for (int i = 0; i < _floors.Items.Count; i++) _floors.SetItemChecked(i, false); };
            extract.Click += (s, e) => Extract();
            zoom.Click += (s, e) => { var r = Current(); if (r != null) Zoom(r); };
            mark.Click += (s, e) => Mark();
            csv.Click += (s, e) => Export();
            place.Click += (s, e) => Place();
        }

        private Item Current() =>
            _grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _shown.Count ? _shown[_grid.CurrentRow.Index] : null;

        /// <summary>The selected rows, or every row when one or none is selected.</summary>
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
                _run = VentExtractor.Extract(doc, levels, App.Rules(doc).Plumbing);
                _shown = _run.Items.ToList();
                _grid.Rows.Clear();
                foreach (var i in _shown)
                    _grid.Rows.Add(i.Floor.Level.Name, i.Row.Id, i.Row.Kind, i.Vent.Name, i.Vent.Kind, $"{i.Vent.Size:0}\"", i.Grid,
                                   FixtureExtractor.Ft(i.Vent.X), FixtureExtractor.Ft(i.Vent.Y), string.Join("; ", i.Vent.Notes));
                int stacks = _shown.Count(i => i.Vent.IsStack), skipped = _run.Floors.Sum(f => f.Rows.Sum(r => r.Skipped.Count));
                int fixtures = _run.Floors.Sum(f => f.Fixtures);
                _summary.Text = $"{_run.Floors.Count(f => !f.OnGrade)} floor(s): {_shown.Count} vent sleeve(s) ({stacks} vent stack(s), {_shown.Count - stacks} fixture vent(s)) " +
                                $"from {fixtures} fixture(s)" + (skipped > 0 ? $"  —  {skipped} not placed (see the detail below)" : "");
                _summary.ForeColor = fixtures == 0 ? Color.Firebrick : SystemColors.ControlText;
                ShowDetail();
            }
            catch (Exception ex)
            {
                App.Log("Vent placer: " + ex);
                MessageBox.Show(this, "Could not extract: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void ShowDetail()
        {
            var i = Current();
            if (i == null)
            {
                if (_run == null) { _detail.Text = ""; return; }
                _detail.Text = string.Join("\r\n", _run.Floors.SelectMany(f => f.Notes.Select(n => $"{f.Level.Name}: {n}")
                                                       .Concat(f.Rows.SelectMany(r => r.Skipped.Select(s => $"{f.Level.Name} {r.Id}: not placed: {s}")))));
                return;
            }
            var v = i.Vent;
            _detail.Text = $"{i.Floor.Level.Name} {i.Row.Id} ({i.Row.Kind}): {v.Name} {v.Size:0}\" {v.Kind} at X {FixtureExtractor.Ft(v.X)}, Y {FixtureExtractor.Ft(v.Y)}: {v.How}\r\n" +
                           (v.Notes.Count > 0 ? string.Join("\r\n", v.Notes) + "\r\n" : "") +
                           $"Row serves: {string.Join(";  ", i.Row.Serves.Select(f => $"{f.Code} ({f.Source})"))}" +
                           (i.Row.Notes.Count > 0 ? "\r\n" + string.Join("\r\n", i.Row.Notes) : "") +
                           (i.Row.Skipped.Count > 0 ? "\r\nNot placed: " + string.Join(";  ", i.Row.Skipped) : "");
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
                var xs = i.Row.Vents.Select(v => v.X).Concat(i.Row.Serves.Select(f => f.X)).DefaultIfEmpty(i.Vent.X).ToList();
                var ys = i.Row.Vents.Select(v => v.Y).Concat(i.Row.Serves.Select(f => f.Y)).DefaultIfEmpty(i.Vent.Y).ToList();
                var ui = _uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                double z = level.ProjectElevation;
                ui?.ZoomAndCenterRectangle(new XYZ(xs.Min() - 4, ys.Min() - 4, z), new XYZ(xs.Max() + 4, ys.Max() + 4, z));
            }
            catch (Exception ex)
            {
                App.Log("Vent placer zoom: " + ex);
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
                    lines += VentExtractor.Mark(doc, view, g.Key.Level, g).Count;
                    done.Add($"{g.Key.Level.Name}: {g.Count()} vent(s) in '{view.Name}'");
                }
                MessageBox.Show(this, string.Join("\n", done) + $"\n\n{lines} detail lines and texts: a circle of each sleeve's size, a line from each vent stack to its row's fixtures and the name. Undo removes them.", Text);
            }
            catch (Exception ex)
            {
                App.Log("Vent placer mark: " + ex);
                MessageBox.Show(this, "Could not mark: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Export()
        {
            if (_run == null) { MessageBox.Show(this, "Extract first.", Text); return; }
            string path = VentExtractor.DefaultCsv(_uidoc.Document);
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileName(path), InitialDirectory = Path.GetDirectoryName(path) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { VentExtractor.SaveCsv(dlg.FileName, _shown, _run); MessageBox.Show(this, $"{_shown.Count} vent sleeve(s) saved to\n{dlg.FileName}", Text); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void Place()
        {
            var items = Picked();
            if (items.Count == 0) { MessageBox.Show(this, "Extract first.", Text); return; }
            if (MessageBox.Show(this, $"Place {items.Count} vent sleeve(s) on {items.Select(i => i.Floor).Distinct().Count()} floor(s)?\n\n" +
                                      "Sizes from the rules (plumbing.vents, plumbing.fixtures); every one is flagged for a check. Undo removes them.",
                                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var doc = _uidoc.Document;
            Cursor = Cursors.WaitCursor;
            try { MessageBox.Show(this, VentExtractor.Place(doc, items), Text); }
            catch (Exception ex)
            {
                App.Log("Vent placer place: " + ex);
                MessageBox.Show(this, "Could not place: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }
    }
}
