using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class ServicesForm : Form
{
    private readonly ListView _list = new();
    private readonly RichTextBox _detail = new();
    private readonly ComboBox _filter = new();
    private readonly TextBox _search = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly Label _adminHint = new();
    private readonly Button _btnDisable, _btnManual, _btnAuto, _btnStop, _btnRestore;

    private List<ServiceRow> _rows = new();

    private static readonly (string Label, Func<ServiceRow, bool> Match)[] Filters =
    {
        ("Kikapcsolható szolgáltatások", r => r.IsKnown),
        ("Csak a biztonsággal tilthatók", r => r.Advice?.Risk == ServiceRisk.Biztonsagos),
        ("Amit már letiltottál", r => r.IsDisabled),
        ("Ami most fut", r => r.IsRunning),
        ("Az összes Windows-szolgáltatás", _ => true),
    };

    public ServicesForm()
    {
        Text = "Windows szolgáltatások – " + AppPaths.DisplayName;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1160, 700);
        MinimumSize = new Size(900, 560);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        // ---------------- eszköztár
        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(6, 4, 6, 4)
        };

        var btnRefresh = new ToolStripButton("Frissítés") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnRefresh.Click += (_, _) => Load();

        _filter.DropDownStyle = ComboBoxStyle.DropDownList;
        _filter.Width = 230;
        foreach (var f in Filters) _filter.Items.Add(f.Label);
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => Populate();

        _search.Width = 170;
        _search.PlaceholderText = "Keresés…";
        _search.TextChanged += (_, _) => Populate();

        var btnExport = new ToolStripButton("Lista mentése") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnExport.Click += (_, _) => Export();

        tools.Items.AddRange(new ToolStripItem[]
        {
            btnRefresh, new ToolStripSeparator(),
            new ToolStripControlHost(_filter), new ToolStripSeparator(),
            btnExport,
            new ToolStripControlHost(_search) { Alignment = ToolStripItemAlignment.Right }
        });

        // ---------------- figyelmeztetés
        _adminHint.Dock = DockStyle.Top;
        _adminHint.Height = 34;
        _adminHint.Padding = new Padding(10, 8, 10, 6);
        _adminHint.BackColor = Color.FromArgb(255, 248, 225);
        _adminHint.ForeColor = Color.FromArgb(120, 88, 10);
        _adminHint.Text = "Rendszergazdai jog nélkül a szolgáltatások csak megtekinthetők, nem módosíthatók.";
        _adminHint.Visible = !HardwareInfo.IsElevated();

        // ---------------- lista
        _list.View = View.Details;
        _list.Dock = DockStyle.Fill;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("", 26);
        _list.Columns.Add("Szolgáltatás", 280);
        _list.Columns.Add("Állapot", 70);
        _list.Columns.Add("Indítás", 100);
        _list.Columns.Add("Letiltható?", 120);
        _list.Columns.Add("Kategória", 150);
        _list.SelectedIndexChanged += (_, _) => ShowDetail();

        _detail.Dock = DockStyle.Fill;
        _detail.ReadOnly = true;
        _detail.BorderStyle = BorderStyle.None;
        _detail.BackColor = Color.White;
        _detail.Font = new Font("Segoe UI", 9.5F);

        var detailHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 10, 8) };
        detailHost.Controls.Add(_detail);
        detailHost.Controls.Add(MakeHeader("Mit csinál és mi romlik el, ha letiltod"));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(760);
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(detailHost);

        // ---------------- műveletek
        var actions = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 11, 12, 11) };

        _btnRestore = MakeButton("Vissza az alapértelmezettre", 190);
        _btnRestore.Click += (_, _) => Apply(null);

        _btnAuto = MakeButton("Automatikus", 110);
        _btnAuto.Click += (_, _) => Apply("Automatic");

        _btnManual = MakeButton("Kézi", 90);
        _btnManual.Click += (_, _) => Apply("Manual");

        _btnDisable = MakeButton("Letiltás", 110, primary: true);
        _btnDisable.Click += (_, _) => Apply("Disabled");

        _btnStop = MakeButton("Leállítás most", 130);
        _btnStop.Click += (_, _) => StopSelected();

        var close = MakeButton("Bezárás", 100);
        close.Dock = DockStyle.Right;
        close.Click += (_, _) => Close();

        foreach (var b in new[] { _btnRestore, _btnAuto, _btnManual, _btnDisable, _btnStop })
        {
            b.Dock = DockStyle.Left;
            b.Margin = new Padding(0, 0, 8, 0);
        }

        actions.Controls.Add(close);
        actions.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6 });
        actions.Controls.Add(_btnStop);
        actions.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6 });
        actions.Controls.Add(_btnRestore);
        actions.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6 });
        actions.Controls.Add(_btnAuto);
        actions.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6 });
        actions.Controls.Add(_btnManual);
        actions.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6 });
        actions.Controls.Add(_btnDisable);

        var strip = new StatusStrip { SizingGrip = true };
        strip.Items.Add(_status);

        Controls.Add(split);
        Controls.Add(actions);
        Controls.Add(_adminHint);
        Controls.Add(tools);
        Controls.Add(strip);

        Load();
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

    private static Button MakeButton(string text, int width, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(186, 60, 40) : Color.White,
            ForeColor = primary ? Color.White : Color.FromArgb(40, 46, 58),
            Font = primary ? new Font("Segoe UI Semibold", 9F) : new Font("Segoe UI", 9F)
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        return b;
    }

    // ================================================================ betöltés

    private new void Load()
    {
        _status.Text = "Szolgáltatások lekérdezése…";
        try { _rows = WindowsServices.Query(WmiContext.Local); }
        catch (Exception ex)
        {
            _status.Text = "Hiba: " + ex.Message;
            return;
        }
        Populate();
    }

    private void Populate()
    {
        var match = Filters[Math.Max(0, _filter.SelectedIndex)].Match;
        var text = _search.Text.Trim();

        var rows = _rows.Where(r => match(r));
        if (text.Length > 0)
            rows = rows.Where(r =>
                r.Display.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                r.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                (r.Advice?.Category.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false));

        var shown = rows.ToList();

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var r in shown)
        {
            var (mark, riskText, color) = r.Advice is null
                ? ("", "nem minősített", Color.FromArgb(130, 136, 148))
                : r.Advice.Risk switch
                {
                    ServiceRisk.Biztonsagos => ("✓", "igen, biztonsággal", Color.FromArgb(20, 120, 60)),
                    ServiceRisk.Megfontolando => ("!", "függ a használattól", Color.FromArgb(185, 100, 0)),
                    _ => ("!!", "csak indokolt esetben", Color.FromArgb(190, 30, 30))
                };

            var lvi = new ListViewItem(new[]
            {
                mark,
                r.Display,
                r.StateHu,
                r.StartModeHu,
                riskText,
                r.Advice?.Category ?? ""
            })
            { Tag = r, ForeColor = color };

            if (r.IsDisabled) lvi.BackColor = Color.FromArgb(242, 244, 247);
            _list.Items.Add(lvi);
        }

        _list.EndUpdate();

        var known = _rows.Count(r => r.IsKnown);
        var disabled = _rows.Count(r => r.IsKnown && r.IsDisabled);
        _status.Text = $"{shown.Count} megjelenítve · összesen {_rows.Count} szolgáltatás · " +
                       $"{known} minősített, ebből {disabled} már letiltva";

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        else ShowDetail();
    }

    private ServiceRow? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ServiceRow : null;

    private void ShowDetail()
    {
        _detail.Clear();
        var r = Selected;

        var editable = HardwareInfo.IsElevated() && r is not null;
        foreach (var b in new[] { _btnDisable, _btnManual, _btnAuto, _btnRestore })
            b.Enabled = editable;
        _btnStop.Enabled = editable && r is { IsRunning: true };

        if (r is null) return;

        void W(string t, float size = 9.5F, FontStyle st = FontStyle.Regular, Color? c = null)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionFont = new Font("Segoe UI", size, st);
            _detail.SelectionColor = c ?? Color.FromArgb(50, 56, 68);
            _detail.AppendText(t + Environment.NewLine);
        }

        W(r.Display, 13F, FontStyle.Bold, Color.FromArgb(28, 32, 40));
        W($"{r.Name}   ·   {r.StateHu}   ·   indítás: {r.StartModeHu}",
            8.5F, FontStyle.Regular, Color.FromArgb(130, 137, 150));
        W("");

        if (r.Advice is { } a)
        {
            var (label, color) = a.Risk switch
            {
                ServiceRisk.Biztonsagos => ("Céges munkaállomáson biztonsággal letiltható", Color.FromArgb(20, 120, 60)),
                ServiceRisk.Megfontolando => ("Letiltható, de függ a használattól", Color.FromArgb(185, 100, 0)),
                _ => ("Csak nagyon indokolt esetben tiltsd le", Color.FromArgb(190, 30, 30))
            };
            W(label, 10F, FontStyle.Bold, color);
            W("");

            W("Mit csinál", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            W(a.What);
            W("");

            W("Mi romlik el, ha letiltod", 9.5F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            W(a.Impact);
            W("");

            W($"Windows alapértelmezés: {HuMode(a.DefaultStartMode)}",
                9F, FontStyle.Regular, Color.FromArgb(120, 127, 140));
        }
        else
        {
            W("Ehhez a szolgáltatáshoz nincs értékelés", 10F, FontStyle.Bold, Color.FromArgb(40, 46, 58));
            W("A lista csak azokat minősíti, amiknél a letiltás következményét pontosan " +
              "meg lehet mondani. A többihez itt a Windows saját leírása:");
            W("");
            W(string.IsNullOrWhiteSpace(r.Description) ? "(nincs leírás)" : r.Description,
                9F, FontStyle.Regular, Color.FromArgb(80, 86, 98));
        }

        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
    }

    private static string HuMode(string m) => m.ToLowerInvariant() switch
    {
        "automatic" or "auto" => "Automatikus",
        "manual" => "Kézi",
        "disabled" => "Letiltva",
        _ => m
    };

    // ================================================================ módosítás

    private void Apply(string? startMode)
    {
        var r = Selected;
        if (r is null) return;

        var target = startMode ?? r.Advice?.DefaultStartMode ?? "Manual";

        var warn = "";
        if (target.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && r.Advice is { } a)
        {
            warn = "\n\nMi romlik el:\n" + a.Impact;
            if (a.Risk == ServiceRisk.Kockazatos)
                warn = "\n\nFIGYELEM – ez kockázatos:\n" + a.Impact;
        }

        var confirm = MessageBox.Show(
            $"„{r.Display}” indítási módja: {HuMode(target)}{warn}\n\n" +
            "A módosítás a következő újraindítás után lép teljesen érvénybe.\n\nFolytatod?",
            "Szolgáltatás módosítása", MessageBoxButtons.YesNo,
            r.Advice?.Risk == ServiceRisk.Kockazatos ? MessageBoxIcon.Warning : MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        var res = WindowsServices.SetStartMode(WmiContext.Local, r.Name, target);
        if (!res.Ok)
        {
            MessageBox.Show("Nem sikerült: " + res.Message, "Hiba",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        r.StartMode = target;
        _status.Text = $"„{r.Display}” – {HuMode(target)}";

        if (target.Equals("Disabled", StringComparison.OrdinalIgnoreCase) && r.IsRunning)
        {
            if (MessageBox.Show("A szolgáltatás jelenleg fut. Leállítsam most is?",
                    "Leállítás", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                StopSelected(silent: true);
        }

        Populate();
    }

    private void StopSelected(bool silent = false)
    {
        var r = Selected;
        if (r is null) return;

        var res = WindowsServices.Stop(WmiContext.Local, r.Name);
        if (res.Ok)
        {
            r.State = "Stopped";
            _status.Text = $"„{r.Display}” leállítva.";
            Populate();
        }
        else if (!silent)
        {
            MessageBox.Show("Nem sikerült leállítani: " + res.Message, "Hiba",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ================================================================ export

    private void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Szolgáltatáslista mentése",
            Filter = "Szövegfájl|*.txt",
            FileName = $"szolgaltatasok_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("DevToolbox – Windows szolgáltatások");
        sb.AppendLine($"Gép: {Environment.MachineName}   Készült: {DateTime.Now:yyyy.MM.dd HH:mm}");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine();

        foreach (var grp in _rows.Where(r => r.IsKnown)
                                 .GroupBy(r => r.Advice!.Category)
                                 .OrderBy(g => g.Key))
        {
            sb.AppendLine($"[{grp.Key}]");
            foreach (var r in grp.OrderBy(r => r.Display))
            {
                sb.AppendLine($"  {r.Display}  ({r.Name})");
                sb.AppendLine($"    állapot: {r.StateHu}, indítás: {r.StartModeHu}, " +
                              $"letiltható: {r.Advice!.Risk}");
                sb.AppendLine($"    hatás:   {r.Advice.Impact}");
            }
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
}
