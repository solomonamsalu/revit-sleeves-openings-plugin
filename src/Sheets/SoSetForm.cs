using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SleevesOpenings.Sheets
{
    /// <summary>
    /// S&amp;O Set: what will happen to each floor's sheet, the notes for the notes table (editable; saved in the model),
    /// and where the PDF goes.
    /// </summary>
    public class SoSetForm : Form
    {
        private readonly DataGridView _notes;
        private readonly CheckBox _print;
        private readonly TextBox _folder, _name;

        public List<SoNote> Notes { get; private set; } = new List<SoNote>();
        public bool PrintPdf => _print.Checked;
        public string PdfFolderPath => _folder.Text.Trim();
        public string PdfFileName => _name.Text.Trim();

        public SoSetForm(SoSetPlan plan, IList<SoNote> notes, string pdfFolder, string pdfName)
        {
            Text = "Sleeves & Openings — S&O Set";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            Width = 900; Height = 720; MinimumSize = new Size(700, 560);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            string head = plan.Pattern != null
                ? $"Pattern: {plan.Pattern.Describe()}"
                : "No pattern sheet: nothing can be made.";
            if (plan.Problems.Count > 0) head += "\n" + string.Join("\n", plan.Problems.Select(p => "! " + p));
            root.Controls.Add(new Label { Text = head, AutoSize = true, MaximumSize = new Size(860, 0), Padding = new Padding(0, 0, 0, 6), UseMnemonic = false }, 0, 0);

            var floors = Grid(true);
            floors.Columns.Add("Level", "Level");
            floors.Columns.Add("Sheet", "Sheet");
            floors.Columns.Add("Name", "Sheet name");
            floors.Columns.Add("Action", "What happens");
            floors.Columns["Action"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            foreach (var f in plan.Floors)
            {
                int r = floors.Rows.Add(f.Level.Name, f.Number, f.Name, f.Action + (f.IsPattern ? " (pattern)" : ""));
                if (f.Sheet == null) floors.Rows[r].DefaultCellStyle.BackColor = Color.Honeydew;
                else if (f.Missing.Count > 0) floors.Rows[r].DefaultCellStyle.BackColor = Color.LightYellow;
            }
            root.Controls.Add(floors, 0, 1);

            root.Controls.Add(new Label
            {
                Text = "Notes (the NOTES TO ARCH, ENG, & G.C. table). One per row; Sheets = which sheets it goes on (e.g. SL101, SL103), empty = all. " +
                       "Saved in the model. Notes typed on the sheets by hand are not touched.",
                AutoSize = true, MaximumSize = new Size(860, 0), Padding = new Padding(0, 8, 0, 4), UseMnemonic = false
            }, 0, 2);
            _notes = Grid(false);
            _notes.Columns.Add("Text", "Note");
            _notes.Columns.Add("Sheets", "Sheets (empty = all)");
            _notes.Columns["Text"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _notes.Columns["Sheets"].Width = 200;
            foreach (var n in notes ?? new List<SoNote>()) _notes.Rows.Add(n.Text, string.Join(", ", n.Sheets ?? new List<string>()));
            root.Controls.Add(_notes, 0, 3);

            var pdf = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            pdf.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pdf.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pdf.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _print = new CheckBox { Text = "Print the set to one PDF", Checked = true, AutoSize = true };
            pdf.Controls.Add(_print, 0, 0);
            pdf.SetColumnSpan(_print, 3);
            pdf.Controls.Add(new Label { Text = "Folder", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 1);
            _folder = new TextBox { Dock = DockStyle.Fill, Text = pdfFolder ?? "" };
            pdf.Controls.Add(_folder, 1, 1);
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
            };
            pdf.Controls.Add(browse, 2, 1);
            pdf.Controls.Add(new Label { Text = "File name", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 2);
            _name = new TextBox { Dock = DockStyle.Fill, Text = pdfName ?? "" };
            pdf.Controls.Add(_name, 1, 2);
            pdf.Controls.Add(new Label { Text = ".pdf", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 2, 2);
            _print.CheckedChanged += (s, e) => { _folder.Enabled = _name.Enabled = browse.Enabled = _print.Checked; };
            root.Controls.Add(pdf, 0, 4);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "Make / update the sheets", AutoSize = true, Enabled = plan.Pattern != null && plan.Floors.Count > 0 };
            ok.Click += (s, e) =>
            {
                if (_print.Checked && (PdfFolderPath.Length == 0 || PdfFileName.Length == 0))
                {
                    MessageBox.Show(this, "Choose the PDF folder and file name, or untick Print.", Text);
                    return;
                }
                Notes = _notes.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
                    .Select(r => new SoNote
                    {
                        Text = (r.Cells["Text"].Value as string ?? "").Trim(),
                        Sheets = (r.Cells["Sheets"].Value as string ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).ToList()
                    })
                    .Where(n => n.Text.Length > 0).ToList();
                DialogResult = DialogResult.OK;
                Close();
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            root.Controls.Add(buttons, 0, 5);
            AcceptButton = ok; CancelButton = cancel;
        }

        private static DataGridView Grid(bool readOnly) => new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = readOnly, AllowUserToAddRows = !readOnly, AllowUserToDeleteRows = !readOnly,
            RowHeadersVisible = !readOnly, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells, BackgroundColor = SystemColors.Window,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
    }
}
