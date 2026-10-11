using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

public sealed class WingetPackage
{
    public string Id = "";
    public string Name = "";
    public string InstalledVersion = "";
    public string AvailableVersion = "";
    public string Source = "";

    public bool HasUpdate =>
        AvailableVersion.Length > 0 &&
        !AvailableVersion.Equals(InstalledVersion, StringComparison.OrdinalIgnoreCase) &&
        !AvailableVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
}

public sealed record WingetResult(bool Ok, int ExitCode, string Output);

/// <summary>
/// A winget (Windows Package Manager) burkolasa a HELYI gepen. Tavoli gepen nem fut: ott az agent
/// frissit (feladatkent, a Fleet Serveren at - lasd AppUpdaterForm); a korabbi PowerShell Remoting-os
/// ut megszunt (a felhasznalo dontese, 1.5.7).
///
/// A winget kimenete tablazatos szoveg, oszlopfejlecekkel. A fejlec pozicioja adja meg
/// az oszlophataroket - ezt olvassuk ki, mert a nyelvezet es a szohossz gepenkent valtozik.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Winget
{
    private static string? _cachedPath;

    /// <summary>Elerheto-e a winget a HELYI gepen.</summary>
    public static bool IsAvailable => FindWinget() is not null;

    public static string? FindWinget()
    {
        if (_cachedPath is not null) return _cachedPath.Length > 0 ? _cachedPath : null;

        // A PATH altalaban tartalmazza, de a WindowsApps mappat is erdemes megnezni.
        var candidates = new List<string>();
        try
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (var d in paths)
                if (!string.IsNullOrWhiteSpace(d))
                    candidates.Add(Path.Combine(d.Trim(), "winget.exe"));
        }
        catch { }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"));
        }
        catch { }

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) { _cachedPath = c; return c; } } catch { }
        }

        _cachedPath = "";
        return null;
    }

    // ================================================================ helyi

    /// <summary>Frissitheto csomagok lekerese: winget upgrade.</summary>
    public static async Task<List<WingetPackage>> GetUpgradableAsync(CancellationToken token)
    {
        var exe = FindWinget();
        if (exe is null) return new();

        var res = await RunAsync(exe,
            "upgrade --include-unknown --accept-source-agreements --disable-interactivity",
            token);

        return ParseUpgradeTable(res.Output);
    }

    /// <summary>Minden telepitett csomag: winget list.</summary>
    public static async Task<List<WingetPackage>> GetInstalledAsync(CancellationToken token)
    {
        var exe = FindWinget();
        if (exe is null) return new();

        var res = await RunAsync(exe,
            "list --accept-source-agreements --disable-interactivity", token);

        return ParseUpgradeTable(res.Output);
    }

    /// <summary>Egy csomag frissitese. A kimenetet folyamatosan visszaadja.</summary>
    public static async Task<WingetResult> UpgradeAsync(
        string id, Action<string>? onOutput, CancellationToken token)
    {
        var exe = FindWinget();
        if (exe is null) return new(false, -1, "winget nem található");

        return await RunStreamingAsync(exe,
            $"upgrade --id \"{id}\" --exact --silent --accept-package-agreements " +
            "--accept-source-agreements --disable-interactivity",
            onOutput, token);
    }

    public static async Task<WingetResult> UpgradeAllAsync(
        Action<string>? onOutput, CancellationToken token)
    {
        var exe = FindWinget();
        if (exe is null) return new(false, -1, "winget nem található");

        return await RunStreamingAsync(exe,
            "upgrade --all --silent --accept-package-agreements " +
            "--accept-source-agreements --disable-interactivity",
            onOutput, token);
    }

    public static async Task<WingetResult> InstallAsync(
        string id, bool machineScope, Action<string>? onOutput, CancellationToken token)
    {
        var exe = FindWinget();
        if (exe is null) return new(false, -1, "winget nem található");

        var scope = machineScope ? "--scope machine " : "";
        return await RunStreamingAsync(exe,
            $"install --id \"{id}\" --exact --silent --source winget {scope}" +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            onOutput, token);
    }

    // ================================================================ futtatás

    private static async Task<WingetResult> RunAsync(string exe, string args, CancellationToken token)
    {
        var sb = new StringBuilder();
        var code = await RunCore(exe, args, line => sb.AppendLine(line), token);
        return new WingetResult(code == 0, code, sb.ToString());
    }

    private static async Task<WingetResult> RunStreamingAsync(
        string exe, string args, Action<string>? onOutput, CancellationToken token)
    {
        var sb = new StringBuilder();
        var code = await RunCore(exe, args, line => { sb.AppendLine(line); onOutput?.Invoke(line); }, token);
        return new WingetResult(code == 0 || code == -1978335189, code, sb.ToString());
    }

    private static async Task<int> RunCore(
        string exe, string args, Action<string> onLine, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // A winget haladasjelzo karaktereket kuld; ezek nelkul tisztabb a kimenet.
        psi.EnvironmentVariables["WINGET_DISABLE_INTERACTIVITY"] = "1";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(CleanLine(e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null && e.Data.Trim().Length > 0) onLine(CleanLine(e.Data)); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try { await p.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }

        try { return p.ExitCode; } catch { return -1; }
    }

    // ================================================================ elemzés

    /// <summary>A winget haladasjelzo és vezerlokarakterei eltavolitasa.</summary>
    private static string CleanLine(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c is '\r' or '\b') continue;
            if (c < 32 && c != '\t') continue;
            // A winget spinner Braille- és blokk-karaktereket használ.
            if (c is '█' or '▒' or '░' or '▓' or '−' or '\u2801' or '\u2802' or '\u2804') continue;
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A winget tablazat feldolgozasa. A fejlecsor oszlopkezdo-poziciói adjak a hataroket,
    /// mert a szohossz es a nyelvezet gepfuggo. A fejlec neve is nyelvfuggo, ezert a
    /// pozicio-alapu darabolashoz keressuk a "----" elvalasztosort.
    /// </summary>
    public static List<WingetPackage> ParseUpgradeTable(string output)
    {
        var list = new List<WingetPackage>();
        var lines = output.Replace("\r", "").Split('\n');

        int headerIdx = -1;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            // Az elvalasztosor csupa '-' (esetleg szokozzel). Alatta jonnek az adatsorok.
            var t = lines[i + 1].Trim();
            if (t.Length >= 10 && t.All(c => c is '-' or ' ') && t.Contains("---")
                && lines[i].Trim().Length > 0)
            {
                headerIdx = i;
                break;
            }
        }
        if (headerIdx < 0) return list;

        var header = lines[headerIdx];

        // Oszlopkezdetek: a fejlec szavainak kezdo indexe.
        var cols = ColumnStarts(header);
        if (cols.Count < 2) return list;

        for (int i = headerIdx + 2; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (line.Contains(" upgrades available")) continue;
            if (line.TrimStart().StartsWith("The following")) continue;
            if (line.Contains("upgrade")) { /* zaro info sorok */ }

            var cells = SplitByColumns(line, cols);
            if (cells.Count < 2) continue;

            var name = cells[0].Trim();
            var id = cells.Count > 1 ? cells[1].Trim() : "";
            if (id.Length == 0 || name.Length == 0) continue;
            // Az utolso "N upgrades available" jellegu sor kiszurese
            if (id.Contains(' ') && !id.Contains('.')) continue;

            var pkg = new WingetPackage
            {
                Name = name,
                Id = id,
                InstalledVersion = cells.Count > 2 ? cells[2].Trim() : "",
                AvailableVersion = cells.Count > 3 ? cells[3].Trim() : "",
                Source = cells.Count > 4 ? cells[4].Trim() : ""
            };

            // A "list" kimenetben nincs elerheto-verzio oszlop, csak forras.
            if (pkg.AvailableVersion.Equals("winget", StringComparison.OrdinalIgnoreCase))
            {
                pkg.Source = pkg.AvailableVersion;
                pkg.AvailableVersion = "";
            }

            list.Add(pkg);
        }

        return list;
    }

    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        bool inWord = false;
        for (int i = 0; i < header.Length; i++)
        {
            var space = header[i] == ' ';
            if (!space && !inWord) { starts.Add(i); inWord = true; }
            else if (space && inWord)
            {
                // Egyetlen szokoz meg nem oszlophatar (a fejlecnevek tobb szavasak lehetnek).
                if (i + 1 < header.Length && header[i + 1] == ' ') inWord = false;
            }
        }
        return starts;
    }

    private static List<string> SplitByColumns(string line, List<int> starts)
    {
        var cells = new List<string>();
        for (int c = 0; c < starts.Count; c++)
        {
            var from = starts[c];
            var to = c + 1 < starts.Count ? starts[c + 1] : line.Length;
            if (from >= line.Length) { cells.Add(""); continue; }
            to = Math.Min(to, line.Length);
            cells.Add(line[from..to]);
        }
        return cells;
    }
}
