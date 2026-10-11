using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.ServiceProcess;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Agent-onfrissites. A menet:
///  1. a szerver ALAIRT valaszaban ajanlat jon (verzio + SHA-256 + meret);
///  2. letoltes a szerverrol, majd a lenyomat ellenorzese - elteres eseten a fajl torlodik;
///  3. az uj exe "self-update" modban elindul, leallitja a szolgaltatast, lecsereli a regit
///     (mentes: .old), ujrainditja, es ha az uj verzio nem indul el, VISSZAALLITJA a regit.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentUpdater
{
    public static string UpdateDir => Path.Combine(AgentConfig.Dir, "update");

    /// <summary>A futo exe lenyomata (egyszer szamolva).</summary>
    public static string SelfSha { get; } = ComputeSha(Environment.ProcessPath ?? "");

    public static string ComputeSha(string path)
    {
        try { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)); }
        catch { return ""; }
    }

    public static async Task<(string? Path, string Error)> DownloadAndVerify(
        HttpClient http, string url, AgentUpdateOffer offer, CancellationToken token)
    {
        if (offer.Sha256.Length != 64) return (null, "érvénytelen lenyomat az ajánlatban");
        if (offer.Size <= 0 || offer.Size > 300_000_000) return (null, "érvénytelen méret az ajánlatban");

        Directory.CreateDirectory(UpdateDir);
        var tmp = Path.Combine(UpdateDir, $"DevToolboxAgent-{offer.Sha256[..12]}.exe");
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!resp.IsSuccessStatusCode) return (null, $"letöltés: HTTP {(int)resp.StatusCode}");

            await using (var src = await resp.Content.ReadAsStreamAsync(token))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long total = 0; int n;
                while ((n = await src.ReadAsync(buf, token)) > 0)
                {
                    total += n;
                    if (total > offer.Size) return (null, "a letöltés nagyobb az ajánlottnál – elvetve");
                    await dst.WriteAsync(buf.AsMemory(0, n), token);
                }
            }

            if (!ComputeSha(tmp).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                return (null, "a letöltött fájl lenyomata NEM egyezik az aláírttal – elvetve");
            }
            return (tmp, "");
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            return (null, "letöltés: " + ex.Message);
        }
    }

    /// <summary>Az ellenorzott uj exe elinditasa csere-modban. A szolgaltatast O allitja le.</summary>
    public static void Launch(string newExe)
    {
        var target = Environment.ProcessPath ?? "";
        Process.Start(new ProcessStartInfo(newExe, $"self-update --target \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true });
    }

    /// <summary>Az uj exe fut: csere es ellenorzes, hiba eseten visszaallitas.</summary>
    public static int RunSelfUpdate(string target)
    {
        var self = Environment.ProcessPath ?? "";
        AgentLog.Write($"Önfrissítés: {self} -> {target}");
        if (!File.Exists(target)) { AgentLog.Write("Önfrissítés: a cél nem létezik – megszakítva."); return 1; }

        Stop();
        AgentTrayHost.StopAll(target);   // 1.5.7: a felhasználók tálca-programja is ebből az exéből fut – különben zárolt maradna
        WaitExit(target);

        var bak = target + ".old";
        try { File.Copy(target, bak, overwrite: true); }
        catch (Exception ex) { AgentLog.Write("Önfrissítés: mentés nem sikerült – megszakítva: " + ex.Message); Start(); return 1; }

        var copied = false;
        for (int i = 0; i < 20 && !copied; i++)
        {
            try { File.Copy(self, target, overwrite: true); copied = true; }
            catch { Thread.Sleep(1000); }
        }
        if (!copied) { AgentLog.Write("Önfrissítés: a csere nem sikerült (zárolt fájl) – a régi marad."); Start(); return 1; }

        // Az uj verzionak el kell indulnia es futva maradnia.
        if (Start() && StaysRunning(TimeSpan.FromSeconds(15)))
        {
            AgentLog.Write("Önfrissítés kész – az új verzió fut.");
            return 0;
        }

        AgentLog.Write("Önfrissítés: az új verzió nem indult el – VISSZAÁLLÍTÁS a régire.");
        Stop(); WaitExit(target);
        try { File.Copy(bak, target, overwrite: true); } catch (Exception ex) { AgentLog.Write("Visszaállítás hiba: " + ex.Message); }
        Start();
        return 2;
    }

    /// <summary>Indulaskor a korabbi frissitesek maradek fajljainak torlese.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(UpdateDir)) return;
            foreach (var f in Directory.GetFiles(UpdateDir))
                if (!string.Equals(f, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private static void Stop()
    {
        try
        {
            using var sc = new ServiceController(AgentInstaller.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch { }
    }

    private static bool Start()
    {
        try
        {
            using var sc = new ServiceController(AgentInstaller.ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return true;
        }
        catch { return false; }
    }

    private static bool StaysRunning(TimeSpan span)
    {
        var until = DateTime.UtcNow + span;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var sc = new ServiceController(AgentInstaller.ServiceName);
                if (sc.Status != ServiceControllerStatus.Running) return false;
            }
            catch { return false; }
            Thread.Sleep(1000);
        }
        return true;
    }

    private static void WaitExit(string target)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var running = Process.GetProcessesByName("DevToolboxAgent").Where(p =>
            {
                try { return p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToList();
            if (running.Count == 0) return;
            if (DateTime.UtcNow > until.AddSeconds(-5)) foreach (var p in running) try { p.Kill(true); } catch { }
            Thread.Sleep(500);
        }
    }
}
