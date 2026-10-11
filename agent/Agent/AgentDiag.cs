using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// "DevToolboxAgent.exe diag" - lepesrol lepesre megnezi, hol akad el az agent.
/// A kimenet egyben bemasolhato hibabejelentesnek is.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentDiag
{
    private static int _fails;

    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("  DevToolbox Agent – diagnosztika");
        Console.WriteLine("  ───────────────────────────────");
        Console.WriteLine();

        // --- 1. konfiguráció
        var cfg = AgentConfig.Load();
        var hasCfg = File.Exists(AgentConfig.FilePath);
        Step("Konfiguráció", hasCfg, hasCfg ? AgentConfig.FilePath
            : "nincs – futtasd a telepítőt (install --server … --token …)");
        if (!hasCfg) { Summary(); return; }

        Step("Szerver cím", cfg.ServerUrl.Length > 0, cfg.ServerUrl.Length > 0 ? cfg.ServerUrl : "üres");

        if (cfg.ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Warn("A cím https://-sel kezdődik. A Fleet Server magában HTTP-t beszél –",
                 "ha nincs elé TLS-proxy téve, cseréld http://-re és telepítsd újra.");

        // --- 2. hálózat: DNS + TCP port
        Uri? uri = null;
        try { uri = new Uri(cfg.ServerUrl); } catch { }
        if (uri is null) { Step("Cím formátuma", false, "érvénytelen URL"); Summary(); return; }

        bool tcpOk = false;
        try
        {
            using var tcp = new TcpClient();
            var t = tcp.ConnectAsync(uri.Host, uri.Port);
            tcpOk = await Task.WhenAny(t, Task.Delay(4000)) == t && tcp.Connected;
        }
        catch { }
        Step($"TCP kapcsolat {uri.Host}:{uri.Port}", tcpOk, tcpOk ? "a port nyitva" :
            "nem érhető el – fut a szerver? engedi a tűzfal a portot? jó a gépnév/IP?");

        // --- 3. HTTP: a szerver valóban a Fleet Server-e
        if (tcpOk)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var body = await http.GetStringAsync(cfg.ServerUrl.TrimEnd('/') + "/api/ping");
                var ok = body.Contains("devtoolbox-fleet");
                Step("Fleet Server válaszol", ok, ok ? "/api/ping rendben" : "más szolgáltatás figyel ezen a porton");
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException
                                                  || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            {
                Step("Fleet Server válaszol", false, "TLS-hiba: a szerver HTTP-t beszél, a cím https:// – cseréld http://-re");
            }
            catch (Exception ex)
            {
                Step("Fleet Server válaszol", false, ex.Message);
            }
        }

        // --- 4. regisztráció
        Step("Regisztráció", cfg.IsEnrolled, cfg.IsEnrolled
            ? $"agent: {cfg.AgentId[..8]}…"
            : cfg.EnrollmentToken.Length > 0
                ? "még nem regisztrált (token megvan – a szolgáltatás első jelentkezéskor regisztrál)"
                : "nincs regisztrálva és nincs token – új telepítő kell a webfelületről");

        // --- 5. winget
        var winget = AgentWinget.FindWinget();
        Step("winget", winget is not null, winget ?? "nem található – az állapot és a frissítés nem fog működni");

        // --- 6. szolgáltatás
        var (exists, running) = ServiceState();
        Step("Szolgáltatás (DevToolboxAgent)", running,
            !exists ? "nincs telepítve" : running ? "fut" : "telepítve, de NEM fut – lásd a naplót lent");

        // --- metrikagyűjtés élő próbája: tényleg ad-e adatot a WMI ezen a gépen
        Console.WriteLine();
        Console.WriteLine("  Metrikagyűjtés próbája:");
        try
        {
            var host = Uri.TryCreate(AgentConfig.Load().ServerUrl, UriKind.Absolute, out var su) ? su.Host : "";
            var metrics = AgentMetrics.Collect(new AgentMetrics.Config
            {
                // Szolgaltatas/folyamat lista nelkul is probaljuk a tobbit.
                Services = new() { "Spooler" }, Processes = new() { "explorer" }, PerCore = true, ServerHost = host
            });
            Console.Write(AgentMetrics.LastDiagnostics);
            Step("Összesen gyűjtött metrika", metrics.Count > 0, metrics.Count + " db");
            // pár konkrét érték, hogy lásd, valódi számok jönnek
            foreach (var key in new[] { "cpu.load", "cpu.mhz", "mem.usedpct", "hw.cpu.temp", "hw.cpu.power", "hw.gpu0.temp", "gpu.load", "ping.server.ms", "ping.gw.ms" })
            {
                var m = metrics.FirstOrDefault(x => x.Key == key);
                if (m is not null) Console.WriteLine($"         {m.Label}: {m.Value} {m.Unit}");
            }
            Console.WriteLine($"         Érzékelő-meghajtó (PawnIO): {(AgentHardware.DriverInstalled ? "telepítve " + AgentHardware.DriverVersion : "nincs – a CPU- és alaplap-hőmérséklethez kell")}");
        }
        catch (Exception ex) { Step("Metrikagyűjtés", false, ex.Message); }

        // --- napló
        Console.WriteLine();
        Console.WriteLine("  Utolsó naplóbejegyzések (" + AgentLog.FilePath + "):");
        Console.WriteLine();
        foreach (var l in AgentLog.Tail(15).Split(Environment.NewLine))
            Console.WriteLine("    " + l);

        Summary();
    }

    private static (bool Exists, bool Running) ServiceState()
    {
        var st = NativeService.Status(AgentInstaller.ServiceName);
        return (st is not null, st == System.ServiceProcess.ServiceControllerStatus.Running);
    }

    private static void Step(string name, bool ok, string detail)
    {
        if (!ok) _fails++;
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  [ OK ] " : "  [HIBA] ");
        Console.ForegroundColor = prev;
        Console.WriteLine($"{name,-34} {detail}");
    }

    private static void Warn(params string[] lines)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        foreach (var l in lines) Console.WriteLine("         " + l);
        Console.ForegroundColor = prev;
    }

    private static void Summary()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "  Minden ellenőrzés rendben."
            : $"  {_fails} hiba. A fenti [HIBA] sorok mutatják, hol akad el.");
        Console.WriteLine();
    }
}
