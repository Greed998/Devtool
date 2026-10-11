using System.Diagnostics;
using System.Runtime.Versioning;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Az agent telepitese Windows-szolgaltataskent, LocalSystem-kent, kozvetlenul a
/// szolgaltataskezelon at (NativeService) - kulso parancssor nelkul. A privat kulcs
/// mappajat csak a rendszer erheti el.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentInstaller
{
    public const string ServiceName = "DevToolboxAgent";

    public static void Install(string[] args)
    {
        var server = GetArg(args, "--server");
        var token = GetArg(args, "--token");
        var fingerprint = GetArg(args, "--fingerprint");

        // Mar regisztralt gep frissitesehez nem kell uj token - a regisztracio megmarad.
        var enrolled = AgentConfig.Load().AgentId.Length > 0;
        token ??= ""; fingerprint ??= "";
        if (string.IsNullOrWhiteSpace(server) || (string.IsNullOrWhiteSpace(token) && !enrolled))
        {
            Console.WriteLine("Használat:");
            Console.WriteLine("  DevToolboxAgent.exe install --server http://szerver:8443 " +
                              "--token <TOKEN> --fingerprint <UJJLENYOMAT>");
            return;
        }

        if (token.Length > 0 && !Crypto.IsWellFormedToken(token))
        {
            Console.WriteLine("A megadott token formátuma hibás.");
            return;
        }

        if (!IsAdmin()) { Console.WriteLine("Rendszergazdai parancssor kell (jobb klikk -> Futtatás rendszergazdaként)."); return; }

        // Beallitasok (a meglevo kulcs es azonosito megmarad - ujratelepiteskor nem kell ujraregisztralni).
        var cfg = AgentConfig.Load();
        cfg.ServerUrl = server;
        if (token.Length > 0) cfg.EnrollmentToken = token;
        if (fingerprint.Length > 0) cfg.ServerFingerprint = fingerprint;
        cfg.Save();
        RestrictConfigFolder();

        // Az exe allando helyre kerul. Korabban a szolgaltatas oda mutatott, ahonnan a
        // telepitot inditottad (a kicsomagolt ZIP) - annak torlese utan nem indult volna el.
        var exists = ServiceExists();
        if (exists) StopAndWait();
        AgentTrayHost.StopAll(TargetExe);   // 1.5.7: a futó tálca-programok zárolnák a régi exét

        Directory.CreateDirectory(InstallDir);
        var self = Environment.ProcessPath ?? "";
        if (!string.Equals(self, TargetExe, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(self, TargetExe, overwrite: true); }
            catch (Exception ex) { Console.WriteLine("Az exe másolása nem sikerült: " + ex.Message); return; }
        }
        Console.WriteLine("Telepítve ide: " + TargetExe);

        Console.WriteLine(exists ? "Meglévő szolgáltatás frissítése…" : "Szolgáltatás létrehozása…");
        try
        {
            NativeService.CreateOrUpdate(ServiceName, "DevToolbox Agent",
                "DevToolbox felügyeleti agent – kimenő kapcsolattal jelentkezik a Fleet Serverhez.", TargetExe);
        }
        catch (Exception ex) { Console.WriteLine("  hiba: " + ex.Message); return; }

        var running = NativeService.Start(ServiceName, TimeSpan.FromSeconds(20));
        Console.WriteLine();
        if (running)
        {
            Console.WriteLine("Kész. A DevToolbox Agent szolgáltatásként fut – ez az ablak bezárható.");
            Console.WriteLine("A telepítőcsomag (ZIP) törölhető.");
            Console.WriteLine("Ellenőrzés:  \"" + TargetExe + "\" diag");
        }
        else
        {
            Console.WriteLine("A szolgáltatás nem indult el. Részletek:");
            Console.WriteLine("  \"" + TargetExe + "\" diag");
            Console.WriteLine("  napló: " + AgentLog.FilePath);
        }
    }

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DevToolbox Agent");
    public static string TargetExe => Path.Combine(InstallDir, "DevToolboxAgent.exe");

    private static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool ServiceExists()
    {
        try { return System.ServiceProcess.ServiceController.GetServices().Any(x => x.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>Leallitas es varakozas; a beragadt folyamatot lezarjuk, hogy az exe ne legyen zarolva.</summary>
    private static void StopAndWait()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                Console.WriteLine("A futó agent leállítása…");
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch { }

        var self = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("DevToolboxAgent"))
        {
            try
            {
                if (p.Id == self) continue;
                string? path = null; try { path = p.MainModule?.FileName; } catch { }
                if (path is not null && !path.Equals(TargetExe, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill(true); p.WaitForExit(10000);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    public static void Uninstall()
    {
        if (!IsAdmin()) { Console.WriteLine("Rendszergazdai parancssor kell."); return; }
        StopAndWait();
        // 1.5.7: a felhasználók tálca-programjai is leállnak (különben az exe zárolva marad)
        AgentTrayHost.StopAll(TargetExe);
        try { NativeService.Delete(ServiceName); } catch (Exception ex) { Console.WriteLine("  hiba: " + ex.Message); }
        var until = DateTime.UtcNow.AddSeconds(30);
        while (ServiceExists() && DateTime.UtcNow < until) System.Threading.Thread.Sleep(500);

        Console.WriteLine("A szolgáltatás eltávolítva.");
        Console.WriteLine($"A beállítások és a kulcs megmaradtak: {AgentConfig.Dir}");
        Console.WriteLine("Teljes törléshez a mappát kézzel távolítsd el.");
    }

    /// <summary>A kulcs mappajahoz csak a SYSTEM és a rendszergazdak férjenek hozza.</summary>
    private static void RestrictConfigFolder()
    {
        // Oroklodes tiltasa + csak SYSTEM és Administrators.
        try { NativeAcl.RestrictToSystemAndAdmins(AgentConfig.Dir); }
        catch (Exception ex) { Console.WriteLine("  mappajogok: " + ex.Message); }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
