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
        private readonly ElectricalSurvey _survey;

        public List<ElectricalSegment> Result { get; private set; }

        /// <summary>DialogResult.Retry means: the drafter asked to point at the architect's drawings instead.</summary>
        public const DialogResult FindDrawings = DialogResult.Retry;

        public ElectricalForm(LevelMap levels, ElectricalRules rules, ElectricalSurvey survey = null)
        {
            _rules = rules;
            _survey = survey;
            _model = levels.All
                .Where(l => l.Role != LevelRole.Roof && l.Role != LevelRole.Bulkhead)
                .Select(l => new ElectricalFloor { Level = l.Level, Apartments = Apartments(l) })
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
                       (survey != null && survey.Any
                           ? "Apartment counts were read from the architect's drawings — check them, they are editable."
                           : "Start at the lowest level near the electrical room.")
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

            if (survey != null && survey.Any)
            {
                var reread = new Button { Text = "Use the drawings' counts", AutoSize = true, Location = new Point(720, 6) };
                reread.Click += (o, e) => FillFromDrawings();
                top.Controls.Add(reread);
            }
            else
            {
                // the drawings were not found: let the drafter point at them instead of typing every floor
                var browse = new Button { Text = "Find the architect's drawings…", AutoSize = true, Location = new Point(700, 6) };
                browse.Click += (o, e) => { DialogResult = FindDrawings; Close(); };
                top.Controls.Add(browse);
            }

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 480 };

            _floors = Grid();
            _floors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Level (top first)", Name = "Level", ReadOnly = true, FillWeight = 45 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Apartments", Name = "Apts", FillWeight = 25 });
            _floors.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Offset above", Name = "Offset", FillWeight = 26 });
            _floors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "From the drawings", Name = "From", ReadOnly = true, FillWeight = 44 });
            _floors.Columns.Add(new DataGridViewCheckBoxColumn { Name = "IsApt", Visible = false });
            // "IsApt" marks the rows the spinner above fills: the apartment floors, even where the drawings show none.
            var apartmentLevels = new HashSet<Autodesk.Revit.DB.ElementId>(levels.Apartments.Select(l => l.Level.Id));
            foreach (var f in _model.AsEnumerable().Reverse())
            {
                int row = _floors.Rows.Add(f.Level.Name, f.Apartments, false, Source(f.Level), apartmentLevels.Contains(f.Level.Id));
                var read = _survey?.For(f.Level);
                if (read != null && read.Notes.Count > 0)
                {
                    _floors.Rows[row].Cells["From"].ToolTipText = string.Join("\n", read.Notes);
                    _floors.Rows[row].Cells["From"].Style.ForeColor = Color.FromArgb(180, 95, 6);
                }
            }
            _floors.DataError += (o, e) => e.ThrowException = false;
            _floors.CellValueChanged += (o, e) => Recalc();
            _floors.CurrentCellDirtyStateChanged += (o, e) => { if (_floors.IsCurrentCellDirty) _floors.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            _segments = Grid(); _segments.ReadOnly = true;
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Segment floors", Name = "Floors", FillWeight = 40 });
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Circles", Name = "Circles", FillWeight = 18 });
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Grid", Name = "Grid", FillWeight = 18 });
            _segments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Opening W x L", Name = "Size", FillWeight = 30 });

            var left = new GroupBox
            {
                Text = survey != null && survey.Any ? "Floors — read from " + System.IO.Path.GetFileName(survey.Read.Folder ?? "") : "Floors",
                Dock = DockStyle.Fill, Padding = new Padding(8)
            };
            left.Controls.Add(_floors);
            if (survey != null && survey.Read.Warnings.Count > 0)
            {
                var warn = new Label
                {
                    Dock = DockStyle.Bottom, Height = 36, ForeColor = Color.FromArgb(180, 95, 6),
                    Text = survey.Read.Warnings[0] + (survey.Read.Warnings.Count > 1 ? " (+" + (survey.Read.Warnings.Count - 1) + " more)" : "")
                };
                warn.Click += (o, e) => MessageBox.Show(string.Join("\n\n", survey.Read.Warnings), "Reading the architectural drawings");
                left.Controls.Add(warn);
            }
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

        /// <summary>Apartments the architect's drawings show on this level; the old 10-per-floor guess when they say nothing.</summary>
        private int Apartments(ClassifiedLevel level)
        {
            var read = _survey?.For(level.Level);
            if (read != null) return read.Apartments;
            return _survey != null && _survey.Any ? 0 : (level.Role == LevelRole.Apartment ? 10 : 0);
        }

        private string Source(Autodesk.Revit.DB.Level level)
        {
            var read = _survey?.For(level);
            if (read == null) return _survey != null && _survey.Any ? "no drawing for this level" : "";
            if (read.Apartments == 0) return "no apartment tags";
            return read.Apartments + (read.NumbersText.Length > 0 ? " tags: " + read.NumbersText : " tags");
        }

        private void FillFromDrawings()
        {
            for (int i = 0; i < _floors.Rows.Count; i++)
            {
                var read = _survey?.For(_model[_floors.Rows.Count - 1 - i].Level);
                if (read != null) _floors.Rows[i].Cells["Apts"].Value = read.Apartments;
            }
            Recalc();
        }

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
