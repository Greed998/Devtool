using System.Text;

namespace DevToolbox;

public sealed class ScriptEditForm : Form
{
    private readonly ScriptItem _item;
    private readonly bool _isNew;

    private readonly TextBox _txtName = new();
    private readonly TextBox _txtDesc = new();
    private readonly ComboBox _cmbCategory = new();
    private readonly ComboBox _cmbKind = new();
    private readonly TextBox _txtArgs = new();
    private readonly ComboBox _cmbEnc = new();
    private readonly CheckBox _chkAdmin = new();
    private readonly TextBox _txtCode = new();

    public ScriptItem Item => _item;
    public string Code => _txtCode.Text;
    public ScriptKind SelectedKind => (ScriptKind)(_cmbKind.SelectedItem is KindOption k ? k.Kind : ScriptKind.Batch);

    private sealed record KindOption(ScriptKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    public ScriptEditForm(ScriptItem item, IEnumerable<string> categories, bool isNew)
    {
        _item = item;
        _isNew = isNew;

        Text = isNew ? "Új szkript" : "Szkript szerkesztése – " + item.Name;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(880, 640);
        MinimumSize = new Size(700, 500);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 152,
            ColumnCount = 4,
            Padding = new Padding(14, 12, 14, 6),
            BackColor = Color.White
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        _txtName.Text = item.Name;
        _txtName.Dock = DockStyle.Fill;
        _txtDesc.Text = item.Description;
        _txtDesc.Dock = DockStyle.Fill;

        _cmbCategory.Dock = DockStyle.Fill;
        _cmbCategory.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (var c in categories.Distinct()) _cmbCategory.Items.Add(c);
        if (!_cmbCategory.Items.Contains("Általános")) _cmbCategory.Items.Insert(0, "Általános");
        _cmbCategory.Text = string.IsNullOrWhiteSpace(item.Category) ? "Általános" : item.Category;

        _cmbKind.Dock = DockStyle.Fill;
        _cmbKind.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbKind.Items.AddRange(new object[]
        {
            new KindOption(ScriptKind.Batch, "Batch (.bat) – Windows"),
            new KindOption(ScriptKind.PowerShell, "PowerShell (.ps1) – Windows"),
            new KindOption(ScriptKind.PowerShellCore, "PowerShell 7 (.ps1) – többplatformos"),
            new KindOption(ScriptKind.VBScript, "VBScript (.vbs) – Windows"),
            new KindOption(ScriptKind.Python, "Python (.py) – többplatformos"),
            new KindOption(ScriptKind.Bash, "Bash (.sh) – Linux / Unix"),
            new KindOption(ScriptKind.Sql, "SQL (.sql)")
        });
        _cmbKind.SelectedIndex = item.Kind switch
        {
            ScriptKind.PowerShell => 1,
            ScriptKind.PowerShellCore => 2,
            ScriptKind.VBScript => 3,
            ScriptKind.Python => 4,
            ScriptKind.Bash => 5,
            ScriptKind.Sql => 6,
            _ => 0
        };
        _cmbKind.Enabled = isNew; // meglevo fajl tipusa nem valtoztathato

        _txtArgs.Text = item.Arguments;
        _txtArgs.Dock = DockStyle.Fill;
        _txtArgs.PlaceholderText = "pl. -Verbose vagy C:\\cél\\mappa";

        _cmbEnc.Dock = DockStyle.Fill;
        _cmbEnc.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbEnc.Items.AddRange(ScriptRunner.SupportedEncodings.Cast<object>().ToArray());
        _cmbEnc.SelectedItem = ScriptRunner.SupportedEncodings.Contains(item.OutputEncoding)
            ? item.OutputEncoding : "utf-8";

        _chkAdmin.Text = "Futtatás rendszergazdaként (külön ablakban, UAC kérdéssel)";
        _chkAdmin.Checked = item.RunAsAdmin;
        _chkAdmin.AutoSize = true;
        _chkAdmin.Anchor = AnchorStyles.Left;

        top.Controls.Add(Lbl("Név:"), 0, 0); top.Controls.Add(_txtName, 1, 0);
        top.Controls.Add(Lbl("Típus:"), 2, 0); top.Controls.Add(_cmbKind, 3, 0);
        top.Controls.Add(Lbl("Leírás:"), 0, 1); top.Controls.Add(_txtDesc, 1, 1);
        top.Controls.Add(Lbl("Kategória:"), 2, 1); top.Controls.Add(_cmbCategory, 3, 1);
        top.Controls.Add(Lbl("Paraméterek:"), 0, 2); top.Controls.Add(_txtArgs, 1, 2);
        top.Controls.Add(Lbl("Kódolás:"), 2, 2); top.Controls.Add(_cmbEnc, 3, 2);
        top.Controls.Add(new Label { Text = "" }, 0, 3);
        top.Controls.Add(_chkAdmin, 1, 3);
        top.SetColumnSpan(_chkAdmin, 3);
        top.RowCount = 4;
        for (int i = 0; i < 4; i++) top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        _txtCode.Multiline = true;
        _txtCode.Dock = DockStyle.Fill;
        _txtCode.ScrollBars = ScrollBars.Both;
        _txtCode.WordWrap = false;
        _txtCode.AcceptsTab = true;
        _txtCode.Font = new Font("Consolas", 10F);
        _txtCode.BackColor = Color.FromArgb(30, 30, 32);
        _txtCode.ForeColor = Color.FromArgb(220, 224, 230);
        _txtCode.BorderStyle = BorderStyle.None;
        _txtCode.Text = LoadCode(item, isNew);

        var codeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 4), BackColor = Color.White };
        var codeInner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.FromArgb(30, 30, 32) };
        codeInner.Controls.Add(_txtCode);
        codeHost.Controls.Add(codeInner);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Color.White, Padding = new Padding(14, 10, 14, 10) };
        var btnSave = new Button
        {
            Text = "Mentés",
            Dock = DockStyle.Right,
            Width = 110,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F)
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += OnSave;

        var btnCancel = new Button { Text = "Mégse", Dock = DockStyle.Right, Width = 100, FlatStyle = FlatStyle.Flat };
        btnCancel.FlatAppearance.BorderColor = Color.FromArgb(205, 208, 214);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; };

        var spacer = new Panel { Dock = DockStyle.Right, Width = 8 };
        bottom.Controls.Add(btnSave);
        bottom.Controls.Add(spacer);
        bottom.Controls.Add(btnCancel);

        Controls.Add(codeHost);
        Controls.Add(bottom);
        Controls.Add(top);
        CancelButton = btnCancel;
    }

    private static Label Lbl(string t) => new()
    {
        Text = t,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(70, 78, 92)
    };

    private static string LoadCode(ScriptItem item, bool isNew)
    {
        if (isNew)
        {
            return item.Kind switch
            {
                ScriptKind.PowerShell =>
                    "# Új PowerShell szkript\r\n" +
                    "Write-Host \"Helló, DevToolbox!\"\r\n",
                ScriptKind.VBScript =>
                    "' Új VBScript\r\nWScript.Echo \"Helló, DevToolbox!\"\r\n",
                ScriptKind.Python =>
                    "# Új Python szkript\r\nprint(\"Helló, DevToolbox!\")\r\n",
                ScriptKind.PowerShellCore =>
                    "# Új PowerShell 7 szkript (többplatformos)\r\n" +
                    "Write-Host \"Helló, DevToolbox!\"\r\n",
                ScriptKind.Bash =>
                    "#!/usr/bin/env bash\n# Új bash szkript\nset -euo pipefail\n\n" +
                    "echo \"Helló, DevToolbox!\"\n",
                ScriptKind.Sql =>
                    "-- Új SQL szkript\nSELECT 1;\n",
                _ =>
                    "@echo off\r\nrem Új batch szkript\r\necho Hello, DevToolbox!\r\n"
            };
        }

        try { return File.ReadAllText(item.FullPath, DetectEncoding(item.FullPath)); }
        catch (Exception ex) { return "[a fájl nem olvasható: " + ex.Message + "]"; }
    }

    private static Encoding DetectEncoding(string path)
    {
        try
        {
            var bom = new byte[3];
            using var fs = File.OpenRead(path);
            if (fs.Read(bom, 0, 3) == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new UTF8Encoding(true);
        }
        catch { }
        return new UTF8Encoding(false);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_txtName.Text))
        {
            MessageBox.Show("A név nem lehet üres.", "Hiányzó adat",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }

        _item.Name = _txtName.Text.Trim();
        _item.Description = _txtDesc.Text.Trim();
        _item.Category = string.IsNullOrWhiteSpace(_cmbCategory.Text) ? "Általános" : _cmbCategory.Text.Trim();
        _item.Arguments = _txtArgs.Text.Trim();
        _item.RunAsAdmin = _chkAdmin.Checked;
        _item.OutputEncoding = _cmbEnc.SelectedItem?.ToString() ?? "utf-8";
        if (_isNew) _item.Kind = SelectedKind;

        DialogResult = DialogResult.OK;
    }
}
