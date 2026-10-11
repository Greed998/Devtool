using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows.Forms;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// A tálca-felület kinézete a Fleet webfelületének stílusában (1.5.7, a felhasználó kérése): sötét háttér és panelek,
/// kék kiemelőszín, bal oldali menü, lapos gombok, sötét címsor (Windows 10 20H1+ / 11). A színek a WebUi.Css változói.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayTheme
{
    public static readonly Color Bg = Color.FromArgb(0x0f, 0x11, 0x16), Panel = Color.FromArgb(0x17, 0x1a, 0x21), Panel2 = Color.FromArgb(0x1d, 0x21, 0x2a),
        Line = Color.FromArgb(0x27, 0x2c, 0x37), Ink = Color.FromArgb(0xe6, 0xe9, 0xef), Muted = Color.FromArgb(0x8a, 0x93, 0xa3),
        Accent = Color.FromArgb(0x5b, 0x8c, 0xff), Accent2 = Color.FromArgb(0x7c, 0x5c, 0xff), Ok = Color.FromArgb(0x31, 0xc0, 0x7a),
        Warn = Color.FromArgb(0xe0, 0xa5, 0x2b), Err = Color.FromArgb(0xf0, 0x55, 0x4e);

    public static readonly Font Base = new("Segoe UI", 9.5f), Bold = new("Segoe UI", 9.5f, FontStyle.Bold),
        H2 = new("Segoe UI Semibold", 13f), Small = new("Segoe UI", 8.5f), Brand = new("Segoe UI", 12f, FontStyle.Bold);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Sötét ablak, sötét címsorral (ahol a Windows tudja; máshol marad a világos címsor, az ablak sötét).</summary>
    public static void Apply(Form f)
    {
        f.BackColor = Bg; f.ForeColor = Ink; f.Font = Base;
        try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        f.HandleCreated += (_, _) =>
        {
            var on = 1;
            try { if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4); } catch { }
        };
    }

    public static Button Btn(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Padding = new Padding(10, 3, 10, 3),
            BackColor = primary ? Accent : Panel2, ForeColor = primary ? Color.White : Ink, Font = primary ? Bold : Base, Margin = new Padding(0, 0, 8, 0)
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0x74, 0x9e, 0xff) : Line;
        b.FlatAppearance.MouseDownBackColor = primary ? Accent2 : Panel;
        return b;
    }

    public static TextBox Box(string placeholder, int width, bool password = false) => new()
    {
        PlaceholderText = placeholder, Width = width, BackColor = Panel2, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
        UseSystemPasswordChar = password, Margin = new Padding(0, 2, 8, 0), Font = Base
    };

    public static Label Text(string text, Font? font = null, Color? color = null, int maxWidth = 0) => new()
    {
        Text = text, AutoSize = true, Font = font ?? Base, ForeColor = color ?? Ink, BackColor = Color.Transparent,
        MaximumSize = maxWidth > 0 ? new Size(maxWidth, 0) : Size.Empty, Margin = new Padding(0, 4, 8, 0)
    };

    /// <summary>Csak olvasható, sötét táblázat (a Fleet upd-table mintájára): soronként kijelölés, rácsvonal a vonalszínnel.</summary>
    public static DataGridView Grid(params (string Title, int Width)[] cols)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BorderStyle = BorderStyle.None,
            BackgroundColor = Panel, GridColor = Line, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles = false, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 32,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, RowTemplate = { Height = 30 }, Font = Base
        };
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Panel2, ForeColor = Muted, Font = Small, SelectionBackColor = Panel2, SelectionForeColor = Muted, Padding = new Padding(6, 0, 0, 0)
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Panel, ForeColor = Ink, SelectionBackColor = Color.FromArgb(0x24, 0x33, 0x5c), SelectionForeColor = Color.White, Padding = new Padding(6, 0, 0, 0)
        };
        foreach (var (t, w) in cols)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = t, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (w > 0) c.Width = w; else c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            g.Columns.Add(c);
        }
        return g;
    }

    /// <summary>Sötét helyi menü (a tálca-ikoné).</summary>
    public static void Style(ToolStrip m)
    {
        m.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        m.BackColor = Panel; m.ForeColor = Ink; m.Font = Base;
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Line;
        public override Color MenuItemSelected => Panel2;
        public override Color MenuItemSelectedGradientBegin => Panel2;
        public override Color MenuItemSelectedGradientEnd => Panel2;
        public override Color MenuItemPressedGradientBegin => Panel2;
        public override Color MenuItemPressedGradientEnd => Panel2;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }

    /// <summary>Üzenetablak a Fleet stílusában (a MessageBox világos maradna).</summary>
    public static bool Ask(IWin32Window? owner, string text, string title, bool question)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = owner is null,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(18)
        };
        Apply(f);
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Bg };
        p.Controls.Add(Text(text, maxWidth: 420));
        var btns = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0), BackColor = Bg };
        var ok = Btn(question ? "Igen" : "Rendben", true); ok.DialogResult = DialogResult.OK; btns.Controls.Add(ok);
        if (question) { var no = Btn("Mégse"); no.DialogResult = DialogResult.Cancel; btns.Controls.Add(no); f.CancelButton = no; }
        p.Controls.Add(btns);
        f.Controls.Add(p); f.AcceptButton = ok;
        return f.ShowDialog(owner) == DialogResult.OK;
    }

    public static Color StateColor(string state) => state switch
    {
        "installed" => Ok, "failed" => Err, "denied" => Muted, "pending" => Warn, _ => Accent
    };
}

/// <summary>A fő ablak (Fleet-oldal): fejléc, bal oldali menü (jóváhagyott programok / program kérése / kéréseim), tartalom.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayMainForm : Form
{
    private readonly TrayAdmin _admin;
    private readonly Button[] _nav = new Button[3];
    private readonly Panel[] _pages = new Panel[3];
    private int _page = -1;
    public bool ForceClose;

    private readonly DataGridView _repo = TrayTheme.Grid(("Program", 0), ("Kategória", 170), ("Állapot", 120));
    private readonly TextBox _repoFilter = TrayTheme.Box("Szűrés…", 240);
    private readonly Button _repoInstall = TrayTheme.Btn("Telepítés", true);
    private readonly DataGridView _found = TrayTheme.Grid(("Program", 0), ("Azonosító", 220), ("Verzió", 90), ("", 100));
    private readonly TextBox _q = TrayTheme.Box("Program neve (pl. Lenovo, Zoom, 7-Zip)…", 320);
    private readonly Button _search = TrayTheme.Btn("Keresés");
    private readonly TextBox _reason = TrayTheme.Box("Miért kell? (a rendszergazda látja)", 380);
    private readonly Button _ask = TrayTheme.Btn("Kérés küldése", true);
    private readonly DataGridView _mine = TrayTheme.Grid(("Program", 0), ("Kérve", 130), ("Állapot", 150), ("Megjegyzés", 220));
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0),
        BackColor = TrayTheme.Panel, ForeColor = TrayTheme.Muted, Font = TrayTheme.Small };
    private readonly Label _who = TrayTheme.Text("", TrayTheme.Small, TrayTheme.Muted);
    private readonly Label _badge = new() { AutoSize = true, Font = TrayTheme.Small, ForeColor = TrayTheme.Warn, Padding = new Padding(6, 2, 6, 2), Visible = false };
    private List<(string Id, string Name, string Category, bool Installed)> _repoItems = new();

    private static readonly Dictionary<string, string> States = new()
    {
        ["pending"] = "döntésre vár", ["installing"] = "jóváhagyva – települ", ["installed"] = "települt",
        ["failed"] = "nem sikerült", ["denied"] = "elutasítva", ["approved"] = "jóváhagyva"
    };

    public TrayMainForm(TrayAdmin admin)
    {
        _admin = admin;
        Text = "Fleet – programok";
        Size = new Size(940, 600); MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterScreen;
        TrayTheme.Apply(this);

        // ---- fejléc (mint a Fleet topbar): ◆ Fleet · Programok, jobbra a gép és az admin-jelzés
        var head = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = TrayTheme.Panel, Padding = new Padding(16, 0, 16, 0) };
        var brand = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = TrayTheme.Panel, Padding = new Padding(0, 15, 0, 0) };
        brand.Controls.Add(new Label { Text = "◆", AutoSize = true, ForeColor = TrayTheme.Accent, Font = TrayTheme.Brand, Margin = new Padding(0, 0, 6, 0) });
        brand.Controls.Add(new Label { Text = "Fleet", AutoSize = true, ForeColor = TrayTheme.Ink, Font = TrayTheme.Brand, Margin = new Padding(0) });
        brand.Controls.Add(new Label { Text = "Programok", AutoSize = true, ForeColor = TrayTheme.Accent, Font = TrayTheme.Brand, Margin = new Padding(4, 0, 0, 0) });
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = TrayTheme.Panel, Padding = new Padding(0, 17, 0, 0) };
        right.Controls.Add(_badge); right.Controls.Add(_who);
        head.Controls.Add(right); head.Controls.Add(brand);
        var headLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = TrayTheme.Line };

        // ---- bal oldali menü (mint a Fleet page-menu)
        var menu = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = TrayTheme.Panel, Padding = new Padding(10, 12, 10, 12) };
        var menuLine = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = TrayTheme.Line };
        var labels = new[] { "Jóváhagyott programok", "Program kérése", "Kéréseim" };
        for (var i = labels.Length - 1; i >= 0; i--)
        {
            var k = i;
            var b = new Button
            {
                Text = "  " + labels[i], Dock = DockStyle.Top, Height = 38, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                BackColor = TrayTheme.Panel, ForeColor = TrayTheme.Ink, Cursor = Cursors.Hand, Font = TrayTheme.Base
            };
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = TrayTheme.Panel2;
            b.Click += async (_, _) => await Go(k);
            _nav[i] = b;
            menu.Controls.Add(b);
            menu.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4, BackColor = TrayTheme.Panel });
        }

        // ---- oldalak
        var content = new Panel { Dock = DockStyle.Fill, BackColor = TrayTheme.Bg, Padding = new Padding(20, 16, 20, 12) };
        _pages[0] = Page("Jóváhagyott programok", "A rendszergazda által jóváhagyott programok – kérés nélkül, egy kattintással telepíthetők.",
            Bar(_repoFilter, _repoInstall), _repo, null);
        _pages[1] = Page("Program kérése", "Keress a teljes winget-tárban; a kérést a rendszergazda bírálja el, jóváhagyás után magától települ.",
            Bar(_q, _search), _found, Bar(TrayTheme.Text("Indoklás:", color: TrayTheme.Muted), _reason, _ask));
        var refresh = TrayTheme.Btn("Frissítés");
        _pages[2] = Page("Kéréseim", "A kéréseid és az állapotuk. Értesítést kapsz, ha jóváhagyták, települt vagy elutasították.", Bar(refresh), _mine, null);
        foreach (var p in _pages) { p.Visible = false; content.Controls.Add(p); }

        Controls.Add(content); Controls.Add(menuLine); Controls.Add(menu); Controls.Add(_status); Controls.Add(headLine); Controls.Add(head);

        _repoFilter.TextChanged += (_, _) => FillRepo();
        _repoInstall.Click += async (_, _) => await InstallRepo();
        _repo.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await InstallRepo(); };
        _search.Click += async (_, _) => await Search();
        _q.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Search(); } };
        _ask.Click += async (_, _) => await Ask();
        refresh.Click += async (_, _) => await LoadMine();
        AdminChanged();
    }

    private static Panel Page(string title, string sub, Control bar, Control grid, Control? bottom)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = TrayTheme.Bg };
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = TrayTheme.Bg };
        head.Controls.Add(TrayTheme.Text(title, TrayTheme.H2));
        head.Controls.Add(TrayTheme.Text(sub, TrayTheme.Small, TrayTheme.Muted, 640));
        var gap = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = TrayTheme.Bg };
        // a táblázat kerete (a Fleet panel-kártyája)
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = TrayTheme.Line, Padding = new Padding(1) };
        frame.Controls.Add(grid);
        p.Controls.Add(frame);
        if (bottom is not null) { bottom.Dock = DockStyle.Bottom; p.Controls.Add(bottom); }
        bar.Dock = DockStyle.Top;
        p.Controls.Add(bar); p.Controls.Add(gap); p.Controls.Add(head);
        return p;
    }

    private static FlowLayoutPanel Bar(params Control[] c)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = TrayTheme.Bg, Padding = new Padding(0, 4, 0, 10) };
        p.Controls.AddRange(c);
        return p;
    }

    public void AdminChanged()
    {
        if (IsDisposed) return;
        _ask.Text = _admin.Active ? "Telepítés most (admin)" : "Kérés küldése";
        _reason.Enabled = !_admin.Active;
        _badge.Text = _admin.Active ? $"admin: {_admin.User}" : "";
        _badge.Visible = _admin.Active;
        _who.Text = $"{Environment.MachineName} · {Environment.UserName}";
    }

    public void ShowTab(int i)
    {
        AdminChanged();
        Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _ = Go(i);
    }

    private async Task Go(int i)
    {
        for (var k = 0; k < _pages.Length; k++)
        {
            _pages[k].Visible = k == i;
            _nav[k].BackColor = k == i ? TrayTheme.Panel2 : TrayTheme.Panel;
            _nav[k].ForeColor = k == i ? Color.White : TrayTheme.Ink;
            _nav[k].Font = k == i ? TrayTheme.Bold : TrayTheme.Base;
        }
        _page = i;
        if (i == 0) await LoadRepo();
        else if (i == 2) await LoadMine();
        else _q.Focus();
    }

    private void Busy(string text) { _status.Text = text; UseWaitCursor = text.EndsWith("…"); }

    private static string? Selected(DataGridView g) => g.SelectedRows.Count > 0 ? g.SelectedRows[0].Tag as string : null;

    private async Task LoadRepo()
    {
        Busy("Betöltés…");
        var r = await TrayPipe.Call("catalog");
        if (!r.Ok) { Busy(r.Message); return; }
        _repoItems = TrayPipe.Items(r.Data).Select(i => (TrayPipe.S(i, "id"), TrayPipe.S(i, "name"), TrayPipe.S(i, "category"), TrayPipe.B(i, "installed"))).ToList();
        FillRepo();
        Busy(_repoItems.Count == 0 ? "Még nincs jóváhagyott program – a Program kérése oldalon kérhetsz." : $"{_repoItems.Count} jóváhagyott program.");
    }

    private void FillRepo()
    {
        var q = _repoFilter.Text.Trim();
        _repo.Rows.Clear();
        foreach (var (id, name, cat, inst) in _repoItems.Where(x => q.Length == 0 || x.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || x.Category.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
        {
            var row = _repo.Rows[_repo.Rows.Add(name, cat, inst ? "telepítve" : "")];
            row.Tag = id;
            if (inst) row.Cells[2].Style.ForeColor = TrayTheme.Ok;
        }
    }

    private async Task InstallRepo()
    {
        if (Selected(_repo) is not { } id) { Busy("Válassz programot."); return; }
        var name = _repo.SelectedRows[0].Cells[0].Value?.ToString() ?? id;
        if (!TrayTheme.Ask(this, $"Telepíted ezt: {name}?", "Telepítés", true)) return;
        Busy("Küldés…");
        var r = await TrayPipe.Call("install", new { id });
        Busy(r.Message.Length > 0 ? r.Message : r.Ok ? "Elindult." : "Nem sikerült.");
    }

    private async Task Search()
    {
        var q = _q.Text.Trim();
        if (q.Length < 2) { Busy("Legalább 2 betű kell."); return; }
        Busy("Keresés…");
        var r = await TrayPipe.Call("search", new { q });
        _found.Rows.Clear();
        foreach (var i in TrayPipe.Items(r.Data))
        {
            var inRepo = TrayPipe.B(i, "inRepo");
            var row = _found.Rows[_found.Rows.Add(TrayPipe.S(i, "name"), TrayPipe.S(i, "id"), TrayPipe.S(i, "version"), inRepo ? "jóváhagyott" : "")];
            row.Tag = TrayPipe.S(i, "id");
            row.Cells[1].Style.ForeColor = TrayTheme.Muted;
            if (inRepo) row.Cells[3].Style.ForeColor = TrayTheme.Ok;
        }
        Busy(!r.Ok ? r.Message : _found.Rows.Count == 0 ? "Nincs találat (a Microsoft Store programjai nincsenek a tárban)." : $"{_found.Rows.Count} találat – válassz, és küldd el a kérést.");
    }

    private async Task Ask()
    {
        if (Selected(_found) is not { } id) { Busy("Válassz programot a listából."); return; }
        var name = _found.SelectedRows[0].Cells[0].Value?.ToString() ?? id;
        if (_admin.Active)
        {
            if (!TrayTheme.Ask(this, $"Telepíted most ezt (admin): {name}?", "Telepítés", true)) return;
            Busy("Küldés…");
            var a = await TrayPipe.Call("admin-install", new { token = _admin.Token, id, name });
            if (!a.Ok && a.Message.Contains("lejárt")) { _admin.Clear(); AdminChanged(); }
            Busy(a.Message);
            return;
        }
        Busy("Küldés…");
        var r = await TrayPipe.Call("request", new { id, name, reason = _reason.Text.Trim() });
        Busy(r.Message.Length > 0 ? r.Message : r.Ok ? "Elküldve." : "Nem sikerült.");
        if (r.Ok) _reason.Clear();
    }

    private async Task LoadMine()
    {
        Busy("Betöltés…");
        var r = await TrayPipe.Call("requests");
        _mine.Rows.Clear();
        foreach (var i in TrayPipe.Items(r.Data))
        {
            var st = TrayPipe.S(i, "state");
            var row = _mine.Rows[_mine.Rows.Add(TrayPipe.S(i, "name"), TrayPipe.S(i, "created"), States.GetValueOrDefault(st, st), TrayPipe.S(i, "note"))];
            row.Cells[2].Style.ForeColor = TrayTheme.StateColor(st);
            row.Cells[1].Style.ForeColor = TrayTheme.Muted;
        }
        Busy(!r.Ok ? r.Message : _mine.Rows.Count == 0 ? "Még nincs kérésed." : $"{_mine.Rows.Count} kérés.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // A tálca-alkalmazás tovább fut; az ablak csak elrejtődik (újranyitáskor gyors).
        if (!ForceClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}

/// <summary>„Belépés adminként”: a jelszóból itt számoljuk a kulcsot (PBKDF2, a szerver sójával) – a jelszó nem megy át.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayLoginForm : Form
{
    private readonly TextBox _user = TrayTheme.Box("", 280);
    private readonly TextBox _pw = TrayTheme.Box("", 280, password: true);
    private readonly Label _msg = TrayTheme.Text("", TrayTheme.Small, TrayTheme.Err, 300);
    private readonly Button _ok = TrayTheme.Btn("Belépés", true);
    public string Token = "", User = "";
    public int Minutes = 15;

    public TrayLoginForm()
    {
        Text = "Belépés adminként";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(22, 18, 22, 18);
        TrayTheme.Apply(this);
        var cancel = TrayTheme.Btn("Mégse"); cancel.DialogResult = DialogResult.Cancel;
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = TrayTheme.Bg };
        var brand = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = TrayTheme.Bg, Margin = new Padding(0, 0, 0, 6) };
        brand.Controls.Add(new Label { Text = "◆", AutoSize = true, ForeColor = TrayTheme.Accent, Font = TrayTheme.Brand, Margin = new Padding(0, 0, 6, 0) });
        brand.Controls.Add(new Label { Text = "Fleet", AutoSize = true, ForeColor = TrayTheme.Ink, Font = TrayTheme.Brand, Margin = new Padding(0) });
        brand.Controls.Add(new Label { Text = "Belépés", AutoSize = true, ForeColor = TrayTheme.Accent, Font = TrayTheme.Brand, Margin = new Padding(4, 0, 0, 0) });
        p.Controls.Add(brand);
        p.Controls.Add(TrayTheme.Text("A Fleet kezelői vagy admin fiókja. A jelszó nem megy át a hálón: a gép csak egy belőle számolt, egyszer használható választ küld.", TrayTheme.Small, TrayTheme.Muted, 300));
        p.Controls.Add(Lbl("Felhasználónév")); p.Controls.Add(_user);
        p.Controls.Add(Lbl("Jelszó")); p.Controls.Add(_pw);
        p.Controls.Add(_msg);
        var btns = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0), BackColor = TrayTheme.Bg };
        btns.Controls.Add(_ok); btns.Controls.Add(cancel);
        p.Controls.Add(btns);
        Controls.Add(p);
        AcceptButton = _ok; CancelButton = cancel;
        _ok.Click += async (_, _) => await DoLogin();
    }

    private static Label Lbl(string t) { var l = TrayTheme.Text(t, TrayTheme.Small, TrayTheme.Muted); l.Margin = new Padding(0, 10, 0, 2); return l; }

    private async Task DoLogin()
    {
        var user = _user.Text.Trim(); var pw = _pw.Text;
        if (user.Length < 3 || pw.Length == 0) { Fail("Add meg a felhasználónevet és a jelszót."); return; }
        _ok.Enabled = false; _msg.ForeColor = TrayTheme.Muted; _msg.Text = "Belépés…";
        try
        {
            var s = await TrayPipe.Call("login-start", new { username = user });
            if (!s.Ok) { Fail(s.Message); return; }
            var salt = TrayPipe.S(s.Data, "salt"); var ch = TrayPipe.S(s.Data, "challenge");
            var iter = s.Data.TryGetProperty("iterations", out var it) && it.TryGetInt32(out var n) ? n : 210_000;
            if (iter is < 10_000 or > 5_000_000) { Fail("Hibás válasz a szervertől."); return; }
            // A válasz a gép agent-azonosítójához kötött (a kihívás erre a gépre szól) – a szerver aláírt válaszában jön.
            var agentId = TrayPipe.S(s.Data, "agentId");
            var proof = await Task.Run(() => TrayLogin.Proof(TrayLogin.Key(pw, salt, iter), agentId, user, ch));
            var r = await TrayPipe.Call("login", new { username = user, challenge = ch, proof });
            if (!r.Ok) { Fail(r.Message); return; }
            Token = TrayPipe.S(r.Data, "token"); User = TrayPipe.S(r.Data, "user");
            Minutes = r.Data.TryGetProperty("minutes", out var m) && m.TryGetInt32(out var mm) ? mm : 15;
            DialogResult = DialogResult.OK;
            Close();
        }
        finally { if (!IsDisposed) _ok.Enabled = true; _pw.Clear(); }
    }

    private void Fail(string m) { _msg.ForeColor = TrayTheme.Err; _msg.Text = m.Length > 0 ? m : "Nem sikerült."; }
}

/// <summary>Adminnak: a gép döntésre váró kérései.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayApprovalsForm : Form
{
    private readonly TrayAdmin _admin;
    private readonly DataGridView _list = TrayTheme.Grid(("Program", 0), ("Kérte", 170), ("Kérve", 120), ("Indoklás", 220));
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0),
        BackColor = TrayTheme.Panel, ForeColor = TrayTheme.Muted, Font = TrayTheme.Small };

    public TrayApprovalsForm(TrayAdmin admin)
    {
        _admin = admin;
        Text = "Döntésre váró kérések – " + Environment.MachineName;
        Size = new Size(820, 440); StartPosition = FormStartPosition.CenterScreen;
        TrayTheme.Apply(this);
        var ok = TrayTheme.Btn("Jóváhagyás", true); var no = TrayTheme.Btn("Elutasítás");
        var body = new Panel { Dock = DockStyle.Fill, BackColor = TrayTheme.Bg, Padding = new Padding(20, 14, 20, 12) };
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = TrayTheme.Bg };
        head.Controls.Add(TrayTheme.Text("Döntésre váró kérések", TrayTheme.H2));
        head.Controls.Add(TrayTheme.Text("Ezen a gépen kért programok. A jóváhagyott program a következő jelentkezéskor magától települ.", TrayTheme.Small, TrayTheme.Muted, 640));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = TrayTheme.Bg, Padding = new Padding(0, 10, 0, 10) };
        bar.Controls.Add(ok); bar.Controls.Add(no);
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = TrayTheme.Line, Padding = new Padding(1) };
        frame.Controls.Add(_list);
        body.Controls.Add(frame); body.Controls.Add(bar); body.Controls.Add(head);
        Controls.Add(body); Controls.Add(_status);
        ok.Click += async (_, _) => await Decide(true);
        no.Click += async (_, _) => await Decide(false);
        Shown += async (_, _) => await Reload();
    }

    private async Task Reload()
    {
        if (!_admin.Active) { _status.Text = "A belépés lejárt – lépj be újra."; return; }
        var r = await TrayPipe.Call("admin-requests", new { token = _admin.Token });
        _list.Rows.Clear();
        foreach (var i in TrayPipe.Items(r.Data))
            _list.Rows[_list.Rows.Add(TrayPipe.S(i, "name"), TrayPipe.S(i, "user"), TrayPipe.S(i, "created"), TrayPipe.S(i, "reason"))].Tag = TrayPipe.S(i, "id");
        _status.Text = !r.Ok ? r.Message : _list.Rows.Count == 0 ? "Nincs döntésre váró kérés ezen a gépen." : $"{_list.Rows.Count} kérés.";
    }

    private async Task Decide(bool approve)
    {
        if (_list.SelectedRows.Count == 0 || _list.SelectedRows[0].Tag is not string id) return;
        var r = await TrayPipe.Call("admin-decide", new { token = _admin.Token, id, approve });
        _status.Text = r.Message;
        await Reload();
    }
}
