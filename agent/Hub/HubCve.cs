using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// Sérülékenység-keresés a Központban (1.5.6, a felhasználó kérése): a telephelyek gépeinek programjai, külső gyártótól
/// származó driverei és BIOS-a az NVD (NIST) adatbázisával párosítva, kiegészítve a CISA KEV-listával (ténylegesen
/// kihasznált hibák) és az EPSS-sel (a kihasználás valószínűsége). Naponta egyszer fut (CveHour órától), a letöltött adatok
/// gyorsítótárban maradnak (cve\), így a párosítás hálózat nélkül is frissül. Az eredmény a betárcsázás aláírt válaszában
/// megy le a telephelyre (HubCheckinReply.Cve), ott a gép riportja és az eszköz-összegzés mutatja.
///
/// A párosítás szándékosan óvatos (inkább maradjon ki egy találat, mint hogy hamis riasztás legyen):
///  - programnév → CPE (gyártó:termék): beépített táblázat, utána az NVD CPE-szótárában szigorú névegyezéssel;
///  - verzió: csak az NVD verziótartományai és pontos verziói számítanak, a „minden verzió” (*) bejegyzések nem;
///  - más platformra (Linux, macOS, Android…) szóló bejegyzés kimarad;
///  - driver: NVIDIA (a Windows-verzióból a gyártói számra váltva) és Intel grafika; BIOS: Dell, HP, Lenovo típusnévből.
/// A Microsoft saját termékeit (Windows, Office) a Windows-frissítések listája fedi le, azokat itt nem keressük.
/// </summary>
public static class HubCve
{
    // ================================================================ állapot (a felületnek)
    public sealed class State
    {
        public bool Running { get; set; }
        public string Phase { get; set; } = "";
        public int Done { get; set; }
        public int Total { get; set; }
        public string Current { get; set; } = "";
        public DateTime? StartedUtc { get; set; }
        public DateTime? LastRunUtc { get; set; }
        public int LastSeconds { get; set; }
        public string LastError { get; set; } = "";
        public int Requests { get; set; }
        public int Products { get; set; }
    }

    public static readonly State Status = new();
    private static int _running;

    private static string Dir => Path.Combine(HubPaths.DataDir, "cve");

    // ================================================================ hálózat
    /// <summary>Csak TLS 1.2 / 1.3, átirányítás nélkül; a válasz legfeljebb 64 MB (az NVD 500-as lapja néhány MB).</summary>
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }
        })
        { Timeout = TimeSpan.FromSeconds(120), MaxResponseContentBufferSize = 64L * 1024 * 1024 };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("DevToolbox-Fleet/" + HubPaths.Version);
        return h;
    }

    private static DateTime _lastNvd = DateTime.MinValue;

    /// <summary>
    /// NVD-kérés a sebességkorláttal: kulcs nélkül 5 kérés / 30 mp (ezért 6,5 mp szünet), kulccsal 50 / 30 mp.
    /// 403 / 429 / 503 esetén vár és újrapróbál.
    /// </summary>
    private static async Task<JsonDocument?> NvdGet(string url, string apiKey, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var gap = TimeSpan.FromMilliseconds(apiKey.Length > 0 ? 700 : 6500) - (DateTime.UtcNow - _lastNvd);
            if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
            _lastNvd = DateTime.UtcNow;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("apiKey", apiKey);
            Status.Requests++;
            using var resp = await Http.SendAsync(req, ct);
            if ((int)resp.StatusCode is 403 or 429 or 503 or 502 or 504)
            { await Task.Delay(TimeSpan.FromSeconds(30 * (attempt + 1)), ct); continue; }
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"NVD: HTTP {(int)resp.StatusCode}");
            var s = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(s);
        }
        throw new InvalidOperationException("NVD: túl sok kérés (sebességkorlát) – később újrapróbálom.");
    }

    private static async Task<string> GetString(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{new Uri(url).Host}: HTTP {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ================================================================ gyorsítótár
    public sealed class NvdRange
    {
        /// <summary>A feltétel verziója („*” = tartomány vagy minden verzió).</summary>
        public string V { get; set; } = "*";
        public string? Si { get; set; }
        public string? Se { get; set; }
        public string? Ei { get; set; }
        public string? Ee { get; set; }
    }

    public sealed class NvdCve
    {
        public string Id { get; set; } = "";
        public string Pub { get; set; } = "";
        public double Score { get; set; }
        public string Sev { get; set; } = "";
        public string Sum { get; set; } = "";
        public List<NvdRange> R { get; set; } = new();
    }

    public sealed class NvdProduct
    {
        public string Key { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public List<NvdCve> Cves { get; set; } = new();
    }

    private sealed class KevFile
    {
        public DateTime FetchedUtc { get; set; }
        /// <summary>CVE → zsarolóvírus-kampányban is használták-e.</summary>
        public Dictionary<string, bool> Items { get; set; } = new();
    }

    private sealed class ResultsFile
    {
        public DateTime? CheckedUtc { get; set; }
        public Dictionary<string, HubCveData> Sites { get; set; } = new();
    }

    private static readonly ConcurrentDictionary<string, NvdProduct> Products = new(StringComparer.Ordinal);
    private static Dictionary<string, CpeName> _names = new(StringComparer.Ordinal);
    private static KevFile _kev = new();
    private static Dictionary<string, EpssItem> _epss = new(StringComparer.Ordinal);
    private static ResultsFile _results = new();
    private static readonly object FileLock = new();
    private static bool _loaded;

    public sealed class CpeName { public string Cpe { get; set; } = ""; public DateTime At { get; set; } }
    public sealed class EpssItem { public double Score { get; set; } public DateTime At { get; set; } }

    /// <summary>A JSON-tömb elemei (ha nincs ilyen mező, vagy nem tömb: üres) – a hiányzó mező ne dobjon kivételt.</summary>
    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static T? ReadJson<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), ProtocolJson.Options) : null; }
        catch { return null; }
    }

    private static void WriteJson(string file, object o)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(o, ProtocolJson.Options));
            File.Move(tmp, file, true);
        }
    }

    private static string ProductFile(string key) =>
        Path.Combine(Dir, "nvd", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".json");

    /// <summary>A gyorsítótár betöltése (indításkor egyszer; a termékek csak az első használatkor).</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        _names = ReadJson<Dictionary<string, CpeName>>(Path.Combine(Dir, "nevek.json")) ?? new();
        _kev = ReadJson<KevFile>(Path.Combine(Dir, "kev.json")) ?? new();
        _epss = ReadJson<Dictionary<string, EpssItem>>(Path.Combine(Dir, "epss.json")) ?? new();
        _results = ReadJson<ResultsFile>(Path.Combine(Dir, "eredmeny.json")) ?? new();
        Status.LastRunUtc = _results.CheckedUtc;
    }

    /// <summary>A teszteknek: termék a gyorsítótárba hálózat nélkül.</summary>
    internal static void PutProduct(NvdProduct p) => Products[p.Key] = p;

    private static NvdProduct? Product(string key)
    {
        if (Products.TryGetValue(key, out var p)) return p;
        p = ReadJson<NvdProduct>(ProductFile(key));
        if (p is not null && p.Key == key) Products[key] = p;
        return p is not null && p.Key == key ? p : null;
    }

    // ================================================================ a telephely felé
    /// <summary>A telephely CVE-adatai, ha a nála levő lenyomat eltér (különben null: nem küldjük újra).</summary>
    public static HubCveData? ForSite(string siteId, string siteHash)
    {
        Load();
        lock (FileLock) return _results.Sites.TryGetValue(siteId, out var d) && d.Hash != siteHash ? d : null;
    }

    public static SiteCveAgent? ForAgent(string siteId, string agentId)
    {
        Load();
        lock (FileLock) return _results.Sites.TryGetValue(siteId, out var d) ? d.Agents.FirstOrDefault(a => a.AgentId == agentId) : null;
    }

    public static List<(string SiteId, SiteCveAgent Agent)> All()
    {
        Load();
        lock (FileLock) return _results.Sites.SelectMany(s => s.Value.Agents.Select(a => (s.Key, a))).ToList();
    }

    // ================================================================ leltár → CPE
    public sealed record Item(string Kind, string Product, string Version, string[] Keys);

    /// <summary>Beépített párosítás: a normalizált programnév (regex) → NVD gyártó:termék (több is lehet, „|”-val).</summary>
    private static readonly (Regex Name, string Cpe)[] Known = new (string, string)[]
    {
        (@"^google chrome$", "a:google:chrome"),
        (@"^mozilla firefox esr$", "a:mozilla:firefox_esr"),
        (@"^mozilla firefox$", "a:mozilla:firefox"),
        (@"^mozilla thunderbird$", "a:mozilla:thunderbird"),
        (@"^microsoft edge$", "a:microsoft:edge_chromium"),
        (@"^7-zip$", "a:7-zip:7-zip"),
        (@"^notepad\+\+$", @"a:notepad-plus-plus:notepad\+\+"),
        (@"^vlc media player$", "a:videolan:vlc_media_player"),
        (@"^adobe acrobat reader( dc)?$", "a:adobe:acrobat_reader_dc|a:adobe:acrobat_reader"),
        (@"^adobe acrobat( dc| pro| pro dc)?$", "a:adobe:acrobat_dc|a:adobe:acrobat"),
        (@"^zoom( workplace)?$", "a:zoom:meetings|a:zoom:workplace_desktop"),
        (@"^teamviewer( host)?$", "a:teamviewer:teamviewer"),
        (@"^anydesk$", "a:anydesk:anydesk"),
        (@"^rustdesk$", "a:rustdesk:rustdesk"),
        (@"^putty", "a:putty:putty"),
        (@"^winscp$", "a:winscp:winscp"),
        (@"^filezilla( client)?$", "a:filezilla-project:filezilla_client"),
        (@"^filezilla server$", "a:filezilla-project:filezilla_server"),
        (@"^git$", "a:git-scm:git|a:git_for_windows_project:git_for_windows"),
        (@"^python( core interpreter)?$", "a:python:python"),
        (@"^node\.?js$", "a:nodejs:node.js"),
        (@"^winrar$", "a:rarlab:winrar"),
        (@"^keepassxc$", "a:keepassxc:keepassxc"),
        (@"^keepass( password safe)?$", "a:keepass:keepass"),
        (@"^libreoffice$", "a:libreoffice:libreoffice"),
        (@"^vmware workstation$", "a:vmware:workstation"),
        (@"^vmware (workstation )?player$", "a:vmware:workstation_player|a:vmware:player"),
        (@"^vmware tools$", "a:vmware:tools"),
        (@"^(oracle )?(vm )?virtualbox$", "a:oracle:vm_virtualbox"),
        (@"^wireshark$", "a:wireshark:wireshark"),
        (@"^foxit (pdf )?reader$", "a:foxit:pdf_reader|a:foxitsoftware:foxit_reader"),
        (@"^foxit pdf editor$", "a:foxit:pdf_editor"),
        (@"^microsoft visual studio code$", "a:microsoft:visual_studio_code"),
        (@"^microsoft teams$", "a:microsoft:teams"),
        (@"^microsoft onedrive$", "a:microsoft:onedrive"),
        (@"^powershell( 7)?$", "a:microsoft:powershell"),
        (@"^microsoft \.net( core)? (runtime|desktop runtime|sdk)$", "a:microsoft:.net|a:microsoft:.net_core"),
        (@"^microsoft asp\.net core", @"a:microsoft:asp.net_core"),
        (@"^gimp$", "a:gimp:gimp"),
        (@"^inkscape$", "a:inkscape:inkscape"),
        (@"^audacity$", "a:audacityteam:audacity"),
        (@"^obs studio$", "a:obsproject:obs_studio"),
        (@"^paint\.net$", "a:getpaint:paint.net"),
        (@"^irfanview$", "a:irfanview:irfanview"),
        (@"^brave$", "a:brave:brave"),
        (@"^opera( stable)?$", "a:opera:opera_browser"),
        (@"^vivaldi$", "a:vivaldi:vivaldi"),
        (@"^citrix workspace$", "a:citrix:workspace"),
        (@"^forticlient", "a:fortinet:forticlient"),
        (@"^openvpn connect$", "a:openvpn:openvpn_connect"),
        (@"^openvpn$", "a:openvpn:openvpn"),
        (@"^cisco (anyconnect secure mobility client|secure client)", "a:cisco:anyconnect_secure_mobility_client|a:cisco:secure_client"),
        (@"^mysql server$", "a:oracle:mysql"),
        (@"^mariadb", "a:mariadb:mariadb"),
        (@"^postgresql$", "a:postgresql:postgresql"),
        (@"^php$", "a:php:php"),
        (@"^greenshot$", "a:getgreenshot:greenshot"),
        (@"^sumatrapdf$", "a:sumatrapdfreader:sumatrapdf"),
        (@"^veracrypt$", "a:idrix:veracrypt"),
        (@"^tightvnc$", "a:tightvnc:tightvnc"),
        (@"^(realvnc )?vnc viewer$", "a:realvnc:vnc_viewer"),
        (@"^ultravnc$", "a:uvnc:ultravnc"),
        (@"^docker desktop$", "a:docker:desktop"),
        (@"^nextcloud( desktop)?$", "a:nextcloud:desktop"),
        (@"^telegram desktop$", "a:telegram:telegram_desktop"),
        (@"^dell command \| update", "a:dell:command_update"),
        (@"^dell supportassist", "a:dell:supportassist_for_home_pcs|a:dell:supportassist_for_business_pcs"),
        (@"^lenovo system update$", "a:lenovo:system_update"),
        (@"^lenovo vantage", "a:lenovo:vantage"),
        (@"^hp support assistant$", "a:hp:support_assistant"),
        (@"^intel.*driver (&|and) support assistant$", @"a:intel:driver_\&_support_assistant"),
        (@"^nvidia geforce experience$", "a:nvidia:geforce_experience"),
        (@"^ccleaner$", "a:piriform:ccleaner"),
        (@"^malwarebytes", "a:malwarebytes:malwarebytes"),
        (@"^qbittorrent$", "a:qbittorrent:qbittorrent"),
        (@"^plex media server$", "a:plex:media_server"),
        (@"^openssl", "a:openssl:openssl"),
        (@"^curl$", "a:haxx:curl"),
    }.Select(x => (new Regex(x.Item1, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), x.Item2)).ToArray();

    /// <summary>Ezeket nem keressük a CPE-szótárban: a Windows-frissítések fedik le, vagy túl általános a nevük.</summary>
    private static readonly Regex NoLookup = new(@"^(microsoft|windows|intel|nvidia|amd|realtek|update for|security update|hotfix|vs_|vcpp|visual c\+\+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A programnév egyszerűsítése: zárójeles rész, verziószám, architektúra, nyelvi címke nélkül, kisbetűvel.</summary>
    public static string NormalizeName(string name)
    {
        var s = name.ToLowerInvariant();
        s = Regex.Replace(s, @"\([^)]*\)|\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"\s[-–]\s.*$", " ");                         // „… - 8.0.5 (x64)”, „… – magyar”
        s = Regex.Replace(s, @"\bv?\d+(\.\d+)+[a-z0-9.\-]*\b", " ");        // verziószámok
        s = Regex.Replace(s, @"\b(x64|x86|x86_64|amd64|arm64|64-bit|32-bit|64 bit|32 bit|win64|win32|(hu|en)(-[a-z]{2})?|hu-hu|en-us)\b", " ");
        s = Regex.Replace(s, @"[-\s]+(x64|x86)$", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', ',');
        return s;
    }

    /// <summary>Verzió a gépről: az eleje, ami számmal kezdődik (pl. „N32ET88W (1.64 )” → 1.64, „T76 Ver. 01.12.00” → 01.12.00).</summary>
    public static string NormalizeVersion(string v, string kind = "")
    {
        v = (v ?? "").Trim();
        if (kind == "bios")
        {
            var m = Regex.Match(v, @"\((\d+(\.\d+)+)\s*\)");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(v, @"ver\.?\s*(\d+(\.\d+)+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
        }
        var mm = Regex.Match(v, @"\d[0-9A-Za-z.\-_]*");
        return mm.Success ? mm.Value.TrimEnd('.', '-', '_') : "";
    }

    /// <summary>NVIDIA: a Windows-driver verziójából (31.0.15.3742) a gyártói szám (537.42) – az NVD ezt használja.</summary>
    public static string NvidiaVersion(string winVer)
    {
        var p = winVer.Split('.');
        if (p.Length < 4) return "";
        var digits = p[^2] + p[^1].PadLeft(4, '0');
        if (digits.Length < 5) return "";
        var last5 = digits[^5..];
        return int.Parse(last5[..3]) + "." + last5[3..];
    }

    private static string Slug(string s) =>
        Regex.Replace(s.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "_").Trim('_');

    /// <summary>A BIOS lehetséges NVD-terméknevei a gép típusából (pl. Dell Latitude 7420 → dell:latitude_7420_firmware).</summary>
    public static string[] BiosKeys(string vendor, string model)
    {
        var v = vendor.ToLowerInvariant();
        var brand = v.StartsWith("dell") ? "dell" : v.StartsWith("hp") || v.StartsWith("hewlett") ? "hp" : v.StartsWith("lenovo") ? "lenovo" : "";
        if (brand.Length == 0 || model.Trim().Length < 3) return Array.Empty<string>();
        var m = Regex.Replace(model.ToLowerInvariant(), @"^(hp|dell|lenovo)\s+", "");
        var full = Slug(m);
        var list = new List<string> { $"o:{brand}:{full}_firmware" };
        var shortM = Regex.Replace(m, @"\s+(notebook pc|desktop pc|mobile workstation|pc|tower|small form factor|sff|mt|aio|all-in-one|2-in-1)$", "");
        if (Slug(shortM) != full) list.Add($"o:{brand}:{Slug(shortM)}_firmware");
        return list.Distinct().ToArray();
    }

    /// <summary>A telephely gépeinek tételei (program / driver / BIOS) a hozzájuk tartozó NVD-termékekkel.</summary>
    private static List<Item> ItemsOf(SiteAgent a, Dictionary<string, CpeName> names)
    {
        var items = new List<Item>();
        foreach (var app in a.Installed)
        {
            var ver = NormalizeVersion(app.Version);
            if (ver.Length == 0) continue;
            var n = NormalizeName(app.Name);
            var keys = KeysForName(n, names);
            if (keys.Length > 0) items.Add(new Item("app", app.Name, ver, keys));
        }
        foreach (var c in a.Components ?? new())
        {
            if (c.Kind == "driver")
            {
                var vend = c.Vendor.ToLowerInvariant();
                var display = c.Detail.Equals("Display", StringComparison.OrdinalIgnoreCase);
                if (display && vend.Contains("nvidia") && NvidiaVersion(c.Version) is { Length: > 0 } nv)
                    items.Add(new Item("driver", c.Name, nv, new[] { "a:nvidia:gpu_display_driver" }));
                else if (display && vend.StartsWith("intel") && NormalizeVersion(c.Version) is { Length: > 0 } iv)
                    items.Add(new Item("driver", c.Name, iv, new[] { "a:intel:graphics_drivers" }));
            }
            else if (c.Kind == "bios" && BiosKeys(c.Vendor, c.Name) is { Length: > 0 } bk && NormalizeVersion(c.Version, "bios") is { Length: > 0 } bv)
                items.Add(new Item("bios", $"BIOS: {c.Vendor} {c.Name}".Trim(), bv, bk));
        }
        return items;
    }

    private static string[] KeysForName(string n, Dictionary<string, CpeName> names)
    {
        if (n.Length < 2) return Array.Empty<string>();
        foreach (var (re, cpe) in Known) if (re.IsMatch(n)) return cpe.Split('|');
        return names.TryGetValue(n, out var c) && c.Cpe.Length > 0 ? new[] { c.Cpe } : Array.Empty<string>();
    }

    /// <summary>
    /// Ismeretlen programnév keresése az NVD CPE-szótárában, szigorú egyezéssel: egyszavas névnél a termék pontosan a név
    /// és a gyártó tartalmazza (pl. greenshot → getgreenshot:greenshot); többszavasnál a termék a teljes név, vagy
    /// a gyártó az első szó és a termék a többi. Az eredmény (a „nincs” is) 30 napig érvényes.
    /// </summary>
    private static async Task<string> LookupCpe(string n, string apiKey, CancellationToken ct)
    {
        var url = "https://services.nvd.nist.gov/rest/json/cpes/2.0?resultsPerPage=200&keywordSearch=" + Uri.EscapeDataString(n);
        using var doc = await NvdGet(url, apiKey, ct);
        if (doc is null) return "";
        var t = n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Slug).Where(x => x.Length > 0).ToArray();
        if (t.Length == 0) return "";
        var joined = string.Join("_", t);
        foreach (var p in Arr(doc.RootElement, "products"))
        {
            var name = p.TryGetProperty("cpe", out var cpe) ? Str(cpe, "cpeName") ?? "" : "";
            var f = name.Split(':');
            if (f.Length < 5 || f[2] != "a") continue;
            string vendor = f[3], product = f[4];
            var ok = t.Length == 1
                ? product == t[0] && vendor.Contains(t[0], StringComparison.Ordinal)
                : product == joined || (vendor.Replace("_project", "") == t[0] && product == string.Join("_", t[1..]));
            if (ok) return $"a:{vendor}:{product}";
        }
        return "";
    }

    // ================================================================ NVD-letöltés
    /// <summary>Egy termék összes CVE-je (első alkalommal teljesen, utána csak az azóta módosultak, 110 napon belül).</summary>
    private static async Task RefreshProduct(string key, string apiKey, CancellationToken ct)
    {
        var old = Product(key);
        if (old is not null && DateTime.UtcNow - old.FetchedUtc < TimeSpan.FromHours(20)) return;
        var started = DateTime.UtcNow;
        var incremental = old is not null && DateTime.UtcNow - old.FetchedUtc < TimeSpan.FromDays(110);
        var byId = incremental ? old!.Cves.ToDictionary(c => c.Id) : new Dictionary<string, NvdCve>();
        var parts = key.Split(':');   // part:vendor:product
        var match = $"cpe:2.3:{parts[0]}:{parts[1]}:{parts[2]}";
        var baseUrl = "https://services.nvd.nist.gov/rest/json/cves/2.0?resultsPerPage=500&virtualMatchString=" + Uri.EscapeDataString(match);
        if (incremental)
            baseUrl += "&lastModStartDate=" + Uri.EscapeDataString(old!.FetchedUtc.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z")
                     + "&lastModEndDate=" + Uri.EscapeDataString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z");
        int start = 0, total;
        do
        {
            using var doc = await NvdGet(baseUrl + "&startIndex=" + start, apiKey, ct);
            if (doc is null) return;
            var root = doc.RootElement;
            total = root.TryGetProperty("totalResults", out var tr) ? tr.GetInt32() : 0;
            var page = 0;
            foreach (var v in Arr(root, "vulnerabilities"))
            {
                page++;
                if (!v.TryGetProperty("cve", out var cv)) continue;
                if (ParseCve(cv, parts) is { } c) byId[c.Id] = c;
                else if (Str(cv, "id") is { } rid) byId.Remove(rid);   // visszavont / már nem érinti
            }
            start += Math.Max(page, 1);
            if (page == 0) break;
        } while (start < total && start < 20000);
        var p = new NvdProduct { Key = key, FetchedUtc = started, Cves = byId.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList() };
        Products[key] = p;
        WriteJson(ProductFile(key), p);
    }

    /// <summary>Egy CVE tömören: azonosító, dátum, pontszám, rövid leírás és csak az ERRE a termékre vonatkozó feltételek.</summary>
    internal static NvdCve? ParseCve(JsonElement cve, string[] key)
    {
        var id = Str(cve, "id") ?? "";
        if (id.Length == 0) return null;
        if (Str(cve, "vulnStatus") is "Rejected") return null;
        var prefix = $"cpe:2.3:{key[0]}:{key[1]}:{key[2]}:";
        var ranges = new List<NvdRange>();
        foreach (var cfg in Arr(cve, "configurations"))
                foreach (var node in Arr(cfg, "nodes"))
                {
                    if (node.TryGetProperty("negate", out var ng) && ng.ValueKind == JsonValueKind.True) continue;
                    foreach (var m in Arr(node, "cpeMatch"))
                    {
                        if (!(m.TryGetProperty("vulnerable", out var vul) && vul.ValueKind == JsonValueKind.True)) continue;
                        var crit = Str(m, "criteria") ?? "";
                        if (!crit.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        var f = SplitCpe(crit);
                        // más platformra szóló bejegyzés (Linux, macOS, Android…) kimarad; a Windows és a „bármi” marad
                        var tsw = f.Length > 10 ? f[10] : "*";
                        if (tsw is not ("*" or "-") && !tsw.Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
                        var upd = f.Length > 6 ? f[6] : "*";
                        var r = new NvdRange
                        {
                            V = f.Length > 5 ? f[5] : "*",
                            Si = Str(m, "versionStartIncluding"), Se = Str(m, "versionStartExcluding"),
                            Ei = Str(m, "versionEndIncluding"), Ee = Str(m, "versionEndExcluding")
                        };
                        var hasRange = r.Si is not null || r.Se is not null || r.Ei is not null || r.Ee is not null;
                        if (!hasRange && (r.V is "*" or "-" || upd is not ("*" or "-"))) continue;   // „minden verzió” / béta: bizonytalan
                        ranges.Add(r);
                    }
                }
        if (ranges.Count == 0) return null;
        var (score, sev) = ScoreOf(cve);
        var sum = Arr(cve, "descriptions").Where(d => Str(d, "lang") == "en").Select(d => Str(d, "value")).FirstOrDefault() ?? "";
        if (sum.Length > 280) sum = sum[..277] + "…";
        var pub = Str(cve, "published") ?? "";
        return new NvdCve { Id = id, Pub = pub.Length >= 10 ? pub[..10] : pub, Score = score, Sev = sev, Sum = sum, R = ranges };
    }

    private static (double, string) ScoreOf(JsonElement cve)
    {
        if (!cve.TryGetProperty("metrics", out var ms)) return (0, "");
        foreach (var k in new[] { "cvssMetricV31", "cvssMetricV30", "cvssMetricV40", "cvssMetricV2" })
        {
            var arr = Arr(ms, k).ToList();
            if (arr.Count == 0) continue;
            // az NVD saját („Primary”) értékelése előnyben
            var pick = arr.FirstOrDefault(x => Str(x, "type") == "Primary");
            if (pick.ValueKind == JsonValueKind.Undefined) pick = arr[0];
            if (!pick.TryGetProperty("cvssData", out var data)) continue;
            var score = data.TryGetProperty("baseScore", out var bs) && bs.ValueKind == JsonValueKind.Number ? bs.GetDouble() : 0;
            var sev = Str(data, "baseSeverity") ?? Str(pick, "baseSeverity") ?? "";
            return (score, sev.Length > 0 ? sev.ToUpperInvariant() : SevOf(score));
        }
        return (0, "");
    }

    /// <summary>Csak szabályos CVE-azonosító kerülhet URL-be (EPSS).</summary>
    private static readonly Regex CveId = new(@"^CVE-\d{4}-\d{4,8}$", RegexOptions.CultureInvariant);

    private static string SevOf(double s) => s >= 9 ? "CRITICAL" : s >= 7 ? "HIGH" : s >= 4 ? "MEDIUM" : s > 0 ? "LOW" : "NONE";

    /// <summary>CPE 2.3 mezőkre bontása (a „\:” nem elválasztó).</summary>
    private static string[] SplitCpe(string cpe)
    {
        var l = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < cpe.Length; i++)
        {
            if (cpe[i] == '\\' && i + 1 < cpe.Length) { sb.Append(cpe[i]).Append(cpe[++i]); continue; }
            if (cpe[i] == ':') { l.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(cpe[i]);
        }
        l.Add(sb.ToString());
        return l.Select(x => x.Replace("\\", "")).ToArray();
    }

    // ================================================================ verzió-összevetés
    /// <summary>
    /// Verziók összevetése darabonként: a szám számként, a betű szövegként; a hiányzó darab 0 (0.81 = 0.81.0.0), és a
    /// szám nagyobb a betűnél (4.43 > 4.43 beta).
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var ta = Tokens(a); var tb = Tokens(b);
        for (var i = 0; i < Math.Max(ta.Count, tb.Count); i++)
        {
            var x = i < ta.Count ? ta[i] : "0"; var y = i < tb.Count ? tb[i] : "0";
            bool nx = x.All(char.IsDigit), ny = y.All(char.IsDigit);
            int c;
            if (nx && ny)
            {
                x = x.TrimStart('0'); y = y.TrimStart('0');
                c = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            }
            else if (nx != ny) c = nx ? 1 : -1;
            else c = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return Math.Sign(c);
        }
        return 0;
    }

    private static List<string> Tokens(string v) =>
        Regex.Matches(v ?? "", @"\d+|[A-Za-z]+").Select(m => m.Value).ToList();

    public static bool Affects(NvdRange r, string ver)
    {
        if (r.Si is null && r.Se is null && r.Ei is null && r.Ee is null) return CompareVersions(ver, r.V) == 0;
        if (r.V is not ("*" or "-") && CompareVersions(ver, r.V) != 0) return false;
        return (r.Si is null || CompareVersions(ver, r.Si) >= 0) && (r.Se is null || CompareVersions(ver, r.Se) > 0)
            && (r.Ei is null || CompareVersions(ver, r.Ei) <= 0) && (r.Ee is null || CompareVersions(ver, r.Ee) < 0);
    }

    // ================================================================ futtatás
    /// <summary>Kell-e ma futnia (bekapcsolva, CveHour elmúlt, és ma még nem futott / 20 óránál régebben).</summary>
    public static bool Due(ServerConfig cfg)
    {
        if (!cfg.CveEnabled || Status.Running) return false;
        // sikertelen kör (pl. nincs internet) után óránként próbálja újra, ne 5 percenként
        if (Status.StartedUtc is { } st && DateTime.UtcNow - st < TimeSpan.FromHours(1)) return false;
        if (Status.LastRunUtc is not { } last) return true;
        return DateTime.UtcNow - last > TimeSpan.FromHours(20) && DateTime.Now.Hour >= Math.Clamp(cfg.CveHour, 0, 23);
    }

    /// <summary>A teljes kör: leltár, CPE-párosítás, NVD / KEV / EPSS frissítés, egyeztetés. Egyszerre csak egy fut.</summary>
    public static async Task RunAsync(Func<IEnumerable<HubSite>> sites, ServerConfig cfg, Action<string> log, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Status.Running = true; Status.StartedUtc = DateTime.UtcNow; Status.LastError = ""; Status.Requests = 0; Status.Done = 0; Status.Total = 0;
        try
        {
            Load();
            var apiKey = "";
            try { apiKey = cfg.NvdKeyProtected.Length > 0 ? NativeDpapi.Unprotect(cfg.NvdKeyProtected) ?? "" : ""; } catch { }

            // 1. ismeretlen programnevek a CPE-szótárban (30 napig érvényes, a „nincs” is)
            Status.Phase = "Programnevek párosítása (NVD CPE-szótár)";
            var agents = sites().Where(s => !s.Revoked).SelectMany(s => (s.LastSummary?.Agents ?? new()).Where(a => !a.Revoked)).ToList();
            var unknown = agents.SelectMany(a => a.Installed).Select(i => NormalizeName(i.Name)).Distinct()
                .Where(n => n.Length >= 3 && !NoLookup.IsMatch(n) && !Known.Any(k => k.Name.IsMatch(n)) &&
                            (!_names.TryGetValue(n, out var c) || DateTime.UtcNow - c.At > TimeSpan.FromDays(30)))
                .Take(400).ToList();
            Status.Total = unknown.Count;
            foreach (var n in unknown)
            {
                ct.ThrowIfCancellationRequested();
                Status.Current = n;
                try { _names[n] = new CpeName { Cpe = await LookupCpe(n, apiKey, ct), At = DateTime.UtcNow }; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Status.LastError = ex.Message; break; }   // sebességkorlát / hálózat: a többi a következő körben
                Status.Done++;
                if (Status.Done % 20 == 0) WriteJson(Path.Combine(Dir, "nevek.json"), _names);
            }
            WriteJson(Path.Combine(Dir, "nevek.json"), _names);

            // 2. az érintett termékek CVE-i
            Status.Phase = "Sérülékenységek letöltése (NVD)";
            var keys = agents.SelectMany(a => ItemsOf(a, _names)).SelectMany(i => i.Keys).Distinct().OrderBy(k => k).ToList();
            Status.Products = keys.Count; Status.Total = keys.Count; Status.Done = 0;
            foreach (var k in keys)
            {
                ct.ThrowIfCancellationRequested();
                Status.Current = k.Replace("\\", "");
                try { await RefreshProduct(k, apiKey, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Status.LastError = $"{Status.Current}: {ex.Message}"; log("CVE: " + Status.LastError); }
                Status.Done++;
            }

            // 3. CISA KEV (naponta)
            Status.Phase = "CISA KEV-lista"; Status.Current = "";
            if (DateTime.UtcNow - _kev.FetchedUtc > TimeSpan.FromHours(20))
                try
                {
                    using var doc = JsonDocument.Parse(await GetString("https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json", ct));
                    var d = new Dictionary<string, bool>(StringComparer.Ordinal);
                    foreach (var v in Arr(doc.RootElement, "vulnerabilities"))
                        if (Str(v, "cveID") is { Length: > 0 } id) d[id] = Str(v, "knownRansomwareCampaignUse") == "Known";
                    if (d.Count > 100) { _kev = new KevFile { FetchedUtc = DateTime.UtcNow, Items = d }; WriteJson(Path.Combine(Dir, "kev.json"), _kev); }
                }
                catch (Exception ex) { Status.LastError = "KEV: " + ex.Message; log("CVE: " + Status.LastError); }

            // 4. egyeztetés, majd EPSS a talált CVE-kre, és még egyszer az EPSS-sel
            Status.Phase = "Egyeztetés";
            Rematch(sites);
            var found = All().SelectMany(x => x.Agent.Findings.Select(f => f.Id)).Distinct()
                .Where(id => CveId.IsMatch(id) && (!_epss.TryGetValue(id, out var e) || DateTime.UtcNow - e.At > TimeSpan.FromHours(20))).ToList();
            Status.Phase = "EPSS (kihasználási valószínűség)"; Status.Total = found.Count; Status.Done = 0;
            foreach (var chunk in found.Chunk(80))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var doc = JsonDocument.Parse(await GetString("https://api.first.org/data/v1/epss?cve=" + string.Join(",", chunk), ct));
                    foreach (var e in Arr(doc.RootElement, "data"))
                        if (Str(e, "cve") is { Length: > 0 } c && double.TryParse(Str(e, "epss"), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var sc))
                            _epss[c] = new EpssItem { Score = sc, At = DateTime.UtcNow };
                    foreach (var id in chunk) if (!_epss.ContainsKey(id)) _epss[id] = new EpssItem { Score = 0, At = DateTime.UtcNow };
                }
                catch (Exception ex) { Status.LastError = "EPSS: " + ex.Message; log("CVE: " + Status.LastError); break; }
                Status.Done += chunk.Length;
            }
            WriteJson(Path.Combine(Dir, "epss.json"), _epss);
            Rematch(sites);
            Status.LastRunUtc = DateTime.UtcNow;
            log($"CVE-ellenőrzés kész: {keys.Count} termék, {All().Sum(x => x.Agent.Findings.Count)} találat, {Status.Requests} NVD-kérés, {sw.Elapsed.TotalMinutes:0.#} perc.");
        }
        catch (OperationCanceledException) { Status.LastError = "Megszakítva."; }
        catch (Exception ex) { Status.LastError = ex.Message; log("CVE HIBA: " + ex.Message); }
        finally
        {
            Status.Running = false; Status.Phase = ""; Status.Current = ""; Status.LastSeconds = (int)sw.Elapsed.TotalSeconds;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Egyeztetés a gyorsítótárból (hálózat nélkül): a gépek mostani programjai / driverei a tárolt CVE-kkel.</summary>
    public static void Rematch(Func<IEnumerable<HubSite>> sites)
    {
        Load();
        var res = new ResultsFile { CheckedUtc = Status.LastRunUtc ?? _results.CheckedUtc };
        foreach (var s in sites().Where(s => !s.Revoked && s.LastSummary is not null))
        {
            var data = new HubCveData { CheckedUtc = res.CheckedUtc };
            foreach (var a in s.LastSummary!.Agents.Where(a => !a.Revoked))
            {
                var findings = new Dictionary<string, CveFinding>(StringComparer.Ordinal);
                foreach (var it in ItemsOf(a, _names))
                    foreach (var k in it.Keys)
                    {
                        if (Product(k) is not { } p) continue;
                        foreach (var c in p.Cves)
                        {
                            if (findings.ContainsKey(c.Id)) continue;
                            var hit = c.R.FirstOrDefault(r => Affects(r, it.Version));
                            if (hit is null) continue;
                            _kev.Items.TryGetValue(c.Id, out var rw);
                            findings[c.Id] = new CveFinding
                            {
                                Id = c.Id, Score = c.Score, Severity = c.Sev, Kev = _kev.Items.ContainsKey(c.Id), Ransomware = rw,
                                Epss = _epss.TryGetValue(c.Id, out var e) ? e.Score : 0, Kind = it.Kind, Product = it.Product,
                                // „versionEndIncluding”-nál a javított verziót az NVD nem nevezi meg: az utolsó érintett utáni
                                Version = it.Version, FixedIn = hit.Ee ?? (hit.Ei is { } ei ? ei + " után" : ""), Published = c.Pub, Summary = c.Sum
                            };
                        }
                    }
                var list = findings.Values.OrderByDescending(f => f.Kev).ThenByDescending(f => f.Score).ThenByDescending(f => f.Epss).ToList();
                data.Agents.Add(new SiteCveAgent
                {
                    AgentId = a.AgentId,
                    Critical = list.Count(f => f.Severity == "CRITICAL"), High = list.Count(f => f.Severity == "HIGH"),
                    Medium = list.Count(f => f.Severity == "MEDIUM"), Low = list.Count(f => f.Severity is "LOW" or "NONE" or ""),
                    Exploited = list.Count(f => f.Kev), Findings = list.Take(300).ToList()
                });
            }
            data.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data.Agents, ProtocolJson.Options))))[..32];
            res.Sites[s.SiteId] = data;
        }
        lock (FileLock) _results = res;
        try { WriteJson(Path.Combine(Dir, "eredmeny.json"), res); } catch { }
    }

    /// <summary>Háttérhurok: 5 percenként megnézi, kell-e futnia; közben félóránként újraegyeztet (új gép / új program).</summary>
    public static async Task LoopAsync(Func<IEnumerable<HubSite>> sites, Func<ServerConfig> cfg, Action<string> log, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        var lastMatch = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Load();
                if (Due(cfg())) await RunAsync(sites, cfg(), log, ct);
                else if (DateTime.UtcNow - lastMatch > TimeSpan.FromMinutes(30) && !Status.Running) { Rematch(sites); lastMatch = DateTime.UtcNow; }
            }
            catch (Exception ex) { log("CVE-hurok: " + ex.Message); }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}
