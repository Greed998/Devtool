using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Gyors szolgáltatás-figyelés a szerveren (1.5.6): az agent 5 mp-enként nézi a figyelt szolgáltatásokat, és változáskor
/// azonnal jelentkezik; itt lesz belőle riasztás („Szolgáltatás leállt”, újraindulásnál megszűnik). Az események a
/// Központba is felmennek (SiteSummary.ServiceEvents) - onnan e-mail / Janus.
/// </summary>
public static class ServiceWatch
{
    private static readonly object Lock = new();
    private static readonly LinkedList<SiteSnmpEvent> Recent = new();
    private static long _lastId;

    /// <summary>Egy jelentkezés szolgáltatás-állapotai → riasztás-események (a hívó a riasztásnaplóba írja).</summary>
    public static List<AlertEvent> Apply(AlertStore alerts, string agentId, string machine, List<ServiceState> states)
    {
        var events = new List<AlertEvent>();
        var names = states.Where(s => !s.Missing).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Ami már nincs a figyeltek között (vagy nincs a gépen), annak a riasztása csendben megszűnik.
        alerts.ClearConditions(AlertStore.ServiceRuleId, k => k.StartsWith(agentId + "|", StringComparison.Ordinal) && !names.Contains(k[(agentId.Length + 1)..]));
        foreach (var s in states.Where(s => !s.Missing))
        {
            var label = s.DisplayName.Length > 0 && !s.DisplayName.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ? $"{s.DisplayName} ({s.Name})" : s.Name;
            var ev = alerts.SetCondition(AlertStore.ServiceRuleId, AlertStore.ServiceRuleName, agentId + "|" + s.Name, machine, !s.Running,
                $"{label} leállt ({s.Status}, {s.SinceUtc.ToLocalTime():MM.dd HH:mm:ss})", $"{label} újra fut ({s.SinceUtc.ToLocalTime():HH:mm:ss})", s.Name);
            if (ev is null) continue;
            events.Add(ev);
            lock (Lock)
            {
                var id = Math.Max(DateTime.UtcNow.Ticks, _lastId + 1);
                _lastId = id;
                Recent.AddFirst(new SiteSnmpEvent { Id = id, AtUtc = DateTime.UtcNow, Source = machine, Name = s.Name, Text = ev.Detail, Cleared = !ev.Raised });
                while (Recent.Count > 50) Recent.RemoveLast();
            }
        }
        return events;
    }

    /// <summary>A Központnak: a legutóbbi leállások / újraindulások (időrendben).</summary>
    public static List<SiteSnmpEvent> Events(int n) { lock (Lock) return Recent.Take(n).Reverse().ToList(); }
}

/// <summary>
/// Másolás külső eszközre – a szerver oldali napló (1.5.6): az agentek USB-eseményei a naplómappa usb.log fájljába
/// (JSON soronként, forgatva) és az utolsó 3000 a memóriába (felület). A Központba NEM megy fel (adatvédelem, mennyiség).
/// </summary>
public static class UsbLog
{
    public sealed class Entry
    {
        public string AgentId { get; set; } = "";
        public string Machine { get; set; } = "";
        public UsbEvent Event { get; set; } = new();
    }

    private static readonly object Lock = new();
    private static readonly LinkedList<Entry> Recent = new();
    private static string _dataDir = "";
    public const int RecentMax = 3000;

    public static void Init(string dataDir)
    {
        _dataDir = dataDir;
        var lines = new List<string>();
        foreach (var f in new[] { "usb.log.1", "usb.log" })
            try { var p = LogFolder.PathOf(dataDir, f); if (File.Exists(p)) lines.AddRange(File.ReadLines(p).TakeLast(RecentMax)); } catch { }
        lock (Lock)
            foreach (var l in lines.TakeLast(RecentMax))
                try { if (JsonSerializer.Deserialize<Entry>(l, ProtocolJson.Options) is { } e) Recent.AddFirst(e); } catch { }
    }

    public static void Add(string agentId, string machine, IEnumerable<UsbEvent> events)
    {
        var sb = new System.Text.StringBuilder();
        lock (Lock)
            foreach (var e in events.Take(1000))
            {
                // Az agent adata: hossz- és típuskorlát (a felületre és a naplóba ne kerülhessen bármi).
                e.Kind = e.Kind is "connect" or "disconnect" or "write" or "rename" or "delete" or "overflow" ? e.Kind : "?";
                e.Path = Cut(e.Path, 400); e.OldPath = Cut(e.OldPath, 400); e.Label = Cut(e.Label, 64); e.Drive = Cut(e.Drive, 8);
                e.Serial = Cut(e.Serial, 16); e.User = Cut(e.User, 128);
                var entry = new Entry { AgentId = agentId, Machine = machine, Event = e };
                Recent.AddFirst(entry);
                sb.Append(JsonSerializer.Serialize(entry, ProtocolJson.Options)).Append('\n');
            }
        lock (Lock) while (Recent.Count > RecentMax) Recent.RemoveLast();
        if (sb.Length > 0) LogFolder.Append(_dataDir.Length > 0 ? _dataDir : ServerPaths.DataDir, "usb.log", "usb.log.1", sb.ToString());
    }

    private static string Cut(string? s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length <= n ? s : s[..n]; }

    /// <summary>Lista (legújabb elöl): gép szerint (üres = mind), keresés a fájlnévben / felhasználóban / címkében.</summary>
    public static (int Total, List<Entry> Items) List(string agentId, string q, int page, int size = 100)
    {
        lock (Lock)
        {
            var all = Recent.Where(e => (agentId.Length == 0 || e.AgentId == agentId) && (q.Length == 0 || e.Event.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Event.User.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Event.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Machine.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            return (all.Count, all.Skip(Math.Max(0, page) * size).Take(size).ToList());
        }
    }
}
