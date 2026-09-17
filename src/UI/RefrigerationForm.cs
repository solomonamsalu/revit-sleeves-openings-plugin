using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Panel = System.Windows.Forms.Panel;
using Control = System.Windows.Forms.Control;
using Autodesk.Revit.DB;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.UI
{
    /// <summary>One apartment stack served by one refrigeration riser.</summary>
    public class RefrigerationStack
    {
        public string Name;
        public int UnitsPerFloor;       // indoor units per floor (split) or refrigeration pairs per floor (VRF)
        public Level From, To;          // floors served (inclusive)
        public Level Condenser;         // roof / setback where the condensers sit
        public double Width, Length;    // inches
        public int LinesPerFloor;
        public int TotalLines;
        public XYZ Point;
    }

    /// <summary>Refrigeration quick rules 1-25: system type → line count per stack → where each riser runs.</summary>
    public class RefrigerationForm : System.Windows.Forms.Form
    {
        private readonly RefrigerationRules _rules;
        private readonly LevelMap _levels;
        private readonly ComboBox _system;
        private readonly DataGridView _grid;
        private readonly Label _coverage;
        private readonly string[] _levelNames, _roofNames;

        public bool IsPtac => _system.SelectedIndex == 0;
        public bool IsSplit => _system.SelectedIndex == 1;
        public List<RefrigerationStack> Stacks { get; private set; } = new List<RefrigerationStack>();

        public RefrigerationForm(LevelMap levels, RefrigerationRules rules)
        {
            _rules = rules; _levels = levels;
            _levelNames = levels.All.Select(l => l.Name).Reverse().ToArray();
            _roofNames = levels.All.Where(l => l.Role == LevelRole.Roof || l.Role == LevelRole.Setback).Select(l => l.Name).Reverse().ToArray();
            if (_roofNames.Length == 0) _roofNames = _levelNames;

            Text = "Sleeves & Openings — Refrigeration Line Planner";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 560);
            MinimumSize = new Size(900, 450);

            var info = new Label
            {
                Dock = DockStyle.Top, Height = 70, Padding = new Padding(12, 10, 12, 0), ForeColor = Color.DimGray,
                Text = "Check the AC system first (rule 1-4). PTAC = no refrigeration lines. Split = 2 lines per indoor unit. " +
                       "VRF/VRV = indoor units share pairs; enter pairs per floor (most projects: 1). " +
                       "Group apartments by stack (rule 8), give each stack its floors and the roof its condensers sit on (rules 10-13, 19). " +
                       "Lines that come from below need no penetration above (rule 13) — that is what 'To' expresses."
            };

            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 8, 12, 0) };
            top.Controls.Add(new Label { Text = "AC system:", AutoSize = true, Location = new Point(12, 12) });
            _system = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Location = new Point(100, 8) };
            _system.Items.AddRange(new object[] { "PTAC (no refrigeration lines)", "Traditional split (2 lines per indoor unit)", "VRF / VRV (shared pairs)" });
            _system.SelectedIndex = 1;
            _system.SelectedIndexChanged += (o, e) => { _grid.Enabled = !IsPtac; Recalc(); };
            top.Controls.Add(_system);
            var add = new Button { Text = "Add stack", Size = new Size(110, 30), Location = new Point(420, 7) };
            var remove = new Button { Text = "Remove", Size = new Size(90, 30), Location = new Point(540, 7) };
            add.Click += (o, e) => AddRow();
            remove.Click += (o, e) => { if (_grid.CurrentRow != null) _grid.Rows.Remove(_grid.CurrentRow); Recalc(); };
            top.Controls.AddRange(new Control[] { add, remove });

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 30 }
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Stack", Name = "Stack", FillWeight = 12 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Units / pairs per floor", Name = "Units", FillWeight = 16 });
            _grid.Columns.Add(Combo("From (lowest floor)", "From", _levelNames, 18));
            _grid.Columns.Add(Combo("To (highest floor)", "To", _levelNames, 18));
            _grid.Columns.Add(Combo("Condensers on", "Cond", _roofNames, 18));
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Lines", Name = "Lines", ReadOnly = true, FillWeight = 10 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "W (in)", Name = "W", FillWeight = 10 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "L (in)", Name = "L", FillWeight = 10 });
            _grid.DataError += (o, e) => e.ThrowException = false;
            _grid.CellValueChanged += (o, e) => Recalc();
            _grid.CurrentCellDirtyStateChanged += (o, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            _coverage = new Label { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(12, 6, 12, 0), ForeColor = Color.Firebrick };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
            var ok = new Button { Text = "Place openings…", DialogResult = DialogResult.OK, Size = new Size(150, 32) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            var hint = new Label { Text = "Next: click one location per stack (closets, shafts, mech rooms — rule 17-18). Roof openings get +3\" W/L (rule 23).", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(12, 20) };
            bottom.Controls.AddRange(new Control[] { hint, ok, cancel });
            bottom.Resize += (o, e) => { cancel.Location = new Point(bottom.ClientSize.Width - 112, 12); ok.Location = new Point(cancel.Left - 160, 12); };

            Controls.Add(_grid);
            Controls.Add(top);
            Controls.Add(info);
            Controls.Add(_coverage);
            Controls.Add(bottom);
            AcceptButton = ok; CancelButton = cancel;

            foreach (var name in new[] { "A", "B" }) AddRow(name);
            Recalc();
        }

        private static DataGridViewComboBoxColumn Combo(string header, string name, string[] items, int weight)
        {
            var c = new DataGridViewComboBoxColumn { HeaderText = header, Name = name, FillWeight = weight, FlatStyle = FlatStyle.Flat };
            c.Items.AddRange(items);
            return c;
        }

        private void AddRow(string name = null)
        {
            name = name ?? ((char)('A' + _grid.Rows.Count)).ToString();
            var lowestApt = _levels.Apartments.FirstOrDefault()?.Name ?? _levelNames.Last();
            var highestApt = _levels.HighestApartment?.Name ?? _levelNames.First();
            var roof = _levels.MainRoof?.Name ?? _roofNames.First();
            _grid.Rows.Add(name, 1, lowestApt, highestApt, roof, 0, "", "");
            Recalc();
        }

        private void Recalc()
        {
            if (_grid == null || _coverage == null) return;
            _grid.EndEdit();
            Stacks = new List<RefrigerationStack>();
            var covered = new HashSet<ElementId>();
            if (IsPtac) { _coverage.Text = "PTAC: no refrigeration line coordination required (rule 2)."; return; }

            foreach (DataGridViewRow row in _grid.Rows)
            {
                var from = LevelByName(row.Cells["From"].Value); var to = LevelByName(row.Cells["To"].Value); var cond = LevelByName(row.Cells["Cond"].Value);
                int units = int.TryParse(Convert.ToString(row.Cells["Units"].Value), out var u) ? u : 0;
                if (from == null || to == null || cond == null) continue;
                if (from.Elevation > to.Elevation) { var tmp = from; from = to; to = tmp; }

                var st = new RefrigerationStack { Name = Convert.ToString(row.Cells["Stack"].Value), UnitsPerFloor = units, From = from, To = to, Condenser = cond };
                st.LinesPerFloor = units * (_system.SelectedIndex == 1 ? _rules.LinesPerIndoorUnitSplit : 2);
                var floors = _levels.All.Where(l => l.Elevation >= from.Elevation && l.Elevation <= to.Elevation).ToList();
                st.TotalLines = st.LinesPerFloor * floors.Count;
                foreach (var f in floors) covered.Add(f.Level.Id);

                var (sw, sl) = _rules.SuggestSize(st.TotalLines);
                if (!InputForm.TryParseInches(Convert.ToString(row.Cells["W"].Value), out st.Width)) { st.Width = sw; row.Cells["W"].Value = sw.ToString("0.#"); }
                if (!InputForm.TryParseInches(Convert.ToString(row.Cells["L"].Value), out st.Length)) { st.Length = sl; row.Cells["L"].Value = sl.ToString("0.#"); }
                row.Cells["Lines"].Value = st.TotalLines;
                Stacks.Add(st);
            }

            var missing = _levels.Apartments.Where(l => !covered.Contains(l.Level.Id)).Select(l => l.Name).ToList();
            _coverage.Text = missing.Count == 0
                ? "Every apartment floor is covered by a stack (rule 25)."
                : "Apartment floors not covered by any stack: " + string.Join(", ", missing);
            _coverage.ForeColor = missing.Count == 0 ? Color.DarkGreen : Color.Firebrick;
        }

        private Level LevelByName(object name) =>
            _levels.All.FirstOrDefault(l => l.Name == Convert.ToString(name))?.Level;
    }
}
