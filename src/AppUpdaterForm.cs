using System.Runtime.Versioning;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;

namespace DevToolbox;

/// <summary>
/// Alkalmazások frissítése wingettel. Helyben a DevToolbox futtatja a wingetet; távoli gépen a gép AGENTJE
/// frissít: a feladat a Fleet Server postaládáján át megy ki, és az agent a következő jelentkezéskor
/// rendszergazdai jogon elvégzi. (A korábbi PowerShell Remoting-os út megszűnt - a felhasználó döntése.)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppUpdaterForm : Form
{
    private readonly RadioButton _rbLocal = new();
    private readonly RadioButton _rbRemote = new();
    private readonly ComboBox _remoteAgent = new();
    private readonly ListView _list = new();
    private readonly RichTextBox _output = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly Button _btnRefresh, _btnUpdate, _btnUpdateAll, _btnCancel;
    private readonly CheckBox _showAll = new();
    private readonly Label _wingetWarn = new();

    private List<WingetPackage> _packages = new();
    private CancellationTokenSource? _cts;

    public AppUpdaterForm()
    {
        Text = "Alkalmazás frissítő – " + AppPaths.DisplayName;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 680);
        MinimumSize = new Size(880, 560);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);
        Icon = (Application.OpenForms.Count > 0 ? Application.OpenForms[0]?.Icon : null);

        // ---------------- felső sáv: hol frissítünk
        var top = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(16, 12, 16, 8) };

        _rbLocal.Text = "Ezen a gépen";
        _rbLocal.Checked = true;
        _rbLocal.AutoSize = true;
        _rbLocal.Location = new Point(16, 12);
        // Csak a bekapcsolódó gomb váltson (különben váltáskor kétszer töltene).
        _rbLocal.CheckedChanged += (_, _) => { if (_rbLocal.Checked) ApplyTarget(); };

        _rbRemote.Text = "Távoli gépen (agenttel):";
        _rbRemote.AutoSize = true;
        _rbRemote.Location = new Point(150, 12);
        _rbRemote.CheckedChanged += (_, _) => { if (_rbRemote.Checked) ApplyTarget(); };

        _remoteAgent.Location = new Point(320, 9);
        _remoteAgent.Width = 340;
        _remoteAgent.DropDownStyle = ComboBoxStyle.DropDownList;
        _remoteAgent.Enabled = false;
        _remoteAgent.SelectedIndexChanged += async (_, _) => { if (_rbRemote.Checked) await LoadAsync(); };

        _wingetWarn.Location = new Point(16, 44);
        _wingetWarn.AutoSize = true;
        _wingetWarn.MaximumSize = new Size(1000, 0);
        _wingetWarn.Font = new Font("Segoe UI", 8.5F);

        top.Controls.Add(_rbLocal);
        top.Controls.Add(_rbRemote);
        top.Controls.Add(_remoteAgent);
        top.Controls.Add(_wingetWarn);

        // ---------------- eszköztár
        var tools = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(6, 4, 6, 4)
        };

        _btnRefresh = MakeBtn("Alkalmazások lekérése", primary: false);
        _btnRefresh.Click += async (_, _) => await LoadAsync();

        _btnUpdate = MakeBtn("Kijelölt frissítése", primary: false);
        _btnUpdate.Click += async (_, _) => await UpdateSelectedAsync();
        _btnUpdate.Enabled = false;

        _btnUpdateAll = MakeBtn("Összes frissítése", primary: true);
        _btnUpdateAll.Click += async (_, _) => await UpdateAllAsync();
        _btnUpdateAll.Enabled = false;

        _btnCancel = MakeBtn("Megszakítás", primary: false);
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        _btnCancel.Enabled = false;

        var refreshHost = new ToolStripControlHost(_btnRefresh);
        var updHost = new ToolStripControlHost(_btnUpdate);
        var allHost = new ToolStripControlHost(_btnUpdateAll);
        var cancelHost = new ToolStripControlHost(_btnCancel);

        _showAll.Text = "Minden telepített alkalmazás mutatása (nem csak a frissíthetők)";
        _showAll.AutoSize = true;
        // Távoli gépnél a teljes lista az agent jelentéséből épül újra.
        _showAll.CheckedChanged += async (_, _) => { if (_rbRemote.Checked) await LoadAsync(); else Populate(); };
        var showHost = new ToolStripControlHost(_showAll) { Alignment = ToolStripItemAlignment.Right };

        tools.Items.AddRange(new ToolStripItem[]
        {
            refreshHost, new ToolStripSeparator(), updHost, allHost, new ToolStripSeparator(),
            cancelHost, showHost
        });

        // ---------------- lista
        _list.View = View.Details;
        _list.Dock = DockStyle.Fill;
        _list.FullRowSelect = true;
        _list.CheckBoxes = true;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Alkalmazás", 280);
        _list.Columns.Add("Telepített", 150);
        _list.Columns.Add("Elérhető", 150);
        _list.Columns.Add("Azonosító", 250);
        _list.ItemChecked += (_, _) => UpdateButtons();

        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_list);
        listPanel.Controls.Add(Header("Alkalmazások"));

        // ---------------- kimenet
        _output.Dock = DockStyle.Fill;
        _output.ReadOnly = true;
        _output.BorderStyle = BorderStyle.None;
        _output.BackColor = Color.FromArgb(24, 24, 27);
        _output.ForeColor = Color.FromArgb(210, 214, 220);
        _output.Font = new Font("Consolas", 9F);
        _output.WordWrap = false;

        var outHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(38, 38, 42) };
        outHeaderPanel.Controls.Add(new Label
        {
            Text = "  Napló",
            Dock = DockStyle.Left,
            Width = 120,
            ForeColor = Color.FromArgb(190, 195, 205),
            TextAlign = ContentAlignment.MiddleLeft
        });
        var btnClear = new Button
        {
            Text = "Törlés", Dock = DockStyle.Right, Width = 70,
            FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(190, 195, 205),
            BackColor = Color.FromArgb(38, 38, 42)
        };
        btnClear.FlatAppearance.BorderSize = 0;
        btnClear.Click += (_, _) => _output.Clear();
        outHeaderPanel.Controls.Add(btnClear);

        var outPanel = new Panel { Dock = DockStyle.Fill };
        outPanel.Controls.Add(_output);
        outPanel.Controls.Add(outHeaderPanel);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(340);
        split.Panel1.Controls.Add(listPanel);
        split.Panel2.Controls.Add(outPanel);

        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(_status);

        Controls.Add(split);
        Controls.Add(tools);
        Controls.Add(top);
        Controls.Add(strip);

        ApplyTarget();
    }

    private static Button MakeBtn(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(0, 120, 212) : Color.White,
            ForeColor = primary ? Color.White : Color.FromArgb(40, 46, 58),
            Font = primary ? new Font("Segoe UI Semibold", 9F) : new Font("Segoe UI", 9F),
            Margin = new Padding(2)
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
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

    // ================================================================ cél

    private void ApplyTarget()
    {
        var remote = _rbRemote.Checked;
        _remoteAgent.Enabled = remote;
        _packages = new();
        Populate();

        if (!remote && !Winget.IsAvailable)
        {
            _wingetWarn.Text = "⚠  A winget nem található ezen a gépen. Telepítsd a Microsoft " +
                               "App Installer alkalmazást a Microsoft Store-ból, vagy futtasd a " +
                               "beépített „Winget telepítők” szkriptet.";
            _wingetWarn.ForeColor = Color.FromArgb(185, 100, 0);
        }
        else if (remote)
        {
            var err = FillAgents();
            _wingetWarn.Text = err is not null
                ? "⚠  " + err + "\nTávolról csak agenttel felügyelt gép frissíthető: a Fleet Server gépén futó DevToolboxból, " +
                  "vagy a Fleet Server / Központ webfelületéről."
                : "A frissítést a gép agentje végzi, rendszergazdai jogon, a felhasználó közreműködése nélkül: a feladat a Fleet " +
                  "Serveren át megy ki, és a gép a következő jelentkezéskor (legfeljebb 5 perc) elvégzi. A lista az agent legutóbbi jelentése.";
            _wingetWarn.ForeColor = err is not null ? Color.FromArgb(185, 100, 0) : Color.FromArgb(110, 117, 130);
        }
        else
        {
            _wingetWarn.Text = "A winget a bejelentkezett felhasználó jogaival frissít. " +
                               "Gépszintű csomagokhoz rendszergazdai jog kell.";
            _wingetWarn.ForeColor = Color.FromArgb(110, 117, 130);
        }
    }

    // ================================================================ távoli gép: agent a Fleet Serveren át

    /// <summary>A Flotta fül adatmappája (helyi Fleet Server). Csak olvas; a feladat a postaládába megy.</summary>
    private static FleetStore? OpenFleet(out string? error)
    {
        error = null;
        var dir = FleetPage.StoreDirectory;
        if (!File.Exists(Path.Combine(dir, "server-public.pem")))
        {
            error = Directory.Exists(dir)
                ? "A Fleet adatmappa nem olvasható – indítsd a DevToolboxot rendszergazdaként."
                : $"Nincs Fleet Server adatmappa ({dir}) – a Flotta fülön adható meg.";
            return null;
        }
        try { return new FleetStore(dir, owner: false); }
        catch (UnauthorizedAccessException) { error = "A Fleet adatmappához nincs hozzáférés – indítsd a DevToolboxot rendszergazdaként."; }
        catch (Exception ex) { error = "A Fleet adatmappa nem olvasható: " + ex.Message; }
        return null;
    }

    private sealed record AgentItem(string AgentId, string Text) { public override string ToString() => Text; }

    /// <summary>A gépválasztó feltöltése a Fleet Server agentjeivel. Hibaüzenetet ad vissza, ha nincs elérhető flotta.</summary>
    private string? FillAgents()
    {
        var keep = (_remoteAgent.SelectedItem as AgentItem)?.AgentId;
        _remoteAgent.Items.Clear();
        var store = OpenFleet(out var err);
        if (store is null) return err;
        foreach (var a in store.Agents.Where(a => !a.Revoked).OrderBy(a => a.MachineName, StringComparer.CurrentCultureIgnoreCase))
            _remoteAgent.Items.Add(new AgentItem(a.AgentId, a.MachineName +
                (a.IsOnline ? "" : "  (offline)") + ((a.LastStatus?.UpdatesAvailable ?? 0) > 0 ? $"  – {a.LastStatus!.UpdatesAvailable} frissítés" : "")));
        if (_remoteAgent.Items.Count == 0) return "A Fleet Serveren még nincs agent.";
        var idx = _remoteAgent.Items.Cast<AgentItem>().ToList().FindIndex(i => i.AgentId == keep);
        _remoteAgent.SelectedIndex = Math.Max(0, idx);
        return null;
    }

    /// <summary>A kiválasztott gép friss állapota (a fleet.json-t a szerver közben írja, ezért mindig újraolvassuk).</summary>
    private (FleetStore? Store, EnrolledAgent? Agent, string? Error) RemoteAgent()
    {
        var store = OpenFleet(out var err);
        if (store is null) return (null, null, err);
        var id = (_remoteAgent.SelectedItem as AgentItem)?.AgentId;
        var a = id is null ? null : store.GetAgent(id);
        return a is null || a.Revoked ? (store, null, "Válassz gépet a listából.") : (store, a, null);
    }

    /// <summary>Az agent jelentéséből: a frissíthetők, „minden” módban a telepített programok is.</summary>
    private List<WingetPackage> AgentPackages(EnrolledAgent a)
    {
        var st = a.LastStatus;
        if (st is null) return new();
        var list = st.Updates.Select(u => new WingetPackage { Id = u.Id, Name = u.Name, InstalledVersion = u.Installed, AvailableVersion = u.Available }).ToList();
        if (_showAll.Checked)
        {
            var have = list.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.AddRange(st.Installed.Where(i => !have.Contains(i.Id))
                .Select(i => new WingetPackage { Id = i.Id, Name = i.Name, InstalledVersion = i.Version, Source = i.Source }));
        }
        return list;
    }

    /// <summary>Feladat(ok) a gép agentjének: kijelöltek (UpgradeApp) vagy minden (UpgradeAll).</summary>
    private void QueueRemote(List<WingetPackage> packages, bool all)
    {
        var (store, a, err) = RemoteAgent();
        if (store is null || a is null)
        {
            MessageBox.Show(err, "Távoli frissítés", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Az azonosító az agent parancssorába kerül - az agent is ellenőrzi, de rossz feladat ki se menjen.
        var ids = packages.Select(p => p.Id).Where(WingetId.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            if (all) store.QueueTask(a.AgentId, new AgentTask { Kind = AgentTaskKind.UpgradeAll });
            else foreach (var id in ids) store.QueueTask(a.AgentId, new AgentTask { Kind = AgentTaskKind.UpgradeApp, Target = id });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log("HIBA: a feladat nem tehető a Fleet Server postaládájába: " + ex.Message, error: true);
            MessageBox.Show("A feladat nem tehető be (nincs írási jog a Fleet adatmappához). Indítsd a DevToolboxot rendszergazdaként.",
                "Távoli frissítés", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Log("");
        Log(all ? $"▶ {a.MachineName}: összes frissítése – feladat betéve."
                : $"▶ {a.MachineName}: {ids.Count} alkalmazás frissítése – feladat betéve.", color: Color.FromArgb(120, 190, 255));
        foreach (var p in packages.Where(p => !all && WingetId.IsValid(p.Id))) Log($"   {p.Name}  ({p.InstalledVersion} → {p.AvailableVersion})");
        Log(a.IsOnline
            ? "   Az agent a következő jelentkezéskor (legfeljebb 5 perc) elvégzi; a lista a frissítés utáni jelentéssel változik."
            : "   A gép most offline – a feladat megvárja, és a gép következő jelentkezésekor fut le.");
        _status.Text = $"Feladat betéve: {a.MachineName}.";
    }

    // ================================================================ betöltés

    private async Task LoadAsync()
    {
        if (_cts is not null) return;

        if (_rbRemote.Checked)
        {
            // Nincs hálózati lekérdezés: az agent legutóbbi jelentése a Fleet Server adatmappájában van.
            var (_, a, err) = RemoteAgent();
            if (a is null)
            {
                if (_remoteAgent.Items.Count > 0) MessageBox.Show(err, "Távoli frissítés", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _packages = AgentPackages(a);
            Populate();
            var upd = _packages.Count(p => p.HasUpdate);
            var seen = a.LastSeenUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm") ?? "még nem jelentkezett";
            _status.Text = $"{a.MachineName}: {upd} frissíthető alkalmazás (az agent jelentése, {seen}).";
            Log($"{a.MachineName}: az agent jelentése ({seen}{(a.IsOnline ? "" : ", a gép most offline")}) – {upd} frissíthető.");
            if (a.LastStatus is null) Log("   Erről a gépről még nincs programlista – az agent első jelentkezése után jelenik meg.");
            return;
        }
        else if (!Winget.IsAvailable)
        {
            MessageBox.Show(
                "A winget nem érhető el ezen a gépen.\n\n" +
                "Telepítsd a Microsoft App Installer alkalmazást a Microsoft Store-ból, " +
                "vagy a Szkriptek fülön futtasd a „Winget – App Installer helyreállítás” szkriptet.",
                "Nincs winget", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        BeginBusy("Alkalmazások lekérése…");

        try
        {
            var token = _cts.Token;
            _packages = _showAll.Checked
                ? await Winget.GetInstalledAsync(token)
                : await Winget.GetUpgradableAsync(token);

            Populate();
            var updatable = _packages.Count(p => p.HasUpdate);
            _status.Text = $"{_packages.Count} alkalmazás, ebből {updatable} frissíthető.";
            Log($"Kész: {_packages.Count} alkalmazás, {updatable} frissíthető.");
        }
        catch (OperationCanceledException) { _status.Text = "Megszakítva."; }
        catch (Exception ex) { Log("HIBA: " + ex.Message, error: true); }
        finally { EndBusy(); }
    }

    private void Populate()
    {
        var showAll = _showAll.Checked;
        var items = _packages.Where(p => showAll || p.HasUpdate)
                             .OrderByDescending(p => p.HasUpdate)
                             .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                             .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var p in items)
        {
            var lvi = new ListViewItem(new[]
            {
                p.Name,
                p.InstalledVersion,
                p.HasUpdate ? p.AvailableVersion : "—",
                p.Id
            })
            { Tag = p, Checked = p.HasUpdate };

            if (!p.HasUpdate) lvi.ForeColor = Color.FromArgb(130, 136, 148);
            _list.Items.Add(lvi);
        }

        _list.EndUpdate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var checkedCount = _list.CheckedItems.Count;
        var anyUpdate = _packages.Any(p => p.HasUpdate);
        var busy = _cts is not null;

        _btnUpdate.Enabled = !busy && checkedCount > 0;
        _btnUpdateAll.Enabled = !busy && anyUpdate;
    }

    // ================================================================ frissítés

    private async Task UpdateSelectedAsync()
    {
        var ids = _list.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as WingetPackage)
            .Where(p => p is not null && p.HasUpdate)
            .Cast<WingetPackage>()
            .ToList();

        if (ids.Count == 0)
        {
            MessageBox.Show("Jelölj ki legalább egy frissíthető alkalmazást.", "Nincs kijelölés",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_rbRemote.Checked) { QueueRemote(ids, all: false); return; }
        await RunUpdatesAsync(ids);
    }

    private async Task UpdateAllAsync()
    {
        var ids = _packages.Where(p => p.HasUpdate).ToList();
        if (ids.Count == 0) return;

        var remote = _rbRemote.Checked;
        if (MessageBox.Show(remote
                ? $"A gép agentje mind a(z) {ids.Count} frissíthető alkalmazást frissíti a következő jelentkezéskor, rendszergazdai jogon.\n\nFolytatod?"
                : $"Mind a(z) {ids.Count} frissíthető alkalmazás frissítése elindul.\n\nFolytatod?",
                "Összes frissítése", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (remote) { QueueRemote(ids, all: true); return; }
        await RunUpdatesAsync(ids);
    }

    /// <summary>Helyi frissítés a wingettel, a kimenet folyamatosan a naplóba.</summary>
    private async Task RunUpdatesAsync(List<WingetPackage> packages)
    {
        _cts = new CancellationTokenSource();
        BeginBusy("Frissítés folyamatban…");

        var token = _cts.Token;

        int ok = 0, fail = 0;

        try
        {
            foreach (var pkg in packages)
            {
                token.ThrowIfCancellationRequested();
                Log("");
                Log($"▶ {pkg.Name}  ({pkg.InstalledVersion} → {pkg.AvailableVersion})",
                    color: Color.FromArgb(120, 190, 255));

                void OnLine(string l)
                {
                    if (IsHandleCreated) BeginInvoke(() => Log("   " + l));
                }

                var result = await Winget.UpgradeAsync(pkg.Id, OnLine, token);

                if (result.Ok)
                {
                    Log($"✔ {pkg.Name} kész.", color: Color.FromArgb(120, 220, 140));
                    ok++;
                }
                else
                {
                    Log($"✘ {pkg.Name} — hibakód: {result.ExitCode}", error: true);
                    fail++;
                }
            }

            _status.Text = $"Kész: {ok} sikeres, {fail} sikertelen.";
            Log("");
            Log($"Összesítés: {ok} sikeres, {fail} sikertelen.",
                color: Color.FromArgb(120, 190, 255));
        }
        catch (OperationCanceledException)
        {
            Log("[megszakítva]", error: true);
            _status.Text = "Megszakítva.";
        }
        finally
        {
            EndBusy();
            await LoadAsync();   // frissül a lista, hogy eltűnjenek a már frissítettek
        }
    }

    // ================================================================ segédek

    private void BeginBusy(string text)
    {
        _status.Text = text;
        _btnRefresh.Enabled = false;
        _btnUpdate.Enabled = false;
        _btnUpdateAll.Enabled = false;
        _btnCancel.Enabled = true;
        _rbLocal.Enabled = _rbRemote.Enabled = _remoteAgent.Enabled = false;
    }

    private void EndBusy()
    {
        _btnRefresh.Enabled = true;
        _btnCancel.Enabled = false;
        _rbLocal.Enabled = _rbRemote.Enabled = true;
        _remoteAgent.Enabled = _rbRemote.Checked;
        _cts?.Dispose();
        _cts = null;
        UpdateButtons();
    }

    private void Log(string text, bool error = false, Color? color = null)
    {
        if (_output.IsDisposed) return;
        _output.SelectionStart = _output.TextLength;
        _output.SelectionColor = color ?? (error ? Color.FromArgb(255, 120, 120) : Color.FromArgb(210, 214, 220));
        _output.AppendText(text + Environment.NewLine);
        _output.SelectionColor = _output.ForeColor;
        _output.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts is not null)
        {
            if (MessageBox.Show("Frissítés van folyamatban. Biztosan megszakítod és bezárod?",
                    "Folyamatban", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cts.Cancel();
        }
        base.OnFormClosing(e);
    }
}
