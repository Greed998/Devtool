using System.Diagnostics;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Kereses a winget-tarban (programtar feltoltesehez) a szerver / Kozpont gepen levo wingettel.
/// A winget az egyetlen kulso program, amit inditunk (dontes szerint); a keresoszo argumentumlistaban
/// megy at (nem parancssor-szovegben), igy parancs nem juttathato be.
/// </summary>
public static class WingetSearch
{
    public sealed record Hit(string Name, string Id, string Version);

    public static async Task<(bool Ok, List<Hit> Results, string Error)> RunAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 60 || query.Any(c => c == '"' || char.IsControl(c)))
            return (false, new(), "Adj meg 2–60 karakteres keresőszót.");
        var exe = Find();
        if (exe is null)
            return (false, new(), "Ezen a gépen nincs winget – válassz a gépeken talált programok közül, vagy add meg az azonosítót kézzel.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in new[] { "search", "--query", query, "--source", "winget", "--count", "60",
                                  "--accept-source-agreements", "--disable-interactivity" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));   // az elso kereses a forras frissitese miatt lassu lehet
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (false, new(), "A keresés túllépte az időkorlátot."); }
            var hits = Parse(await outTask);
            return (true, hits, "");
        }
        catch (Exception ex) { return (false, new(), "A winget nem futtatható: " + ex.Message); }
    }

    /// <summary>A winget tablazatos kimenete: Nev, Azonosito, Verzio, ... (a fejlec nyelvfuggo, oszloppozicio alapjan).</summary>
    public static List<Hit> Parse(string output)
    {
        var list = new List<Hit>();
        var lines = output.Replace("\r", "").Split('\n').Select(l => l.Contains('\b') ? l[(l.LastIndexOf('\b') + 1)..] : l).ToArray();
        int header = -1;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var t = lines[i + 1].Trim();
            if (t.Length >= 10 && t.All(c => c is '-' or ' ') && lines[i].Trim().Length > 0) { header = i; break; }
        }
        if (header < 0) return list;
        var h = lines[header];
        var starts = new List<int>();
        for (int i = 0; i < h.Length; i++) if (h[i] != ' ' && (i == 0 || h[i - 1] == ' ')) starts.Add(i);
        if (starts.Count < 3) return list;
        string Cell(string l, int c) => starts[c] >= l.Length ? "" :
            (c + 1 < starts.Count ? l[starts[c]..Math.Min(l.Length, starts[c + 1])] : l[starts[c]..]).Trim();
        for (int i = header + 2; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Trim().Length == 0) continue;
            var id = Cell(l, 1);
            if (WingetId.IsValid(id)) list.Add(new Hit(Cell(l, 0), id, Cell(l, 2)));
        }
        return list;
    }

    /// <summary>A winget helye: PATH, a felhasznaloi alias, vagy (rendszerfiokban) az App Installer csomag mappaja.</summary>
    private static string? Find()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { var f = Path.Combine(d.Trim(), "winget.exe"); if (d.Trim().Length > 0 && File.Exists(f)) return f; } catch { }
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            return Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => Path.Combine(x, "winget.exe")).FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }
}
