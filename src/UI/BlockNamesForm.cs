using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SleevesOpenings.Automation;
using SleevesOpenings.Automation.Drawings;
using Button = System.Windows.Forms.Button;
using Color = System.Drawing.Color;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using Label = System.Windows.Forms.Label;
using Panel = System.Windows.Forms.Panel;
using Point = System.Drawing.Point;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Name the architect's blocks once: each kind of block the floor's DWG holds, with how many, its size and layer,
    /// and what it is (toilet, lavatory, kitchen sink, tub, shower, washer, floor drain, or "not a fixture" for doors,
    /// ranges, furniture). A suggestion from the name is pre-filled in grey until confirmed. "Show in plan" marks every
    /// insert of the selected kinds. Saved for the office (fixture-blocks.json), so every floor and project knows them.
    /// </summary>
    public class BlockNamesForm : Form
    {
        private static readonly string[] Choices = { "", "WC", "LAV", "KS", "LS", "BT", "SH", "W/D", "FD", "none" };
        private static readonly Dictionary<string, string> Meaning = new Dictionary<string, string>
        {
            [""] = "(not named)", ["WC"] = "WC – toilet", ["LAV"] = "LAV – lavatory", ["KS"] = "KS – kitchen sink", ["LS"] = "LS – laundry sink",
            ["BT"] = "BT – bathtub", ["SH"] = "SH – shower", ["W/D"] = "W/D – washer", ["FD"] = "FD – floor drain", ["none"] = "none – not a fixture"
        };

        private readonly DataGridView _grid;
        private readonly List<string> _names;
        private readonly HashSet<int> _suggested = new HashSet<int>();

        public BlockNamesForm(List<DwgFixtureCatalog.Item> blocks, Action<List<string>> mark)
        {
            var map = FixtureBlockMap.Load();
            var kinds = blocks.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.First().Code == null ? 0 : 1).ThenBy(g => g.Key).ToList();
            _names = kinds.Select(g => g.Key).ToList();

            Text = "Sleeves & Openings — Name the DWG's blocks";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1040, 560);
            MinimumSize = new Size(760, 360);

            var intro = new Label
            {
                Dock = DockStyle.Top, Height = 54, Padding = new Padding(12, 8, 12, 0),
                Text = "Say once what each block of the architect's drawing is. Grey = a guess from its name: check it, then Save. " +
                       "Doors, ranges and furniture are 'none – not a fixture'. When a block holds another ('inside …'), name the inner one " +
                       "(the toilet itself, not toilet + cleanout). Select rows and 'Show in plan' to see them."
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true, RowTemplate = { Height = 26 }, EditMode = DataGridViewEditMode.EditOnEnter
            };
            foreach (var (h, w) in new[] { ("Block", 30), ("How many", 8), ("Size", 10), ("Layer", 14), ("Contains", 14) })
                _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, FillWeight = w, ReadOnly = true });
            var what = new DataGridViewComboBoxColumn { HeaderText = "It is", FillWeight = 20, FlatStyle = FlatStyle.Flat };
            foreach (var c in Choices) what.Items.Add(Meaning[c]);
            _grid.Columns.Add(what);

            for (int i = 0; i < kinds.Count; i++)
            {
                var g = kinds[i]; var b = g.First();
                string code = map.TryGetValue(b.Name, out var saved) ? saved : null;
                if (code == null) { code = FixtureBlockMap.Suggest(b.Name); if (code != null) _suggested.Add(i); }
                _grid.Rows.Add(b.Name, g.Count(), $"{b.X1 - b.X0:0}\" x {b.Y1 - b.Y0:0}\"", b.Layer, string.Join(", ", b.Inner.Take(3)),
                               Meaning.TryGetValue(code ?? "", out var m) ? m : Meaning[""]);
                if (_suggested.Contains(i)) _grid.Rows[i].Cells[5].Style.ForeColor = Color.Gray;
            }
            // the blocks inside them (the bowl block in a toilet-and-cleanout block): naming the inner one makes it the fixture
            foreach (var inner in blocks.Where(b => b.Code == null).SelectMany(b => b.Inner.Select(n => (Inner: n, Outer: b.Name))).GroupBy(t => t.Inner, StringComparer.OrdinalIgnoreCase))
            {
                if (_names.Contains(inner.Key, StringComparer.OrdinalIgnoreCase)) continue;
                int i = _names.Count;
                _names.Add(inner.Key);
                string code = map.TryGetValue(inner.Key, out var saved) ? saved : null;
                if (code == null) { code = FixtureBlockMap.Suggest(inner.Key); if (code != null) _suggested.Add(i); }
                int count = blocks.Count(b => b.Inner.Contains(inner.Key, StringComparer.OrdinalIgnoreCase));
                _grid.Rows.Add(inner.Key, count, "", "inside " + string.Join(", ", inner.Select(t => t.Outer).Distinct().Take(2)), "",
                               Meaning.TryGetValue(code ?? "", out var m) ? m : Meaning[""]);
                if (_suggested.Contains(i)) _grid.Rows[i].Cells[5].Style.ForeColor = Color.Gray;
            }
            _grid.CellValueChanged +=(s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 5) _grid.Rows[e.RowIndex].Cells[5].Style.ForeColor = Color.Black; };
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.DataError += (s, e) => { e.ThrowException = false; };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(12) };
            var show = new Button { Text = "Show in plan", Size = new Size(130, 32), Location = new Point(12, 10) };
            var hint = new Label { Text = $"Saved for every project in {FixtureBlockMap.FilePath}", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(156, 18) };
            var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Size = new Size(100, 32) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            bottom.Controls.AddRange(new Control[] { show, hint, save, cancel });
            bottom.Resize += (s, e) => { cancel.Location = new Point(bottom.ClientSize.Width - 112, 10); save.Location = new Point(bottom.ClientSize.Width - 220, 10); };
            AcceptButton = save; CancelButton = cancel;

            Controls.Add(_grid);
            Controls.Add(bottom);
            Controls.Add(intro);

            show.Click += (s, e) =>
            {
                var picked = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => _names[r.Index]).ToList();
                if (picked.Count == 0 && _grid.CurrentRow != null) picked.Add(_names[_grid.CurrentRow.Index]);
                if (picked.Count > 0) mark(picked);
            };
            save.Click += (s, e) => Save(map);
        }

        private void Save(Dictionary<string, string> map)
        {
            _grid.EndEdit();
            for (int i = 0; i < _names.Count; i++)
            {
                string shown = _grid.Rows[i].Cells[5].Value as string ?? "";
                string code = Meaning.FirstOrDefault(kv => kv.Value == shown).Key ?? "";
                if (code == "") map.Remove(_names[i]);
                else map[_names[i]] = code;
            }
            try
            {
                FixtureBlockMap.Save(map);
                App.Log("Fixture blocks named: " + string.Join(", ", _names.Where(map.ContainsKey).Select(n => $"{n}={map[n]}")));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        }
    }
}
