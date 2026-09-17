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
using SleevesOpenings.Risers;
using SleevesOpenings.Setup;

namespace SleevesOpenings.UI
{
    /// <summary>Per selected opening: where the riser stops. Defaults to the lowest level (manual rules 45-51).</summary>
    public class PropagateForm : System.Windows.Forms.Form
    {
        private readonly DataGridView _grid;
        private readonly CheckBox _skipExisting;
        private readonly List<OpeningRecord> _sources;
        private readonly List<ClassifiedLevel> _levels;

        public bool SkipExisting => _skipExisting.Checked;

        public PropagateForm(List<OpeningRecord> sources, LevelMap levels, Level currentLevel)
        {
            _sources = sources;
            _levels = levels.All.AsEnumerable().Reverse().ToList();   // top of building first

            Text = "Sleeves & Openings — Propagate Risers";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 460);
            MinimumSize = new Size(700, 360);

            var info = new Label
            {
                Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 10, 12, 0), ForeColor = Color.DimGray,
                Text = $"Copies each opening from {currentLevel.Name} to every level down (or up) to its 'Stop at' level, " +
                       "same location and size. Check the riser diagram for where each riser terminates (rule 51)."
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect, RowTemplate = { Height = 30 }
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Riser", Name = "Riser", ReadOnly = true, FillWeight = 22 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "System", Name = "System", ReadOnly = true, FillWeight = 24 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Size", Name = "Size", ReadOnly = true, FillWeight = 22 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Label", Name = "Label", ReadOnly = true, FillWeight = 30 });
            var stop = new DataGridViewComboBoxColumn { HeaderText = "Stop at level", Name = "Stop", FillWeight = 30, FlatStyle = FlatStyle.Flat };
            foreach (var l in _levels) stop.Items.Add(l.Name);
            _grid.Columns.Add(stop);

            string defaultStop = (levels.Lowest ?? levels.All.First()).Name;
            foreach (var s in sources)
                _grid.Rows.Add(s.Riser, s.Data.System, s.SizeText, s.Data.Label, defaultStop);

            _grid.DataError += (o, e) => e.ThrowException = false;
            // Commit combo edits immediately so a single click changes the value.
            _grid.CurrentCellDirtyStateChanged += (o, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
            _skipExisting = new CheckBox { Text = "Skip levels where this riser already exists at the same spot", Checked = true, AutoSize = true, Location = new Point(12, 18) };
            var allTo = new Button { Text = "Set all to lowest", Size = new Size(140, 32), Location = new Point(420, 12) };
            allTo.Click += (o, e) => { foreach (DataGridViewRow r in _grid.Rows) r.Cells["Stop"].Value = defaultStop; };
            var ok = new Button { Text = "Propagate", DialogResult = DialogResult.OK, Size = new Size(110, 32), Location = new Point(580, 12) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 32), Location = new Point(700, 12) };
            bottom.Controls.AddRange(new Control[] { _skipExisting, allTo, ok, cancel });
            bottom.Resize += (o, e) => { cancel.Left = bottom.ClientSize.Width - 112; ok.Left = cancel.Left - 120; allTo.Left = ok.Left - 150; };

            Controls.Add(_grid);
            Controls.Add(info);
            Controls.Add(bottom);
            AcceptButton = ok; CancelButton = cancel;
        }

        public List<PropagateItem> Items()
        {
            _grid.EndEdit();
            var items = new List<PropagateItem>();
            for (int i = 0; i < _sources.Count; i++)
            {
                var stopName = (string)_grid.Rows[i].Cells["Stop"].Value;
                var stop = _levels.First(l => l.Name == stopName).Level;
                items.Add(new PropagateItem { Source = _sources[i], StopLevel = stop });
            }
            return items;
        }
    }
}
