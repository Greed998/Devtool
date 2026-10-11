#if GUI_SETUP
using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;

namespace DevToolbox.Fleet;

/// <summary>Egy varazslo-lepes.</summary>
public sealed class WizardPage
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public Control Body { get; init; } = new Panel();
    /// <summary>A Tovabb gomb felirata ezen a lepesen (pl. "Telepítés").</summary>
    public string NextText { get; init; } = "Tovább >";
    /// <summary>Tovabblepes elotti ellenorzes. false = marad a lepesen.</summary>
    public Func<Task<bool>>? OnNext { get; init; }
    /// <summary>Belepeskor fut (pl. maga a telepites). Ilyenkor a gombok tiltva vannak.</summary>
    public Func<Task>? OnEnter { get; init; }
    public bool CanGoBack { get; init; } = true;
    public bool IsLast { get; init; }
}

/// <summary>
/// InstallShield-szeru telepitovarazslo keret. Bal oldali savval, lepescimmel es
/// Vissza / Tovabb / Megse gombokkal. A tartalmat a programok adjak (WizardPage).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WizardForm : Form
{
    public static readonly Color Accent = Color.FromArgb(91, 140, 255);
    private static readonly Color Side = Color.FromArgb(22, 26, 36);

    private readonly List<WizardPage> _pages;
    private readonly Label _title = new() { AutoSize = false, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 13f, FontStyle.Bold) };
    private readonly Label _subtitle = new() { AutoSize = false, Dock = DockStyle.Top, Height = 38, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 9.5f) };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
    private readonly Button _back = new() { Text = "< Vissza", AutoSize = true, MinimumSize = new Size(96, 30) };
    private readonly Button _next = new() { Text = "Tovább >", AutoSize = true, MinimumSize = new Size(110, 30) };
    private readonly Button _cancel = new() { Text = "Mégse", AutoSize = true, MinimumSize = new Size(90, 30) };
    private int _index;
    private bool _busy;

    public WizardForm(string product, string version, List<WizardPage> pages)
    {
        _pages = pages;
        // A meretek 96 DPI-re vannak megadva; a Windows a tenyleges DPI szerint aranyosan felskalazza
        // (kulonben nagyobb DPI-n a betu no, a pozicio nem - a varazslo szetcsuszik).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = product + " – telepítő";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 480);
        BackColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // bal oldali sav
        var side = new Panel { Dock = DockStyle.Left, Width = 190, BackColor = Side };
        side.Controls.Add(new Label
        {
            Text = "Poltoli Informatika", ForeColor = Color.FromArgb(140, 150, 170), Dock = DockStyle.Bottom, Height = 40,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 8.5f)
        });
        side.Controls.Add(new Label
        {
            Text = product + "\n\nverzió " + version, ForeColor = Color.White, Dock = DockStyle.Top, Height = 140,
            Padding = new Padding(18, 28, 12, 0), Font = new Font("Segoe UI", 12f, FontStyle.Bold)
        });
        side.Paint += (_, e) => e.Graphics.FillRectangle(new SolidBrush(Accent), 0, 0, side.Width, 5);

        // alsó gombsor
        // Jobbra igazitott gombsor: a tenyleges szelesseghez igazodik. (Korabban fix koordinatak + jobb horgony
        // voltak a meg 200 px szeles panelhez merve - a vegleges szelessegnel a gombok kicsusztak az ablakbol.)
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(246, 247, 250) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 12, 16, 0) };
        _cancel.Margin = new Padding(12, 0, 0, 0); _next.Margin = new Padding(6, 0, 0, 0); _back.Margin = new Padding(0);
        buttons.Controls.AddRange(new Control[] { _cancel, _next, _back });
        bottom.Controls.Add(buttons);
        _next.BackColor = Accent; _next.ForeColor = Color.White; _next.FlatStyle = FlatStyle.Flat; _next.FlatAppearance.BorderSize = 0;

        // Minden oldal mar most az ablakba kerul (csak a lathato valtozik), igy a DPI-skalazas rajuk is vonatkozik.
        foreach (var pg in _pages) { pg.Body.Dock = DockStyle.Fill; pg.Body.Visible = false; _content.Controls.Add(pg.Body); }

        var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 10) };
        main.Controls.Add(_content);
        main.Controls.Add(_subtitle);
        main.Controls.Add(_title);

        Controls.Add(main);
        Controls.Add(bottom);
        Controls.Add(side);

        _back.Click += (_, _) => { if (_index > 0) Show(_index - 1); };
        _next.Click += async (_, _) => await NextAsync();
        _cancel.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; MessageBox.Show(this, "A telepítés folyamatban van, kérlek várd meg a végét.", Text); }
        };
        Shown += (_, _) => Show(0);
    }

    public void SetNextEnabled(bool on) => _next.Enabled = on;

    private async Task NextAsync()
    {
        var page = _pages[_index];
        if (page.IsLast) { Close(); return; }
        if (page.OnNext is not null)
        {
            _next.Enabled = false;
            bool ok;
            try { ok = await page.OnNext(); } finally { _next.Enabled = true; }
            if (!ok) return;
        }
        Show(_index + 1);
    }

    private async void Show(int i)
    {
        _index = i;
        var p = _pages[i];
        _title.Text = p.Title;
        _subtitle.Text = p.Subtitle;
        foreach (var pg in _pages) pg.Body.Visible = ReferenceEquals(pg, p);
        _next.Text = p.IsLast ? "Befejezés" : p.NextText;
        _back.Enabled = i > 0 && p.CanGoBack;
        _cancel.Enabled = !p.IsLast;

        if (p.OnEnter is not null)
        {
            _busy = true; _back.Enabled = _next.Enabled = _cancel.Enabled = false;
            try { await p.OnEnter(); }
            finally { _busy = false; }
            if (i + 1 < _pages.Count) Show(i + 1);
        }
        else _next.Enabled = true;
    }

    // ------------------------------------------------------------------ építőelemek

    public static Label Para(string text, int height = 0) => new()
    {
        Text = text, AutoSize = height == 0, MaximumSize = new Size(470, 0), Height = height,
        Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 10)
    };

    /// <summary>Cimke + szovegmezo egy sorban, felulrol lefele egymas ala.</summary>
    public static TextBox Field(Control parent, string label, string value, ref int y, bool password = false)
    {
        parent.Controls.Add(new Label { Text = label, Location = new Point(0, y), AutoSize = true });
        var tb = new TextBox { Text = value, Location = new Point(0, y + 20), Width = 470, UseSystemPasswordChar = password };
        parent.Controls.Add(tb);
        y += 54;
        return tb;
    }

    /// <summary>Konzol-kimenet atiranyitasa egy szovegdobozba (a telepitok Console-ra irnak).</summary>
    public sealed class BoxWriter(TextBox box) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var v = value.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            if (box.IsHandleCreated) box.BeginInvoke(() => box.AppendText(v));
        }
        public override void WriteLine(string? value) => Write((value ?? "") + "\n");
    }

    /// <summary>A telepito futtatasa hattersalon, a kimenet a naplodobozba.</summary>
    public static async Task RunWithLog(TextBox log, Action install)
    {
        var old = Console.Out;
        var w = new BoxWriter(log);
        Console.SetOut(w);
        try { await Task.Run(install); }
        catch (Exception ex) { w.WriteLine("HIBA: " + ex.Message); }
        finally { Console.SetOut(old); }
    }

    public static TextBox LogBox() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(22, 26, 36),
        ForeColor = Color.FromArgb(220, 225, 235), Font = new Font("Consolas", 9f), Dock = DockStyle.Fill
    };

    /// <summary>A konzolablak elrejtese, ha dupla kattintassal indult (az exe konzolos).</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool FreeConsole();
}
#endif
