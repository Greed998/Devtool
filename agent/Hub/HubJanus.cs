using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// Kapcsolat a Janus ügyviteli rendszerrel (a felhasználó döntése: a Központ küld, minden telephely nevében).
/// A Janus fogadó végpontja: POST {JanusUrl} (api/fleet.php), fejlécek: X-Janus-Key / -Timestamp / -Nonce / -Signature,
/// aláírás = hex(HMAC-SHA256(titok, timestamp + "\n" + nonce + "\n" + nyers törzs)). A Janusban ügyfelenként (telephelyenként)
/// létrehozott kulcsot és titkot a Központban telephelyenként adod meg; a titok DPAPI-val titkosítva, a felületre nem megy vissza.
///
/// Mit küld: géplista („inventory”, 15 percenként vagy változáskor) és a Központ riasztásai („alert”: küszöb, kiesés;
/// a megszűnés „resolved”). A Janus a figyelmeztető / kritikus riasztásból hibajegyet nyit az ügyfélnek.
/// </summary>
public static class HubJanus
{
    /// <summary>
    /// Biztonsági átnézés (1.5.5): átirányítást NEM követünk (az aláírt kérés ne menjen más címre – egy 307/308 a törzset
    /// és a fejléceket is újraküldené), csak TLS 1.2 / 1.3, a tanúsítvány ellenőrzése a Windows szerint (kikapcsolni nem
    /// lehet), a válasz legfeljebb 64 KB, és a kapcsolatok 5 percenként újranyílnak (DNS-váltás).
    /// </summary>
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        }
    })
    { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 64 * 1024 };
    private sealed record Item(string SiteId, string Json, DateTime FirstUtc);
    private static readonly ConcurrentQueue<Item> Queue = new();
    private static readonly ConcurrentDictionary<string, (string Hash, DateTime At)> Sent = new();
    private static Func<ServerConfig> _cfg = () => new();
    private static HubStore? _store;
    private static MetricStore? _metrics;

    /// <summary>Telephelyenként az utolsó sikeres küldés és az utolsó hiba (a felületnek).</summary>
    public static readonly ConcurrentDictionary<string, (DateTime? Ok, string Error)> State = new();

    public static void Start(Func<ServerConfig> cfg, HubStore store, MetricStore metrics)
    {
        _cfg = cfg; _store = store; _metrics = metrics;
        _ = Task.Run(Loop);
    }

    private static bool Active(HubSite s) => _cfg().JanusEnabled && _cfg().JanusUrl.Length > 0 && !s.Revoked &&
                                             s.JanusKeyId.Length > 0 && s.JanusSecretProtected.Length > 0;

    private static async Task Loop()
    {
        while (true)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30)); } catch { }
            try { await Flush(); await Inventory(false); }
            catch (Exception ex) { HubLog.Write("Janus: " + ex.Message); }
        }
    }

    /// <summary>A riasztásnapló eseménye a Janusba (AlertLog.Notify hívja). A gépet / telephelyet az azonosító alapján keresi.</summary>
    public static void Enqueue(AlertEvent ev)
    {
        if (_store is null || !_cfg().JanusEnabled) return;
        HubSite? site; SiteAgent? agent = null;
        if (ev.AgentId.StartsWith("site:")) site = _store.Sites.FirstOrDefault(s => "site:" + s.SiteId == ev.AgentId);
        else
        {
            site = _store.Sites.FirstOrDefault(s => s.LastSummary?.Agents.Any(a => a.AgentId == ev.AgentId) == true);
            agent = site?.LastSummary?.Agents.FirstOrDefault(a => a.AgentId == ev.AgentId);
        }
        if (site is null || !Active(site)) return;
        var offline = ev.RuleName == AlertStore.OfflineRuleName;
        // A lezárult internet-kiesés tájékoztatás (a Janusban nem nyit jegyet): „info”, és rögtön meg is szűnik.
        var internet = ev.RuleName == InternetOutageRule;
        var key = Cut(internet ? "telephely-internet" : agent is null ? (offline ? "telephely-kieses" : "telephely:" + ev.RuleName) : (offline ? "kieses" : "kuszob:" + ev.RuleName), 150);
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "alert",
            ["key"] = key,
            ["severity"] = offline ? "kritikus" : internet ? "info" : "figyelmeztetes",
            ["message"] = Cut(ev.Raised ? $"{ev.RuleName}: {ev.Detail}" : "-", 500),
            ["hostname"] = agent?.MachineName ?? $"Fleet szerver ({site.Name})"
        };
        if (agent is not null) payload["uid"] = agent.AgentId;
        if (!ev.Raised) payload["resolved"] = true;
        Queue.Enqueue(new Item(site.SiteId, JsonSerializer.Serialize(payload), DateTime.UtcNow));
        if (internet && ev.Raised)
            Queue.Enqueue(new Item(site.SiteId, JsonSerializer.Serialize(new Dictionary<string, object?>
            { ["type"] = "alert", ["key"] = key, ["severity"] = "info", ["message"] = "-", ["hostname"] = payload["hostname"], ["resolved"] = true }), DateTime.UtcNow));
    }

    /// <summary>A telephely lezárult internet-kiesésének riasztásneve (a Központ riasztásnaplójában).</summary>
    public const string InternetOutageRule = "Internet-kiesés (telephely)";

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>A várakozó riasztások küldése sorrendben; hibánál a sor marad (legfeljebb 24 óráig, 2000 elemig).</summary>
    public static async Task Flush()
    {
        var keep = new List<Item>();
        while (Queue.TryDequeue(out var it))
        {
            var site = _store?.Sites.FirstOrDefault(s => s.SiteId == it.SiteId);
            if (site is null || !Active(site)) continue;
            var (ok, _) = await Send(site, it.Json);
            if (!ok && DateTime.UtcNow - it.FirstUtc < TimeSpan.FromHours(24)) { keep.Add(it); break; }
        }
        // Ami a hiba után még a sorban volt, az is marad - a sorrend nem borul.
        while (Queue.TryDequeue(out var rest)) keep.Add(rest);
        foreach (var k in keep.TakeLast(2000)) Queue.Enqueue(k);
    }

    /// <summary>A telephelyek géplistája: 15 percenként, vagy ha változott (gép, verzió, OS). force: azonnal mindet.</summary>
    public static async Task Inventory(bool force, string? onlySite = null)
    {
        if (_store is null) return;
        foreach (var s in _store.Sites.Where(s => Active(s) && (onlySite is null || s.SiteId == onlySite)))
        {
            var machines = (s.LastSummary?.Agents ?? new()).Where(a => !a.Revoked).Select(a =>
            {
                var m = new Dictionary<string, object?> { ["uid"] = a.AgentId, ["hostname"] = a.MachineName, ["os"] = a.OperatingSystem, ["agent_version"] = a.AgentVersion };
                var latest = _metrics?.Latest(a.AgentId);
                if (latest is not null)
                {
                    if (latest.TryGetValue("mem.totalgb", out var ram)) m["ram_gb"] = Math.Round(ram.Value, 1);
                    if (latest.TryGetValue("disk.C.totalgb", out var dt)) m["disk_total_gb"] = Math.Round(dt.Value, 1);
                    if (latest.TryGetValue("disk.C.freegb", out var df)) m["disk_free_gb"] = Math.Round(df.Value, 1);
                }
                return m;
            }).Take(2000).ToList();
            var json = JsonSerializer.Serialize(new { type = "inventory", machines });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(machines.Select(x => new { u = x["uid"], h = x["hostname"], o = x["os"], v = x["agent_version"] })))));
            if (!force && Sent.TryGetValue(s.SiteId, out var prev) && prev.Hash == hash && DateTime.UtcNow - prev.At < TimeSpan.FromMinutes(15)) continue;
            var (ok, _) = await Send(s, json);
            if (ok) Sent[s.SiteId] = (hash, DateTime.UtcNow);
        }
    }

    /// <summary>Kapcsolat-próba (életjel) egy telephely kulcsával.</summary>
    public static Task<(bool Ok, string Message)> Test(HubSite s) => Send(s, "{\"type\":\"heartbeat\"}");

    private static async Task<(bool Ok, string Message)> Send(HubSite s, string body)
    {
        var url = _cfg().JanusUrl;
        string secret;
        try { secret = NativeDpapi.Unprotect(s.JanusSecretProtected) ?? ""; } catch { secret = ""; }
        if (url.Length == 0 || secret.Length == 0) return Fail("Nincs megadva a Janus címe vagy a telephely kulcsa.");
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            // A titok bájtjai csak az aláírás idejére vannak a memóriában (utána nullázzuk).
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            string sig;
            try { sig = Convert.ToHexString(HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes($"{ts}\n{nonce}\n{body}"))).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(keyBytes); }
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Janus-Key", s.JanusKeyId); req.Headers.Add("X-Janus-Timestamp", ts);
            req.Headers.Add("X-Janus-Nonce", nonce); req.Headers.Add("X-Janus-Signature", sig);
            using var resp = await Http.SendAsync(req);
            if ((int)resp.StatusCode is >= 300 and < 400)
                return Fail($"HTTP {(int)resp.StatusCode}: a Janus címe átirányít ({Cut(resp.Headers.Location?.ToString() ?? "?", 120)}) – a pontos (végleges) címet add meg; átirányítást biztonsági okból nem követünk.");
            var text = await resp.Content.ReadAsStringAsync();
            string? err = null; bool? okField = null; long? serverTime = null;
            try
            {
                using var d = JsonDocument.Parse(text);
                if (d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) err = e.GetString();
                if (d.RootElement.TryGetProperty("ok", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False) okField = o.GetBoolean();
                if (d.RootElement.TryGetProperty("server_time", out var t) && t.TryGetInt64(out var tv)) serverTime = tv;
            }
            catch { }
            // Sikernek csak a Janus saját válasza számít ({"ok":true}): egy proxy / captive portal 200-as HTML-oldala ne
            // nyelje el csendben a riasztást (a sorban marad, és újrapróbáljuk).
            if (resp.IsSuccessStatusCode && okField == true)
            {
                State[s.SiteId] = (DateTime.UtcNow, "");
                var skew = serverTime is { } st ? Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - st) : 0;
                return (true, "A Janus válaszolt: rendben." + (skew > 60 ? $" Figyelem: a Központ és a Janus órája {skew} mp-cel eltér (5 perc fölött a kérések elutasítva)." : ""));
            }
            if (resp.IsSuccessStatusCode)
                return Fail($"HTTP {(int)resp.StatusCode}, de nem a Janus API válaszolt (rossz cím, vagy proxy / bejelentkező oldal a hálózaton).");
            return Fail($"HTTP {(int)resp.StatusCode}: {err ?? (text.TrimStart().StartsWith("<") ? "nem a Janus API válaszolt (rossz cím?)" : Cut(text, 200))}");
        }
        catch (Exception ex) { return Fail("A Janus nem érhető el: " + (ex.InnerException?.Message ?? ex.Message)); }

        (bool, string) Fail(string m)
        {
            State[s.SiteId] = (State.TryGetValue(s.SiteId, out var st) ? st.Ok : null, m);
            return (false, m);
        }
    }

    /// <summary>A Janus címének ellenőrzése: https, vagy helyi fejlesztéshez localhost.</summary>
    public static string? ValidUrl(string url)
    {
        if (url.Length == 0) return null;
        if (url.Length > 300 || url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return "Érvénytelen cím.";
        if (u.Scheme != "https" && !(u.Scheme == "http" && u.IsLoopback)) return "A Janus címe https:// legyen (titkosítás nélkül csak localhost).";
        // Felhasználónév/jelszó a címben a naplóba és a felületre kerülne; a #rész értelmetlen.
        if (u.UserInfo.Length > 0) return "A címben ne legyen felhasználónév vagy jelszó.";
        if (u.Fragment.Length > 0) return "A cím ne tartalmazzon #-részt.";
        return null;
    }
}
