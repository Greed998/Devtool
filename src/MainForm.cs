using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class MainForm : Form
{
    private readonly TabControl _tabs = new();
    private readonly ToolStripStatusLabel _lblMode = new();
    private readonly ToolStripStatusLabel _lblData = new();
    private readonly ToolStripStatusLabel _lblAdmin = new();
    private readonly ToolStripStatusLabel _lblMonitor = new();

    private readonly NotifyIcon _tray = new();
    private bool _exiting;

    /// <summary>Lusta fulbetoltes: a lap csak az elso megnyitaskor jon letre.</summary>
    private readonly Dictionary<TabPage, Func<Control>> _factories = new();
    private readonly Dictionary<TabPage, Control> _pages = new();

    public MainForm()
    {
        Text = $"{AppPaths.DisplayName} {AppPaths.Version} – {AppPaths.Tagline}   ·   {AppPaths.ModeLabel}" +
               (HardwareInfo.IsElevated() ? "   ·   rendszergazda" : "");
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 740);
        MinimumSize = new Size(960, 600);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildMenu();
        BuildTabs();
        BuildStatus();
        BuildTray();

        MonitorConfig.Load();
        MonitorService.Instance.StatusChanged += OnMonitorStatusChanged;
        MonitorService.Instance.CycleCompleted += OnMonitorCycle;
    }

    // ================================================================ menü

    private void BuildMenu()
    {
        var menu = new MenuStrip { RenderMode = ToolStripRenderMode.System };

        var mFile = new ToolStripMenuItem("&Program");
        var miServices = new ToolStripMenuItem("Windows szolgáltatások…", null, (_, _) => ShowServices());
        var miAppUpdate = new ToolStripMenuItem("Alkalmazás frissítő…", null, (_, _) => ShowAppUpdater());
        var miRaid = new ToolStripMenuItem("RAID kalkulátor…", null, (_, _) => ShowRaid());
        var miInstall = new ToolStripMenuItem("Telepítés erre a gépre…", null, (_, _) => InstallNow())
        { Enabled = AppPaths.Mode == AppMode.Portable };
        var miUninstall = new ToolStripMenuItem("Eltávolítás…", null, (_, _) => UninstallNow())
        { Enabled = AppPaths.Mode == AppMode.Installed };
        var miAdmin = new ToolStripMenuItem("Újraindítás rendszergazdaként", null, (_, _) => RestartElevated())
        { Enabled = !HardwareInfo.IsElevated() };
        var miDataFolder = new ToolStripMenuItem("Adatmappa megnyitása", null, (_, _) => OpenFolder(AppPaths.DataDir));
        var miHide = new ToolStripMenuItem("Elrejtés a tálcára", null, (_, _) => HideToTray());
        var miExit = new ToolStripMenuItem("Kilépés", null, (_, _) => ExitApp());

        mFile.DropDownItems.AddRange(new ToolStripItem[]
        {
            miAppUpdate, miServices, miRaid, new ToolStripSeparator(),
            miInstall, miUninstall, new ToolStripSeparator(),
            miAdmin, miDataFolder, new ToolStripSeparator(),
            miHide, miExit
        });

        var mSettings = new ToolStripMenuItem("&Beállítások");
        mSettings.DropDownItems.Add(new ToolStripMenuItem("Beállítások…", null, (_, _) => ShowSettings()));
        mSettings.DropDownItems.Add(new ToolStripSeparator());
        mSettings.DropDownItems.Add(new ToolStripMenuItem("Szkriptek könyvtára", null,
            (_, _) => OpenFolder(AppPaths.ScriptsDir)));
        mSettings.DropDownItems.Add(new ToolStripMenuItem("Adatmappa", null,
            (_, _) => OpenFolder(AppPaths.DataDir)));

        var mExport = new ToolStripMenuItem("&Exportálás");
        mExport.DropDownItems.Add(new ToolStripMenuItem("Adatok exportálása…", null, (_, _) => ShowExport())
        { ShortcutKeys = Keys.Control | Keys.E });
        mExport.DropDownItems.Add(new ToolStripSeparator());
        mExport.DropDownItems.Add(new ToolStripMenuItem("Export mappa megnyitása", null,
            (_, _) => OpenFolder(AppPaths.ExportDir)));

        var mHelp = new ToolStripMenuItem("&Súgó");
        mHelp.DropDownItems.Add(new ToolStripMenuItem("Névjegy", null, (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { mFile, mSettings, mExport, mHelp });
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    // ================================================================ fülek

    private void BuildTabs()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(14, 6);

        // A vezerlopult azonnal letrejon - ez a nyito lap.
        var dashboard = new DashboardPage();
        var tDash = NewTab("  Vezérlőpult  ");
        tDash.Controls.Add(dashboard);
        _pages[tDash] = dashboard;

        // A tobbi lap csak az elso megnyitaskor epul fel: igy indulaskor nem olvasunk
        // szkriptkonyvtarat, nem nyitunk szenzort es nem kerdezunk WMI-t feleslegesen.
        Lazy("  Szkriptek  ", () => new ScriptsPage());
        Lazy("  Hardver  ", () => new HardwarePage());
        Lazy("  Tárhely  ", () => new StoragePage());
        Lazy("  Hálózat  ", () => new NetworkPage());
        Lazy("  Eseménynapló  ", () => new EventLogPage());
        Lazy("  Licencek  ", () => new LicensePage());
        Lazy("  Távoli gép  ", () => new RemotePage());
        Lazy("  Flotta  ", () => new FleetPage());

        _tabs.SelectedIndexChanged += (_, _) => ActivateTab();

        Controls.Add(_tabs);
        _tabs.BringToFront();

        dashboard.EnsureLoaded();
    }

    private TabPage NewTab(string title)
    {
        var t = new TabPage(title) { BackColor = Color.White };
        _tabs.TabPages.Add(t);
        return t;
    }

    private void Lazy(string title, Func<Control> factory) => _factories[NewTab(title)] = factory;

    private void ActivateTab()
    {
        var tab = _tabs.SelectedTab;
        if (tab is null) return;

        if (!_pages.TryGetValue(tab, out var page))
        {
            if (!_factories.TryGetValue(tab, out var factory)) return;

            Cursor = Cursors.WaitCursor;
            try
            {
                page = factory();
                tab.Controls.Add(page);
                _pages[tab] = page;
            }
            finally { Cursor = Cursors.Default; }
        }

        switch (page)
        {
            case DashboardPage d: d.EnsureLoaded(); break;
            case HardwarePage h: h.EnsureLoaded(); break;
            case StoragePage s: s.EnsureLoaded(); break;
            case NetworkPage n: n.EnsureLoaded(); break;
            case EventLogPage e: e.EnsureLoaded(); break;
            case LicensePage l: l.EnsureLoaded(); break;
            case FleetPage f: f.EnsureLoaded(); break;
        }
    }

    // ================================================================ állapotsor

    private void BuildStatus()
    {
        var strip = new StatusStrip { SizingGrip = true };

        _lblMode.Text = AppPaths.ModeLabel;
        _lblMode.ForeColor = AppPaths.Mode == AppMode.Portable
            ? Color.FromArgb(16, 110, 60) : Color.FromArgb(0, 90, 165);
        _lblMode.Font = new Font("Segoe UI Semibold", 9F);

        _lblMonitor.Text = "Felügyelet: –";

        _lblData.Text = "Adatok: " + AppPaths.DataDir;
        _lblData.Spring = true;
        _lblData.TextAlign = ContentAlignment.MiddleLeft;

        _lblAdmin.Text = HardwareInfo.IsElevated() ? "Rendszergazda" : "Normál jog";
        _lblAdmin.ForeColor = HardwareInfo.IsElevated()
            ? Color.FromArgb(150, 80, 0) : Color.FromArgb(110, 116, 128);

        strip.Items.AddRange(new ToolStripItem[]
        {
            _lblMode, new ToolStripSeparator(), _lblMonitor, new ToolStripSeparator(),
            _lblData, _lblAdmin
        });

        Controls.Add(strip);
    }

    // ================================================================ tálca

    private void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = AppPaths.DisplayName;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Megnyitás", null, (_, _) => ShowFromTray())
        { Font = new Font("Segoe UI Semibold", 9F) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Ellenőrzés most", null,
            async (_, _) => await MonitorService.Instance.RunNowAsync()));
        menu.Items.Add(new ToolStripMenuItem("Figyelés szüneteltetése", null, (_, _) => ToggleMonitor())
        { Name = "pause" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Kilépés", null, (_, _) => ExitApp()));

        menu.Opening += (_, _) =>
        {
            if (menu.Items["pause"] is ToolStripMenuItem p)
                p.Text = MonitorService.Instance.IsRunning
                    ? "Figyelés szüneteltetése" : "Figyelés indítása";
        };

        _tray.ContextMenuStrip = menu;
    }

    private void ToggleMonitor()
    {
        if (MonitorService.Instance.IsRunning) MonitorService.Instance.Stop();
        else MonitorService.Instance.Start();
        UpdateMonitorLabel();
    }

    private void HideToTray()
    {
        Hide();
        TrimWorkingSet();

        _tray.ShowBalloonTip(4000, AppPaths.DisplayName,
            "A program a tálcán fut tovább, és figyeli a beállított gépeket.\n" +
            "Megnyitás: dupla kattintás az ikonon.", ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Bezaraskor a talcara kerulunk, hogy a figyeles ne alljon le.
        if (!_exiting &&
            e.CloseReason == CloseReason.UserClosing &&
            MonitorConfig.Current.MinimizeToTray &&
            MonitorConfig.Current.Hosts.Count > 0)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _tray.Visible = false;
        MonitorService.Instance.Stop();
        base.OnFormClosing(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        MonitorService.Instance.Stop();
        Close();
        Application.Exit();
    }

    // ================================================================ figyelés

    private void OnMonitorStatusChanged(MonitoredHost host, HealthState from, HostHealth health)
    {
        if (_exiting) return;

        var problem = health.Overall is HealthState.Hiba or HealthState.Elerhetetlen;

        var title = problem
            ? $"Hiba: {host.DisplayName}"
            : $"Helyreállt: {host.DisplayName}";

        var body = problem
            ? BuildProblemText(health)
            : "A gép ismét minden ellenőrzésen átment.";

        try
        {
            _tray.ShowBalloonTip(problem ? 15000 : 6000, title, body,
                problem ? ToolTipIcon.Error : ToolTipIcon.Info);
        }
        catch { }
    }

    private static string BuildProblemText(HostHealth h)
    {
        var bad = h.Checks
            .Where(c => c.State is HealthState.Hiba or HealthState.Elerhetetlen)
            .Take(3)
            .Select(c => $"• {c.Name}: {c.Detail}")
            .ToList();

        return bad.Count == 0 ? h.Summary : string.Join("\n", bad);
    }

    private void OnMonitorCycle()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(UpdateMonitorLabel); } catch { }
    }

    private void UpdateMonitorLabel()
    {
        var svc = MonitorService.Instance;
        var hosts = MonitorConfig.Current.Hosts.Count(h => h.Enabled);

        if (hosts == 0) { _lblMonitor.Text = "Felügyelet: nincs beállítva"; _lblMonitor.ForeColor = SystemColors.ControlText; return; }
        if (!svc.IsRunning) { _lblMonitor.Text = "Felügyelet: szünetel"; _lblMonitor.ForeColor = Color.FromArgb(150, 90, 20); return; }

        var (text, color) = svc.Overall switch
        {
            HealthState.Rendben => ($"Felügyelet: {hosts} gép rendben", Color.FromArgb(20, 130, 70)),
            HealthState.Figyelmeztetes => ("Felügyelet: figyelmeztetés", Color.FromArgb(200, 130, 0)),
            HealthState.Hiba => ("Felügyelet: HIBA", Color.FromArgb(200, 40, 40)),
            HealthState.Elerhetetlen => ("Felügyelet: gép nem érhető el", Color.FromArgb(150, 40, 160)),
            _ => ($"Felügyelet: {hosts} gép", SystemColors.ControlText)
        };

        _lblMonitor.Text = text;
        _lblMonitor.ForeColor = color;

        _tray.Text = (AppPaths.DisplayName + " – " + text).Length > 62
            ? AppPaths.DisplayName
            : AppPaths.DisplayName + " – " + text;
    }

    // ================================================================ erőforrás

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    /// <summary>
    /// Talcara teveskor visszaadjuk a munkakeszletet az operacios rendszernek.
    /// Ez nem csokkenti a valodi memoriaigenyt - a Windows visszalapozza, amikor
    /// megint kell -, de a hosszan a hatterben ulo folyamat igy nem tart bent
    /// feleslegesen tobb szaz megabajtot.
    /// </summary>
    private static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch { }
    }

    // ================================================================ műveletek

    private void ShowServices() { using var f = new ServicesForm(); f.ShowDialog(this); }
    private void ShowAppUpdater() { using var f = new AppUpdaterForm(); f.ShowDialog(this); }

    private void ShowRaid()
    {
        using var f = new Form
        {
            Text = "RAID kalkulátor – " + AppPaths.DisplayName,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(1180, 720),
            MinimumSize = new Size(960, 600),
            BackColor = Color.White,
            Font = new Font("Segoe UI", 9F),
            Icon = Icon
        };
        f.Controls.Add(new RaidPage());
        f.ShowDialog(this);
    }

    private void ShowExport() { using var f = new ExportForm(); f.ShowDialog(this); }
    private void ShowAbout() { using var f = new AboutForm(); f.ShowDialog(this); }

    private void ShowSettings()
    {
        using var f = new SettingsForm();
        if (f.ShowDialog(this) != DialogResult.OK) return;

        _lblData.Text = "Adatok: " + AppPaths.DataDir;
        MessageBox.Show(
            "A beállítások elmentve.\n\n" +
            "Néhány beállítás (indítási jogosultság, szenzor-időköz) a program " +
            "következő indításakor lép életbe.",
            "Beállítások", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void InstallNow()
    {
        var r = MessageBox.Show(
            "A program bemásolja magát ide:\n" + AppPaths.InstallDir +
            "\n\nParancsikon készül a Start menübe és az asztalra, és megjelenik a Windows\n" +
            "programlistájában is. Rendszergazdai jog nem szükséges.\n\nFolytatod?",
            "Telepítés", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        try
        {
            var exe = Installer.Install(desktopShortcut: true, startMenuShortcut: true);
            MessageBox.Show("Kész. A telepített példány most elindul.", "Telepítés",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            ExitApp();
        }
        catch (Exception ex)
        {
            MessageBox.Show("A telepítés nem sikerült:\n" + ex.Message, "Hiba",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UninstallNow()
    {
        var r = MessageBox.Show("Eltávolítod a programot erről a gépről?", "Eltávolítás",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return;

        var removeData = MessageBox.Show(
            "Töröljem a szkripteket és a beállításokat is?\n\nNem = az adatok megmaradnak.",
            "Adatok", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        Installer.Uninstall(removeData);
        MessageBox.Show("Az eltávolítás elindult. A program most bezárul.", "Eltávolítás",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        ExitApp();
    }

    private void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            ExitApp();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Nem sikerült emelt jogon indítani:\n" + ex.Message,
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            MonitorService.Instance.StatusChanged -= OnMonitorStatusChanged;
            MonitorService.Instance.CycleCompleted -= OnMonitorCycle;
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
