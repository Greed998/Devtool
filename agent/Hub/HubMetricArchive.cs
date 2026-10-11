using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevToolbox.Fleet.Server;
using Microsoft.Data.Sqlite;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// A telephelyek lezárt napi mérési fájljai a Központban (1.5.8, a felhasználó kérése: „csak lezárt fájl tölthető fel”, és ne
/// legyen kettő ugyanabból). A telephely a megőrzése (alap 30 nap) után feltölti, és a nyugta után törli a sajátját; itt
/// telephelyenként tároljuk (metrics-telephelyek\&lt;telephely&gt;\metrics-ÉÉÉÉ-HH-NN.db), a MonKeepDays-ig (alap 90 nap)
/// olvasható – a gép grafikonja a régi időszakra ebből rajzol –, utána ZIP-archívum a MetricArchiveDays-ig (alap 180), majd
/// törlés. A beérkező fájl az adott napra kiváltja a Központ saját 5 perces másolatát (az törlődik a telephely gépeire).
/// </summary>
public static class HubMetricArchive
{
    public const string Folder = "metrics-telephelyek";
    public const long MaxUpload = 512L * 1024 * 1024, MaxUnpacked = 4L * 1024 * 1024 * 1024;
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant);
    private static string _root = "";
    private static DateTime _lastMaintain = DateTime.MinValue;

    public static void Init(string metricsDir) => _root = Path.Combine(metricsDir, Folder);

    public static string? DirOf(string siteId) => SafeId.IsMatch(siteId) && _root.Length > 0 ? Path.Combine(_root, siteId) : null;

    public static List<MetricStore.DayFile> Files(string siteId) =>
        DirOf(siteId) is { } d ? MetricStore.DayFilesIn(d) : new();

    /// <summary>A gép idősora a feltöltött fájlokból (a Központ saját adata mellé).</summary>
    public static List<MetricStore.Point> Series(string siteId, string agentId, string key, int hours) =>
        MetricStore.SeriesFromFiles(Files(siteId), agentId, key, hours);

    /// <summary>12 óránként: olvasható → archívum (ZIP) → törlés.</summary>
    public static void Maintain(int keepDays, int archiveDays)
    {
        if (DateTime.UtcNow - _lastMaintain < TimeSpan.FromHours(12) || !Directory.Exists(_root)) return;
        _lastMaintain = DateTime.UtcNow;
        foreach (var d in Directory.GetDirectories(_root))
            try { MetricStore.MaintainDir(d, keepDays, keepDays, archiveDays, holdForUpload: false); } catch { }
    }

    /// <summary>
    /// Egy feltöltés fogadása: a fejléc a telephely kulcsával aláírt (friss, egyszeri nonce), a törzs mérete és SHA-256-ja
    /// egyezik, kicsomagolva érvényes SQLite (samples tábla), és minden sora a megadott UTC-napra esik. A válasz a Központ
    /// kulcsával aláírt nyugta – a telephely csak erre törli a sajátját.
    /// </summary>
    public static Task<SignedEnvelope> Receive(HttpListenerContext ctx, HubStore store, MetricStore own, string? tunnelIp = null) =>
        Receive(ctx.Request.Headers["X-Fleet-Envelope"] ?? "", ctx.Request.InputStream, store, own, tunnelIp);

    /// <summary>A fogadás magja (a fejléc base64 SignedEnvelope-ja és a törzs) – a teszt ezt hívja.</summary>
    public static async Task<SignedEnvelope> Receive(string envelopeHeader, Stream body, HubStore store, MetricStore own, string? tunnelIp = null)
    {
        MetricFileHeader? h = null;
        SignedEnvelope Reply(bool ok, string msg) => store.Sign(new MetricFileReceipt
        { Ok = ok, Message = msg, Day = h?.Day ?? "", Sha256 = h?.Sha256 ?? "", RequestNonce = h?.Nonce ?? "" });

        SignedEnvelope? env;
        try { env = ProtocolJson.Deserialize<SignedEnvelope>(Encoding.UTF8.GetString(Convert.FromBase64String(envelopeHeader))); }
        catch { env = null; }
        h = env?.Open<MetricFileHeader>();
        if (env is null || h is null) return Reply(false, "Hibás fejléc.");
        var site = store.VerifySite(env, h.Timestamp, h.Nonce);
        if (site is null) return Reply(false, "Érvénytelen aláírás, ismeretlen telephely vagy ismételt kérés.");
        if (store.WgRequired?.Invoke(site) == true && tunnelIp != site.WgAddress) return Reply(false, "Ez a telephely csak a WireGuard-alagúton küldhet adatot.");
        if (!MetricStore.TryDay(h.Day, out var start)) return Reply(false, "Hibás nap.");
        // Csak lezárt nap: legalább 48 órával a vége után (a telephely sem zárja le korábban).
        if (start + 86400 + 172800 > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return Reply(false, "Ez a nap még nincs lezárva.");
        if (h.Size <= 0 || h.Size > MaxUpload || !Regex.IsMatch(h.Sha256 ?? "", "^[0-9A-Fa-f]{64}$")) return Reply(false, "Hibás méret vagy lenyomat.");
        if (DirOf(site.SiteId) is not { } dir) return Reply(false, "Hibás telephely-azonosító.");

        Directory.CreateDirectory(dir);
        var gz = Path.Combine(dir, $".{h.Day}-{Guid.NewGuid():N}.gz");
        var db = Path.Combine(dir, $".{h.Day}-{Guid.NewGuid():N}.db");
        try
        {
            // 1. a törzs (méretkorláttal) és a lenyomata
            long n = 0;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var o = File.Create(gz))
                {
                    var buf = new byte[81920]; int r;
                    while ((r = await body.ReadAsync(buf)) > 0)
                    {
                        n += r;
                        if (n > h.Size) return Reply(false, "A törzs nagyobb a fejlécben megadottnál.");
                        sha.AppendData(buf, 0, r);
                        await o.WriteAsync(buf.AsMemory(0, r));
                    }
                }
                if (n != h.Size || !Convert.ToHexString(sha.GetHashAndReset()).Equals(h.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Reply(false, "A feltöltött fájl lenyomata vagy mérete nem egyezik.");
            }
            // 2. kicsomagolás (méretkorláttal)
            await using (var i = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress))
            await using (var o = File.Create(db))
            {
                var buf = new byte[81920]; int r; long u = 0;
                while ((r = await i.ReadAsync(buf)) > 0)
                {
                    u += r;
                    if (u > MaxUnpacked) return Reply(false, "A kicsomagolt fájl túl nagy.");
                    await o.WriteAsync(buf.AsMemory(0, r));
                }
            }
            // 3. ellenőrzés: érvényes SQLite, samples tábla, minden sor erre a napra
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                c.Open();
                using var q = c.CreateCommand();
                q.CommandText = "SELECT COUNT(*),MIN(ts),MAX(ts) FROM samples";
                using var rd = q.ExecuteReader();
                if (!rd.Read()) return Reply(false, "Üres vagy hibás mérési fájl.");
                if (rd.GetInt64(0) > 0 && (rd.GetInt64(1) < start || rd.GetInt64(2) >= start + 86400)) return Reply(false, "A fájlban más napra eső mérés van.");
            }
            SqliteConnection.ClearAllPools();
            // 4. a helyére (egy újrafeltöltött nap felülírja az előzőt), és a Központ saját másolata erre a napra törlődik
            var final = Path.Combine(dir, $"metrics-{h.Day}.db");
            File.Move(db, final, true);
            var agents = (site.LastSummary?.Agents ?? new()).Select(a => a.AgentId).ToHashSet();
            try { own.PurgeRange(agents, start, start + 86400); } catch { }
            HubLog.Write($"Mérési napi fájl a telephelyről: {site.Name} {h.Day} ({n / 1024} KB tömörítve)");
            return Reply(true, "");
        }
        catch (Exception ex) { return Reply(false, "Hiba a fogadáskor: " + ex.Message); }
        finally
        {
            try { File.Delete(gz); } catch { }
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }
}
