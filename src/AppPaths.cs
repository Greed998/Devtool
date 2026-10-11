using System.Diagnostics;

namespace DevToolbox;

public enum AppMode
{
    /// <summary>Meg nincs eldontve - elso inditas.</summary>
    NotConfigured,
    /// <summary>Hordozhato: minden adat az exe melletti Data mappaban.</summary>
    Portable,
    /// <summary>Telepitett: adatok a %AppData%\DevToolbox alatt.</summary>
    Installed
}

public static class AppPaths
{
    public const string AppName = "DevToolbox";
    public const string DisplayName = "DevToolbox";
    public const string Tagline = "IT Admin toolkit";
    public const string Version = "1.5.1";

    /// <summary>A kiado neve. Egyeznie kell a kodalairo tanusitvany subject nevevel.</summary>
    public const string Publisher = "Poltoli Informatika";
    public const string PublisherLong = "Pusztai Csaba e.v. – Poltoli Informatika";
    public const string Website = "https://poltoli.hu";
    public const string SupportMail = "info@poltoli.hu";

    /// <summary>Ennek a fajlnak a jelenlete kapcsolja hordozhato modba.</summary>
    public const string PortableMarkerName = "portable.dat";

    public static string ExePath { get; } =
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static string ExeDir { get; } =
        Path.GetDirectoryName(ExePath) is { Length: > 0 } d ? d : AppContext.BaseDirectory;

    public static string PortableMarkerPath => Path.Combine(ExeDir, PortableMarkerName);

    /// <summary>Ide telepit a program sajat magat (nem kell rendszergazda jog).</summary>
    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    public static string InstalledExePath => Path.Combine(InstallDir, AppName + ".exe");

    public static AppMode Mode { get; private set; } = AppMode.NotConfigured;

    /// <summary>Az adatok (szkriptek + beallitasok) gyokermappaja.</summary>
    public static string DataDir { get; private set; } = "";

    /// <summary>Az alapertelmezett szkriptmappa (ha a beallitasokban nincs mas megadva).</summary>
    public static string DefaultScriptsDir => Path.Combine(DataDir, "scripts");

    /// <summary>A tenylegesen hasznalt szkriptmappa - a beallitasokban atallithato.</summary>
    public static string ScriptsDir => AppSettings.Current.EffectiveScriptsFolder;

    public static string ExportDir => AppSettings.Current.EffectiveExportFolder;
    public static string LibraryFile => Path.Combine(DataDir, "library.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Eldonti, milyen modban futunk. Program indulasakor hivando.</summary>
    public static void Detect()
    {
        if (File.Exists(PortableMarkerPath))
        {
            Mode = AppMode.Portable;
            DataDir = Path.Combine(ExeDir, "Data");
        }
        else if (IsRunningFromInstallDir())
        {
            Mode = AppMode.Installed;
            DataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        }
        else
        {
            Mode = AppMode.NotConfigured;
            DataDir = "";
        }
    }

    public static bool IsRunningFromInstallDir()
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExeDir)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallDir)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Hordozhato modba allitja magat (marker fajl letrehozasa).</summary>
    public static void SetPortable()
    {
        File.WriteAllText(PortableMarkerPath,
            "DevToolbox hordozhato mod.\r\n" +
            "Amig ez a fajl az exe mellett van, az adatok a Data mappaba kerulnek.\r\n" +
            "Torold, ha ujra valasztani szeretnel telepitett/hordozhato mod kozott.\r\n");
        Detect();
        EnsureDataDirs();
    }

    public static void EnsureDataDirs()
    {
        if (string.IsNullOrEmpty(DataDir)) return;
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        try { Directory.CreateDirectory(ScriptsDir); }
        catch { Directory.CreateDirectory(DefaultScriptsDir); }
    }

    public static string ModeLabel => Mode switch
    {
        AppMode.Portable => "Hordozható mód",
        AppMode.Installed => "Telepített mód",
        _ => "Nincs beállítva"
    };
}
