using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class ExportForm : Form
{
    private readonly CheckBox _cHardware = new();
    private readonly CheckBox _cSmart = new();
    private readonly CheckBox _cStorage = new();
    private readonly CheckBox _cNetwork = new();
    private readonly CheckBox _cEvents = new();
    private readonly CheckBox _cLicense = new();
    private readonly CheckBox _cApps = new();
    private readonly CheckBox _cScripts = new();

    private readonly ComboBox _period = new();
    private readonly ComboBox _format = new();
    private readonly TextBox _folder = new();
    private readonly TextBox _fileName = new();
    private readonly CheckBox _openAfter = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _statusLabel = new();
    private readonly Button _btnExport, _btnCancel;

    private CancellationTokenSource? _cts;

    private static readonly (string Label, int Hours)[] Periods =
    {
        ("24 óra", 24), ("3 nap", 72), ("7 nap", 168), ("30 nap", 720)
    };

    public ExportForm()
    {
        Text = "Adatok exportálása";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(660, 720);
        MinimumSize = new Size(600, 620);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(18, 14, 18, 8)
        };

        // ---------------- mit exportáljon
        var secRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 4)
        };
        secRow.Controls.Add(new Label
        {
            Text = "Mit tartalmazzon a jelentés",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10F),
            ForeColor = Color.FromArgb(28, 32, 40),
            Margin = new Padding(0, 4, 14, 0)
        });
        var btnAll = SmallLink("mind");
        btnAll.Click += (_, _) => SetAll(true);
        var btnNone = SmallLink("egyik sem");
        btnNone.Click += (_, _) => SetAll(false);
        secRow.Controls.Add(btnAll);
        secRow.Controls.Add(btnNone);
        root.Controls.Add(secRow);
        Add(root, _cHardware, "Rendszer és hardver (CPU, memória, GPU, alaplap, lemezek)", true);
        Add(root, _cSmart, "Lemezállapot – SMART attribútumokkal és értékeléssel", true);
        Add(root, _cStorage, "Tárhely – meghajtók kihasználtsága", true);
        Add(root, _cNetwork, "Hálózat – adapterek, IP-beállítás, figyelt portok", true);
        Add(root, _cEvents, "Eseménynapló elemzés – rangsorolt problémalista", true);
        Add(root, _cLicense, "Licenckulcsok és aktiválási állapot", true);
        Add(root, _cApps, "Telepített szoftverek listája", false);
        Add(root, _cScripts, "Szkriptkönyvtár tartalma", false);

        var periodRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(22, 4, 0, 6)
        };
        periodRow.Controls.Add(new Label
        {
            Text = "Eseménynapló időszaka:",
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0),
            ForeColor = Color.FromArgb(88, 96, 110)
        });
        foreach (var p in Periods) _period.Items.Add(p.Label);
        _period.DropDownStyle = ComboBoxStyle.DropDownList;
        _period.Width = 100;
        _period.SelectedIndex = Array.FindIndex(Periods, p => p.Hours == AppSettings.Current.EventLogHours);
        if (_period.SelectedIndex < 0) _period.SelectedIndex = 2;
        periodRow.Controls.Add(_period);
        root.Controls.Add(periodRow);

        // ---------------- hova
        root.Controls.Add(Section("Hova mentse"));

        var folderRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(20, 2, 0, 4)
        };
        _folder.Width = 420;
        _folder.Text = AppPaths.ExportDir;
        var btnBrowse = new Button
        {
            Text = "Tallózás…",
            Width = 96,
            Height = 25,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            Margin = new Padding(6, 0, 0, 0)
        };
        btnBrowse.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        btnBrowse.Click += (_, _) => Browse();
        folderRow.Controls.Add(_folder);
        folderRow.Controls.Add(btnBrowse);
        root.Controls.Add(folderRow);

        var fileRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(20, 6, 0, 4)
        };
        fileRow.Controls.Add(new Label
        {
            Text = "Fájlnév:", AutoSize = true,
            Margin = new Padding(0, 6, 8, 0), ForeColor = Color.FromArgb(88, 96, 110)
        });
        _fileName.Width = 280;
        _fileName.Text = $"{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}";
        fileRow.Controls.Add(_fileName);

        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        _format.Items.AddRange(new object[] { "HTML (.html)", "Szöveg (.txt)" });
        _format.SelectedIndex = AppSettings.Current.ExportFormat == "txt" ? 1 : 0;
        _format.Width = 130;
        _format.Margin = new Padding(8, 0, 0, 0);
        fileRow.Controls.Add(_format);
        root.Controls.Add(fileRow);

        _openAfter.Text = "Megnyitás mentés után";
        _openAfter.Checked = AppSettings.Current.OpenAfterExport;
        _openAfter.AutoSize = true;
        _openAfter.Margin = new Padding(22, 8, 0, 4);
        root.Controls.Add(_openAfter);

        var hint = new Label
        {
            Text = "A HTML jelentés önálló fájl: nem hivatkozik külső képre, stíluslapra vagy " +
                   "betűtípusra, tehát e-mailben küldhető és böngészőből nyomtatható. " +
                   "A szakaszai összecsukhatók, a nagy táblázatok alapból csukva nyílnak meg.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.FromArgb(120, 127, 140),
            Font = new Font("Segoe UI", 8.5F),
            Margin = new Padding(22, 8, 24, 6)
        };
        root.Controls.Add(hint);
        root.ClientSizeChanged += (_, _) =>
        {
            var w = root.ClientSize.Width - root.Padding.Horizontal - hint.Margin.Horizontal;
            if (w > 200) hint.MaximumSize = new Size(w, 0);
        };

        // ---------------- állapot
        _progress.Width = 560;
        _progress.Height = 6;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 0;
        _progress.Margin = new Padding(20, 14, 0, 4);
        root.Controls.Add(_progress);

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.FromArgb(88, 96, 110);
        _statusLabel.Margin = new Padding(22, 2, 0, 4);
        _statusLabel.Text = "Válaszd ki a tartalmat, majd indítsd az exportálást.";
        root.Controls.Add(_statusLabel);

        // ---------------- gombsor
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(18, 11, 18, 11) };

        _btnExport = new Button
        {
            Text = "Exportálás",
            Dock = DockStyle.Right,
            Width = 130,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F)
        };
        _btnExport.FlatAppearance.BorderSize = 0;
        _btnExport.Click += async (_, _) => await RunAsync();

        _btnCancel = new Button
        {
            Text = "Bezárás",
            Dock = DockStyle.Right,
            Width = 100,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White
        };
        _btnCancel.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); Close(); };

        bottom.Controls.Add(_btnExport);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(_btnCancel);

        Controls.Add(root);
        Controls.Add(bottom);
        CancelButton = _btnCancel;
    }

    private static Button SmallLink(string text)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(0, 90, 165),
            Font = new Font("Segoe UI", 8.5F),
            Margin = new Padding(0, 2, 6, 0),
            Padding = new Padding(6, 1, 6, 1),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
        return b;
    }

    private void SetAll(bool on)
    {
        foreach (var c in new[] { _cHardware, _cSmart, _cStorage, _cNetwork,
                                  _cEvents, _cLicense, _cApps, _cScripts })
            c.Checked = on;
    }

    private static Label Section(string t) => new()
    {
        Text = t,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10F),
        ForeColor = Color.FromArgb(28, 32, 40),
        Margin = new Padding(0, 12, 0, 6)
    };

    private static void Add(Control parent, CheckBox c, string text, bool on)
    {
        c.Text = text;
        c.Checked = on;
        c.AutoSize = true;
        c.Margin = new Padding(20, 2, 0, 2);
        parent.Controls.Add(c);
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Hova kerüljön a jelentés?",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : AppSettings.DefaultExportFolder
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
    }

    private async Task RunAsync()
    {
        var opts = new ExportOptions
        {
            Hardware = _cHardware.Checked,
            Smart = _cSmart.Checked,
            Storage = _cStorage.Checked,
            Network = _cNetwork.Checked,
            Events = _cEvents.Checked,
            License = _cLicense.Checked,
            Apps = _cApps.Checked,
            Scripts = _cScripts.Checked,
            EventHours = Periods[Math.Max(0, _period.SelectedIndex)].Hours,
            EventWarnings = AppSettings.Current.EventLogWarnings
        };

        if (!opts.Hardware && !opts.Smart && !opts.Storage && !opts.Network &&
            !opts.Events && !opts.License && !opts.Apps && !opts.Scripts)
        {
            MessageBox.Show("Jelölj ki legalább egy tartalmat.", "Nincs mit exportálni",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var isHtml = _format.SelectedIndex == 0;
        var folder = _folder.Text.Trim();
        var name = string.IsNullOrWhiteSpace(_fileName.Text) ? "jelentes" : _fileName.Text.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        var target = Path.Combine(folder, name + (isHtml ? ".html" : ".txt"));

        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            MessageBox.Show("A célmappa nem hozható létre:\n\n" + ex.Message,
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _cts = new CancellationTokenSource();
        _btnExport.Enabled = false;
        _progress.MarqueeAnimationSpeed = 30;

        var progress = new Progress<string>(m => _statusLabel.Text = m);

        try
        {
            var token = _cts.Token;
            var data = await Task.Run(() => ReportBuilder.Collect(opts, progress, token), token);
            var text = isHtml ? HtmlReport.Render(data) : TextReport.Render(data);

            await File.WriteAllTextAsync(target, text, new UTF8Encoding(true), token);

            // A választás megjegyzése a következő alkalomra.
            var s = AppSettings.Current;
            s.ExportFolder = folder;
            s.ExportFormat = isHtml ? "html" : "txt";
            s.OpenAfterExport = _openAfter.Checked;
            s.Save();

            _statusLabel.Text = "Kész: " + target;

            if (_openAfter.Checked)
            {
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                catch { }
            }
            else
            {
                MessageBox.Show("A jelentés elkészült:\n\n" + target, "Kész",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Megszakítva.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Hiba: " + ex.Message;
            MessageBox.Show("Az exportálás nem sikerült:\n\n" + ex.Message,
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progress.MarqueeAnimationSpeed = 0;
            _btnExport.Enabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}
