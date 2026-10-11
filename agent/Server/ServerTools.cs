using System.Diagnostics;
using System.Net.NetworkInformation;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Naplózás konzolra ÉS fájlba. Szolgáltatásként futva nincs konzol, ezért ha a szerver
/// nem indul, a hiba oka egyedül a naplófájlból derülhet ki:
///   C:\ProgramData\DevToolbox-Fleet\logs\fleet-server.log (1.5.4-tól; a hely a Beállításokban állítható)
/// </summary>
public static class SrvLog
{
    public static string FilePath => LogFolder.PathOf(ServerPaths.DataDir, "fleet-server.log");

    public static void Info(string m) => Write("INFO ", m, ConsoleColor.Gray);
    public static void Ok(string m) => Write("OK   ", m, ConsoleColor.Green);
    public static void Warn(string m) => Write("FIGY ", m, ConsoleColor.Yellow);
    public static void Error(string m) => Write("HIBA ", m, ConsoleColor.Red);

    private static void Write(string level, string msg, ConsoleColor color)
    {
        if (Environment.UserInteractive)
        {
            try
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(msg);
                Console.ForegroundColor = old;
            }
            catch { }
        }

        LogFolder.Append(ServerPaths.DataDir, "fleet-server.log", "fleet-server.log.old",
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level}{msg}{Environment.NewLine}");
    }

    public static string[] Tail(int lines)
    {
        try
        {
            return File.Exists(FilePath)
                ? File.ReadAllLines(FilePath).TakeLast(lines).ToArray()
                : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }
}

public static class ServerBuild
{
    public const string Version = "1.5.11";
}

public static class ServerPaths
{
    /// <summary>A Windows-szolgáltatás neve (a telepítő és a Központból indított frissítés is ezt használja).</summary>
    public const string ServiceName = "DevToolboxFleetServer";

    public static string DataDir =>
        Environment.GetEnvironmentVariable("FLEET_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "DevToolbox-Fleet");

    /// <summary>
    /// A szerver belső címe az agentek számára, ha nincs böngészős kérés, amiből kiderülne (a Központból
    /// kért telepítőcsomaghoz): az első működő, nem loopback IPv4-cím, átjáróval rendelkező kártyáról előnyben.
    /// </summary>
    public static string LanAgentUrl(int agentPort) => $"http://{LanIp() ?? Environment.MachineName}:{agentPort}";

    /// <summary>A gép belső IPv4-címe (átjáróval rendelkező kártya előnyben), vagy null.</summary>
    public static string? LanIp()
    {
        string? any = null;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var ip = ni.GetIPProperties();
                foreach (var a in ip.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a.Address)) continue;
                    if (ip.GatewayAddresses.Count > 0) return a.Address.ToString();
                    any ??= a.Address.ToString();
                }
            }
        }
        catch { }
        return any;
    }

    /// <summary>A telepített exe állandó helye – nem a Letöltések mappa, amit később törölhetsz.</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DevToolbox Fleet");

    /// <summary>
    /// Ha van feltoltott frissites (FleetServer.new.exe), a csere kulso lepesben tortenik:
    /// a futo exét nem lehet felulirni, ezert az UJ exe indul "apply-update" modban, megvarja
    /// a szolgaltatas leallasat, csereli, es ujrainditja (cmd-szkript nelkul, visszaallitassal).
    /// Ezt a szolgaltatas OnStart-ja hivja - ha nincs .new fajl, azonnal visszater.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool ApplyPendingUpdate(string serviceName) =>
        ServiceSelfUpdate.LaunchIfPending(serviceName, Path.Combine(InstallDir, "FleetServer.exe"), SrvLog.Info);
}

/// <summary>
/// Portdiagnosztika. A leggyakoribb „nem indul el” ok, hogy a port foglalt – a 8080-at
/// például az IIS, a Tomcat vagy egy fejlesztői szerver is szereti. Ilyenkor nem elég annyi,
/// hogy „foglalt”: megmondjuk, MELYIK program foglalja.
/// </summary>
public static class PortTools
{
    public static bool IsInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(ep => ep.Port == port);
        }
        catch { return false; }
    }

    /// <summary>Az első szabad port a megadottól felfelé (a foglaltak kihagyásával).</summary>
    public static int FindFree(int start, ISet<int> alsoAvoid, Func<int, bool>? inUse = null)
    {
        inUse ??= IsInUse;
        for (int p = start; p < start + 100 && p <= 65535; p++)
            if (!alsoAvoid.Contains(p) && !inUse(p)) return p;
        return start;
    }

    /// <summary>A portot figyelő folyamat neve – közvetlenül a TCP-táblából, netstat nélkül.</summary>
    public static string? Owner(int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var pid = NativeNet.ListeningPid(port);
            if (pid is null) return null;
            if (pid == 4) return "System (http.sys – pl. IIS vagy egy másik HttpListener-alapú szolgáltatás)";

            try { return $"{Process.GetProcessById(pid.Value).ProcessName} (PID {pid})"; }
            catch { return $"PID {pid}"; }
        }
        catch { return null; }
    }

    /// <summary>
    /// (Tartalék elemző, a tesztek használják.)
    /// A netstat -ano kimenetéből a LISTENING állapotú sor PID-je. A netstat az állapotot
    /// a rendszer nyelvén írja, ezért nem a szóra, hanem a távoli cím „*:0” / „0.0.0.0:0”
    /// alakjára támaszkodunk, ami nyelvfüggetlen.
    /// </summary>
    public static int? ParseListeningPid(string netstatOutput, int port)
    {
        foreach (var raw in netstatOutput.Split('\n'))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;

            var local = parts[1];
            var colon = local.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(local[(colon + 1)..], out var lp) || lp != port) continue;

            var remote = parts[2];
            var listening = remote.EndsWith(":0") || remote == "*:*";
            if (!listening) continue;

            if (int.TryParse(parts[^1], out var pid)) return pid;
        }
        return null;
    }
}
