using System.Drawing.Drawing2D;

namespace DevToolbox;

public enum ModeChoice { Cancel, Install, Portable }

/// <summary>
/// Elso inditaskor jelenik meg: telepitett vagy hordozhato mod.
/// Az elrendezes vegig AutoSize/TableLayout alapu, hogy mas betumeretnel es DPI-nel
/// se vagodjon le a szoveg.
/// </summary>
public sealed class ModeChooserForm : Form
{
    public ModeChoice Choice { get; private set; } = ModeChoice.Cancel;
    public bool DesktopShortcut => _chkDesktop.Checked;
    public bool StartMenuShortcut => _chkStartMenu.Checked;

    /// <summary>Hordozhato modban a felhasznalo valasztotta-e az emelt jogu futtatast.</summary>
    public bool PortableAsAdmin => _rbPortableAdmin.Checked;

    private readonly CheckBox _chkDesktop = new();
    private readonly CheckBox _chkStartMenu = new();
    private readonly RadioButton _rbPortableNormal = new();
    private readonly RadioButton _rbPortableAdmin = new();

    private const int BodyWidth = 470;

    public ModeChooserForm()
    {
        Text = AppPaths.DisplayName + " – indítási mód";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.White
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ---------------- fejlec
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Color.FromArgb(28, 22, 48),
            Padding = new Padding(20, 14, 20, 14),
            Margin = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var headerLogo = Branding.LogoAt(52);
        if (headerLogo is not null)
        {
            var pb = new PictureBox
            {
                Image = headerLogo,
                SizeMode = PictureBoxSizeMode.AutoSize,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 2, 16, 0)
            };
            header.Controls.Add(pb, 0, 0);
            header.SetRowSpan(pb, 2);
        }
        header.Controls.Add(new Label
        {
            Text = "Hogyan szeretnéd használni a programot?",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 13F),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "A választás később is módosítható a Program menüből.",
            ForeColor = Color.FromArgb(178, 168, 210),
            Font = new Font("Segoe UI", 9F),
            AutoSize = true,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        });

        // ---------------- telepites
        _chkStartMenu.Text = "Start menü";
        _chkStartMenu.Checked = true;
        _chkStartMenu.AutoSize = true;
        _chkStartMenu.Font = Font;
        _chkDesktop.Text = "Asztal";
        _chkDesktop.Checked = true;
        _chkDesktop.AutoSize = true;
        _chkDesktop.Font = Font;
        _chkDesktop.Margin = new Padding(18, 3, 3, 3);

        var extras = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 0),
            BackColor = Color.Transparent
        };
        extras.Controls.Add(_chkStartMenu);
        extras.Controls.Add(_chkDesktop);

        var btnInstall = MakeButton("Telepítés", Color.FromArgb(0, 120, 212));
        btnInstall.Click += (_, _) => { Choice = ModeChoice.Install; DialogResult = DialogResult.OK; };

        var cardInstall = MakeCard(
            "Telepítés erre a gépre",
            "Bemásolja magát a felhasználói profilba és parancsikont készít. Megjelenik a " +
            "Windows programlistájában is, ahonnan bármikor eltávolítható. Rendszergazdai " +
            "jog a telepítéshez nem szükséges, de a program utána mindig rendszergazdaként indul.",
            "Helye:  " + AppPaths.InstallDir,
            btnInstall, extras, Color.FromArgb(0, 120, 212));

        // ---------------- hordozhato
        var btnPortable = MakeButton("Hordozható indítás", Color.FromArgb(16, 137, 62));
        btnPortable.Click += (_, _) => { Choice = ModeChoice.Portable; DialogResult = DialogResult.OK; };

        _rbPortableNormal.Text = "Normál futtatás";
        _rbPortableNormal.Checked = true;
        _rbPortableNormal.AutoSize = true;
        _rbPortableNormal.Font = Font;

        _rbPortableAdmin.Text = "Futtatás rendszergazdaként";
        _rbPortableAdmin.AutoSize = true;
        _rbPortableAdmin.Font = Font;
        _rbPortableAdmin.Margin = new Padding(18, 3, 3, 3);

        var runMode = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 0),
            BackColor = Color.Transparent
        };
        runMode.Controls.Add(_rbPortableNormal);
        runMode.Controls.Add(_rbPortableAdmin);

        var cardPortable = MakeCard(
            "Hordozható futtatás (telepítés nélkül)",
            "Semmi nem kerül a rendszerbe: se registry-bejegyzés, se parancsikon. A szkriptek " +
            "és a beállítások az exe melletti „Data” mappába kerülnek, így pendrive-ról is vihető. " +
            "Rendszergazdaként a hőmérséklet, a SMART és a biztonsági napló is olvasható.",
            "Helye:  " + Path.Combine(AppPaths.ExeDir, "Data"),
            btnPortable, runMode, Color.FromArgb(16, 137, 62));

        // ---------------- megse
        var btnCancel = new Button
        {
            Text = "Mégse",
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(96, 30),
            BackColor = Color.White,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 10, 20, 16)
        };
        btnCancel.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        btnCancel.Click += (_, _) => { Choice = ModeChoice.Cancel; DialogResult = DialogResult.Cancel; };

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0)
        };
        bottom.Controls.Add(btnCancel);

        root.Controls.Add(header);
        root.Controls.Add(cardInstall);
        root.Controls.Add(cardPortable);
        root.Controls.Add(bottom);

        Controls.Add(root);
        CancelButton = btnCancel;
    }

    private Button MakeButton(string text, Color back)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(178, 38),
            Padding = new Padding(10, 4, 10, 4),
            Font = new Font("Segoe UI Semibold", 9.5F),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(24, 4, 0, 0)
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    /// <summary>Egy kartya: bal oldalt cim + szoveg + extra vezerlok, jobb oldalt a gomb.</summary>
    private Panel MakeCard(string title, string body, string location,
                           Button action, Control? extras, Color accent)
    {
        // Dock=Fill + a szulo oszlop Percent(100): igy MINDKET kartya ugyanolyan
        // szeles lesz. AutoSize nelkul kulon-kulon a sajat tartalmukhoz igazodnanak,
        // es a hosszabb szovegu kartya szelesebb lenne - ez volt az elcsuszas oka.
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.FromArgb(250, 250, 252),
            Padding = new Padding(18, 14, 18, 16),
            Margin = new Padding(20, 14, 20, 0)
        };

        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(224, 227, 233));
            g.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            using var br = new SolidBrush(accent);
            g.FillRectangle(br, 0, 0, 4, card.Height);
        };

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        };

        left.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = Color.FromArgb(28, 32, 40),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            BackColor = Color.Transparent
        });

        left.Controls.Add(new Label
        {
            Text = body,
            Font = Font,
            ForeColor = Color.FromArgb(86, 94, 108),
            AutoSize = true,
            MaximumSize = new Size(BodyWidth, 0),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent
        });

        // Az utvonal soha ne torjon szo kozben: fix szelesseg + automatikus harompont.
        var pathLabel = new Label
        {
            Text = location,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(120, 128, 142),
            AutoSize = false,
            AutoEllipsis = true,
            Width = BodyWidth,
            Height = 19,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        };
        var tip = new ToolTip();
        tip.SetToolTip(pathLabel, location);
        left.Controls.Add(pathLabel);

        if (extras is not null) left.Controls.Add(extras);

        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(action, 1, 0);
        card.Controls.Add(grid);

        return card;
    }
}
