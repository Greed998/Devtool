using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A Fleet Server tartós konfigurációja.
///
/// Fontos biztonsági döntések, amiket a felépítés kényszerít ki:
///  - A szuperadmin jelszó SOHA nem kerül lemezre visszafejthető formában. PBKDF2-vel
///    (sózott, sok iterációs kulcsleszármaztatás) hasheljük; a konfig kiszivárgása
///    így nem adja ki a jelszót.
///  - Az agent-kommunikáció és a webes admin KÜLÖN porton fut, hogy a webfelület külön
///    tűzfalszabállyal (akár csak az admin-géphez) korlátozható legyen.
///  - A webes munkamenet-sütit egy induláskor generált titkos kulccsal írjuk alá.
/// </summary>
public sealed class ServerConfig
{
    // ---- portok
    public int AgentPort { get; set; } = 8443;   // ide jelentkeznek az agentek (API)
    public int WebPort { get; set; } = 8080;     // ide lép be a szuperadmin (böngésző)

    // ---- szuperadmin (jelszó hash-elve, nem visszafejthető)
    public string AdminUser { get; set; } = "admin";
    public string AdminPasswordHash { get; set; } = "";   // PBKDF2 base64
    public string AdminPasswordSalt { get; set; } = "";   // base64
    public int AdminPasswordIterations { get; set; } = 210_000;

    // ---- webes munkamenet aláíró kulcs (induláskor generált titok)
    public string SessionKey { get; set; } = "";

    // ---- csak a Központ programban: a publikus domain (Caddy + Let's Encrypt)
    public string HubDomain { get; set; } = "";
    /// <summary>A Kozpont admin felulete csak belso halozatrol (a /hub/ mindig nyitva).</summary>
    public bool HubAdminLanOnly { get; set; } = true;

    // ---- Központ (Hub) kapcsolat - opcionális, üres HubUrl = nincs betárcsázás
    public string HubUrl { get; set; } = "";
    public string HubFingerprint { get; set; } = "";
    public string HubToken { get; set; } = "";          // regisztráció után törlődik
    public string HubSiteName { get; set; } = "";
    public string HubSiteId { get; set; } = "";
    /// <summary>A Központba eddig felküldött metrika-sávok vége (Unix-mp) - innen folytatjuk.</summary>
    public long HubMetricsFromTs { get; set; }
    /// <summary>1.5.8: a Központban beállított helyi megőrzés (nap; 0 = a helyi MonKeepDays számít), és hogy a Központ
    /// fogadja-e a lezárt napi fájlokat (ekkor a megőrzés után nem törlünk, hanem feltöltünk, és a nyugta után törlünk).</summary>
    public int HubMetricKeepDays { get; set; }
    public bool HubAcceptsMetricFiles { get; set; }

    /// <summary>A szerveren érvényes megőrzés (a Központé, ha beállította).</summary>
    public int EffectiveKeepDays() => Math.Clamp(HubMetricKeepDays > 0 ? HubMetricKeepDays : MonKeepDays, 2, 3650);
    /// <summary>A Központtól kapott automatikus szabályok (csak olvasható itt; a helyi szabályok mellett futnak).</summary>
    public List<AutoPolicy> HubPolicies { get; set; } = new();
    public string HubPublicKeyPem { get; set; } = "";   // a Központ kulcsa (pinning után)

    // ---- monitorozás (mit gyűjtsön az agent + megőrzés)
    public bool MonCpu { get; set; } = true;
    public bool MonMemory { get; set; } = true;
    public bool MonDisk { get; set; } = true;
    public bool MonDiskSmart { get; set; } = true;
    public bool MonTemperature { get; set; } = true;
    public bool MonEventLog { get; set; } = true;
    public List<string> MonServices { get; set; } = new();
    public List<string> MonProcesses { get; set; } = new();
    public bool MonNetwork { get; set; } = true;
    public bool MonDiskIo { get; set; } = true;
    // 1.5.0: bővített mérés (a felhasználó döntése: a hardver-érzékelők alapból bekapcsolva, a PawnIO-meghajtóval)
    public bool MonCpuDetail { get; set; } = true;
    public bool MonPerCore { get; set; }
    public bool MonHardware { get; set; } = true;
    public bool MonSensorDriver { get; set; } = true;
    public bool MonGpu { get; set; } = true;
    public bool MonSecurity { get; set; } = true;
    public bool MonHyperV { get; set; } = true;
    public bool MonPing { get; set; } = true;
    public List<string> MonPingTargets { get; set; } = new();
    public int MonSampleSeconds { get; set; } = 60; // az agent ilyen sűrűn mér (a jelentkezéstől függetlenül)
    public int LogMaxMb { get; set; } = 2;          // naplófájlok mérete forgatás előtt (MB)
    public int MonDetailDays { get; set; } = 7;     // eddig részletes minta
    public int MonKeepDays { get; set; } = 90;      // eddig órás átlag, utána törlés (a Központban: eddig olvasható, utána archívum)
    /// <summary>1.5.8, csak a Központban (a felhasználó kérése): a telephelyek szerverein ennyi napig (utána a lezárt napi
    /// fájl feltöltődik ide), és itt az archívum (ZIP) a MonKeepDays után összesen eddig.</summary>
    public int SiteMetricKeepDays { get; set; } = 30;
    public int MetricArchiveDays { get; set; } = 180;

    // ---- 1.5.9: WireGuard a telephely és a Központ között (a felhasználó kérése: ezentúl csak azon; szerkeszthető)
    /// <summary>Központ: az alagút bekapcsolva (a telephelyeknek felajánlja). Telephely: a Központ ajánlata szerint él.</summary>
    public bool WgEnabled { get; set; } = true;
    public int WgListenPort { get; set; } = 51820;
    public string WgSubnet { get; set; } = "10.77.0.0/24";
    /// <summary>Központ: amit a telephelyek hívnak (üres = HubDomain:WgListenPort). Telephely: helyi felülírás (üres = a Központé).</summary>
    public string WgEndpoint { get; set; } = "";
    public int WgKeepalive { get; set; } = 25;
    /// <summary>Központ: minden telephely csak alagúton (akkor is, ha még sosem jött rajta).</summary>
    public bool WgStrictAll { get; set; }
    public string WgPrivateKeyProtected { get; set; } = "";
    public string WgPublicKey { get; set; } = "";
    /// <summary>Telephely: a Központtól legutóbb kapott alagút-ajánlat, és hogy ment-e már rajta betárcsázás (onnan csak azon).</summary>
    public HubWgOffer? WgOffer { get; set; }
    public bool WgActive { get; set; }

    /// <summary>Az agentek maguktol frissuljenek a szerver melletti DevToolboxAgent.exe-re.</summary>
    public bool AgentAutoUpdate { get; set; } = true;

    // ---- a Központtól kapott (itt csak olvasható) programtár, küszöb-szabályok és mérés-beállítás (1.5.0)
    public List<CatalogItem> HubCatalogItems { get; set; } = new();
    public List<AppBundle> HubBundles { get; set; } = new();
    public List<CatalogItem> HubRejected { get; set; } = new();
    public string HubCatalogHash { get; set; } = "";
    public List<SiteAlertRule> HubAlertRules { get; set; } = new();
    /// <summary>A Központ mérés-kérése: a helyivel összefésülve megy az agentekhez (null: nincs).</summary>
    public MetricConfig? HubMonitor { get; set; }

    // ---- Csak a Központban: Janus-kapcsolat (1.5.3) - a géplista és a riasztások a Janusba (ott hibajegy lesz belőlük).
    // A kulcs és a titok telephelyenként a HubSite-ban (a Janusban ügyfelenként készül).
    public string JanusUrl { get; set; } = "";
    public bool JanusEnabled { get; set; }

    // ---- Csak a Központban: sérülékenység-keresés (1.5.6) – NVD + CISA KEV + EPSS, naponta egyszer (CveHour órától).
    public bool CveEnabled { get; set; } = true;
    public int CveHour { get; set; } = 3;
    /// <summary>NVD API-kulcs (DPAPI-val védve; üres = kulcs nélkül, lassabban). A felület soha nem kapja vissza.</summary>
    public string NvdKeyProtected { get; set; } = "";
    // ---- Csak a telephelyen: a Központtól legutóbb kapott CVE-találatok lenyomata (ha nem változott, nem jön újra).
    public string HubCveHash { get; set; } = "";

    // ---- Csak a Központban: a mérés-beállítás (Mon*) leküldése a telephelyekre (1.5.0)
    public bool HubMonEnabled { get; set; }
    /// <summary>Mely telephelyekre (üres = mindre).</summary>
    public List<string> HubMonSites { get; set; } = new();

    // ---- gépcsoportok (1.5.0): a létrehozott csoportok - az üresek is (a tagság a gépeknél van)
    public List<string> Groups { get; set; } = new();
    /// <summary>Automatikus csoportok (1.5.4, a felhasználó kérése): Szerverek / Kliensek, Windows-verzió, Virtuális / Fizikai gépek.</summary>
    public bool AutoGroupsOn { get; set; } = true;

    // ---- tárolás (1.5.4, a felhasználó kérése): a naplók, a mentések és a mérési adatbázis helye állítható.
    // Üres = alapértelmezés (az adatmappa logs\, backup\ almappája, ill. maga az adatmappa a metrics.db-nek).
    public string LogDir { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public string MetricsDir { get; set; } = "";

    // ---- 1.5.6 (a felhasználó kérése): frissítés-keresés időköze, gyors szolgáltatás-figyelés, USB-napló
    public int MonAppUpdateMinutes { get; set; } = 30;
    public int MonWuScanHours { get; set; } = 6;
    /// <summary>A figyelt szolgáltatások (MonServices) ellenőrzése ennyi másodpercenként (0 = ki); leállás → azonnali riasztás.</summary>
    public int MonWatchSeconds { get; set; } = 5;
    /// <summary>Másolás külső meghajtóra: a ráírt fájlok naplózása (alapból KI - munkavállalói megfigyelés, GDPR).</summary>
    public bool MonUsbAudit { get; set; }
    /// <summary>1.5.7 (a felhasználó döntése: alapból BE): tálca-ikon a gépeken – repó-programok telepítése, program kérése.</summary>
    public bool TrayEnabled { get; set; } = true;
    /// <summary>1.5.9 (a felhasználó döntése: alapból KI, gépenként): azok az agentek (azonosítók), amelyeken a webes admin
    /// terminál engedélyezett. Csak admin kapcsolhatja; a parancsokat csak admin adhatja ki; minden a műveleti naplóban.</summary>
    public List<string> TerminalAgents { get; set; } = new();

    // ---- internetkapcsolat figyelése (1.5.5, a felhasználó kérése; csak a telephelyi szerver): a megadott címeket
    // sorban pingeli, az első válasz = van internet; ha egyik sem válaszol, kiesés (riasztás, a Központ is megtudja).
    public bool NetWatchEnabled { get; set; }
    public List<string> NetWatchTargets { get; set; } = new();
    /// <summary>Ellenőrzés ennyi percenként (kiesés alatt percenként, hogy a visszatérés pontos legyen).</summary>
    public int NetWatchMinutes { get; set; } = 5;

    // ---- SNMP-trap fogadás (1.5.5, a felhasználó kérése; csak a telephelyi szerver): v1, v2c (trap + inform), v3 (USM).
    // Alapból KI: bekapcsoláskor nyílik a port (tűzfal: csak a megengedett, alapból belső címekről).
    public bool SnmpEnabled { get; set; }
    public int SnmpPort { get; set; } = 162;
    /// <summary>Csak ezekről a címekről / tartományokból (CIDR); üres = a belső (magán) címtartományok.</summary>
    public List<string> SnmpAllowFrom { get; set; } = new();
    /// <summary>v1 / v2c közösségnevek (community) - csak ezekkel fogad; üres = v1 / v2c tiltva.</summary>
    public List<string> SnmpCommunities { get; set; } = new() { "public" };
    public List<SnmpUserConfig> SnmpUsers { get; set; } = new();
    /// <summary>Riasztást adó trapek: trap-OID előtagok („*” = minden trap). Alapból: coldStart, warmStart, linkDown.</summary>
    public List<string> SnmpAlertOids { get; set; } = new() { "1.3.6.1.6.3.1.1.5.1", "1.3.6.1.6.3.1.1.5.2", "1.3.6.1.6.3.1.1.5.3" };
    /// <summary>A saját SNMP-motor azonosítója (v3 inform: a fogadó a hiteles fél) és indulásszámlálója.</summary>
    public string SnmpEngineId { get; set; } = "";
    public int SnmpEngineBoots { get; set; }

    // ---- portváltás (1.5.0): a régi agent-port egy ideig még figyel, amíg az agentek át nem állnak
    public int OldAgentPort { get; set; }
    public DateTime? OldAgentPortSinceUtc { get; set; }
    /// <summary>Legfeljebb ennyi napig figyel a régi port (utána a még át nem állt gépeket újra kell telepíteni).</summary>
    public int OldAgentPortDays { get; set; } = 30;

    /// <summary>
    /// Az agentekhez menő mérés-beállítás: a helyi és a Központtól kapott összefésülve - amit bármelyik kér, azt méri
    /// (a Központ a helyi mérést nem kapcsolhatja ki, csak bővítheti). A mintavétel a sűrűbbik.
    /// </summary>
    public MetricConfig EffectiveMetricConfig()
    {
        var h = HubMonitor;
        var mc = new MetricConfig
        {
            Cpu = MonCpu || h?.Cpu == true, Memory = MonMemory || h?.Memory == true, Disk = MonDisk || h?.Disk == true,
            DiskSmart = MonDiskSmart || h?.DiskSmart == true, Temperature = MonTemperature || h?.Temperature == true,
            EventLog = MonEventLog || h?.EventLog == true, Network = MonNetwork || h?.Network == true, DiskIo = MonDiskIo || h?.DiskIo == true,
            CpuDetail = MonCpuDetail || h?.CpuDetail == true, PerCore = MonPerCore || h?.PerCore == true,
            Hardware = MonHardware || h?.Hardware == true, SensorDriver = MonSensorDriver || h?.SensorDriver == true,
            Gpu = MonGpu || h?.Gpu == true, Security = MonSecurity || h?.Security == true, HyperV = MonHyperV || h?.HyperV == true,
            Ping = MonPing || h?.Ping == true,
            Services = MonServices.Concat(h?.Services ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Processes = MonProcesses.Concat(h?.Processes ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PingTargets = MonPingTargets.Concat(h?.PingTargets ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            SampleSeconds = Math.Clamp(h is { SampleSeconds: > 0 } && h.SampleSeconds < MonSampleSeconds ? h.SampleSeconds : MonSampleSeconds, 15, 3600),
            AcceptsSamples = true, StatusVersion = 4,
            // 1.5.6: időközök - a kisebbik (nem nulla) nyer; az USB-napló, ha bármelyik kéri.
            AppUpdateMinutes = MinSet(MonAppUpdateMinutes, h?.AppUpdateMinutes ?? 0, 15, 1440),
            WuScanHours = MinSet(MonWuScanHours, h?.WuScanHours ?? 0, 1, 48),
            WatchSeconds = MinSet(MonWatchSeconds, h?.WatchSeconds ?? 0, 2, 300),
            UsbAudit = MonUsbAudit || h?.UsbAudit == true,
            // 1.5.7: a tálca-ikon a telephely döntése (a Központ nem kapcsolja be / ki)
            Tray = TrayEnabled
        };
        return mc;
    }

    /// <summary>A mérés-beállítás (Mon*) átvétele a webről érkező kérésből (a szerveren és a Központban is).</summary>
    public void ApplyMonitor(MetricConfig m)
    {
        static List<string> L(List<string>? l, int max) => (l ?? new()).Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        MonCpu = m.Cpu; MonMemory = m.Memory; MonDisk = m.Disk; MonDiskSmart = m.DiskSmart; MonTemperature = m.Temperature;
        MonEventLog = m.EventLog; MonNetwork = m.Network; MonDiskIo = m.DiskIo;
        MonCpuDetail = m.CpuDetail; MonPerCore = m.PerCore; MonHardware = m.Hardware; MonSensorDriver = m.SensorDriver;
        MonGpu = m.Gpu; MonSecurity = m.Security; MonHyperV = m.HyperV; MonPing = m.Ping;
        MonServices = L(m.Services, 100); MonProcesses = L(m.Processes, 100);
        // Ping-cél: csak gépnév vagy IP-cím (az agent is ellenőrzi).
        MonPingTargets = L(m.PingTargets, 8).Where(t => Uri.CheckHostName(t) != UriHostNameType.Unknown).ToList();
        if (m.SampleSeconds > 0) MonSampleSeconds = Math.Clamp(m.SampleSeconds, 15, 3600);
        if (m.AppUpdateMinutes > 0) MonAppUpdateMinutes = Math.Clamp(m.AppUpdateMinutes, 15, 1440);
        if (m.WuScanHours > 0) MonWuScanHours = Math.Clamp(m.WuScanHours, 1, 48);
        MonWatchSeconds = m.WatchSeconds <= 0 ? 0 : Math.Clamp(m.WatchSeconds, 2, 300);
        MonUsbAudit = m.UsbAudit;
    }

    /// <summary>Két időköz közül a kisebbik, ami be van állítva (0 = nincs); mindkettő 0 → 0.</summary>
    private static int MinSet(int a, int b, int lo, int hi)
    {
        var v = a > 0 && b > 0 ? Math.Min(a, b) : Math.Max(a, b);
        return v <= 0 ? 0 : Math.Clamp(v, lo, hi);
    }

    /// <summary>A Mon* beállítás MetricConfig-ként (a Központ ezt küldi le a telephelyeknek).</summary>
    public MetricConfig MonitorAsConfig() => new()
    {
        Cpu = MonCpu, Memory = MonMemory, Disk = MonDisk, DiskSmart = MonDiskSmart, Temperature = MonTemperature, EventLog = MonEventLog,
        Network = MonNetwork, DiskIo = MonDiskIo, CpuDetail = MonCpuDetail, PerCore = MonPerCore, Hardware = MonHardware,
        SensorDriver = MonSensorDriver, Gpu = MonGpu, Security = MonSecurity, HyperV = MonHyperV, Ping = MonPing,
        Services = MonServices.ToList(), Processes = MonProcesses.ToList(), PingTargets = MonPingTargets.ToList(), SampleSeconds = MonSampleSeconds,
        AppUpdateMinutes = MonAppUpdateMinutes, WuScanHours = MonWuScanHours, WatchSeconds = MonWatchSeconds, UsbAudit = MonUsbAudit
    };

    /// <summary>„Semmit nem kér”: a Központ ezt küldi, ha nem állít mérést az adott telephelyre (a korábbi kérése így megszűnik).</summary>
    public static MetricConfig NoMonitor() => new()
    {
        Cpu = false, Memory = false, Disk = false, DiskSmart = false, Temperature = false, EventLog = false, Network = false, DiskIo = false,
        CpuDetail = false, PerCore = false, Hardware = false, SensorDriver = false, Gpu = false, Security = false, HyperV = false, Ping = false, SampleSeconds = 0
    };

    /// <summary>
    /// A Központ programtárának átvétele. A Központ döntései itt csak olvashatók; ami a Központ szerint eldőlt,
    /// az kikerül a helyi „döntésre vár” listából. Igaz, ha változott.
    /// </summary>
    public bool ApplyHubCatalog(HubCatalog c)
    {
        lock (CatalogLock)
        {
            if (c.Hash == HubCatalogHash) return false;
            HubCatalogItems = c.Items.Where(i => WingetId.IsValid(i.Id)).GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Select(i => new CatalogItem { Id = i.Id, Name = i.Name.Trim().Length is > 0 and <= 200 ? i.Name.Trim() : i.Id }).ToList();
            var ids = HubCatalogItems.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            HubBundles = c.Bundles.Where(b => b.Id.Length > 0 && b.Name.Trim().Length > 0)
                .Select(b => new AppBundle { Id = b.Id, Name = b.Name.Trim(), Apps = b.Apps.Where(ids.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }).ToList();
            HubRejected = c.Rejected.Where(i => WingetId.IsValid(i.Id) && !ids.Contains(i.Id)).GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CatalogItem { Id = g.Key, Name = g.First().Name }).ToList();
            HubCatalogHash = c.Hash;
            var decided = ids.Concat(HubRejected.Select(r => r.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            CatalogPending.RemoveAll(p => decided.Contains(p.Id));
            return true;
        }
    }

    /// <summary>A Központ programtárának lenyomata (a Központ ezzel dönti el, kell-e leküldeni).</summary>
    public static string CatalogHash(List<CatalogItem> items, List<AppBundle> bundles, List<CatalogItem> rejected) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProtocolJson.Serialize(new { items, bundles, rejected }))))[..32];

    // ---- kiesés-riasztás: a gép (szerveren) / a telephely és gépei (Központban) N perce nem jelentkezett.
    // A felhasználó döntése: alapból BE, 15 perc (a többi riasztással ellentétben).
    /// <summary>0 = kikapcsolva.</summary>
    public int OfflineAlertMinutes { get; set; } = 15;
    /// <summary>Csak ezeknek a gépcsoportoknak a gépei (üres = minden gép) - pl. az esténként kikapcsolt asztali gépek kihagyhatók.</summary>
    public List<string> OfflineAlertGroups { get; set; } = new();
    /// <summary>Csak a Központban: a telephelyek gépeinek kiesése is (a telephely kiesése mindig riaszt, ha a figyelés be van kapcsolva).</summary>
    public bool OfflineAlertMachines { get; set; } = true;

    /// <summary>A beállítható legkisebb küszöb: az agent 5 percenként jelentkezik, ennél rövidebb küszöb villogna.</summary>
    public static int ClampOfflineMinutes(int m) => m <= 0 ? 0 : Math.Clamp(m, 10, 7 * 24 * 60);

    // ---- automatikus kulcscsere (a felhasználó kérése: hetente, az agenteké és a szerveré/Központé is). 0 = kikapcsolva.
    /// <summary>Az agentek ennyi naponként új kulcspárt készítenek (a szerver aláírva visszaigazolja).</summary>
    public int AgentKeyRotationDays { get; set; } = 7;
    /// <summary>A szerver (a Központban: a Központ) saját kulcsa ennyi naponként cserélődik, láncolt átadással.</summary>
    public int ServerKeyRotationDays { get; set; } = 7;
    public static int ClampRotationDays(int d) => d <= 0 ? 0 : Math.Clamp(d, 1, 365);

    // ---- e-mail értesítés (CSAK a Központ küld). A jelszó DPAPI-val titkosítva, a felületre soha nem megy vissza.
    public bool MailEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    /// <summary>starttls (587) | ssl (465, azonnali TLS) | none (25, titkosítás nélkül - csak belső relay-hez)</summary>
    public string SmtpSecurity { get; set; } = "starttls";
    public string SmtpUser { get; set; } = "";
    public string SmtpPasswordProtected { get; set; } = "";
    public string MailFrom { get; set; } = "";
    public List<string> MailTo { get; set; } = new();

    // ---- programkatalógus és automatikus szabályok
    public List<CatalogItem> SoftwareCatalog { get; set; } = new();
    public List<AppBundle> AppBundles { get; set; } = new();

    /// <summary>
    /// A regi "alapcsomag" jelolokbol egy "Alapcsomag" nevu alkalmazascsomag lesz (egyszer, ha meg nincs csomag).
    /// Igaz, ha valtozott (a hivo mentse).
    /// </summary>
    public bool MigrateBaseToBundle()
    {
        if (AppBundles.Count > 0 || !SoftwareCatalog.Any(c => c.Base)) return false;
        AppBundles.Add(new AppBundle { Id = Guid.NewGuid().ToString("N"), Name = "Alapcsomag", Apps = SoftwareCatalog.Where(c => c.Base).Select(c => c.Id).ToList() });
        return true;
    }

    /// <summary>A webrol kapott programtar es csomagok ellenorzese (ervenyes azonosito, csak a tarban levo program).</summary>
    public void SetCatalog(List<CatalogItem>? items, List<AppBundle>? bundles)
    {
        SoftwareCatalog = (items ?? new()).Where(i => WingetId.IsValid(i.Id))
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(i => new CatalogItem { Id = i.Id, Name = i.Name.Trim().Length > 0 ? i.Name.Trim() : i.Id,
                // A kézi kategória megmarad (a felület a mentéskor nem küldi vissza).
                Category = SoftwareCatalog.FirstOrDefault(o => o.Id.Equals(i.Id, StringComparison.OrdinalIgnoreCase))?.Category ?? "" }).ToList();
        // Egy helyi csomagba a Központ repójának programjai is bekerülhetnek (azok is telepíthetők itt).
        var ids = SoftwareCatalog.Select(c => c.Id).Concat(HubCatalogItems.Select(c => c.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AppBundles = (bundles ?? new()).Where(b => b.Name.Trim().Length > 0).Select(b => new AppBundle
        {
            Id = b.Id.Length > 0 ? b.Id : Guid.NewGuid().ToString("N"),
            Name = b.Name.Trim(),
            Apps = b.Apps.Where(ids.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        }).ToList();
    }

    /// <summary>Egy csomag telepitendo programjai (ures, ha nincs ilyen csomag) - a helyi és a Központtól kapott csomagok közül.</summary>
    public List<string> BundleApps(string bundleId) =>
        (AppBundles.FirstOrDefault(b => b.Id == bundleId) ?? HubBundles.FirstOrDefault(b => b.Id == bundleId))?.Apps.Where(WingetId.IsValid).ToList() ?? new();

    // ---- programtár-döntések a teljes winget-tárhoz (a SoftwareCatalog a „saját repó”)
    /// <summary>A winget-tárba az előző letöltés óta újonnan került programok - döntésre várnak (a felhasználó döntése).</summary>
    public List<PendingApp> CatalogPending { get; set; } = new();

    /// <summary>Ennyi napig vár egy új program döntésre; utána döntés nélkül a teljes tárban marad (már nem „új”).</summary>
    public const int PendingKeepDays = 30;

    /// <summary>
    /// A döntésre várók takarítása (a felhasználó kérése: ott csak a winget-tárban valóban új programok legyenek).
    /// Kikerül: ami PendingKeepDays napnál régebben került be; ami már nincs a tárban (inCatalog, null = nem vizsgáljuk);
    /// és az egy frissítésben (egy percen belül) floodLimit-nél többesével felvett programok - ez a korábbi verziók
    /// forrásváltásának nyoma (a helyi winget-index és a CDN eltérése), nem újdonság. Visszaadja, hány került ki.
    /// </summary>
    public int PrunePending(Func<string, bool>? inCatalog, DateTime nowUtc, int floodLimit)
    {
        lock (CatalogLock)
        {
            var before = CatalogPending.Count;
            CatalogPending.RemoveAll(p => nowUtc - p.SeenUtc > TimeSpan.FromDays(PendingKeepDays) || (inCatalog is not null && !inCatalog(p.Id)));
            // Egy frissítés tételei ezredmásodpercekre egymás után kerülnek be: a percnél kisebb hézaggal egymást követők egy köteg.
            var sorted = CatalogPending.OrderBy(p => p.SeenUtc).ToList();
            var drop = new HashSet<PendingApp>();
            for (int i = 0; i < sorted.Count;)
            {
                var j = i + 1;
                while (j < sorted.Count && sorted[j].SeenUtc - sorted[j - 1].SeenUtc < TimeSpan.FromMinutes(1)) j++;
                if (j - i > floodLimit) for (int k = i; k < j; k++) drop.Add(sorted[k]);
                i = j;
            }
            if (drop.Count > 0) CatalogPending.RemoveAll(drop.Contains);
            return before - CatalogPending.Count;
        }
    }
    /// <summary>Elutasított programok: nem kellenek a repóba, és újra sem jelennek meg döntésre.</summary>
    public List<CatalogItem> CatalogRejected { get; set; } = new();

    /// <summary>A webkérések és a háttérfrissítés ugyanazt a példányt módosítja - a programtár-műveletek sorban mennek.</summary>
    public static readonly object CatalogLock = new();

    /// <summary>
    /// Egy program állapota a programtárban: repo / hubrepo (a Központ repójában) / rejected / hubrejected (a Központ
    /// elutasította) / pending / "" (nincs döntés). A Központ döntései a telephelyen csak olvashatók.
    /// </summary>
    public string CatalogState(string id)
    {
        bool In(List<CatalogItem> l) => l.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return In(SoftwareCatalog) ? "repo" : In(HubCatalogItems) ? "hubrepo" : In(CatalogRejected) ? "rejected" : In(HubRejected) ? "hubrejected"
            : CatalogPending.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ? "pending" : "";
    }

    /// <summary>Új winget-programok a döntésre várók közé (ami már a repóban van vagy elutasított, kimarad). Visszaadja, hányat vett fel.</summary>
    public int AddPending(IEnumerable<(string Id, string Name, string Version)> apps)
    {
        lock (CatalogLock)
        {
            var n = 0;
            foreach (var a in apps)
            {
                if (!WingetId.IsValid(a.Id) || CatalogState(a.Id).Length > 0) continue;
                CatalogPending.Add(new PendingApp { Id = a.Id, Name = a.Name, Version = a.Version, SeenUtc = DateTime.UtcNow });
                n++;
            }
            // Felső korlát: hosszú kiesés után se nőjön a végtelenségig (a legrégebbiek esnek ki).
            if (CatalogPending.Count > 5000) CatalogPending.RemoveRange(0, CatalogPending.Count - 5000);
            return n;
        }
    }

    /// <summary>
    /// Döntés programokról: accept (a repóba), reject (elutasított – a repóból és a csomagokból is kikerül),
    /// remove (ki a repóból, döntés nélküli lesz), unreject (az elutasítás visszavonása). nameOf: a winget-tár neve.
    /// Visszaadja, hány program állapota változott.
    /// </summary>
    public int CatalogDecide(string decision, IEnumerable<string> ids, Func<string, string?> nameOf)
    {
        lock (CatalogLock)
        {
            var n = 0;
            foreach (var raw in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var id = raw.Trim();
                if (!WingetId.IsValid(id)) continue;
                bool Is(string s) => s.Equals(id, StringComparison.OrdinalIgnoreCase);
                var name = nameOf(id) ?? SoftwareCatalog.FirstOrDefault(c => Is(c.Id))?.Name
                           ?? CatalogPending.FirstOrDefault(c => Is(c.Id))?.Name ?? CatalogRejected.FirstOrDefault(c => Is(c.Id))?.Name ?? id;
                var before = CatalogState(id);
                if (before is "hubrepo" or "hubrejected") continue;   // a Központ döntése itt nem módosítható
                switch (decision)
                {
                    case "accept":
                        CatalogPending.RemoveAll(c => Is(c.Id)); CatalogRejected.RemoveAll(c => Is(c.Id));
                        if (!SoftwareCatalog.Any(c => Is(c.Id))) SoftwareCatalog.Add(new CatalogItem { Id = id, Name = name });
                        break;
                    case "reject":
                        CatalogPending.RemoveAll(c => Is(c.Id)); SoftwareCatalog.RemoveAll(c => Is(c.Id));
                        foreach (var b in AppBundles) b.Apps.RemoveAll(Is);
                        if (!CatalogRejected.Any(c => Is(c.Id))) CatalogRejected.Add(new CatalogItem { Id = id, Name = name });
                        break;
                    case "remove":
                        SoftwareCatalog.RemoveAll(c => Is(c.Id));
                        foreach (var b in AppBundles) b.Apps.RemoveAll(Is);
                        break;
                    case "unreject":
                        CatalogRejected.RemoveAll(c => Is(c.Id));
                        break;
                    default: return 0;
                }
                if (CatalogState(id) != before) n++;
            }
            return n;
        }
    }
    public List<AutoPolicy> AutoPolicies { get; set; } = new();

    // ---- beléptető token alapértékei (a felhasználó a weben szabja meg)
    public int TokenLifetimeMinutes { get; set; } = 60;   // 0 = nincs lejárat
    public int TokenMaxUses { get; set; } = 1;            // 0 = korlátlan

    /// <summary>Igaz, ha a kezdeti beállítás megtörtént.</summary>
    public bool SetupDone => AdminPasswordHash.Length > 0;

    // ================================================================ jelszó

    /// <summary>
    /// Jelszó beállítása. PBKDF2-SHA256, egyedi sóval. Csak a leszármaztatott kulcsot
    /// és a sót tároljuk – az eredeti jelszó sehol nem marad meg.
    /// </summary>
    public void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            AdminPasswordIterations, HashAlgorithmName.SHA256, 32);

        AdminPasswordSalt = Convert.ToBase64String(salt);
        AdminPasswordHash = Convert.ToBase64String(hash);
    }

    /// <summary>Jelszó ellenőrzése időállandó összehasonlítással (időzítés-támadás ellen).</summary>
    public bool VerifyPassword(string password)
    {
        if (AdminPasswordHash.Length == 0) return false;
        try
        {
            var salt = Convert.FromBase64String(AdminPasswordSalt);
            var expected = Convert.FromBase64String(AdminPasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt,
                AdminPasswordIterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    /// <summary>
    /// Ujratelepiteshez: a szuperadmin fiok es a munkamenet-kulcs torlodik, igy a
    /// webfelulet ujra a setupot mutatja. A portok megmaradnak (elore kitoltve),
    /// hogy a mar telepitett agentek tovabbra is elerjek a szervert.
    /// </summary>
    public void ResetSetup()
    {
        AdminUser = "admin";
        AdminPasswordHash = "";
        AdminPasswordSalt = "";
        SessionKey = "";   // a regi munkamenetek ervenytelenek lesznek
    }

    public void EnsureSessionKey()
    {
        if (SessionKey.Length == 0)
            SessionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    // ================================================================ perzisztencia

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string PathFor(string dir) => Path.Combine(dir, "server-config.json");

    public static ServerConfig Load(string dir)
    {
        try
        {
            var path = PathFor(dir);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Opts) ?? new();
        }
        catch { }
        return new ServerConfig();
    }

    private static readonly object CacheLock = new();
    private static (string Path, DateTime Write, long Len, ServerConfig Cfg)? _cache;

    /// <summary>
    /// CSAK OLVASASRA: ugyanazt a peldanyt adja vissza, amig a fajl nem valtozik. A gyakori hivoknak
    /// (minden agent-jelentkezes) igy nem kell minden alkalommal lemezrol olvasni es JSON-t bontani.
    /// </summary>
    public static ServerConfig LoadCached(string dir)
    {
        var path = PathFor(dir);
        try
        {
            var fi = new FileInfo(path);
            var w = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            var len = fi.Exists ? fi.Length : -1;
            lock (CacheLock)
            {
                if (_cache is { } c && c.Path == path && c.Write == w && c.Len == len) return c.Cfg;
                var cfg = Load(dir);
                _cache = (path, w, len, cfg);
                return cfg;
            }
        }
        catch { return Load(dir); }
    }

    /// <summary>Visszaadja, sikerult-e - az ujratelepites ezt ellenorzi.</summary>
    public bool Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var tmp = PathFor(dir) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, PathFor(dir), overwrite: true);
            return true;
        }
        catch { return false; }
    }

    // ================================================================ jelszó-erősség

    public sealed record Strength(bool Ok, string Message);

    /// <summary>A setup ezt használja: értelmes minimum, hogy ne lehessen gyenge admin-jelszó.</summary>
    public static Strength CheckPasswordStrength(string pw)
    {
        if (pw.Length < 12)
            return new Strength(false, "Legalább 12 karakter szükséges.");

        int classes = 0;
        if (pw.Any(char.IsUpper)) classes++;
        if (pw.Any(char.IsLower)) classes++;
        if (pw.Any(char.IsDigit)) classes++;
        if (pw.Any(c => !char.IsLetterOrDigit(c))) classes++;

        if (classes < 3)
            return new Strength(false,
                "Legalább háromféle karaktertípus kell (kis- és nagybetű, szám, jel).");

        return new Strength(true, "Megfelelő.");
    }
}

/// <summary>Döntésre váró program: a winget-tárba újonnan került (az első letöltés az alap, az nem kerül ide).</summary>
/// <summary>
/// SNMPv3 felhasználó (USM). A jelszavak DPAPI-val titkosítva (a SYSTEM kulcsával), a felületre nem mennek vissza.
/// Auth: none | md5 | sha1 | sha224 | sha256 | sha384 | sha512; Priv: none | des | 3des | aes128 | aes192 | aes256 |
/// aes192c | aes256c (a „c” változat a Cisco-féle kulcsbővítés).
/// </summary>
public sealed class SnmpUserConfig
{
    public string Name { get; set; } = "";
    public string Auth { get; set; } = "sha256";
    public string AuthKeyProtected { get; set; } = "";
    public string Priv { get; set; } = "aes128";
    public string PrivKeyProtected { get; set; } = "";
}

public sealed class PendingApp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime SeenUtc { get; set; }
}

// =====================================================================================

/// <summary>
/// Webes munkamenetek. Aláírt, lejáró sütiket ad. A sütiben nincs érzékeny adat, csak
/// egy felhasználónév + lejárat + aláírás – a szerver a session-kulccsal ellenőrzi.
/// </summary>
public sealed class SessionManager
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime = TimeSpan.FromHours(8);

    public SessionManager(string base64Key) => _key = Convert.FromBase64String(base64Key);

    public string Issue(string user) => Issue(user, "");

    /// <summary>Suti a felhasznalonevvel es (opcionalisan) a fiok azonositojaval.</summary>
    public string Issue(string user, string userId)
    {
        var expires = DateTimeOffset.UtcNow.Add(_lifetime).ToUnixTimeSeconds();
        // A userId hex (nincs benne '|'), ezert biztonsagos elvalaszto.
        var payload = $"{user}|{expires}|{userId}";
        var sig = Sign(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{sig}"));
    }

    public string? Validate(string? cookie) => ValidateFull(cookie)?.User;

    public sealed record Info(string User, string UserId);

    /// <summary>A suti teljes tartalma: felhasznalonev + fiok-azonosito (ha van).</summary>
    public Info? ValidateFull(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cookie));
            var parts = decoded.Split('|');
            // regi (3 reszes: user|exp|sig) es uj (4 reszes: user|exp|userId|sig) formatum
            if (parts.Length is not (3 or 4)) return null;

            var sigIdx = parts.Length - 1;
            var payload = string.Join('|', parts[..sigIdx]);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Sign(payload)),
                    Encoding.UTF8.GetBytes(parts[sigIdx])))
                return null;

            if (!long.TryParse(parts[1], out var exp)) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;

            var userId = parts.Length == 4 ? parts[2] : "";
            return new Info(parts[0], userId);
        }
        catch { return null; }
    }

    private string Sign(string data)
    {
        using var hmac = new HMACSHA256(_key);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }
}
