using System.Collections.Concurrent;
using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Sérülékenységek (CVE) gépenként a telephelyen (1.5.6): a Központ gyűjti az NVD + CISA KEV + EPSS adatait, párosítja
/// a gépek programjaira, drivereire és BIOS-ára, és a betárcsázás aláírt válaszában küldi le (HubCheckinReply.Cve).
/// Itt csak tároljuk (cve.json, hogy újraindítás után is meglegyen) és mutatjuk; az eszköz-összegzés (DeviceScore) innen számol.
/// Amíg nincs Központ vagy adat, minden 0.
/// </summary>
public static class CveCounts
{
    private static readonly ConcurrentDictionary<string, SiteCveAgent> ByAgent = new();
    private static readonly object FileLock = new();
    private static string _file = "";

    public static DateTime? CheckedUtc { get; private set; }

    public static void Init(string dataDir)
    {
        _file = Path.Combine(dataDir, "cve.json");
        try
        {
            if (!File.Exists(_file)) return;
            var d = JsonSerializer.Deserialize<HubCveData>(File.ReadAllText(_file), ProtocolJson.Options);
            if (d is not null) Load(d);
        }
        catch { }
    }

    /// <summary>A Központtól kapott teljes állapot (a telephely minden gépe): a korábbit lecseréli.</summary>
    public static void Apply(HubCveData d)
    {
        Load(d);
        if (_file.Length == 0) return;
        try
        {
            lock (FileLock)
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(d, ProtocolJson.Options));
                File.Move(tmp, _file, true);
            }
        }
        catch { }
    }

    private static void Load(HubCveData d)
    {
        ByAgent.Clear();
        foreach (var a in d.Agents) ByAgent[a.AgentId] = a;
        CheckedUtc = d.CheckedUtc;
    }

    public static SiteCveAgent? Of(string agentId) => ByAgent.TryGetValue(agentId, out var c) ? c : null;
    public static int Critical(string agentId) => Of(agentId)?.Critical ?? 0;
    public static int High(string agentId) => Of(agentId)?.High ?? 0;
    public static int Exploited(string agentId) => Of(agentId)?.Exploited ?? 0;
}
