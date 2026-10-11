using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class DashboardPage : UserControl
{
    private readonly Panel _setup = new();
    private readonly Panel _main = new();
    private readonly Label _banner = new();
    private readonly Label _bannerSub = new();
    private readonly ListView _hosts = new();
    private readonly ListView _checks = new();
    private readonly RichTextBox _detail = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripButton _btnAuto;
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private bool _loaded;

    public DashboardPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        BuildSetup();
        BuildMain();

        Controls.Add(_main);
        Controls.Add(_setup);

        _btnAuto = new ToolStripButton("Automatikus figyelés")
        { CheckOnClick = true, DisplayStyle = ToolStripItemDisplayStyle.Text };

        BuildToolbar();

        // A felulet csak akkor frissul, ha latszik - talcan nem porgetunk feleslegesen.
        _uiTimer.Interval = 2000;
        _uiTimer.Tick += (_, _) => RefreshStatusLine();
        VisibleChanged += (_, _) => _uiTimer.Enabled = Visible && _loaded;

        MonitorService.Instance.CycleCompleted += OnCycleCompleted;
        MonitorService.Instance.HostChecked += (_, _) => { };
    }

    // ================================================================ felépítés

    private void BuildToolbar()
    {
        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(6, 4, 6, 4)
        };

        var btnCheck = new ToolStripButton("Ellenőrzés most") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnCheck.Click += async (_, _) => await CheckNowAsync();

        _btnAuto.Checked = MonitorService.Instance.IsRunning;
        _btnAuto.CheckedChanged += (_, _) => ToggleAuto();

        var btnAdd = new ToolStripButton("Gép felvétele") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnAdd.Click += (_, _) => AddHost();

        var btnEdit = new ToolStripButton("Szerkesztés") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnEdit.Click += (_, _) => EditHost();

        var btnDelete = new ToolStripButton("Eltávolítás") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnDelete.Click += (_, _) => DeleteHost();

        var btnSettings = new ToolStripButton("Figyelés beállításai") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnSettings.Click += (_, _) => EditSettings();

        var btnExport = new ToolStripButton("Jelentés mentése") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnExport.Click += (_, _) => Export();

        tools.Items.AddRange(new ToolStripItem[]
        {
            btnCheck, _btnAuto, new ToolStripSeparator(),
            btnAdd, btnEdit, btnDelete, new ToolStripSeparator(),
            btnSettings, btnExport
        });

        _main.Controls.Add(tools);
        tools.SendToBack();
    }

    private void BuildSetup()
    {
        _setup.Dock = DockStyle.Fill;
        _setup.BackColor = Color.White;
        _setup.Visible = false;

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(60, 50, 60, 40) };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        flow.Controls.Add(new Label
        {
            Text = "Első beállítás",
            Font = new Font("Segoe UI Semibold", 18F),
            ForeColor = Color.FromArgb(28, 32, 40),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        });

        flow.Controls.Add(new Label
        {
            Text = "A vezérlőpult figyelt gépek állapotát mutatja. Kezdéshez vegyél fel " +
                   "legalább egy tartományvezérlőt.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = Color.FromArgb(80, 88, 102),
            Font = new Font("Segoe UI", 10F),
            Margin = new Padding(0, 0, 0, 22)
        });

        flow.Controls.Add(Card("Mit fog ellenőrizni",
            "• Elérhetőség és válaszidő (ping, illetve tűzfal mögött TCP-port)\n" +
            "• LDAP, Kerberos, DNS, SMB és RPC portok\n" +
            "• Címtárszolgáltatások futása, SYSVOL és NETLOGON megosztás\n" +
            "• Óraeltérés – a Kerberos 5 percnél nagyobb csúszásnál megtagadja a belépést\n" +
            "• Lemezkapacitás és fizikai lemezállapot\n" +
            "• Az elmúlt 24 óra komoly eseménynapló-hibái"));

        flow.Controls.Add(Card("Csak olvas",
            "A felügyelet egyetlen műveletet sem hajt végre a célgépen: nem indít és nem " +
            "állít le semmit, nem módosít beállítást. A lekérdezés a bejelentkezett " +
            "felhasználó jogaival megy, jelszót a program nem tárol."));

        flow.Controls.Add(Card("Csak belső hálózaton",
            "Publikus IP-címet a program nem fogad el. Ez szándékos: a modul hitelesítve és " +
            "rendszeresen kérdez le, amit nem szabad az interneten át tenni. Távoli " +
            "telephelyhez építs VPN-t – azon keresztül a gép belső címen látszik, és működik."));

        var btn = new Button
        {
            Text = "Tartományvezérlő felvétele",
            Width = 240,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10F),
            Margin = new Padding(0, 20, 0, 0),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += (_, _) => AddHost();
        flow.Controls.Add(btn);

        host.Controls.Add(flow);
        _setup.Controls.Add(host);
    }

    private static Panel Card(string title, string body)
    {
        var p = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.FromArgb(249, 250, 252),
            Padding = new Padding(18, 14, 18, 14),
            Margin = new Padding(0, 0, 0, 12),
            Width = 660
        };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 229, 235));
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };

        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        flow.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(28, 32, 40),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            BackColor = Color.Transparent
        });
        flow.Controls.Add(new Label
        {
            Text = body,
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = Color.FromArgb(80, 88, 102),
            BackColor = Color.Transparent
        });
        p.Controls.Add(flow);
        return p;
    }

    private void BuildMain()
    {
        _main.Dock = DockStyle.Fill;
        _main.Visible = false;

        // ---- állapotsáv
        var bannerHost = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(246, 247, 249) };
        _banner.Text = "Még nem futott ellenőrzés";
        _banner.Font = new Font("Segoe UI Semibold", 15F);
        _banner.AutoSize = true;
        _banner.Location = new Point(18, 14);
        _bannerSub.AutoSize = true;
        _bannerSub.ForeColor = Color.FromArgb(100, 108, 122);
        _bannerSub.Location = new Point(20, 46);
        bannerHost.Controls.Add(_banner);
        bannerHost.Controls.Add(_bannerSub);

        // ---- géplista
        _hosts.View = View.Details;
        _hosts.Dock = DockStyle.Fill;
        _hosts.FullRowSelect = true;
        _hosts.MultiSelect = false;
        _hosts.HideSelection = false;
        _hosts.BorderStyle = BorderStyle.None;
        _hosts.Columns.Add("", 28);
        _hosts.Columns.Add("Gép", 190);
        _hosts.Columns.Add("Állapot", 150);
        _hosts.Columns.Add("Cím", 130);
        _hosts.Columns.Add("Ellenőrizve", 110);
        _hosts.SelectedIndexChanged += (_, _) => ShowHost();
        _hosts.DoubleClick += (_, _) => EditHost();

        var hostPanel = new Panel { Dock = DockStyle.Fill };
        hostPanel.Controls.Add(_hosts);
        hostPanel.Controls.Add(Header("Figyelt gépek"));

        // ---- ellenőrzések
        _checks.View = View.Details;
        _checks.Dock = DockStyle.Fill;
        _checks.FullRowSelect = true;
        _checks.MultiSelect = false;
        _checks.HideSelection = false;
        _checks.BorderStyle = BorderStyle.None;
        _checks.Columns.Add("", 26);
        _checks.Columns.Add("Ellenőrzés", 190);
        _checks.Columns.Add("Eredmény", 420);
        _checks.SelectedIndexChanged += (_, _) => ShowCheckDetail();

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.BorderStyle = BorderStyle.None;
        _detail.BackColor = Color.White;
        _detail.Font = new Font("Segoe UI", 9.5F);

        var detailHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 6, 10, 8) };
        detailHost.Controls.Add(_detail);

        var checkSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        checkSplit.ApplyWhenSized(260);
        checkSplit.Panel1.Controls.Add(_checks);
        checkSplit.Panel2.Controls.Add(detailHost);

        var checkPanel = new Panel { Dock = DockStyle.Fill };
        checkPanel.Controls.Add(checkSplit);
        checkPanel.Controls.Add(Header("Részletek"));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(620);
        split.Panel1.Controls.Add(hostPanel);
        split.Panel2.Controls.Add(checkPanel);

        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(_status);

        _main.Controls.Add(split);
        _main.Controls.Add(bannerHost);
        _main.Controls.Add(strip);
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

    // ================================================================ betöltés

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        MonitorConfig.Load();
        ApplyMode();

        if (MonitorConfig.Current.Hosts.Count > 0 && MonitorConfig.Current.AutoStart
            && !MonitorService.Instance.IsRunning)
        {
            MonitorService.Instance.Start();
            _btnAuto.Checked = true;
        }

        _uiTimer.Enabled = Visible;
    }

    private void ApplyMode()
    {
        var hasHosts = MonitorConfig.Current.Hosts.Count > 0;
        _setup.Visible = !hasHosts;
        _main.Visible = hasHosts;
        if (hasHosts) RefreshHosts();
    }

    private void OnCycleCompleted()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { RefreshHosts(); RefreshStatusLine(); }); } catch { }
    }

    // ================================================================ megjelenítés

    private static (string Mark, Color Color, string Text) Visual(HealthState s) => s switch
    {
        HealthState.Rendben => ("●", Color.FromArgb(20, 130, 70), "Rendben"),
        HealthState.Figyelmeztetes => ("●", Color.FromArgb(200, 130, 0), "Figyelmeztetés"),
        HealthState.Hiba => ("●", Color.FromArgb(200, 40, 40), "Hiba"),
        HealthState.Elerhetetlen => ("●", Color.FromArgb(150, 40, 160), "Nem érhető el"),
        _ => ("○", Color.FromArgb(140, 146, 158), "Nincs adat")
    };

    private void RefreshHosts()
    {
        var selectedId = (_hosts.SelectedItems.Count > 0
            ? (_hosts.SelectedItems[0].Tag as MonitoredHost)?.Id : null);

        _hosts.BeginUpdate();
        _hosts.Items.Clear();
        _hosts.Groups.Clear();

        foreach (var grp in MonitorConfig.Current.Hosts
                     .GroupBy(h => h.Group)
                     .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var lvg = SplitHelper.NewGroup(grp.Key);
            _hosts.Groups.Add(lvg);

            foreach (var h in grp.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var health = MonitorService.Instance.Get(h.Id);
                var state = h.Enabled ? (health?.Overall ?? HealthState.Ismeretlen) : HealthState.Ismeretlen;
                var (mark, color, _) = Visual(state);

                var lvi = new ListViewItem(new[]
                {
                    h.Enabled ? mark : "–",
                    h.DisplayName + (h.IsDomainController ? "  (DC)" : ""),
                    h.Enabled ? (health?.Summary ?? "Még nem ellenőrizve") : "Kikapcsolva",
                    h.Host,
                    health is null ? "–" : health.Checked.ToString("MM.dd HH:mm")
                })
                { Tag = h, ForeColor = h.Enabled ? color : Color.FromArgb(150, 156, 168), Group = lvg };

                if (state is HealthState.Hiba or HealthState.Elerhetetlen)
                    lvi.Font = new Font(_hosts.Font, FontStyle.Bold);

                _hosts.Items.Add(lvi);
            }
        }

        _hosts.EndUpdate();
        _hosts.MakeGroupsCollapsible();

        if (selectedId is not null)
            foreach (ListViewItem i in _hosts.Items)
                if ((i.Tag as MonitoredHost)?.Id == selectedId) { i.Selected = true; break; }

        if (_hosts.SelectedItems.Count == 0 && _hosts.Items.Count > 0)
            _hosts.Items[0].Selected = true;

        UpdateBanner();
    }

    private void UpdateBanner()
    {
        var cfg = MonitorConfig.Current;
        var svc = MonitorService.Instance;
        var enabled = cfg.Hosts.Count(h => h.Enabled);

        var results = cfg.Hosts.Where(h => h.Enabled)
                               .Select(h => svc.Get(h.Id))
                               .Where(r => r is not null).Select(r => r!).ToList();

        if (results.Count == 0)
        {
            _banner.Text = "Még nem futott ellenőrzés";
            _banner.ForeColor = Color.FromArgb(100, 108, 122);
            _bannerSub.Text = $"{enabled} gép figyelése beállítva.";
            return;
        }

        var bad = results.Count(r => r.Overall is HealthState.Hiba or HealthState.Elerhetetlen);
        var warn = results.Count(r => r.Overall == HealthState.Figyelmeztetes);

        if (bad > 0)
        {
            _banner.Text = $"⚠  {bad} gépen hiba van";
            _banner.ForeColor = Color.FromArgb(200, 40, 40);
        }
        else if (warn > 0)
        {
            _banner.Text = $"{warn} gép figyelmeztetést ad";
            _banner.ForeColor = Color.FromArgb(200, 130, 0);
        }
        else
        {
            _banner.Text = "Minden figyelt gép rendben";
            _banner.ForeColor = Color.FromArgb(20, 130, 70);
        }

        _bannerSub.Text = $"{results.Count}/{enabled} gép ellenőrizve" +
            (svc.LastCycle is { } t ? $"  ·  utoljára {t:HH:mm:ss}" : "") +
            $"  ·  {cfg.IntervalMinutes} percenként";
    }

    private void RefreshStatusLine()
    {
        var svc = MonitorService.Instance;
        _status.Text = svc.IsChecking
            ? "Ellenőrzés folyamatban…"
            : svc.IsRunning
                ? $"Automatikus figyelés aktív – következő kör {MonitorConfig.Current.IntervalMinutes} percenként."
                : "Az automatikus figyelés ki van kapcsolva.";
    }

    private MonitoredHost? SelectedHost =>
        _hosts.SelectedItems.Count > 0 ? _hosts.SelectedItems[0].Tag as MonitoredHost : null;

    private void ShowHost()
    {
        _checks.BeginUpdate();
        _checks.Items.Clear();
        _detail.Clear();

        var h = SelectedHost;
        var health = h is null ? null : MonitorService.Instance.Get(h.Id);

        if (h is null || health is null)
        {
            _checks.Items.Add(new ListViewItem(new[] { "", "Nincs adat",
                h is null ? "Válassz gépet." : "Még nem futott ellenőrzés ezen a gépen." }));
            _checks.EndUpdate();
            return;
        }

        foreach (var c in health.Checks)
        {
            var (mark, color, _) = Visual(c.State);
            _checks.Items.Add(new ListViewItem(new[] { mark, c.Name, c.Detail })
            { Tag = c, ForeColor = color });
        }

        _checks.EndUpdate();
        if (_checks.Items.Count > 0) _checks.Items[0].Selected = true;

        ShowHostSummary(h, health);
    }

    private void ShowHostSummary(MonitoredHost h, HostHealth health)
    {
        _detail.Clear();
        W(h.DisplayName, 13F, FontStyle.Bold, Visual(health.Overall).Color);

        var meta = new List<string> { h.Host };
        if (health.OperatingSystem.Length > 0) meta.Add(health.OperatingSystem);
        if (health.Platform.Length > 0) meta.Add(health.Platform);
        meta.Add(health.IsVirtual ? "virtuális gép" : "fizikai gép");
        if (health.Uptime.Length > 0) meta.Add("üzemidő: " + health.Uptime);

        W(string.Join("  ·  ", meta), 8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));
        W($"Ellenőrizve: {health.Checked:yyyy.MM.dd HH:mm:ss}  ({health.Duration.TotalSeconds:0.#} mp)",
            8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));

        if (h.Notes.Length > 0)
        {
            W("");
            W(h.Notes, 9F, FontStyle.Italic, Color.FromArgb(100, 108, 122));
        }
    }

    private void ShowCheckDetail()
    {
        if (_checks.SelectedItems.Count == 0) return;
        if (_checks.SelectedItems[0].Tag is not CheckResult c)
        {
            var h = SelectedHost;
            var health = h is null ? null : MonitorService.Instance.Get(h.Id);
            if (h is not null && health is not null) ShowHostSummary(h, health);
            return;
        }

        _detail.Clear();
        var (_, color, text) = Visual(c.State);

        W(c.Name, 12F, FontStyle.Bold, Color.FromArgb(28, 32, 40));
        W(text, 9.5F, FontStyle.Bold, color);
        W("");
        W(c.Detail, 9.5F, FontStyle.Regular, Color.FromArgb(50, 56, 68));

        if (c.Advice.Length > 0)
        {
            W("");
            W("Mit érdemes tenni", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            W(c.Advice, 9.5F, FontStyle.Regular, Color.FromArgb(50, 56, 68));
        }
    }

    private void W(string text, float size = 9.5F, FontStyle style = FontStyle.Regular, Color? c = null)
    {
        _detail.SelectionStart = _detail.TextLength;
        _detail.SelectionFont = new Font("Segoe UI", size, style);
        _detail.SelectionColor = c ?? Color.FromArgb(50, 56, 68);
        _detail.AppendText(text + Environment.NewLine);
    }

    // ================================================================ műveletek

    private async Task CheckNowAsync()
    {
        if (MonitorConfig.Current.Hosts.Count == 0) { AddHost(); return; }

        _status.Text = "Ellenőrzés folyamatban…";
        await MonitorService.Instance.RunNowAsync();
        RefreshHosts();
        RefreshStatusLine();
    }

    private void ToggleAuto()
    {
        var cfg = MonitorConfig.Current;
        if (_btnAuto.Checked)
        {
            MonitorService.Instance.Start();
            cfg.AutoStart = true;
        }
        else
        {
            MonitorService.Instance.Stop();
            cfg.AutoStart = false;
        }
        cfg.Save();
        RefreshStatusLine();
    }

    private void AddHost()
    {
        var host = new MonitoredHost();
        using var dlg = new HostEditForm(host, MonitorConfig.Current.Groups, isNew: true);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        MonitorConfig.Current.Hosts.Add(host);
        MonitorConfig.Current.SetupDone = true;
        MonitorConfig.Current.Save();

        ApplyMode();
        RefreshHosts();

        if (!MonitorService.Instance.IsRunning && MonitorConfig.Current.AutoStart)
        {
            MonitorService.Instance.Start();
            _btnAuto.Checked = true;
        }

        _ = CheckNowAsync();
    }

    private void EditHost()
    {
        var h = SelectedHost;
        if (h is null) return;

        using var dlg = new HostEditForm(h, MonitorConfig.Current.Groups, isNew: false);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        MonitorConfig.Current.Save();
        RefreshHosts();
    }

    private void DeleteHost()
    {
        var h = SelectedHost;
        if (h is null) return;

        if (MessageBox.Show($"Eltávolítod a felügyeletből: „{h.DisplayName}”?",
                "Eltávolítás", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        MonitorConfig.Current.Hosts.Remove(h);
        MonitorConfig.Current.Save();
        MonitorService.Instance.Forget(h.Id);

        ApplyMode();
        RefreshHosts();
    }

    private void EditSettings()
    {
        using var dlg = new MonitorSettingsForm();
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        if (MonitorService.Instance.IsRunning) MonitorService.Instance.Start();   // új időköz
        RefreshHosts();
        RefreshStatusLine();
    }

    private void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Állapotjelentés mentése",
            Filter = "Szövegfájl|*.txt",
            FileName = $"felugyelet_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            InitialDirectory = AppPaths.ExportDir
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("DevToolbox – felügyeleti állapotjelentés");
        sb.AppendLine($"Készült: {DateTime.Now:yyyy.MM.dd HH:mm}");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine();

        foreach (var grp in MonitorConfig.Current.Hosts.GroupBy(h => h.Group).OrderBy(g => g.Key))
        {
            sb.AppendLine($"[{grp.Key}]");
            foreach (var h in grp)
            {
                var health = MonitorService.Instance.Get(h.Id);
                sb.AppendLine($"  {h.DisplayName}  ({h.Host})");
                if (health is null) { sb.AppendLine("    nincs adat"); continue; }

                sb.AppendLine($"    állapot: {health.Overall}   ellenőrizve: {health.Checked:yyyy.MM.dd HH:mm}");
                if (health.OperatingSystem.Length > 0)
                    sb.AppendLine($"    {health.OperatingSystem} · {(health.IsVirtual ? "virtuális" : "fizikai")}");

                foreach (var c in health.Checks)
                {
                    sb.AppendLine($"      [{c.State}] {c.Name}: {c.Detail}");
                    if (c.Advice.Length > 0) sb.AppendLine($"           → {c.Advice}");
                }
                sb.AppendLine();
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
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
        if (disposing)
        {
            _uiTimer.Stop();
            _uiTimer.Dispose();
            MonitorService.Instance.CycleCompleted -= OnCycleCompleted;
        }
        base.Dispose(disposing);
    }
}
