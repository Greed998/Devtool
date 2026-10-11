using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToolbox;

/// <summary>
/// Programbeallitasok. A DataDir\settings.json fajlban tarolodik, tehat hordozhato
/// modban az exe mellett, telepitett modban a %AppData% alatt.
/// </summary>
public sealed class AppSettings
{
    // ---------------- elérési utak (üres = alapértelmezett)
    public string ScriptsFolder { get; set; } = "";
    public string ExportFolder { get; set; } = "";

    // ---------------- indítás
    public bool AlwaysRunAsAdmin { get; set; }

    // ---------------- hardver / szenzorok
    public int SensorRefreshSeconds { get; set; } = 3;
    public bool LiveSensors { get; set; } = true;
    public int TempWarnCelsius { get; set; } = 70;
    public int TempCriticalCelsius { get; set; } = 85;

    // ---------------- eseménynapló
    public int EventLogHours { get; set; } = 168;
    public bool EventLogWarnings { get; set; } = true;
    public bool HideKnownNoise { get; set; }

    // ---------------- szkriptek
    public string DefaultScriptEncoding { get; set; } = "utf-8";
    public bool ConfirmBeforeRun { get; set; }
    public int SeedVersion { get; set; }
    public string FleetDataDir { get; set; } = "";

    // ---------------- távoli hozzáférés (jelszót SOHA nem tárolunk)
    public string RemoteUser { get; set; } = "";
    public string RemoteHosts { get; set; } = "";

    // ---------------- export
    public string ExportFormat { get; set; } = "html";
    public bool OpenAfterExport { get; set; } = true;

    // ================================================================

    [JsonIgnore]
    public string EffectiveScriptsFolder =>
        string.IsNullOrWhiteSpace(ScriptsFolder) ? AppPaths.DefaultScriptsDir : ScriptsFolder;

    [JsonIgnore]
    public string EffectiveExportFolder =>
        string.IsNullOrWhiteSpace(ExportFolder) ? DefaultExportFolder : ExportFolder;

    [JsonIgnore]
    public static string DefaultExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "DevToolbox jelentések");

    public static AppSettings Current { get; private set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) { Current = new AppSettings(); return; }
            var json = File.ReadAllText(AppPaths.SettingsFile);
            Current = JsonSerializer.Deserialize<AppSettings>(json, Opts) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }

        Current.Clamp();
    }

    public void Save()
    {
        try
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* a beallitas mentese nem letfontossagu */ }
    }

    /// <summary>Ertelmes hatarok kozott tartja az ertekeket (kezzel szerkesztett fajl ellen).</summary>
    private void Clamp()
    {
        SensorRefreshSeconds = Math.Clamp(SensorRefreshSeconds, 1, 60);
        TempWarnCelsius = Math.Clamp(TempWarnCelsius, 40, 110);
        TempCriticalCelsius = Math.Clamp(TempCriticalCelsius, TempWarnCelsius + 1, 125);
        if (EventLogHours is not (24 or 72 or 168 or 720)) EventLogHours = 168;
        if (!ScriptRunner.SupportedEncodings.Contains(DefaultScriptEncoding))
            DefaultScriptEncoding = "utf-8";
        if (ExportFormat is not ("html" or "txt")) ExportFormat = "html";
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Opts), Opts)!;

    public static void Replace(AppSettings s)
    {
        Current = s;
        Current.Save();
    }
}
