using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DevToolbox;

internal static class Program
{
    private const string NoElevateFlag = "--noelevate";

    [STAThread]
    private static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowCrash(e.ExceptionObject as Exception);

        AppPaths.Detect();

        // --- Eltavolitas (a Windows "Alkalmazasok" listajabol hivva) ---
        if (Has(args, "--uninstall")) { RunUninstall(Has(args, "--quiet")); return; }

        // --- Elso inditas: mod valasztasa ---
        if (AppPaths.Mode == AppMode.NotConfigured)
        {
            if (!FirstRun()) return;
        }

        AppPaths.EnsureDataDirs();
        AppSettings.Load();

        // Egyetlen peldany fusson: a talcan futo valtozat mellett egy masodik
        // ket parhuzamos figyelokort jelentene ugyanazokra a gepekre.
        using var single = new Mutex(true, @"Local\DevToolbox.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "A DevToolbox már fut – nézd meg a tálca értesítési területén.\n" +
                "Dupla kattintás az ikonon, és előjön az ablak.",
                AppPaths.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // --- Rendszergazdai inditas, ha a beallitas ezt keri ---
        if (AppSettings.Current.AlwaysRunAsAdmin
            && !HardwareInfo.IsElevated()
            && !Has(args, NoElevateFlag))
        {
            if (!TryElevate()) return;   // sikeres emeles -> az uj peldany fut tovabb
        }

        Application.Run(new MainForm());
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    // ================================================================ elso inditas

    private static bool FirstRun()
    {
        using var chooser = new ModeChooserForm();
        if (chooser.ShowDialog() != DialogResult.OK) return false;

        if (chooser.Choice == ModeChoice.Install)
        {
            try
            {
                var exe = Installer.Install(
                    chooser.DesktopShortcut, chooser.StartMenuShortcut, runAsAdmin: true);

                // A beallitasokat mar a telepitett hely adatmappajaba irjuk.
                AppPaths.Detect();
                if (AppPaths.Mode == AppMode.NotConfigured)
                {
                    // Meg a hordozhato peldanybol futunk; a telepitett adatmappat kezzel keszitjuk elo.
                    var appData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        AppPaths.AppName);
                    Directory.CreateDirectory(appData);
                    var settings = new AppSettings { AlwaysRunAsAdmin = true };
                    File.WriteAllText(Path.Combine(appData, "settings.json"),
                        System.Text.Json.JsonSerializer.Serialize(settings,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }

                MessageBox.Show(
                    "A telepítés kész.\n\n" +
                    "Helye:\n" + AppPaths.InstallDir + "\n\n" +
                    "Adatok:\n" + Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        AppPaths.AppName) + "\n\n" +
                    "A program mostantól mindig rendszergazdaként indul, hogy a hőmérséklet-, " +
                    "SMART- és biztonsági napló adatok is olvashatók legyenek.",
                    "Telepítés kész", MessageBoxButtons.OK, MessageBoxIcon.Information);

                StartElevated(exe);
            }
            catch (Exception ex)
            {
                MessageBox.Show("A telepítés nem sikerült:\n\n" + ex.Message,
                    "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;   // az uj peldany veszi at
        }

        // ---- hordozhato mod
        AppPaths.SetPortable();
        AppPaths.EnsureDataDirs();

        var s2 = new AppSettings { AlwaysRunAsAdmin = chooser.PortableAsAdmin };
        AppSettings.Replace(s2);

        if (chooser.PortableAsAdmin && !HardwareInfo.IsElevated())
        {
            if (!TryElevate()) return false;
        }

        return true;
    }

    // ================================================================ jogosultsag

    /// <summary>
    /// Ujrainditas emelt joggal. true = folytathatjuk ebben a peldanyban,
    /// false = az uj (emelt) peldany fut tovabb, vagy a felhasznalo kilepett.
    /// </summary>
    private static bool TryElevate()
    {
        try
        {
            StartElevated(AppPaths.ExePath);
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // UAC megszakitva
        {
            var r = MessageBox.Show(
                "A rendszergazdai indítást elutasítottad.\n\n" +
                "Folytassam korlátozott jogosultsággal? Ebben az esetben a hőmérséklet-, " +
                "SMART- és biztonsági napló adatok egy része nem lesz olvasható.",
                AppPaths.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            return r == DialogResult.Yes;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Nem sikerült rendszergazdaként indulni:\n\n" + ex.Message +
                            "\n\nA program korlátozott jogosultsággal fut tovább.",
                AppPaths.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }
    }

    private static void StartElevated(string exePath)
    {
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
        });
    }

    // ================================================================ eltavolitas

    private static void RunUninstall(bool quiet)
    {
        bool removeData = false;
        if (!quiet)
        {
            var r = MessageBox.Show(
                "Biztosan eltávolítod a " + AppPaths.DisplayName + " programot?",
                "Eltávolítás", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            removeData = MessageBox.Show(
                "Töröljem a szkripteket és a beállításokat is?\n\n" +
                "Nem  = az adatok megmaradnak a %AppData% mappában.",
                "Adatok törlése", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        Installer.Uninstall(removeData);

        if (!quiet)
            MessageBox.Show("Az eltávolítás megtörtént.", AppPaths.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void ShowCrash(Exception? ex)
    {
        try
        {
            MessageBox.Show("Váratlan hiba történt:\n\n" + (ex?.ToString() ?? "ismeretlen"),
                "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
