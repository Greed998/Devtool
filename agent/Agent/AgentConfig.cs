using System.Text.Json;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Az agent tartos beallitasai. A ProgramData vedett mappajaban, ahol csak a
/// LocalSystem szolgaltatas fer hozza - a privat kulcs igy nem olvashato normal user altal.
/// </summary>
public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "";
    public string ServerFingerprint { get; set; } = "";
    public string EnrollmentToken { get; set; } = "";

    public string AgentId { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public string PrivateKeyPem { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ServerPublicKeyPem { get; set; } = "";
    /// <summary>A saját kulcs mióta él (null: 1.4.5 előtti - a szerver szabálya szerint azonnal cserélendő).</summary>
    public DateTime? KeyCreatedUtc { get; set; }

    public int CheckinSeconds { get; set; } = 300;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEnrolled => AgentId.Length > 0 && PrivateKeyPem.Length > 0;

    [System.Text.Json.Serialization.JsonIgnore]
    /// <summary>
    /// Az agent adatmappaja. A DEVTOOLBOX_AGENT_DIR kornyezeti valtozo csak a tesztekhez van:
    /// igy a tesztek sosem irjak felul egy valodi agent beallitasait. Nelkule a megszokott hely.
    /// </summary>
    public static string Dir =>
        Environment.GetEnvironmentVariable("DEVTOOLBOX_AGENT_DIR") is { Length: > 0 } d ? d :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DevToolbox-Agent");

    [System.Text.Json.Serialization.JsonIgnore]
    public static string FilePath => Path.Combine(Dir, "agent.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(FilePath), Opts) ?? new();
        }
        catch { }
        return new AgentConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}
