using System.Management;
using System.Runtime.Versioning;

namespace DevToolbox;

public enum ServiceRisk
{
    /// <summary>Ceges munkaallomason bizton letilthato.</summary>
    Biztonsagos,
    /// <summary>Fugg a hasznalattol - olvasd el, mi romlik el.</summary>
    Megfontolando,
    /// <summary>Csak nagyon indokolt esetben, tudatosan.</summary>
    Kockazatos
}

public sealed record ServiceAdvice(
    string Name,
    string Display,
    string Category,
    ServiceRisk Risk,
    string What,
    string Impact,
    string DefaultStartMode);

public sealed class ServiceRow
{
    public string Name = "";
    public string Display = "";
    public string State = "";
    public string StartMode = "";
    public string Description = "";
    public ServiceAdvice? Advice;

    public bool IsKnown => Advice is not null;
    public bool IsDisabled => StartMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);

    public string StartModeHu => StartMode.ToLowerInvariant() switch
    {
        "auto" => "Automatikus",
        "manual" => "Kézi",
        "disabled" => "Letiltva",
        _ => StartMode
    };

    public string StateHu => State.ToLowerInvariant() switch
    {
        "running" => "Fut",
        "stopped" => "Áll",
        "paused" => "Szüneteltetve",
        _ => State
    };
}

[SupportedOSPlatform("windows")]
public static class WindowsServices
{
    /// <summary>
    /// Ceges munkaallomason jellemzoen felesleges Windows-szolgaltatasok.
    /// Minden bejegyzesnel szerepel, hogy MI ROMLIK EL a letiltastol - a lista
    /// nem "optimalizalasi" javaslat, hanem donteshez adott informacio.
    /// </summary>
    public static readonly ServiceAdvice[] Catalog =
    {
        // ---------------------------------------------------------------- Xbox / játék
        new("XblAuthManager", "Xbox Live Auth Manager", "Xbox és játék", ServiceRisk.Biztonsagos,
            "Az Xbox Live szolgáltatásokhoz tartozó hitelesítés.",
            "Csak az Xbox alkalmazás és a Game Pass játékok nem működnek. Céges gépen nincs hatása.",
            "Manual"),
        new("XblGameSave", "Xbox Live Game Save", "Xbox és játék", ServiceRisk.Biztonsagos,
            "Játékmentések szinkronizálása az Xbox felhőbe.",
            "Csak az Xbox játékok felhős mentése szűnik meg.", "Manual"),
        new("XboxGipSvc", "Xbox Accessory Management", "Xbox és játék", ServiceRisk.Biztonsagos,
            "Xbox kontrollerek és kiegészítők firmware-kezelése.",
            "Xbox kontroller USB-n továbbra is működik, csak a firmware-frissítés nem.", "Manual"),
        new("XboxNetApiSvc", "Xbox Live Networking Service", "Xbox és játék", ServiceRisk.Biztonsagos,
            "Hálózati funkciók az Xbox Live-hoz.",
            "Nincs hatása a normál hálózati működésre.", "Manual"),
        new("GamingServices", "Gaming Services", "Xbox és játék", ServiceRisk.Biztonsagos,
            "A Microsoft Store-ból telepített játékok futtatókörnyezete.",
            "A Store-os játékok nem indulnak és nem telepíthetők. Egyéb Store-alkalmazásokat nem érint.",
            "Manual"),
        new("BcastDVRUserService", "GameDVR és közvetítés", "Xbox és játék", ServiceRisk.Biztonsagos,
            "Játékfelvétel és képernyőközvetítés (Game Bar).",
            "A Win+G játéksáv felvételi funkciója nem működik. A képernyőkép továbbra is igen.",
            "Manual"),

        // ---------------------------------------------------------------- fogyasztói funkciók
        new("MapsBroker", "Downloaded Maps Manager", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "Offline térképek letöltése és frissítése a Térképek alkalmazásnak.",
            "Csak az offline térképek. Az online térkép a böngészőben működik.", "Automatic"),
        new("WalletService", "Wallet Service", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "A Microsoft Wallet (fizetési kártyák) háttérszolgáltatása.",
            "Céges gépen nincs hatása.", "Manual"),
        new("SEMgrSvc", "Payments and NFC/SE Manager", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "NFC-alapú fizetés kezelése.",
            "Csak az érintéses fizetés, amit asztali gépen senki nem használ.", "Manual"),
        new("RetailDemo", "Retail Demo Service", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "Bolti bemutató üzemmód.",
            "Semmi – ez kizárólag üzletben kiállított gépeken használt.", "Manual"),
        new("WpcMonSvc", "Parental Controls", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "Szülői felügyelet (Microsoft Family).",
            "Céges gépen nincs hatása. Otthoni gépen a gyerekfiók korlátozásai szűnnek meg.",
            "Manual"),
        new("MessagingService", "MessagingService", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "SMS-kezelés és üzenetszinkronizálás.",
            "A Telefonos kapcsolat (Phone Link) SMS funkciója nem működik.", "Manual"),
        new("PimIndexMaintenanceSvc", "Contact Data", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "A Névjegyek alkalmazás adatainak indexelése.",
            "A beépített Levelezés/Naptár/Névjegyek keresése lassul. Outlookot nem érint.",
            "Manual"),
        new("PhoneSvc", "Phone Service", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "Telefonhívás-kezelés (mobil eszközökhöz).",
            "Asztali gépen nincs hatása.", "Manual"),
        new("Fax", "Fax", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "Faxküldés és -fogadás.",
            "Csak akkor számít, ha tényleg van faxmodem a gépben.", "Manual"),
        new("AJRouter", "AllJoyn Router Service", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "IoT eszközök közötti AllJoyn kommunikáció.",
            "Gyakorlatilag semmi – elhagyott technológia.", "Manual"),
        new("icssvc", "Windows Mobile Hotspot Service", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "A gép Wi-Fi hotspotként való megosztása.",
            "A mobil hotspot funkció nem indul.", "Manual"),
        new("SharedAccess", "Internet Connection Sharing", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "Internetkapcsolat megosztása más gépekkel (NAT).",
            "Csak a kapcsolatmegosztás. Egyes VPN- és virtualizációs eszközök viszont " +
            "használják – ha Hyper-V vagy VirtualBox NAT van a gépen, hagyd meg.",
            "Manual"),

        // ---------------------------------------------------------------- hardver, ha nincs
        new("bthserv", "Bluetooth Support Service", "Hardverfüggő", ServiceRisk.Megfontolando,
            "Bluetooth eszközök felderítése és társítása.",
            "MINDEN Bluetooth eszköz megszűnik működni: egér, billentyűzet, fejhallgató. " +
            "Csak akkor tiltsd, ha a gépben nincs vagy nem használt a Bluetooth.", "Manual"),
        new("WbioSrvc", "Windows Biometric Service", "Hardverfüggő", ServiceRisk.Megfontolando,
            "Ujjlenyomat- és arcfelismerés (Windows Hello).",
            "A Windows Hello biometrikus bejelentkezés nem működik – PIN-nel és jelszóval " +
            "továbbra is be lehet lépni.", "Manual"),
        new("StiSvc", "Windows Image Acquisition (WIA)", "Hardverfüggő", ServiceRisk.Megfontolando,
            "Lapolvasók és fényképezőgépek kezelése.",
            "A szkennelés nem működik. Multifunkciós nyomtatónál ez fontos lehet.", "Automatic"),
        new("TabletInputService", "Touch Keyboard and Handwriting", "Hardverfüggő", ServiceRisk.Megfontolando,
            "Érintőképernyős billentyűzet és kézírás-felismerés.",
            "Érintőképernyős gépen és táblagépen a képernyőbillentyűzet eltűnik. " +
            "Asztali gépen nincs hatása.", "Manual"),

        // ---------------------------------------------------------------- adatvédelem, telemetria
        new("DiagTrack", "Connected User Experiences and Telemetry", "Telemetria", ServiceRisk.Megfontolando,
            "Használati és diagnosztikai adatok küldése a Microsoftnak.",
            "Céges környezetben gyakran tiltják. Viszont ez táplálja a Windows Update for " +
            "Business jelentéseket és az Intune eszközállapotot – ha ezeket használjátok, hagyd meg.",
            "Automatic"),
        new("dmwappushservice", "WAP Push Message Routing", "Telemetria", ServiceRisk.Biztonsagos,
            "Telemetriához kapcsolódó üzenetútvonal-kezelés.",
            "Nincs érdemi hatás. Újabb Windows kiadásokból már hiányzik.", "Manual"),
        new("diagnosticshub.standardcollector.service", "Diagnostics Hub Collector",
            "Telemetria", ServiceRisk.Biztonsagos,
            "A Visual Studio diagnosztikai gyűjtője.",
            "Csak a Visual Studio teljesítménymérő eszközeit érinti.", "Manual"),
        new("WerSvc", "Windows Error Reporting", "Telemetria", ServiceRisk.Megfontolando,
            "Alkalmazás-összeomlások jelentése a Microsoftnak.",
            "Az összeomlások nem kerülnek be a jelentésekbe. A helyi eseménynapló " +
            "továbbra is rögzíti őket, tehát a hibakeresés nem lehetetlenül el.", "Manual"),

        // ---------------------------------------------------------------- hálózat, biztonság
        new("RemoteRegistry", "Remote Registry", "Hálózat és biztonság", ServiceRisk.Biztonsagos,
            "A registry távoli szerkesztésének engedélyezése.",
            "Biztonsági szempontból ÉRDEMES letiltva hagyni – alapból az is. " +
            "Néhány leltározó eszköz viszont igényli.", "Disabled"),
        new("SSDPSRV", "SSDP Discovery", "Hálózat és biztonság", ServiceRisk.Megfontolando,
            "UPnP eszközök felderítése a hálózaton.",
            "Elromlik a médiamegosztás (DLNA), egyes nyomtatók és a hálózati eszközfelderítés. " +
            "Biztonsági szempontból viszont kevesebb támadási felület.", "Manual"),
        new("upnphost", "UPnP Device Host", "Hálózat és biztonság", ServiceRisk.Megfontolando,
            "A gép UPnP eszközként való megjelenítése.",
            "Ugyanaz, mint az SSDP-nél. Kettesben szokás kezelni őket.", "Manual"),
        new("lfsvc", "Geolocation Service", "Hálózat és biztonság", ServiceRisk.Megfontolando,
            "Helymeghatározás alkalmazások számára.",
            "Az időjárás, Térképek és a helyfüggő alkalmazások nem tudják a helyet. " +
            "Az automatikus időzóna-beállítás is ettől függ.", "Manual"),
        new("CscService", "Offline Files", "Hálózat és biztonság", ServiceRisk.Megfontolando,
            "Hálózati mappák offline elérhetővé tétele.",
            "Ha a felhasználók barangoló profilt vagy offline mappákat használnak, ne tiltsd.",
            "Manual"),

        // ---------------------------------------------------------------- teljesítmény
        new("SysMain", "SysMain (Superfetch)", "Teljesítmény", ServiceRisk.Megfontolando,
            "Gyakran használt alkalmazások előtöltése a memóriába.",
            "SSD-s gépen a haszna csekély, és néha felesleges lemezterhelést okoz. " +
            "HDD-s gépen viszont érezhetően lassul tőle az indulás – ott hagyd meg.",
            "Automatic"),
        new("DoSvc", "Delivery Optimization", "Teljesítmény", ServiceRisk.Megfontolando,
            "Frissítések letöltése és megosztása más gépekkel P2P módon.",
            "A Windows Update továbbra is működik, csak lassabb lehet, és minden gép " +
            "külön tölti le ugyanazt. Nagy irodában ez sávszélességet zabál.", "Automatic"),
        new("PcaSvc", "Program Compatibility Assistant", "Teljesítmény", ServiceRisk.Megfontolando,
            "Régi programok kompatibilitási problémáinak felismerése.",
            "A kompatibilitási varázsló nem ugrik fel. Régi szoftverek futtatásánál hiányozhat.",
            "Automatic"),
        new("WMPNetworkSvc", "Windows Media Player Network Sharing", "Teljesítmény", ServiceRisk.Biztonsagos,
            "Médiatár megosztása a hálózaton a Media Playerből.",
            "Csak a WMP hálózati megosztása. Lejátszani továbbra is lehet.", "Manual"),

        // ---------------------------------------------------------------- óvatosan
        new("Spooler", "Print Spooler", "Óvatosan", ServiceRisk.Kockazatos,
            "A nyomtatási feladatok sorba állítása. Minden nyomtatás ezen megy át.",
            "SEMMILYEN nyomtatás nem működik, és a PDF-nyomtatók sem. Kizárólag olyan gépen " +
            "tiltsd, ahol biztosan nincs és nem is lesz nyomtatás – szerveren ezt biztonsági " +
            "megfontolásból (PrintNightmare) szokás megtenni.", "Automatic"),
        new("WSearch", "Windows Search", "Óvatosan", ServiceRisk.Kockazatos,
            "A fájlok és e-mailek tartalmának indexelése.",
            "Az Outlook keresése ELROMLIK, és az Intézőben a keresés nagyon lelassul. " +
            "Gyenge gépen mégis szokás letiltani – tudd, mit adsz fel érte.", "Automatic"),
        new("DPS", "Diagnostic Policy Service", "Óvatosan", ServiceRisk.Kockazatos,
            "A beépített hibaelhárítók motorja.",
            "Egyetlen Windows-hibaelhárító sem indul el, és néhány hálózati diagnosztikai " +
            "funkció is elromlik. Hibakeresésnél pont ez hiányzik majd.", "Automatic"),
        new("TapiSrv", "Telephony", "Óvatosan", ServiceRisk.Kockazatos,
            "TAPI alapú telefonos/modemes kapcsolatok.",
            "Néhány VPN-kliens, fax- és CTI-szoftver ezen keresztül működik. " +
            "Ha ilyet használtok, ne tiltsd.", "Manual"),

        // ---------------------------------------------------------------- további fogyasztói / speciális
        new("MixedRealityOpenXRSvc", "Windows Mixed Reality OpenXR", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "A Windows Mixed Reality VR-headsetek OpenXR futtatókörnyezete.",
            "Csak akkor számít, ha van WMR VR-szemüveg a gépen – irodai gépen soha.", "Manual"),
        new("wisvc", "Windows Insider Service", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "A Windows Insider előzetes build-ek kezelése.",
            "Éles, céges gépen nem szabadna Insider buildeket futtatni, tehát nincs hatása.", "Manual"),
        new("CloudBackupRestoreSvc", "Cloud Backup and Restore", "Fogyasztói funkciók", ServiceRisk.Biztonsagos,
            "Alkalmazásbeállítások mentése és visszaállítása a Microsoft-fiókon keresztül.",
            "Céges gépen, ahol nincs személyes Microsoft-fiók, nincs hatása.", "Manual"),
        new("OneSyncSvc", "Sync Host", "Fogyasztói funkciók", ServiceRisk.Megfontolando,
            "Levelezés, Naptár és Névjegyek szinkronizálása a beépített alkalmazásokban.",
            "A beépített Levelezés/Naptár alkalmazás nem szinkronizál. Az Outlookot nem érinti. " +
            "Ez felhasználónkénti szolgáltatás, egyedi utótaggal.", "Automatic"),
        new("UnistoreSvc", "User Data Storage", "Fogyasztói funkciók", ServiceRisk.Megfontolando,
            "A beépített alkalmazások felhasználói adatainak (üzenetek, névjegyek) tárolása.",
            "A beépített Személyek, Levelezés és Naptár alkalmazás elromolhat. Outlookot nem érint. " +
            "Felhasználónkénti szolgáltatás.", "Manual"),
        new("UserDataSvc", "User Data Access", "Fogyasztói funkciók", ServiceRisk.Megfontolando,
            "Alkalmazások hozzáférése a névjegyekhez és felhasználói adatokhoz.",
            "Ugyanaz a kör, mint az User Data Storage-nél – együtt szokás kezelni.", "Manual"),

        // ---------------------------------------------------------------- hálózat
        new("WebClient", "WebClient (WebDAV)", "Hálózat és biztonság", ServiceRisk.Megfontolando,
            "WebDAV-megosztások csatlakoztatása hálózati meghajtóként (http/https protokollon).",
            "WebDAV-mappák nem érhetők el. FONTOS: ez biztonsági szempontból hasznos tiltás, mert " +
            "több támadás (pl. NTLM-relay) használja ki. Csak akkor hagyd meg, ha tényleg " +
            "használtok WebDAV-ot – a legtöbb helyen nem.", "Manual"),

        // ---------------------------------------------------------------- smart card (hardverfüggő)
        new("SCardSvr", "Smart Card", "Hardverfüggő", ServiceRisk.Megfontolando,
            "Chipkártyák (smart card) kezelése – kártyás bejelentkezés, elektronikus aláírás.",
            "Ha van kártyaolvasó és kártyás bejelentkezés vagy e-aláírás (pl. e-Szignó, ügyfélkapu " +
            "kártya), NE tiltsd. Enélkül a kártyás hitelesítés megszűnik.", "Manual"),
        new("ScDeviceEnum", "Smart Card Device Enumeration", "Hardverfüggő", ServiceRisk.Megfontolando,
            "Chipkártya-eszközök felismerése az alkalmazások számára.",
            "A Smart Card szolgáltatással együtt jár – ha kártyát használtok, ez is kell.", "Manual"),
        new("SCPolicySvc", "Smart Card Removal Policy", "Hardverfüggő", ServiceRisk.Megfontolando,
            "A gép zárolása, amikor a felhasználó kihúzza a chipkártyát.",
            "Csak akkor van értelme, ha kártyás bejelentkezés van beállítva. Enélkül nincs hatása.", "Manual"),
    };

    public static ServiceAdvice? Find(string name) =>
        Catalog.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string[] CategoryNames =>
        Catalog.Select(c => c.Category).Distinct().ToArray();

    // ================================================================ lekérdezés

    public static List<ServiceRow> Query(WmiContext ctx)
    {
        var list = new List<ServiceRow>();

        foreach (var o in ctx.Query(
                     "SELECT Name, DisplayName, State, StartMode, Description FROM Win32_Service"))
        {
            var name = o["Name"]?.ToString() ?? "";
            if (name.Length == 0) continue;

            list.Add(new ServiceRow
            {
                Name = name,
                Display = o["DisplayName"]?.ToString() ?? name,
                State = o["State"]?.ToString() ?? "",
                StartMode = o["StartMode"]?.ToString() ?? "",
                Description = o["Description"]?.ToString() ?? "",
                Advice = Find(name)
            });
        }

        // Per-user szolgaltatasok (pl. BcastDVRUserService_1a2b3) egyedi utotagot kapnak.
        foreach (var row in list.Where(r => r.Advice is null))
        {
            var underscore = row.Name.LastIndexOf('_');
            if (underscore > 0) row.Advice = Find(row.Name[..underscore]);
        }

        return list.OrderBy(r => r.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ================================================================ módosítás

    public sealed record ChangeResult(bool Ok, string Message);

    /// <param name="startMode">Automatic, Manual vagy Disabled</param>
    public static ChangeResult SetStartMode(WmiContext ctx, string serviceName, string startMode)
    {
        try
        {
            var scope = ctx.BuildScope(@"root\CIMV2");
            scope.Connect();
            using var svc = new ManagementObject(
                scope, new ManagementPath($"Win32_Service.Name='{Escape(serviceName)}'"), null);

            var result = Convert.ToUInt32(svc.InvokeMethod("ChangeStartMode", new object[] { startMode }));
            return result == 0
                ? new ChangeResult(true, "Beállítva: " + startMode)
                : new ChangeResult(false, ReturnCodeText(result));
        }
        catch (Exception ex)
        {
            return new ChangeResult(false, ex.Message);
        }
    }

    public static ChangeResult Stop(WmiContext ctx, string serviceName)
    {
        try
        {
            var scope = ctx.BuildScope(@"root\CIMV2");
            scope.Connect();
            using var svc = new ManagementObject(
                scope, new ManagementPath($"Win32_Service.Name='{Escape(serviceName)}'"), null);

            var result = Convert.ToUInt32(svc.InvokeMethod("StopService", null));
            return result == 0
                ? new ChangeResult(true, "Leállítva")
                : new ChangeResult(false, ReturnCodeText(result));
        }
        catch (Exception ex)
        {
            return new ChangeResult(false, ex.Message);
        }
    }

    private static string Escape(string s) => s.Replace("'", "\\'");

    private static string ReturnCodeText(uint code) => code switch
    {
        1 => "a művelet nem támogatott ennél a szolgáltatásnál",
        2 => "hozzáférés megtagadva – rendszergazdai jog kell",
        3 => "a szolgáltatás függőségei miatt nem állítható le",
        5 => "a szolgáltatás nem ebben az állapotban van",
        8 => "ismeretlen hiba",
        9 => "az elérési út nem található",
        10 => "a szolgáltatás már fut",
        15 => "a szolgáltatás letiltva",
        16 => "a szolgáltatás törlésre van jelölve",
        21 => "érvénytelen paraméter",
        22 => "a fiók nem létezik",
        _ => $"hibakód: {code}"
    };
}
