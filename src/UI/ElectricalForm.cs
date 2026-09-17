using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Panel = System.Windows.Forms.Panel;
using Control = System.Windows.Forms.Control;
using SleevesOpenings.Electrical;
using SleevesOpenings.Rules;
using SleevesOpenings.Setup;

namespace SleevesOpenings.UI
{
    /// <summary>Apartments per floor + offset floors → conduit count and opening size per segment.</summary>
    public class ElectricalForm : System.Windows.Forms.Form
    {
        private readonly ElectricalRules _rules;
        private readonly DataGridView _floors, _segments;
        private readonly NumericUpDown _apts, _cols;
        private readonly List<ElectricalFloor> _model;

        public List<ElectricalSegment> Result { get; private set; }

        public ElectricalForm(LevelMap levels, ElectricalRules rules)
        {
            _rules = rules;
            _model = levels.All
                .Where(l => l.Role != LevelRole.Roof && l.Role != LevelRole.Bulkhead)
                .Select(l => new ElectricalFloor { Level = l.Level, Apartments = l.Role == LevelRole.Apartment ? 10 : 0 })
                .ToList();

            Text = "Sleeves & Openings — Electrical Conduit Calculator";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 600);
            MinimumSize = new Size(900, 500);

            var info = new Label
            {
                Dock = DockStyle.Top, Height = 66, Padding = new Padding(12, 10, 12, 0), ForeColor = Color.DimGray,
                Text = "One conduit circle per apartment above the floor + 1 for the roof, " + Units.FormatInches(rules.ConduitSpacingCenterToCenter) +
                       " c-c (electrical rules 5-9). The count stays the same all the way up unless the riser offsets; " +
                       "tick 'Offset above' on the floor where it moves and the floors above are recalculated (rules 11-14). " +
                       "Start at the lowest level near the electrical room."
            };

            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 8, 12, 0) };
            top.Controls.Add(new Label { Text = "Apartments per floor (fills the table):", AutoSize = true, Location = new Point(12, 12) });
            _apts = new NumericUpDown { Minimum = 0, Maximum = 500, Value = 10, Width = 70, Location = new Point(300, 8) };
            top.Controls.Add(_apts);
            top.Controls.Add(new Label { Text = "Circles per row (0 = square):", AutoSize = true, Location = new Point(400, 12) });
            _cols = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 0, Width = 70, Location = new Point(620, 8) };
            top.Controls.Add(_cols);
            _apts.ValueChanged += (o, e) => { foreach (DataGridViewRow r in _floors.Rows) if ((bool)r.Cells["IsApt"].Value) r.Cells["Apts"].Value = (int)_apts.Value; Recalc(); };
            _cols.ValueChanged += (o, e) => Recalc();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 480 };

            _floors = Grid();
            _floors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Level (top first)", Name = "Level", ReadOnly = true, FillWeight = 45 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Apartments", Name = "Apts", FillWeight = 25 });
            _floors.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Offset above", Name = "Offset", FillWeight = 30 });
            _floors.Columns.Add(new DataGridViewCheckBoxColumn { Name = "IsApt", Visible = false });
            foreach (var f in _model.AsEnumerable().Reverse())
                _floors.Rows.Add(f.Level.Name, f.Apartments, false, f.Apartments > 0);
            _floors.DataError += (o, e) => e.ThrowException = false;
            _floors.CellValueChanged += (o, e) => Recalc();
            _floors.CurrentCellDirtyStateChanged += (o, e) => { if (_floors.IsCurrentCellDirty) _floors.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            _segments = Grid(); _segments.ReadOnly = true;
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Segment floors", Name = "Floors", FillWeight = 40 });
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Circles", Name = "Circles", FillWeight = 18 });
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Grid", Name = "Grid", FillWeight = 18 });
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Opening W x L", Name = "Size", FillWeight = 30 });

            var left = new GroupBox { Text = "Floors", Dock = DockStyle.Fill, Padding = new Padding(8) };
            left.Controls.Add(_floors);
            var right = new GroupBox { Text = "Resulting segments (one click location each)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            right.Controls.Add(_segments);
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
            var ok = new Button { Text = "Place openings…", DialogResult = DialogResult.OK, Size = new Size(150, 32) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            var hint = new Label { Text = "Next: click one location per segment on the plan (bottom segment first), then the roof gets one 2\" ELECTRIC sleeve.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(12, 20) };
            bottom.Controls.AddRange(new Control[] { hint, ok, cancel });
            bottom.Resize += (o, e) => { cancel.Location = new Point(bottom.ClientSize.Width - 112, 12); ok.Location = new Point(cancel.Left - 160, 12); };

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(info);
            Controls.Add(bottom);
            AcceptButton = ok; CancelButton = cancel;
            Recalc();
        }

        private static DataGridView Grid() => new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 28 }
        };

        private void Recalc()
        {
            if (_segments == null) return;
            _floors.EndEdit();
            int n = _floors.Rows.Count;
            for (int i = 0; i < n; i++)
            {
                var row = _floors.Rows[i];
                var f = _model[n - 1 - i];           // grid is top-first, model is ascending
                f.Apartments = int.TryParse(Convert.ToString(row.Cells["Apts"].Value), out var a) ? a : 0;
                f.OffsetAbove = row.Cells["Offset"].Value is bool b && b;
            }
            Result = ElectricalPlanner.Segments(_model, _rules, (int)_cols.Value);
            _segments.Rows.Clear();
            foreach (var s in Result)
                _segments.Rows.Add(s.FloorsText, s.Circles, $"{s.Columns} x {s.Rows}", $"{Units.FormatInches(s.Width)} x {Units.FormatInches(s.Length)}");
        }
    }
}
