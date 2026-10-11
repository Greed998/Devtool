using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class ScriptBuilderForm : Form
{
    private readonly ListView _templates = new();
    private readonly TableLayoutPanel _fields = new();
    private readonly TextBox _preview = new();
    private readonly Label _description = new();
    private readonly Label _warning = new();
    private readonly TextBox _name = new();

    private readonly Dictionary<string, Control> _inputs = new();
    private BuilderTemplate? _current;

    /// <summary>A konyvtarba felveendo szkript, ha a felhasznalo ezt valasztotta.</summary>
    public (string Name, ScriptKind Kind, string Content, bool RunAsAdmin, string Description)? Result
    { get; private set; }

    public ScriptBuilderForm()
    {
        Text = "Szkript építő – " + AppPaths.DisplayName;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(980, 620);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        // ---------------- bal: sablonok
        _templates.View = View.Details;
        _templates.Dock = DockStyle.Fill;
        _templates.FullRowSelect = true;
        _templates.MultiSelect = false;
        _templates.HideSelection = false;
        _templates.BorderStyle = BorderStyle.None;
        _templates.Columns.Add("Sablon", 250);
        _templates.SelectedIndexChanged += (_, _) => SelectTemplate();

        foreach (var grp in ScriptBuilder.Templates.GroupBy(t => t.Category))
        {
            var g = SplitHelper.NewGroup(grp.Key);
            _templates.Groups.Add(g);
            foreach (var t in grp)
                _templates.Items.Add(new ListViewItem(t.Name) { Tag = t, Group = g });
        }

        var left = new Panel { Dock = DockStyle.Left, Width = 270 };
        left.Controls.Add(_templates);
        left.Controls.Add(Header("Mit építsünk?"));

        // ---------------- közép: paraméterek
        _description.Dock = DockStyle.Top;
        _description.AutoSize = false;
        _description.Height = 56;
        _description.Padding = new Padding(14, 8, 14, 4);
        _description.ForeColor = Color.FromArgb(88, 96, 110);

        _warning.Dock = DockStyle.Top;
        _warning.AutoSize = false;
        _warning.Height = 48;
        _warning.Padding = new Padding(14, 8, 14, 6);
        _warning.BackColor = Color.FromArgb(255, 248, 225);
        _warning.ForeColor = Color.FromArgb(120, 88, 10);
        _warning.Visible = false;

        _fields.Dock = DockStyle.Fill;
        _fields.ColumnCount = 2;
        _fields.AutoScroll = true;
        _fields.Padding = new Padding(14, 8, 18, 12);
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var mid = new Panel { Dock = DockStyle.Fill };
        mid.Controls.Add(_fields);
        mid.Controls.Add(_warning);
        mid.Controls.Add(_description);
        mid.Controls.Add(Header("Adatok"));

        // ---------------- jobb: előnézet
        _preview.Multiline = true;
        _preview.ReadOnly = true;
        _preview.Dock = DockStyle.Fill;
        _preview.ScrollBars = ScrollBars.Both;
        _preview.WordWrap = false;
        _preview.Font = new Font("Consolas", 9.5F);
        _preview.BackColor = Color.FromArgb(30, 30, 32);
        _preview.ForeColor = Color.FromArgb(220, 224, 230);
        _preview.BorderStyle = BorderStyle.None;

        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 4, 4) };
        var previewInner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Color.FromArgb(30, 30, 32) };
        previewInner.Controls.Add(_preview);
        previewHost.Controls.Add(previewInner);
        previewHost.Controls.Add(Header("Előnézet"));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(430);
        split.Panel1.Controls.Add(mid);
        split.Panel2.Controls.Add(previewHost);

        // ---------------- alsó gombsor
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(14, 12, 14, 12) };

        var nameLabel = new Label
        {
            Text = "Szkript neve:",
            Dock = DockStyle.Left,
            Width = 90,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(70, 78, 92)
        };
        _name.Dock = DockStyle.Left;
        _name.Width = 260;

        var btnAdd = new Button
        {
            Text = "Hozzáadás a könyvtárhoz",
            Dock = DockStyle.Right,
            Width = 190,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F)
        };
        btnAdd.FlatAppearance.BorderSize = 0;
        btnAdd.Click += (_, _) => AddToLibrary();

        var btnSave = MakeFlat("Mentés fájlba", 130);
        btnSave.Click += (_, _) => SaveToFile();

        var btnCopy = MakeFlat("Vágólapra", 110);
        btnCopy.Click += (_, _) => CopyToClipboard();

        var btnClose = MakeFlat("Bezárás", 100);
        btnClose.Click += (_, _) => Close();

        bottom.Controls.Add(btnAdd);
        bottom.Controls.Add(Spacer());
        bottom.Controls.Add(btnSave);
        bottom.Controls.Add(Spacer());
        bottom.Controls.Add(btnCopy);
        bottom.Controls.Add(Spacer());
        bottom.Controls.Add(btnClose);
        bottom.Controls.Add(_name);
        bottom.Controls.Add(nameLabel);

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(left);

        CancelButton = btnClose;

        if (_templates.Items.Count > 0) _templates.Items[0].Selected = true;
    }

    private static Panel Spacer() => new() { Dock = DockStyle.Right, Width = 8 };

    private static Button MakeFlat(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Right,
            Width = width,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        return b;
    }

    private static Label Header(string text) => new()
    {
        Text = "  " + text,
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(246, 247, 249),
        ForeColor = Color.FromArgb(60, 68, 82),
        Font = new Font("Segoe UI Semibold", 9F)
    };

    // ================================================================ sablonválasztás

    private void SelectTemplate()
    {
        if (_templates.SelectedItems.Count == 0) return;
        if (_templates.SelectedItems[0].Tag is not BuilderTemplate t) return;

        _current = t;
        _name.Text = t.Name;
        _description.Text = t.Description;

        _warning.Text = t.Warning.Length > 0 ? "⚠  " + t.Warning : "";
        _warning.Visible = t.Warning.Length > 0;

        _fields.SuspendLayout();
        _fields.Controls.Clear();
        _fields.RowStyles.Clear();
        _inputs.Clear();

        foreach (var f in t.Fields)
        {
            var label = new Label
            {
                Text = f.Label + ":",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(70, 78, 92),
                Margin = new Padding(0, 4, 8, 0)
            };

            Control input = f.Kind switch
            {
                FieldKind.Choice => MakeCombo(f),
                FieldKind.Number => MakeNumber(f),
                _ => MakeText(f)
            };
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(0, 3, 0, 3);

            _fields.Controls.Add(label);
            _fields.Controls.Add(input);
            _fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _inputs[f.Key] = input;

            if (f.Hint.Length > 0)
            {
                _fields.Controls.Add(new Label { Text = "", AutoSize = true });
                _fields.Controls.Add(new Label
                {
                    Text = f.Hint,
                    AutoSize = true,
                    MaximumSize = new Size(380, 0),
                    ForeColor = Color.FromArgb(130, 137, 150),
                    Font = new Font("Segoe UI", 8.25F),
                    Margin = new Padding(0, 0, 0, 6)
                });
                _fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
        }

        _fields.ResumeLayout();
        Refresh2();
    }

    private Control MakeText(BuilderField f)
    {
        var t = new TextBox { Text = f.Default };
        t.TextChanged += (_, _) => Refresh2();
        return t;
    }

    private Control MakeNumber(BuilderField f)
    {
        var n = new NumericUpDown { Minimum = 0, Maximum = 65535 };
        if (decimal.TryParse(f.Default, out var d)) n.Value = Math.Clamp(d, 0, 65535);
        n.ValueChanged += (_, _) => Refresh2();
        return n;
    }

    private Control MakeCombo(BuilderField f)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var o in f.Options ?? Array.Empty<string>()) c.Items.Add(o);
        c.SelectedItem = f.Default;
        if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
        c.SelectedIndexChanged += (_, _) => Refresh2();
        return c;
    }

    private Dictionary<string, string> Values()
    {
        var d = new Dictionary<string, string>();
        foreach (var (key, ctl) in _inputs)
            d[key] = ctl switch
            {
                NumericUpDown n => ((int)n.Value).ToString(),
                ComboBox c => c.SelectedItem?.ToString() ?? "",
                _ => ctl.Text.Trim()
            };
        return d;
    }

    private void Refresh2()
    {
        if (_current is null) return;
        try { _preview.Text = ScriptBuilder.Generate(_current, Values()); }
        catch (Exception ex) { _preview.Text = "A generálás hibára futott:\r\n" + ex.Message; }
    }

    // ================================================================ kimenet

    private bool Validate2()
    {
        if (_current is null) return false;

        var v = Values();
        var empty = _current.Fields
            .Where(f => f.Kind is FieldKind.Text &&
                        !f.Hint.Contains("üresen", StringComparison.OrdinalIgnoreCase) &&
                        v.GetValueOrDefault(f.Key, "").Length == 0)
            .Select(f => f.Label).ToList();

        if (empty.Count > 0)
        {
            MessageBox.Show("Ezek a mezők nem maradhatnak üresen:\n\n  • " +
                            string.Join("\n  • ", empty),
                "Hiányzó adat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void AddToLibrary()
    {
        if (_current is null || !Validate2()) return;

        var name = _name.Text.Trim();
        if (name.Length == 0) name = _current.Name;

        Result = (name, _current.Kind, _preview.Text, _current.RunAsAdmin,
                  _current.Description);

        DialogResult = DialogResult.OK;
        Close();
    }

    private void SaveToFile()
    {
        if (_current is null || !Validate2()) return;

        var ext = ScriptItem.ExtensionOf(_current.Kind);
        using var dlg = new SaveFileDialog
        {
            Title = "Generált szkript mentése",
            Filter = $"Szkript (*{ext})|*{ext}|Minden fájl|*.*",
            FileName = SafeName(_name.Text) + ext,
            InitialDirectory = AppPaths.ExportDir
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);

            // A bash LF-et var, a batch CRLF-et - a konyvtar logikaja donti el.
            var text = ScriptLibrary.Normalize(_preview.Text, _current.Kind);
            var enc = new UTF8Encoding(_current.Kind is not (ScriptKind.Batch or ScriptKind.Bash));
            File.WriteAllText(dlg.FileName, text, enc);

            MessageBox.Show("Mentve:\n" + dlg.FileName, "Kész",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopyToClipboard()
    {
        if (_current is null) return;
        try
        {
            Clipboard.SetText(_preview.Text);
            _description.Text = "A szkript a vágólapra másolva.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().Length == 0 ? "szkript" : s.Trim();
    }
}
