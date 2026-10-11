using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToolbox;

public enum HealthState { Ismeretlen, Rendben, Figyelmeztetes, Hiba, Elerhetetlen }

public sealed class CheckResult
{
    public string Name = "";
    public HealthState State = HealthState.Ismeretlen;
    public string Detail = "";
    public string Advice = "";
}

public sealed class HostHealth
{
    public string HostId = "";
    public DateTime Checked = DateTime.Now;
    public HealthState Overall = HealthState.Ismeretlen;

    public bool IsVirtual;
    public string Platform = "";
    public string OperatingSystem = "";
    public string Uptime = "";
    public TimeSpan Duration;

    public List<CheckResult> Checks = new();

    public int Count(HealthState s) => Checks.Count(c => c.State == s);

    public string Summary => Overall switch
    {
        HealthState.Rendben => "Minden rendben",
        HealthState.Figyelmeztetes => $"{Count(HealthState.Figyelmeztetes)} figyelmeztetés",
        HealthState.Hiba => $"{Count(HealthState.Hiba)} hiba",
        HealthState.Elerhetetlen => "Nem érhető el",
        _ => "Még nem ellenőrizve"
    };
}

public sealed class MonitoredHost
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Group { get; set; } = "Alapértelmezett";
    public string Notes { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IsDomainController { get; set; } = true;

    /// <summary>Virtualis gepen a fizikai lemez (SMART) ellenorzese ertelmetlen.</summary>
    public bool SkipPhysicalDiskIfVirtual { get; set; } = true;

    [JsonIgnore] public string DisplayName => Name.Length > 0 ? Name : Host;
}

public sealed class MonitorConfig
{
    public List<MonitoredHost> Hosts { get; set; } = new();

    public int IntervalMinutes { get; set; } = 5;
    public bool AutoStart { get; set; } = true;
    public bool NotifyOnError { get; set; } = true;
    public bool NotifyOnRecovery { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    public int DiskWarnPercent { get; set; } = 85;
    public int DiskCriticalPercent { get; set; } = 95;
    public int PingTimeoutMs { get; set; } = 1500;
    public int TimeSkewWarnSeconds { get; set; } = 120;

    /// <summary>Igaz, ha a felhasznalo mar vegigment a kezdeti beallitason.</summary>
    public bool SetupDone { get; set; }

    // ================================================================

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath => Path.Combine(AppPaths.DataDir, "monitoring.json");

    public static MonitorConfig Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            Current = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<MonitorConfig>(File.ReadAllText(FilePath), Opts) ?? new()
                : new MonitorConfig();
        }
        catch { Current = new MonitorConfig(); }

        Current.Clamp();
    }

    public void Save()
    {
        try
        {
            Clamp();
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }

    private void Clamp()
    {
        IntervalMinutes = Math.Clamp(IntervalMinutes, 1, 1440);
        DiskWarnPercent = Math.Clamp(DiskWarnPercent, 50, 98);
        DiskCriticalPercent = Math.Clamp(DiskCriticalPercent, DiskWarnPercent + 1, 99);
        PingTimeoutMs = Math.Clamp(PingTimeoutMs, 200, 10000);
        TimeSkewWarnSeconds = Math.Clamp(TimeSkewWarnSeconds, 30, 280);
    }

    public IEnumerable<string> Groups =>
        Hosts.Select(h => h.Group).Where(g => g.Length > 0).Distinct()
             .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase);
}

// =====================================================================================

/// <summary>
/// A felugyeleti modul KIZAROLAG belso halozati cimeket fogad el.
///
/// Miert: a modul hitelesitett WMI-lekerdezeseket indit a bejelentkezett felhasznalo
/// jogaival, es rendszeresen, a hatterben fut. Ha publikus cimre engednenk ravenni,
/// az egyreszt hitelesito adatokat szivarogtatna ki az internetre, masreszt a sajat
/// gepedrol indulo, ismetlodo halozati vizsgalattá valna egy idegen rendszer ellen.
/// VPN-en at a tavoli gep is belso cimet kap, ezert az tovabbra is mukodik.
/// </summary>
public static class PrivateNetwork
{
    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                                   // 10.0.0.0/8
                172 => b[1] >= 16 && b[1] <= 31,              // 172.16.0.0/12
                192 => b[1] == 168,                           // 192.168.0.0/16
                169 => b[1] == 254,                           // 169.254.0.0/16 link-local
                100 => b[1] >= 64 && b[1] <= 127,             // 100.64.0.0/10 CGNAT (Tailscale, ZeroTier)
                _ => false
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC;                     // fc00::/7 egyedi helyi cim
        }

        return false;
    }

    public sealed record Check(bool Allowed, string Message, List<IPAddress> Addresses);

    /// <summary>
    /// Feloldja a gepnevet, es megnezi, hogy MINDEN kapott cim belso-e.
    /// Ha barmelyik publikus, elutasitjuk - igy egy megtevesztoen beallitott
    /// DNS-bejegyzes sem tudja kivinni a lekerdezest az internetre.
    /// </summary>
    public static Check Validate(string host)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0)
            return new Check(false, "Nincs megadva gépnév vagy cím.", new());

        List<IPAddress> addresses;

        if (IPAddress.TryParse(host, out var direct))
        {
            addresses = new List<IPAddress> { direct };
        }
        else
        {
            try
            {
                addresses = Dns.GetHostAddresses(host)
                               .Where(a => a.AddressFamily is AddressFamily.InterNetwork
                                                          or AddressFamily.InterNetworkV6)
                               .ToList();
            }
            catch (Exception ex)
            {
                return new Check(false,
                    $"A(z) „{host}” név nem oldható fel: {ex.Message}", new());
            }

            if (addresses.Count == 0)
                return new Check(false, $"A(z) „{host}” névhez nem tartozik IP-cím.", new());
        }

        var publics = addresses.Where(a => !IsPrivate(a)).ToList();
        if (publics.Count > 0)
        {
            return new Check(false,
                $"Publikus cím: {string.Join(", ", publics)}\n\n" +
                "A felügyelet csak belső hálózaton működik. Ez szándékos: a modul a " +
                "bejelentkezett felhasználó jogaival, hitelesítetten és rendszeresen " +
                "kérdez le, amit nem szabad az interneten át tenni.\n\n" +
                "Ha távoli telephelyről figyelnél, építs VPN-kapcsolatot – azon keresztül " +
                "a gép belső címen látszik, és onnantól működik.",
                addresses);
        }

        return new Check(true,
            $"Belső cím: {string.Join(", ", addresses)}", addresses);
    }
}
