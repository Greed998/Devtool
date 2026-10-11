using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class RemotePage : UserControl
{
    private readonly TextBox _hosts = new();
    private readonly TextBox _user = new();
    private readonly TextBox _pass = new();
    private readonly CheckBox _useCurrentUser = new();
    private readonly CheckBox _pingFirst = new();
    private readonly Label _hostCount = new();
    private readonly CheckBox _cHardware = new();
    private readonly CheckBox _cSmart = new();
    private readonly CheckBox _cNetwork = new();
    private readonly CheckBox _cLicense = new();
    private readonly CheckBox _cApps = new();
    private readonly CheckBox _cEvents = new();
    private readonly ComboBox _period = new();
    private readonly RichTextBox _out = new();
    private readonly Button _btnRun, _btnTest, _btnStop, _btnSave;
    private readonly ToolStripStatusLabel _status = new();
    private CancellationTokenSource? _cts;

    private static readonly (string Label, int Hours)[] Periods =
    {
        ("24 óra", 24), ("3 nap", 72), ("7 nap", 168), ("30 nap", 720)
    };

    public RemotePage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        // ================= bal oldali beállítópanel
        var side = new Panel { Dock = DockStyle.Left, Width = 340, Padding = new Padding(14, 12, 14, 12) };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        flow.Controls.Add(SectionLabel("Célgépek"));
        _hosts.Multiline = true;
        _hosts.Height = 76;
        _hosts.Width = 300;
        _hosts.ScrollBars = ScrollBars.Vertical;
        _hosts.PlaceholderText =
            "Soronként egy gépnév, cím vagy tartomány:\n" +
            "IRODA-PC1\n192.168.1.50\n192.168.1.10-40\n192.168.1.0/24";
        _hosts.Text = AppSettings.Current.RemoteHosts;
        _hosts.TextChanged += (_, _) => UpdateHostCount();
        flow.Controls.Add(_hosts);

        flow.Controls.Add(new Label
        {
            Text = "Tartomány megadható rövid formában (192.168.1.10-40), teljes " +
                   "tartományként (192.168.1.10-192.168.1.40) vagy maszkkal (192.168.1.0/24).",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Color.FromArgb(120, 127, 140),
            Font = new Font("Segoe UI", 8.25F),
            Margin = new Padding(3, 4, 3, 2)
        });

        _hostCount.AutoSize = true;
        _hostCount.ForeColor = Color.FromArgb(0, 90, 165);
        _hostCount.Font = new Font("Segoe UI Semibold", 8.5F);
        _hostCount.Margin = new Padding(3, 2, 3, 2);
        flow.Controls.Add(_hostCount);

        _pingFirst.Text = "Csak az elérhető gépek (ping előszűrés)";
        _pingFirst.Checked = true;
        _pingFirst.AutoSize = true;
        _pingFirst.Margin = new Padding(3, 8, 3, 2);
        flow.Controls.Add(_pingFirst);

        flow.Controls.Add(SectionLabel("Bejelentkezés"));

        _useCurrentUser.Text = "A bejelentkezett felhasználó adataival";
        _useCurrentUser.Checked = string.IsNullOrWhiteSpace(AppSettings.Current.RemoteUser);
        _useCurrentUser.AutoSize = true;
        _useCurrentUser.Margin = new Padding(3, 2, 3, 6);
        _useCurrentUser.CheckedChanged += (_, _) => ApplyCredentialMode();
        flow.Controls.Add(_useCurrentUser);

        flow.Controls.Add(new Label
        {
            Text = $"Jelenleg bejelentkezve: {Environment.UserDomainName}\\{Environment.UserName}",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Color.FromArgb(120, 127, 140),
            Font = new Font("Segoe UI", 8.25F),
            Margin = new Padding(20, 0, 3, 6)
        });

        _user.Width = 300;
        _user.PlaceholderText = "TARTOMÁNY\\felhasználó  vagy  .\\Administrator";
        _user.Text = AppSettings.Current.RemoteUser;
        flow.Controls.Add(_user);

        _pass.Width = 300;
        _pass.UseSystemPasswordChar = true;
        _pass.PlaceholderText = "jelszó";
        _pass.Margin = new Padding(3, 6, 3, 3);
        flow.Controls.Add(_pass);

        ApplyCredentialMode();
        UpdateHostCount();

        _btnTest = MakeButton("Kapcsolat tesztelése", Color.White, Color.FromArgb(40, 46, 58));
        _btnTest.Click += async (_, _) => await TestAsync();
        flow.Controls.Add(_btnTest);

        flow.Controls.Add(SectionLabel("Mit gyűjtsön be"));
        AddCheck(flow, _cHardware, "Rendszer és hardver", true);
        AddCheck(flow, _cSmart, "Lemezállapot (SMART)", true);
        AddCheck(flow, _cNetwork, "Hálózati beállítások", true);
        AddCheck(flow, _cLicense, "Licenckulcsok, aktiválás", true);
        AddCheck(flow, _cApps, "Telepített szoftverek", false);
        AddCheck(flow, _cEvents, "Eseménynapló elemzés", true);

        var periodPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(20, 2, 3, 6)
        };
        periodPanel.Controls.Add(new Label
        {
            Text = "Napló időszaka:",
            AutoSize = true,
            Margin = new Padding(0, 6, 6, 0),
            ForeColor = Color.FromArgb(88, 96, 110)
        });
        foreach (var p in Periods) _period.Items.Add(p.Label);
        _period.SelectedIndex = 2;
        _period.DropDownStyle = ComboBoxStyle.DropDownList;
        _period.Width = 90;
        periodPanel.Controls.Add(_period);
        flow.Controls.Add(periodPanel);

        _btnRun = MakeButton("Adatgyűjtés indítása", Color.FromArgb(0, 120, 212), Color.White);
        _btnRun.Click += async (_, _) => await RunAsync();
        _btnRun.Margin = new Padding(3, 10, 3, 3);
        flow.Controls.Add(_btnRun);

        _btnStop = MakeButton("Megszakítás", Color.White, Color.FromArgb(40, 46, 58));
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => _cts?.Cancel();
        flow.Controls.Add(_btnStop);

        _btnSave = MakeButton("Jelentés mentése", Color.White, Color.FromArgb(40, 46, 58));
        _btnSave.Enabled = false;
        _btnSave.Click += (_, _) => Save();
        flow.Controls.Add(_btnSave);

        var help = new Label
        {
            Text = "Feltételek a célgépen:\n" +
                   "• a megadott fiók helyi rendszergazda legyen\n" +
                   "• tűzfalon engedélyezve: „Windows Management\n" +
                   "   Instrumentation (WMI)” és a naplóhoz a „Remote\n" +
                   "   Event Log Management” szabály\n" +
                   "• munkacsoportos gépnél a célgépen szükséges:\n" +
                   "   LocalAccountTokenFilterPolicy = 1",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Color.FromArgb(120, 127, 140),
            Font = new Font("Segoe UI", 8.25F),
            Margin = new Padding(3, 14, 3, 3)
        };
        flow.Controls.Add(help);

        side.Controls.Add(flow);

        // ================= jobb oldali kimenet
        _out.Dock = DockStyle.Fill;
        _out.ReadOnly = true;
        _out.BorderStyle = BorderStyle.None;
        _out.BackColor = Color.White;
        _out.Font = new Font("Consolas", 9F);
        _out.WordWrap = false;
        _out.ScrollBars = RichTextBoxScrollBars.Both;

        var outPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 4, 4) };
        outPanel.Controls.Add(_out);
        outPanel.Controls.Add(MakeHeader("Begyűjtött adatok"));

        var strip = new StatusStrip { SizingGrip = false };
        _status.Text = "Add meg a célgépeket, majd indítsd az adatgyűjtést.";
        strip.Items.Add(_status);

        Controls.Add(outPanel);
        Controls.Add(side);
        Controls.Add(strip);
    }

    // ---------------------------------------------------------------- segédek

    private static Label SectionLabel(string t) => new()
    {
        Text = t,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9F),
        ForeColor = Color.FromArgb(40, 46, 58),
        Margin = new Padding(3, 12, 3, 4)
    };

    private static Label MakeHeader(string text) => new()
    {
        Text = "  " + text,
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(246, 247, 249),
        ForeColor = Color.FromArgb(60, 68, 82),
        Font = new Font("Segoe UI Semibold", 9F)
    };

    private static void AddCheck(Control parent, CheckBox c, string text, bool on)
    {
        c.Text = text;
        c.Checked = on;
        c.AutoSize = true;
        c.Margin = new Padding(20, 3, 3, 3);
        parent.Controls.Add(c);
    }

    private static Button MakeButton(string text, Color back, Color fore)
    {
        var b = new Button
        {
            Text = text,
            Width = 300,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = new Font("Segoe UI Semibold", 9F),
            Margin = new Padding(3, 6, 3, 3),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = back == Color.White
            ? Color.FromArgb(203, 207, 214) : back;
        b.FlatAppearance.BorderSize = back == Color.White ? 1 : 0;
        return b;
    }

    private void ApplyCredentialMode()
    {
        var useCurrent = _useCurrentUser.Checked;
        _user.Enabled = !useCurrent;
        _pass.Enabled = !useCurrent;
        _user.BackColor = useCurrent ? Color.FromArgb(245, 246, 248) : Color.White;
        _pass.BackColor = _user.BackColor;
    }

    private void UpdateHostCount()
    {
        var r = HostRange.Expand(_hosts.Text);
        _hostCount.Text = r.Hosts.Count == 0
            ? ""
            : $"{r.Hosts.Count} gép a listában";
        _hostCount.ForeColor = r.Warnings.Count > 0
            ? Color.FromArgb(185, 100, 0) : Color.FromArgb(0, 90, 165);
        if (r.Warnings.Count > 0) _hostCount.Text += "  ⚠";
    }

    private HostRange.ExpandResult HostList() => HostRange.Expand(_hosts.Text);

    private WmiContext Ctx(string host) => new()
    {
        Host = host,
        // Ures felhasznalonev = a futo folyamat jogaival (integralt hitelesites).
        User = _useCurrentUser.Checked ? "" : _user.Text.Trim(),
        Password = _useCurrentUser.Checked ? "" : _pass.Text
    };

    private void Log(string text, Color? color = null, bool bold = false)
    {
        _out.SelectionStart = _out.TextLength;
        _out.SelectionLength = 0;
        _out.SelectionFont = new Font("Consolas", 9F, bold ? FontStyle.Bold : FontStyle.Regular);
        _out.SelectionColor = color ?? Color.FromArgb(50, 56, 68);
        _out.AppendText(text + Environment.NewLine);
        _out.ScrollToCaret();
    }

    // ---------------------------------------------------------------- teszt

    private async Task TestAsync()
    {
        var expand = HostList();
        var hosts = expand.Hosts;
        if (hosts.Count == 0)
        {
            MessageBox.Show("Adj meg legalább egy gépnevet vagy IP-címet.", "Hiányzó adat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnTest.Enabled = false;
        _out.Clear();
        Log($"Kapcsolat ellenőrzése – {hosts.Count} gép", Color.FromArgb(0, 90, 165), true);
        foreach (var w in expand.Warnings) Log("  " + w, Color.FromArgb(185, 100, 0));
        Log("");

        if (_pingFirst.Checked && hosts.Count > 4)
        {
            _status.Text = "Elérhetőség vizsgálata…";
            var progress = new Progress<string>(m => _status.Text = m);
            var alive = await HostRange.FilterAliveAsync(hosts, 500, progress, CancellationToken.None);
            Log($"Ping: {alive.Count} elérhető a(z) {hosts.Count} címből.",
                Color.FromArgb(20, 120, 60));
            Log("");
            hosts = alive;
        }

        foreach (var h in hosts)
        {
            var ctx = Ctx(h);
            var (ok, msg) = await Task.Run(() => ctx.TestConnection());
            Log($"{h,-24} {(ok ? "OK" : "HIBA")}   {msg}",
                ok ? Color.FromArgb(20, 120, 60) : Color.FromArgb(190, 40, 40));
        }

        _btnTest.Enabled = true;
        _status.Text = "Kapcsolatteszt kész.";
    }

    // ---------------------------------------------------------------- gyűjtés

    private async Task RunAsync()
    {
        var expand = HostList();
        var hosts = expand.Hosts;
        if (hosts.Count == 0)
        {
            MessageBox.Show("Adj meg legalább egy gépnevet vagy IP-címet.", "Hiányzó adat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _btnRun.Enabled = false;
        _btnTest.Enabled = false;
        _btnStop.Enabled = true;
        _btnSave.Enabled = false;
        _out.Clear();

        var hours = Periods[Math.Max(0, _period.SelectedIndex)].Hours;

        Log($"DevToolbox – távoli adatgyűjtés   ({DateTime.Now:yyyy.MM.dd HH:mm})", Color.FromArgb(0, 90, 165), true);
        Log($"Hitelesítés: {(_useCurrentUser.Checked ? $"{Environment.UserDomainName}\\{Environment.UserName} (bejelentkezett felhasználó)" : _user.Text.Trim())}",
            Color.FromArgb(100, 106, 118));
        foreach (var w in expand.Warnings) Log("  " + w, Color.FromArgb(185, 100, 0));
        Log(new string('=', 78), Color.FromArgb(150, 156, 168));
        Log("");

        try
        {
            if (_pingFirst.Checked && hosts.Count > 4)
            {
                _status.Text = "Elérhetőség vizsgálata…";
                var pingProgress = new Progress<string>(m => _status.Text = m);
                var alive = await HostRange.FilterAliveAsync(hosts, 500, pingProgress, token);
                Log($"Ping előszűrés: {alive.Count} elérhető gép a(z) {hosts.Count} címből.",
                    Color.FromArgb(20, 120, 60), true);
                Log("");
                hosts = alive;

                if (hosts.Count == 0)
                {
                    Log("Egyetlen cím sem válaszolt. Ha a tűzfal tiltja a pinget, " +
                        "kapcsold ki az előszűrést.", Color.FromArgb(185, 100, 0));
                    _status.Text = "Nincs elérhető gép.";
                    return;
                }
            }

            var index = 0;
            foreach (var host in hosts)
            {
                token.ThrowIfCancellationRequested();
                index++;
                _status.Text = $"[{index}/{hosts.Count}] {host} lekérdezése…";

                Log($"■ {host}", Color.FromArgb(20, 30, 45), true);
                Log(new string('-', 78), Color.FromArgb(190, 195, 205));

                var ctx = Ctx(host);
                var (ok, msg) = await Task.Run(() => ctx.TestConnection(), token);
                if (!ok)
                {
                    Log("  Nem érhető el: " + msg, Color.FromArgb(190, 40, 40));
                    Log("");
                    continue;
                }

                if (_cHardware.Checked) await SectionAsync("Rendszer és hardver",
                    () => HardwareInfo.Collect(ctx), token);

                if (_cNetwork.Checked) await SectionAsync("Hálózat",
                    () => NetworkInfo.Collect(ctx), token);

                if (_cSmart.Checked) await SmartSectionAsync(ctx, token);
                if (_cLicense.Checked) await LicenseSectionAsync(ctx, token);
                if (_cApps.Checked) await AppsSectionAsync(ctx, token);
                if (_cEvents.Checked) await EventsSectionAsync(ctx, hours, token);

                Log("");
            }

            _status.Text = $"Kész – {hosts.Count} gép feldolgozva.";
            _btnSave.Enabled = true;

            var st = AppSettings.Current;
            st.RemoteHosts = _hosts.Text.Trim();
            st.RemoteUser = _useCurrentUser.Checked ? "" : _user.Text.Trim();
            st.Save();
        }
        catch (OperationCanceledException)
        {
            Log("");
            Log("[megszakítva]", Color.FromArgb(190, 105, 0));
            _status.Text = "Megszakítva.";
        }
        catch (Exception ex)
        {
            Log("HIBA: " + ex.Message, Color.FromArgb(190, 40, 40));
        }
        finally
        {
            _btnRun.Enabled = true;
            _btnTest.Enabled = true;
            _btnStop.Enabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task SectionAsync(string title, Func<List<HwSection>> work, CancellationToken token)
    {
        Log($"  [{title}]", Color.FromArgb(0, 90, 165), true);
        var sections = await Task.Run(work, token);

        if (sections.Count == 0) { Log("    (nincs adat)", Color.FromArgb(140, 146, 158)); return; }

        foreach (var s in sections)
        {
            Log($"    {s.Title}", Color.FromArgb(70, 78, 92), true);
            foreach (var r in s.Rows) Log($"      {r.Key,-26} {r.Value}");
        }
        Log("");
    }

    private async Task SmartSectionAsync(WmiContext ctx, CancellationToken token)
    {
        Log("  [Lemezállapot – SMART]", Color.FromArgb(0, 90, 165), true);
        var drives = await Task.Run(() => SmartInfo.Collect(ctx, includeLocalSensors: false), token);

        if (drives.Count == 0) { Log("    (nincs adat)", Color.FromArgb(140, 146, 158)); return; }

        foreach (var d in drives)
        {
            var color = d.Verdict switch
            {
                SmartVerdict.Kritikus => Color.FromArgb(190, 30, 30),
                SmartVerdict.Figyelendo => Color.FromArgb(185, 100, 0),
                SmartVerdict.Rendben => Color.FromArgb(20, 120, 60),
                _ => Color.FromArgb(130, 136, 148)
            };
            Log($"    {d.Model}  ({Fmt.Bytes(d.SizeBytes)})  –  {d.Verdict}", color, true);
            foreach (var f in d.Findings) Log("      • " + f, color);

            foreach (var a in d.Attributes.Where(a => a.Critical || a.Id is 9 or 194 or 199)
                                          .OrderBy(a => a.Id))
                Log($"        {a.Id,-4} {a.Name,-32} {a.RawDisplay}");
        }
        Log("");
    }

    private async Task LicenseSectionAsync(WmiContext ctx, CancellationToken token)
    {
        Log("  [Licenckulcsok]", Color.FromArgb(0, 90, 165), true);
        var entries = await Task.Run(() => LicenseInfo.Collect(ctx), token);

        foreach (var e in entries)
        {
            Log($"    {e.Product}", Color.FromArgb(70, 78, 92), true);
            Log($"      Kulcs:   {e.Key}", Color.FromArgb(20, 60, 130));
            Log($"      Állapot: {e.Status}  {e.Channel}");
        }
        Log("");
    }

    private async Task AppsSectionAsync(WmiContext ctx, CancellationToken token)
    {
        Log("  [Telepített szoftverek]", Color.FromArgb(0, 90, 165), true);
        Log("    (a távoli registry olvasása lassú, kis türelmet…)", Color.FromArgb(140, 146, 158));

        var apps = await Task.Run(() => LicenseInfo.GetInstalledApps(ctx, 300), token);
        if (apps.Count == 0) { Log("    (nincs adat)", Color.FromArgb(140, 146, 158)); return; }

        foreach (var a in apps) Log($"      {a.Name,-50} {a.Version,-16} {a.Publisher}");
        Log($"    Összesen: {apps.Count} db");
        Log("");
    }

    private async Task EventsSectionAsync(WmiContext ctx, int hours, CancellationToken token)
    {
        Log("  [Eseménynapló]", Color.FromArgb(0, 90, 165), true);

        var result = await Task.Run(() => EventLogAnalyzer.Scan(
            ctx, new[] { "System", "Application" }, hours, true, 5000, null, token), token);

        foreach (var s in result.Skipped) Log("    " + s, Color.FromArgb(185, 100, 0));

        Log($"    {result.TotalEvents} esemény · {result.Critical} kritikus, " +
            $"{result.Errors} hiba, {result.Warnings} figyelmeztetés");

        var important = result.Groups.Where(g => g.EffectiveSeverity >= Severity.Figyelmeztetes).Take(15).ToList();
        if (important.Count == 0) { Log("    Nem található érdemi hiba.", Color.FromArgb(20, 120, 60)); Log(""); return; }

        foreach (var g in important)
        {
            var color = g.EffectiveSeverity switch
            {
                Severity.Kritikus => Color.FromArgb(190, 30, 30),
                Severity.Komoly => Color.FromArgb(185, 100, 0),
                _ => Color.FromArgb(120, 100, 20)
            };
            Log($"    {g.Count,4}x  {g.Headline}" + (g.Subject.Length > 0 ? $"  ({g.Subject})" : ""), color);
            if (g.Diag is { } d) Log($"          → {d.Action}", Color.FromArgb(100, 106, 118));
        }
        Log("");
    }

    private void Save()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Távoli jelentés mentése",
            Filter = "Szövegfájl|*.txt",
            FileName = $"tavoli_jelentes_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dlg.FileName, _out.Text, new UTF8Encoding(true));
            MessageBox.Show("Mentve:\n" + dlg.FileName, "Kész",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}
