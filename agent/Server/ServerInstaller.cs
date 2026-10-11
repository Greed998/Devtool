using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A Fleet Server telepitese Windows-szolgaltataskent. Kulso fuggoseg es parancssor nelkul,
/// kozvetlenul a Windows API-n at (NativeService, NativeFirewall, NativeUrlAcl, NativeAcl).
///
/// Fontos: az exét ALLANDO helyre masoljuk (Program Files), mert ha a szolgaltatas a
/// Letoltesek mappabol futna es azt kesobb torlod/atmozgatod, a szolgaltatas nem indulna.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServerInstaller
{
    public const string ServiceName = ServerPaths.ServiceName;

    public static void Install()
    {
        if (!IsAdmin())
        {
            SrvLog.Error("A telepiteshez RENDSZERGAZDAI parancssor kell.");
            SrvLog.Error("Zard be ezt, es nyiss egy rendszergazda PowerShellt/CMD-t, majd futtasd ujra.");
            return;
        }

        var dir = ServerPaths.DataDir;
        var config = ServerConfig.Load(dir);

        // 1. Az exe allando helyre masolasa.
        var installDir = ServerPaths.InstallDir;
        var targetExe = Path.Combine(installDir, "FleetServer.exe");
        var currentExe = Environment.ProcessPath ?? "";

        try
        {
            Directory.CreateDirectory(installDir);
            if (!string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase))
            {
                // Futo szolgaltatas zarolja az exét - frissitesnel elobb leallitjuk.
                if (ServiceExists()) { NativeService.Stop(ServiceName, TimeSpan.FromSeconds(30)); KillServerProcesses(); }
                File.Copy(currentExe, targetExe, overwrite: true);
                SrvLog.Ok($"Az exe bemasolva: {targetExe}");
                // Az agent az exébe van beágyazva (AgentPackage) - külön fájlt nem kell mellé tenni.
            }
        }
        catch (Exception ex)
        {
            SrvLog.Error("Az exe masolasa nem sikerult: " + ex.Message);
            SrvLog.Error("Folytatom a jelenlegi helyrol, de allando helyre ajanlott telepiteni.");
            targetExe = currentExe;
        }

        // 2. URL-foglalas mindket portra, hogy LocalSystem-kent figyelhessen.
        UrlAcl(config.WebPort);
        UrlAcl(config.AgentPort);

        Console.WriteLine("Tűzfalszabályok (minden profil, csak belső hálózatokról)…");
        EnsureFirewall(config.AgentPort, config.WebPort);

        // 3. Az adatmappa jogainak leszukitese (a privat kulcs miatt).
        RestrictDataDir(dir);

        // 4. Szolgaltatas letrehozasa (vagy frissitesnel atallitasa). A binPath-ban az ALLANDO exe.
        SrvLog.Info("Szolgaltatas letrehozasa...");
        try
        {
            NativeService.CreateOrUpdate(ServiceName, "Fleet Server",
                "DevToolbox flottakezelo szerver - agentek felugyelete es webes admin.", targetExe);
        }
        catch (Exception ex) { SrvLog.Error("A szolgaltatas letrehozasa nem sikerult: " + ex.Message); return; }
        NativeService.Start(ServiceName, TimeSpan.FromSeconds(20));

        // 5. Ellenorizzuk, tenyleg elindult-e.
        System.Threading.Thread.Sleep(2500);
        var state = QueryState();

        Console.WriteLine();
        if (state.Contains("RUNNING"))
        {
            SrvLog.Ok("A szolgaltatas FUT.");
            SrvLog.Info($"Nyisd meg a webfeluletet:  http://localhost:{config.WebPort}");
            SrvLog.Info("Ott hozod letre a szuperadmin fiokot es allitod be a portokat.");
        }
        else
        {
            SrvLog.Error("A szolgaltatas nem indult el. Allapot: " + state);
            SrvLog.Error("A reszletes ok a naplofajlban:  " + SrvLog.FilePath);
            Console.WriteLine();
            SrvLog.Info("A naplo utolso sorai:");
            foreach (var line in SrvLog.Tail(12)) Console.WriteLine("   " + line);
        }
    }

    /// <summary>
    /// Ujratelepites: szolgaltatas le, setup visszaallitva, szolgaltatas ujra fel.
    /// Alapbol MEGTARTJA a szerverkulcsot, az agenteket es a tokeneket - kulonben minden
    /// telepitett agent ujraregisztralast igenyelne. --clean eseten mindent torol.
    /// </summary>
    public static void Reinstall(bool clean)
    {
        if (!IsAdmin())
        {
            SrvLog.Error("Az újratelepítéshez RENDSZERGAZDAI parancssor kell.");
            return;
        }

        var dir = ServerPaths.DataDir;
        Console.WriteLine(clean
            ? "Újratelepítés TISZTA lappal: minden agent újraregisztrálást igényel!"
            : "Újratelepítés: a szuperadmin beállítás törlődik, az agentek és kulcsok megmaradnak.");
        Console.Write("Folytatod? (i/n): ");
        if (!(Console.ReadLine() ?? "").Trim().StartsWith("i", StringComparison.OrdinalIgnoreCase)) return;

        if (!Uninstall())
        {
            SrvLog.Error("Az újratelepítés megszakítva – a régi szolgáltatás nem távolítható el.");
            return;
        }

        if (clean)
        {
            // Visszafordithatatlan: minden agent ujraregisztralast igenyel. Kulon megerosites + mentes.
            Console.Write("A TELJES törléshez írd be nagybetűvel: TORLES  ");
            if ((Console.ReadLine() ?? "").Trim() != "TORLES") { Console.WriteLine("Megszakítva."); return; }
            var bak = DataBackup.Create(dir, "clean-elott");
            if (bak is not null) Console.WriteLine("Mentés készült (a kulcsok visszaállíthatók): " + bak);
            foreach (var f in new[] { "fleet.json", "server-config.json", "server-private.pem", "server-public.pem",
                                      "server-keystore.json", "hub-client-private.pem", "hub-client-public.pem" })
                try { File.Delete(Path.Combine(dir, f)); } catch { }
            try { Directory.Delete(Path.Combine(dir, "inbox"), true); } catch { }
        }
        else
        {
            var cfg = ServerConfig.Load(dir);
            cfg.ResetSetup();
            if (!cfg.Save(dir) || ServerConfig.Load(dir).SetupDone)
            {
                SrvLog.Error("A beállítás visszaállítása nem sikerült: " + ServerConfig.PathFor(dir));
                SrvLog.Error("Ellenőrizd, hogy a fájl írható-e, majd futtasd újra.");
                return;
            }
            SrvLog.Ok("A szuperadmin beállítás törölve.");
        }

        Install();
        Console.WriteLine();
        SrvLog.Info("A webfelületen újra végig kell menni az első beállításon.");
    }

    /// <summary>
    /// Szolgaltatas leallitasa es torlese, MEGVARVA, hogy tenyleg eltunjon.
    /// A korabbi fix varakozas utan az 'sc create' gyakran 1072-es hibat adott
    /// ("torlesre jelolve"), mert a regi folyamat meg futott.
    /// </summary>
    public static bool Uninstall()
    {
        if (!IsAdmin()) { SrvLog.Error("Rendszergazdai parancssor kell."); return false; }

        if (ServiceExists())
        {
            // 1. leallitas es varakozas a STOPPED allapotra
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    SrvLog.Info("Szolgáltatás leállítása...");
                    if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) { SrvLog.Warn("Leállítás: " + ex.Message); }

            // 2. ha a folyamat nem lepett ki, lezarjuk - kulonben az exe zarolva marad
            KillServerProcesses();

            // 3. torles es varakozas, amig a szolgaltatas tenyleg eltunik
            try { NativeService.Delete(ServiceName); } catch (Exception ex) { SrvLog.Warn("Törlés: " + ex.Message); }
            var until = DateTime.UtcNow.AddSeconds(30);
            while (ServiceExists() && DateTime.UtcNow < until) System.Threading.Thread.Sleep(500);

            if (ServiceExists())
            {
                SrvLog.Error("A szolgáltatás törlésre van jelölve, de még nem tűnt el.");
                SrvLog.Error("Zárd be a Szolgáltatások ablakot (services.msc) és a Feladatkezelőt,");
                SrvLog.Error("majd futtasd újra. Ha így sem megy, indítsd újra a gépet.");
                return false;
            }
        }

        NativeFirewall.Delete(FwAgent);
        NativeFirewall.Delete(FwWeb);
        NativeFirewall.Delete(FwSnmp);
        SrvLog.Ok("A Fleet Server szolgáltatás eltávolítva.");
        SrvLog.Info("Az adatok (agentek, kulcsok, konfiguráció) megmaradtak: " + ServerPaths.DataDir);
        return true;
    }

    private static bool ServiceExists()
    {
        try
        {
            return ServiceController.GetServices()
                .Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>A telepitett FleetServer.exe futo peldanyainak lezarasa (sajat magunkat kiveve).</summary>
    private static void KillServerProcesses()
    {
        var self = Environment.ProcessId;
        var installed = Path.Combine(ServerPaths.InstallDir, "FleetServer.exe");
        foreach (var p in Process.GetProcessesByName("FleetServer"))
        {
            try
            {
                if (p.Id == self) continue;
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (path is not null && !path.Equals(installed, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill(true);
                p.WaitForExit(10000);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Diagnosztika: mi az allapot, figyel-e a port, mit mond a naplo.</summary>
    public static void Status()
    {
        var config = ServerConfig.Load(ServerPaths.DataDir);
        Console.WriteLine();
        Console.WriteLine("  Fleet Server - allapot");
        Console.WriteLine("  ─────────────────────────────────");
        Console.WriteLine($"  Szolgaltatas:  {QueryState()}");
        Console.WriteLine($"  Adatmappa:     {ServerPaths.DataDir}");
        Console.WriteLine($"  Setup kesz:    {(config.SetupDone ? "igen" : "NEM")}");
        Console.WriteLine($"  Web-port:      {config.WebPort}  " +
            (PortTools.IsInUse(config.WebPort) ? "(figyel/foglalt)" : "(nem figyel)"));
        Console.WriteLine($"  Agent-port:    {config.AgentPort}  " +
            (PortTools.IsInUse(config.AgentPort) ? "(figyel/foglalt)" : "(nem figyel)"));
        Console.WriteLine($"  Naplofajl:     {SrvLog.FilePath}");
        Console.WriteLine();
        Console.WriteLine("  A naplo utolso sorai:");
        var tail = SrvLog.Tail(15);
        if (tail.Length == 0) Console.WriteLine("   (ures)");
        foreach (var line in tail) Console.WriteLine("   " + line);
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- segédek

    private static string QueryState() => NativeService.StatusText(ServiceName);

    private static bool IsAdmin() => NativeAdmin.IsAdmin();

    private static void RestrictDataDir(string dir)
    {
        try { NativeAcl.RestrictToSystemAndAdmins(dir); }
        catch (Exception ex) { SrvLog.Warn("Az adatmappa jogainak beállítása: " + ex.Message); }
    }

    public const string FwAgent = "DevToolbox Fleet - agent";
    public const string FwWeb = "DevToolbox Fleet - web";
    /// <summary>SNMP-trap fogadás (UDP) - csak bekapcsolva, csak a megengedett (alapból belső) címekről.</summary>
    public const string FwSnmp = SnmpReceiver.FwRule;

    /// <summary>Belso tartomanyok - a tuzfal csak innen enged be.</summary>
    public const string PrivateRanges =
        "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,169.254.0.0/16,fc00::/7,fe80::/10";

    /// <summary>
    /// Bejovo tuzfalszabalyok a ket portra. Minden szerverinduláskor ujra beallitjuk,
    /// mert a webes setupban a port megvaltoztathato. Minden profilon nyit, de csak belso
    /// IP-tartomanyokbol - osszhangban a "csak belso halozat" elvvel.
    /// </summary>
    public static void EnsureFirewall(int agentPort, int webPort) => EnsureFirewall(new[] { agentPort }, webPort);

    /// <summary>Portváltás alatt az agent-szabály a régi és az új portra is szól.</summary>
    public static void EnsureFirewall(IEnumerable<int> agentPorts, int webPort)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            NativeFirewall.Replace(FwAgent, agentPorts.Distinct().ToArray(), PrivateRanges, "DevToolbox Fleet Server - agentek jelentkezése");
            NativeFirewall.Replace(FwWeb, new[] { webPort }, PrivateRanges, "DevToolbox Fleet Server - webes admin");
        }
        catch (Exception ex) { SrvLog.Warn("Tűzfalszabály: " + ex.Message); }
    }

    private static void UrlAcl(int port)
    {
        // SDDL-lel adjuk meg (SY = LocalSystem), mert a fioknev nyelvfuggo.
        try { NativeUrlAcl.Ensure($"http://+:{port}/", "D:(A;;GX;;;SY)"); }
        catch (Exception ex) { SrvLog.Warn(ex.Message); }
    }
}
