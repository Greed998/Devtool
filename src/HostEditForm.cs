using System.Runtime.Versioning;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class HostEditForm : Form
{
    private readonly MonitoredHost _host;
    private readonly TextBox _name = new();
    private readonly TextBox _address = new();
    private readonly ComboBox _group = new();
    private readonly TextBox _notes = new();
    private readonly CheckBox _isDc = new();
    private readonly CheckBox _skipDisk = new();
    private readonly CheckBox _enabled = new();
    private readonly Label _validation = new();

    public MonitoredHost Host => _host;

    public HostEditForm(MonitoredHost host, IEnumerable<string> groups, bool isNew)
    {
        _host = host;

        Text = isNew ? "Gép felvétele a felügyeletbe" : "Gép szerkesztése – " + host.DisplayName;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 520);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(20, 16, 24, 12),
            AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control c)
        {
            root.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(70, 78, 92),
                Margin = new Padding(0, 4, 8, 0)
            });
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 3, 0, 3);
            root.Controls.Add(c);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        void Note(string text)
        {
            root.Controls.Add(new Label { Text = "", AutoSize = true });
            root.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                ForeColor = Color.FromArgb(130, 137, 150),
                Font = new Font("Segoe UI", 8.25F),
                Margin = new Padding(0, 0, 0, 8)
            });
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _name.Text = host.Name;
        Row("Megnevezés:", _name);
        Note("Ahogy a listában látni szeretnéd, pl. „Központi DC”.");

        _address.Text = host.Host;
        _address.TextChanged += (_, _) => Revalidate();
        Row("Gépnév vagy IP:", _address);

        _validation.AutoSize = true;
        _validation.MaximumSize = new Size(420, 0);
        _validation.Font = new Font("Segoe UI", 8.25F);
        _validation.Margin = new Padding(0, 0, 0, 8);
        root.Controls.Add(new Label { Text = "", AutoSize = true });
        root.Controls.Add(_validation);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _group.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (var g in groups.Distinct()) _group.Items.Add(g);
        if (!_group.Items.Contains("Alapértelmezett")) _group.Items.Insert(0, "Alapértelmezett");
        _group.Text = host.Group.Length > 0 ? host.Group : "Alapértelmezett";
        Row("Csoport:", _group);
        Note("Telephely, ügyfél vagy szerepkör szerint – a lista eszerint csoportosít. " +
             "Új név beírásával új csoport jön létre.");

        _isDc.Text = "Tartományvezérlő (címtár-ellenőrzésekkel)";
        _isDc.Checked = host.IsDomainController;
        _isDc.AutoSize = true;
        Row("Szerepkör:", _isDc);
        Note("Bekapcsolva a program a címtárszolgáltatásokat, a SYSVOL/NETLOGON megosztást, " +
             "az óraeltérést és az LDAP/Kerberos portokat is ellenőrzi.");

        _skipDisk.Text = "Virtuális gépen a fizikai lemez ellenőrzésének kihagyása";
        _skipDisk.Checked = host.SkipPhysicalDiskIfVirtual;
        _skipDisk.AutoSize = true;
        Row("Lemez:", _skipDisk);
        Note("Virtuális lemeznél a SMART adat a hypervisor mögül nem látszik, ezért az " +
             "ellenőrzés félrevezető lenne. A szabad hely ilyenkor is mérve marad – az " +
             "ugyanúgy betelhet.");

        _enabled.Text = "A felügyelet aktív erre a gépre";
        _enabled.Checked = host.Enabled;
        _enabled.AutoSize = true;
        Row("Állapot:", _enabled);

        _notes.Text = host.Notes;
        _notes.Multiline = true;
        _notes.Height = 60;
        _notes.ScrollBars = ScrollBars.Vertical;
        Row("Megjegyzés:", _notes);

        // ---------------- gombsor
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(20, 11, 24, 11) };

        var ok = new Button
        {
            Text = "Mentés",
            Dock = DockStyle.Right,
            Width = 110,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F)
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => Save();

        var cancel = new Button
        {
            Text = "Mégse", Dock = DockStyle.Right, Width = 100,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var test = new Button
        {
            Text = "Cím ellenőrzése", Dock = DockStyle.Left, Width = 140,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White
        };
        test.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        test.Click += (_, _) => Revalidate(verbose: true);

        bottom.Controls.Add(ok);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(test);

        Controls.Add(root);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;

        Revalidate();
    }

    private void Revalidate(bool verbose = false)
    {
        var addr = _address.Text.Trim();
        if (addr.Length == 0)
        {
            _validation.Text = "";
            return;
        }

        var check = PrivateNetwork.Validate(addr);
        _validation.Text = check.Allowed
            ? "✓  " + check.Message
            : "✗  " + check.Message.Split('\n')[0];
        _validation.ForeColor = check.Allowed
            ? Color.FromArgb(20, 120, 60) : Color.FromArgb(190, 40, 40);

        if (verbose)
            MessageBox.Show(check.Message,
                check.Allowed ? "A cím megfelelő" : "A cím nem használható",
                MessageBoxButtons.OK,
                check.Allowed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void Save()
    {
        var addr = _address.Text.Trim();
        if (addr.Length == 0)
        {
            MessageBox.Show("Add meg a gép nevét vagy IP-címét.", "Hiányzó adat",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _address.Focus();
            return;
        }

        var check = PrivateNetwork.Validate(addr);
        if (!check.Allowed)
        {
            MessageBox.Show(check.Message, "A cím nem használható",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _address.Focus();
            return;
        }

        _host.Name = _name.Text.Trim();
        _host.Host = addr;
        _host.Group = _group.Text.Trim().Length > 0 ? _group.Text.Trim() : "Alapértelmezett";
        _host.Notes = _notes.Text.Trim();
        _host.IsDomainController = _isDc.Checked;
        _host.SkipPhysicalDiskIfVirtual = _skipDisk.Checked;
        _host.Enabled = _enabled.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }
}
