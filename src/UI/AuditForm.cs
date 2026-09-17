using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Panel = System.Windows.Forms.Panel;
using Control = System.Windows.Forms.Control;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Audit;

namespace SleevesOpenings.UI
{
    /// <summary>F8 results: severity-coloured list, click to select, double-click to zoom, CSV export.</summary>
    public class AuditForm : System.Windows.Forms.Form
    {
        private readonly UIDocument _uidoc;
        private readonly Func<List<AuditIssue>> _rerun;
        private List<AuditIssue> _all;
        private int _openingCount;
        private List<AuditIssue> _shown;
        private readonly DataGridView _grid;
        private readonly CheckBox _errors, _warnings, _infos;
        private readonly Label _summary;

        public AuditForm(UIDocument uidoc, List<AuditIssue> issues, int openingCount, Func<List<AuditIssue>> rerun)
        {
            _uidoc = uidoc; _all = issues; _rerun = rerun; _openingCount = openingCount;

            Text = "Sleeves & Openings — Final Check";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 560);
            MinimumSize = new Size(800, 400);

            int e = issues.Count(i => i.Severity == Severity.Error), w = issues.Count(i => i.Severity == Severity.Warning), n = issues.Count(i => i.Severity == Severity.Info);
            _summary = new Label
            {
                Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 10, 12, 0),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };

            var filters = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(12, 6, 12, 0) };
            _errors = new CheckBox { Text = $"Errors ({e})", Checked = true, AutoSize = true, Location = new Point(12, 8), ForeColor = Color.Firebrick };
            _warnings = new CheckBox { Text = $"Warnings ({w})", Checked = true, AutoSize = true, Location = new Point(130, 8), ForeColor = Color.DarkGoldenrod };
            _infos = new CheckBox { Text = $"Notes ({n})", Checked = true, AutoSize = true, Location = new Point(270, 8), ForeColor = Color.DimGray };
            filters.Controls.AddRange(new Control[] { _errors, _warnings, _infos });
            foreach (var cb in new[] { _errors, _warnings, _infos }) cb.CheckedChanged += (o, ev) => Refill();

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowTemplate = { Height = 28 }
            };
            AddCol("Severity", 8); AddCol("Rule", 9); AddCol("Level", 11); AddCol("Riser", 8); AddCol("System", 10); AddCol("Message", 40); AddCol("Fix", 14);
            _grid.CellFormatting += (o, ev) =>
            {
                var sev = (string)_grid.Rows[ev.RowIndex].Cells["Severity"].Value;
                _grid.Rows[ev.RowIndex].DefaultCellStyle.ForeColor =
                    sev == "Error" ? Color.Firebrick : sev == "Warning" ? Color.DarkGoldenrod : Color.DimGray;
            };
            _grid.SelectionChanged += (o, ev) => Select(false);
            _grid.CellDoubleClick += (o, ev) => Select(true);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
            var zoom = new Button { Text = "Show / zoom", Size = new Size(120, 32), Location = new Point(12, 12) };
            var export = new Button { Text = "Export CSV…", Size = new Size(120, 32), Location = new Point(142, 12) };
            var fixOne = new Button { Text = "Fix selected", Size = new Size(120, 32), Location = new Point(290, 12) };
            var fixAll = new Button { Text = "Fix all fixable", Size = new Size(130, 32), Location = new Point(420, 12) };
            var hint = new Label { Text = "Click = select, double-click = zoom. 'Fix' = the manual gives exactly one right answer.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(570, 20) };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 32) };
            bottom.Controls.AddRange(new Control[] { zoom, export, fixOne, fixAll, hint, close });
            bottom.Resize += (o, ev) => close.Location = new Point(bottom.ClientSize.Width - 112, 12);
            zoom.Click += (o, ev) => Select(true);
            export.Click += (o, ev) => Export();
            fixOne.Click += (o, ev) => { if (_grid.CurrentRow != null && _grid.CurrentRow.Index < _shown.Count) ApplyFixes(new[] { _shown[_grid.CurrentRow.Index] }); };
            fixAll.Click += (o, ev) => ApplyFixes(_shown.Where(i => i.CanFix).ToList());

            Controls.Add(_grid);
            Controls.Add(filters);
            Controls.Add(_summary);
            Controls.Add(bottom);
            CancelButton = close;
            UpdateSummary();
            Refill();
        }

        private void UpdateSummary()
        {
            int e = _all.Count(i => i.Severity == Severity.Error), w = _all.Count(i => i.Severity == Severity.Warning), n = _all.Count(i => i.Severity == Severity.Info);
            int f = _all.Count(i => i.CanFix);
            _summary.Text = _all.Count == 0
                ? $"All {_openingCount} opening(s) pass the manual's checks."
                : $"{_openingCount} opening(s) checked — {e} error(s), {w} warning(s), {n} note(s); {f} fixable automatically";
            _errors.Text = $"Errors ({e})"; _warnings.Text = $"Warnings ({w})"; _infos.Text = $"Notes ({n})";
        }

        /// <summary>Runs the chosen fixes in one transaction, then re-audits and refreshes the list.</summary>
        private void ApplyFixes(IList<AuditIssue> issues)
        {
            var fixable = issues.Where(i => i.CanFix).ToList();
            if (fixable.Count == 0) { MessageBox.Show(this, "Nothing selected that the add-in can fix on its own.", "Fix"); return; }

            var doc = _uidoc.Document;
            int done = 0; var failed = new List<string>();
            using (var t = new Transaction(doc, "Sleeves & Openings: Fix " + fixable.Count + " issue(s)"))
            {
                t.Start();
                foreach (var i in fixable)
                {
                    try { i.Fix(doc); done++; }
                    catch (Exception ex) { failed.Add(i.Message + ": " + ex.Message); }
                }
                t.Commit();
            }
            App.Log("FinalCheck fixes: " + done + " applied, " + failed.Count + " failed");
            if (failed.Count > 0) MessageBox.Show(this, "Could not fix:" + Environment.NewLine + string.Join(Environment.NewLine, failed), "Fix");

            if (_rerun != null) { _all = _rerun(); UpdateSummary(); Refill(); }
        }

        private void AddCol(string name, int weight) =>
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Name = name, FillWeight = weight });

        private void Refill()
        {
            _shown = _all.Where(i =>
                (i.Severity == Severity.Error && _errors.Checked) ||
                (i.Severity == Severity.Warning && _warnings.Checked) ||
                (i.Severity == Severity.Info && _infos.Checked)).ToList();
            _grid.Rows.Clear();
            foreach (var i in _shown)
                _grid.Rows.Add(i.Severity.ToString(), i.Rule, i.Level, i.Riser, i.System, i.Message, i.FixLabel ?? "");
            _grid.ClearSelection();
        }

        private void Select(bool zoom)
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _shown.Count) return;
            var ids = _shown[_grid.CurrentRow.Index].Elements;
            if (ids.Count == 0) return;
            _uidoc.Selection.SetElementIds(ids);
            if (zoom) _uidoc.ShowElements(ids);
        }

        private void Export()
        {
            using (var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "sleeves-openings-check.csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder("Severity,Rule,Level,Riser,System,Message,ElementIds\n");
                foreach (var i in _all)
                    sb.AppendLine(string.Join(",", new[] { i.Severity.ToString(), i.Rule, i.Level, i.Riser, i.System, i.Message, i.FixLabel,
                        string.Join(" ", i.Elements.Select(x => x.IntegerValue)) }.Select(Csv)));
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            }
        }

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
