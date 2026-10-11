using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Az internetkapcsolat figyelése a telephelyi szerveren (a felhasználó kérése, 1.5.5). A megadott címeket SORBAN
/// pingeli (címenként legfeljebb kétszer, 2 mp várakozással): az első válasz = van internet, a többit már nem kérdezi.
/// Ha egyik sem válaszol, 30 mp múlva még egyszer végigpróbálja (egy elveszett csomag ne riasszon), és csak utána
/// jelez kiesést. Kiesés alatt percenként ellenőriz, hogy a visszatérés ideje pontos legyen.
///
/// A kiesés riasztás (AlertStore.InternetRuleId - a riasztásnaplóba, az aktív riasztások közé), és a kiesések listája
/// a Központba is felmegy (SiteSummary.Internet): a Központ a visszatérés után e-mailben jelzi, mettől meddig tartott.
/// Ping: a .NET Ping osztálya (ICMP, Windows-hívás) - nem indítunk ping.exe-t.
/// </summary>
public static class NetWatch
{
    public sealed class Outage { public DateTime StartUtc { get; set; } public DateTime? EndUtc { get; set; } }

    private sealed class FileModel
    {
        public bool? Online { get; set; }
        public DateTime? SinceUtc { get; set; }
        public List<Outage> Outages { get; set; } = new();
    }

    public sealed record ProbeResult(bool Ok, string Target, long RttMs, List<string> NoReply);

    private static readonly object Lock = new();
    private static string _path = "";
    private static List<Outage> _outages = new();

    /// <summary>null: még nem volt ellenőrzés (vagy ki van kapcsolva).</summary>
    public static bool? Online { get; private set; }
    public static DateTime? SinceUtc { get; private set; }
    public static DateTime? LastCheckUtc { get; private set; }
    public static string LastTarget { get; private set; } = "";
    public static long LastRttMs { get; private set; }
    public static List<string> LastNoReply { get; private set; } = new();

    /// <summary>Teszthez: a ping helyett (cím → válaszidő ms, vagy null).</summary>
    public static Func<string, CancellationToken, Task<long?>>? PingOverride { get; set; }
    /// <summary>Teszthez: a megerősítő próba előtti várakozás.</summary>
    public static TimeSpan ConfirmDelay { get; set; } = TimeSpan.FromSeconds(30);

    public const int MaxTargets = 10;

    /// <summary>A célok ellenőrzése: IP-cím vagy gépnév (a gépnév feloldásához DNS kell - IP-cím ajánlott). Hibánál üzenet.</summary>
    public static string? Validate(IEnumerable<string> raw, out List<string> targets)
    {
        targets = raw.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count > MaxTargets) return $"Legfeljebb {MaxTargets} cím adható meg.";
        foreach (var t in targets)
            if (!IPAddress.TryParse(t, out _) && !Regex.IsMatch(t, @"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$"))
                return $"Érvénytelen cím: {t}";
        return null;
    }

    public static int ClampMinutes(int m) => Math.Clamp(m, 1, 60);

    /// <summary>Az állapot betöltése (újraindítás után a folyamatban levő kiesés folytatódik).</summary>
    public static void Load(string dataDir)
    {
        lock (Lock)
        {
            _path = Path.Combine(dataDir, "internet-figyeles.json");
            try
            {
                if (File.Exists(_path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), ProtocolJson.Options) is { } m)
                { Online = m.Online; SinceUtc = m.SinceUtc; _outages = m.Outages; }
            }
            catch { }
        }
    }

    private static void Save()
    {
        if (_path.Length == 0) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Online = Online, SinceUtc = SinceUtc, Outages = _outages }, ProtocolJson.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }

    public static List<Outage> Outages { get { lock (Lock) return _outages.Select(o => new Outage { StartUtc = o.StartUtc, EndUtc = o.EndUtc }).ToList(); } }

    private static async Task<long?> PingOnce(string host, CancellationToken ct)
    {
        if (PingOverride is { } o) return await o(host, ct);
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(host, TimeSpan.FromSeconds(2), cancellationToken: ct);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }   // feloldhatatlan név, hálózat nélküli gép: nincs válasz
    }

    /// <summary>A címek sorban: az első válaszoló nyer (címenként legfeljebb két próba).</summary>
    public static async Task<ProbeResult> Probe(IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        var none = new List<string>();
        foreach (var t in targets)
        {
            for (int i = 0; i < 2; i++)
                if (await PingOnce(t, ct) is { } rtt) return new ProbeResult(true, t, rtt, none);
            none.Add(t);
        }
        return new ProbeResult(false, "", 0, none);
    }

    /// <summary>„Próba most” a felületről: MINDEN címet megkérdez (melyik válaszol), a sorrendtől függetlenül.</summary>
    public static async Task<List<(string Target, long? RttMs)>> ProbeAll(IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        var list = new List<(string, long?)>();
        foreach (var t in targets)
        {
            long? rtt = null;
            for (int i = 0; i < 2 && rtt is null; i++) rtt = await PingOnce(t, ct);
            list.Add((t, rtt));
        }
        return list;
    }

    /// <summary>Egy teljes ellenőrzés: ha egyik cím sem válaszol, ConfirmDelay múlva még egyszer (megerősítés).</summary>
    public static async Task<ProbeResult> Check(IReadOnlyList<string> targets, CancellationToken ct = default)
    {
        var r = await Probe(targets, ct);
        if (r.Ok) return r;
        await Task.Delay(ConfirmDelay, ct);
        return await Probe(targets, ct);
    }

    /// <summary>Az eredmény átvétele: állapotváltáskor kiesés kezdete / vége és riasztás. Visszaadja a riasztás-eseményt (vagy null).</summary>
    public static AlertEvent? Apply(ProbeResult r, AlertStore alerts, string serverName, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (Lock)
        {
            LastCheckUtc = now;
            LastNoReply = r.NoReply;
            if (r.Ok) { LastTarget = r.Target; LastRttMs = r.RttMs; }
            if (Online != r.Ok)
            {
                if (!r.Ok) _outages.Add(new Outage { StartUtc = now });
                else if (_outages.LastOrDefault() is { EndUtc: null } open) open.EndUtc = now;
                if (_outages.Count > 50) _outages.RemoveRange(0, _outages.Count - 50);
                Online = r.Ok; SinceUtc = now;
                Save();
            }
            var start = _outages.LastOrDefault()?.StartUtc ?? now;
            return alerts.SetCondition(AlertStore.InternetRuleId, AlertStore.InternetRuleName, "internet", serverName, !r.Ok,
                $"nincs internet – egyik cím sem válaszol ({string.Join(", ", r.NoReply)})",
                $"az internet újra elérhető ({r.Target}, {r.RttMs} ms) – kiesés: {start.ToLocalTime():MM.dd HH:mm} – {now.ToLocalTime():HH:mm}, {Math.Max(1, Math.Round((now - start).TotalMinutes))} perc");
        }
    }

    /// <summary>Kikapcsoláskor: az állapot törlése, a folyamatban levő kiesés lezárása, a riasztás csendben megszűnik.</summary>
    public static void Reset(AlertStore alerts)
    {
        lock (Lock)
        {
            if (Online is null && LastCheckUtc is null) return;
            if (_outages.LastOrDefault() is { EndUtc: null } open) open.EndUtc = DateTime.UtcNow;
            Online = null; SinceUtc = null; LastCheckUtc = null; LastTarget = ""; LastNoReply = new();
            Save();
        }
        alerts.SetCondition(AlertStore.InternetRuleId, AlertStore.InternetRuleName, "internet", "", false, "", "");
    }

    /// <summary>A háttérciklus (a szerver indítja). A beállítás minden kör elején friss (a webről módosítható).</summary>
    public static async Task Loop(Func<ServerConfig> config, AlertStore alerts, Action<AlertEvent> raise, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            var cfg = config();
            var delay = TimeSpan.FromMinutes(1);
            try
            {
                if (!cfg.NetWatchEnabled || cfg.NetWatchTargets.Count == 0) Reset(alerts);
                else
                {
                    var r = await Check(cfg.NetWatchTargets, ct);
                    if (Apply(r, alerts, "Fleet szerver (" + Environment.MachineName + ")") is { } ev) raise(ev);
                    delay = Online == false ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(ClampMinutes(cfg.NetWatchMinutes));
                }
            }
            catch (OperationCanceledException) { return; }
            catch { }
            try { await Task.Delay(delay, ct); } catch { return; }
        }
    }

    /// <summary>A Központnak (SiteSummary.Internet); null, ha ki van kapcsolva.</summary>
    public static SiteInternet? Summary(ServerConfig cfg)
    {
        if (!cfg.NetWatchEnabled) return null;
        lock (Lock)
            return new SiteInternet
            {
                Online = Online, SinceUtc = SinceUtc, LastCheckUtc = LastCheckUtc, Target = LastTarget, RttMs = LastRttMs,
                IntervalMinutes = ClampMinutes(cfg.NetWatchMinutes),
                Outages = _outages.TakeLast(20).Select(o => new SiteOutage { StartUtc = o.StartUtc, EndUtc = o.EndUtc }).ToList()
            };
    }
}
