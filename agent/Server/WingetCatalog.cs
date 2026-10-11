using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A TELJES winget-tár (a „winget” forrás minden programja, kb. 15 ezer) a programtárhoz: ebből választ a
/// felhasználó a saját repójába. Forrás: a Microsoft hivatalos CDN-jén levő előre indexelt forráscsomag
/// (source2.msix → Public/index.db, SQLite, „packages” tábla) – ugyanaz, amit a winget is letölt. Ha nincs
/// internet, a gépen telepített winget-forrás (Microsoft.Winget.Source csomag) indexe a tartalék.
///
/// Naponta egyszer frissül. Az előző letöltéshez képest ÚJ programok döntésre várnak (ServerConfig.CatalogPending);
/// az első letöltés az alap, abból semmi nem kerül döntésre. Csak adatot olvasunk belőle (azonosító, név,
/// verzió, címkék): az azonosító ugyanazon az ellenőrzésen megy át, mint a kézzel megadott (WingetId.IsValid).
/// A gyorsítótár neve szándékosan nem .json: a frissítés előtti mentésbe (DataBackup) nem kell bekerülnie.
///
/// Áttekinthetőség (a felhasználó kérése, 1.5.4): minden programnak van fejlesztője (a winget-azonosító első tagja,
/// pl. „VideoLAN.VLC” → VideoLAN) és kategóriája (Irodai, Játék, Pénzügy…) – ez a tár címkéiből (tags2), címke
/// nélkül a névből jön, saját szabálylistával (Rules).
/// </summary>
public sealed class WingetCatalog
{
    public sealed record Pkg(string Id, string Name, string Version, string Category = "")
    {
        /// <summary>A fejlesztő (kiadó): a winget-azonosító első tagja – a winget így nevezi el a kiadót.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public string Publisher => PublisherOf(Id);
    }

    public static string CdnUrl { get; set; } = "https://cdn.winget.microsoft.com/cache/source2.msix";

    /// <summary>A gyorsítótár formátuma: 2-től a programok kategóriát is hordoznak (a régebbit egy frissítés pótolja).</summary>
    private const int CacheFormat = 2;

    private sealed class FileModel
    {
        public DateTime UpdatedUtc { get; set; }
        public string Source { get; set; } = "";
        public int Format { get; set; }
        public List<Pkg> Packages { get; set; } = new();
    }

    private readonly string _dir;
    private readonly object _lock = new();
    private List<Pkg> _pkgs = new();
    private Dictionary<string, Pkg> _byId = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshing;
    private int _format;

    public DateTime? UpdatedUtc { get; private set; }
    public string Source { get; private set; } = "";
    public string LastError { get; private set; } = "";
    /// <summary>Az utolsó frissítés megjegyzése (pl. forrásváltás miatt új alap) - a naplóba és a felületre.</summary>
    public string LastNote { get; private set; } = "";
    public bool Refreshing => _refreshing == 1;

    // ================================================================ fejlesztő és kategória

    /// <summary>A fejlesztő a winget-azonosítóból: az első pont előtti tag (a winget-tár elnevezési szabálya).</summary>
    public static string PublisherOf(string id)
    {
        var i = id.IndexOf('.');
        return i > 0 ? id[..i] : id;
    }

    public const string OtherCategory = "Egyéb";

    /// <summary>
    /// Kategóriák a besorolás sorrendjében (az első illeszkedő nyer): a szűkebb, egyértelmű kategória előbb, hogy pl. a
    /// TeamViewer (távelérés + „meeting” címke) ne a Kommunikációba, a Java futtatókörnyezet ne a Fejlesztőibe kerüljön.
    /// Tags: a winget-címkék (pontos egyezés, kisbetűvel); Words: címke nélküli programnál a név szavai.
    /// </summary>
    private static readonly (string Cat, HashSet<string> Tags, HashSet<string> Words)[] Rules =
    {
        R("Játék", "game games gaming video-games videogame videogames emulator emulation minecraft steam 游戏", "game games steam epic ubisoft gog battle.net minecraft roblox"),
        R("Böngésző", "browser web-browser webbrowser 浏览器 网页浏览器", "browser chrome firefox opera brave vivaldi edge chromium"),
        R("Futtatókörnyezet", "runtime redistributable redist jre vcredist 运行时", "runtime redistributable redist jre jdk .net vcredist directx"),
        R("Biztonság", "security antivirus anti-virus antimalware anti-malware malware password-manager password passwords vpn encryption encrypt firewall 2fa authenticator privacy 安全",
          "antivirus vpn password security firewall bitwarden keepass 1password eset kaspersky malwarebytes avast avg sophos norton"),
        R("Pénzügy, könyvelés", "finance accounting budget money invoice invoicing banking bank tax bookkeeping cryptocurrency crypto bitcoin wallet trading 金融 会计 财务",
          "accounting invoice finance wallet budget"),
        R("Távelérés", "remote-desktop remote-access remote-control remote-support remote remotedesktop remoteaccess vnc rdp 远程桌面 远程控制", "remote vnc rdp anydesk teamviewer"),
        R("Irodai", "office productivity pdf document documents docs word excel spreadsheet presentation powerpoint notes note-taking notetaking writing ebook calendar todo to-do task-management project-management microsoft365 office365 办公 文档",
          "office pdf word excel notes calendar libreoffice onlyoffice acrobat reader foxit notion obsidian evernote"),
        R("Mesterséges intelligencia", "ai llm chatbot large-language-model chatgpt openai gpt 人工智能 大语言模型", "ai gpt llm"),
        R("Kommunikáció", "chat messaging messenger email mail e-mail voip video-conferencing conferencing meeting meetings communication sms 聊天 邮件", "mail chat messenger teams zoom skype slack discord telegram whatsapp signal thunderbird outlook webex"),
        R("Grafika és fotó", "image image-editor image-editing photo photography photo-editing graphics design drawing paint painting 3d cad svg illustration screenshot",
          "photo image paint draw"),
        R("Multimédia", "audio video music media media-player video-player audio-player player streaming podcast podcasts multimedia dvd radio recording recorder 音频 视频 音乐",
          "player music video audio media vlc spotify itunes audacity obs handbrake mpc foobar2000 kodi plex"),
        R("Fejlesztői eszköz", "developer-tools development develop developer programming coding code ide git sdk compiler cli terminal python rust golang nodejs javascript typescript java dotnet database sql docker kubernetes devops api json debugger editor text-editor 编程 开发 代码",
          "sdk code git sql cli python node nodejs java visual studio jetbrains intellij pycharm notepad++ putty winscp docker postman"),
        R("Hálózat és felhő", "network networking ftp sftp ssh download downloader torrent bittorrent p2p proxy dns wifi internet http cloud sync file-sharing upload 网络",
          "ftp ssh torrent download cloud sync"),
        R("Rendszereszköz", "utility utilities system tool tools file-manager filemanager archive archiver compression zip backup disk partition monitor monitoring hardware driver drivers cleaner benchmark uninstaller explorer clipboard keyboard mouse desktop taskbar customization tweak recovery windows",
          "driver backup disk zip manager tool utility monitor 7-zip winrar nvidia intel amd realtek logitech dell hp lenovo asus sysinternals powertoys everything"),
    };

    private static (string, HashSet<string>, HashSet<string>) R(string cat, string tags, string words) =>
        (cat, tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
              words.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal));

    /// <summary>Az összes kategória a felület sorrendjében (az „Egyéb” a végén).</summary>
    public static IReadOnlyList<string> CategoryNames { get; } = Rules.Select(r => r.Cat).Append(OtherCategory).ToList();

    /// <summary>Besorolás: előbb a címkék (a szabályok sorrendjében), címke-találat nélkül a név szavai, végül „Egyéb”.</summary>
    public static string Categorize(string name, IReadOnlyCollection<string>? tags)
    {
        if (tags is { Count: > 0 })
            foreach (var r in Rules)
                if (tags.Any(r.Tags.Contains)) return r.Cat;
        var words = System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}+]+").Where(w => w.Length > 0).ToList();
        foreach (var r in Rules)
            if (words.Any(r.Words.Contains)) return r.Cat;
        return OtherCategory;
    }

    /// <summary>Egy program fejlesztője és kategóriája (a tárból, vagy ha nincs benne, az azonosítóból és a névből).</summary>
    public (string Publisher, string Category) Meta(string id, string name) =>
        Find(id) is { } p ? (p.Publisher, p.Category) : (PublisherOf(id), Categorize(name, null));

    /// <summary>A repó / csomagszerkesztő listájához: azonosító, név, fejlesztő, kategória.</summary>
    public List<object> Describe(IEnumerable<CatalogItem> items) =>
        items.Select(i => { var (pub, cat) = Meta(i.Id, i.Name); return (object)new { id = i.Id, name = i.Name, publisher = pub,
            category = CategoryNames.Contains(i.Category) ? i.Category : cat, manual = CategoryNames.Contains(i.Category) }; }).ToList();

    /// <summary>Kézi kategória egy repó-programnak (üres = vissza az automatikusra). Igaz, ha változott.</summary>
    public static bool SetCategory(ServerConfig cfg, string id, string category)
    {
        lock (ServerConfig.CatalogLock)
        {
            var it = cfg.SoftwareCatalog.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (it is null) return false;
            it.Category = CategoryNames.Contains(category) ? category : "";
            return true;
        }
    }

    /// <summary>
    /// Egy frissítésben ennél több „új” program nem valódi újdonság, hanem más pillanatkép (pl. az első letöltés a gép régi
    /// helyi winget-indexéből jött, a következő a CDN-ről): ilyenkor új alapot veszünk, semmi nem kerül döntésre.
    /// A winget-tárba naponta néhány tucat program kerül, egy hónap kiesés után is ezer alatt marad.
    /// </summary>
    public static int FloodLimit(int total) => Math.Max(1000, total / 20);

    public string CachePath => Path.Combine(_dir, "winget-katalogus.cache");

    public WingetCatalog(string dir)
    {
        _dir = dir;
        try
        {
            if (File.Exists(CachePath))
                Apply(JsonSerializer.Deserialize<FileModel>(File.ReadAllText(CachePath), ProtocolJson.Options) ?? new());
        }
        catch (Exception ex) { LastError = "A tárolt winget-lista sérült: " + ex.Message; }
    }

    public IReadOnlyList<Pkg> All { get { lock (_lock) return _pkgs; } }
    public int Count { get { lock (_lock) return _pkgs.Count; } }

    /// <summary>Keresés név / azonosító szerint (1.5.7, a tálca „Program kérése” ablakához): előbb a névvel kezdődők.</summary>
    public List<Pkg> Search(string q, int max = 50)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return new();
        lock (_lock)
            return _pkgs.Where(p => p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ThenBy(p => p.Name.Length)
                .Take(max).ToList();
    }
    public Pkg? Find(string id) { lock (_lock) return _byId.GetValueOrDefault(id); }

    private void Apply(FileModel m)
    {
        // A régi (1.5.4 előtti) gyorsítótárban nincs kategória: addig a névből soroljuk be, amíg egy frissítés nem pótolja.
        var list = m.Packages.Where(p => WingetId.IsValid(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(p => p.Category.Length > 0 ? p : p with { Category = Categorize(p.Name, null) })
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        lock (_lock)
        {
            _pkgs = list;
            _byId = list.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            UpdatedUtc = m.UpdatedUtc == default ? null : m.UpdatedUtc;
            Source = m.Source;
            _format = m.Format;
        }
    }

    /// <summary>A tárolt lista régi formátumú (nincs benne a címke szerinti kategória) - érdemes hamarabb frissíteni.</summary>
    public bool NeedsUpgrade { get { lock (_lock) return _pkgs.Count > 0 && _format < CacheFormat; } }

    /// <summary>
    /// Letöltés (vagy helyi tartalék), mentés, és az előzőhöz képest ÚJ programok listája. Az első letöltésnél
    /// (nincs előző) üres lista: az az alap. Hibánál null, az ok a LastError-ban; egyszerre csak egy fut.
    /// </summary>
    public async Task<List<Pkg>?> RefreshAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return null;
        var work = Path.Combine(_dir, "winget-cache");
        try
        {
            Directory.CreateDirectory(work);
            List<Pkg> fresh; string source;
            try { fresh = ReadIndex(await DownloadIndexAsync(work, ct)); source = "Microsoft CDN"; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var local = LocalIndex();
                if (local is null) { LastError = "A winget-tár nem tölthető le: " + ex.Message; return null; }
                var copy = Path.Combine(work, "helyi-index.db");
                File.Copy(local, copy, overwrite: true);   // a telepített csomag fájlját nem nyitjuk meg közvetlenül
                fresh = ReadIndex(copy); source = "a gépen telepített winget-forrás";
            }
            if (fresh.Count < 100) { LastError = $"A winget-tár gyanúsan kicsi ({fresh.Count} program) – nem cseréltük le."; return null; }

            HashSet<string> previous; string prevSource;
            lock (_lock) { previous = _pkgs.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); prevSource = Source; }
            var model = new FileModel { UpdatedUtc = DateTime.UtcNow, Source = source, Format = CacheFormat, Packages = fresh };
            var tmp = CachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(model, ProtocolJson.Options), ct);
            File.Move(tmp, CachePath, overwrite: true);
            Apply(model);
            LastError = "";
            LastNote = "";
            if (previous.Count == 0) return new();
            var added = fresh.Where(p => !previous.Contains(p.Id)).ToList();
            // Döntésre csak a valóban új programok kerülnek: forrásváltáskor (helyi tartalék ↔ CDN) vagy gyanúsan nagy
            // ugrásnál a különbség a két pillanatkép eltérése, nem újdonság - új alap (a felhasználó kérése, 1.5.4).
            if (added.Count > 0 && ((prevSource.Length > 0 && prevSource != source) || added.Count > FloodLimit(fresh.Count)))
            {
                LastNote = $"{added.Count} program eltérés a(z) „{prevSource}” és a(z) „{source}” lista között – új alap, nem kerültek döntésre.";
                return new();
            }
            return added;
        }
        catch (Exception ex) { LastError = "A winget-tár frissítése nem sikerült: " + ex.Message; return null; }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>A forráscsomag letöltése (méretkorláttal) és az index kicsomagolása. Visszaadja az index útvonalát.</summary>
    private static async Task<string> DownloadIndexAsync(string work, CancellationToken ct)
    {
        const long MaxBytes = 300_000_000;
        var msix = Path.Combine(work, "source.msix");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var resp = await http.GetAsync(CdnUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("túl nagy forráscsomag");
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(msix);
            var buf = new byte[81920]; long total = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                if ((total += n) > MaxBytes) throw new InvalidOperationException("túl nagy forráscsomag");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        var db = Path.Combine(work, "index.db");
        using (var zip = ZipFile.OpenRead(msix))
        {
            var e = zip.GetEntry("Public/index.db")
                    ?? zip.Entries.FirstOrDefault(x => x.FullName.EndsWith("/index.db", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("a forráscsomagban nincs index");
            if (e.Length > MaxBytes) throw new InvalidOperationException("túl nagy index");
            e.ExtractToFile(db, overwrite: true);
        }
        return db;
    }

    /// <summary>Teszthez: a helyi tartalék index helye (null = a valódi keresés).</summary>
    public static Func<string?>? LocalIndexOverride { get; set; }

    /// <summary>A gépen telepített winget-forrás indexe (szolgáltatásként - SYSTEM - a WindowsApps mappa listázható).</summary>
    private static string? LocalIndex()
    {
        if (LocalIndexOverride is { } o) return o();
        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            return Directory.GetDirectories(apps, "Microsoft.Winget.Source_*_neutral__8wekyb3d8bbwe")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "Public", "index.db")).FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }

    /// <summary>
    /// A winget-index (2.x séma) „packages” táblája: azonosító, név, legújabb verzió; a kategória a címkékből
    /// (tags2 + tags2_map). Címketábla nélküli indexnél a kategória a névből jön.
    /// </summary>
    public static List<Pkg> ReadIndex(string dbPath)
    {
        var list = new List<Pkg>();
        // Pooling=False: különben a kapcsolat nyitva tartja a fájlt, és az ideiglenes index nem törölhető.
        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        db.Open();
        var tags = new Dictionary<long, List<string>>();
        try
        {
            // Egyben olvasva: a leképezés kulcsa (címke, csomag), csomagonkénti lekérdezés minden sort végigolvasna.
            using var tc = db.CreateCommand();
            tc.CommandText = "SELECT m.package, t.tag FROM tags2_map m JOIN tags2 t ON t.rowid = m.tag";
            using var tr = tc.ExecuteReader();
            while (tr.Read())
            {
                if (tr.IsDBNull(0) || tr.IsDBNull(1)) continue;
                var pkg = tr.GetInt64(0);
                if (!tags.TryGetValue(pkg, out var l)) tags[pkg] = l = new List<string>();
                l.Add(tr.GetString(1).Trim().ToLowerInvariant());
            }
        }
        catch (SqliteException) { tags.Clear(); }   // régebbi / más index: címkék nélkül
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT rowid, id, name, latest_version FROM packages";
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (!WingetId.IsValid(id)) continue;
                var name = r.IsDBNull(2) ? id : r.GetString(2).Trim();
                var ver = r.IsDBNull(3) ? "" : r.GetString(3).Trim();
                name = name.Length is > 0 and <= 200 ? name : id;
                list.Add(new Pkg(id, name, ver.Length <= 60 ? ver : ver[..60], Categorize(name, tags.GetValueOrDefault(r.GetInt64(0)))));
            }
        }
        catch (SqliteException ex) { throw new InvalidOperationException("ismeretlen winget-index formátum: " + ex.Message); }
        return list;
    }

    /// <summary>
    /// Háttérfrissítés: induláskor pár perc múlva, utána óránként ellenőrzi, és ha a lista egy napnál régebbi,
    /// frissít. Az új programok a döntésre várók közé kerülnek (onNew menti a konfigurációt).
    /// </summary>
    public async Task RunLoop(Func<ServerConfig> config, Action<ServerConfig> save, Action<string> log, CancellationToken token)
    {
        // Induláskor a régi (pl. forrásváltásból felgyűlt) vagy elavult döntésre várók kitakarítása.
        Prune(config(), save, log);
        try { await Task.Delay(TimeSpan.FromMinutes(2), token); } catch { return; }
        while (!token.IsCancellationRequested)
        {
            if (UpdatedUtc is not { } u || DateTime.UtcNow - u > TimeSpan.FromHours(24) || NeedsUpgrade)
                await RefreshAndRecord(config(), save, log, token);
            try { await Task.Delay(TimeSpan.FromHours(1), token); } catch { return; }
        }
    }

    // ================================================================ webes API (közös a szerver és a Központ felületén)

    /// <summary>
    /// ShowDecided: a teljes tárban a már eldöntött (repóban levő / elutasított) programok is - alapból rejtve (a felhasználó kérése).
    /// Cat: csak ez a kategória; Pub: csak ez a fejlesztő (üres = mind).
    /// </summary>
    public sealed class ListReq { public string Filter { get; set; } = "all"; public string Q { get; set; } = ""; public int Page { get; set; } public bool ShowDecided { get; set; }
        public string Cat { get; set; } = ""; public string Pub { get; set; } = ""; }

    /// <summary>A már eldöntött programok azonosítói (repó + elutasított).</summary>
    private static HashSet<string> Decided(ServerConfig cfg) =>
        cfg.SoftwareCatalog.Concat(cfg.CatalogRejected).Concat(cfg.HubCatalogItems).Concat(cfg.HubRejected)
            .Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A teljes tár még el nem döntött programjainak száma (a menü darabszámához).</summary>
    public int Undecided(ServerConfig cfg)
    {
        lock (ServerConfig.CatalogLock) { var d = Decided(cfg); return All.Count(p => !d.Contains(p.Id)); }
    }
    /// <summary>Names: a nem winget-tárbeli (pl. gépeken talált) programok neve - csak tartaléknak, rövidítve.</summary>
    public sealed class DecideReq { public string Decision { get; set; } = ""; public List<string>? Ids { get; set; } public bool AllPending { get; set; }
        public Dictionary<string, string>? Names { get; set; } }

    /// <summary>A winget-tár állapota a felületnek.</summary>
    public object Status() => new
    {
        count = Count, updated = UpdatedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"), source = Source,
        error = LastError, refreshing = Refreshing
    };

    /// <summary>
    /// Egy lap a listából: all = a teljes winget-tár, pending = döntésre vár, rejected = elutasított, repo = saját repó.
    /// Keresés az azonosítóban és a névben (kis-nagybetű nélkül); lapméret 100.
    /// </summary>
    public object Page(ServerConfig cfg, ListReq r)
    {
        const int Size = 100;
        var q = (r.Q ?? "").Trim();
        var pub = (r.Pub ?? "").Trim();
        var cat = (r.Cat ?? "").Trim();
        bool Hit(string id, string name, string publisher) => q.Length == 0 || id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || name.Contains(q, StringComparison.OrdinalIgnoreCase) || publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
        List<(string Id, string Name, string Version, string Extra)> raw;
        HashSet<string>? decided = null;   // egyszer számolva (15 ezer sor szűrése)
        lock (ServerConfig.CatalogLock)
        {
            raw = r.Filter switch
            {
                "pending" => cfg.CatalogPending.OrderByDescending(p => p.SeenUtc).ThenBy(p => p.Name)
                    .Select(p => (p.Id, p.Name, Find(p.Id)?.Version ?? p.Version, "új: " + p.SeenUtc.ToLocalTime().ToString("yyyy.MM.dd"))).ToList(),
                // A Központ repója / elutasítottjai a telephelyen a helyiek mellett, csak olvashatóként (a Központ a mester).
                "rejected" => cfg.CatalogRejected.Select(c => (c, "")).Concat(cfg.HubRejected.Select(c => (c, "a Központ elutasította")))
                    .GroupBy(x => x.c.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(x => (x.c.Id, x.c.Name, Find(x.c.Id)?.Version ?? "", x.Item2)).ToList(),
                "repo" => cfg.SoftwareCatalog.Select(c => (c, "")).Concat(cfg.HubCatalogItems.Select(c => (c, "a Központtól")))
                    .GroupBy(x => x.c.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(x => (x.c.Id, x.c.Name, Find(x.c.Id)?.Version ?? "", x.Item2)).ToList(),
                _ => (r.ShowDecided ? All : All.Where(p => !(decided ??= Decided(cfg)).Contains(p.Id))).Select(p => (p.Id, p.Name, p.Version, "")).ToList()
            };
            // Keresés és fejlesztő-szűrés után a kategóriák darabszáma (a választóhoz), utána a kategória-szűrés.
            // A repóban kézzel választott kategória (1.5.6) felülírja az automatikusat.
            var over = cfg.SoftwareCatalog.Where(c => CategoryNames.Contains(c.Category)).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Category, StringComparer.OrdinalIgnoreCase);
            var rows = raw.Select(x => { var (p, c) = Meta(x.Id, x.Name); return (x.Id, x.Name, x.Version, x.Extra, Pub: p, Cat: over.TryGetValue(x.Id, out var oc) ? oc : c); })
                .Where(x => Hit(x.Id, x.Name, x.Pub) && (pub.Length == 0 || x.Pub.Equals(pub, StringComparison.OrdinalIgnoreCase))).ToList();
            var cats = rows.GroupBy(x => x.Cat).ToDictionary(g => g.Key, g => g.Count());
            if (cat.Length > 0) rows = rows.Where(x => x.Cat == cat).ToList();
            var pages = Math.Max(1, (rows.Count + Size - 1) / Size);
            var page = Math.Clamp(r.Page, 0, pages - 1);
            return new
            {
                total = rows.Count, page, pages, size = Size, cat, pub,
                cats = CategoryNames.Where(cats.ContainsKey).Select(c => new { name = c, count = cats[c] }).ToList(),
                items = rows.Skip(page * Size).Take(Size).Select(x => new
                {
                    id = x.Id, name = x.Name, version = x.Version, extra = x.Extra, publisher = x.Pub, category = x.Cat,
                    state = cfg.CatalogState(x.Id), inWinget = Find(x.Id) is not null
                }).ToList()
            };
        }
    }

    /// <summary>Döntés a webről. A teljes döntésre-váró lista egyszerre is elfogadható / elutasítható.</summary>
    public int Decide(ServerConfig cfg, DecideReq r)
    {
        List<string> ids;
        lock (ServerConfig.CatalogLock) ids = r.AllPending ? cfg.CatalogPending.Select(p => p.Id).ToList() : (r.Ids ?? new()).Take(20000).ToList();
        string? Given(string id) => r.Names?.FirstOrDefault(kv => kv.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Value?.Trim() is { Length: > 0 and <= 200 } n
            && !n.Any(char.IsControl) ? n : null;
        return cfg.CatalogDecide(r.Decision, ids, id => Find(id)?.Name ?? Given(id));
    }

    /// <summary>Frissítés + az új programok felvétele a döntésre várók közé (a webes „Frissítés most” is ezt hívja).</summary>
    public async Task RefreshAndRecord(ServerConfig cfg, Action<ServerConfig> save, Action<string> log, CancellationToken token = default)
    {
        var fresh = await RefreshAsync(token);
        if (fresh is null) { if (LastError.Length > 0) log(LastError); return; }
        var added = cfg.AddPending(fresh.Select(p => (p.Id, p.Name, p.Version)));
        if (added > 0) save(cfg);
        if (LastNote.Length > 0) log("Winget-tár: " + LastNote);
        log($"Winget-tár frissítve: {Count} program, {added} új döntésre vár ({Source}).");
        Prune(cfg, save, log);
    }

    /// <summary>A döntésre várók takarítása: a tárból kikerültek, a régiek és a tömegesen (forrásváltásból) felvettek ki.</summary>
    public void Prune(ServerConfig cfg, Action<ServerConfig> save, Action<string> log)
    {
        var total = Count;
        var n = cfg.PrunePending(total > 0 ? id => Find(id) is not null : null, DateTime.UtcNow, FloodLimit(total));
        if (n <= 0) return;
        save(cfg);
        log($"Programtár: {n} program kikerült a döntésre várók közül (nem új a winget-tárban, {ServerConfig.PendingKeepDays} napnál régebbi, vagy már nincs a tárban).");
    }
}
