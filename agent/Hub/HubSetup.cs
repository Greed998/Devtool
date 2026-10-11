using System.ServiceProcess;
using System.Text;
using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

public static class HubPaths
{
    public const string Version = "1.5.11";
    public const string ServiceName = "DevToolboxFleetHub";

    public static string DataDir =>
        Environment.GetEnvironmentVariable("FLEET_HUB_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DevToolbox-Fleet-Hub");

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DevToolbox Fleet Hub");

    public static string CaddyDir => Path.Combine(DataDir, "caddy");
}

/// <summary>
/// Egyszerű naplófájl (fleet-hub.log, 1.5.4-tól a naplómappában: alapból DataDir\logs, a Beállításokban állítható).
/// Szolgáltatásként és a frissítő folyamatban nincs konzol, a hiba oka csak innen derülhet ki.
/// </summary>
public static class HubLog
{
    public static string FilePath => LogFolder.PathOf(HubPaths.DataDir, "fleet-hub.log");

    public static void Write(string msg)
    {
        if (Environment.UserInteractive) try { Console.WriteLine(msg); } catch { }
        LogFolder.Append(HubPaths.DataDir, "fleet-hub.log", "fleet-hub.log.old", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
    }
}

/// <summary>
/// NGINX Proxy Manager utmutato. Nem generalunk tanusitvanyt - azt az NPM adja (Let's Encrypt).
/// A Kozpont HTTP-n figyel egyetlen porton; az NPM elotte all, DNS only moddal.
/// </summary>
public static class ProxyHint
{
    public static string Readme(string domain, int webPort, string localIp) =>
        "DevToolbox Kozpont mogott NGINX Proxy Manager (DNS only)\r\n" +
        "========================================================\r\n\r\n" +
        $"A Kozpont HTTP-n figyel: http://{localIp}:{webPort}\r\n" +
        "Az NPM egy masik gepen fut. Hozz letre EGY Proxy Host-ot:\r\n\r\n" +
        $"  Domain Names:      {domain}\r\n" +
        $"  Scheme:            http\r\n" +
        $"  Forward Hostname:  {localIp}      (a Kozpont gepe)\r\n" +
        $"  Forward Port:      {webPort}\r\n" +
        "  Websockets:        nem szukseges\r\n" +
        "  Block Common Exploits: bekapcsolhato\r\n\r\n" +
        "  SSL ful:  Request a new SSL Certificate (Let's Encrypt), Force SSL, HTTP/2.\r\n\r\n" +
        "Az admin felulet vedelmet a Kozpont maga vegzi: kulso (publikus) IP-rol a /web\r\n" +
        "oldalak 403-at adnak, a telephely-API (/hub/) viszont nyitva marad (alairas vedi).\r\n" +
        "Ezert az NPM-ben NEM kell Advanced blokk.\r\n\r\n" +
        "FONTOS: ha korabban az Advanced fulre 'location /' blokkot irtal, TOROLD.\r\n" +
        "Az utkozik az NPM altal generalt blokkal ('duplicate location'), es a proxy host\r\n" +
        "emiatt nem toltodik be - ezert nem tudnak a telephelyek betarcsazni.\r\n\r\n" +
        "OPCIONALIS - valodi eldobas mar az NPM-nel (a kulso latogato meg hibaoldalt sem kap):\r\n" +
        "  Az Advanced fulre CSAK ezt (location blokk nelkul - nem utkozik semmivel):\r\n\r\n" +
        "    set $dtb_block 1;\r\n" +
        "    if ($request_uri ~ \"^/hub/\") { set $dtb_block 0; }\r\n" +
        "    if ($remote_addr ~ \"^(10\\.|192\\.168\\.|172\\.(1[6-9]|2[0-9]|3[01])\\.|127\\.)\") { set $dtb_block 0; }\r\n" +
        "    if ($dtb_block) { return 444; }\r\n\r\n" +
        "  A 444 az nginx kulonleges kodja: valasz nelkul bontja a kapcsolatot.\r\n" +
        "  A /hub/ es a belso halo tovabbra is atmegy.\r\n\r\n" +
        "Ellenorzes a Kozpont gepen:  FleetHub.exe diag\r\n\r\n" +
        "Cloudflare: DNS only (szurke felho). Igy a TLS vegig az NPM-ig tart, es a\r\n" +
        "tartalom (gepnevek, szoftverlistak) senkinel nem lathato utkozben.\r\n";

    public static string Write(string domain, int webPort, string localIp)
    {
        Directory.CreateDirectory(HubPaths.CaddyDir);
        File.WriteAllText(Path.Combine(HubPaths.CaddyDir, "NPM-beallitas.txt"),
            Readme(domain, webPort, localIp), new UTF8Encoding(false));
        return HubPaths.CaddyDir;
    }
}

public static class HubInstaller
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static void Install()
    {
        if (!IsAdmin()) { Console.WriteLine("Rendszergazdai parancssor kell."); return; }

        Directory.CreateDirectory(HubPaths.InstallDir);
        Directory.CreateDirectory(HubPaths.DataDir);
        var target = Path.Combine(HubPaths.InstallDir, "FleetHub.exe");
        var self = Environment.ProcessPath ?? "";

        // Frissites: a futo szolgaltatast leallitjuk, kulonben az exe zarolva van.
        // A beallitas (admin, domain, telephelyek) az adatmappaban marad.
        var upgrading = Exists();
        if (upgrading)
        {
            Console.WriteLine("Meglévő Központ frissítése – a beállítások megmaradnak…");
            try
            {
                using var sc = new ServiceController(HubPaths.ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            catch { }
            Thread.Sleep(1000);
        }

        if (!string.Equals(self, target, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(self, target, overwrite: true); }
            catch (Exception ex) { Console.WriteLine("Az exe cseréje nem sikerült: " + ex.Message); return; }
        }

        // Az adatmappa (kulcsok, jelszo-hash) csak SYSTEM és a rendszergazdak - SID-del, nyelvfuggetlenul.
        try { NativeAcl.RestrictToSystemAndAdmins(HubPaths.DataDir); }
        catch (Exception ex) { Console.WriteLine("  mappajogok: " + ex.Message); }

        try
        {
            NativeService.CreateOrUpdate(HubPaths.ServiceName, "Fleet HUB",
                "Fleet HUB - a telephelyi Fleet Serverek betárcsázása és közös felülete.", target);
        }
        catch (Exception ex) { Console.WriteLine("  hiba: " + ex.Message); return; }
        // A web-port megnyitasa a tuzfalon - kulonben masik geprol (NPM) nem erheto el.
        // A Kozpont minden interfeszen figyel.
        var cfg0 = ServerConfig.Load(HubPaths.DataDir);
        var port = cfg0.WebPort == 0 ? 8090 : cfg0.WebPort;
        HubFirewall.Ensure(port);   // minden profilon, de csak belso IP-tartomanyokbol

        NativeService.Start(HubPaths.ServiceName, TimeSpan.FromSeconds(20));

        var cfg = ServerConfig.Load(HubPaths.DataDir);
        Console.WriteLine();
        Console.WriteLine("Kész. A Központ fut, és CSAK a gépen belülről érhető el:");
        Console.WriteLine($"   http://localhost:{HubProgram.Port(cfg)}   <- itt az első beállítás (admin + domain)");
        Console.WriteLine("Kívülről az NGINX Proxy Manageren át, https-en lesz elérhető (útmutató a webes beállítás után).");
    }

    public static bool Uninstall()
    {
        if (!IsAdmin()) { Console.WriteLine("Rendszergazdai parancssor kell."); return false; }
        try
        {
            using var sc = new ServiceController(HubPaths.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch { }
        try { NativeService.Delete(HubPaths.ServiceName); } catch (Exception ex) { Console.WriteLine("  hiba: " + ex.Message); }
        var until = DateTime.UtcNow.AddSeconds(30);
        while (Exists() && DateTime.UtcNow < until) Thread.Sleep(500);
        if (Exists()) { Console.WriteLine("A szolgáltatás még nem tűnt el – zárd be a services.msc-t, vagy indítsd újra a gépet."); return false; }
        Console.WriteLine("A Központ szolgáltatás eltávolítva. Az adatok megmaradtak: " + HubPaths.DataDir);
        return true;
    }

    public static void Reinstall()
    {
        if (!IsAdmin()) { Console.WriteLine("Rendszergazdai parancssor kell."); return; }
        Console.Write("Újratelepítés: az admin beállítás törlődik, a telephelyek megmaradnak. Folytatod? (i/n): ");
        if (!(Console.ReadLine() ?? "").Trim().StartsWith("i", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uninstall()) return;
        var cfg = ServerConfig.Load(HubPaths.DataDir);
        cfg.ResetSetup();
        if (!cfg.Save(HubPaths.DataDir)) { Console.WriteLine("A beállítás nem írható: " + ServerConfig.PathFor(HubPaths.DataDir)); return; }
        Install();
    }

    /// <summary>Az elso nem-loopback IPv4 cim - az NPM ezen a cimen eri el a Kozpontot.</summary>
    public static string LocalIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(a.Address))
                        return a.Address.ToString();
            }
        }
        catch { }
        return "A-KOZPONT-IP-CIME";
    }

    private static bool Exists() => NativeService.Exists(HubPaths.ServiceName);
}
