namespace DevToolbox.Fleet;

// ============================================================================
//  Telephelyi FleetServer <-> Központ (Hub) protokoll.
//
//  Publikus interneten megy, ezert ket reteg vedi:
//   1. TLS (Caddy + Let's Encrypt) - titkositas es a Kozpont domainjenek hitelesitese.
//   2. Alkalmazasszintu alairas MINDKET iranyban - a telephely a sajat kulcsaval ir
//      ala, a Kozpont a sajatjaval. A telephely a Kozpont kulcsat az elso
//      kapcsolodaskor az ujjlenyomattal ellenorzi (pinning). Igy egy TLS-proxyn vagy
//      tanusitvanyon keresztuli tamadas sem tud hamis parancsot kuldeni a telephelyre.
//
//  Az alairas a Payload SZOVEGEN tortenik (nem ujraszerializalt objektumon), igy a
//  JSON-oda-vissza alakitas sosem ronthatja el.
// ============================================================================

/// <summary>Alairt boritek: a Payload egy JSON szoveg, a Signature ezen a szovegen van.</summary>
public sealed class SignedEnvelope
{
    public string SenderId { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
    /// <summary>Az alairo jelenlegi publikus kulcsa + a lanc (kulcscsere). Opcionalis.</summary>
    public string? SignerPublicKeyPem { get; set; }
    public List<KeyLink>? KeyChain { get; set; }

    public static SignedEnvelope Create<T>(string senderId, T body, string privateKeyPem)
    {
        var payload = ProtocolJson.Serialize(body);
        return new SignedEnvelope
        {
            SenderId = senderId,
            Payload = payload,
            Signature = Crypto.Sign(privateKeyPem, payload)
        };
    }

    public bool Verify(string publicKeyPem) => Crypto.Verify(publicKeyPem, Payload, Signature);

    public T? Open<T>() => ProtocolJson.Deserialize<T>(Payload);
}

// ---------------------------------------------------------------- regisztráció

public sealed class HubEnrollRequest
{
    public string Token { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string ServerVersion { get; set; } = "";
}

public sealed class HubEnrollResponse
{
    public bool Success { get; set; }
    public string SiteId { get; set; } = "";
    public string Message { get; set; } = "";
    public string HubPublicKeyPem { get; set; } = "";
    public List<KeyLink>? KeyChain { get; set; }
}

// ---------------------------------------------------------------- jelentkezés

/// <summary>A telephely → Kozpont uzenet tartalma (a borítékban, alairva).</summary>
public sealed class HubCheckinBody
{
    public string Timestamp { get; set; } = "";
    public string Nonce { get; set; } = "";
    public SiteSummary Summary { get; set; } = new();

    /// <summary>
    /// Az elozo betarcsazas ota LEZART 5 perces metrika-atlagok (gepenkent, kulcsonkent). Csak ha a
    /// Kozpont jelezte, hogy fogadja (HubCheckinReply.AcceptsMetrics). Az alairas a nyers szovegre
    /// vonatkozik, ezert a regi Kozpont ezt a mezot egyszeruen figyelmen kivul hagyja.
    /// </summary>
    public List<SiteMetricRow>? Metrics { get; set; }

    /// <summary>A Kozpont altal kert agent-beleptetok (uj gep a Kozpontbol) - a kovetkezo betarcsazaskor mennek fel.</summary>
    public List<HubTokenResult>? TokenResults { get; set; }

    /// <summary>
    /// 1.5.7: a telephely naplóinak új sorai az előző sikeres felküldés óta (audit, riasztás, SNMP, szerver – az USB-napló
    /// NEM: személyes adat, a telephelyen marad). Csak ha a Központ jelezte, hogy fogadja (HubCheckinReply.AcceptsLogs).
    /// </summary>
    public List<SiteLogChunk>? Logs { get; set; }
}

/// <summary>Egy napló új része: Log = audit | alerts | snmp | server; Text = egész sorok (\n-nel).</summary>
public sealed class SiteLogChunk
{
    public string Log { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// A Központ művelete a telephelyen (1.5.7), a betárcsázás aláírt válaszában: a telephely hajtja végre (ott marad az
/// egyetlen író), és a következő összesítőben jön vissza az eredmény. Kind: rule-save | rule-delete | request-approve |
/// request-deny. By: a Központ felhasználója (a telephely műveleti naplójába).
/// </summary>
public sealed class HubSiteOp
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public SiteAlertRule? Rule { get; set; }
    public string Note { get; set; } = "";
    public string By { get; set; } = "";
}

/// <summary>Programkérés a gép tálca-ikonjáról (1.5.7) – a telephely jóváhagyási sorából, a Központ is döntheti.</summary>
public sealed class ProgramRequestInfo
{
    public string Id { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Machine { get; set; } = "";
    /// <summary>A kérő Windows-felhasználó (DOMAIN\név).</summary>
    public string User { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>pending | approved | denied | installing | installed | failed</summary>
    public string State { get; set; } = "";
    public string DecidedBy { get; set; } = "";
    public DateTime? DecidedUtc { get; set; }
    public string Note { get; set; } = "";
    /// <summary>A jóváhagyás után kiadott telepítési feladat (az eredménye zárja le a kérést).</summary>
    public string TaskId { get; set; } = "";
}

/// <summary>A Kozpont kerese: keszits beleptetot egy uj agenthez (a token csak a telephelyen keletkezhet).</summary>
public sealed class HubTokenRequest
{
    public string RequestId { get; set; } = "";
    public string Label { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 60;
    public int MaxUses { get; set; } = 1;
}

/// <summary>A telephely valasza: a token, az agentek altal elerheto cim es a szerver ujjlenyomata.</summary>
public sealed class HubTokenResult
{
    public string RequestId { get; set; } = "";
    public string Token { get; set; } = "";
    public string AgentUrl { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class SiteSummary
{
    public string SiteName { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public List<SiteAgent> Agents { get; set; } = new();

    // ---- frissítés a Központból (új mezők a végén)
    public bool AgentAutoUpdate { get; set; }
    public string AgentPackageVersion { get; set; } = "";
    /// <summary>A telephely ismeri a Központból indított szerver-/agentfrissítést (ServerUpdate, UpdateAgent).</summary>
    public bool SupportsHubUpdates { get; set; }

    // ---- 1.5.0 (új mezők a végén; a régi Központ figyelmen kívül hagyja)
    /// <summary>A telephely minden csoportja (az üresek is) - a Központ csoport-oldalához.</summary>
    public List<string>? GroupNames { get; set; }
    /// <summary>A telephely végrehajtja a Központ csoport-műveleteit (HubCheckinReply.GroupOps).</summary>
    public bool SupportsGroupOps { get; set; }
    /// <summary>A Központtól legutóbb kapott programtár lenyomata: ha nem változott, a Központ nem küldi újra.</summary>
    public string HubCatalogHash { get; set; } = "";
    /// <summary>A telephely agent-portja (és portváltás alatt a régi is) - a Központ kijelzi.</summary>
    public int AgentPort { get; set; }
    public int OldAgentPort { get; set; }
    /// <summary>A telephely gépein levő szolgáltatások összesítve (1.5.1) - a Központ „Mit mérjen?” választólistájához.</summary>
    public List<SiteService>? Services { get; set; }

    // ---- 1.5.5 (új mezők a VÉGÉN: a régi Központ figyelmen kívül hagyja)
    /// <summary>A telephely internetkapcsolat-figyelése (null: nincs bekapcsolva / régi telephely).</summary>
    public SiteInternet? Internet { get; set; }
    /// <summary>Az SNMP-fogadás be van-e kapcsolva, és a legutóbbi riasztást adó trapek (a Központ azonosító szerint veszi át az újakat).</summary>
    public bool SnmpEnabled { get; set; }
    public List<SiteSnmpEvent>? SnmpEvents { get; set; }
    /// <summary>1.5.6: a gyorsan figyelt szolgáltatások leállásai / újraindulásai (Source = gép, Cleared = újra fut).</summary>
    public List<SiteSnmpEvent>? ServiceEvents { get; set; }
    /// <summary>1.5.6: a Központtól legutóbb kapott CVE-találatok lenyomata (ha nem változott, a Központ nem küldi újra).</summary>
    public string HubCveHash { get; set; } = "";

    // ---- 1.5.7 (új mezők a VÉGÉN)
    /// <summary>A telephely saját küszöb-szabályai – a Központ szerkesztheti őket (HubSiteOp), a telephely írja.</summary>
    public List<SiteAlertRule>? LocalRules { get; set; }
    /// <summary>A telephely végrehajtja a Központ műveleteit (HubCheckinReply.SiteOps).</summary>
    public bool SupportsSiteOps { get; set; }
    /// <summary>Programkérések a tálcáról: a függők és a legutóbbi 50 lezárt.</summary>
    public List<ProgramRequestInfo>? ProgramRequests { get; set; }

    // ---- 1.5.9 (új mezők a VÉGÉN)
    /// <summary>A telephely WireGuard nyilvános kulcsa (a Központ ehhez rendel alagút-címet, és felveszi társként).</summary>
    public string WgPublicKey { get; set; } = "";
    /// <summary>A telephely alagútjának állapota (a Központ felületén): utolsó kézfogás, hiba.</summary>
    public DateTime? WgHandshakeUtc { get; set; }
    public string WgError { get; set; } = "";
}

/// <summary>Internetkapcsolat a telephelyen: az utolsó ellenőrzés és a legutóbbi kiesések.</summary>
public sealed class SiteInternet
{
    /// <summary>null: még nem volt ellenőrzés.</summary>
    public bool? Online { get; set; }
    public DateTime? SinceUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    /// <summary>Amelyik cím válaszolt, és a válaszidő.</summary>
    public string Target { get; set; } = "";
    public long RttMs { get; set; }
    public int IntervalMinutes { get; set; }
    public List<SiteOutage> Outages { get; set; } = new();
}

public sealed class SiteOutage
{
    public DateTime StartUtc { get; set; }
    /// <summary>null: még tart.</summary>
    public DateTime? EndUtc { get; set; }
}

/// <summary>Egy riasztást adó SNMP-trap a telephelyről. Az Id időrendben nő (a telephely újraindítása után is).</summary>
public sealed class SiteSnmpEvent
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>1.5.6: megszűnés (pl. a szolgáltatás újra fut) - a Központ a riasztást lezárja.</summary>
    public bool Cleared { get; set; }
}

/// <summary>Egy szolgáltatás a telephely gépein: hány gépen van, hány gépen fut.</summary>
public sealed class SiteService
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Machines { get; set; }
    public int Running { get; set; }
}

/// <summary>
/// A Központ programtára a telephelyeknek (a Központ a „mester”): a telephelyen csak olvasható, a helyi repó mellett.
/// Csak akkor megy le, ha a telephelynél levő lenyomat (SiteSummary.HubCatalogHash) eltér.
/// </summary>
public sealed class HubCatalog
{
    public string Hash { get; set; } = "";
    public List<CatalogItem> Items { get; set; } = new();
    public List<AppBundle> Bundles { get; set; } = new();
    public List<CatalogItem> Rejected { get; set; } = new();
}

/// <summary>A Központ küszöb-szabálya a telephelyen (ott a helyi szabályok mellett fut, csak olvasható).</summary>
public sealed class SiteAlertRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MetricKey { get; set; } = "";
    /// <summary>0 = nagyobb, 1 = kisebb, 2 = egyenlő (AlertOp).</summary>
    public int Op { get; set; }
    public double Threshold { get; set; }
    /// <summary>Gépcsoport (üres = minden gép) és gép (üres = a csoport / minden gép).</summary>
    public string Group { get; set; } = "";
    public string AgentId { get; set; } = "";
    public int ForSamples { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    /// <summary>1.5.6: a küszöb megjelenítési egysége (csak a felületnek; a Threshold a mérés saját egységében).</summary>
    public string DisplayUnit { get; set; } = "";

    // ---- 1.5.13 (új mezők a VÉGÉN)
    /// <summary>Telephely → Központ: a helyi szabály felküldése központi szabálynak (új érték = új kérés; üres = nincs kérés).</summary>
    public string PromoteToken { get; set; } = "";
    /// <summary>Központ → telephely: ez a központi szabály ebből a helyi szabályból lett – a telephely a helyit mellette nem futtatja.</summary>
    public string OriginRuleId { get; set; } = "";
}

/// <summary>
/// Csoport-művelet a Központból: create / delete / rename (NewName) / members (a csoport tagjai pontosan AgentIds).
/// A telephely hajtja végre (ott a csoport a gépek tulajdonsága), és a következő összesítőben jön vissza az eredmény.
/// </summary>
public sealed class HubGroupOp
{
    public string Op { get; set; } = "";
    public string Group { get; set; } = "";
    public string NewName { get; set; } = "";
    public List<string> AgentIds { get; set; } = new();
}

/// <summary>A Központ ajánlata: a telephely frissítse magát a Központ exéjére (a lenyomat az aláírt válaszban).</summary>
public sealed class HubServerUpdate
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Rövid életű letöltési azonosító: GET /hub/fleet-exe?t=…</summary>
    public string DownloadToken { get; set; } = "";
}

public sealed class SiteAgent
{
    public string AgentId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public bool Online { get; set; }
    public bool Revoked { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public int UpdatesAvailable { get; set; }
    public List<AppUpdate> Updates { get; set; } = new();

    // ---- Gépkezelés a Központból (új mezők a végén: a régi telephelyek egyszerűen nem küldik őket)
    public List<string> Groups { get; set; } = new();
    public bool PendingReboot { get; set; }
    public List<WinUpdate> WindowsUpdates { get; set; } = new();
    public List<InstalledApp> Installed { get; set; } = new();
    /// <summary>A programlista nem valtozott az elozo kuldes ota, ezert most ures - a Kozpont tartsa meg az elozot.</summary>
    public bool InstalledOmitted { get; set; }
    /// <summary>1.5.4: virtuális gép-e (az automatikus „Virtuális / Fizikai gépek” csoporthoz); null = régi telephely, nem ismert.</summary>
    public bool? IsVirtual { get; set; }
    /// <summary>1.5.6: eszköz-összegzés – a telephely számolja (ott vannak a mérések és a riasztások), a Központ csak mutatja.</summary>
    public SiteScore? Score { get; set; }
    /// <summary>1.5.6: driverek és firmware (a CVE-kereséshez); null = nem változott az előző küldés óta, a Központ megtartja.</summary>
    public List<ComponentInfo>? Components { get; set; }
    /// <summary>1.5.10: a gép szolgáltatásai (a Központ szolgáltatás-kezelő paneljéhez); null = nem változott az előző küldés óta, a Központ megtartja.</summary>
    public List<ServiceEntry>? ServiceList { get; set; }
}

/// <summary>A Központ sérülékenység-találatai a telephely gépeire (1.5.6) – csak ha a telephelynél levő lenyomat eltér.</summary>
public sealed class HubCveData
{
    public string Hash { get; set; } = "";
    public DateTime? CheckedUtc { get; set; }
    public List<SiteCveAgent> Agents { get; set; } = new();
}

public sealed class SiteCveAgent
{
    public string AgentId { get; set; } = "";
    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    /// <summary>A CISA KEV-listán szereplő (ténylegesen kihasznált) találatok száma.</summary>
    public int Exploited { get; set; }
    /// <summary>A találatok súlyosság szerint (legfeljebb 300 gépenként).</summary>
    public List<CveFinding> Findings { get; set; } = new();
}

public sealed class CveFinding
{
    public string Id { get; set; } = "";
    /// <summary>CVSS alappontszám (3.1 / 3.0 / 4.0 / 2.0, ami van) és a súlyosság: CRITICAL | HIGH | MEDIUM | LOW | NONE.</summary>
    public double Score { get; set; }
    public string Severity { get; set; } = "";
    /// <summary>Szerepel a CISA KEV-listán (ténylegesen kihasználják); Ransomware: zsarolóvírus-kampányban is.</summary>
    public bool Kev { get; set; }
    public bool Ransomware { get; set; }
    /// <summary>EPSS: a kihasználás valószínűsége a következő 30 napban (0–1).</summary>
    public double Epss { get; set; }
    /// <summary>app | driver | bios</summary>
    public string Kind { get; set; } = "";
    /// <summary>A gépen levő név és verzió.</summary>
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Az első javított verzió, ha az NVD megadja (versionEndExcluding).</summary>
    public string FixedIn { get; set; } = "";
    public string Published { get; set; } = "";
    public string Summary { get; set; } = "";
}

/// <summary>Eszköz-összegzés (DeviceScore.Result): 0–100, Biztonság + Állapot, és a levonások tételesen.</summary>
public sealed class SiteScore
{
    public int Score { get; set; }
    public int Security { get; set; }
    public int Health { get; set; }
    public string Grade { get; set; } = "";
    public List<SiteScoreItem> Items { get; set; } = new();
}

public sealed class SiteScoreItem
{
    public string Area { get; set; } = "";
    public string Text { get; set; } = "";
    public string Level { get; set; } = "";
    public int Penalty { get; set; }
}

/// <summary>A Kozpont → telephely valasz tartalma (alairva a Kozpont kulcsaval).</summary>
public sealed class HubCheckinReply
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    /// <summary>A keres nonce-a visszakuldve: a valasz ehhez a kereshez kotott, nem jatszhato vissza.</summary>
    public string RequestNonce { get; set; } = "";
    /// <summary>A Kozpontban beallitott betarcsazasi ido - a telephely ehhez igazodik.</summary>
    public int NextCheckinSeconds { get; set; } = 300;
    public List<HubTask> Tasks { get; set; } = new();
    /// <summary>A Kozpont fogadja a metrika-atlagokat (HubCheckinBody.Metrics) - regi Kozpont nem kuldi.</summary>
    public bool AcceptsMetrics { get; set; }
    /// <summary>A Kozpont erre a telephelyre vonatkozo automatikus szabalyai (null: regi Kozpont - ne bantsuk).</summary>
    public List<AutoPolicy>? Policies { get; set; }
    /// <summary>Uj agent a Kozpontbol: keszits beleptetot (a valasz a kovetkezo betarcsazasban jon).</summary>
    public List<HubTokenRequest>? TokenRequests { get; set; }
    /// <summary>A telephely frissítse a FleetServert a Központ verziójára (csak ha kérték).</summary>
    public HubServerUpdate? ServerUpdate { get; set; }
    /// <summary>A Központból állított „agentek automatikus frissítése” (null: ne változtass).</summary>
    public bool? AgentAutoUpdate { get; set; }

    // ---- 1.5.0 (null: régi Központ - a telephely megtartja, ami nála van)
    /// <summary>A Központ programtára - csak ha a telephelynél levő lenyomat eltér.</summary>
    public HubCatalog? Catalog { get; set; }
    /// <summary>A Központ erre a telephelyre leküldött küszöb-szabályai.</summary>
    public List<SiteAlertRule>? AlertRules { get; set; }
    /// <summary>A Központ mérés-beállítása: a telephely a sajátjával összefésüli (amit bármelyik kér, azt méri).</summary>
    public MetricConfig? Monitor { get; set; }
    /// <summary>Csoport-műveletek a Központból (csak ha a telephely jelezte: SupportsGroupOps).</summary>
    public List<HubGroupOp>? GroupOps { get; set; }

    // ---- 1.5.6 (új mezők a VÉGÉN)
    /// <summary>Sérülékenység-találatok a telephely gépeire (null: nem változott / régi Központ).</summary>
    public HubCveData? Cve { get; set; }

    // ---- 1.5.7 (új mezők a VÉGÉN)
    /// <summary>A Központ műveletei a telephelyen (csak ha a telephely jelezte: SupportsSiteOps).</summary>
    public List<HubSiteOp>? SiteOps { get; set; }
    /// <summary>A Központ fogadja és tárolja a telephely naplóit (HubCheckinBody.Logs) – a felküldött részt eltárolta.</summary>
    public bool AcceptsLogs { get; set; }

    // ---- 1.5.8 (új mezők a VÉGÉN)
    /// <summary>A Központban beállított megőrzés a telephely szerverén (nap; null = a telephely saját beállítása).</summary>
    public int? MetricKeepDays { get; set; }
    /// <summary>A Központ fogadja a lezárt napi mérési fájlokat (POST /hub/metric-file) – a telephely a megőrzés után feltölti.</summary>
    public bool AcceptsMetricFiles { get; set; }

    // ---- 1.5.9 (új mezők a VÉGÉN)
    /// <summary>A WireGuard-alagút adatai ehhez a telephelyhez (null: a Központban nincs WireGuard, vagy még nem kapta meg a kulcsot).</summary>
    public HubWgOffer? Wg { get; set; }
}

/// <summary>
/// 1.5.9: a WireGuard-alagút a Központ felé. A telephely ebből állítja be a saját adapterét (címe SiteAddress/Prefix, egyetlen
/// társa a Központ: HubPublicKey @ Endpoint, engedélyezett címe HubAddress/32), és HubUrl-en (az alagúton át) tárcsáz be.
/// Required: a Központ a telephely kéréseit csak az alagútból fogadja (szigorú mód).
/// </summary>
public sealed class HubWgOffer
{
    public string HubPublicKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string HubAddress { get; set; } = "";
    public string SiteAddress { get; set; } = "";
    public int Prefix { get; set; } = 24;
    public string HubUrl { get; set; } = "";
    public int Keepalive { get; set; } = 25;
    public bool Required { get; set; }
}

/// <summary>
/// 1.5.9: a telephely WireGuard-kulcsának közlése / frissítése HTTPS-en (POST /hub/wg-key, a telephely kulcsával aláírt
/// SignedEnvelope-ban). Szigorú módban is ez az egyetlen, alagúton kívül fogadott kérés: csak nyilvános kulcsot és címet visz,
/// a válasz a Központ kulcsával aláírt és a nonce-hoz kötött – így a helyben javított / cserélt kulcs után is felépül az alagút.
/// </summary>
public sealed class HubWgKeyRequest
{
    public string WgPublicKey { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Nonce { get; set; } = "";
}

public sealed class HubWgKeyReply
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string RequestNonce { get; set; } = "";
    public HubWgOffer? Offer { get; set; }
}

/// <summary>
/// 1.5.8: egy lezárt napi mérési fájl feltöltésének fejléce (a telephely kulcsával aláírt SignedEnvelope-ban, az
/// X-Fleet-Envelope fejlécben); a törzs a fájl gzip-pel tömörítve. Sha256 / Size: a TÖMÖRÍTETT törzsé.
/// </summary>
public sealed class MetricFileHeader
{
    public string Day { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Timestamp { get; set; } = "";
    public string Nonce { get; set; } = "";
}

/// <summary>A Központ aláírt nyugtája: a fájl eltárolva – a telephely csak erre törli a sajátját.</summary>
public sealed class MetricFileReceipt
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Day { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string RequestNonce { get; set; } = "";
}

/// <summary>A Kozpontbol kiadott feladat egy telephelyi agentnek.</summary>
public sealed class HubTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public string AgentId { get; set; } = "";
    public AgentTaskKind Kind { get; set; }
    public string Target { get; set; } = "";
}
