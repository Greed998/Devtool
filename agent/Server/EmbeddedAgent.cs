using System.Security.Cryptography;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A szerver exéjébe fordításkor beépített DevToolboxAgent.exe (EmbeddedResource). Így a
/// telepítőcsomaghoz és az agentek önfrissítéséhez nem kell külön fájlt a szerver mellé tenni.
/// Az első kéréskor az adatmappába kerül (az ellenőrző lenyomatot és a verziót fájlból olvassuk).
/// </summary>
public static class EmbeddedAgent
{
    public const string ResourceName = "DevToolboxAgent.exe";
    private static readonly object Lock = new();
    private static string? _path;
    private static bool _noResource;
    private static DateTime _failedAt = DateTime.MinValue;
    /// <summary>Napló - a szerver (SrvLog) és a Központ (HubLog) a sajátját állítja be.</summary>
    public static Action<string> Log { get; set; } = _ => { };

    /// <summary>
    /// A kicsomagolt agent útvonala; null, ha a szerver agent nélkül készült (pl. tesztkörnyezet).
    /// 1.4.7: a fájlnév a tartalom lenyomatát viseli (DevToolboxAgent-&lt;sha&gt;.exe), így a csere soha nem ütközik egy
    /// éppen olvasott / vírusirtó által vizsgált régi fájllal; hibánál 2 perc múlva újrapróbál (eddig az első hiba
    /// az újraindításig megmaradt, és a szerver nem ajánlotta fel az agent-frissítést).
    /// </summary>
    public static string? Extract(string dataDir)
    {
        lock (Lock)
        {
            if (_noResource) return null;
            if (_path is not null && File.Exists(_path)) return _path;
            if (DateTime.UtcNow - _failedAt < TimeSpan.FromMinutes(2)) return null;
            try
            {
                using var res = typeof(EmbeddedAgent).Assembly.GetManifestResourceStream(ResourceName);
                if (res is null) { _noResource = true; return null; }

                using var ms = new MemoryStream();
                res.CopyTo(ms);
                var bytes = ms.ToArray();
                var sha = Convert.ToHexString(SHA256.HashData(bytes));
                var dir = Path.Combine(dataDir, "beepitett-agent");
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, $"DevToolboxAgent-{sha[..12]}.exe");

                if (!(File.Exists(target) && new FileInfo(target).Length == bytes.Length))
                {
                    var tmp = target + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, target, overwrite: true);
                }
                // A korábbi verziók kicsomagolt példányai (a régi, fix nevű is) - ha épp nem használja senki.
                foreach (var old in Directory.GetFiles(dir, "DevToolboxAgent*.exe").Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    try { File.Delete(old); } catch { }
                Log($"Beépített agent kicsomagolva: {Path.GetFileName(target)} ({VersionOf(target)})");
                return _path = target;
            }
            catch (Exception ex)
            {
                _failedAt = DateTime.UtcNow;
                Log("FIGYELEM: a beépített agent nem csomagolható ki (2 perc múlva újra): " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>Az exe termékverziója; olvashatatlan fájlnál 0.0 (így a beépített nyer).</summary>
    public static Version VersionOf(string path)
    {
        try
        {
            var v = (System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "").Split('+')[0];
            return Version.TryParse(v, out var pv) ? pv : new Version(0, 0);
        }
        catch { return new Version(0, 0); }
    }
}

/// <summary>Az agent telepitocsomag (ZIP) - a FleetServer es a Kozpont is ezt adja ki.</summary>
public static class AgentZip
{
    /// <summary>
    /// Az agent telepitocsomag (ZIP): exe + a grafikus telepito elotoltese + TELEPIT.cmd + olvasd el.
    /// A FleetServer webes "Uj agent"-je es a Kozpont is ezt adja ki.
    /// </summary>
    public static byte[] Build(string exePath, string agentUrl, string token, string fingerprint, string label)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal).Open(), utf8);
                w.Write(content);
            }
            // 1. az agent exe
            using (var s = zip.CreateEntry("DevToolboxAgent.exe", System.IO.Compression.CompressionLevel.Optimal).Open())
            using (var f = File.OpenRead(exePath))
                f.CopyTo(s);
            // 2. a telepito (tokennel) - parancssoros alternativa
            Text("TELEPIT.cmd", InstallerGen.BatchFor(agentUrl, token, fingerprint, label));
            // 3. a grafikus telepito elotoltese (dupla kattintas az exe-re) - sikeres telepites utan torli magat
            Text("DevToolboxAgent.telepites.json", System.Text.Json.JsonSerializer.Serialize(new { server = agentUrl, token, fingerprint, label }));
            // 4. rovid olvassel
            Text("OLVASSEL.txt",
                "DevToolbox Agent telepitocsomag\r\n" +
                "================================\r\n\r\n" +
                "1. Csomagold ki a ZIP-et egy mappaba.\r\n" +
                "2. Dupla kattintas a DevToolboxAgent.exe-re -> grafikus telepito\r\n" +
                "   (rendszergazdai jovahagyast ker; a kapcsolodasi adatok elore kitoltve,\r\n" +
                "   a 'Kapcsolat tesztelese' telepites elott ellenorzi a szervert).\r\n" +
                "   Alternativa parancssorbol: TELEPIT.cmd -> Futtatas rendszergazdakent.\r\n\r\n" +
                "A token a TELEPIT.cmd-ben van, es rovid ideig ervenyes. Telepites utan\r\n" +
                "a csomag torolheto.\r\n");
        }
        return ms.ToArray();
    }

}
