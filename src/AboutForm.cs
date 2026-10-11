using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public sealed class AboutForm : Form
{
    private sealed record Lib(string Name, string Version, string License, string Purpose);

    private static readonly Lib[] Libraries =
    {
        new(".NET / Windows Forms", "10.0", "MIT",
            "A program futtatókörnyezete és felhasználói felülete. Az exe-be fordítva, " +
            "ezért nem kell külön runtime a célgépre."),
        new("LibreHardwareMonitorLib", "0.9.6", "MPL 2.0",
            "Hőmérséklet, terhelés, ventilátor-fordulatszám olvasása, valamint NVMe " +
            "meghajtók élettartam- és hőadatai. Kernel szintű hozzáférést használ, " +
            "ezért rendszergazdai jog kell hozzá."),
        new("System.Management (WMI)", "10.0", "MIT",
            "Hardverleltár, fizikai lemezek, SATA SMART attribútumok, licenckulcsok, " +
            "távoli gépek lekérdezése és a távoli registry olvasása."),
        new("System.Diagnostics.EventLog", "10.0", "MIT",
            "A Windows eseménynapló olvasása XPath szűréssel, helyben és távoli " +
            "munkameneten keresztül is."),
        new("System.Net.NetworkInformation", "beépített", "MIT",
            "Hálózati adapterek, IP-beállítások, aktív kapcsolatok és figyelt portok."),
        new("Microsoft.Win32.Registry", "beépített", "MIT",
            "Telepített szoftverek listája, videokártya valódi VRAM mérete, a program " +
            "eltávolítási bejegyzése és a termékkulcs azonosítója."),
        new("WScript.Shell (COM)", "Windows", "—",
            "Parancsikon készítése telepítéskor. A Windows saját összetevője, " +
            "nincs külön telepítve."),
    };

    private static readonly (string Title, string Text)[] Requirements =
    {
        ("Operációs rendszer",
         "Windows 10 vagy Windows 11, kizárólag 64 bites (x64). 32 bites Windowson nem indul el."),
        ("Futtatókörnyezet",
         "Nincs külön követelmény. A .NET futtatókörnyezet bele van fordítva az exe-be, " +
         "tehát telepítés nélküli gépen is elindul."),
        ("Jogosultság",
         "A program alapfunkciói normál joggal is működnek. Rendszergazdai jog kell a " +
         "hőmérséklet-érzékelőkhöz, a SMART attribútumokhoz és a Biztonság eseménynaplóhoz."),
        ("Szkriptek futtatása",
         "A batch és PowerShell szkriptekhez a Windows beépített értelmezői elegendők " +
         "(a PowerShell -ExecutionPolicy Bypass kapcsolóval indul). Python szkripthez a " +
         "python.exe kell a PATH-ban."),
        ("Távoli lekérdezés",
         "A célgépen: a megadott fiók legyen helyi rendszergazda; tűzfalon engedélyezve a " +
         "„Windows Management Instrumentation (WMI)” szabály, eseménynaplóhoz a „Remote " +
         "Event Log Management” szabály. Munkacsoportos gépnél szükséges a " +
         "LocalAccountTokenFilterPolicy = 1 registry érték."),
        ("Hely",
         "Az önálló exe kb. 49 MB. Az adatmappa mérete a tárolt szkriptektől függ."),
    };

    public AboutForm()
    {
        Text = "Névjegy – " + AppPaths.DisplayName;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        ClientSize = new Size(720, 640);
        MinimumSize = new Size(600, 480);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        // ---------------- fejléc
        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Color.FromArgb(28, 22, 48) };

        var logo = Branding.LogoAt(64);
        var textLeft = 20;
        if (logo is not null)
        {
            header.Controls.Add(new PictureBox
            {
                Image = logo,
                SizeMode = PictureBoxSizeMode.AutoSize,
                Location = new Point(20, 16),
                BackColor = Color.Transparent
            });
            textLeft = 100;
        }

        header.Controls.Add(new Label
        {
            Text = $"{AppPaths.DisplayName} {AppPaths.Version}  –  {AppPaths.Tagline}",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 14F),
            AutoSize = true,
            Location = new Point(textLeft, 20),
            BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "Szkriptkezelő, hardver- és lemezdiagnosztika, hálózati leltár, " +
                   "eseménynapló-elemzés és licenckulcs-kinyerés – helyben és hálózaton át.",
            ForeColor = Color.FromArgb(178, 168, 210),
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Location = new Point(textLeft + 2, 50),
            BackColor = Color.Transparent
        });

        // ---------------- tartalom
        var body = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = new Font("Segoe UI", 9F),
            ScrollBars = RichTextBoxScrollBars.Vertical
        };
        BuildContent(body);

        var bodyHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 14, 8) };
        bodyHost.Controls.Add(body);

        // ---------------- gombsor
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(14, 10, 14, 10) };

        var btnOk = new Button
        {
            Text = "OK",
            Dock = DockStyle.Right,
            Width = 100,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F)
        };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += (_, _) => Close();

        var btnCopy = new Button
        {
            Text = "Másolás vágólapra",
            Dock = DockStyle.Right,
            Width = 150,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White
        };
        btnCopy.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        btnCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(body.Text); } catch { }
        };

        var btnFolder = new Button
        {
            Text = "Adatmappa",
            Dock = DockStyle.Left,
            Width = 110,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White
        };
        btnFolder.FlatAppearance.BorderColor = Color.FromArgb(203, 207, 214);
        btnFolder.Click += (_, _) =>
        {
            try
            {
                AppPaths.EnsureDataDirs();
                Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
            }
            catch { }
        };

        bottom.Controls.Add(btnOk);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(btnCopy);
        bottom.Controls.Add(btnFolder);

        Controls.Add(bodyHost);
        Controls.Add(bottom);
        Controls.Add(header);

        AcceptButton = btnOk;
        CancelButton = btnOk;
    }

    /// <summary>Alá van-e írva a futó exe, es ha igen, ki altal.</summary>
    private static string SignatureState()
    {
        try
        {
            // A CreateFromSignedFile elavultnak jelolt, de az Authenticode alairo
            // kiolvasasara nincs mas kezelt API - a javasolt X509CertificateLoader
            // tanusitvanyfajlt tolt be, nem alairt PE fajlt.
#pragma warning disable SYSLIB0057
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate
                .CreateFromSignedFile(AppPaths.ExePath);
#pragma warning restore SYSLIB0057
            var subject = cert.Subject ?? "";
            var cn = subject.Split(',')
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
            return "aláírva – " + (cn is null ? subject : cn[3..]);
        }
        catch
        {
            return "nincs aláírva (a SmartScreen „ismeretlen kiadó”-ként jelzi)";
        }
    }

    private static void BuildContent(RichTextBox rtb)
    {
        void H(string t)
        {
            rtb.SelectionFont = new Font("Segoe UI Semibold", 11F);
            rtb.SelectionColor = Color.FromArgb(28, 32, 40);
            rtb.AppendText(t + "\n\n");
        }
        void Row(string k, string v)
        {
            rtb.SelectionFont = new Font("Segoe UI Semibold", 9F);
            rtb.SelectionColor = Color.FromArgb(70, 78, 92);
            rtb.AppendText(k + ":  ");
            rtb.SelectionFont = new Font("Segoe UI", 9F);
            rtb.SelectionColor = Color.FromArgb(40, 46, 58);
            rtb.AppendText(v + "\n");
        }
        void P(string t, Color? c = null)
        {
            rtb.SelectionFont = new Font("Segoe UI", 9F);
            rtb.SelectionColor = c ?? Color.FromArgb(60, 66, 78);
            rtb.AppendText(t + "\n");
        }
        void Gap() => rtb.AppendText("\n");

        // ---------------- kiadó
        H("Kiadó");
        Row("Készítette", AppPaths.PublisherLong);
        Row("Weboldal", AppPaths.Website);
        Row("Támogatás", AppPaths.SupportMail);
        Row("Digitális aláírás", SignatureState());
        Gap();

        // ---------------- mód és elérési utak
        H("Működési mód és elérési utak");

        Row("Mód", AppPaths.ModeLabel);
        Row("Jogosultság", HardwareInfo.IsElevated()
            ? "rendszergazda (minden adat olvasható)"
            : "normál felhasználó – a hőmérséklet, SMART és biztonsági napló korlátozott");
        Row("Indítás", AppSettings.Current.AlwaysRunAsAdmin
            ? "mindig rendszergazdaként"
            : "normál futtatás");
        Gap();

        Row(AppPaths.Mode == AppMode.Installed ? "Telepítve ide" : "Program helye", AppPaths.ExeDir);
        Row("Program fájl", AppPaths.ExePath);
        Row("Adatok", AppPaths.DataDir);
        Row("Szkriptek könyvtára", AppPaths.ScriptsDir);
        Row("Exportálás alapmappája", AppPaths.ExportDir);
        Row("Beállítások fájl", AppPaths.SettingsFile);
        Gap();

        P(AppPaths.Mode == AppMode.Portable
            ? "Hordozható mód: az exe melletti „portable.dat” fájl kapcsolja be. Ha törlöd, " +
              "a program a következő indításkor újra megkérdezi a módot."
            : "Telepített mód: a program a felhasználói profilba került, és a Windows " +
              "programlistájából eltávolítható.",
            Color.FromArgb(110, 117, 130));
        Gap();

        // ---------------- komponensek
        H("Használt komponensek");

        foreach (var lib in Libraries)
        {
            rtb.SelectionFont = new Font("Segoe UI Semibold", 9.5F);
            rtb.SelectionColor = Color.FromArgb(20, 60, 130);
            rtb.AppendText($"{lib.Name}  ");
            rtb.SelectionFont = new Font("Segoe UI", 8.5F);
            rtb.SelectionColor = Color.FromArgb(130, 137, 150);
            rtb.AppendText($"{lib.Version} · {lib.License}\n");
            P("    " + lib.Purpose);
            Gap();
        }

        P("A LibreHardwareMonitor MPL 2.0 licencű: ha a programot továbbadod vagy ügyfélnek " +
          "telepíted, ezt a licencet fel kell tüntetni. Ez a névjegy megteszi.",
          Color.FromArgb(110, 117, 130));
        Gap();

        // ---------------- követelmények
        H("Rendszerkövetelmények");

        foreach (var (title, text) in Requirements)
        {
            rtb.SelectionFont = new Font("Segoe UI Semibold", 9.5F);
            rtb.SelectionColor = Color.FromArgb(40, 46, 58);
            rtb.AppendText(title + "\n");
            P("    " + text);
            Gap();
        }

        // ---------------- gépadatok
        H("A futtató gép");
        Row("Gépnév", Environment.MachineName);
        Row("Operációs rendszer", Environment.OSVersion.VersionString);
        Row("Processzormagok", Environment.ProcessorCount.ToString());
        Row("Folyamat", Environment.Is64BitProcess ? "64 bites" : "32 bites");

        rtb.SelectionStart = 0;
        rtb.ScrollToCaret();
    }
}
