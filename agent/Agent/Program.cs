using System.Runtime.Versioning;
using System.ServiceProcess;
using DevToolbox.Fleet.Agent;

// ============================================================================
//  DevToolbox Agent
//
//  Windows-szolgaltataskent fut a felugyelt gepen, LocalSystem-kent. Kimeno HTTPS-en
//  jelentkezik a Fleet Serverhez: allapotot kuld es feladatokat kap. A gepen NINCS
//  nyitott port. A felhasznalonak NEM kell adminnak lennie.
//
//  Telepites (rendszergazda parancssorbol):
//    DevToolboxAgent.exe install --server http://szerver:8443 --token <TOKEN> --fingerprint <UJJLENYOMAT>
//    DevToolboxAgent.exe uninstall
//    DevToolboxAgent.exe run        (eloterben futtatas, teszthez)
//    DevToolboxAgent.exe once       (egyetlen jelentkezes, teszthez)
// ============================================================================

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Az agent csak Windowson fut.");
    return;
}

await RunWindows(args);

[SupportedOSPlatform("windows")]
static async Task RunWindows(string[] args)
{
#if GUI_SETUP
    // Dupla kattintas (parameter nelkul, interaktivan): grafikus telepito.
    // A szolgaltataskezelo nem interaktivan inditja - az a "service" ag marad.
    if ((args.Length == 0 && Environment.UserInteractive) || (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase)))
    {
        DevToolbox.Fleet.WizardForm.FreeConsole();
        var t = new Thread(DevToolbox.Fleet.Agent.AgentSetupWizard.Run);
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        return;
    }
#endif
    var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "service";

    switch (cmd)
    {
        case "install":
            AgentInstaller.Install(args);
            return;

        case "uninstall":
            AgentInstaller.Uninstall();
            return;

        case "self-update":
        {
            var ti = Array.IndexOf(args, "--target");
            Environment.ExitCode = ti >= 0 && ti + 1 < args.Length ? AgentUpdater.RunSelfUpdate(args[ti + 1]) : 1;
            return;
        }

        case "diag":
            await AgentDiag.RunAsync();
            return;

        case "tray":
        {
            // 1.5.7: a tálca-ikon a felhasználó munkamenetében (a szolgáltatás indítja, ablak nélkül)
            DevToolbox.Fleet.WizardForm.FreeConsole();
            var t = new Thread(AgentTray.Run);
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join();
            return;
        }

        case "once":
        {
            var cfg = AgentConfig.Load();
            var core = new AgentCore(cfg);
            core.Log += Console.WriteLine;
            core.Log += AgentLog.Write;
            await core.CheckinAsync(CancellationToken.None);
            return;
        }

        case "run":
        {
            var cfg = AgentConfig.Load();
            var core = new AgentCore(cfg);
            core.Log += Console.WriteLine;
            core.Log += AgentLog.Write;
            Console.WriteLine("Előtérben futás. Leállítás: Ctrl+C");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            await Worker.Loop(core, cts.Token);
            return;
        }

        default:
            // Duplakattintassal vagy parameter nelkul konzolbol inditva nem a
            // szolgaltataskezelo hivott meg - a ServiceBase.Run itt ertelmetlen hibat adna.
            if (Environment.UserInteractive)
            {
                Console.WriteLine("DevToolbox Agent " + AgentCore.AgentVersion);
                Console.WriteLine();
                Console.WriteLine("  install --server http://SZERVER:8443 --token <TOKEN> --fingerprint <UJJLENYOMAT>");
                Console.WriteLine("  uninstall      a szolgáltatás eltávolítása");
                Console.WriteLine("  diag           hibakeresés: hol akad el az agent");
                Console.WriteLine("  once           egyetlen jelentkezés (teszt)");
                Console.WriteLine("  run            előtérben futás (teszt)");
                return;
            }
            ServiceBase.Run(new AgentService());
            return;
    }
}

[SupportedOSPlatform("windows")]
static class Worker
{
    public static async Task Loop(AgentCore core, CancellationToken token)
    {
        core.StartSampler(token);
        core.StartWatchers(token);   // 1.5.6: gyors szolgáltatás-figyelés (azonnali jelentkezés), USB-napló
        AgentTrayHost.Start(core, token);   // 1.5.7: tálca-felület (named pipe + indítás a felhasználói munkamenetekben)
        while (!token.IsCancellationRequested)
        {
            int next = 300;
            try { next = await core.CheckinAsync(token); }
            catch (OperationCanceledException) { break; }
            catch { }

            try { await core.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(next, 30, 3600)), token); }
            catch (OperationCanceledException) { break; }
        }
    }
}

[SupportedOSPlatform("windows")]
sealed class AgentService : ServiceBase
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    public AgentService() { ServiceName = "DevToolboxAgent"; CanHandleSessionChangeEvent = true; }

    /// <summary>1.5.7: bejelentkezéskor / feloldáskor / távoli csatlakozáskor a tálca hamar elinduljon.</summary>
    protected override void OnSessionChange(SessionChangeDescription d)
    {
        try
        {
            if (d.Reason is SessionChangeReason.SessionLogon or SessionChangeReason.SessionUnlock or SessionChangeReason.RemoteConnect or SessionChangeReason.ConsoleConnect)
                AgentTrayHost.OnSessionChange(d.SessionId, d.Reason == SessionChangeReason.SessionLogon);
        }
        catch { }
    }

    protected override void OnStart(string[] args)
    {
        AgentLog.Write("Szolgáltatás indul, verzió " + AgentCore.AgentVersion);
        AgentCore.IsService = true;
        AgentUpdater.Cleanup();
        var core = new AgentCore(AgentConfig.Load());
        core.Log += AgentLog.Write;
        _worker = Worker.Loop(core, _cts.Token);
    }

    protected override void OnShutdown() => OnStop();

    protected override void OnStop()
    {
        AgentLog.Write("Szolgáltatás leáll.");
        _cts.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(10)); } catch { }
    }
}
