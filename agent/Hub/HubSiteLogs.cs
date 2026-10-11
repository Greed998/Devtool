using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// Központi napló (1.5.7, a felhasználó döntése: a HUB tárolja a telephelyek naplóit, az USB-napló nélkül). Telephelyenként
/// és naplónként egy fájl a Központ naplómappájában: logs\telephelyek\&lt;telephely&gt;\&lt;napló&gt;.log, 10 MB-onként forgatva,
/// a legutóbbi 5 fájl marad (naplónként legfeljebb ~50 MB). A felület visszafelé olvassa és szűri.
/// </summary>
public static class HubSiteLogs
{
    /// <summary>A fogadott naplók és a felületi nevük. Más nevű darabot nem fogadunk (pl. az USB-naplót sem).</summary>
    public static readonly (string Log, string Label)[] Logs =
        { ("audit", "Műveleti napló"), ("alerts", "Riasztások"), ("snmp", "SNMP-trapek"), ("server", "Szerver napló") };

    public const long MaxFile = 10L * 1_048_576;   // 10 MB (1024 × 1024)
    public const int Keep = 5;
    public const int MaxChunk = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, object> Locks = new();
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant);

    public static string Root => Path.Combine(LogFolder.Dir(HubPaths.DataDir), "telephelyek");

    private static string? FileOf(string siteId, string log, int n = 0)
    {
        if (!SafeId.IsMatch(siteId) || !Logs.Any(l => l.Log == log)) return null;
        var f = Path.Combine(Root, siteId, log + ".log");
        return n == 0 ? f : f + "." + n;
    }

    /// <summary>A betárcsázással jött naplórészek hozzáfűzése (a telephely csak egész sorokat küld).</summary>
    public static void Append(HubSite site, List<SiteLogChunk> chunks)
    {
        var l = Locks.GetOrAdd(site.SiteId, _ => new object());
        lock (l)
            foreach (var c in chunks.Take(20))
            {
                var path = FileOf(site.SiteId, c.Log);
                if (path is null || c.Text.Length == 0) continue;
                var text = c.Text.Length > MaxChunk ? c.Text[..MaxChunk] : c.Text;
                if (!text.EndsWith('\n')) text += "\n";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxFile) Rotate(path);
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
    }

    private static void Rotate(string path)
    {
        var last = path + "." + (Keep - 1);
        if (File.Exists(last)) File.Delete(last);
        for (var i = Keep - 2; i >= 1; i--)
            if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
        File.Move(path, path + ".1", true);
    }

    /// <summary>
    /// A legutóbbi sorok (legújabb elöl), opcionálisan szűrve (kis-nagybetű nélkül, minden szónak benne kell lennie).
    /// Visszafelé olvas a fájlokon át, amíg max találat meg nem lesz; scanned: hány sort nézett át.
    /// </summary>
    public static (List<string> Lines, int Scanned, long TotalBytes) Tail(string siteId, string log, string q, int max = 500)
    {
        var words = (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var res = new List<string>();
        var scanned = 0; long total = 0;
        var l = Locks.GetOrAdd(siteId, _ => new object());
        lock (l)
            for (var n = 0; n < Keep && res.Count < max; n++)
            {
                var f = FileOf(siteId, log, n);
                if (f is null || !File.Exists(f)) { if (n == 0) continue; break; }
                total += new FileInfo(f).Length;
                var lines = File.ReadAllLines(f, Encoding.UTF8);
                for (var i = lines.Length - 1; i >= 0 && res.Count < max; i--)
                {
                    scanned++;
                    var s = lines[i];
                    if (s.Length == 0) continue;
                    if (words.All(w => s.Contains(w, StringComparison.CurrentCultureIgnoreCase))) res.Add(s.Length > 4000 ? s[..4000] + "…" : s);
                }
            }
        return (res, scanned, total);
    }

    /// <summary>A telephely tárolt naplóinak mérete naplónként (a felületnek).</summary>
    public static Dictionary<string, long> Sizes(string siteId)
    {
        var d = new Dictionary<string, long>();
        foreach (var (log, _) in Logs)
        {
            long s = 0;
            for (var n = 0; n < Keep; n++) if (FileOf(siteId, log, n) is { } f && File.Exists(f)) s += new FileInfo(f).Length;
            d[log] = s;
        }
        return d;
    }
}
