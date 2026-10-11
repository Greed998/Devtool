using System.Runtime.Versioning;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;

namespace DevToolbox;

/// <summary>
/// Flottakezelő: a Fleet Server adatait mutatja. Ha a szerver ugyanezen a gépen fut és a
/// DevToolbox hozzáfér az adatmappájához, közvetlenül olvassa; egyébként az adatmappa
/// beállítható. A DevToolbox itt a szerver „adminfelülete” – az agentekhez feladatot ad
/// (frissítés), és látja, mi elavult rajtuk.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FleetPage : UserControl
{
    private readonly ListView _agents = new();
    private readonly ListView _updates = new();
    private readonly RichTextBox _detail = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly Label _noStore = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private FleetStore? _store;
    private bool _loaded;

    public FleetPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(6, 4, 6, 4)
        };

        var btnRefresh = new ToolStripButton("Frissítés") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnRefresh.Click += (_, _) => Reload();

        var btnToken = new ToolStripButton("Új beléptető-token…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnToken.Click += (_, _) => NewToken();

        var btnUpdate = new ToolStripButton("Kijelölt frissítése") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnUpdate.Click += (_, _) => QueueUpdateSelected();

        var btnUpdateAll = new ToolStripButton("Összes frissítése a gépen") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnUpdateAll.Click += (_, _) => QueueUpdateAll();

        var btnRevoke = new ToolStripButton("Agent visszavonása") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnRevoke.Click += (_, _) => RevokeSelected();

        var btnFolder = new ToolStripButton("Adatmappa megadása…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnFolder.Click += (_, _) => PickFolder();

        tools.Items.AddRange(new ToolStripItem[]
        {
            btnRefresh, new ToolStripSeparator(),
            btnUpdate, btnUpdateAll, new ToolStripSeparator(),
            btnToken, btnRevoke, new ToolStripSeparator(), btnFolder
        });

        // ---- nincs adatmappa üzenet
        _noStore.Dock = DockStyle.Fill;
        _noStore.TextAlign = ContentAlignment.MiddleCenter;
        _noStore.ForeColor = Color.FromArgb(100, 108, 122);
        _noStore.Font = new Font("Segoe UI", 10F);
        _noStore.Visible = false;

        // ---- agentek
        _agents.View = View.Details;
        _agents.Dock = DockStyle.Fill;
        _agents.FullRowSelect = true;
        _agents.MultiSelect = false;
        _agents.HideSelection = false;
        _agents.BorderStyle = BorderStyle.None;
        _agents.Columns.Add("", 26);
        _agents.Columns.Add("Gép", 180);
        _agents.Columns.Add("Állapot", 90);
        _agents.Columns.Add("Frissíthető", 90, HorizontalAlignment.Right);
        _agents.Columns.Add("Agent verzió", 90);
        _agents.Columns.Add("Utoljára látott", 130);
        _agents.SelectedIndexChanged += (_, _) => ShowAgent();

        var agentPanel = new Panel { Dock = DockStyle.Fill };
        agentPanel.Controls.Add(_agents);
        agentPanel.Controls.Add(Header("Felügyelt gépek (agenttel)"));

        // ---- frissíthető alkalmazások
        _updates.View = View.Details;
        _updates.Dock = DockStyle.Fill;
        _updates.FullRowSelect = true;
        _updates.CheckBoxes = true;
        _updates.BorderStyle = BorderStyle.None;
        _updates.Columns.Add("Alkalmazás", 240);
        _updates.Columns.Add("Telepített", 120);
        _updates.Columns.Add("Elérhető", 120);
        _updates.Columns.Add("Azonosító", 200);

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.BorderStyle = BorderStyle.None;
        _detail.BackColor = Color.White;
        _detail.Font = new Font("Segoe UI", 9.5F);
        _detail.Height = 90;

        var detailHost = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(14, 6, 10, 6) };
        detailHost.Controls.Add(_detail);

        var updPanel = new Panel { Dock = DockStyle.Fill };
        updPanel.Controls.Add(_updates);
        updPanel.Controls.Add(detailHost);
        updPanel.Controls.Add(Header("Elavult alkalmazások a kijelölt gépen"));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(640);
        split.Panel1.Controls.Add(agentPanel);
        split.Panel2.Controls.Add(updPanel);

        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(_status);

        var main = new Panel { Dock = DockStyle.Fill };
        main.Controls.Add(split);
        main.Controls.Add(_noStore);

        Controls.Add(main);
        Controls.Add(tools);
        Controls.Add(strip);

        _timer.Interval = 15000;   // 15 mp-enként frissül, ha a fül látszik
        _timer.Tick += (_, _) => Reload();
        VisibleChanged += (_, _) => _timer.Enabled = Visible && _loaded && _store is not null;
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
        OpenStore();
    }

    /// <summary>A Fleet Server adatmappája (beállítható; alapból a helyi szerveré) - az alkalmazásfrissítő távoli módja is ezt olvassa.</summary>
    public static string StoreDirectory =>
        AppSettings.Current.FleetDataDir.Length > 0
            ? AppSettings.Current.FleetDataDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                           "DevToolbox-Fleet");

    private string StoreDir => StoreDirectory;

    private void OpenStore()
    {
        var dir = StoreDir;
        if (!Directory.Exists(Path.Combine(dir)) || !File.Exists(Path.Combine(dir, "server-public.pem")))
        {
            _store = null;
            _noStore.Visible = true;
            _noStore.Text =
                "Nincs elérhető Fleet Server adatmappa.\n\n" +
                "A flottakezeléshez futnia kell a Fleet Servernek (a szerveren telepített\n" +
                "DevToolbox Fleet Server szolgáltatás). Ha a szerver ezen a gépen fut,\n" +
                "add meg az adatmappáját az „Adatmappa megadása” gombbal.\n\n" +
                $"Alapértelmezett hely: {dir}";
            return;
        }

        try
        {
            _store = new FleetStore(dir, owner: false);
            _noStore.Visible = false;
            Reload();
            _timer.Enabled = Visible;
        }
        catch (UnauthorizedAccessException)
        {
            _store = null;
            _noStore.Visible = true;
            _noStore.Text =
                "A Fleet adatmappához nincs hozzáférés.\n\n" +
                "A mappa a szerver kulcsa miatt csak a rendszer és a rendszergazdák számára\n" +
                "olvasható. Indítsd a DevToolboxot rendszergazdaként\n" +
                "(Program → Újraindítás rendszergazdaként).";
        }
        catch (Exception ex)
        {
            _store = null;
            _noStore.Visible = true;
            _noStore.Text = "A Fleet adatmappa nem olvasható:\n\n" + ex.Message;
        }
    }

    private void Reload()
    {
        if (_store is null) return;

        // Ujraolvassuk a fajlt, mert a szerver kozben irja.
        try { _store = new FleetStore(StoreDir, owner: false); }
        catch { return; }

        var selectedId = SelectedAgent?.AgentId;

        _agents.BeginUpdate();
        _agents.Items.Clear();

        foreach (var a in _store.Agents.OrderByDescending(x => x.LastSeenUtc))
        {
            var (mark, color, state) = a.Revoked
                ? ("✕", Color.FromArgb(150, 40, 160), "Visszavonva")
                : a.IsOnline
                    ? ("●", Color.FromArgb(20, 130, 70), "Online")
                    : ("○", Color.FromArgb(150, 90, 20), "Offline");

            var upd = a.LastStatus?.UpdatesAvailable ?? 0;
            var lvi = new ListViewItem(new[]
            {
                mark,
                a.MachineName,
                state,
                upd.ToString(),
                a.AgentVersion,
                a.LastSeenUtc?.ToLocalTime().ToString("MM.dd HH:mm") ?? "–"
            })
            { Tag = a, ForeColor = color };

            if (upd > 0 && !a.Revoked) lvi.Font = new Font(_agents.Font, FontStyle.Bold);
            _agents.Items.Add(lvi);
        }

        _agents.EndUpdate();

        if (selectedId is not null)
            foreach (ListViewItem i in _agents.Items)
                if ((i.Tag as EnrolledAgent)?.AgentId == selectedId) { i.Selected = true; break; }
        if (_agents.SelectedItems.Count == 0 && _agents.Items.Count > 0)
            _agents.Items[0].Selected = true;

        var online = _store.Agents.Count(a => a.IsOnline && !a.Revoked);
        var totalUpd = _store.Agents.Where(a => !a.Revoked).Sum(a => a.LastStatus?.UpdatesAvailable ?? 0);
        _status.Text = $"{_store.Agents.Count} agent · {online} online · " +
                       $"összesen {totalUpd} frissíthető alkalmazás";
    }

    private EnrolledAgent? SelectedAgent =>
        _agents.SelectedItems.Count > 0 ? _agents.SelectedItems[0].Tag as EnrolledAgent : null;

    private void ShowAgent()
    {
        _updates.Items.Clear();
        _detail.Clear();

        var a = SelectedAgent;
        if (a is null) return;

        W(a.MachineName, 12F, FontStyle.Bold, Color.FromArgb(28, 32, 40));
        var meta = new List<string>();
        if (a.LastStatus?.OperatingSystem is { Length: > 0 } os) meta.Add(os);
        meta.Add(a.LastStatus?.IsVirtual == true ? "virtuális" : "fizikai");
        meta.Add("agent " + a.AgentVersion);
        meta.Add("ujjlenyomat " + a.Fingerprint[..12]);
        W(string.Join("  ·  ", meta), 8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));
        W($"Regisztrálva: {a.EnrolledUtc.ToLocalTime():yyyy.MM.dd}  ·  " +
          $"utoljára látva: {a.LastSeenUtc?.ToLocalTime():yyyy.MM.dd HH:mm}",
          8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));

        if (a.LastStatus?.Updates is { Count: > 0 } updates)
        {
            foreach (var u in updates.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                _updates.Items.Add(new ListViewItem(new[] { u.Name, u.Installed, u.Available, u.Id })
                { Tag = u });
        }
        else if (a.LastStatus is not null)
        {
            _updates.Items.Add(new ListViewItem(new[] { "Minden alkalmazás naprakész", "", "", "" })
            { ForeColor = Color.FromArgb(20, 130, 70) });
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

    private void NewToken()
    {
        if (_store is null) { NoStoreWarning(); return; }

        var label = Prompt("Adj nevet a tokennek (pl. iroda-2024):", "iroda");
        if (label is null) return;

        var token = _store.CreateEnrollmentToken(label);
        using var dlg = new TokenDisplayForm(token, _store.ServerKeys.Fingerprint, StoreDir);
        dlg.ShowDialog(FindForm());
    }

    private void QueueUpdateSelected()
    {
        var a = SelectedAgent;
        if (a is null || _store is null) return;

        var ids = _updates.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as AppUpdate).Where(u => u is not null).Cast<AppUpdate>().ToList();

        if (ids.Count == 0)
        {
            MessageBox.Show("Jelölj ki legalább egy frissíthető alkalmazást.", "Nincs kijelölés",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var u in ids)
            _store.QueueTask(a.AgentId, new AgentTask { Kind = AgentTaskKind.UpgradeApp, Target = u.Id });

        MessageBox.Show(
            $"{ids.Count} frissítési feladat betéve a(z) „{a.MachineName}” sorába.\n\n" +
            "Az agent a következő jelentkezéskor elvégzi. Az eredmény a szerveren látszik.",
            "Feladat betéve", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void QueueUpdateAll()
    {
        var a = SelectedAgent;
        if (a is null || _store is null) return;

        if (MessageBox.Show(
                $"A(z) „{a.MachineName}” ÖSSZES frissíthető alkalmazása frissüljön?\n\n" +
                "Az agent rendszergazdai jogon végzi el, a felhasználó közreműködése nélkül.",
                "Összes frissítése", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _store.QueueTask(a.AgentId, new AgentTask { Kind = AgentTaskKind.UpgradeAll });
        MessageBox.Show("A feladat betéve. Az agent a következő jelentkezéskor elvégzi.",
            "Feladat betéve", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RevokeSelected()
    {
        var a = SelectedAgent;
        if (a is null || _store is null) return;

        if (MessageBox.Show(
                $"Visszavonod a(z) „{a.MachineName}” agentet?\n\n" +
                "Ezután nem tud jelentkezni és feladatot kapni. Új regisztrációhoz " +
                "újra kell telepíteni a gépen.",
                "Visszavonás", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _store.RevokeAgent(a.AgentId);
        Reload();
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "A Fleet Server adatmappája (server-public.pem és fleet.json van benne)",
            UseDescriptionForTitle = true
        };
        if (Directory.Exists(StoreDir)) dlg.SelectedPath = StoreDir;
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        AppSettings.Current.FleetDataDir = dlg.SelectedPath;
        AppSettings.Current.Save();
        OpenStore();
    }

    private void NoStoreWarning() =>
        MessageBox.Show("Nincs elérhető Fleet Server adatmappa.", "Nincs szerver",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static string? Prompt(string text, string def)
    {
        using var f = new Form
        {
            Text = "DevToolbox", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(420, 130),
            MaximizeBox = false, MinimizeBox = false, BackColor = Color.White
        };
        var lbl = new Label { Text = text, AutoSize = true, Location = new Point(16, 16), MaximumSize = new Size(388, 0) };
        var tb = new TextBox { Text = def, Location = new Point(16, 56), Width = 388 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(228, 92), Width = 80 };
        var cancel = new Button { Text = "Mégse", DialogResult = DialogResult.Cancel, Location = new Point(316, 92), Width = 80 };
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK && tb.Text.Trim().Length > 0 ? tb.Text.Trim() : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
}

// ================================================================ token megjelenítő

[SupportedOSPlatform("windows")]
public sealed class TokenDisplayForm : Form
{
    public TokenDisplayForm(string token, string serverFingerprint, string storeDir)
    {
        Text = "Új beléptető-token";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(680, 420);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var body = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5F)
        };

        void H(string t) { body.SelectionFont = new Font("Segoe UI Semibold", 11F); body.SelectionColor = Color.FromArgb(28,32,40); body.AppendText(t + "\n"); }
        void P(string t) { body.SelectionFont = new Font("Segoe UI", 9.5F); body.SelectionColor = Color.FromArgb(60,66,78); body.AppendText(t + "\n"); }
        void Mono(string t) { body.SelectionFont = new Font("Consolas", 11F, FontStyle.Bold); body.SelectionColor = Color.FromArgb(20,60,130); body.AppendText("    " + t + "\n"); }
        void Gap() => body.AppendText("\n");

        H("A token csak MOST látszik");
        P("A szerver csak a lenyomatát tárolja, visszafejteni nem lehet. Másold ki és tedd " +
          "biztonságos helyre – ezt adod meg minden gép telepítésekor.");
        Gap();

        H("Beléptető-token");
        Mono(token);
        Gap();

        H("A szerver kulcs-ujjlenyomata");
        P("Ezt is add meg a telepítőnek – ezzel ellenőrzi az agent, hogy a valódi szerverhez " +
          "csatlakozik (közbeékelődés ellen).");
        Mono(serverFingerprint);
        Gap();

        H("Telepítés a felügyelt gépen (rendszergazda parancssor)");
        body.SelectionFont = new Font("Consolas", 9.5F);
        body.SelectionColor = Color.FromArgb(70, 76, 88);
        body.AppendText(
            "    DevToolboxAgent.exe install ^\n" +
            "        --server https://SZERVER:8443 ^\n" +
            $"        --token {token[..Math.Min(16, token.Length)]}… ^\n" +
            $"        --fingerprint {serverFingerprint[..Math.Min(16, serverFingerprint.Length)]}…\n");
        Gap();
        P("A felhasználónak NEM kell adminnak lennie – az agent LocalSystem-ként fut, és ő " +
          "végzi a frissítéseket. A gépen nem nyílik port: az agent kifelé csatlakozik.");

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(16, 10, 16, 10) };
        var copy = new Button
        {
            Text = "Token másolása", Dock = DockStyle.Right, Width = 140,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 120, 212), ForeColor = Color.White
        };
        copy.FlatAppearance.BorderSize = 0;
        copy.Click += (_, _) => { try { Clipboard.SetText(token); } catch { } };

        var copyFp = new Button
        {
            Text = "Ujjlenyomat másolása", Dock = DockStyle.Right, Width = 160,
            FlatStyle = FlatStyle.Flat, BackColor = Color.White
        };
        copyFp.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        copyFp.Click += (_, _) => { try { Clipboard.SetText(serverFingerprint); } catch { } };

        var close = new Button { Text = "Bezárás", Dock = DockStyle.Right, Width = 90, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
        close.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        close.Click += (_, _) => Close();

        bottom.Controls.Add(close);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(copyFp);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(copy);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8) };
        host.Controls.Add(body);

        Controls.Add(host);
        Controls.Add(bottom);
        CancelButton = close;
    }
}
