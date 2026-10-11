using System.Diagnostics;
using System.Runtime.Versioning;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private readonly AppSettings _s;

    private readonly TextBox _scripts = new();
    private readonly TextBox _export = new();
    private readonly CheckBox _admin = new();
    private readonly NumericUpDown _refresh = new();
    private readonly CheckBox _live = new();
    private readonly NumericUpDown _tempWarn = new();
    private readonly NumericUpDown _tempCrit = new();
    private readonly ComboBox _period = new();
    private readonly CheckBox _warnings = new();
    private readonly CheckBox _hideNoise = new();
    private readonly ComboBox _encoding = new();
    private readonly CheckBox _confirmRun = new();
    private readonly TextBox _remoteUser = new();
    private readonly TextBox _remoteHosts = new();

    private readonly string _oldScriptsFolder;

    private static readonly (string Label, int Hours)[] Periods =
    {
        ("24 óra", 24), ("3 nap", 72), ("7 nap", 168), ("30 nap", 720)
    };

    public SettingsForm()
    {
        _s = AppSettings.Current.Clone();
        _oldScriptsFolder = AppPaths.ScriptsDir;

        Text = "Beállítások";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(780, 660);
        MinimumSize = new Size(700, 560);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(PathsPage());
        tabs.TabPages.Add(StartupPage());
        tabs.TabPages.Add(HardwarePage());
        tabs.TabPages.Add(EventPage());
        tabs.TabPages.Add(RemotePage());

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(16, 11, 16, 11) };

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
        ok.Click += (_, _) => SaveAndClose();

        var cancel = new Button
        {
            Text = "Mégse", Dock = DockStyle.Right, Width = 100,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var reset = new Button
        {
            Text = "Alapértelmezettek", Dock = DockStyle.Left, Width = 145,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White
        };
        reset.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        reset.Click += (_, _) => ResetDefaults();

        bottom.Controls.Add(ok);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(reset);

        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    // ================================================================ lapok

    private TabPage PathsPage()
    {
        var p = NewPage("  Elérési utak  ");
        var f = NewFlow();

        f.Controls.Add(Head("Szkriptek könyvtára"));
        f.Controls.Add(Note("Ide másolódnak a betöltött és itt jönnek létre az új szkriptek. " +
                            "Hálózati vagy felhős mappa is megadható, így több gép ugyanazt a " +
                            "készletet használhatja."));
        _scripts.Text = _s.ScriptsFolder;
        _scripts.PlaceholderText = AppPaths.DefaultScriptsDir + "   (alapértelmezett)";
        f.Controls.Add(PathRow(_scripts, () => _s.ScriptsFolder = ""));

        f.Controls.Add(Head("Exportálás alapmappája"));
        f.Controls.Add(Note("A jelentések alapértelmezett helye. Az exportálás ablakban " +
                            "alkalmanként felülírható."));
        _export.Text = _s.ExportFolder;
        _export.PlaceholderText = AppSettings.DefaultExportFolder + "   (alapértelmezett)";
        f.Controls.Add(PathRow(_export, () => _s.ExportFolder = ""));

        f.Controls.Add(Head("Aktuális helyek"));
        f.Controls.Add(ReadOnlyRow("Adatmappa", AppPaths.DataDir));
        f.Controls.Add(ReadOnlyRow("Beállítások fájl", AppPaths.SettingsFile));
        f.Controls.Add(ReadOnlyRow("Program", AppPaths.ExePath));

        var open = new Button
        {
            Text = "Adatmappa megnyitása", AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(200, 30),
            Anchor = AnchorStyles.Left,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(0, 10, 0, 0)
        };
        open.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        open.Click += (_, _) =>
        {
            try
            {
                AppPaths.EnsureDataDirs();
                Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
            }
            catch { }
        };
        f.Controls.Add(open);

        p.Controls.Add(f);
        return p;
    }

    private TabPage StartupPage()
    {
        var p = NewPage("  Indítás  ");
        var f = NewFlow();

        f.Controls.Add(Head("Jogosultság"));
        _admin.Text = "Mindig rendszergazdaként induljon";
        _admin.Checked = _s.AlwaysRunAsAdmin;
        _admin.AutoSize = true;
        f.Controls.Add(_admin);
        f.Controls.Add(Note("Enélkül a hőmérséklet-érzékelők, a SMART attribútumok és a " +
                            "Biztonság eseménynapló nem olvasható. Bekapcsolva a program " +
                            "indításkor UAC megerősítést kér, és ha elutasítod, felajánlja a " +
                            "korlátozott futtatást."));

        f.Controls.Add(Head("Jelenlegi állapot"));
        f.Controls.Add(ReadOnlyRow("Mód", AppPaths.ModeLabel));
        f.Controls.Add(ReadOnlyRow("Ez a példány",
            HardwareInfo.IsElevated() ? "rendszergazdaként fut" : "normál jogosultsággal fut"));

        if (AppPaths.Mode == AppMode.Portable)
            f.Controls.Add(Note("Hordozható módban az emelt jogú indítás az exe melletti " +
                                "beállításfájlból jön. Másik gépen ugyanígy fog viselkedni."));
        else
            f.Controls.Add(Note("Telepített módban a Start menü és az asztali parancsikon is " +
                                "„futtatás rendszergazdaként” jelzővel készült."));

        f.Controls.Add(Head("Szkriptek"));
        _confirmRun.Text = "Megerősítés kérése szkript futtatása előtt";
        _confirmRun.Checked = _s.ConfirmBeforeRun;
        _confirmRun.AutoSize = true;
        f.Controls.Add(_confirmRun);

        f.Controls.Add(LabelFor("Új szkriptek alapértelmezett kódolása"));
        _encoding.DropDownStyle = ComboBoxStyle.DropDownList;
        _encoding.Items.AddRange(ScriptRunner.SupportedEncodings.Cast<object>().ToArray());
        _encoding.SelectedItem = _s.DefaultScriptEncoding;
        _encoding.Width = 140;
        f.Controls.Add(_encoding);
        f.Controls.Add(Note("Régi, ékezetes batch fájloknál a cp852 vagy cp1250 adja a helyes kimenetet."));

        p.Controls.Add(f);
        return p;
    }

    private TabPage HardwarePage()
    {
        var p = NewPage("  Hardver  ");
        var f = NewFlow();

        f.Controls.Add(Head("Élő érzékelők"));
        _live.Text = "Szenzorok automatikus frissítése";
        _live.Checked = _s.LiveSensors;
        _live.AutoSize = true;
        f.Controls.Add(_live);

        f.Controls.Add(LabelFor("Frissítési időköz (másodperc)"));
        _refresh.Minimum = 1; _refresh.Maximum = 60; _refresh.Width = 80;
        _refresh.Value = Math.Clamp(_s.SensorRefreshSeconds, 1, 60);
        f.Controls.Add(_refresh);
        f.Controls.Add(Note("Sűrűbb frissítés több terhelést jelent, mert a szenzorolvasás " +
                            "SMART- és I/O-műveleteket indít."));

        f.Controls.Add(Head("Hőmérséklet-küszöbök"));
        f.Controls.Add(LabelFor("Figyelmeztetés (°C) – narancssárga"));
        _tempWarn.Minimum = 40; _tempWarn.Maximum = 110; _tempWarn.Width = 80;
        _tempWarn.Value = Math.Clamp(_s.TempWarnCelsius, 40, 110);
        f.Controls.Add(_tempWarn);

        f.Controls.Add(LabelFor("Kritikus (°C) – piros"));
        _tempCrit.Minimum = 41; _tempCrit.Maximum = 125; _tempCrit.Width = 80;
        _tempCrit.Value = Math.Clamp(_s.TempCriticalCelsius, 41, 125);
        f.Controls.Add(_tempCrit);

        p.Controls.Add(f);
        return p;
    }

    private TabPage EventPage()
    {
        var p = NewPage("  Eseménynapló  ");
        var f = NewFlow();

        f.Controls.Add(Head("Alapértelmezett elemzés"));
        f.Controls.Add(LabelFor("Vizsgált időszak"));
        _period.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var x in Periods) _period.Items.Add(x.Label);
        _period.SelectedIndex = Math.Max(0, Array.FindIndex(Periods, x => x.Hours == _s.EventLogHours));
        _period.Width = 140;
        f.Controls.Add(_period);

        _warnings.Text = "Figyelmeztetések is jelenjenek meg (ne csak a hibák)";
        _warnings.Checked = _s.EventLogWarnings;
        _warnings.AutoSize = true;
        _warnings.Margin = new Padding(0, 10, 0, 3);
        f.Controls.Add(_warnings);

        _hideNoise.Text = "Ismert ártalmatlan események elrejtése";
        _hideNoise.Checked = _s.HideKnownNoise;
        _hideNoise.AutoSize = true;
        f.Controls.Add(_hideNoise);
        f.Controls.Add(Note("A DistributedCOM 10016, Schannel 36887 és hasonló bejegyzések a " +
                            "legtöbb gépen megjelennek, és nem jeleznek valódi hibát. " +
                            "Alapesetben a lista aljára kerülnek; ezzel teljesen elrejthetők."));

        p.Controls.Add(f);
        return p;
    }

    private TabPage RemotePage()
    {
        var p = NewPage("  Távoli gépek  ");
        var f = NewFlow();

        f.Controls.Add(Head("Alapértelmezett bejelentkezés"));
        _remoteUser.Text = _s.RemoteUser;
        _remoteUser.Dock = DockStyle.Fill;
        _remoteUser.Margin = new Padding(0, 2, 24, 2);
        _remoteUser.PlaceholderText = "TARTOMÁNY\\felhasználó  vagy  .\\Administrator";
        f.Controls.Add(_remoteUser);
        f.Controls.Add(Note("A jelszót a program szándékosan NEM tárolja el. Minden " +
                            "adatgyűjtéskor be kell írni – így a beállításfájl kiszivárgása " +
                            "nem ad hozzáférést az ügyfélgépekhez."));

        f.Controls.Add(Head("Gyakran használt gépek"));
        _remoteHosts.Multiline = true;
        _remoteHosts.Height = 150;
        _remoteHosts.Dock = DockStyle.Fill;
        _remoteHosts.Margin = new Padding(0, 2, 24, 2);
        _remoteHosts.ScrollBars = ScrollBars.Vertical;
        _remoteHosts.Text = _s.RemoteHosts;
        _remoteHosts.PlaceholderText = "Soronként egy gépnév vagy IP-cím";
        f.Controls.Add(_remoteHosts);
        f.Controls.Add(Note("Ez a lista töltődik be a Távoli gép fülre induláskor."));

        p.Controls.Add(f);
        return p;
    }

    // ================================================================ segédek

    private static TabPage NewPage(string title) => new(title) { BackColor = Color.White };

    /// <summary>
    /// Egyoszlopos, szelessegre kitolto elrendezes. A FlowLayoutPanel nem nyujtja a
    /// gyermekeket a szulo szelessegere, ezert lognak ki a gombok - a TableLayoutPanel igen.
    /// </summary>
    private static TableLayoutPanel NewFlow()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(20, 12, 24, 16),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    /// <summary>A tordelheto cimke szelessege koveti az ablakot.</summary>
    private static void HookWrap(Label l)
    {
        void Update()
        {
            var p = l.Parent;
            if (p is null) return;
            var w = p.ClientSize.Width - p.Padding.Horizontal - l.Margin.Horizontal - 6;
            if (w > 120 && l.MaximumSize.Width != w) l.MaximumSize = new Size(w, 0);
        }

        l.ParentChanged += (_, _) =>
        {
            if (l.Parent is { } p) p.ClientSizeChanged += (_, _) => Update();
            Update();
        };
    }

    private static Label Head(string t) => new()
    {
        Text = t,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10F),
        ForeColor = Color.FromArgb(28, 32, 40),
        Margin = new Padding(0, 14, 0, 6)
    };

    private static Label LabelFor(string t) => new()
    {
        Text = t,
        AutoSize = true,
        ForeColor = Color.FromArgb(70, 78, 92),
        Margin = new Padding(0, 8, 0, 3)
    };

    private static Label Note(string t)
    {
        var l = new Label
        {
            Text = t,
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = Color.FromArgb(120, 127, 140),
            Font = new Font("Segoe UI", 8.5F),
            Margin = new Padding(0, 4, 0, 8)
        };
        HookWrap(l);
        return l;
    }

    /// <summary>Cimke fix szelessegu oszlopban (igy egy vonalban all), az ertek kitolti a maradekot.</summary>
    private Control ReadOnlyRow(string key, string value)
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 2, 0, 2)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        t.Controls.Add(new Label
        {
            Text = key + ":",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(120, 127, 140),
            Margin = new Padding(0)
        }, 0, 0);

        t.Controls.Add(new TextBox
        {
            Text = value,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Margin = new Padding(0, 2, 0, 2)
        }, 1, 0);

        return t;
    }

    private Control PathRow(TextBox box, Action clear)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 2, 0, 4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 1, 6, 1);

        var browse = new Button
        {
            Text = "Tallózás…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(94, 25), Padding = new Padding(6, 0, 6, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(0, 0, 4, 0)
        };
        browse.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { UseDescriptionForTitle = true };
            if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        };

        var def = new Button
        {
            Text = "Alap", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(64, 25), Padding = new Padding(6, 0, 6, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(0)
        };
        def.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        def.Click += (_, _) => { box.Text = ""; clear(); };

        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        panel.Controls.Add(def, 2, 0);
        return panel;
    }

    // ================================================================ mentés

    private void ResetDefaults()
    {
        if (MessageBox.Show("Minden beállítás visszaáll az alapértelmezettre. Folytatod?",
                "Alapértelmezettek", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes) return;

        var d = new AppSettings();
        _scripts.Text = ""; _export.Text = "";
        _admin.Checked = d.AlwaysRunAsAdmin;
        _live.Checked = d.LiveSensors;
        _refresh.Value = d.SensorRefreshSeconds;
        _tempWarn.Value = d.TempWarnCelsius;
        _tempCrit.Value = d.TempCriticalCelsius;
        _period.SelectedIndex = Math.Max(0, Array.FindIndex(Periods, x => x.Hours == d.EventLogHours));
        _warnings.Checked = d.EventLogWarnings;
        _hideNoise.Checked = d.HideKnownNoise;
        _encoding.SelectedItem = d.DefaultScriptEncoding;
        _confirmRun.Checked = d.ConfirmBeforeRun;
        _remoteUser.Text = ""; _remoteHosts.Text = "";
    }

    private void SaveAndClose()
    {
        if (_tempCrit.Value <= _tempWarn.Value)
        {
            MessageBox.Show("A kritikus hőmérséklet legyen nagyobb a figyelmeztetésinél.",
                "Ellentmondó érték", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var newScripts = _scripts.Text.Trim();
        if (newScripts.Length > 0)
        {
            try { Directory.CreateDirectory(newScripts); }
            catch (Exception ex)
            {
                MessageBox.Show("A megadott szkriptmappa nem hozható létre:\n\n" + ex.Message,
                    "Hibás elérési út", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        _s.ScriptsFolder = newScripts;
        _s.ExportFolder = _export.Text.Trim();
        _s.AlwaysRunAsAdmin = _admin.Checked;
        _s.LiveSensors = _live.Checked;
        _s.SensorRefreshSeconds = (int)_refresh.Value;
        _s.TempWarnCelsius = (int)_tempWarn.Value;
        _s.TempCriticalCelsius = (int)_tempCrit.Value;
        _s.EventLogHours = Periods[Math.Max(0, _period.SelectedIndex)].Hours;
        _s.EventLogWarnings = _warnings.Checked;
        _s.HideKnownNoise = _hideNoise.Checked;
        _s.DefaultScriptEncoding = _encoding.SelectedItem?.ToString() ?? "utf-8";
        _s.ConfirmBeforeRun = _confirmRun.Checked;
        _s.RemoteUser = _remoteUser.Text.Trim();
        _s.RemoteHosts = _remoteHosts.Text.Trim();

        AppSettings.Replace(_s);

        // Ha a szkriptmappa megváltozott, ne maradjanak árván a régi fájlok.
        var nowScripts = AppPaths.ScriptsDir;
        if (!string.Equals(nowScripts, _oldScriptsFolder, StringComparison.OrdinalIgnoreCase))
            OfferScriptMove(_oldScriptsFolder, nowScripts);

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OfferScriptMove(string from, string to)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(from)) return;
            files = Directory.GetFiles(from);
            if (files.Length == 0) return;
        }
        catch { return; }

        var r = MessageBox.Show(
            $"A szkriptmappa megváltozott.\n\nRégi hely:\n{from}\n\nÚj hely:\n{to}\n\n" +
            $"Átmásoljam az ott lévő {files.Length} fájlt az új helyre?\n" +
            "(Az eredetiek megmaradnak.)",
            "Szkriptek áthelyezése", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        int copied = 0;
        foreach (var f in files)
        {
            try
            {
                var dest = Path.Combine(to, Path.GetFileName(f));
                if (!File.Exists(dest)) { File.Copy(f, dest); copied++; }
            }
            catch { }
        }

        MessageBox.Show($"{copied} fájl átmásolva.", "Kész",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
