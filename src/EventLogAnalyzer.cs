using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;

namespace DevToolbox;

public enum Severity { Zaj = 0, Figyelmeztetes = 1, Komoly = 2, Kritikus = 3 }

/// <summary>Egy ismert eseményhez tartozó magyarázat.</summary>
public sealed record Diagnosis(string Title, string Meaning, string Action, Severity Severity);

/// <summary>Azonos forrásból, azonos azonosítóval érkező események összevonva.</summary>
public sealed class ProblemGroup
{
    public string LogName = "";
    public string Provider = "";
    public int EventId;
    public string Subject = "";          // pl. az összeomlott alkalmazás vagy szolgáltatás neve
    public byte Level;                   // 1=kritikus 2=hiba 3=figyelmeztetés
    public int Count;
    public DateTime First = DateTime.MaxValue;
    public DateTime Last = DateTime.MinValue;
    public string SampleMessage = "";
    public Diagnosis? Diag;

    public string Key => $"{LogName}|{Provider}|{EventId}|{Subject}";

    public Severity EffectiveSeverity => Diag?.Severity
        ?? Level switch { 1 => Severity.Kritikus, 2 => Severity.Komoly, _ => Severity.Figyelmeztetes };

    public string LevelLabel => Level switch
    {
        1 => "Kritikus",
        2 => "Hiba",
        3 => "Figyelmeztetés",
        _ => "Egyéb"
    };

    /// <summary>Rendezési súly: elöl a valódi gond, hátul a közismert zaj.</summary>
    public int Rank
    {
        get
        {
            var s = (int)EffectiveSeverity * 1000;
            var freq = Math.Min(Count, 500);
            return s + freq;
        }
    }

    public string Headline =>
        Diag?.Title ?? $"{Provider} – {EventId}. esemény";
}

public sealed class ScanResult
{
    public List<ProblemGroup> Groups { get; } = new();
    public int TotalEvents;
    public int Critical, Errors, Warnings;
    public List<string> Skipped { get; } = new();   // pl. jogosultság hiánya miatt kihagyott napló
    public TimeSpan Duration;
}

[SupportedOSPlatform("windows")]
public static class EventLogAnalyzer
{
    public static readonly string[] DefaultLogs = { "System", "Application" };

    /// <param name="hours">Visszamenőleg hány óra.</param>
    /// <param name="includeWarnings">Figyelmeztetések is kellenek-e.</param>
    public static ScanResult Scan(
        IEnumerable<string> logNames,
        int hours,
        bool includeWarnings,
        int maxRecordsPerLog,
        IProgress<string>? progress,
        CancellationToken token)
        => Scan(WmiContext.Local, logNames, hours, includeWarnings, maxRecordsPerLog, progress, token);

    /// <summary>Ugyanaz, de tetszoleges (akar tavoli) gepre.</summary>
    public static ScanResult Scan(
        WmiContext ctx,
        IEnumerable<string> logNames,
        int hours,
        bool includeWarnings,
        int maxRecordsPerLog,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new ScanResult();
        var map = new Dictionary<string, ProblemGroup>(StringComparer.OrdinalIgnoreCase);

        long ms = (long)hours * 3600_000;
        var levels = includeWarnings ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2";

        // Tavoli gepnel kulon munkamenet kell (Remote Event Log Management tuzfalszabaly).
        EventLogSession? session = null;
        if (ctx.IsRemote)
        {
            try { session = CreateSession(ctx); }
            catch (Exception ex)
            {
                result.Skipped.Add($"Nem sikerult csatlakozni a(z) {ctx.Host} naplojahoz: {ex.Message}");
                result.Duration = sw.Elapsed;
                return result;
            }
        }

        foreach (var log in logNames)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"{log} napló olvasása…");

            // A Security naplóban a hibák nem "Level" alapján jelöltek, hanem audit-failure kulcsszóval.
            var xpath = log.Equals("Security", StringComparison.OrdinalIgnoreCase)
                ? $"*[System[band(Keywords,4503599627370496) and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
                : $"*[System[({levels}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";

            EventLogReader? reader = null;
            try
            {
                var q = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
                if (session is not null) q.Session = session;
                reader = new EventLogReader(q);
            }
            catch (UnauthorizedAccessException)
            {
                result.Skipped.Add($"{log}: nincs jogosultság (indítsd rendszergazdaként)");
                continue;
            }
            catch (Exception ex)
            {
                result.Skipped.Add($"{log}: {ex.Message}");
                continue;
            }

            int read = 0;
            try
            {
                for (EventRecord? rec = reader.ReadEvent(); rec is not null; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++read > maxRecordsPerLog) break;

                        var provider = rec.ProviderName ?? "ismeretlen";
                        var id = rec.Id;
                        var level = rec.Level ?? 4;
                        var time = rec.TimeCreated ?? DateTime.Now;

                        // A Security napló audit-failure eseményei Információ szintűek -> hibaként kezeljük.
                        if (log.Equals("Security", StringComparison.OrdinalIgnoreCase) && level > 3)
                            level = 2;

                        result.TotalEvents++;
                        switch (level)
                        {
                            case 1: result.Critical++; break;
                            case 2: result.Errors++; break;
                            case 3: result.Warnings++; break;
                        }

                        var subject = ExtractSubject(rec, provider);
                        var key = $"{log}|{provider}|{id}|{subject}";

                        if (!map.TryGetValue(key, out var g))
                        {
                            g = new ProblemGroup
                            {
                                LogName = log,
                                Provider = provider,
                                EventId = id,
                                Subject = subject,
                                Level = level,
                                // A teljes szöveg előállítása drága: csak a csoport első példányánál kérjük le.
                                SampleMessage = SafeMessage(rec),
                                Diag = KnowledgeBase.Lookup(provider, id, subject)
                            };
                            map[key] = g;
                            result.Groups.Add(g);
                        }

                        g.Count++;
                        if (time < g.First) g.First = time;
                        if (time > g.Last) g.Last = time;
                        if (level < g.Level) g.Level = level;   // a súlyosabb szint nyer
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Skipped.Add($"{log}: olvasás megszakadt – {ex.Message}");
            }
            finally
            {
                reader.Dispose();
            }
        }

        result.Groups.Sort((a, b) =>
        {
            var c = b.Rank.CompareTo(a.Rank);
            return c != 0 ? c : b.Last.CompareTo(a.Last);
        });

        try { session?.Dispose(); } catch { }

        result.Duration = sw.Elapsed;
        return result;
    }

    private static EventLogSession CreateSession(WmiContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.User))
            return new EventLogSession(ctx.Host);

        // A "TARTOMANY\\felhasznalo" es a sima felhasznalonev is mukodjon.
        string domain = "", user = ctx.User;
        var slash = user.IndexOf('\\');
        if (slash > 0) { domain = user[..slash]; user = user[(slash + 1)..]; }

        var pwd = new System.Security.SecureString();
        foreach (var c in ctx.Password) pwd.AppendChar(c);
        pwd.MakeReadOnly();

        return new EventLogSession(ctx.Host, domain, user, pwd, SessionAuthentication.Default);
    }

    private static string SafeMessage(EventRecord rec)
    {
        try
        {
            var m = rec.FormatDescription();
            if (!string.IsNullOrWhiteSpace(m)) return m.Trim();
        }
        catch { }

        try
        {
            var props = rec.Properties.Select(p => p.Value?.ToString() ?? "").Where(s => s.Length > 0);
            var joined = string.Join(" · ", props);
            if (joined.Length > 0)
                return "(a szolgáltató szövegsablonja nem elérhető, csak a nyers adatok)\n" + joined;
        }
        catch { }

        return "(a leírás nem elérhető)";
    }

    /// <summary>Az érintett alkalmazás / szolgáltatás / eszköz neve, ha kinyerhető.</summary>
    private static string ExtractSubject(EventRecord rec, string provider)
    {
        try
        {
            var p = provider;
            bool wanted =
                p.Contains("Application Error", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("Application Hang", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("Service Control Manager", StringComparison.OrdinalIgnoreCase) ||
                p.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("Windows Error Reporting", StringComparison.OrdinalIgnoreCase);

            if (!wanted) return "";

            var props = rec.Properties;
            if (props.Count == 0) return "";
            var v = props[0].Value?.ToString()?.Trim() ?? "";
            return v.Length > 64 ? v[..64] : v;
        }
        catch { return ""; }
    }
}

// =====================================================================================

/// <summary>
/// Ismert Windows eseményekhez tartozó magyar nyelvű magyarázatok.
/// A cél, hogy ne kelljen eseményazonosítót guglizni.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KnowledgeBase
{
    private sealed record Rule(int Id, string ProviderContains, Diagnosis Diag);

    private static readonly List<Rule> Rules = new()
    {
        // ---------------------------------------------------------- tápellátás / leállás
        new(41, "Kernel-Power", new Diagnosis(
            "A gép nem szabályosan állt le",
            "A Windows nem kapott értesítést a leállásról: a gép áramtalanítva lett, lefagyott, " +
            "vagy kék halállal újraindult. Ez az esemény mindig utólag, a következő induláskor keletkezik.",
            "Ha rendszeresen előfordul: ellenőrizd a tápegységet és a hálózati áramot (UPS), " +
            "a CPU/GPU hőfokot, és nézd meg, van-e mellette BugCheck (1001) vagy WHEA bejegyzés.",
            Severity.Kritikus)),

        new(6008, "EventLog", new Diagnosis(
            "Váratlan leállás",
            "Az előző leállítás nem volt szabályos. Ugyanannak a jelenségnek a másik bejegyzése, " +
            "mint a Kernel-Power 41.",
            "Nézd meg a leállás időpontja körüli hibákat – azok mutatják meg az okot.",
            Severity.Komoly)),

        new(1001, "WER-SystemErrorReporting", new Diagnosis(
            "Kék halál (BugCheck) történt",
            "A rendszer leállt egy végzetes hibával. Az üzenetben szereplő kód (pl. 0x0000007E) " +
            "és a hozzá tartozó paraméterek azonosítják a hibát okozó modult.",
            "A dump fájl (C:\\Windows\\MEMORY.DMP vagy a Minidump mappa) megmutatja a hibás " +
            "illesztőprogramot. Gyakori ok: elavult driver, hibás RAM, túlmelegedés.",
            Severity.Kritikus)),

        new(2004, "Resource-Exhaustion-Detector", new Diagnosis(
            "Elfogyott a memória",
            "A Windows észlelte, hogy a virtuális memória kifogyóban van, és megnevezi a legtöbbet " +
            "fogyasztó folyamatokat.",
            "Nézd meg az üzenetben felsorolt programokat. Megoldás: több RAM, nagyobb lapozófájl, " +
            "vagy a memóriaszivárgó alkalmazás frissítése.",
            Severity.Komoly)),

        // ---------------------------------------------------------- tároló
        new(7, "Disk", new Diagnosis(
            "Hibás szektor a lemezen",
            "A meghajtó egy adatblokkot nem tudott hibátlanul kiolvasni. Ez fizikai lemezhiba jele, " +
            "és általában romlik tovább.",
            "AZONNAL ments biztonsági másolatot. Ellenőrizd a SMART állapotot, és tervezd " +
            "a meghajtó cseréjét.",
            Severity.Kritikus)),

        new(11, "Disk", new Diagnosis(
            "A vezérlő hibát észlelt a lemezen",
            "Kommunikációs hiba a meghajtó és a vezérlő között. Leggyakoribb okai: meglazult vagy " +
            "hibás SATA/tápkábel, haldokló meghajtó, ritkábban vezérlő- vagy driverhiba.",
            "Első lépés a kábelek ellenőrzése (vagy USB-s háznál a port csere). Ha marad, " +
            "SMART-ellenőrzés és mentés.",
            Severity.Kritikus)),

        new(51, "Disk", new Diagnosis(
            "Hiba lapozás közben",
            "A rendszer hibát észlelt a lemezen memórialapozás közben. Rendszerint ugyanaz a " +
            "hardveres gyökér-ok, mint a Disk 7 / 11 eseményeknél.",
            "Ellenőrizd a meghajtó állapotát (SMART) és a kábelezést, majd készíts mentést.",
            Severity.Komoly)),

        new(153, "Disk", new Diagnosis(
            "Az I/O művelet újrapróbálva",
            "A lemezművelet elsőre nem sikerült, de az újrapróbálás megoldotta. Önmagában még nem " +
            "adatvesztés, viszont figyelmeztető jel.",
            "Ha egyre gyakoribb, a meghajtó kopik. Érdemes SMART-ot nézni.",
            Severity.Figyelmeztetes)),

        new(129, "storahci", new Diagnosis(
            "A tároló nem válaszolt, újraindítás történt",
            "A vezérlő reset parancsot küldött, mert a meghajtó időben nem válaszolt. Okozhatja " +
            "energiagazdálkodási beállítás (LPM), firmware hiba vagy haldokló SSD.",
            "Frissítsd a meghajtó firmware-ét és a chipset drivert. Ha SSD-ről van szó és sűrűn " +
            "jön, kezdj mentéssel.",
            Severity.Komoly)),

        new(129, "stornvme", new Diagnosis(
            "Az NVMe meghajtó nem válaszolt időben",
            "A vezérlő újraindította az NVMe eszközt. Gyakori okok: túlmelegedés, firmware hiba, " +
            "vagy energiatakarékos állapot (ASPM) hibás kezelése.",
            "Nézd meg az SSD hőfokát a Hardver fülön, frissítsd a firmware-t, és próbáld " +
            "kikapcsolni a PCIe energiagazdálkodást.",
            Severity.Komoly)),

        new(55, "Ntfs", new Diagnosis(
            "Fájlrendszer-sérülés",
            "Az NTFS struktúra sérült a köteten. Ez adatvesztéshez vezethet.",
            "Futtasd: chkdsk C: /f  (újraindítást kér). Ha ismétlődik, a meghajtó a hibás.",
            Severity.Kritikus)),

        new(98, "Ntfs", new Diagnosis(
            "NTFS metaadat-hiba",
            "A fájlrendszer belső nyilvántartása sérült.",
            "chkdsk futtatása, majd a meghajtó állapotának ellenőrzése.",
            Severity.Komoly)),

        new(25, "volsnap", new Diagnosis(
            "Árnyékmásolatok törlődtek",
            "Nem volt elég hely, ezért a Windows eldobta a kötet árnyékmásolatait – ezzel a " +
            "rendszer-visszaállítási pontok és a korábbi fájlverziók is elvesztek.",
            "Szabadíts fel helyet, vagy növeld az árnyékmásolatnak fenntartott területet.",
            Severity.Figyelmeztetes)),

        // ---------------------------------------------------------- hardver (WHEA)
        new(1, "WHEA-Logger", new Diagnosis(
            "Processzor-szintű hardverhiba",
            "Machine Check kivétel: a CPU vagy a hozzá tartozó busz hibát jelzett. Ez valódi " +
            "hardverprobléma, nem szoftveres.",
            "Ellenőrizd a hűtést és a tápellátást. Ha tuningolva (OC/undervolt) van a gép, " +
            "állítsd vissza alapra. Gyakori ismétlődés esetén CPU/alaplap/RAM vizsgálat.",
            Severity.Kritikus)),

        new(17, "WHEA-Logger", new Diagnosis(
            "Javított hardverhiba (PCI Express)",
            "A rendszer hibát észlelt egy PCIe eszközön, de ki tudta javítani. Egy-két darab " +
            "elszórtan normális lehet; a tömeges előfordulás nem az.",
            "Ha sok van belőle: nézd meg, melyik eszköz (videokártya, NVMe, bővítőkártya), " +
            "ültesd át másik foglalatba, frissíts BIOS-t.",
            Severity.Figyelmeztetes)),

        new(18, "WHEA-Logger", new Diagnosis(
            "Végzetes hardverhiba",
            "Nem javítható hardverhiba, ami rendszerint azonnali összeomlást okoz.",
            "Komoly jel: memória- és tápellátás-teszt, hőfokok ellenőrzése, tuning visszaállítása.",
            Severity.Kritikus)),

        new(19, "WHEA-Logger", new Diagnosis(
            "Javított hardverhiba (memória vagy gyorsítótár)",
            "A hibajavító logika elkapta a hibát, de a hardver jelezte. Ismétlődés esetén " +
            "romló memóriamodulra utalhat.",
            "Futtass memóriatesztet (Windows Memóriadiagnosztika vagy MemTest86).",
            Severity.Komoly)),

        new(37, "Kernel-Processor-Power", new Diagnosis(
            "A processzor teljesítménye korlátozva lett",
            "A firmware visszavette a CPU órajelét. Leggyakrabban túlmelegedés vagy elégtelen " +
            "tápellátás miatt (laptopnál gyenge töltő is okozhatja).",
            "Nézd meg a CPU hőfokot a Hardver fülön. Portalanítás, hűtőpaszta-csere, " +
            "vagy megfelelő teljesítményű töltő.",
            Severity.Figyelmeztetes)),

        // ---------------------------------------------------------- szolgáltatások
        new(7000, "Service Control Manager", new Diagnosis(
            "Egy szolgáltatás nem indult el",
            "A megnevezett szolgáltatás indítása sikertelen volt.",
            "services.msc-ben nézd meg a szolgáltatás bejelentkezési fiókját és a függőségeit. " +
            "Az üzenet végén szereplő hibakód mondja meg a pontos okot.",
            Severity.Komoly)),

        new(7001, "Service Control Manager", new Diagnosis(
            "Szolgáltatás függőségi hiba",
            "A szolgáltatás azért nem indult, mert egy másik, tőle függő szolgáltatás sem indult el.",
            "Előbb azt a szolgáltatást javítsd, amelyikre hivatkozik.",
            Severity.Figyelmeztetes)),

        new(7009, "Service Control Manager", new Diagnosis(
            "Időtúllépés a szolgáltatás indításakor",
            "A szolgáltatás nem jelentkezett be a megadott időn belül (alapból 30 mp). Lassú " +
            "lemezen vagy erősen terhelt gépen gyakori.",
            "Ha csak induláskor jön és minden működik, általában ártalmatlan. Tartós esetben " +
            "SSD-re váltás vagy a szolgáltatás késleltetett indítása segít.",
            Severity.Figyelmeztetes)),

        new(7011, "Service Control Manager", new Diagnosis(
            "A szolgáltatás nem válaszolt időben",
            "Tranzakciós időtúllépés a szolgáltatás felé. Gyakran lassú I/O vagy beragadt " +
            "szolgáltatás jele.",
            "Nézd meg, melyik szolgáltatásról van szó, és hogy nem terheli-e valami a lemezt.",
            Severity.Figyelmeztetes)),

        new(7023, "Service Control Manager", new Diagnosis(
            "Szolgáltatás hibával leállt",
            "A szolgáltatás futás közben hibakóddal kilépett.",
            "Az üzenetben szereplő hibakód a kulcs. Ellenőrizd a szolgáltatás naplóját is.",
            Severity.Komoly)),

        new(7031, "Service Control Manager", new Diagnosis(
            "Szolgáltatás váratlanul összeomlott",
            "A szolgáltatás nem szabályosan állt le, a Windows helyreállítási műveletet indított " +
            "(általában újraindítja).",
            "Ha ismétlődik, a szolgáltatáshoz tartozó programot kell frissíteni vagy " +
            "újratelepíteni. Az Alkalmazás naplóban rendszerint van mellé egy 1000-es hiba.",
            Severity.Komoly)),

        new(7034, "Service Control Manager", new Diagnosis(
            "Szolgáltatás összeomlott, nincs helyreállítás",
            "A szolgáltatás váratlanul leállt, és a Windows nem indítja újra automatikusan.",
            "Az érintett funkció ettől kezdve nem működik. Nézd meg a hozzá tartozó alkalmazáshibát.",
            Severity.Komoly)),

        new(7026, "Service Control Manager", new Diagnosis(
            "Rendszerindító illesztőprogram nem töltődött be",
            "Egy boot-start driver nem indult el. Sokszor eltávolított hardver maradéka, " +
            "ilyenkor ártalmatlan.",
            "Ha az érintett eszköz már nincs a gépben, figyelmen kívül hagyható.",
            Severity.Figyelmeztetes)),

        // ---------------------------------------------------------- alkalmazások
        new(1000, "Application Error", new Diagnosis(
            "Alkalmazás összeomlott",
            "A program váratlanul leállt. Az üzenet megnevezi a hibás modult is – ez a " +
            "legfontosabb adat: ha nem a program saját DLL-je, hanem pl. egy grafikus driver, " +
            "akkor máshol a baj.",
            "Frissítsd a programot, majd a hibás modulhoz tartozó összetevőt (driver, .NET, " +
            "Visual C++ runtime). Ismétlődés esetén újratelepítés.",
            Severity.Komoly)),

        new(1002, "Application Hang", new Diagnosis(
            "Alkalmazás lefagyott",
            "A program nem válaszolt, a Windows kilőtte. Gyakran nem a program hibája, hanem " +
            "lassú lemez, hálózati megosztás vagy erőforráshiány.",
            "Nézd meg, mikor történt, és mi terhelte a gépet akkor. Hálózati meghajtóra dolgozó " +
            "programoknál a kapcsolat szakadása tipikus ok.",
            Severity.Figyelmeztetes)),

        new(1026, ".NET Runtime", new Diagnosis(
            "Kezeletlen .NET kivétel",
            "Egy .NET alkalmazás összeomlott. Az üzenet tartalmazza a kivétel típusát és a " +
            "hívási vermet – ebből általában pontosan látszik a hiba helye.",
            "Fejlesztőnek: a stack trace az érdekes. Felhasználóként a program frissítése " +
            "vagy a megfelelő .NET verzió telepítése segít.",
            Severity.Komoly)),

        // ---------------------------------------------------------- hálózat
        new(1014, "DNS Client Events", new Diagnosis(
            "Névfeloldási időtúllépés",
            "A DNS-kiszolgáló nem válaszolt időben. Szórványosan normális, tömegesen hálózati gond.",
            "Ellenőrizd a beállított DNS-kiszolgálót. Teszt: nslookup, illetve váltás " +
            "megbízható DNS-re.",
            Severity.Figyelmeztetes)),

        new(1003, "Dhcp-Client", new Diagnosis(
            "Nem kapott IP-címet",
            "A gép nem kapott választ a DHCP-kiszolgálótól, ezért nincs érvényes IP-címe.",
            "Ellenőrizd a kábelt/Wi-Fi kapcsolatot és a routert. Ideiglenes megoldás: " +
            "ipconfig /renew",
            Severity.Komoly)),

        new(36887, "Schannel", new Diagnosis(
            "TLS kapcsolati hiba",
            "Titkosított kapcsolat felépítése hiúsult meg. Nagyon gyakori zaj: elavult " +
            "tanúsítvány, régi TLS verzió, vagy egyszerűen egy megszakadt böngésző-kapcsolat.",
            "Önmagában ritkán jelent valódi problémát. Csak akkor foglalkozz vele, ha egy " +
            "konkrét alkalmazás nem tud csatlakozni.",
            Severity.Zaj)),

        // ---------------------------------------------------------- ismert zaj
        new(10016, "DistributedCOM", new Diagnosis(
            "DCOM engedélyhiba – jellemzően ártalmatlan",
            "Egy komponens olyan DCOM objektumhoz próbált hozzáférni, amire nincs joga. " +
            "A Microsoft álláspontja szerint ez a bejegyzés a legtöbb esetben figyelmen kívül " +
            "hagyható, a rendszer működését nem befolyásolja.",
            "Ne piszkáld a registry jogosultságokat miatta. Csak akkor érdemes vele foglalkozni, " +
            "ha egy konkrét funkció is hibás mellette.",
            Severity.Zaj)),

        new(10005, "DistributedCOM", new Diagnosis(
            "DCOM szolgáltatásindítási hiba",
            "A DCOM nem tudott elindítani egy szolgáltatást. Sokszor egy másik, valódi hiba " +
            "következménye.",
            "Nézd meg, hogy a hozzá tartozó szolgáltatásnál van-e 7000/7031 esemény.",
            Severity.Figyelmeztetes)),

        new(455, "ESENT", new Diagnosis(
            "ESENT adatbázis figyelmeztetés – rendszerint zaj",
            "A Windows belső adatbázismotorja jelez. A legtöbb ilyen bejegyzés " +
            "(pl. a keresési index vagy a Windows Update ideiglenes fájljai miatt) ártalmatlan.",
            "Csak akkor foglalkozz vele, ha a hozzá tartozó funkció (pl. keresés) tényleg hibás.",
            Severity.Zaj)),

        // ---------------------------------------------------------- frissítés, teljesítmény
        new(20, "WindowsUpdateClient", new Diagnosis(
            "Windows Update telepítési hiba",
            "Egy frissítés telepítése nem sikerült. Az üzenetben szereplő hibakód mondja meg, miért.",
            "Futtasd a Windows Update hibaelhárítót, vagy töltsd le a frissítést kézzel a " +
            "Microsoft Update katalógusból.",
            Severity.Figyelmeztetes)),

        new(100, "Diagnostics-Performance", new Diagnosis(
            "Lassú rendszerindítás",
            "A Windows a szokásosnál lassabbnak mérte az indulást, és megnevezi, mi húzta el.",
            "Az üzenetben szereplő szolgáltatás/alkalmazás az elkövető. Automatikus indítású " +
            "programok szűkítése segít.",
            Severity.Figyelmeztetes)),

        new(6006, "Winlogon", new Diagnosis(
            "Lassú bejelentkezés",
            "Egy bejelentkezési értesítéskezelő sokáig futott, ez lassítja a belépést.",
            "Vállalati gépnél gyakran csoportházirend vagy hálózati meghajtó okozza.",
            Severity.Figyelmeztetes)),

        // ---------------------------------------------------------- biztonság
        new(4625, "Security-Auditing", new Diagnosis(
            "Sikertelen bejelentkezés",
            "Hibás jelszóval vagy nem létező fiókkal próbáltak belépni. Néhány darab általában " +
            "elgépelés; a sok, gyors ismétlődés viszont jelszótörési kísérletre utal.",
            "Ha kívülről elérhető RDP van a gépen és tömeges: azonnal korlátozd a hozzáférést " +
            "(tűzfal, VPN, fiókzárolási házirend).",
            Severity.Figyelmeztetes)),

        new(4740, "Security-Auditing", new Diagnosis(
            "Fiók zárolva lett",
            "Túl sok sikertelen próbálkozás miatt a rendszer zárolta a felhasználói fiókot.",
            "Gyakori ártatlan ok: egy régi jelszót tároló eszköz vagy leképezett meghajtó " +
            "próbálkozik a háttérben.",
            Severity.Komoly)),

        new(219, "Kernel-PnP", new Diagnosis(
            "Illesztőprogram betöltési hiba",
            "Egy eszköz illesztőprogramja nem töltődött be megfelelően.",
            "Eszközkezelőben keresd a sárga felkiáltójelet, és telepítsd újra az adott drivert.",
            Severity.Figyelmeztetes)),
    };

    public static Diagnosis? Lookup(string provider, int eventId, string subject)
    {
        foreach (var r in Rules)
        {
            if (r.Id != eventId) continue;
            if (provider.Contains(r.ProviderContains, StringComparison.OrdinalIgnoreCase))
                return r.Diag;
        }
        return null;
    }

    public static int KnownEventCount => Rules.Count;
}
