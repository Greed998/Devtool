using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class EventLogPage : UserControl
{
    private readonly ListView _list = new();
    private readonly RichTextBox _detail = new();
    private readonly Label _verdict = new();
    private readonly Label _counters = new();
    private readonly ToolStripComboBox _period = new();
    private readonly ToolStripComboBox _level = new();
    private readonly ToolStripComboBox _logs = new();
    private readonly ToolStripButton _btnScan, _btnCancel, _btnExport;
    private readonly ToolStripStatusLabel _status = new();
    private readonly TextBox _search = new();

    private ScanResult? _result;
    private CancellationTokenSource? _cts;
    private bool _loaded;
    private bool _ready;      // az inditaskori beallitasok ne inditsanak elemzest

    private static readonly (string Label, int Hours)[] Periods =
    {
        ("Utolsó 24 óra", 24),
        ("Utolsó 3 nap", 72),
        ("Utolsó 7 nap", 168),
        ("Utolsó 30 nap", 720)
    };

    private static readonly (string Label, string[] Logs)[] LogSets =
    {
        ("Rendszer + Alkalmazás", new[] { "System", "Application" }),
        ("Csak Rendszer", new[] { "System" }),
        ("Csak Alkalmazás", new[] { "Application" }),
        ("Mind + Biztonság", new[] { "System", "Application", "Setup", "Security" })
    };

    public EventLogPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        // ---------------- eszköztár
        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(6, 4, 6, 4)
        };

        foreach (var p in Periods) _period.Items.Add(p.Label);
        _period.SelectedIndex = Math.Max(0,
            Array.FindIndex(Periods, x => x.Hours == AppSettings.Current.EventLogHours));
        _period.DropDownStyle = ComboBoxStyle.DropDownList;
        _period.Width = 130;

        _level.Items.AddRange(new object[] { "Hibák + figyelmeztetések", "Csak hibák" });
        _level.SelectedIndex = AppSettings.Current.EventLogWarnings ? 0 : 1;
        _level.DropDownStyle = ComboBoxStyle.DropDownList;
        _level.Width = 175;

        foreach (var l in LogSets) _logs.Items.Add(l.Label);
        _logs.SelectedIndex = 0;
        _logs.DropDownStyle = ComboBoxStyle.DropDownList;
        _logs.Width = 165;

        _btnScan = new ToolStripButton("Elemzés") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        _btnScan.Click += async (_, _) => await ScanAsync();

        _btnCancel = new ToolStripButton("Megszakítás") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnCancel.Click += (_, _) => _cts?.Cancel();

        _btnExport = new ToolStripButton("Jelentés mentése") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnExport.Click += (_, _) => Export();

        _search.Width = 150;
        _search.PlaceholderText = "Szűrés…";
        _search.TextChanged += (_, _) => Populate();

        tools.Items.AddRange(new ToolStripItem[]
        {
            _period, _level, _logs, new ToolStripSeparator(),
            _btnScan, _btnCancel, new ToolStripSeparator(), _btnExport,
            new ToolStripControlHost(_search) { Alignment = ToolStripItemAlignment.Right }
        });

        // ---------------- összegző sáv
        _verdict.Dock = DockStyle.Top;
        _verdict.Height = 40;
        _verdict.Padding = new Padding(14, 9, 14, 4);
        _verdict.Font = new Font("Segoe UI Semibold", 11F);
        _verdict.BackColor = Color.FromArgb(246, 247, 249);
        _verdict.Text = "Még nem futott elemzés.";

        _counters.Dock = DockStyle.Top;
        _counters.Height = 26;
        _counters.Padding = new Padding(16, 2, 14, 6);
        _counters.BackColor = Color.FromArgb(246, 247, 249);
        _counters.ForeColor = Color.FromArgb(88, 96, 110);
        _counters.Text = "";

        // ---------------- lista
        _list.View = View.Details;
        _list.Dock = DockStyle.Fill;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("", 26);                        // súlyosság jelölő
        _list.Columns.Add("Db", 46, HorizontalAlignment.Right);
        _list.Columns.Add("Mi a probléma", 320);
        _list.Columns.Add("Forrás", 170);
        _list.Columns.Add("ID", 50, HorizontalAlignment.Right);
        _list.Columns.Add("Utolsó előfordulás", 130);
        _list.SelectedIndexChanged += (_, _) => ShowDetail();

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.BorderStyle = BorderStyle.None;
        _detail.BackColor = Color.White;
        _detail.Font = new Font("Segoe UI", 9.5F);
        _detail.Text = "Válassz egy sort a listából.";

        var detailHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 10, 8) };
        detailHost.Controls.Add(_detail);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(660);
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(detailHost);

        var strip = new StatusStrip { SizingGrip = false };
        _status.Text = "Készen áll.";
        strip.Items.Add(_status);

        Controls.Add(split);
        Controls.Add(_counters);
        Controls.Add(_verdict);
        Controls.Add(tools);
        Controls.Add(strip);

        // Idoszak / szint / naplo valtasakor azonnal fusson ujra az elemzes.
        _ready = true;
        _period.SelectedIndexChanged += async (_, _) => await RescanAsync();
        _level.SelectedIndexChanged  += async (_, _) => await RescanAsync();
        _logs.SelectedIndexChanged   += async (_, _) => await RescanAsync();
    }

    /// <summary>Ujraelemzes beallitas-valtaskor. A meg futo elemzest megszakitja.</summary>
    private async Task RescanAsync()
    {
        if (!_ready || !_loaded) return;

        if (_cts is not null)
        {
            _cts.Cancel();
            // Megvarjuk, hogy az elozo befejezodjon, kulonben osszeakadnanak.
            for (int i = 0; i < 50 && _cts is not null; i++) await Task.Delay(20);
        }

        await ScanAsync();
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // async void: ha a betoltes kivetelt dob, az itt elnyelodne es osszeomlaszthatna
        // az appot. Ezert becsomagoljuk, es a hibat a felhasznalonak jelezzuk.
        try { await ScanAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("A fül betöltése hibába ütközött:\n\n" + ex.Message,
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- elemzés

    private async Task ScanAsync()
    {
        if (_cts is not null) return;

        var hours = Periods[Math.Max(0, _period.SelectedIndex)].Hours;
        var logs = LogSets[Math.Max(0, _logs.SelectedIndex)].Logs;
        var withWarnings = _level.SelectedIndex == 0;

        _cts = new CancellationTokenSource();
        _btnScan.Enabled = false;
        _btnCancel.Enabled = true;
        _btnExport.Enabled = false;
        _list.Items.Clear();
        _detail.Clear();
        _verdict.Text = "Elemzés folyamatban…";
        _verdict.ForeColor = Color.FromArgb(60, 68, 82);

        var progress = new Progress<string>(m => _status.Text = m);
        var token = _cts.Token;

        try
        {
            _result = await Task.Run(
                () => EventLogAnalyzer.Scan(logs, hours, withWarnings, 8000, progress, token),
                token);

            Populate();
            _btnExport.Enabled = true;
            _status.Text = $"Kész – {_result.TotalEvents} esemény feldolgozva " +
                           $"{_result.Duration.TotalSeconds:0.#} mp alatt, " +
                           $"{_result.Groups.Count} csoportba vonva.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Az elemzés megszakítva.";
            _verdict.Text = "Megszakítva.";
        }
        catch (Exception ex)
        {
            _status.Text = "Hiba: " + ex.Message;
            _verdict.Text = "Az elemzés hibára futott.";
            _verdict.ForeColor = Color.FromArgb(190, 40, 40);
        }
        finally
        {
            _btnScan.Enabled = true;
            _btnCancel.Enabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------------------------------------------------------------- megjelenítés

    private void Populate()
    {
        if (_result is null) return;

        var filter = _search.Text.Trim();
        var groups = _result.Groups.AsEnumerable();

        if (filter.Length > 0)
            groups = groups.Where(g =>
                g.Headline.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                g.Provider.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                g.Subject.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                g.EventId.ToString() == filter);

        if (AppSettings.Current.HideKnownNoise)
            groups = groups.Where(g => g.EffectiveSeverity != Severity.Zaj);

        var shown = groups.ToList();

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var g in shown)
        {
            var mark = g.EffectiveSeverity switch
            {
                Severity.Kritikus => "!!",
                Severity.Komoly => "!",
                Severity.Figyelmeztetes => "·",
                _ => ""
            };

            var head = g.Headline;
            if (g.Subject.Length > 0) head += $"  ({g.Subject})";

            var lvi = new ListViewItem(new[]
            {
                mark,
                g.Count.ToString(),
                head,
                ShortProvider(g.Provider),
                g.EventId.ToString(),
                g.Last.ToString("MM.dd HH:mm")
            })
            { Tag = g };

            lvi.ForeColor = ColorFor(g.EffectiveSeverity);
            if (g.EffectiveSeverity >= Severity.Komoly)
                lvi.Font = new Font(_list.Font, FontStyle.Bold);

            _list.Items.Add(lvi);
        }

        _list.EndUpdate();
        UpdateSummary(shown);

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    private void UpdateSummary(List<ProblemGroup> shown)
    {
        if (_result is null) return;

        var crit = shown.Count(g => g.EffectiveSeverity == Severity.Kritikus);
        var serious = shown.Count(g => g.EffectiveSeverity == Severity.Komoly);
        var warn = shown.Count(g => g.EffectiveSeverity == Severity.Figyelmeztetes);
        var noise = shown.Count(g => g.EffectiveSeverity == Severity.Zaj);

        if (crit > 0)
        {
            var top = shown.First(g => g.EffectiveSeverity == Severity.Kritikus);
            _verdict.Text = $"⚠  {crit} kritikus probléma – legfontosabb: {top.Headline}";
            _verdict.ForeColor = Color.FromArgb(190, 30, 30);
        }
        else if (serious > 0)
        {
            var top = shown.First(g => g.EffectiveSeverity == Severity.Komoly);
            _verdict.Text = $"{serious} komoly hiba – legfontosabb: {top.Headline}";
            _verdict.ForeColor = Color.FromArgb(190, 105, 0);
        }
        else if (warn > 0)
        {
            _verdict.Text = $"Nincs komoly hiba. {warn} figyelmeztetés érdemel átnézést.";
            _verdict.ForeColor = Color.FromArgb(150, 120, 0);
        }
        else
        {
            _verdict.Text = "Nem található érdemi hiba a vizsgált időszakban.";
            _verdict.ForeColor = Color.FromArgb(20, 120, 60);
        }

        var sb = new StringBuilder();
        sb.Append($"{_result.TotalEvents} esemény · ");
        sb.Append($"{_result.Critical} kritikus, {_result.Errors} hiba, {_result.Warnings} figyelmeztetés · ");
        sb.Append($"{shown.Count} csoport");
        if (noise > 0) sb.Append($" (ebből {noise} ismert ártalmatlan)");
        if (_result.Skipped.Count > 0) sb.Append("  ·  kihagyva: " + string.Join("; ", _result.Skipped));
        _counters.Text = sb.ToString();
    }

    private static Color ColorFor(Severity s) => s switch
    {
        Severity.Kritikus => Color.FromArgb(190, 30, 30),
        Severity.Komoly => Color.FromArgb(185, 100, 0),
        Severity.Figyelmeztetes => Color.FromArgb(120, 100, 20),
        _ => Color.FromArgb(140, 145, 155)
    };

    private static string ShortProvider(string p) =>
        p.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase) ? p[18..] : p;

    private void ShowDetail()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not ProblemGroup g) return;

        _detail.Clear();

        AppendLine(g.Headline, 13F, FontStyle.Bold, ColorFor(g.EffectiveSeverity));
        AppendLine(
            $"{g.LevelLabel} · {g.LogName} napló · {g.Provider} · {g.EventId}. esemény",
            8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));

        var span = g.Count == 1
            ? g.Last.ToString("yyyy.MM.dd HH:mm:ss")
            : $"{g.Count} alkalommal, {g.First:MM.dd HH:mm} és {g.Last:MM.dd HH:mm} között";
        AppendLine(span, 8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));
        AppendLine("");

        if (g.Diag is { } d)
        {
            AppendLine("Mit jelent", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            AppendLine(d.Meaning, 9.5F, FontStyle.Regular, Color.FromArgb(50, 56, 68));
            AppendLine("");

            AppendLine("Mit érdemes tenni", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            AppendLine(d.Action, 9.5F, FontStyle.Regular, Color.FromArgb(50, 56, 68));
            AppendLine("");

            if (d.Severity == Severity.Zaj)
            {
                AppendLine("Ez az esemény a legtöbb gépen megjelenik, és önmagában nem jelez hibát.",
                    9F, FontStyle.Italic, Color.FromArgb(120, 126, 138));
                AppendLine("");
            }
        }
        else
        {
            AppendLine("Ehhez az eseményhez nincs beépített magyarázat", 9.5F, FontStyle.Bold,
                Color.FromArgb(40, 46, 58));
            AppendLine(
                "Az alábbi eredeti üzenet tartalmazza a részleteket. A forrás és az azonosító " +
                "alapján lehet rákeresni a gyártó dokumentációjában.",
                9.5F, FontStyle.Regular, Color.FromArgb(50, 56, 68));
            AppendLine("");
        }

        AppendLine("Eredeti üzenet", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
        AppendLine(g.SampleMessage, 9F, FontStyle.Regular, Color.FromArgb(70, 76, 88));

        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
    }

    private void AppendLine(string text, float size = 9.5F, FontStyle style = FontStyle.Regular, Color? color = null)
    {
        _detail.SelectionStart = _detail.TextLength;
        _detail.SelectionLength = 0;
        _detail.SelectionFont = new Font("Segoe UI", size, style);
        _detail.SelectionColor = color ?? Color.Black;
        _detail.AppendText(text + Environment.NewLine);
    }

    // ---------------------------------------------------------------- export

    private void Export()
    {
        if (_result is null) return;

        using var dlg = new SaveFileDialog
        {
            Title = "Eseménynapló jelentés mentése",
            Filter = "Szövegfájl|*.txt",
            FileName = $"esemenynaplo_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("DevToolbox – eseménynapló elemzés");
        sb.AppendLine($"Gép: {Environment.MachineName}   Készült: {DateTime.Now:yyyy.MM.dd HH:mm}");
        sb.AppendLine($"Időszak: {Periods[_period.SelectedIndex].Label}");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine(_verdict.Text);
        sb.AppendLine(_counters.Text);
        sb.AppendLine();

        foreach (var g in _result.Groups)
        {
            sb.AppendLine(new string('-', 78));
            sb.AppendLine($"[{g.EffectiveSeverity}] {g.Headline}" +
                          (g.Subject.Length > 0 ? $"  ({g.Subject})" : ""));
            sb.AppendLine($"  {g.Provider} / {g.EventId} · {g.LogName} · {g.Count}x · utoljára {g.Last:yyyy.MM.dd HH:mm}");
            if (g.Diag is { } d)
            {
                sb.AppendLine($"  Jelentése: {d.Meaning}");
                sb.AppendLine($"  Teendő:    {d.Action}");
            }
            sb.AppendLine("  Eredeti üzenet:");
            foreach (var line in g.SampleMessage.Split('\n').Take(12))
                sb.AppendLine("    " + line.TrimEnd());
            sb.AppendLine();
        }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}
