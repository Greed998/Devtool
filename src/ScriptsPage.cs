using System.Diagnostics;

namespace DevToolbox;

public sealed class ScriptsPage : UserControl
{
    private readonly ScriptLibrary _lib = new();
    private readonly ListView _list = new();
    private readonly RichTextBox _out = new();
    private readonly TextBox _search = new();
    private readonly Label _detail = new();
    private readonly ToolStripButton _btnRun, _btnStop, _btnEdit, _btnDelete;
    private ScriptRunner? _runner;
    private CancellationTokenSource? _cts;

    public ScriptsPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        // ---------------- eszkoztar
        var tools = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(6, 4, 6, 4),
            RenderMode = ToolStripRenderMode.System,
            ImageScalingSize = new Size(18, 18)
        };

        var btnImport = MakeBtn("Betöltés fájlból", "Meglévő .bat / .ps1 / .vbs / .py fájl felvétele a könyvtárba");
        btnImport.Click += (_, _) => ImportFiles();

        var btnNew = MakeBtn("Új szkript", "Új szkript írása a beépített szerkesztőben");
        btnNew.Click += (_, _) => CreateNew();

        var btnBuilder = MakeBtn("⚙  Építő…",
            "Kiszolgálói szkript összeállítása adatok megadásával (AD, DHCP, DNS, Linux)");
        btnBuilder.Click += (_, _) => RunBuilder();

        _btnEdit = MakeBtn("Szerkesztés", "A kijelölt szkript megnyitása szerkesztésre");
        _btnEdit.Click += (_, _) => EditSelected();

        _btnRun = MakeBtn("▶  Futtatás", "A kijelölt szkript futtatása (F5)");
        _btnRun.Click += async (_, _) => await RunSelectedAsync();

        _btnStop = MakeBtn("■  Leállítás", "A futó szkript megszakítása");
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => _cts?.Cancel();

        _btnDelete = MakeBtn("Törlés", "A kijelölt szkript eltávolítása a könyvtárból");
        _btnDelete.Click += (_, _) => DeleteSelected();

        var btnFolder = MakeBtn("Mappa", "A szkriptek mappájának megnyitása az Intézőben");
        btnFolder.Click += (_, _) => OpenFolder();

        var btnCopy = MakeBtn("Vágólapra", "A kijelölt szkript tartalmának másolása");
        btnCopy.Click += (_, _) => CopySelected();

        var btnSaveAs = MakeBtn("Mentés…", "A kijelölt szkript kimentése fájlba");
        btnSaveAs.Click += (_, _) => SaveSelectedAs();

        _search.Width = 180;
        _search.PlaceholderText = "Keresés…";
        _search.TextChanged += (_, _) => RefreshList();
        var searchHost = new ToolStripControlHost(_search) { Alignment = ToolStripItemAlignment.Right };

        tools.Items.AddRange(new ToolStripItem[]
        {
            btnImport, btnNew, btnBuilder, new ToolStripSeparator(),
            _btnRun, _btnStop, new ToolStripSeparator(),
            _btnEdit, _btnDelete, new ToolStripSeparator(),
            btnFolder, btnCopy, btnSaveAs, searchHost
        });

        // ---------------- lista
        _list.View = View.Details;
        _list.Dock = DockStyle.Fill;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.ShowGroups = true;
        _list.Columns.Add("Név", 210);
        _list.Columns.Add("Típus", 95);
        _list.Columns.Add("Platform", 100);
        _list.Columns.Add("Utolsó futás", 110);
        _list.Columns.Add("Db", 45, HorizontalAlignment.Right);
        _list.SelectedIndexChanged += (_, _) => UpdateDetail();
        _list.DoubleClick += async (_, _) => await RunSelectedAsync();
        _list.KeyDown += async (s, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; await RunSelectedAsync(); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = true; DeleteSelected(); }
            else if (e.KeyCode == Keys.F2) { e.Handled = true; EditSelected(); }
        };

        // ---------------- kimenet
        _out.Dock = DockStyle.Fill;
        _out.ReadOnly = true;
        _out.BorderStyle = BorderStyle.None;
        _out.BackColor = Color.FromArgb(24, 24, 27);
        _out.ForeColor = Color.FromArgb(210, 214, 220);
        _out.Font = new Font("Consolas", 9.5F);
        _out.WordWrap = false;
        _out.ScrollBars = RichTextBoxScrollBars.Both;

        _detail.Dock = DockStyle.Top;
        _detail.Height = 46;
        _detail.Padding = new Padding(10, 6, 10, 4);
        _detail.BackColor = Color.FromArgb(246, 247, 249);
        _detail.ForeColor = Color.FromArgb(70, 78, 92);
        _detail.Text = "Nincs kijelölt szkript.";

        var outHeader = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(38, 38, 42) };
        outHeader.Controls.Add(new Label
        {
            Text = "  Kimenet",
            Dock = DockStyle.Left,
            Width = 120,
            ForeColor = Color.FromArgb(190, 195, 205),
            TextAlign = ContentAlignment.MiddleLeft
        });
        var btnClear = new Button
        {
            Text = "Törlés",
            Dock = DockStyle.Right,
            Width = 70,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(190, 195, 205),
            BackColor = Color.FromArgb(38, 38, 42)
        };
        btnClear.FlatAppearance.BorderSize = 0;
        btnClear.Click += (_, _) => _out.Clear();
        outHeader.Controls.Add(btnClear);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_out);
        right.Controls.Add(outHeader);
        right.Controls.Add(_detail);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BorderStyle = BorderStyle.None
        };
        split.ApplyWhenSized(470);
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);
        Controls.Add(tools);

        _lib.Load();
        RefreshList();
        SeedOrRefreshSamples();
    }

    private static ToolStripButton MakeBtn(string text, string tip) => new(text)
    {
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        ToolTipText = tip,
        Padding = new Padding(6, 2, 6, 2)
    };

    // ---------------------------------------------------------------- lista

    private void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();

        var filter = _search.Text.Trim();
        var items = _lib.Items.AsEnumerable();
        if (filter.Length > 0)
            items = items.Where(i =>
                i.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                i.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                i.Category.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

        foreach (var grp in items.GroupBy(i => i.Category).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var lvg = SplitHelper.NewGroup(grp.Key);
            _list.Groups.Add(lvg);

            foreach (var it in grp.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var lvi = new ListViewItem(new[]
                {
                    it.Name,
                    it.KindLabel + (it.RunAsAdmin ? " ⚡" : ""),
                    it.PlatformLabel,
                    it.LastRun?.ToString("MM.dd HH:mm") ?? "–",
                    it.RunCount.ToString()
                })
                { Tag = it, Group = lvg };

                // Ami itt nem futtathato, halvanyabban jelenik meg.
                if (!ScriptRunner.CanRun(it.Kind).CanRun)
                    lvi.ForeColor = Color.FromArgb(120, 126, 138);

                _list.Items.Add(lvi);
            }
        }

        _list.EndUpdate();
        _list.MakeGroupsCollapsible();
        UpdateDetail();
    }

    private ScriptItem? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ScriptItem : null;

    private void UpdateDetail()
    {
        var it = Selected;
        var has = it is not null;
        _btnEdit.Enabled = has;
        _btnDelete.Enabled = has;

        var runnable = has && ScriptRunner.CanRun(it!.Kind).CanRun;
        _btnRun.Enabled = runnable && _runner is null;

        if (it is null)
        {
            _detail.Text = "Nincs kijelölt szkript.";
            _detail.ForeColor = Color.FromArgb(70, 78, 92);
            return;
        }

        var line = $"{it.Name}   ·   {it.KindLabel}   ·   {it.PlatformLabel}   ·   {it.FileName}";
        if (!string.IsNullOrWhiteSpace(it.Description)) line += "\n" + it.Description;

        var (can, reason) = ScriptRunner.CanRun(it.Kind);
        if (reason.Length > 0) line += "\n" + (can ? "ℹ  " : "⚠  ") + reason;

        _detail.Text = line;
        _detail.ForeColor = can ? Color.FromArgb(70, 78, 92) : Color.FromArgb(150, 90, 20);
    }

    // ---------------------------------------------------------------- muveletek

    private void ImportFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Szkript kiválasztása",
            Multiselect = true,
            Filter = "Szkriptek|*.bat;*.cmd;*.ps1;*.vbs;*.py|Batch|*.bat;*.cmd|PowerShell|*.ps1|Minden fájl|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int ok = 0;
        foreach (var f in dlg.FileNames)
        {
            try { _lib.ImportFile(f); ok++; }
            catch (Exception ex)
            {
                MessageBox.Show($"„{Path.GetFileName(f)}" + "\" betöltése sikertelen:\n" + ex.Message,
                    "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        RefreshList();
        if (ok > 0) Log($"[{ok} szkript betöltve a könyvtárba]", false);
    }

    private void CreateNew()
    {
        var draft = new ScriptItem
        {
            Name = "Új szkript",
            Kind = ScriptKind.Batch,
            OutputEncoding = AppSettings.Current.DefaultScriptEncoding
        };
        using var dlg = new ScriptEditForm(draft, _lib.Categories, isNew: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var created = _lib.CreateNew(draft.Name, dlg.SelectedKind, dlg.Code, draft.Category);
            created.Description = draft.Description;
            created.Arguments = draft.Arguments;
            created.RunAsAdmin = draft.RunAsAdmin;
            created.OutputEncoding = draft.OutputEncoding;
            _lib.Save();
            RefreshList();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Nem sikerült létrehozni:\n" + ex.Message, "Hiba",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EditSelected()
    {
        var it = Selected;
        if (it is null) return;

        using var dlg = new ScriptEditForm(it, _lib.Categories, isNew: false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // A sorvegek es a BOM tipusfuggo: a bash elszall a CR-tol es a BOM-tol is.
            File.WriteAllText(it.FullPath, ScriptLibrary.Normalize(dlg.Code, it.Kind),
                new System.Text.UTF8Encoding(
                    it.Kind is not (ScriptKind.Batch or ScriptKind.Bash or ScriptKind.Sql)));
            _lib.Save();
            RefreshList();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Mentés sikertelen:\n" + ex.Message, "Hiba",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteSelected()
    {
        var it = Selected;
        if (it is null) return;

        var r = MessageBox.Show(
            $"Törlöd a(z) „{it.Name}\" szkriptet?\n\nA fájl is törlődik a könyvtárból.",
            "Törlés", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        _lib.Delete(it);
        RefreshList();
    }

    private void OpenFolder()
    {
        try
        {
            AppPaths.EnsureDataDirs();
            Process.Start(new ProcessStartInfo(AppPaths.ScriptsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RunBuilder()
    {
        using var dlg = new ScriptBuilderForm();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null) return;

        var (name, kind, content, admin, desc) = dlg.Result.Value;
        try
        {
            var created = _lib.CreateNew(name, kind, content, "Kiszolgáló");
            created.Description = desc;
            created.RunAsAdmin = admin;
            _lib.Save();
            RefreshList();

            foreach (ListViewItem i in _list.Items)
                if (i.Tag is ScriptItem s && s.Id == created.Id) { i.Selected = true; i.EnsureVisible(); }

            Log($"[„{name}” hozzáadva a könyvtárhoz]", false, Color.FromArgb(120, 190, 255));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Nem sikerült felvenni:\n" + ex.Message, "Hiba",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopySelected()
    {
        var it = Selected;
        if (it is null) return;
        try
        {
            Clipboard.SetText(File.ReadAllText(it.FullPath));
            Log($"[„{it.Name}” a vágólapra másolva]", false, Color.FromArgb(120, 190, 255));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveSelectedAs()
    {
        var it = Selected;
        if (it is null) return;

        var ext = ScriptItem.ExtensionOf(it.Kind);
        using var dlg = new SaveFileDialog
        {
            Title = "Szkript mentése",
            Filter = $"Szkript (*{ext})|*{ext}|Minden fájl|*.*",
            FileName = it.FileName,
            InitialDirectory = AppPaths.ExportDir
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);
            File.Copy(it.FullPath, dlg.FileName, overwrite: true);
            Log($"[mentve: {dlg.FileName}]", false, Color.FromArgb(120, 190, 255));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------------------------------------------------------- futtatas

    private async Task RunSelectedAsync()
    {
        var it = Selected;
        if (it is null || _runner is not null) return;

        if (AppSettings.Current.ConfirmBeforeRun)
        {
            var confirm = MessageBox.Show(
                $"Lefuttatod a(z) „{it.Name}\" szkriptet?" +
                (it.RunAsAdmin ? "\n\nEmelt jogosultsággal fog futni." : ""),
                "Megerősítés", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
        }

        _out.Clear();
        Log($"▶ {it.Name}  ({it.KindLabel})  –  {DateTime.Now:HH:mm:ss}", false, Color.FromArgb(120, 190, 255));
        if (it.RunAsAdmin)
            Log("  emelt jogosultsággal, külön ablakban indul – a kimenet ott jelenik meg", false,
                Color.FromArgb(230, 190, 100));
        Log(new string('─', 60), false, Color.FromArgb(90, 95, 105));

        _runner = new ScriptRunner();
        _cts = new CancellationTokenSource();
        _btnRun.Enabled = false;
        _btnStop.Enabled = !it.RunAsAdmin;

        _runner.OutputReceived += (line, isErr) =>
        {
            if (IsHandleCreated)
                BeginInvoke(() => Log(line, isErr));
        };

        try
        {
            await _runner.RunAsync(it, _cts.Token);
            var elapsed = TimeSpan.Zero;
            it.LastRun = DateTime.Now;
            it.RunCount++;
            _lib.Save();
            Log(new string('─', 60), false, Color.FromArgb(90, 95, 105));
            Log($"✔ Kész – {DateTime.Now:HH:mm:ss}", false, Color.FromArgb(120, 220, 140));
        }
        catch (Exception ex)
        {
            Log("HIBA: " + ex.Message, true);
        }
        finally
        {
            _runner = null;
            _cts?.Dispose();
            _cts = null;
            _btnStop.Enabled = false;
            RefreshList();
        }
    }

    private void Log(string text, bool isError, Color? color = null)
    {
        _out.SelectionStart = _out.TextLength;
        _out.SelectionLength = 0;
        _out.SelectionColor = color ?? (isError ? Color.FromArgb(255, 120, 120) : Color.FromArgb(210, 214, 220));
        _out.AppendText(text + Environment.NewLine);
        _out.SelectionColor = _out.ForeColor;
        _out.ScrollToCaret();
    }

    // ---------------------------------------------------------------- minták

    private void SeedOrRefreshSamples()
    {
        try
        {
            if (_lib.SeedSamples())
            {
                RefreshList();
                AppSettings.Current.SeedVersion = ScriptSamples.Version;
                AppSettings.Current.Save();
                return;
            }

            if (AppSettings.Current.SeedVersion >= ScriptSamples.Version) return;

            var updated = _lib.RefreshSamples();
            AppSettings.Current.SeedVersion = ScriptSamples.Version;
            AppSettings.Current.Save();

            if (updated > 0)
            {
                RefreshList();
                Log($"[{updated} szkript hozzáadva vagy frissítve a beépített készletből]", false, Color.FromArgb(120, 190, 255));
                Log("Nézd meg a Diagnosztika, Karbantartás, Leltár, Hálózat és Biztonság kategóriákat.",
                    false, Color.FromArgb(150, 156, 168));
            }
        }
        catch { }
    }
}
