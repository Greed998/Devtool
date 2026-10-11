using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace DevToolbox;

public sealed class StoragePage : UserControl
{
    private readonly FlowLayoutPanel _cards = new();
    private readonly ListView _folders = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripButton _btnAnalyze, _btnCancel;
    private CancellationTokenSource? _cts;
    private DriveCard? _selected;
    private bool _loaded;

    public StoragePage()
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
        btnRefresh.Click += async (_, _) => await LoadDrivesAsync();

        _btnAnalyze = new ToolStripButton("Mappák elemzése") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnAnalyze.ToolTipText = "A kijelölt meghajtó legnagyobb mappáinak kiszámolása";
        _btnAnalyze.Click += async (_, _) => await AnalyzeAsync();

        _btnCancel = new ToolStripButton("Megszakítás") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
        _btnCancel.Click += (_, _) => _cts?.Cancel();

        tools.Items.AddRange(new ToolStripItem[] { btnRefresh, new ToolStripSeparator(), _btnAnalyze, _btnCancel });

        _cards.Dock = DockStyle.Top;
        _cards.Height = 250;
        _cards.AutoScroll = true;
        _cards.Padding = new Padding(12, 12, 12, 6);
        _cards.BackColor = Color.White;

        _folders.View = View.Details;
        _folders.Dock = DockStyle.Fill;
        _folders.FullRowSelect = true;
        _folders.BorderStyle = BorderStyle.None;
        _folders.Columns.Add("Mappa", 460);
        _folders.Columns.Add("Méret", 110, HorizontalAlignment.Right);
        _folders.Columns.Add("Arány", 90, HorizontalAlignment.Right);
        _folders.DoubleClick += (_, _) =>
        {
            if (_folders.SelectedItems.Count == 0) return;
            var p = _folders.SelectedItems[0].Tag as string;
            if (p is null) return;
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
        };

        var header = new Label
        {
            Text = "  Legnagyobb mappák",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(246, 247, 249),
            ForeColor = Color.FromArgb(60, 68, 82),
            Font = new Font("Segoe UI Semibold", 9F)
        };

        var strip = new StatusStrip { SizingGrip = false };
        _status.Text = "Válassz meghajtót a kártyára kattintva.";
        strip.Items.Add(_status);

        var bottom = new Panel { Dock = DockStyle.Fill };
        bottom.Controls.Add(_folders);
        bottom.Controls.Add(header);

        Controls.Add(bottom);
        Controls.Add(_cards);
        Controls.Add(tools);
        Controls.Add(strip);
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // async void: ha a betoltes kivetelt dob, az itt elnyelodne es osszeomlaszthatna
        // az appot. Ezert becsomagoljuk, es a hibat a felhasznalonak jelezzuk.
        try { await LoadDrivesAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("A fül betöltése hibába ütközött:\n\n" + ex.Message,
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// A DriveInfo tulajdonsagai (IsReady, TotalSize, VolumeLabel...) blokkolo rendszerhivasok:
    /// lecsatolt halozati meghajtonal vagy ures optikai meghajtonal masodpercekre megallhatnak.
    /// Ezert hattérszalon olvassuk ki oket EGYSZER, es a kartyak csak a masolattal dolgoznak.
    /// </summary>
    private async Task LoadDrivesAsync()
    {
        _cards.Controls.Clear();
        _selected = null;
        _btnAnalyze.Enabled = false;
        _status.Text = "Meghajtók beolvasása…";

        List<DriveSnapshot> snapshots;
        try
        {
            snapshots = await Task.Run(StorageInfo.ReadDrives);
        }
        catch (Exception ex)
        {
            _status.Text = "A meghajtók beolvasása nem sikerült: " + ex.Message;
            return;
        }

        foreach (var snap in snapshots)
        {
            var card = new DriveCard(snap);
            card.Click += Card_Click;
            _cards.Controls.Add(card);
        }

        _status.Text = _cards.Controls.Count == 0
            ? "Nem található csatolt meghajtó."
            : $"{_cards.Controls.Count} meghajtó. Válassz egyet a kártyára kattintva.";
    }

    private void Card_Click(object? sender, EventArgs e)
    {
        var card = sender as DriveCard ?? (sender as Control)?.Parent as DriveCard;
        if (card is null) return;

        foreach (Control c in _cards.Controls) if (c is DriveCard dc) dc.Selected = false;
        card.Selected = true;
        _selected = card;
        _btnAnalyze.Enabled = true;
        var d = card.Drive;
        _status.Text = $"{d.Name} kijelölve – {Fmt.Bytes(d.Total - d.Free)} használatban, " +
                       $"{Fmt.Bytes(d.Free)} szabad.";
    }

    private async Task AnalyzeAsync()
    {
        if (_selected is null) return;
        var root = _selected.Drive.RootPath;

        _folders.Items.Clear();
        _btnAnalyze.Enabled = false;
        _btnCancel.Enabled = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _status.Text = $"{root} elemzése… (ez több percig is tarthat)";

        try
        {
            var results = await Task.Run(() =>
            {
                var list = new List<(string Path, long Size)>();
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root); }
                catch { return list; }

                foreach (var dir in dirs)
                {
                    token.ThrowIfCancellationRequested();
                    list.Add((dir, DirectorySize(dir, token)));
                }

                // A gyokerben levo fajlok osszmerete kulon sorban.
                try
                {
                    long rootFiles = 0;
                    foreach (var f in Directory.EnumerateFiles(root))
                    {
                        try { rootFiles += new FileInfo(f).Length; } catch { }
                    }
                    if (rootFiles > 0) list.Add((root + "  (fájlok a gyökérben)", rootFiles));
                }
                catch { }

                return list.OrderByDescending(x => x.Size).Take(40).ToList();
            }, token);

            var total = results.Sum(r => r.Size);
            _folders.BeginUpdate();
            foreach (var (path, size) in results)
            {
                var pct = total > 0 ? size * 100.0 / total : 0;
                _folders.Items.Add(new ListViewItem(new[]
                {
                    path,
                    Fmt.Bytes(size),
                    $"{pct:0.#}%"
                })
                { Tag = Directory.Exists(path) ? path : root });
            }
            _folders.EndUpdate();

            _status.Text = $"Kész – {results.Count} mappa, összesen {Fmt.Bytes(total)}. Dupla kattintás: megnyitás.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Az elemzés megszakítva.";
        }
        catch (Exception ex)
        {
            _status.Text = "Hiba: " + ex.Message;
        }
        finally
        {
            _btnAnalyze.Enabled = true;
            _btnCancel.Enabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Rekurziv meretszamitas, a hozzaferesi hibakat atlepve.</summary>
    private static long DirectorySize(string path, CancellationToken token)
    {
        long size = 0;
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            try
            {
                foreach (var f in Directory.EnumerateFiles(current))
                {
                    try { size += new FileInfo(f).Length; } catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(current))
                {
                    // Reparse pointek (junction, symlink) kihagyasa a vegtelen ciklus ellen.
                    try
                    {
                        var attr = File.GetAttributes(d);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(d);
                }
            }
            catch { /* hozzaferes megtagadva - atlepjuk */ }
        }

        return size;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Egy meghajtot abrazolo kartya. KIZAROLAG a pillanatfelvetelbol rajzol - a festes
/// soha nem inditbat blokkolo rendszerhivast, kulonben megall a felulet.
/// </summary>
public sealed class DriveCard : Panel
{
    public DriveSnapshot Drive { get; }
    private bool _selected;

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public DriveCard(DriveSnapshot drive)
    {
        Drive = drive;
        Size = new Size(268, 108);
        Margin = new Padding(6);
        Cursor = Cursors.Hand;
        BackColor = Color.White;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var total = Drive.Total;
        var free = Drive.Free;
        var used = total - free;
        var ratio = total > 0 ? (double)used / total : 0;

        using (var pen = new Pen(_selected ? Color.FromArgb(0, 120, 212) : Color.FromArgb(224, 227, 233),
                   _selected ? 2f : 1f))
            g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);

        var title = $"{Drive.Name.TrimEnd('\\')}  {Drive.Label}".TrimEnd();
        using (var f = new Font("Segoe UI Semibold", 11F))
        using (var br = new SolidBrush(Color.FromArgb(28, 32, 40)))
            g.DrawString(title, f, br, 14, 11);

        var typeText = Drive.Type switch
        {
            DriveType.Fixed => "belső",
            DriveType.Removable => "cserélhető",
            DriveType.Network => "hálózati",
            DriveType.CDRom => "optikai",
            _ => "egyéb"
        };

        using (var f = new Font("Segoe UI", 8F))
        using (var br = new SolidBrush(Color.FromArgb(130, 138, 152)))
            g.DrawString($"{typeText} · {Drive.Format}", f, br, 15, 32);

        var barRect = new Rectangle(14, 54, Width - 28, 14);
        using (var back = new SolidBrush(Color.FromArgb(237, 239, 243)))
            g.FillRectangle(back, barRect);

        var fillW = (int)(barRect.Width * ratio);
        var color = ratio >= 0.92 ? Color.FromArgb(214, 48, 49)
                  : ratio >= 0.80 ? Color.FromArgb(230, 145, 20)
                  : Color.FromArgb(0, 145, 105);
        if (fillW > 0)
            using (var fill = new SolidBrush(color))
                g.FillRectangle(fill, barRect.X, barRect.Y, fillW, barRect.Height);

        using (var pen = new Pen(Color.FromArgb(220, 223, 229)))
            g.DrawRectangle(pen, barRect);

        using (var f = new Font("Segoe UI", 8.5F))
        using (var br = new SolidBrush(Color.FromArgb(70, 78, 92)))
            g.DrawString($"{Fmt.Bytes(used)} / {Fmt.Bytes(total)}   ·   szabad: {Fmt.Bytes(free)}",
                f, br, 14, 76);

        using (var f = new Font("Segoe UI Semibold", 9F))
        using (var br = new SolidBrush(color))
        {
            var pct = $"{ratio * 100:0}%";
            var sz = g.MeasureString(pct, f);
            g.DrawString(pct, f, br, Width - 14 - sz.Width, 30);
        }
    }
}
