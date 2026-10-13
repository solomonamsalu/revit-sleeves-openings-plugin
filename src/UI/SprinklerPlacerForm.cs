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
using ComboBox = System.Windows.Forms.ComboBox;
using Item = SleevesOpenings.Commands.SprinklerExtractor.Item;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Sprinkler Placer: tick floors (all of them: the spot is the one free on every floor), lay out the riser sleeves in
    /// the stairs by the manual's p16; double-click to zoom; step a riser to another corner, swap its kind, change its pipe,
    /// add one by clicking; mark in the plan, export CSV, place.
    /// </summary>
    public class SprinklerPlacerForm : Form
    {
        private readonly UIDocument _uidoc;
        private readonly List<Level> _levels;
        private readonly CheckedListBox _floors;
        private readonly Label _summary;
        private readonly DataGridView _grid;
        private readonly TextBox _detail;
        private readonly ComboBox _pipe;
        private SprinklerExtractor.Run _run;
        private List<Item> _shown = new List<Item>();
        private bool _filling;

        private static readonly double[] Pipes = { 1.5, 2, 2.5, 3, 4, 5, 6, 8 };

        public SprinklerPlacerForm(UIDocument uidoc, Level start, SprinklerExtractor.Run run = null)
        {
            _uidoc = uidoc;
            _run = run;
            _levels = FixtureExtractor.Levels(uidoc.Document).OrderByDescending(l => l.ProjectElevation).ToList();

            Text = "Sleeves & Openings — Sprinkler Placer";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1260, 700);
            MinimumSize = new Size(900, 460);

            var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(12, 10, 6, 10) };
            var floorsLabel = new Label { Text = "Floors (tick all):", Dock = DockStyle.Top, Height = 24 };
            _floors = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (var l in _levels) _floors.Items.Add($"{l.Name}   ({FixtureExtractor.Ft(l.ProjectElevation)})", run == null || run.Levels.Any(x => x.Id == l.Id));
            var pick = new Panel { Dock = DockStyle.Bottom, Height = 76 };
            var allBtn = new Button { Text = "All", Size = new Size(70, 28), Location = new Point(0, 4) };
            var noneBtn = new Button { Text = "None", Size = new Size(70, 28), Location = new Point(76, 4) };
            var extract = new Button { Text = "Lay out", Size = new Size(146, 32), Location = new Point(0, 40) };
            pick.Controls.AddRange(new Control[] { allBtn, noneBtn, extract });
            left.Controls.Add(_floors);
            left.Controls.Add(floorsLabel);
            left.Controls.Add(pick);

            var rules = App.Rules(uidoc.Document);
            var how = new Label
            {
                Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 8, 12, 0), ForeColor = Color.DimGray,
                Text = $"Manual p16: sleeves in the stairwell, the same spot top to bottom, {Units.FormatInches(rules.Systems.Standpipe.MinCenterToWall)} pipe centre to any wall, sleeve = pipe + {Units.FormatInches(rules.Systems.Standpipe.SleeveOverPipe)}. " +
                       "One riser per stair enclosure (Combined, then Standpipe Only), on a landing corner clear of doors, steps and columns. No sleeve in the slab on grade or the bulkhead roof."
            };
            var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 4, 12, 0) };
            _summary = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Text = "Tick the floors and press Lay out.", TextAlign = ContentAlignment.MiddleLeft };
            top.Controls.Add(_summary);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, RowTemplate = { Height = 26 }
            };
            foreach (var (h, w) in new[] { ("Floor", 12), ("Name", 6), ("Kind", 16), ("Stair", 9), ("Pipe", 5), ("Sleeve", 5), ("Location (grids)", 16), ("X", 7), ("Y", 7), ("Check", 6), ("Where", 24) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w });
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Zoom(_shown[e.RowIndex]); };
            _grid.SelectionChanged += (s, e) => ShowDetail();
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _shown.Count) return;
                var i = _shown[e.RowIndex];
                _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = i.Riser.Review || !i.StairShown ? Color.DarkOrange : Color.Firebrick;
            };

            _detail = new TextBox { Dock = DockStyle.Bottom, Height = 120, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };

            var tools = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(12, 6, 12, 0) };
            var next = new Button { Text = "Next corner", Size = new Size(110, 32), Location = new Point(12, 6) };
            var swap = new Button { Text = "Swap kind", Size = new Size(100, 32), Location = new Point(128, 6) };
            var pipeLabel = new Label { Text = "Pipe:", AutoSize = true, Location = new Point(240, 13) };
            _pipe = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Location = new Point(284, 9) };
            foreach (var p in Pipes) _pipe.Items.Add($"{p:0.#}\"");
            var add = new Button { Text = "Add riser…", Size = new Size(110, 32), Location = new Point(366, 6) };
            var toolHint = new Label { Text = "These change the selected row's riser on every floor.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(486, 13) };
            tools.Controls.AddRange(new Control[] { next, swap, pipeLabel, _pipe, add, toolHint });

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
            Controls.Add(tools);
            Controls.Add(bottom);
            Controls.Add(top);
            Controls.Add(how);
            Controls.Add(left);

            allBtn.Click += (s, e) => { for (int i = 0; i < _floors.Items.Count; i++) _floors.SetItemChecked(i, true); };
            noneBtn.Click += (s, e) => { for (int i = 0; i < _floors.Items.Count; i++) _floors.SetItemChecked(i, false); };
            extract.Click += (s, e) => Extract();
            zoom.Click += (s, e) => { var r = Current(); if (r != null) Zoom(r); };
            mark.Click += (s, e) => Mark();
            csv.Click += (s, e) => Export();
            place.Click += (s, e) => Place();
            next.Click += (s, e) => NextCorner();
            swap.Click += (s, e) => Swap();
            _pipe.SelectedIndexChanged += (s, e) => SetPipe();
            add.Click += (s, e) => AddRiser();
            if (_run != null) Shown += (s, e) => { Fill(); var last = _run.Plan.Risers.LastOrDefault(); if (last != null) Rebuild(last); };
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
                _run = SprinklerExtractor.Extract(doc, levels, App.Rules(doc));
                Fill();
            }
            catch (Exception ex)
            {
                App.Log("Sprinkler placer: " + ex);
                MessageBox.Show(this, "Could not lay out: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        /// <summary>The list again after a riser changed, keeping the selected riser.</summary>
        private void Rebuild(StairFinder.Riser keep)
        {
            SprinklerExtractor.Build(_uidoc.Document, _run, App.Rules(_uidoc.Document));
            Fill();
            int row = _shown.FindIndex(i => i.Riser == keep);
            if (row >= 0) { _grid.ClearSelection(); _grid.CurrentCell = _grid.Rows[row].Cells[0]; _grid.Rows[row].Selected = true; }
        }

        private void Fill()
        {
            if (_run == null) return;
            _filling = true;
            _shown = _run.Items.ToList();
            _grid.Rows.Clear();
            foreach (var i in _shown)
                _grid.Rows.Add(i.Level.Name, i.Name, i.Kind, i.Riser.Stair, $"{i.Pipe:0.#}\"", $"{i.Sleeve:0.#}\"", i.Grid,
                               FixtureExtractor.Ft(i.X), FixtureExtractor.Ft(i.Y), i.Riser.Review || !i.StairShown ? "check" : "",
                               i.Riser.Corner?.Where ?? i.Riser.Notes.FirstOrDefault());
            var risers = _run.Plan.Risers;
            _summary.Text = risers.Count == 0
                ? "No stair found on the ticked floors' drawings: use Add riser… to place it by hand."
                : $"{risers.Count} riser(s) on {_shown.Select(i => i.Level).Distinct().Count()} floor(s): " +
                  string.Join(", ", risers.Select(r => $"{r.Name} {r.Kind} in {r.Stair}")) + (risers.Any(r => r.Review) ? " — check the orange rows" : "");
            _summary.ForeColor = risers.Count == 0 ? Color.Firebrick : SystemColors.ControlText;
            _filling = false;
            ShowDetail();
        }

        private void ShowDetail()
        {
            var i = Current();
            if (_run == null) { _detail.Text = ""; return; }
            if (i == null)
            {
                _detail.Text = string.Join("\r\n", _run.Plan.Notes.Concat(_run.Plan.Floors.SelectMany(f => f.Notes.Select(n => $"{f.Level}: {n}"))));
                return;
            }
            _filling = true;
            int p = Array.IndexOf(Pipes, i.Pipe);
            _pipe.SelectedIndex = p;
            _filling = false;
            var r = i.Riser;
            _detail.Text = $"{i.Level.Name}: {i.Name} {i.Kind} ({r.Label}), {i.Pipe:0.#}\" pipe, {i.Sleeve:0.#}\" sleeve at X {FixtureExtractor.Ft(i.X)}, Y {FixtureExtractor.Ft(i.Y)} ({i.Grid})\r\n" +
                           (r.Options.Count > 0 ? $"Spot {r.Option + 1} of {r.Options.Count} (Next corner): free on {r.Options[r.Option].Floors} floor(s)\r\n" : "") +
                           (i.StairShown ? "" : "This floor's drawings do not show the stair: same spot as the floors that do.\r\n") +
                           string.Join("\r\n", r.Notes) + "\r\n" +
                           string.Join("\r\n", _run.Plan.Notes);
        }

        private void NextCorner()
        {
            var i = Current();
            if (i == null) { MessageBox.Show(this, "Select a row first.", Text); return; }
            var r = i.Riser;
            if (r.Options.Count < 2) { MessageBox.Show(this, "No other corner found for this riser: use Add riser… to place it by hand.", Text); return; }
            r.Option = (r.Option + 1) % r.Options.Count;
            if (r.Corner.Landing.Name != null) r.Stair = r.Corner.Landing.Name;
            r.Review = r.Options[r.Option].Floors < r.Options.Max(o => o.Floors) || !r.Corner.Landing.Enclosed;
            StairFinder.Spot(r, App.Rules(_uidoc.Document).Systems.Standpipe.MinCenterToCenter);
            Rebuild(r);
        }

        private void Swap()
        {
            var i = Current();
            if (i == null) { MessageBox.Show(this, "Select a row first.", Text); return; }
            // Combined (with its drain) -> Standpipe Only -> Sprinkler Riser -> Combined
            var r = i.Riser;
            var rules = App.Rules(_uidoc.Document);
            r.Kind = r.Kind == StairFinder.Combined ? StairFinder.Standpipe : r.Kind == StairFinder.Standpipe ? StairFinder.Sprinkler : StairFinder.Combined;
            r.Drain = r.Kind == StairFinder.Combined ? rules.SprinklerPlacer?.DrainPipe ?? 2 : 0;
            StairFinder.Spot(r, rules.Systems.Standpipe.MinCenterToCenter);
            Rebuild(i.Riser);
        }

        private void SetPipe()
        {
            if (_filling || _pipe.SelectedIndex < 0) return;
            var i = Current();
            if (i == null) return;
            if (i.IsDrain) i.Riser.Drain = Pipes[_pipe.SelectedIndex];
            else i.Riser.Pipe = Pipes[_pipe.SelectedIndex];
            Rebuild(i.Riser);
        }

        /// <summary>The layout so far: the command keeps it while the window is closed for a click (Add riser).</summary>
        public SprinklerExtractor.Run LaidOut => _run;

        /// <summary>Add riser: the window closes (Retry) so the user can click in the plan; the command adds the riser and opens it again.</summary>
        private void AddRiser()
        {
            if (_run == null) { MessageBox.Show(this, "Lay out first (tick the floors that get the riser).", Text); return; }
            if (!(_uidoc.ActiveView is ViewPlan)) { MessageBox.Show(this, "Open a floor plan first, then click the riser's spot in it.", Text); return; }
            DialogResult = DialogResult.Retry;
            Close();
        }

        private void Zoom(Item i)
        {
            var doc = _uidoc.Document;
            try
            {
                var level = i.Level;
                var view = FixtureExtractor.PlanFor(doc, level, _uidoc.ActiveView);
                if (view == null) { MessageBox.Show(this, $"No floor plan of {level.Name} to zoom in.", Text); return; }
                if (_uidoc.ActiveView?.Id != view.Id)
                {
                    try { _uidoc.ActiveView = view; }
                    catch (Exception) { _uidoc.RequestViewChange(view); return; }
                }
                var ui = _uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                double z = level.ProjectElevation;
                ui?.ZoomAndCenterRectangle(new XYZ(i.X - 6, i.Y - 6, z), new XYZ(i.X + 6, i.Y + 6, z));
            }
            catch (Exception ex)
            {
                App.Log("Sprinkler placer zoom: " + ex);
                MessageBox.Show(this, "Could not zoom: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Mark()
        {
            var items = Picked();
            if (items.Count == 0) { MessageBox.Show(this, "Lay out first.", Text); return; }
            var doc = _uidoc.Document;
            try
            {
                var done = new List<string>();
                int lines = 0;
                foreach (var g in items.GroupBy(i => i.Level))
                {
                    var view = FixtureExtractor.PlanFor(doc, g.Key, _uidoc.ActiveView);
                    if (view == null) { done.Add($"{g.Key.Name}: no floor plan to draw in"); continue; }
                    lines += SprinklerExtractor.Mark(doc, view, g.Key, g).Count;
                    done.Add($"{g.Key.Name}: {g.Count()} riser(s) in '{view.Name}'");
                }
                MessageBox.Show(this, string.Join("\n", done) + $"\n\n{lines} detail lines and texts: each sleeve as a crossed circle with its name. Undo removes them.", Text);
            }
            catch (Exception ex)
            {
                App.Log("Sprinkler placer mark: " + ex);
                MessageBox.Show(this, "Could not mark: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Place()
        {
            var items = Picked();
            if (items.Count == 0) { MessageBox.Show(this, _run == null ? "Lay out first." : "Nothing to place.", Text); return; }
            int check = items.Count(i => i.Riser.Review || !i.StairShown);
            if (MessageBox.Show(this, $"Place {items.Count} sprinkler / standpipe sleeve(s) ({string.Join(", ", items.Select(i => i.Name).Distinct())}) on {items.Select(i => i.Level).Distinct().Count()} floor(s)?\n\n" +
                                      (check > 0 ? $"{check} of them are flagged for a check. " : "") + "Undo removes them.",
                                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var doc = _uidoc.Document;
            Cursor = Cursors.WaitCursor;
            try { MessageBox.Show(this, SprinklerExtractor.Place(doc, items), Text); }
            catch (Exception ex)
            {
                App.Log("Sprinkler placer place: " + ex);
                MessageBox.Show(this, "Could not place: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void Export()
        {
            if (_run == null) { MessageBox.Show(this, "Lay out first.", Text); return; }
            string path = SprinklerExtractor.DefaultCsv(_uidoc.Document);
            using (var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileName(path), InitialDirectory = Path.GetDirectoryName(path) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { SprinklerExtractor.SaveCsv(dlg.FileName, _shown, _run); MessageBox.Show(this, $"{_shown.Count} row(s) saved to\n{dlg.FileName}", Text); }
                catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
    }
}
