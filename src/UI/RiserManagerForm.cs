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
using Autodesk.Revit.UI;
using SleevesOpenings.Risers;
using SleevesOpenings.Setup;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// F9 (first cut): every riser in the model with its floors, sizes, gaps and offsets.
    /// Select → highlights all its openings; Show → zooms to them.
    /// </summary>
    public class RiserManagerForm : System.Windows.Forms.Form
    {
        private readonly UIDocument _uidoc;
        private readonly LevelMap _levels;
        private readonly ProjectState _state;
        private readonly string[] _levelChoices;
        private readonly DataGridView _grid;
        private List<RiserRecord> _risers;
        private readonly Label _summary;

        public RiserManagerForm(UIDocument uidoc, LevelMap levels, ProjectState state)
        {
            _uidoc = uidoc; _levels = levels; _state = state;
            _levelChoices = new[] { "(default)" }.Concat(levels.All.Select(l => l.Name).Reverse()).ToArray();

            Text = "Sleeves & Openings — Riser Manager";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1240, 540);
            MinimumSize = new Size(800, 400);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowTemplate = { Height = 28 }
            };
            AddCol("Riser", 10); AddCol("System", 11); AddCol("Sizes", 13); AddCol("Top", 10); AddCol("Bottom", 10);
            AddCol("Floors", 7); AddCol("Gaps", 12); AddCol("Offsets", 7); AddCol("Label", 14);
            _grid.Columns.Add(Combo("Ends at (top)", "DeclTop")); _grid.Columns.Add(Combo("Ends at (bottom)", "DeclBottom"));
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Note (tap-out, bulkhead...)", Name = "Note", FillWeight = 16 });
            foreach (DataGridViewColumn col in _grid.Columns) col.ReadOnly = !(col.Name == "DeclTop" || col.Name == "DeclBottom" || col.Name == "Note");
            _grid.DataError += (o, e) => e.ThrowException = false;
            _grid.CurrentCellDirtyStateChanged += (o, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellFormatting += (o, e) =>
            {
                var row = _grid.Rows[e.RowIndex];
                bool bad = ((string)row.Cells["Gaps"].Value ?? "").Length > 0 || Convert.ToInt32(row.Cells["Offsets"].Value) > 0
                           || ((string)row.Cells["Sizes"].Value ?? "").Contains(",");
                row.DefaultCellStyle.ForeColor = bad ? Color.Firebrick : SystemColors.ControlText;
            };
            _grid.CellDoubleClick += (o, e) => Show(false);

            _summary = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 8, 12, 0), ForeColor = Color.DimGray };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
            var select = new Button { Text = "Select in model", Size = new Size(140, 32), Location = new Point(12, 12) };
            var show = new Button { Text = "Show / zoom", Size = new Size(120, 32), Location = new Point(162, 12) };
            var refresh = new Button { Text = "Refresh", Size = new Size(100, 32), Location = new Point(292, 12) };
            var save = new Button { Text = "Save ends / notes", Size = new Size(150, 32), Location = new Point(402, 12) };
            save.Click += (o, e) => SaveDeclared();
            var hint = new Label { Text = "Red = size change, missing floor or offset (rules 48-51). Declare where a riser legitimately ends (tap-out, bulkhead) so Final Check accepts it.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(562, 12), MaximumSize = new Size(560, 0) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            bottom.Controls.AddRange(new Control[] { select, show, refresh, save, hint, close });
            bottom.Resize += (o, e) => close.Location = new Point(bottom.ClientSize.Width - 112, 12);

            select.Click += (o, e) => Show(false);
            show.Click += (o, e) => Show(true);
            refresh.Click += (o, e) => Reload();

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(bottom);
            CancelButton = close;
            Reload();
        }

        private void AddCol(string name, int weight) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Name = name, FillWeight = weight });

        private DataGridViewComboBoxColumn Combo(string header, string name)
        {
            var c = new DataGridViewComboBoxColumn { HeaderText = header, Name = name, FillWeight = 12, FlatStyle = FlatStyle.Flat };
            c.Items.AddRange(_levelChoices);
            return c;
        }

        /// <summary>Writes the declared end levels / notes into the project (transaction inside).</summary>
        private void SaveDeclared()
        {
            _grid.EndEdit();
            for (int i = 0; i < _risers.Count && i < _grid.Rows.Count; i++)
            {
                var row = _grid.Rows[i];
                string top = Convert.ToString(row.Cells["DeclTop"].Value), bottom = Convert.ToString(row.Cells["DeclBottom"].Value), note = Convert.ToString(row.Cells["Note"].Value);
                bool any = (top != "(default)" && top != "") || (bottom != "(default)" && bottom != "") || !string.IsNullOrWhiteSpace(note);
                if (!any) { _state.RiserEnds.Remove(_risers[i].Riser); continue; }
                _state.RiserEnds[_risers[i].Riser] = new RiserEnds
                {
                    Top = top == "(default)" ? null : top, Bottom = bottom == "(default)" ? null : bottom, Note = note
                };
            }
            using (var t = new Transaction(_uidoc.Document, "Sleeves & Openings: riser ends"))
            {
                t.Start(); ProjectStore.Save(_uidoc.Document, _state); t.Commit();
            }
            MessageBox.Show(this, "Saved. Final Check now accepts these risers ending where you declared.", "Riser Manager");
        }

        private void Reload()
        {
            _risers = RiserIndex.Risers(_uidoc.Document);
            _grid.Rows.Clear();
            foreach (var r in _risers)
            {
                var gaps = r.Gaps(_levels);
                _state.RiserEnds.TryGetValue(r.Riser, out var ends);
                _grid.Rows.Add(r.Riser, r.System, string.Join(", ", r.Sizes), r.Top.Level.Name, r.Bottom.Level.Name,
                               r.Openings.Count, string.Join(", ", gaps.Select(g => g.Name)), r.OffsetCount, r.Top.Data.Label,
                               ends?.Top ?? "(default)", ends?.Bottom ?? "(default)", ends?.Note ?? "");
            }
            int unstamped = RiserIndex.AllOpenings(_uidoc.Document).Count(o => string.IsNullOrEmpty(o.Riser));
            _summary.Text = $"{_risers.Count} riser(s), {_risers.Sum(r => r.Openings.Count)} opening(s)" +
                            (unstamped > 0 ? $" — {unstamped} opening(s) have no riser id yet (they get one when propagated)." : "");
        }

        private void Show(bool zoom)
        {
            if (_grid.CurrentRow == null) return;
            var r = _risers[_grid.CurrentRow.Index];
            var ids = r.Openings.Select(o => o.Instance.Id).ToList();
            _uidoc.Selection.SetElementIds(ids);
            if (zoom) _uidoc.ShowElements(ids);
        }
    }
}
