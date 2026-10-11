using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class LicensePage : UserControl
{
    private readonly ListView _keys = new();
    private readonly ListView _apps = new();
    private readonly RichTextBox _note = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly TextBox _search = new();
    private List<LicenseEntry> _entries = new();
    private List<InstalledApp> _installed = new();
    private bool _loaded;

    public LicensePage()
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
        btnRefresh.Click += async (_, _) => await ReloadAsync();

        var btnCopy = new ToolStripButton("Kulcs másolása") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnCopy.Click += (_, _) => CopyKey();

        var btnExport = new ToolStripButton("Jelentés mentése") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnExport.Click += (_, _) => Export();

        _search.Width = 160;
        _search.PlaceholderText = "Szoftver keresése…";
        _search.TextChanged += (_, _) => FillApps();

        tools.Items.AddRange(new ToolStripItem[]
        {
            btnRefresh, btnCopy, new ToolStripSeparator(), btnExport,
            new ToolStripControlHost(_search) { Alignment = ToolStripItemAlignment.Right }
        });

        SetupList(_keys, ("Termék", 250), ("Kulcs / azonosító", 270), ("Állapot", 150), ("Csatorna", 110));
        _keys.ShowGroups = false;
        _keys.Font = new Font("Segoe UI", 9F);
        _keys.SelectedIndexChanged += (_, _) => ShowNote();

        SetupList(_apps, ("Szoftver", 300), ("Verzió", 110), ("Kiadó", 200), ("Telepítve", 90));
        _apps.ShowGroups = false;

        _note.Dock = DockStyle.Fill;
        _note.ReadOnly = true;
        _note.BorderStyle = BorderStyle.None;
        _note.BackColor = Color.FromArgb(250, 250, 252);
        _note.Font = new Font("Segoe UI", 9F);

        var keyPanel = new Panel { Dock = DockStyle.Fill };
        keyPanel.Controls.Add(_keys);
        keyPanel.Controls.Add(MakeHeader("Licenckulcsok és aktiválás"));

        var notePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
        notePanel.Controls.Add(_note);
        notePanel.BackColor = Color.FromArgb(250, 250, 252);

        var topSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
            BorderStyle = BorderStyle.None
        };
        topSplit.ApplyWhenSized(170);
        topSplit.Panel1.Controls.Add(keyPanel);
        topSplit.Panel2.Controls.Add(notePanel);

        var appPanel = new Panel { Dock = DockStyle.Fill };
        appPanel.Controls.Add(_apps);
        appPanel.Controls.Add(MakeHeader("Telepített szoftverek"));

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        mainSplit.ApplyWhenSized(300);
        mainSplit.Panel1.Controls.Add(topSplit);
        mainSplit.Panel2.Controls.Add(appPanel);

        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(_status);

        Controls.Add(mainSplit);
        Controls.Add(tools);
        Controls.Add(strip);
    }

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

    private static void SetupList(ListView lv, params (string H, int W)[] cols)
    {
        lv.View = View.Details;
        lv.Dock = DockStyle.Fill;
        lv.FullRowSelect = true;
        lv.BorderStyle = BorderStyle.None;
        lv.MultiSelect = false;
        lv.HideSelection = false;
        foreach (var c in cols) lv.Columns.Add(c.H, c.W);
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // async void: ha a betoltes kivetelt dob, az itt elnyelodne es osszeomlaszthatna
        // az appot. Ezert becsomagoljuk, es a hibat a felhasznalonak jelezzuk.
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("A fül betöltése hibába ütközött:\n\n" + ex.Message,
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ReloadAsync()
    {
        _status.Text = "Licencadatok lekérdezése…";

        var (entries, apps) = await Task.Run(() =>
        {
            var e = new List<LicenseEntry>();
            List<InstalledApp> a;

            try { e.AddRange(LicenseInfo.Collect(WmiContext.Local)); } catch { }
            try { e.AddRange(LicenseInfo.CollectOffice()); } catch { }
            try { e.AddRange(LicenseInfo.ScanRegistry()); } catch { }
            try { a = LicenseInfo.GetInstalledApps(WmiContext.Local); } catch { a = new(); }

            return (e, a);
        });

        _entries = entries;
        _installed = apps;

        _keys.BeginUpdate();
        _keys.Items.Clear();
        foreach (var e in _entries)
        {
            var lvi = new ListViewItem(new[] { e.Product, e.Key, e.Status, e.Channel }) { Tag = e };
            if (e.Key.StartsWith("XXXXX") || e.Key == "—")
                lvi.ForeColor = Color.FromArgb(120, 126, 138);
            else
                lvi.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
            _keys.Items.Add(lvi);
        }
        _keys.EndUpdate();

        FillApps();

        if (_keys.Items.Count > 0) _keys.Items[0].Selected = true;
        var full = _entries.Count(e => e.Key.Length > 10 && !e.Key.StartsWith("XXXXX") && e.Key != "—");
        _status.Text = $"{_entries.Count} licencbejegyzés ({full} teljes kulccsal) · " +
                       $"{_installed.Count} telepített szoftver" +
                       (HardwareInfo.IsElevated() ? "" : "  ·  rendszergazdaként több adat látszik");
    }

    private void FillApps()
    {
        var filter = _search.Text.Trim();
        var list = _installed.AsEnumerable();
        if (filter.Length > 0)
            list = list.Where(a =>
                a.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                a.Publisher.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

        _apps.BeginUpdate();
        _apps.Items.Clear();
        foreach (var a in list)
            _apps.Items.Add(new ListViewItem(new[] { a.Name, a.Version, a.Publisher, a.InstallDate }));
        _apps.EndUpdate();
    }

    private void ShowNote()
    {
        _note.Clear();
        if (_keys.SelectedItems.Count == 0) return;
        if (_keys.SelectedItems[0].Tag is not LicenseEntry e) return;
        _note.SelectionColor = Color.FromArgb(70, 78, 92);
        _note.AppendText(e.Note);
    }

    private void CopyKey()
    {
        if (_keys.SelectedItems.Count == 0) return;
        var key = _keys.SelectedItems[0].SubItems[1].Text;
        if (key is "—" || key.StartsWith("XXXXX"))
        {
            MessageBox.Show(
                "Ez a bejegyzés nem tartalmaz teljes kulcsot.\n\n" +
                "A Windows a legtöbb kulcsot csak részlegesen (utolsó 5 karakter) tárolja " +
                "visszafejthető formában. Teljes kulcs a gyári (OEM) sorban szokott lenni.",
                "Nincs másolható kulcs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Clipboard.SetText(key);
            _status.Text = "A kulcs a vágólapra másolva.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Licencjelentés mentése",
            Filter = "Szövegfájl|*.txt",
            FileName = $"licenc_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("DevToolbox – licenc- és szoftverjelentés");
        sb.AppendLine($"Gép: {Environment.MachineName}   Készült: {DateTime.Now:yyyy.MM.dd HH:mm}");
        sb.AppendLine(new string('=', 72));
        sb.AppendLine();
        sb.AppendLine("[Licenckulcsok]");
        foreach (var e in _entries)
        {
            sb.AppendLine($"  {e.Product}");
            sb.AppendLine($"    Kulcs:    {e.Key}");
            sb.AppendLine($"    Állapot:  {e.Status}   {e.Channel}");
            if (e.Note.Length > 0) sb.AppendLine($"    Megjegyzés: {e.Note}");
            sb.AppendLine();
        }

        sb.AppendLine($"[Telepített szoftverek – {_installed.Count} db]");
        foreach (var a in _installed)
            sb.AppendLine($"  {a.Name,-52} {a.Version,-16} {a.Publisher}");

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            MessageBox.Show("Mentve:\n" + dlg.FileName, "Kész",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
