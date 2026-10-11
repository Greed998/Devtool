namespace DevToolbox.Fleet.Agent;

/// <summary>A szerver Winget-osztalyanak konnyitett valtozata az agent szamara.</summary>
public static class AgentWinget
{
    public sealed record Pkg(string Name, string Id, string Installed, string Available);

    public static string? FindWinget()
    {
        // 1. Szolgaltataskent (LocalSystem) a felhasznaloi WindowsApps alias NEM latszik,
        //    ezert a csomag telepitesi mappajabol inditjuk. Tobb verzio is lehet egymas
        //    mellett - a legujabbat valasztjuk, nem az elso betűrendit.
        try
        {
            var wa = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(wa))
            {
                var best = PickNewest(Directory.GetDirectories(wa, "Microsoft.DesktopAppInstaller_*"));
                if (best is not null) return best;
            }
        }
        catch { /* a WindowsApps listazasahoz SYSTEM vagy admin jog kell */ }

        // 2. Interaktiv futtataskor (diag, once) a PATH-ban levo alias is jo.
        try
        {
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                var f = Path.Combine(d.Trim(), "winget.exe");
                if (File.Exists(f)) return f;
            }

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(local)) return local;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// A csomagmappak neve: Microsoft.DesktopAppInstaller_1.24.25200.0_x64__8wekyb3d8bbwe.
    /// Ebbol a verzio es az architektura olvashato ki. A "neutral" nyelvi/eroforras
    /// csomagokban nincs winget.exe, azokat kihagyjuk.
    /// </summary>
    public static string? PickNewest(IEnumerable<string> dirs)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "x64"
        };

        var candidates = new List<(Version V, int ArchRank, string Exe)>();
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var parts = name.Split('_');
            if (parts.Length < 3) continue;
            if (!Version.TryParse(parts[1], out var v)) continue;

            var exe = Path.Combine(dir, "winget.exe");
            if (!File.Exists(exe)) continue;

            // A gep sajat architekturaja az elso, a tobbi csak vesztartalek.
            var rank = parts[2].Equals(arch, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            candidates.Add((v, rank, exe));
        }

        return candidates
            .OrderBy(c => c.ArchRank)
            .ThenByDescending(c => c.V)
            .Select(c => c.Exe)
            .FirstOrDefault();
    }

    /// <summary>
    /// "winget list" feldolgozasa: Nev, Azonosito, Verzio, [Elerheto], Forras. A fejlec a Windows
    /// nyelvetol fugg, ezert oszloppozicio alapjan dolgozunk; a forras mindig az utolso oszlop.
    /// </summary>
    public static List<InstalledApp> ParseList(string output)
    {
        var list = new List<InstalledApp>();
        var lines = output.Replace("\r", "").Split('\n');
        int header = -1;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var t = lines[i + 1].Trim();
            if (t.Length >= 10 && t.All(c => c is '-' or ' ') && t.Contains("---") && lines[i].Trim().Length > 0) { header = i; break; }
        }
        if (header < 0) return list;
        var starts = ColStarts(lines[header]);
        if (starts.Count < 3) return list;

        for (int i = header + 2; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var cells = Split(lines[i], starts);
            if (cells.Count < 3) continue;
            var name = cells[0].Trim(); var id = cells[1].Trim();
            if (name.Length == 0 || id.Length == 0) continue;
            var source = starts.Count >= 4 ? cells[^1].Trim() : "";
            list.Add(new InstalledApp { Name = name, Id = id, Version = cells[2].Trim(), Source = source });
        }
        return list;
    }

    /// <summary>A winget tablazatos kimenetenek feldolgozasa oszloppozicio alapjan.</summary>
    public static List<Pkg> Parse(string output)
    {
        var list = new List<Pkg>();
        var lines = output.Replace("\r", "").Split('\n');

        int header = -1;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            var t = lines[i + 1].Trim();
            if (t.Length >= 10 && t.All(c => c is '-' or ' ') && t.Contains("---")
                && lines[i].Trim().Length > 0) { header = i; break; }
        }
        if (header < 0) return list;

        var starts = ColStarts(lines[header]);
        if (starts.Count < 4) return list;

        for (int i = header + 2; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (line.Contains("upgrades available")) continue;

            var cells = Split(line, starts);
            if (cells.Count < 4) continue;

            var name = cells[0].Trim();
            var id = cells[1].Trim();
            if (name.Length == 0 || id.Length == 0 || !id.Contains('.')) continue;

            list.Add(new Pkg(name, id, cells[2].Trim(), cells[3].Trim()));
        }
        return list;
    }

    private static List<int> ColStarts(string header)
    {
        var s = new List<int>(); bool inWord = false;
        for (int i = 0; i < header.Length; i++)
        {
            var space = header[i] == ' ';
            // A winget oszlopai egyetlen szokozzel is kovethetik egymast (ha az ertekek rovidek),
            // a fejlecek pedig minden nyelven egyszavasak - ezert minden szo uj oszlopot kezd.
            if (!space && !inWord) { s.Add(i); inWord = true; }
            else if (space) inWord = false;
        }
        return s;
    }

    private static List<string> Split(string line, List<int> starts)
    {
        var cells = new List<string>();
        for (int c = 0; c < starts.Count; c++)
        {
            var from = starts[c];
            var to = c + 1 < starts.Count ? starts[c + 1] : line.Length;
            if (from >= line.Length) { cells.Add(""); continue; }
            cells.Add(line[from..Math.Min(to, line.Length)]);
        }
        return cells;
    }
}
