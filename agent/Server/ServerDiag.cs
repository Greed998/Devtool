using System.Net.Sockets;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// "FleetServer.exe diag" - a Központ diag-jának párja: megmondja, miért nem éri el egy agent vagy a
/// böngésző a szervert, és hogy a Központ-kapcsolat él-e. Belülről kifelé halad:
/// fut-e -> figyel-e (ki foglalja a portot) -> válaszol-e -> tűzfal -> hálózati profil -> Központ.
/// </summary>
public static class ServerDiag
{
    private static int _fails;

    public static async Task RunAsync()
    {
        var cfg = ServerConfig.Load(ServerPaths.DataDir);
        Console.WriteLine();
        Console.WriteLine("  Fleet Server – diagnosztika");
        Console.WriteLine("  ──────────────────────────────────────");
        Console.WriteLine($"  Agent-port: {cfg.AgentPort}   Web-port: {cfg.WebPort}   Adatmappa: {ServerPaths.DataDir}");
        Console.WriteLine();

        // 1. szolgáltatás
        var st = NativeService.Status(ServerInstaller.ServiceName);
        Step("Szolgáltatás fut", st == System.ServiceProcess.ServiceControllerStatus.Running,
            st is null ? "nincs telepítve – FleetServer.exe install" : NativeService.StatusText(ServerInstaller.ServiceName));

        // 2. portok: figyel-e, és ha más figyel rajta, ki
        foreach (var (name, port) in new[] { ("Agent-port", cfg.AgentPort), ("Web-port", cfg.WebPort) })
        {
            var up = await Tcp("127.0.0.1", port);
            var owner = up ? null : PortTools.Owner(port);
            Step($"{name} ({port}) figyel", up, up ? "igen" : owner is null ? "nem – a szolgáltatás nem indult el" : "foglalja: " + owner);
        }

        // 3. az agent-API válaszol-e (nem egy másik program a porton)
        var ping = await Get($"http://127.0.0.1:{cfg.AgentPort}/api/ping");
        Step("Az /api/ping válaszol", ping.Contains("devtoolbox-fleet"), ping.Length > 80 ? ping[..80] : ping);

        // 4. tűzfal
        foreach (var (rule, port) in new[] { (ServerInstaller.FwAgent, cfg.AgentPort), (ServerInstaller.FwWeb, cfg.WebPort) })
        {
            var fw = NativeFirewall.Find(rule);
            var ok = fw is { Enabled: true } && fw.Value.LocalPorts.Split(',').Contains(port.ToString());
            Step($"Tűzfalszabály: {rule}", ok, ok ? "megvan" : "hiányzik vagy régi port – indítsd újra a szolgáltatást (induláskor beállítja)");
        }

        // 5. hálózati profil
        Console.WriteLine($"  [INFO] Hálózati profil:                   {string.Join("; ", NativeNet.ConnectionProfiles())}");

        // 6. Központ-kapcsolat (ha be van állítva)
        if (cfg.HubUrl.Length > 0)
        {
            var hub = await Get(cfg.HubUrl.TrimEnd('/') + "/hub/ping");
            Step("A Központ elérhető", hub.Contains("devtoolbox-hub"), hub.Contains("devtoolbox-hub") ? cfg.HubUrl : (hub.Length > 100 ? hub[..100] : hub));
            Console.WriteLine($"  [INFO] Telephely: {(cfg.HubSiteName.Length > 0 ? cfg.HubSiteName : "-")}   regisztrálva: {(cfg.HubSiteId.Length > 0 ? "igen" : "NEM")}");
        }

        Console.WriteLine();
        Console.WriteLine("  A napló utolsó sorai (" + SrvLog.FilePath + "):");
        foreach (var l in SrvLog.Tail(10)) Console.WriteLine("    " + l);
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
}
