using System.Runtime.Versioning;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class MonitorSettingsForm : Form
{
    private readonly NumericUpDown _interval = new();
    private readonly NumericUpDown _diskWarn = new();
    private readonly NumericUpDown _diskCrit = new();
    private readonly NumericUpDown _pingTimeout = new();
    private readonly NumericUpDown _timeSkew = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _notifyError = new();
    private readonly CheckBox _notifyRecovery = new();
    private readonly CheckBox _minimizeToTray = new();

    public MonitorSettingsForm()
    {
        var cfg = MonitorConfig.Current;

        Text = "Felügyelet beállításai";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 560);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(20, 16, 24, 12),
            AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Head(string t)
        {
            var l = new Label
            {
                Text = t,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10F),
                ForeColor = Color.FromArgb(28, 32, 40),
                Margin = new Padding(0, 14, 0, 6)
            };
            root.Controls.Add(l);
            root.SetColumnSpan(l, 2);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

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
                Margin = new Padding(0, 3, 8, 0)
            });
            c.Margin = new Padding(0, 3, 0, 3);
            root.Controls.Add(c);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        void Full(Control c)
        {
            root.Controls.Add(c);
            root.SetColumnSpan(c, 2);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        void Note(string t)
        {
            Full(new Label
            {
                Text = t,
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                ForeColor = Color.FromArgb(130, 137, 150),
                Font = new Font("Segoe UI", 8.25F),
                Margin = new Padding(0, 2, 0, 8)
            });
        }

        void Num(NumericUpDown n, int min, int max, int val)
        {
            n.Minimum = min; n.Maximum = max;
            n.Value = Math.Clamp(val, min, max);
            n.Width = 90;
        }

        // ---------------- ütemezés
        Head("Ütemezés");
        Num(_interval, 1, 1440, cfg.IntervalMinutes);
        Row("Ellenőrzés gyakorisága (perc):", _interval);
        Note("Sűrűbb ellenőrzés gyorsabban jelez, de több hálózati forgalmat és " +
             "processzoridőt jelent. Tartományvezérlőknél 5–15 perc a szokásos.");

        _autoStart.Text = "Automatikus figyelés indítása a program indulásakor";
        _autoStart.Checked = cfg.AutoStart;
        _autoStart.AutoSize = true;
        Full(_autoStart);

        _minimizeToTray.Text = "Az ablak bezárásakor a program a tálcán fusson tovább";
        _minimizeToTray.Checked = cfg.MinimizeToTray;
        _minimizeToTray.AutoSize = true;
        Full(_minimizeToTray);
        Note("Kikapcsolva a bezárás valóban kilép, és a figyelés is leáll.");

        // ---------------- értesítés
        Head("Értesítés");
        _notifyError.Text = "Értesítés, ha egy gépen hiba lép fel";
        _notifyError.Checked = cfg.NotifyOnError;
        _notifyError.AutoSize = true;
        Full(_notifyError);

        _notifyRecovery.Text = "Értesítés, ha a hiba megszűnik";
        _notifyRecovery.Checked = cfg.NotifyOnRecovery;
        _notifyRecovery.AutoSize = true;
        Full(_notifyRecovery);
        Note("Az értesítés csak ÁLLAPOTVÁLTÁSKOR érkezik, és gépenként legfeljebb " +
             "15 percenként – így egy billegő kapcsolat nem áraszt el.");

        // ---------------- küszöbök
        Head("Küszöbértékek");
        Num(_diskWarn, 50, 98, cfg.DiskWarnPercent);
        Row("Lemez figyelmeztetés (% foglalt):", _diskWarn);
        Num(_diskCrit, 51, 99, cfg.DiskCriticalPercent);
        Row("Lemez kritikus (% foglalt):", _diskCrit);
        Note("Tartományvezérlőn a megtelt rendszerlemez leállítja a címtárszolgáltatást, " +
             "ezért érdemes korán szólni.");

        Num(_timeSkew, 30, 280, cfg.TimeSkewWarnSeconds);
        Row("Óraeltérés figyelmeztetés (mp):", _timeSkew);
        Note("A Kerberos 5 percnél (300 mp) nagyobb eltérésnél megtagadja a " +
             "bejelentkezést, ezért efölött már hibát jelzünk.");

        Num(_pingTimeout, 200, 10000, cfg.PingTimeoutMs);
        Row("Ping időtúllépés (ms):", _pingTimeout);

        // ---------------- gombsor
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(20, 11, 24, 11) };

        var ok = new Button
        {
            Text = "Mentés", Dock = DockStyle.Right, Width = 110,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5F)
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

        bottom.Controls.Add(ok);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(cancel);

        Controls.Add(root);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Save()
    {
        if (_diskCrit.Value <= _diskWarn.Value)
        {
            MessageBox.Show("A kritikus küszöb legyen nagyobb a figyelmeztetésinél.",
                "Ellentmondó érték", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cfg = MonitorConfig.Current;
        cfg.IntervalMinutes = (int)_interval.Value;
        cfg.DiskWarnPercent = (int)_diskWarn.Value;
        cfg.DiskCriticalPercent = (int)_diskCrit.Value;
        cfg.PingTimeoutMs = (int)_pingTimeout.Value;
        cfg.TimeSkewWarnSeconds = (int)_timeSkew.Value;
        cfg.AutoStart = _autoStart.Checked;
        cfg.NotifyOnError = _notifyError.Checked;
        cfg.NotifyOnRecovery = _notifyRecovery.Checked;
        cfg.MinimizeToTray = _minimizeToTray.Checked;
        cfg.Save();

        DialogResult = DialogResult.OK;
        Close();
    }
}
