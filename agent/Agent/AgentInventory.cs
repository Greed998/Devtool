using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Telepitett programok a registrybol - ugyanaz a forras, amit a Windows "Programok es szolgaltatasok"
/// listaja mutat. Rendszerfiokban is teljes, folyamat nelkul fut (a "winget list" SYSTEM alatt gyakran
/// ures vagy hianyos). A winget-azonositokat a winget listajabol nev szerint parositjuk hozza; a tobbi
/// program a winget sajat ARP-azonositojat kapja (ARP\Machine\X64\{kulcs}), igy az is eltavolithato.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private sealed record Entry(string Name, string Version, string ArpId, string Key, RegistryHive Hive, RegistryView View, string? UserSid);

    /// <summary>A gep telepitett programjai, a winget-azonositokkal kiegeszitve (ha vannak).</summary>
    public static List<InstalledApp> Read(IReadOnlyList<InstalledApp> wingetList)
    {
        var list = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries())
        {
            if (!seen.Add(e.Name + "|" + e.Version)) continue;   // 32/64 bites es felhasznaloi duplikatumok
            var w = MatchWinget(e.Name, wingetList);
            list.Add(new InstalledApp
            {
                Name = e.Name, Version = e.Version,
                Id = w?.Id ?? e.ArpId,
                Source = w?.Source ?? ""
            });
        }
        return list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Csendes eltavolito parancs egy ARP-azonositohoz, ha a winget nem tudta eltavolitani (pl. rendszerfiokban).
    /// MSI-nel msiexec /x, kulonben a gyarto "QuietUninstallString"-je. Parancssort (cmd) nem inditunk.
    /// </summary>
    public static (string Exe, string Args)? UninstallCommand(string arpId)
    {
        var e = Entries().FirstOrDefault(x => x.ArpId.Equals(arpId, StringComparison.OrdinalIgnoreCase));
        if (e is null) return null;
        using var key = Open(e.Hive, e.View, e.UserSid)?.OpenSubKey(e.Key);
        if (key is null) return null;
        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        if (Convert.ToInt32(key.GetValue("WindowsInstaller") ?? 0) == 1 && Guid.TryParse(Path.GetFileName(e.Key), out var code))
            return (msiexec, $"/x {code:B} /qn /norestart");

        var quiet = key.GetValue("QuietUninstallString") as string;
        if (string.IsNullOrWhiteSpace(quiet)) return null;
        var (exe, args) = SplitCommand(quiet.Trim());
        var name = Path.GetFileName(exe);
        if (name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            return null;   // parancssoros eltavolitot szandekosan nem futtatunk
        return (exe, args);
    }

    private static IEnumerable<Entry> Entries()
    {
        foreach (var (hive, view, arch, sid) in Roots())
        {
            using var root = Open(hive, view, sid);
            using var un = root?.OpenSubKey(UninstallPath);
            if (un is null) continue;
            foreach (var sub in un.GetSubKeyNames())
            {
                Entry? e = null;
                try
                {
                    using var k = un.OpenSubKey(sub);
                    if (k is null) continue;
                    var name = (k.GetValue("DisplayName") as string)?.Trim() ?? "";
                    // Rejtett rendszerelemek, frissitesek es szulohoz kotott reszek nem programok.
                    if (name.Length == 0 || Convert.ToInt32(k.GetValue("SystemComponent") ?? 0) == 1
                        || k.GetValue("ParentKeyName") is string { Length: > 0 }
                        || (k.GetValue("ReleaseType") as string) is "Update" or "Hotfix" or "Security Update") continue;
                    var scope = sid is null ? "Machine" : "User";
                    e = new Entry(name, (k.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                        $@"ARP\{scope}\{arch}\{sub}", sub, hive, view, sid);
                }
                catch { }
                if (e is not null) yield return e;
            }
        }
    }

    /// <summary>Gepszintu 64 es 32 bites ag, plusz a betoltott felhasznaloi profilok (bejelentkezett felhasznalok).</summary>
    private static IEnumerable<(RegistryHive, RegistryView, string, string?)> Roots()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, "X64", null);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, "X86", null);
        string[] users;
        try { using var hku = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64); users = hku.GetSubKeyNames(); }
        catch { users = Array.Empty<string>(); }
        foreach (var sid in users.Where(s => s.StartsWith("S-1-5-21-") && !s.EndsWith("_Classes")))
            yield return (RegistryHive.Users, RegistryView.Registry64, "X64", sid);
    }

    private static RegistryKey? Open(RegistryHive hive, RegistryView view, string? sid)
    {
        try
        {
            var b = RegistryKey.OpenBaseKey(hive, view);
            if (sid is null) return b;
            var u = b.OpenSubKey(sid);
            b.Dispose();
            return u;
        }
        catch { return null; }
    }

    /// <summary>
    /// Nev szerinti parositas a winget listajaval (csak a winget-forrasu csomagok). A winget a hosszu
    /// neveket csonkolja ("…"), ezert elotag-egyezest is elfogadunk.
    /// </summary>
    private static InstalledApp? MatchWinget(string name, IReadOnlyList<InstalledApp> winget)
    {
        foreach (var w in winget)
        {
            if (w.Source.Length == 0 || w.Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase)) continue;
            var wn = w.Name.TrimEnd('…', '.', ' ');
            if (wn.Length == 0) continue;
            if (name.Equals(wn, StringComparison.OrdinalIgnoreCase) ||
                (w.Name.EndsWith('…') && name.StartsWith(wn, StringComparison.OrdinalIgnoreCase)))
                return w;
        }
        return null;
    }

    /// <summary>"C:\Program Files\X\unins.exe" /S  ->  (exe, args). Idezojel nelkul az .exe vegeig.</summary>
    public static (string Exe, string Args) SplitCommand(string cmd)
    {
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 0 ? (cmd[1..end], cmd[(end + 1)..].Trim()) : (cmd.Trim('"'), "");
        }
        var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim()) : (cmd, "");
    }
}
