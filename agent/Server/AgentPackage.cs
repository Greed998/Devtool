using System.Security.Cryptography;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A szerver melletti DevToolboxAgent.exe adatai (verzio, SHA-256, meret), gyorsitotarazva.
/// Ha a fajl kicserelodik (uj agent verzio), a kovetkezo lekereskor ujraszamolodik.
/// </summary>
public static class AgentPackage
{
    public sealed record Info(string Path, string Version, string Sha256, long Size);

    private static Info? _cache;
    private static DateTime _cacheTime;
    private static readonly object Lock = new();

    public static Info? Current()
    {
        var path = ServerHost.AgentExePath();
        if (path is null) return null;
        try
        {
            var fi = new FileInfo(path);
            lock (Lock)
            {
                if (_cache is not null && _cache.Path == path && _cacheTime == fi.LastWriteTimeUtc && _cache.Size == fi.Length)
                    return _cache;

                string sha;
                using (var fs = File.OpenRead(path)) sha = Convert.ToHexString(SHA256.HashData(fs));

                var ver = "";
                try { ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? ""; } catch { }
                ver = ver.Split('+')[0];   // "1.1.1+commit" -> "1.1.1"

                _cache = new Info(path, ver, sha, fi.Length);
                _cacheTime = fi.LastWriteTimeUtc;
                return _cache;
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Kell-e frissitest ajanlani. Csak ha a lenyomat mas, ES az agent nem ujabb a csomagnal
    /// (visszafejlesztes nincs). Ha a csomag verzioja nem olvashato, a lenyomat dont.
    /// </summary>
    public static bool ShouldOffer(Info pkg, string agentVersion, string agentSha)
    {
        if (agentSha.Length == 0 || string.Equals(agentSha, pkg.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (Version.TryParse(pkg.Version, out var pv) && Version.TryParse(agentVersion, out var av))
            return pv > av;
        return true;
    }
}
