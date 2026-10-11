using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class NetworkPage : UserControl
{
    private readonly ListView _adapters = new();
    private readonly ListView _conns = new();
    private readonly ToolStripButton _btnListeners;
    private readonly ToolStripButton _btnVirtual;
    private readonly ToolStripStatusLabel _status = new();
    private bool _loaded;

    public NetworkPage()
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

        _btnListeners = new ToolStripButton("Csak figyelt portok")
        { CheckOnClick = true, Checked = true, DisplayStyle = ToolStripItemDisplayStyle.Text };
        _btnListeners.ToolTipText = "Kikapcsolva az összes aktív kapcsolat is látszik";
        _btnListeners.CheckedChanged += async (_, _) => await ReloadAsync();

        var btnExport = new ToolStripButton("Mentés szövegfájlba") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnExport.Click += (_, _) => Export();

        _btnVirtual = new ToolStripButton("Virtuális adapterek is")
        { CheckOnClick = true, Checked = false, DisplayStyle = ToolStripItemDisplayStyle.Text };
        _btnVirtual.ToolTipText =
            "WAN Miniport, QoS Packet Scheduler, Npcap és egyéb szűrődriver-példányok mutatása";
        _btnVirtual.CheckedChanged += async (_, _) => await ReloadAsync();

        tools.Items.AddRange(new ToolStripItem[]
        {
            btnRefresh, new ToolStripSeparator(), _btnVirtual, new ToolStripSeparator(),
            _btnListeners, new ToolStripSeparator(), btnExport
        });

        SetupList(_adapters, ("Tulajdonság", 190), ("Érték", 420));
        SetupList(_conns, ("Protokoll", 70), ("Helyi cím", 200), ("Távoli cím", 200), ("Állapot", 110));
        _conns.ShowGroups = false;

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_adapters);
        left.Controls.Add(MakeHeader("Hálózati adapterek"));

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_conns);
        right.Controls.Add(MakeHeader("Kapcsolatok és portok"));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(640);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(_status);

        Controls.Add(split);
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
        lv.ShowGroups = true;
        lv.MultiSelect = false;
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
        _status.Text = "Hálózati adatok lekérdezése…";
        var listenersOnly = _btnListeners.Checked;
        var includeVirtual = _btnVirtual.Checked;

        var (sections, conns) = await Task.Run(() =>
        {
            List<HwSection> s;
            List<NetworkInfo.Connection> c;
            try { s = NetworkInfo.Collect(WmiContext.Local, includeVirtual); } catch { s = new(); }
            try { c = NetworkInfo.GetConnections(listenersOnly); } catch { c = new(); }
            return (s, c);
        });

        _adapters.BeginUpdate();
        _adapters.Items.Clear();
        _adapters.Groups.Clear();
        foreach (var sec in sections)
        {
            var g = SplitHelper.NewGroup(sec.Title, sec.Collapsed);
            _adapters.Groups.Add(g);
            foreach (var r in sec.Rows)
                _adapters.Items.Add(new ListViewItem(new[] { r.Key, r.Value }) { Group = g });
        }
        _adapters.EndUpdate();
        _adapters.MakeGroupsCollapsible();

        _conns.BeginUpdate();
        _conns.Items.Clear();
        foreach (var c in conns.OrderBy(x => x.Protocol).ThenBy(x => x.Local))
            _conns.Items.Add(new ListViewItem(new[] { c.Protocol, c.Local, c.Remote, c.State }));
        _conns.EndUpdate();

        var adapterCount = Math.Max(0, sections.Count - 1);
        _status.Text = $"{adapterCount} adapter · {conns.Count} " +
                       (listenersOnly ? "figyelt port" : "kapcsolat") +
                       (includeVirtual ? "  ·  a virtuális adapterek is látszanak" : "");
    }

    private void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Hálózati jelentés mentése",
            Filter = "Szövegfájl|*.txt",
            FileName = $"halozat_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("DevToolbox – hálózati jelentés");
        sb.AppendLine($"Készült: {DateTime.Now:yyyy.MM.dd HH:mm}");
        sb.AppendLine(new string('=', 64));
        sb.AppendLine();

        foreach (ListViewGroup g in _adapters.Groups)
        {
            sb.AppendLine("[" + g.Header + "]");
            foreach (ListViewItem i in g.Items)
                sb.AppendLine($"  {i.SubItems[0].Text,-24} {i.SubItems[1].Text}");
            sb.AppendLine();
        }

        sb.AppendLine("[Kapcsolatok és portok]");
        foreach (ListViewItem i in _conns.Items)
            sb.AppendLine($"  {i.SubItems[0].Text,-6} {i.SubItems[1].Text,-24} {i.SubItems[2].Text,-24} {i.SubItems[3].Text}");

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
