using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToolbox.Fleet;

/// <summary>
/// Az agent és a szerver közötti protokoll üzenetei.
///
/// Alapelv: az agent MINDIG kimenő kapcsolatot nyit a szerver felé (HTTPS), soha nem vár
/// befelé jövő kapcsolatot. Így a felügyelt gépeken nincs nyitott port, nincs támadási
/// felület, és a megoldás tűzfal/NAT/VPN mögül is működik – csak kifelé kell HTTPS.
///
/// Minden agent→szerver kérés alá van írva az agent privát kulcsával (a regisztráció után),
/// időbélyeggel és nonce-szal az ismétlés ellen.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

// ================================================================ regisztráció

/// <summary>Agent → szerver: első jelentkezés a beléptető-tokennel.</summary>
public sealed class EnrollRequest
{
    public string Token { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

public sealed class EnrollResponse
{
    public bool Success { get; set; }
    public string AgentId { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>A szerver publikus kulcsa – az agent ezután ezzel ellenőrzi a szervert.</summary>
    public string ServerPublicKeyPem { get; set; } = "";
    /// <summary>A szerver kulcslanca (kulcscsere utan): egy regi ujjlenyomat is ellenorizhetoen eljut a jelenlegig.</summary>
    public List<KeyLink>? KeyChain { get; set; }
}

// ================================================================ jelentkezés (heartbeat)

/// <summary>
/// Agent → szerver: rendszeres jelentkezés. Ekkor küldi az állapotát, és megkérdezi,
/// van-e elvégzendő feladat. Ez a „kimenő pull” modell szíve.
/// </summary>
public sealed class CheckinRequest
{
    public string AgentId { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Nonce { get; set; } = "";
    public AgentStatus Status { get; set; } = new();
    /// <summary>Az agent privát kulcsával aláírt kanonikus tartalom.</summary>
    public string Signature { get; set; } = "";

    /// <summary>Az aláírandó/aláírt kanonikus szöveg (a Signature nélkül).</summary>
    public string CanonicalPayload() =>
        $"{AgentId}\n{Timestamp}\n{Nonce}\n{ProtocolJson.Serialize(Status)}";
}

public sealed class CheckinResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    /// <summary>A végrehajtandó feladatok (pl. frissítés). Üres, ha nincs teendő.</summary>
    public List<AgentTask> Tasks { get; set; } = new();
    /// <summary>Következő jelentkezésig javasolt idő másodpercben.</summary>
    public int NextCheckinSeconds { get; set; } = 300;

    /// <summary>
    /// Mit monitorozzon az agent. A FleetServer allitja be gepenkent/profilonkent,
    /// es a valaszban kuldi le. Null = a korabbi beallitas marad ervenyben.
    /// </summary>
    public MetricConfig? Metrics { get; set; }

    /// <summary>Elerheto agent-frissites (az alairt reszbe masolva kerul at).</summary>
    public AgentUpdateOffer? AgentUpdate { get; set; }

    /// <summary>Kulcscsere-szabaly (az alairt reszbe masolva kerul at).</summary>
    public KeyPolicy? KeyPolicy { get; set; }

    /// <summary>Az agent-port portváltáskor (az alairt reszbe masolva kerul at).</summary>
    public int? AgentPort { get; set; }

    /// <summary>1.5.9: a webes admin terminál ENGEDÉLYEZVE van erre a gépre (az aláírt válaszban – a régi agent nem kapja meg).</summary>
    public bool Terminal { get; set; }

    // ---- alairas (uj agentek ezt ellenorzik; a regiek figyelmen kivul hagyjak)
    /// <summary>A valasz tenyleges tartalma JSON-kent (CheckinReplyBody). Ezen van az alairas.</summary>
    public string? SignedPayload { get; set; }
    public string? Signature { get; set; }
    /// <summary>A szerver jelenlegi publikus kulcsa + a lanc a korabbi kulcsoktol (kulcscsere).</summary>
    public string? SignerPublicKeyPem { get; set; }
    public List<KeyLink>? KeyChain { get; set; }
}

/// <summary>
/// A szerver -> agent valasz alairt magja. A keres nonce-a es az agent azonositoja benne van,
/// igy egy elkapott valasz nem jatszhato vissza, es mas agentnek sem adhato at.
/// </summary>
public sealed class CheckinReplyBody
{
    public string AgentId { get; set; } = "";
    public string RequestNonce { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<AgentTask> Tasks { get; set; } = new();
    public int NextCheckinSeconds { get; set; } = 300;
    public MetricConfig? Metrics { get; set; }
    public AgentUpdateOffer? AgentUpdate { get; set; }
    /// <summary>Az agent kulcsának cseréje (1.4.5-től; a régi agent figyelmen kívül hagyja, a régi szerver nem küldi).</summary>
    public KeyPolicy? KeyPolicy { get; set; }
    /// <summary>
    /// Portváltás (1.5.0-tól): a szerver agent-portja. Ha eltér attól, amin az agent most jelentkezett, az agent
    /// ellenőrzi az új porton a szerver kulcsát, és átáll. Az aláírt részben van: hamis szerver nem irányíthatja el.
    /// </summary>
    public int? AgentPort { get; set; }
    /// <summary>1.5.9: a webes admin terminál engedélyezve erre a gépre (csak admin indíthat parancsot, a szerver aláírt válaszában).</summary>
    public bool Terminal { get; set; }
}

/// <summary>
/// Kulcscsere-szabály a szervertől: RotateDays naponként (0 = kikapcsolva) új kulcspár; Strong: a szerver
/// fogad P-384-es kulcsot - ilyenkor az agent a régebbi P-256-osát azonnal erősebbre cseréli.
/// </summary>
public sealed class KeyPolicy
{
    public int RotateDays { get; set; }
    public bool Strong { get; set; }
}

/// <summary>
/// Agent → szerver: kulcscsere. A régi (jelenlegi) kulccsal aláírva (ki kéri), ÉS az újjal is (birtoklás-
/// bizonyítás: az új kulcs privát része tényleg nála van). A „rotate” előtag miatt más üzenet aláírása nem
/// használható fel ehhez.
/// </summary>
public sealed class KeyRotateRequest
{
    public string AgentId { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string NewPublicKeyPem { get; set; } = "";
    public string Signature { get; set; } = "";
    public string NewKeySignature { get; set; } = "";

    public string CanonicalPayload() => $"rotate\n{AgentId}\n{Timestamp}\n{Nonce}\n{NewPublicKeyPem}";
}

/// <summary>A szerver aláírt válasza a kulcscserére (a kérés nonce-ához kötve) - az agent csak erre vált kulcsot.</summary>
public sealed class KeyRotateReplyBody
{
    public string AgentId { get; set; } = "";
    public string RequestNonce { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Az elfogadott új kulcs ujjlenyomata.</summary>
    public string Fingerprint { get; set; } = "";
}

public sealed class KeyRotateResponse
{
    public string SignedPayload { get; set; } = "";
    public string Signature { get; set; } = "";
    public string? SignerPublicKeyPem { get; set; }
    public List<KeyLink>? KeyChain { get; set; }
}

/// <summary>
/// Agent-frissites ajanlat. Az ALAIRT valaszban erkezik, benne a fajl SHA-256 lenyomata:
/// az agent csak akkor telepiti a letoltott exe-t, ha a lenyomat pontosan egyezik -
/// igy a letoltes HTTP-n at sem hamisithato.
/// </summary>
public sealed class AgentUpdateOffer
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>Metrikagyujtes beallitasai - a protokoll resze, hogy az agent es a szerver ossze illjon.</summary>
public sealed class MetricConfig
{
    public bool Cpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Disk { get; set; } = true;
    public bool DiskSmart { get; set; } = true;
    public bool Temperature { get; set; } = true;
    public bool EventLog { get; set; } = true;
    public List<string> Services { get; set; } = new();
    public List<string> Processes { get; set; } = new();
    public int SampleSeconds { get; set; } = 60;   // milyen surun mintavetelezzen (az agenten belul)
    public bool Network { get; set; } = true;      // halozati forgalom adapterenkent
    public bool DiskIo { get; set; } = true;       // lemez-aktivitas (foglaltsag, sor, olvasas/iras)
    /// <summary>
    /// A szerver fogadja a jelentkezesek KOZOTT vett, idobelyeges mintakat (AgentStatus.MetricSamples).
    /// Regi szerver nem kuldi - akkor az agent sem kuldi oket (kulonben az alairas nem egyezne).
    /// </summary>
    public bool AcceptsSamples { get; set; }

    // ---- 1.5.0: bővített mérés. Az új kategóriák alapértéke olyan, hogy régi szerver mellett se történjen
    // semmi, amit a szerver nem kért (meghajtót csak kifejezett kérésre telepít az agent).
    /// <summary>CPU részletek: órajel, teljesítmény %, korlátozás (throttling), turbó, C-állapotok, megszakítások, környezetváltás.</summary>
    public bool CpuDetail { get; set; } = true;
    /// <summary>Magonkénti terhelés (és hőmérséklet): sok sor a metrikatárban, ezért alapból ki.</summary>
    public bool PerCore { get; set; }
    /// <summary>Hardver-érzékelők (LibreHardwareMonitor): hőmérsékletek, ventilátorok, fogyasztás, GPU.</summary>
    public bool Hardware { get; set; } = true;
    /// <summary>Ha a hardver-érzékelőkhöz kell, a PawnIO meghajtó telepítése wingettel (csak ha a szerver kéri).</summary>
    public bool SensorDriver { get; set; }
    /// <summary>GPU-terhelés és videómemória a Windows saját számlálóiból (meghajtó nélkül is).</summary>
    public bool Gpu { get; set; } = true;
    /// <summary>Biztonsági napló: sikeres / sikertelen belépés, zárolás, próbálkozás egy forrásból.</summary>
    public bool Security { get; set; } = true;
    /// <summary>Hyper-V: virtuális gépek állapota, ellenőrzőpontok, terhelés (ha van Hyper-V).</summary>
    public bool HyperV { get; set; } = true;
    /// <summary>Ping a szerverre és az alapértelmezett átjáróra (válaszidő, ingadozás, csomagvesztés).</summary>
    public bool Ping { get; set; } = true;
    /// <summary>További ping-célok (név vagy IP).</summary>
    public List<string> PingTargets { get; set; } = new();
    /// <summary>
    /// A szerver ismeri az AgentStatus új mezőit: 2 = TopProcesses (1.5.0), 3 = ServiceList (1.5.1), 4 = ServiceStates +
    /// UsbEvents + Components (1.5.6). Régi szerver nem küldi - akkor az agent sem tölti ki őket (az aláírás a teljes, újraszerializált Status-t fedi).
    /// </summary>
    public int StatusVersion { get; set; }

    // ---- 1.5.6 (0 / üres = alapérték, a régi szerver nem küldi)
    /// <summary>Programfrissítések keresése ennyi percenként (alap 30), Windows Update ennyi óránként (alap 6).</summary>
    public int AppUpdateMinutes { get; set; }
    public int WuScanHours { get; set; }
    /// <summary>A figyelt szolgáltatások (Services) gyors ellenőrzése ennyi másodpercenként (0 = ki); leálláskor az agent azonnal jelentkezik.</summary>
    public int WatchSeconds { get; set; }
    /// <summary>Másolás külső (cserélhető / USB) meghajtóra: a ráírt fájlok naplózása. Alapból KI (munkavállalói megfigyelés - GDPR).</summary>
    public bool UsbAudit { get; set; }
    /// <summary>1.5.7: tálca-ikon a bejelentkezett felhasználóknak (a régi szerver nem küldi → nincs tálca).</summary>
    public bool Tray { get; set; }
}

/// <summary>Egy gyorsan figyelt szolgáltatás állapota (1.5.6): fut-e, és mióta ebben az állapotban.</summary>
public sealed class ServiceState
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>A szolgáltatás nincs a gépen.</summary>
    public bool Missing { get; set; }
    public bool Running { get; set; }
    public string Status { get; set; } = "";
    public DateTime SinceUtc { get; set; }
}

/// <summary>Esemény a külső (cserélhető / USB) meghajtón (1.5.6): csatlakozás, leválasztás, fájl írása / átnevezése / törlése.</summary>
public sealed class UsbEvent
{
    public DateTime AtUtc { get; set; }
    /// <summary>connect | disconnect | write | rename | delete</summary>
    public string Kind { get; set; } = "";
    public string Drive { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>A kötet sorozatszáma (a meghajtó azonosítására).</summary>
    public string Serial { get; set; } = "";
    /// <summary>A fájl útvonala a meghajtón belül (átnevezésnél az új név; a régi az OldPath).</summary>
    public string Path { get; set; } = "";
    public string OldPath { get; set; } = "";
    public long Size { get; set; }
    /// <summary>A bejelentkezett (konzol-) felhasználó az esemény idején.</summary>
    public string User { get; set; } = "";
}

/// <summary>Driver vagy firmware a gépen (1.5.6, a CVE-kereséshez). Kind: driver | bios | disk.</summary>
public sealed class ComponentInfo
{
    public string Kind { get; set; } = "";
    /// <summary>Driver: a szolgáltató (Intel, Realtek…); BIOS: a gép gyártója; lemez: a modell első szava.</summary>
    public string Vendor { get; set; } = "";
    /// <summary>Driver: az eszköz neve; BIOS: a gép típusa (modell / család); lemez: a modell.</summary>
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>A driver / BIOS dátuma (yyyy-MM-dd), ha ismert.</summary>
    public string Date { get; set; } = "";
    /// <summary>Driver: az eszközosztály (Display, Net, MEDIA…); BIOS: a BIOS gyártója.</summary>
    public string Detail { get; set; } = "";
}

/// <summary>Egy Windows-szolgáltatás a gépen (a „Mit mérjen?” választólistájához).</summary>
public sealed class ServiceEntry
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Running { get; set; }
    /// <summary>Automatikus indítású.</summary>
    public bool Auto { get; set; }
}

/// <summary>Egy folyamat erőforrás-használata (a legtöbb memóriát / CPU-t használók listájához).</summary>
public sealed class ProcessInfo
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public double MemMb { get; set; }
    public double CpuPct { get; set; }
    public int Handles { get; set; }
    public int Threads { get; set; }
}

/// <summary>Programkatalogus-elem (programtar). Base = a regi "alapcsomag" jelolo (1.3.3 elott).</summary>
public sealed class CatalogItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Base { get; set; }
    /// <summary>1.5.6: kézzel választott kategória a repóban (üres = automatikus, a winget-címkékből).</summary>
    public string Category { get; set; } = "";
}

/// <summary>Alkalmazascsomag: a programtar programjaibol osszevalogatott, elnevezett csoport (egy gombbal telepitheto).</summary>
public sealed class AppBundle
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Apps { get; set; } = new();
}

/// <summary>
/// Automatikus frissitesi szabaly: melyik gepekre (csoport), mit (programok / Windows),
/// mikor (karbantartasi ablak), es szabad-e ujrainditani. Alapbol semmi nem automatikus.
/// A Kozpont szabalyai is ilyenek: betarcsazaskor lemennek, es a telephelyen futnak (helyi idoben).
/// </summary>
public sealed class AutoPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";          // ures = minden gep
    public bool Apps { get; set; }
    public bool Windows { get; set; }
    public bool Reboot { get; set; }
    public List<int> Days { get; set; } = new();     // DayOfWeek: 0=vasarnap … 6=szombat
    public int StartHour { get; set; } = 22;
    public int EndHour { get; set; } = 4;            // start > end: ejfelen atnyulo ablak
    public bool Enabled { get; set; } = true;
    /// <summary>Csak a Kozpontban: mely telephelyekre megy le (ures = mindre). A telephely nem hasznalja.</summary>
    public List<string> Sites { get; set; } = new();
}

/// <summary>Egy 5 perces metrika-atlag a telephelyrol a Kozpontba (Ts: a sav kezdete, Unix-mp, UTC).</summary>
public sealed class SiteMetricRow
{
    public string AgentId { get; set; } = "";
    public string Key { get; set; } = "";
    public long Ts { get; set; }
    public double Value { get; set; }
    public string Category { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
}

/// <summary>Egy mintavetel: az idopont es az akkor mert ertekek.</summary>
public sealed class MetricSample
{
    public DateTime TimestampUtc { get; set; }
    public List<Metric> Metrics { get; set; } = new();
}

// ================================================================ állapot

public sealed class AgentStatus
{
    public string MachineName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    /// <summary>A futo agent exe SHA-256 lenyomata - ebbol latja a szerver, kell-e frissites.</summary>
    public string AgentSha256 { get; set; } = "";
    public bool IsVirtual { get; set; }
    public string Uptime { get; set; } = "";

    public int UpdatesAvailable { get; set; }
    public List<AppUpdate> Updates { get; set; } = new();

    /// <summary>Egészség-ellenőrzések eredménye (opcionális, tartományvezérlőknél hasznos).</summary>
    public List<HealthItem> Health { get; set; } = new();

    /// <summary>
    /// Monitorozási metrikák (CPU, RAM, lemez, szolgáltatások, folyamatok, hőmérséklet…).
    /// Kulcs-alapú, hogy uj metrika a modell valtoztatasa nelkul bekerulhessen; a
    /// FleetServer ezekbol epit idosort es ezekre allitasz kuszoboket.
    /// </summary>
    public List<Metric> Metrics { get; set; } = new();

    // ---- programok és Windows Update
    public List<InstalledApp> Installed { get; set; } = new();
    public List<WinUpdate> WindowsUpdates { get; set; } = new();
    public bool PendingReboot { get; set; }
    /// <summary>Honnan frissit a Windows: "Microsoft Update", "WSUS: http://…", "Intune (MDM)".</summary>
    public string UpdateSource { get; set; } = "";
    public DateTime? WuScannedUtc { get; set; }
    public string WuError { get; set; } = "";

    public DateTime CollectedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A jelentkezesek KOZOTT, SampleSeconds-onkent vett minták (idobelyeggel) - igy a grafikon
    /// percre pontos, nem csak jelentkezesenkent egy pont. Null, ha nincs (igy ki sem irodik,
    /// es a regi szerverek alairas-ellenorzese valtozatlan marad).
    /// </summary>
    public List<MetricSample>? MetricSamples { get; set; }

    /// <summary>A legtöbb memóriát / CPU-t használó folyamatok (1.5.0; csak ha a szerver jelezte: MetricConfig.StatusVersion ≥ 2).</summary>
    public List<ProcessInfo>? TopProcesses { get; set; }

    /// <summary>
    /// A gép szolgáltatásai (1.5.1; csak ha MetricConfig.StatusVersion ≥ 3) - a „Mit mérjen?” listájához. Csak változáskor
    /// (és 6 óránként) küldi az agent; null = nem változott, a szerver megtartja az előzőt.
    /// </summary>
    public List<ServiceEntry>? ServiceList { get; set; }

    /// <summary>1.5.6 (csak ha MetricConfig.StatusVersion ≥ 4): a gyorsan figyelt szolgáltatások állapota.</summary>
    public List<ServiceState>? ServiceStates { get; set; }
    /// <summary>1.5.6 (csak ha StatusVersion ≥ 4 és UsbAudit): a külső meghajtók eseményei az előző jelentés óta.</summary>
    public List<UsbEvent>? UsbEvents { get; set; }
    /// <summary>
    /// 1.5.6 (csak ha StatusVersion ≥ 4): a gép külső gyártótól származó driverei és firmware-e (BIOS, lemezek) a
    /// sérülékenység-kereséshez. Csak változáskor (és naponta) küldi az agent; null = nem változott, a szerver megtartja.
    /// </summary>
    public List<ComponentInfo>? Components { get; set; }

    /// <summary>Masolat a percenkenti mintak nelkul - a szerver ezt tarolja (a mintak a metrikatarba mennek, nem a fleet.json-ba).</summary>
    public AgentStatus WithoutSamples()
    {
        var c = (AgentStatus)MemberwiseClone();
        c.MetricSamples = null;
        c.UsbEvents = null;   // az USB-napló külön fájlba megy, nem a fleet.json-ba
        return c;
    }
}

/// <summary>
/// Egy meresi ertek. A Key stabil azonosito (pl. "cpu.load", "disk.C.usedpct",
/// "svc.Spooler.running"), erre hivatkozik a kuszob es a grafikon. A Value szam;
/// szoveges allapotot (fut/all) 1/0-kent adunk at, hogy egysegesen tarolhato legyen.
/// </summary>
public sealed class Metric
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public string Category { get; set; } = "";   // CPU / Memória / Lemez / Szolgáltatás / Folyamat / Hőmérséklet / Napló
    public string Text { get; set; } = "";        // opcionális ember-olvasható részlet
}

public sealed class InstalledApp
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class WinUpdate
{
    public string UpdateId { get; set; } = "";
    public string Kb { get; set; } = "";
    public string Title { get; set; } = "";
    public string Severity { get; set; } = "";
    public bool RebootRequired { get; set; }
    public double SizeMb { get; set; }
}

public sealed class AppUpdate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Available { get; set; } = "";
}

public sealed class HealthItem
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";   // Rendben / Figyelmeztetes / Hiba
    public string Detail { get; set; } = "";
}

// ================================================================ tálca-felület (1.5.7)

/// <summary>
/// Agent → szerver: a gép tálca-felületének kérése a bejelentkezett felhasználó nevében. Az agent kulcsával aláírva; a
/// „user” előtag miatt más üzenet aláírása nem használható fel hozzá. Kind: catalog | search | install | request |
/// requests | login-start | login | admin-install | admin-requests | admin-decide. User: a tálcát futtató Windows-
/// felhasználó (az agent a named pipe kliensének azonosításából veszi, nem a tálca mondja meg). Data: a kérés JSON-ja.
/// </summary>
public sealed class AgentUserRequest
{
    public string AgentId { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Kind { get; set; } = "";
    public string User { get; set; } = "";
    public string Data { get; set; } = "";
    public string Signature { get; set; } = "";

    public string CanonicalPayload() => $"user\n{AgentId}\n{Timestamp}\n{Nonce}\n{Kind}\n{User}\n{Data}";
}

/// <summary>A szerver aláírt válasza a tálca kérésére (a kérés nonce-ához és az agenthez kötve).</summary>
public sealed class AgentUserReplyBody
{
    public string AgentId { get; set; } = "";
    public string RequestNonce { get; set; } = "";
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Data { get; set; } = "";
}

public sealed class AgentUserResponse
{
    public string SignedPayload { get; set; } = "";
    public string Signature { get; set; } = "";
    public string? SignerPublicKeyPem { get; set; }
    public List<KeyLink>? KeyChain { get; set; }
}

/// <summary>
/// „Belépés adminként” a tálcán (1.5.7): kihívás–válasz, a jelszó nem megy át a hálón. A tálca a jelszóból a szerver
/// sójával és iterációszámával kiszámolja a felhasználó PBKDF2-kulcsát (ugyanazt, amit a szerver tárol), és azzal
/// HMAC-olja a szerver egyszer használatos kihívását az agent azonosítójával és a felhasználónévvel együtt.
/// </summary>
public static class TrayLogin
{
    public static byte[] Key(string password, string saltB64, int iterations) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password),
            Convert.FromBase64String(saltB64), iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

    public static string Proof(byte[] key, string agentId, string username, string challenge)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(key);
        return Convert.ToBase64String(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"tray-login\n{agentId}\n{username.ToLowerInvariant()}\n{challenge}")));
    }
}

// ================================================================ feladatok

/// <summary>Uj erteket CSAK a vegere szabad felvenni - a regi agentek szam szerint ertelmezik.</summary>
// Uj tipust CSAK a vegere (regi agentek/telephelyek szam szerint kapjak). Az UpdateAgent a telephelynek szol:
// nem jut el az agenthez, a telephely a kovetkezo jelentkezeskor felajanlja az agent-frissitest.
public enum AgentTaskKind { UpgradeApp, UpgradeAll, RunInventory, InstallApp, UninstallApp, WindowsUpdateScan, WindowsUpdateInstall, Reboot, UpdateAgent, RunCommand, RebootNow, ServiceStart, ServiceStop, ServiceRestart }

/// <summary>
/// Programazonosito ellenorzese (winget Id vagy ARP-azonosito). A cel a parancssorba
/// idezojelek koze kerul, ezert idezojel, vezerlokarakter es kotojellel kezdodo (kapcsolo)
/// ertek nem lehet benne. A szerver ES az agent is ellenorzi.
/// </summary>
public static class WingetId
{
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 200 && !id.StartsWith('-') &&
        id.All(c => c != '"' && !char.IsControl(c)) && id.Trim() == id;
}

/// <summary>
/// Windows-szolgáltatásnév ellenőrzése (indít / leállít / újraindít feladatokhoz). A nevet az agent a
/// ServiceController API-nak adja át (NEM parancssornak), de a szerver ES az agent is ellenőrzi: a Windows
/// szolgáltatásneve nem tartalmazhat „/” és „\” karaktert, legfeljebb 256 hosszú, és nincs benne vezérlőkarakter.
/// </summary>
public static class ServiceName
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && name.Trim() == name &&
        name.All(c => c != '/' && c != '\\' && c != '"' && !char.IsControl(c));
}

public sealed class AgentTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public AgentTaskKind Kind { get; set; }
    /// <summary>UpgradeApp esetén a winget csomagazonosító.</summary>
    public string Target { get; set; } = "";
}

/// <summary>Agent → szerver: egy feladat eredményének visszajelzése.</summary>
public sealed class TaskResult
{
    public string AgentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Signature { get; set; } = "";

    public string CanonicalPayload() =>
        $"{AgentId}\n{TaskId}\n{Success}\n{ExitCode}\n{Timestamp}\n{Nonce}";
}
