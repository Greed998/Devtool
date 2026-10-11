using System.Net.Sockets;
using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// "FleetHub.exe diag" - megmondja, miert nem eri el mas gep / az NPM / a telephely a Kozpontot.
/// Belulrol kifele halad: fut-e -> figyel-e -> tuzfal -> halozati profil -> NPM-en at.
/// </summary>
public static class HubDiag
{
    private static int _fails;

    public static async Task RunAsync()
    {
        var cfg = ServerConfig.Load(HubPaths.DataDir);
        var port = cfg.WebPort == 0 ? 8090 : cfg.WebPort;
        var ip = HubInstaller.LocalIp();

        Console.WriteLine();
        Console.WriteLine("  Fleet HUB – diagnosztika");
        Console.WriteLine("  ─────────────────────────────────");
        Console.WriteLine($"  Port: {port}   Belső IP: {ip}   Domain: {(cfg.HubDomain.Length > 0 ? cfg.HubDomain : "(nincs beállítva)")}");
        Console.WriteLine();

        // 1. szolgáltatás
        var st = NativeService.Status(HubPaths.ServiceName);
        Step("Szolgáltatás fut", st == System.ServiceProcess.ServiceControllerStatus.Running,
            st is null ? "nincs telepítve – FleetHub.exe install" : "ha nem fut: Szolgáltatások → " + HubPaths.ServiceName + " → Indítás");

        // 2. figyel-e a porton (helyben és a belső IP-n)
        var loc = await Tcp("127.0.0.1", port);
        Step($"Figyel a {port}-es porton (helyben)", loc, loc ? "igen" : "nem – a szolgáltatás nem indult el, vagy más porton van");
        var lan = await Tcp(ip, port);
        Step($"Elérhető a belső IP-n ({ip}:{port})", lan, lan ? "igen" : "nem");

        // 3. a telephely-API válaszol-e
        var ping = await Get($"http://127.0.0.1:{port}/hub/ping");
        Step("A /hub/ping válaszol", ping.Contains("devtoolbox-hub"), ping.Length > 80 ? ping[..80] : ping);

        // 4. tűzfal
        var fw = NativeFirewall.Find(HubFirewall.RuleName);
        var fwOk = fw is { Enabled: true } && fw.Value.LocalPorts.Split(',').Contains(port.ToString());
        Step("Tűzfalszabály a portra", fwOk, fwOk ? "megvan" : "hiányzik – indítsd újra a szolgáltatást (induláskor létrehozza)");
        if (fwOk && !fw!.Value.AllProfiles)
            Warn("A szabály nem minden hálózati profilra vonatkozik – régi szabály? Indítsd újra a szolgáltatást.");

        // 5. hálózati profil
        Console.WriteLine($"  [INFO] Hálózati profil:                   {string.Join("; ", NativeNet.ConnectionProfiles())}");

        // 6. NPM-en át (a domain)
        if (cfg.HubDomain.Length > 0)
        {
            var pub = await Get($"https://{cfg.HubDomain}/hub/ping");
            var pubOk = pub.Contains("devtoolbox-hub");
            Step($"https://{cfg.HubDomain}/hub/ping", pubOk, pubOk ? "a proxy is jól továbbít" : pub.Length > 100 ? pub[..100] : pub);
            if (!pubOk)
            {
                Warn("Ha a fenti belső lépések rendben vannak, az NPM a hiba helye:",
                     $"  - a Proxy Host célja: http  {ip} : {port}",
                     "  - az Advanced fülön NE legyen 'location /' blokk (ütközik, a host nem töltődik be)",
                     "  - innen a gépről a saját domainre hívás elbukhat 'hairpin NAT' miatt – ",
                     "    próbáld meg egy külső hálózatról (pl. mobilnet) is.");
            }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "  Minden ellenőrzés rendben." : $"  {_fails} hiba – a [HIBA] sorok mutatják, hol akad el.");
        Console.WriteLine();
    }

    private static async Task<bool> Tcp(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(host, port);
            return await Task.WhenAny(t, Task.Delay(3000)) == t && c.Connected;
        }
        catch { return false; }
    }

    private static async Task<string> Get(string url)
    {
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var r = await h.GetAsync(url);
            var body = await r.Content.ReadAsStringAsync();
            return r.IsSuccessStatusCode ? body : $"HTTP {(int)r.StatusCode} {r.ReasonPhrase}";
        }
        catch (Exception ex) { return ex.InnerException?.Message ?? ex.Message; }
    }

    private static void Step(string name, bool ok, string detail)
    {
        if (!ok) _fails++;
        Console.WriteLine($"  [{(ok ? " OK " : "HIBA")}] {name,-38} {detail}");
    }

    private static void Warn(params string[] lines) { foreach (var l in lines) Console.WriteLine("         " + l); }
}
