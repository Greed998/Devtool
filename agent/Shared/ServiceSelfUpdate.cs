using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.ServiceProcess;

namespace DevToolbox.Fleet;

/// <summary>
/// Szolgáltatás-exe cseréje külső cmd-szkript nélkül (a Fleet szerver- és Központ-szerepe használja).
/// A feltöltött új exe (*.new.exe) indul „apply-update” módban: leállítja a szolgáltatást, menti a régit
/// (*.old.exe), bemásolja magát, újraindítja, és ha az új verzió nem marad futva, visszaállítja a régit.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceSelfUpdate
{
    /// <summary>
    /// Igaz: a csere elindult, a hívó álljon le. A szolgáltatás indulásakor és közvetlenül a webes
    /// feltöltés után is hívható. Ha a *.new.exe már a telepítettel azonos (az előző csere maradéka), csak törli.
    /// </summary>
    public static bool LaunchIfPending(string serviceName, string installedExe, Action<string> log)
    {
        var incoming = IncomingPath(installedExe);
        if (!File.Exists(incoming)) return false;
        if (!string.Equals(Environment.ProcessPath ?? "", installedExe, StringComparison.OrdinalIgnoreCase)) return false;

        if (Sha(incoming) == Sha(installedExe))
        {
            try { File.Delete(incoming); } catch { }   // ha még fut a csere-folyamat, a következő indulás törli
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(incoming, $"apply-update --service \"{serviceName}\" --target \"{installedExe}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            log("Frissítés: az új exe átveszi a cserét – a szolgáltatás leáll.");
            return true;
        }
        catch (Exception ex) { log("Frissítés indítása nem sikerült: " + ex.Message); return false; }
    }

    public static string IncomingPath(string installedExe) =>
        Path.Combine(Path.GetDirectoryName(installedExe) ?? "", Path.GetFileNameWithoutExtension(installedExe) + ".new.exe");

    /// <summary>Az „apply-update” parancs: a *.new.exe-ként futó új verzió végzi a cserét.</summary>
    public static int Apply(string[] args, Action<string> log)
    {
        string? Arg(string n) { for (int i = 0; i < args.Length - 1; i++) if (args[i].Equals(n, StringComparison.OrdinalIgnoreCase)) return args[i + 1]; return null; }
        var service = Arg("--service"); var target = Arg("--target");
        var self = Environment.ProcessPath ?? "";
        if (service is null || target is null || !File.Exists(target)) { log("Frissítés: hiányzó vagy hibás paraméter."); return 1; }
        log($"Frissítés: {self} -> {target}");

        // A hívó (szolgáltatás) a webes feltöltésre még válaszol, illetve az OnStart-ból visszatér - hagyunk rá időt.
        Thread.Sleep(2000);
        NativeService.Stop(service, TimeSpan.FromSeconds(30));
        WaitExit(target);

        var bak = Path.Combine(Path.GetDirectoryName(target) ?? "", Path.GetFileNameWithoutExtension(target) + ".old.exe");
        try { File.Copy(target, bak, overwrite: true); }
        catch (Exception ex) { log("Frissítés: a mentés nem sikerült – megszakítva: " + ex.Message); Park(self, log); NativeService.Start(service, TimeSpan.FromSeconds(30)); return 1; }

        var copied = false;
        for (int i = 0; i < 20 && !copied; i++)
        {
            try { File.Copy(self, target, overwrite: true); copied = true; }
            catch { Thread.Sleep(1000); }
        }
        if (!copied) { log("Frissítés: a csere nem sikerült (zárolt fájl) – a régi marad."); Park(self, log); NativeService.Start(service, TimeSpan.FromSeconds(30)); return 1; }

        if (NativeService.Start(service, TimeSpan.FromSeconds(30)) && StaysRunning(service, TimeSpan.FromSeconds(15)))
        {
            log("Frissítés kész – az új verzió fut.");
            return 0;
        }

        log("Frissítés: az új verzió nem indult el – VISSZAÁLLÍTÁS a régire.");
        NativeService.Stop(service, TimeSpan.FromSeconds(30)); WaitExit(target);
        try { File.Copy(bak, target, overwrite: true); } catch (Exception ex) { log("Visszaállítás hiba: " + ex.Message); }
        Park(self, log);
        NativeService.Start(service, TimeSpan.FromSeconds(30));
        return 2;
    }

    /// <summary>
    /// A meghiúsult új exe félreállítása (*.hibas.exe). Törölni nem lehet, mert éppen fut, de átnevezni igen –
    /// így a szolgáltatás következő indulása nem próbálja újra és újra telepíteni.
    /// </summary>
    private static void Park(string self, Action<string> log)
    {
        try
        {
            var dir = Path.GetDirectoryName(self) ?? "";
            var name = Path.GetFileNameWithoutExtension(self).Replace(".new", "", StringComparison.OrdinalIgnoreCase);
            File.Move(self, Path.Combine(dir, name + ".hibas.exe"), overwrite: true);
            log("A sikertelen új exe félreállítva: " + name + ".hibas.exe");
        }
        catch (Exception ex) { log("Az új exe félreállítása nem sikerült: " + ex.Message); }
    }

    private static bool StaysRunning(string service, TimeSpan span)
    {
        var until = DateTime.UtcNow + span;
        while (DateTime.UtcNow < until)
        {
            if (NativeService.Status(service) != ServiceControllerStatus.Running) return false;
            Thread.Sleep(1000);
        }
        return true;
    }

    private static void WaitExit(string target)
    {
        var name = Path.GetFileNameWithoutExtension(target);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var running = Process.GetProcessesByName(name).Where(p =>
            {
                try { return p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToList();
            if (running.Count == 0) return;
            if (DateTime.UtcNow > until.AddSeconds(-5)) foreach (var p in running) try { p.Kill(true); } catch { }
            Thread.Sleep(500);
        }
    }

    private static string Sha(string path)
    {
        try { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)); }
        catch { return Guid.NewGuid().ToString(); }   // olvashatatlan: ne tekintsük egyezőnek
    }
}
