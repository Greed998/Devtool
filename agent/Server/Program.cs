using System.IO.Compression;
using System.Net;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;

// ============================================================================
//  DevToolbox Fleet Server
//
//  KET kulon HTTP-listener:
//    - AGENT port (alap 8443): ide jelentkeznek az agentek (JSON API, alairt kerdesek)
//    - WEB port   (alap 8080): a szuperadmin bongeszos felulete
//
//  Igy a webfelulet kulon tuzfalszaballyal korlatozhato, fuggetlenul az agent-forgalomtol.
//
//  Parancsok:
//    FleetServer.exe install          -> Windows-szolgaltataskent telepiti magat
//    FleetServer.exe uninstall
//    FleetServer.exe                  -> eloterben futtatas (a szolgaltatas is ezt hivja)
//    FleetServer.exe token "cimke"    -> token a parancssorbol (a web is tud)
// ============================================================================

// A belepesi pont a kozos Fleet exe-ben van (agent/Fleet/FleetMain.cs); ez a telephelyi szerver szerepe.
static class ServerEntry
{
    public static void Run(string[] args)
    {
        // A naplók / mentések helye a beállításból (1.5.4) - a parancssori módok is oda írnak; költöztetni csak a szolgáltatás indulása fog.
        Storage.PointOnly(ServerPaths.DataDir);
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "install":   ServerInstaller.Install();   return;
                case "uninstall": ServerInstaller.Uninstall(); return;
                case "reinstall": ServerInstaller.Reinstall(args.Contains("--clean", StringComparer.OrdinalIgnoreCase)); return;
                case "status":    ServerInstaller.Status();    return;
                case "diag":      ServerDiag.RunAsync().GetAwaiter().GetResult(); return;
                case "token":     ServerHost.TokenFromCli(args);          return;
                case "fingerprint":
                    Console.WriteLine(ServerHost.OpenForCli().ServerKeys.Fingerprint); return;
                case "run":       ServerHost.RunServer();      return;
                // A feltoltott uj exe (FleetServer.new.exe) ebben a modban cserel - lasd ServiceSelfUpdate.
                case "apply-update": Environment.ExitCode = ServiceSelfUpdate.Apply(args, SrvLog.Info); return;
            }
        }

        // Szolgaltataskent futtatva nincs interaktiv konzol -> ServiceBase.
        #if GUI_SETUP
        if ((args.Length == 0 && Environment.UserInteractive) || (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase)))
        {
            DevToolbox.Fleet.WizardForm.FreeConsole();
            DevToolbox.Fleet.ServiceWizard.OnSta(() =>
            {
                var cfg0 = ServerConfig.Load(ServerPaths.DataDir);
                DevToolbox.Fleet.ServiceWizard.Run("Fleet Server", ServerBuild.Version, ServerInstaller.ServiceName,
                    "A Fleet Server a telephely gépeit kezeli: az agentek ide jelentkeznek a belső hálón, és innen kapják az aláírt feladatokat.",
                    new[] { new DevToolbox.Fleet.ServiceWizard.PortField("Agent-port (a gépek ide jelentkeznek)", cfg0.AgentPort, "Alapból 8443. Az agentek telepítőjébe ez kerül."),
                            new DevToolbox.Fleet.ServiceWizard.PortField("Web-port (a webes felület)", cfg0.WebPort, "Alapból 8080. Böngészőből: http://<szerver>:<port>") },
                    v => { var c = ServerConfig.Load(ServerPaths.DataDir); c.AgentPort = v[0]; c.WebPort = v[1]; c.Save(ServerPaths.DataDir); },
                    ServerInstaller.Install,
                    v => $"http://localhost:{v[1]}/web");
            });
            return;
        }
        #endif
        if (!Environment.UserInteractive)
        {
            System.ServiceProcess.ServiceBase.Run(new FleetServerService());
            return;
        }

        ServerHost.RunServer();
    }
}

// ============================================================================


// ============================================================================
//  A szerver teljes logikaja egy osztalyban, hogy a szolgaltatas is hivhassa.
// ============================================================================

static class ServerHost
{
    public static HttpListener[]? RunningListeners;
    // A futó figyelők: portváltáskor ezekhez adunk / veszünk el előtagot (újraindítás nélkül).
    static HttpListener? _agentListener, _webListener;
    static readonly object PortLock = new();
    /// <summary>Melyik agent melyik porton jelentkezett utoljára (a portváltás végének megítéléséhez; csak memóriában).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> AgentPortSeen = new();

    public static MetricStore Metrics = null!;
    public static AlertStore Alerts = null!;
    public static WingetCatalog Winget = null!;

static string DataDir => ServerPaths.DataDir;

/// <summary>
/// Parancssori hasznalat: ha mar van szerverkulcs (a szerver futott), kliens modban
/// nyitunk, hogy a futo szolgaltatas allapotat ne irjuk felul.
/// </summary>
public static FleetStore OpenForCli() =>
    new FleetStore(DataDir, owner: !File.Exists(Path.Combine(DataDir, "server-public.pem")));

public static void TokenFromCli(string[] args)
{
    // Ha a szolgaltatas mar fut, NEM irhatjuk a fleet.json-t - a token postaladaba megy.
    var store = OpenForCli();
    var token = store.CreateEnrollmentToken(args.Length > 1 ? args[1] : "névtelen");
    Console.WriteLine("\n  Token:       " + token);
    Console.WriteLine("  Ujjlenyomat: " + store.ServerKeys.Fingerprint + "\n");
}

public static void RunServer()
{
    var dir = DataDir;
    // 1.5.4: a naplók és a mentések helye a beállításból - a legelső napló ELŐTT, hogy minden oda menjen; a régi,
    // az adatmappa gyökerében levő naplók ilyenkor átköltöznek (logs\). Mappajog-szűkítés csak emelt jogú folyamatban.
    var startCfg = ServerConfig.Load(dir);
    var storageNotes = Storage.ApplyAtStartup(dir, startCfg, secure: Environment.IsPrivilegedProcess);
    SrvLog.Info("─────────────────────────────────────────────");
    SrvLog.Info($"Fleet Server indul. Adatmappa: {dir} · naplók: {LogFolder.Dir(dir)}");

    ServerConfig config;
    FleetStore store;
    try
    {
        // Barmelyik frissitesi uton: verziovaltaskor mentes a kulcsokrol es adatokrol.
        DataBackup.OnStartup(dir, ServerBuild.Version);
        store = new FleetStore(dir);
        Metrics = new MetricStore(Storage.MetricsDirAtStartup(dir, startCfg, storageNotes));
        foreach (var n in storageNotes) SrvLog.Warn(n);
        HubClient.Metrics = Metrics;   // a Kozpontba innen mennek fel az 5 perces atlagok
        HubClient.AgentPackageVersion = () => AgentPackage.Current()?.Version ?? "";
        HubClient.ScoreOf = SiteScoreOf;
        Alerts = new AlertStore(dir);
        HubClient.Alerts = Alerts;     // a Központ küszöb-szabályai ide kerülnek
        Winget = new WingetCatalog(dir);
        TrayApi.Winget = Winget;   // 1.5.7: a tálca „Program kérése” keresője
        EmbeddedAgent.Log = SrvLog.Info;
        config = ServerConfig.Load(dir);
        Alerts.SetHubRules(config.HubAlertRules.Select(HubClient.ToRule));   // a Központ legutóbb kapott szabályai
        config.EnsureSessionKey();
        LogLimits.SetMb(config.LogMaxMb);
        config.Save(dir);
    }
    catch (Exception ex)
    {
        SrvLog.Error("Az adatmappa nem írható/olvasható: " + ex.Message);
        SrvLog.Error($"Ellenőrizd, hogy a szolgáltatás fiókja hozzáfér-e ehhez: {dir}");
        return;
    }

    var users = new UserStore(dir);
    var sessions = new RoleSession(config.SessionKey);
    TrayApi.Users = users;   // 1.5.7: „Belépés adminként” a tálcán (kihívás–válasz a felhasználó PBKDF2-kulcsával)
    TrayApi.SessionKey = config.SessionKey;
    var agentPort = config.AgentPort;
    var webPort = config.WebPort;
    // Portváltás alatt a régi agent-port is figyel, amíg az agentek át nem állnak.
    var oldPort = config.OldAgentPort > 0 && config.OldAgentPort != agentPort && config.OldAgentPort != webPort ? config.OldAgentPort : 0;

    // A leggyakoribb „nem indul el” ok: a port foglalt. Ezt ELŐRE ellenőrizzük,
    // és megmondjuk, MELYIK program tartja – nem csak annyit, hogy „hiba”.
    foreach (var (port, nev) in new[] { (agentPort, "agent-port"), (webPort, "web-port"), (oldPort, "régi agent-port") }.Where(x => x.Item1 > 0))
    {
        if (PortTools.IsInUse(port))
        {
            var ki = PortTools.Owner(port);
            SrvLog.Error($"A(z) {port} {nev} már foglalt" + (ki is null ? "." : $" – ezt használja: {ki}"));
            SrvLog.Error("Megoldás: állíts be másik portot a webes beállításban, vagy állítsd le a másik programot.");
            SrvLog.Error("A portok a server-config.json fájlban is átírhatók (agentPort / webPort).");
            return;
        }
    }

    var agentListener = new HttpListener();
    agentListener.Prefixes.Add($"http://+:{agentPort}/");
    if (oldPort > 0) agentListener.Prefixes.Add($"http://+:{oldPort}/");
    var webListener = new HttpListener();
    webListener.Prefixes.Add($"http://+:{webPort}/");
    _agentListener = agentListener; _webListener = webListener;

    // A tuzfalat minden indulaskor az aktualis portokhoz igazitjuk.
    if (OperatingSystem.IsWindows()) ServerInstaller.EnsureFirewall(oldPort > 0 ? new[] { agentPort, oldPort } : new[] { agentPort }, webPort);

    try { agentListener.Start(); webListener.Start(); }
    catch (HttpListenerException ex)
    {
        SrvLog.Error($"Nem sikerült a portokon figyelni: {ex.Message} (kód: {ex.ErrorCode})");
        if (ex.ErrorCode == 5)   // Access denied
        {
            SrvLog.Error("HOZZÁFÉRÉS MEGTAGADVA. A szerver nem rendszergazdaként fut, vagy hiányzik az URL-foglalás.");
            SrvLog.Error("Futtasd rendszergazdaként, vagy add hozzá az URL-foglalást:");
            SrvLog.Error($"   netsh http add urlacl url=http://+:{agentPort}/ user=\"NT AUTHORITY\\SYSTEM\"");
            SrvLog.Error($"   netsh http add urlacl url=http://+:{webPort}/ user=\"NT AUTHORITY\\SYSTEM\"");
        }
        return;
    }

    SrvLog.Ok("A szerver figyel.");
    SrvLog.Info($"Agent-port (API):    {agentPort}" + (oldPort > 0 ? $"  (portváltás: a régi {oldPort} is figyel)" : ""));
    SrvLog.Info($"Web-port (admin):    {webPort}  ->  http://localhost:{webPort}");
    SrvLog.Info($"Szerver ujjlenyomat: {store.ServerKeys.Fingerprint}");
    SrvLog.Info($"Setup kész:          {(config.SetupDone ? "igen" : "NEM - nyisd meg a webfelületet a beállításhoz")}");

    var cts = new CancellationTokenSource();
    if (Environment.UserInteractive)
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); agentListener.Stop(); webListener.Stop(); };

    RunningListeners = new[] { agentListener, webListener };

    _config = config;
    // 1.5.5: internetkapcsolat-figyelés (ha be van kapcsolva) és SNMP-trap fogadás (ha be van kapcsolva; alapból ki).
    NetWatch.Load(dir);
    _ = Task.Run(() => NetWatch.Loop(() => config, Alerts, AlertLog.Write, cts.Token));
    SnmpReceiver.Init(dir, AlertLog.Write);
    UsbLog.Init(dir);
    CveCounts.Init(dir);   // a Központtól legutóbb kapott CVE-találatok (újraindítás után is)
    ProgramRequests.Init(dir);   // 1.5.7: a tálca programkérései (jóváhagyási sor)
    LogShip.Init(dir);           // 1.5.7: a naplók felküldése a Központba (meddig ment fel)
    if (SnmpReceiver.Apply(config) is { } snmpErr) SrvLog.Warn("SNMP: " + snmpErr);
    else if (config.SnmpEnabled) SrvLog.Info("SNMP-trap fogadás: " + SnmpReceiver.Status);
    if (config.SnmpEnabled) config.Save(dir);   // az engineID / indulásszámláló megmarad
    // Kozpont betarcsazas (ha be van allitva). Kulon szalon, a szerver mukodeset nem akasztja.
    // 1.5.9: a WireGuard-alagút a Központ legutóbbi ajánlata szerint (a betárcsázás előtt; a HubClient frissíti).
    if (config.WgOffer is not null) _ = Task.Run(() => { if (SiteWg.Apply(config, dir) is { } e) SrvLog.Warn("WireGuard: " + e); });
    _ = Task.Run(() => HubClient.Instance.RunLoop(store, config, dir, cts.Token));
    // Kiesés-figyelés percenként - a jelentkezésektől függetlenül (épp az a hír, ha egy gép NEM jelentkezik).
    // Ugyanitt: a portváltás vége (minden agent átállt, vagy lejárt a türelmi idő).
    _ = Task.Run(() => OfflineLoop(store, cts.Token));
    // A teljes winget-tár naponta (a programtárhoz; az újonnan megjelent programok döntésre várnak).
    _ = Task.Run(() => Winget.RunLoop(() => config, c => c.Save(dir), SrvLog.Info, cts.Token));
    // A szerver kulcsának automatikus cseréje (alapból hetente).
    _ = Task.Run(() => ServerKeyLoop(store, cts.Token));

    var agentLoop = AcceptLoop(agentListener, ctx => HandleAgent(ctx, store), cts.Token);
    var webLoop = AcceptLoop(webListener, ctx => HandleWeb(ctx, store, config, users, sessions, dir), cts.Token);

    try { Task.WaitAll(new[] { agentLoop, webLoop }); }
    catch (Exception ex) { SrvLog.Error("A kiszolgáló leállt: " + ex.Message); }
    SrvLog.Info("Leállt.");
}

// ---- eszköz-összegzés (1.5.6): a gép adataiból és legutóbbi méréseiből, gépenként 60 mp-ig gyorsítótárazva
static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, DeviceScore.Result Value)> _scoreCache = new();

static object ScoreOf(EnrolledAgent a)
{
    var r = ScoreResult(a);
    return new
    {
        score = r.Score, security = r.Security, health = r.Health, grade = r.Grade,
        items = r.Items.Select(x => new { area = x.Area, text = x.Text, level = x.Level, penalty = x.Penalty })
    };
}

/// <summary>A Központnak (SiteAgent.Score): ugyanaz az összegzés, mint a szerver felületén.</summary>
public static SiteScore SiteScoreOf(EnrolledAgent a)
{
    var r = ScoreResult(a);
    return new SiteScore
    {
        Score = r.Score, Security = r.Security, Health = r.Health, Grade = r.Grade,
        Items = r.Items.Select(x => new SiteScoreItem { Area = x.Area, Text = x.Text, Level = x.Level, Penalty = x.Penalty }).ToList()
    };
}

static DeviceScore.Result ScoreResult(EnrolledAgent a)
{
    if (_scoreCache.TryGetValue(a.AgentId, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60)) return c.Value;
    var st = a.LastStatus;
    var latest = new Dictionary<string, double>();
    try { foreach (var kv in Metrics.Latest(a.AgentId)) latest[kv.Key] = kv.Value.Value; } catch { }
    var wu = st?.WindowsUpdates ?? new();
    var imp = wu.Count(w => w.Severity is "Critical" or "Important" or "Kritikus" or "Fontos");
    var pkg = AgentPackage.Current()?.Version;
    var outdated = Version.TryParse(a.AgentVersion, out var av) && Version.TryParse(pkg, out var pv) && av < pv;
    var alerts = Alerts.ActiveStates().Count(s => s.AgentId == a.AgentId || s.AgentId.StartsWith(a.AgentId + "|", StringComparison.Ordinal));
    var r = DeviceScore.Compute(new DeviceScore.Input(a.IsOnline, a.LastSeenUtc, st?.UpdatesAvailable ?? 0, imp, wu.Count - imp,
        st?.PendingReboot ?? false, outdated, alerts, latest, CveCounts.Critical(a.AgentId), CveCounts.High(a.AgentId), CveCounts.Exploited(a.AgentId)));
    _scoreCache[a.AgentId] = (DateTime.UtcNow, r);
    return r;
}

static async Task OfflineLoop(FleetStore store, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), token); } catch { break; }
        try { CheckOffline(store); } catch { }
        try { CheckPortTransition(store); } catch (Exception ex) { SrvLog.Warn("Portváltás: " + ex.Message); }
    }
}

// ================================================================ portváltás (kettős port)

/// <summary>A gépek szolgáltatáslistáinak összesítése: név, megjelenített név, hány gépen van, hány gépen fut.</summary>
public static List<SiteService> ServiceCatalog(IEnumerable<List<ServiceEntry>> lists)
{
    var d = new Dictionary<string, SiteService>(StringComparer.OrdinalIgnoreCase);
    foreach (var l in lists)
        foreach (var s in l)
        {
            if (!d.TryGetValue(s.Name, out var x)) d[s.Name] = x = new SiteService { Name = s.Name, DisplayName = s.DisplayName };
            x.Machines++;
            if (s.Running) x.Running++;
        }
    return d.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>A futó szerver konfigurációja (a webes kérések is ezt a példányt módosítják).</summary>
static ServerConfig? _config;

/// <summary>
/// Portváltás alatt az aktív gépek (3 napon belül jelentkeztek) állapota: átállt / még a régin / nem tudni
/// (a szerver indulása óta még nem jelentkezett).
/// </summary>
public static (List<string> OnNew, List<string> OnOld, List<string> Unknown) PortStatus(FleetStore store, ServerConfig cfg)
{
    var onNew = new List<string>(); var onOld = new List<string>(); var unknown = new List<string>();
    foreach (var a in store.Agents.Where(a => !a.Revoked && a.LastSeenUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromDays(3)))
    {
        if (!AgentPortSeen.TryGetValue(a.AgentId, out var p)) unknown.Add(a.MachineName);
        else if (p == cfg.AgentPort) onNew.Add(a.MachineName);
        else onOld.Add(a.MachineName);
    }
    return (onNew, onOld, unknown);
}

/// <summary>A régi port lezárása, ha minden aktív gép átállt, vagy lejárt a türelmi idő.</summary>
public static void CheckPortTransition(FleetStore store)
{
    var cfg = _config;
    if (cfg is null || cfg.OldAgentPort <= 0) return;
    var (_, onOld, unknown) = PortStatus(store, cfg);
    var deadline = (cfg.OldAgentPortSinceUtc ?? DateTime.UtcNow).AddDays(Math.Max(1, cfg.OldAgentPortDays));
    if (onOld.Count + unknown.Count == 0) CloseOldPort(cfg, "rendszer", "minden aktív gép átállt az új portra");
    else if (DateTime.UtcNow >= deadline)
        CloseOldPort(cfg, "rendszer", $"lejárt a türelmi idő ({cfg.OldAgentPortDays} nap); még a régin: {string.Join(", ", onOld.Concat(unknown).Take(20))}");
}

/// <summary>A régi agent-port lezárása (figyelés, tűzfal). A még át nem állt gépeket újra kell telepíteni / átállítani.</summary>
public static void CloseOldPort(ServerConfig cfg, string user, string reason)
{
    lock (PortLock)
    {
        var old = cfg.OldAgentPort;
        if (old <= 0) return;
        try { _agentListener?.Prefixes.Remove($"http://+:{old}/"); } catch (Exception ex) { SrvLog.Warn($"A régi {old} port lezárása: {ex.Message}"); }
        cfg.OldAgentPort = 0; cfg.OldAgentPortSinceUtc = null;
        cfg.Save(DataDir);
        if (OperatingSystem.IsWindows()) ServerInstaller.EnsureFirewall(new[] { cfg.AgentPort }, cfg.WebPort);
        AuditLog.Write(DataDir, user, "régi agent-port lezárva", $"{old} ({reason})");
        SrvLog.Info($"Portváltás vége: a régi {old} agent-port lezárva – {reason}.");
    }
}

/// <summary>
/// A portok kézi állítása a webről, újraindítás nélkül. Az agent-port váltásakor a régi port tovább figyel (a gépek az aláírt
/// válaszban kapják az újat, és ellenőrzés után átállnak); a web-port azonnal vált (a böngészőt a felület átirányítja).
/// URL-foglalás és tűzfalszabály az új portra. Visszaadja a hibát (null = siker).
/// </summary>
public static string? ChangePorts(ServerConfig cfg, int agentPort, int webPort, string user, out bool webChanged)
{
    webChanged = false;
    lock (PortLock)
    {
        if (agentPort is < 1024 or > 65535 || webPort is < 1024 or > 65535) return "A port 1024 és 65535 közötti szám legyen.";
        if (agentPort == webPort) return "Az agent-port és a web-port nem lehet azonos.";
        int curA = cfg.AgentPort, curW = cfg.WebPort, old = cfg.OldAgentPort;
        var ours = new HashSet<int> { curA, curW };
        if (old > 0) ours.Add(old);
        foreach (var p in new[] { agentPort, webPort }.Distinct())
            if (!ours.Contains(p) && PortTools.IsInUse(p)) return $"A(z) {p} port foglalt" + (PortTools.Owner(p) is { } o ? $" – ezt használja: {o}" : ".");
        if (agentPort != curA && agentPort == curW) return "Az agent-port nem lehet a mostani web-port.";
        if (webPort != curW && (webPort == curA || webPort == old)) return "A web-port nem lehet egy agent-port (a mostani vagy a még figyelő régi).";
        if (agentPort != curA && old > 0 && agentPort != old)
            return $"Még tart az előző portváltás (a régi {old} port is figyel). Várd meg a végét, vagy zárd le a régi portot.";

        var added = new List<(HttpListener L, string P)>();
        try
        {
            if (agentPort != curA)
            {
                var pre = $"http://+:{agentPort}/";
                if (OperatingSystem.IsWindows()) NativeUrlAcl.Ensure(pre, "D:(A;;GX;;;SY)");
                if (_agentListener is { } al && !al.Prefixes.Contains(pre)) { al.Prefixes.Add(pre); added.Add((al, pre)); }
            }
            if (webPort != curW)
            {
                var pre = $"http://+:{webPort}/";
                if (OperatingSystem.IsWindows()) NativeUrlAcl.Ensure(pre, "D:(A;;GX;;;SY)");
                if (_webListener is { } wl && !wl.Prefixes.Contains(pre)) { wl.Prefixes.Add(pre); added.Add((wl, pre)); }
            }
        }
        catch (Exception ex)
        {
            foreach (var (l, p) in added) try { l.Prefixes.Remove(p); } catch { }
            return "Az új porton nem sikerült figyelni: " + ex.Message;
        }

        if (agentPort != curA)
        {
            // Visszaváltás a még figyelő régi portra: a mostani lesz a „régi” (a már átállt gépek is visszatalálnak).
            cfg.OldAgentPort = curA;
            cfg.OldAgentPortSinceUtc = DateTime.UtcNow;
            cfg.AgentPort = agentPort;
            AuditLog.Write(DataDir, user, "agent-port váltása", $"{curA} -> {agentPort} (a régi {cfg.OldAgentPortDays} napig még figyel)");
            SrvLog.Info($"Agent-port váltása: {curA} -> {agentPort}; a régi port az agentek átállásáig figyel.");
        }
        if (webPort != curW)
        {
            cfg.WebPort = webPort;
            webChanged = true;
            AuditLog.Write(DataDir, user, "web-port váltása", $"{curW} -> {webPort}");
            SrvLog.Info($"Web-port váltása: {curW} -> {webPort}.");
            // A régi web-port pár másodperc múlva zár (addig a mostani válasz és az átirányítás még kimegy).
            var wl = _webListener;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => { try { wl?.Prefixes.Remove($"http://+:{curW}/"); } catch { } });
        }
        cfg.Save(DataDir);
        if (OperatingSystem.IsWindows())
            ServerInstaller.EnsureFirewall(cfg.OldAgentPort > 0 ? new[] { cfg.AgentPort, cfg.OldAgentPort } : new[] { cfg.AgentPort }, cfg.WebPort);
        return null;
    }
}

/// <summary>A nem jelentkező gépek (a beállított csoportokból) riasztása; a visszatérők riasztása megszűnik.</summary>
public static void CheckOffline(FleetStore store)
{
    var cfg = ServerConfig.LoadCached(DataDir);
    var limit = TimeSpan.FromMinutes(cfg.OfflineAlertMinutes);
    var items = cfg.OfflineAlertMinutes <= 0 ? Enumerable.Empty<OfflineCheck.Item>()
        : store.Agents.Where(a => !a.Revoked && OfflineCheck.Watched(cfg.OfflineAlertGroups, GroupOps.Of(a, cfg)))
              .Select(a => new OfflineCheck.Item(a.AgentId, a.MachineName, a.LastSeenUtc, limit));
    foreach (var ev in OfflineCheck.Run(Alerts, items)) AlertLog.Write(ev);
}

public static async Task AcceptLoop(HttpListener listener, Func<HttpListenerContext, Task> handler, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { break; }
        _ = Task.Run(() => Safe(ctx, handler));
    }
}

public static async Task Safe(HttpListenerContext ctx, Func<HttpListenerContext, Task> handler)
{
    try { await handler(ctx); }
    catch (Exception ex) { try { ctx.Response.StatusCode = 500; await WriteJson(ctx, new { error = ex.Message }); } catch { } }
    finally { try { ctx.Response.Close(); } catch { } }
}

// ================================================================ AGENT API

/// <summary>
/// A jelentkezes feldolgozasa + metrikak tarolasa + a monitor-konfig leküldese.
/// A tarolas es a konfig kulon van a FleetStore-tol, hogy az ne fuggjon az SQLite-tol.
/// </summary>
public static CheckinResponse DoCheckin(FleetStore store, CheckinRequest req, int localPort = 0)
{
    // Az agent a szolgáltatáslistát csak változáskor küldi: ha most nincs benne, az előző marad.
    var prevServices = store.GetAgent(req.AgentId)?.LastStatus?.ServiceList;
    var prevComponents = store.GetAgent(req.AgentId)?.LastStatus?.Components;   // driverek / firmware: ugyanígy, csak változáskor jön
    var prevVersion = store.GetAgent(req.AgentId)?.AgentVersion ?? "";
    var resp = store.Checkin(req);
    if (!resp.Ok) return SignReply(store, req, resp);
    Terminal.Seen(req.AgentId);   // 1.5.9: az agent most jelentkezett (a terminál-parancsok sorát ez engedi tovább)
    // Az agent frissült (a felület mutatja: mikor, mire) - a műveleti naplóba is.
    var newVersion = req.Status?.AgentVersion ?? "";
    if (Version.TryParse(prevVersion, out var pv) && Version.TryParse(newVersion, out var nv) && nv > pv)
    {
        AgentUpdates.Record(req.AgentId, prevVersion, newVersion);
        AuditLog.Write(ServerPaths.DataDir, "agent", "agent frissült", $"{store.GetAgent(req.AgentId)?.MachineName}: {prevVersion} -> {newVersion}");
    }
    if (req.Status?.ServiceList is null && prevServices is not null && store.GetAgent(req.AgentId)?.LastStatus is { } ls) ls.ServiceList = prevServices;
    if (req.Status?.Components is null && prevComponents is not null && store.GetAgent(req.AgentId)?.LastStatus is { } ls2) ls2.Components = prevComponents;
    // 1.5.6: gyors szolgáltatás-figyelés (leállás → riasztás) és az USB-napló (külön fájlba, nem a fleet.json-ba).
    try
    {
        var mname = store.GetAgent(req.AgentId)?.MachineName ?? req.AgentId;
        if (req.Status?.ServiceStates is { } states)
            foreach (var ev in ServiceWatch.Apply(Alerts, req.AgentId, mname, states)) AlertLog.Write(ev);
        if (req.Status?.UsbEvents is { Count: > 0 } usb) UsbLog.Add(req.AgentId, mname, usb);
    }
    catch { }

    var cfg = ServerConfig.LoadCached(ServerPaths.DataDir);   // csak olvassuk - jelentkezesenkent ne lemezrol
    // Portváltás: melyik porton jött (az átállás követéséhez), és az új port az aláírt válaszba.
    if (localPort > 0) AgentPortSeen[req.AgentId] = localPort;
    if (cfg.OldAgentPort > 0) resp.AgentPort = cfg.AgentPort;

    // 1. a beerkezett metrikak idosorba: a jelentkezesek kozotti mintak a sajat idobelyegukkel,
    //    plusz a jelentkezeskori meres. Egy tranzakcioban, idorendben.
    try
    {
        var now = DateTime.UtcNow;
        var batch = new List<(DateTime When, IReadOnlyList<Metric> Metrics)>();
        foreach (var s in req.Status?.MetricSamples ?? new())
        {
            // Hihetetlen idobelyeg (rossz ora, visszajatszott regi adat) nem kerul be.
            if (s.Metrics.Count == 0 || s.TimestampUtc < now.AddHours(-48) || s.TimestampUtc > now.AddMinutes(5)) continue;
            batch.Add((s.TimestampUtc, s.Metrics));
        }
        if (req.Status?.Metrics is { Count: > 0 } ms) batch.Add((now, ms));

        if (batch.Count > 0)
        {
            batch.Sort((a, b) => a.When.CompareTo(b.When));
            Metrics.InsertMany(req.AgentId, batch);

            // Kuszob-kiertekeles mintankent: a "hany egymast koveto meres" igy percre pontos.
            // Allapotvaltasnal naplozunk (a felulet a naplot es az aktiv riasztasokat mutatja).
            var ag = store.GetAgent(req.AgentId);
            var machine = ag?.MachineName ?? req.AgentId;
            var groups = ag is null ? null : GroupOps.Of(ag, cfg);   // kézi + automatikus csoportok (a szabály hatóköre)
            foreach (var (_, m) in batch)
                foreach (var ev in Alerts.Evaluate(req.AgentId, machine, m, groups))
                    AlertLog.Write(ev);
        }
        // 1.5.8: a megőrzés a Központé, ha beállította; ha a Központ fogadja a lezárt napi fájlokat, a megőrzés után
        // nem törlünk, hanem a HubClient feltölti (és a Központ nyugtája után törli) – így egy példány marad.
        var keep = cfg.EffectiveKeepDays();
        Metrics.ApplyRetention(Math.Min(cfg.MonDetailDays, keep), keep, 0, holdForUpload: cfg.HubUrl.Length > 0 && cfg.HubAcceptsMetricFiles);
    }
    catch { }

    // 2. a monitor-konfig leküldése az agentnek (a helyi és a Központ kérése összefésülve)
    resp.Metrics = cfg.EffectiveMetricConfig();
    // 3. automatikus frissitesi szabalyok (karbantartasi ablak) - a mostani valaszba kerulnek
    try
    {
        var agent = store.GetAgent(req.AgentId);
        // A helyi szabalyok es a Kozponttol kapottak egyutt, ugyanazzal a motorral (helyi idoben).
        if (agent is not null && cfg.AutoPolicies.Count + cfg.HubPolicies.Count > 0)
        {
            var auto = PolicyEngine.Evaluate(cfg.AutoPolicies.Concat(cfg.HubPolicies), agent, DateTime.Now, DateTime.UtcNow, GroupOps.Of(agent, cfg));
            if (auto.Count > 0)
            {
                resp.Tasks.AddRange(auto);
                store.SaveState();
                AuditLog.Write(ServerPaths.DataDir, "automatikus szabály", "feladat kiadva",
                    $"{agent.MachineName}: " + string.Join(", ", auto.Select(t => t.Kind)));
            }
        }
    }
    catch { }

    // 3b. kulcscsere-szabaly: az agent hetente (beallithato) uj kulcsot keszit; a szerver fogad P-384-et is.
    resp.KeyPolicy = new KeyPolicy { RotateDays = cfg.AgentKeyRotationDays, Strong = true };

    // 3c. webes admin terminál (1.5.9): csak a kifejezetten engedélyezett gépeken. Nyitott terminál-ablak alatt gyors
    //     betárcsázás (2 mp), hogy a parancs és a kimenet hamar átmenjen; különben a szokásos ütem.
    resp.Terminal = cfg.TerminalAgents.Contains(req.AgentId);
    if (resp.Terminal && Terminal.Active(req.AgentId)) resp.NextCheckinSeconds = 2;

    // 4. agent-onfrissites ajanlat (az alairt reszbe kerul, SHA-256-tal)
    // Agent-frissítés: automatikusan (beállítás), vagy ha a Központ az adott gépre kérte (ForceUpdate).
    var self = store.GetAgent(req.AgentId);
    if ((cfg.AgentAutoUpdate || self?.ForceUpdate == true) && AgentPackage.Current() is { } pkg)
    {
        if (AgentPackage.ShouldOffer(pkg, req.Status?.AgentVersion ?? "", req.Status?.AgentSha256 ?? ""))
            resp.AgentUpdate = new AgentUpdateOffer { Version = pkg.Version, Sha256 = pkg.Sha256, Size = pkg.Size };
        else if (self?.ForceUpdate == true) { self.ForceUpdate = false; store.SaveState(); }   // már naprakész
    }

    return SignReply(store, req, resp);
}

/// <summary>
/// A valasz alairasa a szerver kulcsaval. A keres nonce-a es az agent azonositoja is az
/// alairt reszben van, igy a valasz nem hamisithato, nem jatszhato vissza, es mas agentnek
/// nem adhato at. Kulcscsere utan a lanc is megy, hogy az agent atallhasson.
/// </summary>
public static CheckinResponse SignReply(FleetStore store, CheckinRequest req, CheckinResponse resp)
{
    var body = new CheckinReplyBody
    {
        AgentId = req.AgentId, RequestNonce = req.Nonce, Ok = resp.Ok, Message = resp.Message,
        Tasks = resp.Tasks, NextCheckinSeconds = resp.NextCheckinSeconds, Metrics = resp.Metrics,
        AgentUpdate = resp.AgentUpdate, KeyPolicy = resp.KeyPolicy, AgentPort = resp.AgentPort, Terminal = resp.Terminal
    };
    resp.SignedPayload = ProtocolJson.Serialize(body);
    resp.Signature = Crypto.Sign(store.ServerKeys.PrivateKeyPem, resp.SignedPayload);
    resp.SignerPublicKeyPem = store.ServerKeys.PublicKeyPem;
    resp.KeyChain = store.KeyChain;
    return resp;
}

/// <summary>
/// Az agent kulcscseréje. A válasz a szerver kulcsával aláírt, a kérés nonce-ához és az agenthez kötött:
/// az agent csak erre a visszaigazolásra vált kulcsot (hamis szerver nem tudja rávenni).
/// </summary>
public static KeyRotateResponse DoRotateKey(FleetStore store, KeyRotateRequest req)
{
    var (ok, msg, fp) = store.RotateAgentKey(req);
    if (ok)
        AuditLog.Write(ServerPaths.DataDir, "agent", "agent-kulcscsere",
            $"{store.GetAgent(req.AgentId)?.MachineName}: új kulcs {fp[..16]}… ({Crypto.Algorithm(req.NewPublicKeyPem)})");
    var body = new KeyRotateReplyBody { AgentId = req.AgentId, RequestNonce = req.Nonce, Ok = ok, Message = msg, Fingerprint = fp };
    var payload = ProtocolJson.Serialize(body);
    return new KeyRotateResponse
    {
        SignedPayload = payload, Signature = Crypto.Sign(store.ServerKeys.PrivateKeyPem, payload),
        SignerPublicKeyPem = store.ServerKeys.PublicKeyPem, KeyChain = store.KeyChain
    };
}

/// <summary>
/// A szerver saját kulcsának automatikus cseréje (alapból hetente). Óránként nézi; a csere láncolt (a régi kulcs
/// aláírja az újat), az agentek a következő jelentkezéskor ellenőrzötten átállnak. Erős (P-384) kulcs csak akkor,
/// ha minden agent tudja ellenőrizni.
/// </summary>
static async Task ServerKeyLoop(FleetStore store, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), token); } catch { break; }
        try
        {
            var days = ServerConfig.LoadCached(DataDir).ServerKeyRotationDays;
            if (days > 0 && DateTime.UtcNow - store.ServerKeyCreatedUtc >= TimeSpan.FromDays(days))
            {
                store.RotateKeys("automatikus (" + days + " naponta)");
                AuditLog.Write(DataDir, "rendszer", "automatikus kulcscsere", $"új szerverkulcs {store.ServerKeys.Fingerprint[..16]}… ({Crypto.Algorithm(store.ServerKeys.PublicKeyPem)})");
                SrvLog.Info($"Automatikus kulcscsere: {Crypto.Algorithm(store.ServerKeys.PublicKeyPem)}, új ujjlenyomat {store.ServerKeys.Fingerprint}");
            }
        }
        catch (Exception ex) { SrvLog.Warn("Automatikus kulcscsere: " + ex.Message); }
        try { await Task.Delay(TimeSpan.FromMinutes(55), token); } catch { break; }
    }
}

/// <summary>Feladat-eredmény: tárolás, és ha egy tálcás programkérés telepítése volt, a kérés lezárása.</summary>
static object DoResult(FleetStore store, TaskResult r)
{
    var ok = store.RecordResult(r);
    if (ok) try { ProgramRequests.OnResult(r); } catch { }
    return new { ok };
}

public static async Task HandleAgent(HttpListenerContext ctx, FleetStore store)
{
    // Agent-frissites letoltese. Nem kell hitelesites: az agent a fajlt a szerver altal
    // ALAIRT SHA-256 lenyomattal ellenorzi, mielott barmit csinalna vele.
    if ((ctx.Request.Url?.AbsolutePath ?? "").Equals("/api/agent-update", StringComparison.OrdinalIgnoreCase))
    {
        await ServeAgentExe(ctx);
        return;
    }

    var path = ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "";
    var body = await ReadBody(ctx);

    object? response = path switch
    {
        "/api/enroll" => store.Enroll(ProtocolJson.Deserialize<EnrollRequest>(body) ?? new()),
        "/api/checkin" => DoCheckin(store, ProtocolJson.Deserialize<CheckinRequest>(body) ?? new(), ctx.Request.LocalEndPoint?.Port ?? 0),
        "/api/rotate-key" => DoRotateKey(store, ProtocolJson.Deserialize<KeyRotateRequest>(body) ?? new()),
        "/api/result" => DoResult(store, ProtocolJson.Deserialize<TaskResult>(body) ?? new()),
        // 1.5.7: a tálca-felület kérései az agenten át (az agent kulcsával aláírva, a válasz a szerverével)
        "/api/user" => TrayApi.Handle(store, ServerConfig.LoadCached(ServerPaths.DataDir), ProtocolJson.Deserialize<AgentUserRequest>(body) ?? new()),
        "/api/ping" => new { ok = true, service = "devtoolbox-fleet" },
        // A telepitovarazslo ezzel ellenorzi az ujjlenyomatot TELEPITES ELOTT. Csak publikus adat.
        "/api/server-key" => new { publicKeyPem = store.ServerKeys.PublicKeyPem, fingerprint = store.ServerKeys.Fingerprint, keyChain = store.KeyChain },
        _ => null
    };

    if (response is null) { ctx.Response.StatusCode = 404; await WriteJson(ctx, new { error = "ismeretlen végpont" }); return; }
    await WriteJson(ctx, response);
}

// ================================================================ WEB

public static async Task HandleWeb(HttpListenerContext ctx, FleetStore store, ServerConfig config,
    UserStore users, RoleSession sessions, string dir)
{
    var path = ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "/";
    var method = ctx.Request.HttpMethod;

    // --- setup: ha meg nincs admin, minden ut a setupra visz
    if (!config.SetupDone)
    {
        if (path == "/web/setup" && method == "POST")
        {
            await HandleSetup(ctx, config, users, sessions, dir);
            return;
        }
        await WriteHtml(ctx, WebUi.SetupPage(null, config.WebPort, config.AgentPort));
        return;
    }

    // --- login
    if (path == "/web/login" && method == "POST") { await HandleLogin(ctx, users, sessions); return; }
    if (path == "/web/logout") { ClearCookie(ctx); Redirect(ctx, "/web"); return; }

    // --- védett terület: érvényes session kell
    var sess = sessions.Validate(GetCookie(ctx, "fleet_session"));
    var user = sess?.User;
    var role = sess?.Role ?? Role.Megfigyelo;

    if (path is "/web" or "/" or "/web/")
    {
        if (user is null) { await WriteHtml(ctx, WebUi.LoginPage(null)); return; }
        await WriteHtml(ctx, WebUi.Dashboard(user, role));
        return;
    }

    // --- web API (session-védett)
    // Az agent-exe letoltese: ezt a telepito szkript hivja, ezert NEM session-vedett,
    // de csak akkor ad ki barmit, ha az exe a szerver mellett van. Az exe onmagaban
    // nem erzekeny - a token nelkul semmit nem tud tenni.
    if (path == "/web/agent-exe" && method == "GET")
    {
        await ServeAgentExe(ctx);
        return;
    }

    // A kesz telepitocsomag: exe + TELEPIT.cmd (tokennel). Egy fajlt tolt le a felhasznalo.
    // A token a query-ben van; ez session-vedett (csak bejelentkezett admin keri a linket).
    if (path == "/web/agent-zip" && method == "GET")
    {
        if (user is null) { ctx.Response.StatusCode = 401; await WriteJson(ctx, new { error = "nincs bejelentkezve" }); return; }
        await ServeAgentZip(ctx, config, store);
        return;
    }

    // FleetServer frissites feltoltese (binaris tartalom, nem JSON).
    if (path == "/web/api/upload-server" && method == "POST")
    {
        if (user is null) { ctx.Response.StatusCode = 401; await WriteJson(ctx, new { error = "nincs bejelentkezve" }); return; }
        if (role < Role.Kezelo) { ctx.Response.StatusCode = 403; await WriteJson(ctx, new { error = "Ehhez kezelői vagy admin jog kell." }); return; }
        AuditLog.Write(ServerPaths.DataDir, user, "FleetServer frissítés feltöltése", ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "");
        await HandleServerUpload(ctx);
        return;
    }

    // CSV-jelentés letöltése (GET link, a munkamenet-süti védi). Megfigyelő is letöltheti: ugyanazt tartalmazza, amit a felületen lát.
    if (path == "/web/report" && method == "GET")
    {
        if (user is null) { ctx.Response.StatusCode = 401; await WriteJson(ctx, new { error = "nincs bejelentkezve" }); return; }
        await ServeReport(ctx, store, user);
        return;
    }

    if (path.StartsWith("/web/api/"))
    {
        if (user is null) { ctx.Response.StatusCode = 401; await WriteJson(ctx, new { error = "nincs bejelentkezve" }); return; }
        await HandleWebApi(ctx, store, config, users, role, user!, path);
        return;
    }

    Redirect(ctx, "/web");
}

public static async Task HandleSetup(HttpListenerContext ctx, ServerConfig config, UserStore users, RoleSession sessions, string dir)
{
    var form = await ReadForm(ctx);
    var user = form.GetValueOrDefault("user", "admin").Trim();
    var pw = form.GetValueOrDefault("password", "");
    var pw2 = form.GetValueOrDefault("password2", "");

    if (pw != pw2) { await WriteHtml(ctx, WebUi.SetupPage("A két jelszó nem egyezik.", config.WebPort, config.AgentPort)); return; }

    var strength = ServerConfig.CheckPasswordStrength(pw);
    if (!strength.Ok) { await WriteHtml(ctx, WebUi.SetupPage(strength.Message, config.WebPort, config.AgentPort)); return; }

    int webPort = int.TryParse(form.GetValueOrDefault("webPort", ""), out var wp) ? wp : 8080;
    int agentPort = int.TryParse(form.GetValueOrDefault("agentPort", ""), out var ap) ? ap : 8443;

    if (webPort == agentPort) { await WriteHtml(ctx, WebUi.SetupPage("A két port nem lehet azonos.", config.WebPort, config.AgentPort)); return; }

    config.AdminUser = user.Length > 0 ? user : "admin";
    config.SetPassword(pw);   // megtartjuk a config-jelszot is (visszafele kompat.)
    config.WebPort = webPort;
    config.AgentPort = agentPort;
    config.Save(dir);

    // Az elso felhasznalo: Admin szerepkorrel.
    users.EnsureFirstAdmin(config.AdminUser, pw);

    SetCookie(ctx, "fleet_session", sessions.Issue(config.AdminUser, Role.Admin));
    await WriteHtml(ctx, $$"""
<!DOCTYPE html><html lang="hu"><head><meta charset="utf-8">
<meta http-equiv="refresh" content="4;url=/web"><title>Kész</title>
<style>body{background:#0f1116;color:#e6e9ef;font-family:"Segoe UI",sans-serif;
display:grid;place-items:center;height:100vh;text-align:center}
a{color:#5b8cff}</style></head><body><div>
<h1>A beállítás elkészült</h1>
<p>Ha megváltoztattad a portokat, indítsd újra a Fleet Server szolgáltatást,<br>
hogy az új portokon figyeljen. Utána a webfelület itt lesz elérhető:</p>
<p><b>http://localhost:{{config.WebPort}}</b></p>
<p style="color:#8a93a3">Átirányítás a vezérlőpultra…</p>
</div></body></html>
""");
}

public static async Task HandleLogin(HttpListenerContext ctx, UserStore users, RoleSession sessions)
{
    var form = await ReadForm(ctx);
    var user = form.GetValueOrDefault("user", "").Trim();
    var pw = form.GetValueOrDefault("password", "");

    var ip = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "?";
    if (LoginThrottle.Blocked(ip) is { } left)
    {
        LoginThrottle.Fail(ip);   // a zarolas alatti probalkozas meghosszabbitja
        AuditLog.Write(DataDir, user, "belépés tiltva (túl sok próbálkozás)", ip);
        await WriteHtml(ctx, WebUi.LoginPage($"Túl sok sikertelen próbálkozás. Próbáld újra {Math.Ceiling(left.TotalMinutes)} perc múlva."));
        return;
    }

    var acc = users.Authenticate(user, pw);
    if (acc is not null)
    {
        LoginThrottle.Success(ip);
        AuditLog.Write(DataDir, acc.Username, "belépés", ip);
        SetCookie(ctx, "fleet_session", sessions.Issue(acc.Username, acc.Role));
        Redirect(ctx, "/web");
    }
    else
    {
        LoginThrottle.Fail(ip);
        AuditLog.Write(DataDir, user, "sikertelen belépés", ip);
        await WriteHtml(ctx, WebUi.LoginPage("Hibás felhasználónév, jelszó, vagy letiltott fiók."));
    }
}

public static async Task HandleWebApi(HttpListenerContext ctx, FleetStore store, ServerConfig config,
    UserStore users, Role role, string user, string path)
{
    var body = await ReadBody(ctx);
    AuditLog.Api(ServerPaths.DataDir, user, path, body);

    // Jogosultsag: a vezerlo muveletek Kezeloi, a felhasznalokezeles Admin jog.
    bool NeedKezelo() { if (role < Role.Kezelo) { ctx.Response.StatusCode = 403; return true; } return false; }
    bool NeedAdmin()  { if (role < Role.Admin)  { ctx.Response.StatusCode = 403; return true; } return false; }

    // Felhasznalokezeles (csak Admin)
    switch (path)
    {
        case "/web/api/audit":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            await WriteJson(ctx, new { log = AuditLog.Tail(ServerPaths.DataDir, 400) });
            return;
        }

        case "/web/api/rotate-keys":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            try
            {
                store.RotateKeys();
                await WriteJson(ctx, new { ok = true, fingerprint = store.ServerKeys.Fingerprint });
            }
            catch (Exception ex) { await WriteJson(ctx, new { ok = false, error = ex.Message }); }
            return;
        }

        // ---- kulcslista: a szerver kulcsai (előzmény), az agentek kulcsai (előzmény), a kiadott beléptető tokenek
        case "/web/api/keys":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { error = "Ehhez admin jog kell." }); return; }
            store.ProcessInbox();
            static string? L(DateTime? t) => t?.ToLocalTime().ToString("yyyy.MM.dd HH:mm");
            static object Hist(IEnumerable<KeyHistoryItem> h) => h.Reverse().Select(k => new
                { fingerprint = k.Fingerprint, algorithm = k.Algorithm, created = L(k.CreatedUtc), retired = L(k.RetiredUtc), reason = k.Reason });
            var blockers = store.StrongKeyBlockers();
            var days = config.ServerKeyRotationDays;
            await WriteJson(ctx, new
            {
                owner = new
                {
                    title = "A szerver kulcsa", fingerprint = store.ServerKeys.Fingerprint, algorithm = Crypto.Algorithm(store.ServerKeys.PublicKeyPem),
                    created = L(store.ServerKeyCreatedUtc), rotateDays = days,
                    next = days > 0 ? L(store.ServerKeyCreatedUtc.AddDays(days)) : null, history = Hist(store.ServerKeyHistory),
                    strongNote = blockers.Count == 0 ? "" : $"A következő szerverkulcs is P-256 lesz, amíg {blockers.Count} régi (1.4.5 előtti) agent van: " +
                        string.Join(", ", blockers.Take(10)) + (blockers.Count > 10 ? " …" : "") + " – frissüljenek (vagy vond vissza őket)."
                },
                peerKind = "agent", peerRotateDays = config.AgentKeyRotationDays,
                peers = store.Agents.OrderBy(a => a.MachineName, StringComparer.CurrentCultureIgnoreCase).Select(a => new
                {
                    name = a.MachineName, fingerprint = a.Fingerprint, algorithm = Crypto.Algorithm(a.PublicKeyPem),
                    since = L(a.KeyChangedUtc ?? a.EnrolledUtc), pending = a.PreviousPublicKeyPem.Length > 0, revoked = a.Revoked,
                    version = a.AgentVersion, history = Hist(a.KeyHistory.Count > 0 ? a.KeyHistory
                        : new List<KeyHistoryItem> { new() { Fingerprint = a.Fingerprint, Algorithm = Crypto.Algorithm(a.PublicKeyPem), CreatedUtc = a.EnrolledUtc, Reason = "regisztráció" } })
                }),
                tokens = store.Tokens.OrderByDescending(t => t.CreatedUtc).Select(t => new
                {
                    id = t.Fingerprint, label = t.Label, created = L(t.CreatedUtc), expires = L(t.ExpiresUtc), uses = t.Uses, maxUses = t.MaxUses,
                    status = !t.Enabled ? "letiltva" : t.IsExpired ? "lejárt" : t.IsUsedUp ? "elhasználva" : "aktív"
                })
            });
            return;
        }
        case "/web/api/key-settings":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<KeySetReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            config.ServerKeyRotationDays = ServerConfig.ClampRotationDays(r.ServerDays);
            config.AgentKeyRotationDays = ServerConfig.ClampRotationDays(r.PeerDays);
            config.Save(ServerPaths.DataDir);
            await WriteJson(ctx, new { ok = true });
            return;
        }
        case "/web/api/token-disable":
        case "/web/api/token-delete":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<KeySetReq>(body);
            var t = store.Tokens.FirstOrDefault(x => x.Fingerprint == r?.Id);
            if (t is null) { await WriteJson(ctx, new { ok = false, error = "Nincs ilyen token." }); return; }
            if (path.EndsWith("delete")) store.RemoveToken(t.Fingerprint); else store.SetTokenEnabled(t.Fingerprint, false);
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/agent-update":
        {
            // Gépenkénti „agent frissítése most”: a következő jelentkezéskor felajánljuk (az automatikus frissítéstől függetlenül).
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<MetricReq>(body);
            if (store.GetAgent(r?.AgentId ?? "") is not { Revoked: false } a) { await WriteJson(ctx, new { ok = false, error = "Nincs ilyen gép." }); return; }
            store.RequestAgentUpdate(a.AgentId);
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/users":
            if (NeedAdmin()) { await WriteJson(ctx, new { error = "Ehhez admin jog kell." }); return; }
            await WriteJson(ctx, new { users = users.All.Select(u => new { u.Username, role = (int)u.Role,
                roleName = RoleName(u.Role), u.Disabled, created = u.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd") }) });
            return;
        case "/web/api/user-create":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<UserReq>(body);
            var res = users.Create(r?.Username ?? "", r?.Password ?? "", (Role)(r?.Role ?? 0));
            await WriteJson(ctx, new { ok = res.Ok, error = res.Error }); return;
        }
        case "/web/api/user-role":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<UserReq>(body);
            var res = users.SetRole(r?.Username ?? "", (Role)(r?.Role ?? 0));
            await WriteJson(ctx, new { ok = res.Ok, error = res.Error }); return;
        }
        case "/web/api/user-password":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<UserReq>(body);
            var res = users.SetPassword(r?.Username ?? "", r?.Password ?? "");
            await WriteJson(ctx, new { ok = res.Ok, error = res.Error }); return;
        }
        case "/web/api/user-disable":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<UserReq>(body);
            var res = users.SetDisabled(r?.Username ?? "", r?.Disabled ?? false);
            await WriteJson(ctx, new { ok = res.Ok, error = res.Error }); return;
        }
        case "/web/api/user-delete":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<UserReq>(body);
            var res = users.Delete(r?.Username ?? "");
            await WriteJson(ctx, new { ok = res.Ok, error = res.Error }); return;
        }
    }

    switch (path)
    {
        case "/web/api/server-info":
        {
            await WriteJson(ctx, new
            {
                version = ServerBuild.Version,
                fingerprint = store.ServerKeys.Fingerprint,
                agentPort = config.AgentPort,
                webPort = config.WebPort,
                oldAgentPort = config.OldAgentPort,
                tokenLifetimeMinutes = config.TokenLifetimeMinutes,
                tokenMaxUses = config.TokenMaxUses,
                agentAutoUpdate = config.AgentAutoUpdate,
                agentPackageVersion = AgentPackage.Current()?.Version,
                pendingUpdate = File.Exists(Path.Combine(ServerPaths.InstallDir, "FleetServer.new.exe")),
                hubUrl = config.HubUrl,
                hubSiteName = config.HubSiteName,
                hubFingerprint = config.HubFingerprint,
                hubEnrolled = config.HubSiteId.Length > 0,
                hubLastContact = HubClient.Instance.LastContactUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss"),
                hubError = HubClient.Instance.LastError,
                hubInterval = HubClient.Instance.NextSeconds
            });
            return;
        }

        case "/web/api/agents":
        {
            store.ProcessInbox();   // a DevToolboxbol vagy parancssorbol erkezett valtozasok
            var list = store.Agents.OrderByDescending(a => a.LastSeenUtc).Select(a => new
            {
                agentId = a.AgentId,
                machineName = a.MachineName,
                operatingSystem = a.LastStatus?.OperatingSystem ?? a.OperatingSystem,
                agentVersion = a.AgentVersion,
                isVirtual = a.LastStatus?.IsVirtual ?? false,
                fingerprint = a.Fingerprint[..Math.Min(16, a.Fingerprint.Length)],
                online = a.IsOnline,
                revoked = a.Revoked,
                enrolled = a.EnrolledUtc.ToLocalTime().ToString("yyyy.MM.dd"),
                lastSeen = a.LastSeenUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                updatesAvailable = a.LastStatus?.UpdatesAvailable ?? 0,
                updates = a.LastStatus?.Updates.Select(u => new
                { id = u.Id, name = u.Name, installed = u.Installed, available = u.Available }),
                groups = a.Groups,
                // Besorolás a gépek listájának csoportosításához (típus, Windows-verzió, hardver) - az automatikus
                // csoportok kikapcsolásától függetlenül (az csak a hatókört érinti).
                classes = AutoGroups.For(GroupOps.OsOf(a), a.LastStatus?.IsVirtual),
                // A telepitett programok listaja nagy: a 15 mp-es frissitesben csak a darabszam,
                // maga a lista az Eltavolitas ablak megnyitasakor jon (/web/api/agent-installed).
                installedCount = a.LastStatus?.Installed.Count ?? 0,
                windowsUpdates = a.LastStatus?.WindowsUpdates.Select(w => new { w.UpdateId, w.Kb, w.Title, w.Severity, w.RebootRequired, w.SizeMb }),
                pendingReboot = a.LastStatus?.PendingReboot ?? false,
                updateSource = a.LastStatus?.UpdateSource ?? "",
                wuScanned = a.LastStatus?.WuScannedUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                wuError = a.LastStatus?.WuError ?? "",
                topProcesses = a.LastStatus?.TopProcesses,
                forceUpdate = a.ForceUpdate,
                // 1.5.6: eszköz-összegzés (Biztonság / Állapot, tételesen)
                score = ScoreOf(a),
                terminal = config.TerminalAgents.Contains(a.AgentId),
                cve = CveCounts.Of(a.AgentId) is { } cv ? new { total = cv.Critical + cv.High + cv.Medium + cv.Low, critical = cv.Critical, exploited = cv.Exploited } : null,
                lastUpdate = AgentUpdates.Last(a.AgentId) is { } lu ? new { from = lu.From, to = lu.To, at = lu.AtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm") } : null
            });
            await WriteJson(ctx, new { agents = list, agentPackageVersion = AgentPackage.Current()?.Version ?? "", agentAutoUpdate = config.AgentAutoUpdate,
                // a főoldal csempéje: internetkapcsolat (ha figyeljük)
                internet = config.NetWatchEnabled ? new { online = NetWatch.Online, since = NetWatch.SinceUtc?.ToLocalTime().ToString("MM.dd HH:mm"), target = NetWatch.LastTarget, rtt = NetWatch.LastRttMs } : null });
            return;
        }

        case "/web/api/agent-installed":
        {
            var req = ProtocolJson.Deserialize<MetricReq>(body);
            var a = store.GetAgent(req?.AgentId ?? "");
            await WriteJson(ctx, new { installed = a?.LastStatus?.Installed.Select(i => new { i.Id, i.Name, i.Version, i.Source }) ?? Enumerable.Empty<object>() });
            return;
        }

        case "/web/api/metric-keys":
        {
            // Mely metrikakra van adat egy gepnel + a legfrissebb ertek. Nezo is lathatja.
            var req = ProtocolJson.Deserialize<MetricReq>(body);
            var id = req?.AgentId ?? "";
            var latest = Metrics.Latest(id);
            var keys = Metrics.Keys(id).Select(k => new
            {
                key = k.Key, label = k.Label, category = k.Category, unit = k.Unit,
                latest = latest.TryGetValue(k.Key, out var v) ? (double?)v.Value : null
            });
            await WriteJson(ctx, new { keys });
            return;
        }

        case "/web/api/metric-series":
        {
            var req = ProtocolJson.Deserialize<MetricReq>(body);
            var hours = req?.Hours is > 0 and <= 2160 ? req.Hours : 24;
            var pts = Metrics.Series(req?.AgentId ?? "", req?.Key ?? "", hours)
                .Select(pp => new { t = pp.Ts, v = pp.Value });
            await WriteJson(ctx, new { series = pts });
            return;
        }

        case "/web/api/monitor-config":
        {
            // A monitor-beallitas lekerdezese (nezo is lathatja, mit gyujtunk).
            await WriteJson(ctx, new
            {
                cpu = config.MonCpu, memory = config.MonMemory, disk = config.MonDisk,
                diskSmart = config.MonDiskSmart, temperature = config.MonTemperature,
                eventLog = config.MonEventLog, services = config.MonServices,
                processes = config.MonProcesses, detailDays = config.MonDetailDays,
                keepDays = config.MonKeepDays, network = config.MonNetwork, diskIo = config.MonDiskIo,
                sampleSeconds = config.MonSampleSeconds,
                cpuDetail = config.MonCpuDetail, perCore = config.MonPerCore, hardware = config.MonHardware, sensorDriver = config.MonSensorDriver,
                gpu = config.MonGpu, security = config.MonSecurity, hyperV = config.MonHyperV, ping = config.MonPing, pingTargets = config.MonPingTargets,
                appUpdateMinutes = config.MonAppUpdateMinutes, wuScanHours = config.MonWuScanHours, watchSeconds = config.MonWatchSeconds, usbAudit = config.MonUsbAudit,
                tray = config.TrayEnabled,
                hubKeepDays = config.HubMetricKeepDays > 0 ? config.HubMetricKeepDays : (int?)null,
                // A Központ mérés-kérése (itt csak olvasható; a helyivel összefésülve megy az agentekhez)
                hub = config.HubMonitor
            });
            return;
        }

        case "/web/api/monitor-save":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<MonReq>(body);
            if (r is not null)
            {
                config.MonCpu = r.Cpu; config.MonMemory = r.Memory; config.MonDisk = r.Disk;
                config.MonDiskSmart = r.DiskSmart; config.MonTemperature = r.Temperature;
                config.MonEventLog = r.EventLog;
                config.MonNetwork = r.Network; config.MonDiskIo = r.DiskIo;
                config.MonDetailDays = Math.Clamp(r.DetailDays, 1, 365);
                config.MonKeepDays = Math.Clamp(r.KeepDays, r.DetailDays, 3650);
                // A mérendők teljes köre (a régi és az 1.5.0-s kapcsolók) ugyanabból a kérésből.
                if (ProtocolJson.Deserialize<MetricConfig>(body) is { } mc) { config.ApplyMonitor(mc); config.TrayEnabled = mc.Tray; }
                config.Save(ServerPaths.DataDir);
                AuditLog.Write(ServerPaths.DataDir, user, "mérés-beállítás mentése", $"hardver-érzékelők: {(config.MonHardware ? "be" : "ki")}, PawnIO: {(config.MonSensorDriver ? "be" : "ki")}");
            }
            await WriteJson(ctx, new { ok = true });
            return;
        }

        // ---- a gépeken levő szolgáltatások (a „Mit mérjen?” választólistájához), hány gépen van / fut
        case "/web/api/services-catalog":
            await WriteJson(ctx, new
            {
                services = ServiceCatalog(store.Agents.Where(a => !a.Revoked).Select(a => a.LastStatus?.ServiceList).Where(l => l is not null)!),
                machines = store.Agents.Count(a => !a.Revoked), reported = store.Agents.Count(a => !a.Revoked && a.LastStatus?.ServiceList is not null)
            });
            return;

        // ---- 1.5.10: egy gép szolgáltatásai a szolgáltatás-kezelő panelhez (név, megjelenítendő név, fut-e, auto)
        case "/web/api/machine-services":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<TerminalReq>(body);
            var a0 = r?.AgentId is { Length: > 0 } ? store.GetAgent(r.AgentId) : null;
            if (a0 is null) { await WriteJson(ctx, new { ok = false, error = "Nincs ilyen gép." }); return; }
            var list = a0.LastStatus?.ServiceList;
            await WriteJson(ctx, new
            {
                ok = true,
                reported = list is not null,
                services = (list ?? new List<ServiceEntry>())
                    .OrderBy(s => s.DisplayName.Length > 0 ? s.DisplayName : s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new { name = s.Name, displayName = s.DisplayName, running = s.Running, auto = s.Auto })
            });
            return;
        }

        // ---- metrika-katalógus a szabály-szerkesztőhöz: minden gép mért kulcsai + „bármelyik” minták
        case "/web/api/metric-catalog":
            await WriteJson(ctx, MetricCatalog.Build(Metrics.AllKeys()));
            return;

        // ---- gépcsoportok: lista tagokkal, létrehozás / törlés / átnevezés / tagság (felmegy a Központba is)
        case "/web/api/groups":
        {
            store.ProcessInbox();
            // Előbb a kézi csoportok (kézi tagsággal), utána az automatikusak (a gépek adataiból, csak olvasható).
            var live = store.Agents.Where(a => !a.Revoked).ToList();
            await WriteJson(ctx, new
            {
                groups = GroupOps.Manual(store, config).Select(g => new
                {
                    name = g, auto = false, members = live.Where(a => a.Groups.Contains(g, StringComparer.OrdinalIgnoreCase)).Select(a => a.AgentId)
                }).Concat(GroupOps.Auto(store, config).Select(g => new
                {
                    name = g, auto = true, members = live.Where(a => GroupOps.Of(a, config).Contains(g, StringComparer.OrdinalIgnoreCase)).Select(a => a.AgentId)
                })),
                autoOn = config.AutoGroupsOn,
                agents = live.OrderBy(a => a.MachineName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(a => new { agentId = a.AgentId, machineName = a.MachineName, online = a.IsOnline, groups = a.Groups })
            });
            return;
        }
        case "/web/api/group-op":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<GroupOpReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            var err = GroupOps.Apply(store, config, Alerts, r.Op, r.Group, r.NewName, r.AgentIds, out var changed);
            if (err is not null) { await WriteJson(ctx, new { ok = false, error = err }); return; }
            if (changed) config.Save(ServerPaths.DataDir);
            AuditLog.Write(ServerPaths.DataDir, user, "csoport: " + r.Op, r.Group + (r.NewName.Length > 0 ? " -> " + r.NewName : "") + (r.Op == "members" ? $" ({r.AgentIds?.Count ?? 0} gép)" : ""));
            await WriteJson(ctx, new { ok = true });
            return;
        }
        // ---- automatikus csoportok be/ki (Szerverek / Kliensek, Windows-verzió, Virtuális / Fizikai gépek)
        case "/web/api/auto-groups":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<AutoGroupsReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            config.AutoGroupsOn = r.On;
            config.Save(ServerPaths.DataDir);
            AuditLog.Write(ServerPaths.DataDir, user, "automatikus csoportok", r.On ? "be" : "ki");
            await WriteJson(ctx, new { ok = true });
            return;
        }

        // ---- internetkapcsolat figyelése (beállítás: kezelő): a címek sorban, az első válasz = van internet
        case "/web/api/netwatch":
            await WriteJson(ctx, new
            {
                enabled = config.NetWatchEnabled, targets = config.NetWatchTargets, minutes = config.NetWatchMinutes,
                online = NetWatch.Online, since = NetWatch.SinceUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"),
                lastCheck = NetWatch.LastCheckUtc?.ToLocalTime().ToString("MM.dd HH:mm:ss"), target = NetWatch.LastTarget, rtt = NetWatch.LastRttMs,
                noReply = NetWatch.LastNoReply,
                outages = NetWatch.Outages.AsEnumerable().Reverse().Take(20).Select(o => new
                {
                    start = o.StartUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm"), end = o.EndUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"),
                    minutes = Math.Max(1, (int)Math.Round(((o.EndUtc ?? DateTime.UtcNow) - o.StartUtc).TotalMinutes))
                })
            });
            return;
        case "/web/api/netwatch-save":
        case "/web/api/netwatch-test":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<NetWatchReq>(body) ?? new();
            if (NetWatch.Validate(r.Targets ?? new(), out var targets) is { } err) { await WriteJson(ctx, new { ok = false, error = err }); return; }
            if (path.EndsWith("test"))
            {
                if (targets.Count == 0) { await WriteJson(ctx, new { ok = false, error = "Adj meg legalább egy címet." }); return; }
                var res = await NetWatch.ProbeAll(targets);
                await WriteJson(ctx, new { ok = true, results = res.Select(x => new { target = x.Target, rtt = x.RttMs }) });
                return;
            }
            if (r.Enabled && targets.Count == 0) { await WriteJson(ctx, new { ok = false, error = "Adj meg legalább egy pingelendő címet." }); return; }
            config.NetWatchEnabled = r.Enabled; config.NetWatchTargets = targets; config.NetWatchMinutes = NetWatch.ClampMinutes(r.Minutes);
            config.Save(ServerPaths.DataDir);
            AuditLog.Write(ServerPaths.DataDir, user, "internetfigyelés", $"{(r.Enabled ? "be" : "ki")}, {config.NetWatchMinutes} perc: {string.Join(", ", targets)}");
            await WriteJson(ctx, new { ok = true });
            return;
        }

        // ---- Másolás külső eszközre (1.5.6): az USB-napló - személyes adat, ezért csak kezelő / admin
        // ---- 1.5.7: programkérések a tálcáról (jóváhagyási sor)
        case "/web/api/requests":
            await WriteJson(ctx, new
            {
                items = ProgramRequests.List().Take(500).Select(i => new
                {
                    id = i.Id, machine = i.Machine, user = i.User, packageId = i.PackageId, name = i.Name, reason = i.Reason, state = i.State,
                    note = i.Note, decidedBy = i.DecidedBy, created = i.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm"),
                    decided = i.DecidedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm")
                }),
                pending = ProgramRequests.PendingCount
            });
            return;
        case "/web/api/request-decide":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<RequestDecideReq>(body);
            if (r is null || r.Id.Length == 0) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            var err = ProgramRequests.Decide(store, r.Id, r.Approve, user, r.Note ?? "");
            await WriteJson(ctx, new { ok = err is null, error = err });
            return;
        }

        case "/web/api/cve":
        {
            // A gép sérülékenységei (a Központ párosítása): olvasásra bárki, aki a gépet látja.
            var r = ProtocolJson.Deserialize<UsbListReq>(body) ?? new();
            var c = CveCounts.Of(r.AgentId ?? "");
            await WriteJson(ctx, new
            {
                hub = config.HubUrl.Length > 0, checkedAt = CveCounts.CheckedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"),
                critical = c?.Critical ?? 0, high = c?.High ?? 0, medium = c?.Medium ?? 0, low = c?.Low ?? 0, exploited = c?.Exploited ?? 0,
                findings = c?.Findings ?? new()
            });
            return;
        }

        case "/web/api/usb-log":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { error = "Az USB-naplóhoz kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<UsbListReq>(body) ?? new();
            var (total, items) = UsbLog.List(r.AgentId ?? "", (r.Q ?? "").Trim(), r.Page);
            await WriteJson(ctx, new
            {
                total, size = 100,
                items = items.Select(e => new
                {
                    at = e.Event.AtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss"), machine = e.Machine, agentId = e.AgentId, kind = e.Event.Kind,
                    drive = e.Event.Drive, label = e.Event.Label, serial = e.Event.Serial, path = e.Event.Path, oldPath = e.Event.OldPath, size = e.Event.Size, user = e.Event.User
                })
            });
            return;
        }

        // ---- SNMP-trap fogadás: a lista mindenkinek, a beállítás csak adminnak (portot nyit, jelszavakat kezel)
        case "/web/api/snmp":
        {
            var e = SnmpReceiver.Engine;
            await WriteJson(ctx, new
            {
                enabled = config.SnmpEnabled, port = config.SnmpPort, allowFrom = config.SnmpAllowFrom, defaultAllow = SnmpReceiver.DefaultAllow,
                // A közösségnév a v1 / v2c „jelszava”: csak az admin látja.
                communities = role >= Role.Admin ? config.SnmpCommunities : null, communityCount = config.SnmpCommunities.Count,
                users = config.SnmpUsers.Select(u => new { name = u.Name, auth = u.Auth, priv = u.Priv, hasAuth = u.AuthKeyProtected.Length > 0, hasPriv = u.PrivKeyProtected.Length > 0 }),
                alertOids = config.SnmpAlertOids, engineId = config.SnmpEngineId, status = SnmpReceiver.Status, error = SnmpReceiver.Error,
                serverIp = ServerPaths.LanIp(), lastAt = SnmpReceiver.LastAtUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss"),
                authNames = Usm.AuthNames, privNames = Usm.PrivNames,
                counters = new
                {
                    received = SnmpReceiver.Received, accepted = SnmpReceiver.Accepted, rejected = SnmpReceiver.Rejected, notAllowed = SnmpReceiver.NotAllowed,
                    rateLimited = SnmpReceiver.RateLimited, tooBig = SnmpReceiver.TooBig, unknownCommunity = e?.UnknownCommunity ?? 0, unknownUser = e?.UnknownUser ?? 0,
                    wrongDigest = e?.WrongDigest ?? 0, decryptErrors = e?.DecryptErrors ?? 0, parseErrors = e?.ParseErrors ?? 0, notInTime = e?.NotInTime ?? 0,
                    replayed = e?.Replayed ?? 0, unsupportedLevel = e?.UnsupportedLevel ?? 0, discovery = e?.UnknownEngine ?? 0
                }
            });
            return;
        }
        case "/web/api/snmp-traps":
        {
            var r = ProtocolJson.Deserialize<SnmpListReq>(body) ?? new();
            var (total, items) = SnmpReceiver.List((r.Q ?? "").Trim(), r.Page);
            var admin = role >= Role.Admin;
            await WriteJson(ctx, new
            {
                total, page = r.Page, size = 100,
                items = items.Select(t => new
                {
                    id = t.Id, at = t.AtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss"), source = t.Source, version = t.Version,
                    security = t.Version == "v3" || admin ? t.Security : "közösségnév", inform = t.Inform, trapOid = t.TrapOid, trapName = t.TrapName,
                    uptime = t.Uptime, agentAddr = t.AgentAddr, alert = t.Alert, text = t.Text,
                    varBinds = t.VarBinds.Select(v => new { oid = v.Oid, name = SnmpEngine.Name(v.Oid), type = v.Type, value = v.Value })
                })
            });
            return;
        }
        case "/web/api/snmp-test":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var before = SnmpReceiver.Accepted;
            if (await SnmpReceiver.SendTest(config) is { } err) { await WriteJson(ctx, new { ok = false, error = err }); return; }
            for (int i = 0; i < 20 && SnmpReceiver.Accepted == before; i++) await Task.Delay(100);
            await WriteJson(ctx, SnmpReceiver.Accepted > before
                ? new { ok = true, error = (string?)null }
                : new { ok = false, error = (string?)"A próba-trap nem érkezett meg (a 127.0.0.1 a megengedett címek között van?)." });
            return;
        }
        case "/web/api/snmp-save":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<SnmpReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            if (r.Port is < 1 or > 65535) { await WriteJson(ctx, new { ok = false, error = "A port 1–65535 lehet." }); return; }
            var allow = (r.AllowFrom ?? new()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (allow.Count > 50) { await WriteJson(ctx, new { ok = false, error = "Legfeljebb 50 cím / tartomány." }); return; }
            if (allow.FirstOrDefault(x => !SnmpReceiver.TryNet(x, out _)) is { } badNet)
            { await WriteJson(ctx, new { ok = false, error = $"Érvénytelen cím vagy tartomány: {badNet} (pl. 192.168.1.0/24 vagy 10.0.0.5)" }); return; }
            var comms = (r.Communities ?? new()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (comms.Count > 20 || comms.Any(c => c.Length > 64 || c.Any(char.IsControl)))
            { await WriteJson(ctx, new { ok = false, error = "Közösségnév: legfeljebb 20 db, egyenként legfeljebb 64 karakter." }); return; }
            var oids = (r.AlertOids ?? new()).Select(x => x.Trim().TrimStart('.')).Where(x => x.Length > 0).Distinct().ToList();
            if (oids.Count > 50) { await WriteJson(ctx, new { ok = false, error = "Legfeljebb 50 riasztó OID." }); return; }
            if (oids.FirstOrDefault(o => o != "*" && !System.Text.RegularExpressions.Regex.IsMatch(o, @"^\d+(\.\d+){1,40}$")) is { } badOid)
            { await WriteJson(ctx, new { ok = false, error = $"Érvénytelen OID: {badOid} (pl. 1.3.6.1.6.3.1.1.5.3, vagy * = minden trap)" }); return; }
            config.SnmpEnabled = r.Enabled; config.SnmpPort = r.Port; config.SnmpAllowFrom = allow; config.SnmpCommunities = comms; config.SnmpAlertOids = oids;
            var applyErr = SnmpReceiver.Apply(config);
            config.Save(ServerPaths.DataDir);
            AuditLog.Write(ServerPaths.DataDir, user, "SNMP-fogadás beállítása", $"{(r.Enabled ? "be" : "ki")}, UDP {r.Port}, források: {(allow.Count > 0 ? string.Join(", ", allow) : "belső hálózatok")}, {comms.Count} közösségnév, riasztás: {string.Join(", ", oids)}");
            await WriteJson(ctx, new { ok = applyErr is null, error = applyErr });
            return;
        }
        case "/web/api/snmp-user-save":
        case "/web/api/snmp-user-delete":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<SnmpUserReq>(body);
            var name = (r?.Name ?? "").Trim();
            if (r is null || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9._@-]{1,32}$"))
            { await WriteJson(ctx, new { ok = false, error = "A felhasználónév 1–32 karakter: betű, szám, pont, kötőjel, aláhúzás, @." }); return; }
            var existing = config.SnmpUsers.FirstOrDefault(u => u.Name == name);
            if (path.EndsWith("delete"))
            {
                config.SnmpUsers.RemoveAll(u => u.Name == name);
                SnmpReceiver.Apply(config); config.Save(ServerPaths.DataDir);
                AuditLog.Write(ServerPaths.DataDir, user, "SNMPv3-felhasználó törlése", name);
                await WriteJson(ctx, new { ok = true }); return;
            }
            var auth = r.Auth.ToLowerInvariant(); var priv = auth == "none" ? "none" : r.Priv.ToLowerInvariant();
            if (!Usm.AuthNames.Contains(auth) || !Usm.PrivNames.Contains(priv)) { await WriteJson(ctx, new { ok = false, error = "Ismeretlen hitelesítési / titkosítási protokoll." }); return; }
            // A jelszó (RFC 3414) legalább 8 karakter. Meglevő felhasználónál üresen hagyva marad a régi.
            string? Key(string pw, string old, bool need, string what, out string? error)
            {
                error = null;
                if (!need) return "";
                if (pw.Length == 0) { if (old.Length > 0) return old; error = $"Add meg a {what} jelszót."; return null; }
                if (pw.Length is < 8 or > 128) { error = $"A {what} jelszó 8–128 karakter."; return null; }
                return NativeDpapi.Protect(pw);
            }
            var ak = Key(r.AuthPassword ?? "", auth == existing?.Auth ? existing?.AuthKeyProtected ?? "" : "", auth != "none", "hitelesítési", out var e1);
            var pk = Key(r.PrivPassword ?? "", priv == existing?.Priv && auth == existing?.Auth ? existing?.PrivKeyProtected ?? "" : "", priv != "none", "titkosítási", out var e2);
            if (e1 is not null || e2 is not null || ak is null || pk is null) { await WriteJson(ctx, new { ok = false, error = e1 ?? e2 }); return; }
            if (existing is null && config.SnmpUsers.Count >= 50) { await WriteJson(ctx, new { ok = false, error = "Legfeljebb 50 v3-felhasználó." }); return; }
            config.SnmpUsers.RemoveAll(u => u.Name == name);
            config.SnmpUsers.Add(new SnmpUserConfig { Name = name, Auth = auth, AuthKeyProtected = ak, Priv = priv, PrivKeyProtected = pk });
            var applyErr = SnmpReceiver.Apply(config);
            config.Save(ServerPaths.DataDir);
            AuditLog.Write(ServerPaths.DataDir, user, "SNMPv3-felhasználó mentése", $"{name} ({auth} / {priv})");
            await WriteJson(ctx, new { ok = true, error = applyErr });
            return;
        }

        // ---- tárolási helyek (admin): naplók, mentések, mérési adatbázis - áthelyezés átköltöztetéssel
        case "/web/api/storage":
            if (NeedAdmin()) { await WriteJson(ctx, new { error = "Ehhez admin jog kell." }); return; }
            await WriteJson(ctx, Storage.Info(ServerPaths.DataDir, config, Metrics));
            return;
        case "/web/api/storage-save":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<Storage.Req>(body) ?? new();
            var changes = new List<string>();
            var err = Storage.Save(ServerPaths.DataDir, config, Metrics, r, changes);
            config.Save(ServerPaths.DataDir);   // a sikeres részlépések (pl. a naplók már átköltöztek) akkor is megmaradnak
            if (changes.Count > 0)
            {
                AuditLog.Write(ServerPaths.DataDir, user, "tárolási helyek módosítása", string.Join("; ", changes));
                SrvLog.Info("Tárolás: " + string.Join("; ", changes));
            }
            await WriteJson(ctx, new { ok = err is null, error = err, changed = changes });
            return;
        }

        // ---- portok kézi állítása (admin): a régi agent-port az agentek átállásáig figyel, a web-port azonnal vált
        case "/web/api/ports":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { error = "Ehhez admin jog kell." }); return; }
            var (onNew, onOld, unknown) = PortStatus(store, config);
            await WriteJson(ctx, new
            {
                agentPort = config.AgentPort, webPort = config.WebPort, oldAgentPort = config.OldAgentPort,
                since = config.OldAgentPortSinceUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"),
                deadline = config.OldAgentPortSinceUtc?.AddDays(config.OldAgentPortDays).ToLocalTime().ToString("yyyy.MM.dd HH:mm"),
                days = config.OldAgentPortDays, onNew, onOld, unknown
            });
            return;
        }
        case "/web/api/port-save":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<PortReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            if (r.Days is int d) { config.OldAgentPortDays = Math.Clamp(d, 1, 365); config.Save(ServerPaths.DataDir); }
            var err = ChangePorts(config, r.AgentPort, r.WebPort, user, out var webChanged);
            if (err is not null) { await WriteJson(ctx, new { ok = false, error = err }); return; }
            var host = Uri.TryCreate("http://" + (ctx.Request.Headers["Host"] ?? "localhost"), UriKind.Absolute, out var hu) ? hu.Host : "localhost";
            await WriteJson(ctx, new { ok = true, webChanged, webUrl = webChanged ? $"{(ctx.Request.IsSecureConnection ? "https" : "http")}://{host}:{config.WebPort}/web" : null });
            return;
        }
        case "/web/api/port-close-old":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            CloseOldPort(config, user, "kézi lezárás");
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/alerts":
        {
            // Kuszob-szabalyok listaja + aktualisan aktiv riasztasok. Nezo is lathatja.
            var active = Alerts.ActiveStates();
            var all = Alerts.Rules;
            object Rule(AlertRule r, bool hub) => new
            {
                id = r.Id, name = r.Name, metricKey = r.MetricKey, op = (int)r.Op, opText = r.OpText,
                threshold = r.Threshold, agentId = r.AgentId, machine = r.AgentId.Length > 0 ? store.GetAgent(r.AgentId)?.MachineName ?? "?" : "",
                group = r.Group, forSamples = r.ForSamples, enabled = r.Enabled, hub, displayUnit = r.DisplayUnit,
                activeCount = active.Count(a => a.RuleId == r.Id),
                // 1.5.13: felküldve a Központba / a Központ szabálya váltja ki / a Központtól jött, de innen indult
                promoted = r.PromoteToken.Length > 0, superseded = !hub && Alerts.IsSuperseded(r.Id), fromHere = hub && r.OriginRuleId.Length > 0
            };
            var rules = all.Select(r => Rule(r, false));
            await WriteJson(ctx, new
            {
                rules, hubConnected = config.HubUrl.Length > 0,
                // A Központtól kapott szabályok: itt csak olvashatók, a helyiek mellett futnak.
                hubRules = Alerts.HubRules.Select(r => Rule(r, true)),
                active = active.OrderByDescending(a => a.SinceUtc).Select(a => new
                {
                    a.RuleId, a.AgentId, a.LastValue, since = a.SinceUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                    offline = a.RuleId == AlertStore.OfflineRuleId, metricKey = a.MetricKey,
                    name = a.RuleId == AlertStore.OfflineRuleId ? AlertStore.OfflineRuleName : Alerts.RuleName(a.RuleId),
                    machine = store.GetAgent(a.AgentId)?.MachineName ?? a.Name
                }),
                offlineMinutes = config.OfflineAlertMinutes, offlineGroups = config.OfflineAlertGroups,
                groups = GroupOps.All(store, config),
                machines = store.Agents.Where(a => !a.Revoked).OrderBy(a => a.MachineName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(a => new { agentId = a.AgentId, name = a.MachineName })
            });
            return;
        }

        case "/web/api/offline-save":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<OfflineReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            config.OfflineAlertMinutes = ServerConfig.ClampOfflineMinutes(r.Minutes);
            config.OfflineAlertGroups = (r.Groups ?? new()).Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            config.Save(ServerPaths.DataDir);
            CheckOffline(store);
            await WriteJson(ctx, new { ok = true, minutes = config.OfflineAlertMinutes });
            return;
        }

        case "/web/api/alert-save":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<AlertReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }
            // Ugyanaz az ellenőrzés, mint a Központból érkező szabálynál (SiteOps).
            var err = SiteOps.SaveLocalRule(store, Alerts, new SiteAlertRule
            {
                Id = r.Id ?? "", Name = r.Name ?? "", MetricKey = r.MetricKey ?? "", Op = r.Op, Threshold = r.Threshold, AgentId = r.AgentId ?? "",
                Group = r.Group ?? "", ForSamples = r.ForSamples, Enabled = r.Enabled, DisplayUnit = r.DisplayUnit ?? ""
            }, out var rule);
            if (err is not null) { await WriteJson(ctx, new { ok = false, error = err }); return; }
            AuditLog.Write(ServerPaths.DataDir, user, "küszöb mentése", $"{rule!.Name}: {rule.MetricKey} {rule.OpText} {rule.Threshold}");
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/alert-promote":
        {
            // 1.5.13: a helyi szabály felküldése a Központba – ott központi szabály lesz (először csak erre a telephelyre),
            // és a Központból a többi telephelyre is leküldhető. A helyi a Központ szabályának megérkezéséig fut tovább.
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            if (config.HubUrl.Length == 0) { await WriteJson(ctx, new { ok = false, error = "A szerver nincs Központhoz csatlakoztatva." }); return; }
            var r = ProtocolJson.Deserialize<AlertReq>(body);
            if (r?.Id is not { Length: > 0 } || r.Id.StartsWith(AlertStore.HubPrefix) || !Alerts.Promote(r.Id))
            { await WriteJson(ctx, new { ok = false, error = "Nincs ilyen helyi szabály." }); return; }
            AuditLog.Write(ServerPaths.DataDir, user, "küszöb felküldése a Központba", Alerts.RuleName(r.Id));
            HubClient.Instance.Wake();
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/alert-delete":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<AlertReq>(body);
            if (r?.Id is { Length: > 0 } && Alerts.Rules.FirstOrDefault(x => x.Id == r.Id) is { } old)
            {
                Alerts.Remove(old.Id);
                AuditLog.Write(ServerPaths.DataDir, user, "küszöb törlése", old.Name);
            }
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/alert-log":
        {
            await WriteJson(ctx, new { log = AlertLog.Tail(200) });
            return;
        }

        case "/web/api/task":
        {
            // Egy vagy tobb gepre (agentIds), vagy egy csoportra kiadott feladat.
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<TaskReq>(body);
            if (r is null) { await WriteJson(ctx, new { ok = false, error = "Hibás kérés." }); return; }

            var targets = store.Agents.Where(a => !a.Revoked &&
                ((r.AgentIds?.Contains(a.AgentId) ?? false) ||
                 (r.Group.Length > 0 && GroupOps.Of(a, config).Contains(r.Group, StringComparer.OrdinalIgnoreCase)))).ToList();
            if (targets.Count == 0) { await WriteJson(ctx, new { ok = false, error = "Nincs célgép." }); return; }

            var tasks = new List<AgentTask>();
            switch (r.Kind)
            {
                case "install":
                case "uninstall":
                    if (!WingetId.IsValid(r.Target)) { await WriteJson(ctx, new { ok = false, error = "Érvénytelen programazonosító." }); return; }
                    tasks.Add(new AgentTask { Kind = r.Kind == "install" ? AgentTaskKind.InstallApp : AgentTaskKind.UninstallApp, Target = r.Target });
                    break;
                case "installbase":
                    foreach (var c in config.SoftwareCatalog.Where(c => c.Base && WingetId.IsValid(c.Id)))
                        tasks.Add(new AgentTask { Kind = AgentTaskKind.InstallApp, Target = c.Id });
                    if (tasks.Count == 0) { await WriteJson(ctx, new { ok = false, error = "Az alapcsomag üres – a Programok menüben jelöld ki." }); return; }
                    break;
                case "installbundle":
                    foreach (var id in config.BundleApps(r.Target))
                        tasks.Add(new AgentTask { Kind = AgentTaskKind.InstallApp, Target = id });
                    if (tasks.Count == 0) { await WriteJson(ctx, new { ok = false, error = "A csomag üres vagy nem létezik." }); return; }
                    break;
                case "upgradeall": tasks.Add(new AgentTask { Kind = AgentTaskKind.UpgradeAll }); break;
                case "wuscan": tasks.Add(new AgentTask { Kind = AgentTaskKind.WindowsUpdateScan }); break;
                case "wuinstall":
                    var t = r.Target.Trim();
                    if (!t.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                        !t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(x => Guid.TryParse(x, out _)))
                    { await WriteJson(ctx, new { ok = false, error = "Érvénytelen frissítés-azonosító." }); return; }
                    tasks.Add(new AgentTask { Kind = AgentTaskKind.WindowsUpdateInstall, Target = t.Length > 0 ? t : "all" });
                    break;
                case "reboot":
                    var delay = int.TryParse(r.Target, out var d) ? Math.Clamp(d, 0, 3600) : 300;
                    tasks.Add(new AgentTask { Kind = AgentTaskKind.Reboot, Target = delay.ToString() });
                    break;
                case "rebootnow":   // 1.5.9: azonnali újraindítás (admin)
                    if (role < Role.Admin) { await WriteJson(ctx, new { ok = false, error = "Azonnali újraindításhoz admin jog kell." }); return; }
                    tasks.Add(new AgentTask { Kind = AgentTaskKind.RebootNow });
                    break;
                case "servicestart":   // 1.5.10: Windows-szolgáltatás kezelése (admin)
                case "servicestop":
                case "servicerestart":
                    if (role < Role.Admin) { await WriteJson(ctx, new { ok = false, error = "Szolgáltatás kezeléséhez admin jog kell." }); return; }
                    if (!ServiceName.IsValid(r.Target)) { await WriteJson(ctx, new { ok = false, error = "Érvénytelen szolgáltatásnév." }); return; }
                    tasks.Add(new AgentTask
                    {
                        Kind = r.Kind == "servicestart" ? AgentTaskKind.ServiceStart
                             : r.Kind == "servicestop" ? AgentTaskKind.ServiceStop
                             : AgentTaskKind.ServiceRestart,
                        Target = r.Target.Trim()
                    });
                    break;
                default: await WriteJson(ctx, new { ok = false, error = "Ismeretlen művelet." }); return;
            }

            foreach (var a in targets)
                foreach (var tk in tasks)
                    store.QueueTask(a.AgentId, new AgentTask { Kind = tk.Kind, Target = tk.Target });

            // 1.5.10: a szolgáltatás-kezelés (admin) a műveleti naplóba kerül.
            if (r.Kind is "servicestart" or "servicestop" or "servicerestart")
            {
                var verb = r.Kind == "servicestart" ? "szolgáltatás indítása"
                         : r.Kind == "servicestop" ? "szolgáltatás leállítása" : "szolgáltatás újraindítása";
                var who = targets.Count == 1 ? (targets[0].MachineName ?? targets[0].AgentId) : $"{targets.Count} gép";
                AuditLog.Write(ServerPaths.DataDir, user, verb, $"{r.Target} – {who}");
            }
            await WriteJson(ctx, new { ok = true, machines = targets.Count, tasks = tasks.Count });
            return;
        }

        // ---- 1.5.9: webes admin terminál (csak admin; PowerShell a gépen, LocalSystemként; alapból KI, gépenként engedélyezve)
        case "/web/api/terminal-enable":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<TerminalReq>(body);
            if (r?.AgentId is not { Length: > 0 } || store.GetAgent(r.AgentId) is null) { await WriteJson(ctx, new { ok = false, error = "Nincs ilyen gép." }); return; }
            config.TerminalAgents.RemoveAll(x => x == r.AgentId);
            if (r.On) config.TerminalAgents.Add(r.AgentId);
            config.Save(ServerPaths.DataDir);
            AuditLog.Write(ServerPaths.DataDir, user, r.On ? "terminál engedélyezve" : "terminál letiltva", store.GetAgent(r.AgentId)?.MachineName ?? r.AgentId);
            await WriteJson(ctx, new { ok = true, enabled = r.On });
            return;
        }
        case "/web/api/terminal":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<TerminalReq>(body);
            var a0 = r?.AgentId is { Length: > 0 } ? store.GetAgent(r.AgentId) : null;
            if (a0 is null) { await WriteJson(ctx, new { ok = false, error = "Nincs ilyen gép." }); return; }
            if (!config.TerminalAgents.Contains(r!.AgentId)) { await WriteJson(ctx, new { ok = false, error = "A terminál ezen a gépen nincs engedélyezve." }); return; }
            var cmd = (r.Command ?? "").Trim();
            if (cmd.Length == 0) { await WriteJson(ctx, new { ok = false, error = "Üres parancs." }); return; }
            if (cmd.Length > 8000) { await WriteJson(ctx, new { ok = false, error = "Túl hosszú parancs (max. 8000 karakter)." }); return; }
            var task = new AgentTask { Kind = AgentTaskKind.RunCommand, Target = cmd };
            store.QueueTask(r.AgentId, task);
            Terminal.Open(r.AgentId);
            AuditLog.Write(ServerPaths.DataDir, user, "terminál-parancs", $"{a0.MachineName}: {(cmd.Length > 200 ? cmd[..200] + "…" : cmd)}");
            await WriteJson(ctx, new { ok = true, taskId = task.TaskId, online = Terminal.Online(r.AgentId) });
            return;
        }
        case "/web/api/terminal-result":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<TerminalReq>(body);
            if (r?.AgentId is { Length: > 0 }) Terminal.Open(r.AgentId);   // amíg a böngésző vár az eredményre, tartsa gyorsan a betárcsázást
            var res = store.GetResult(r?.TaskId ?? "");
            if (res is null) { await WriteJson(ctx, new { ok = true, done = false, online = Terminal.Online(r?.AgentId ?? "") }); return; }
            await WriteJson(ctx, new { ok = true, done = true, success = res.Success, exit = res.ExitCode, output = res.Output });
            return;
        }

        case "/web/api/agent-groups":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<GroupReq>(body);
            // Automatikus csoport nevét kézzel nem lehet a gépre írni (a tagságát az operációs rendszer adja).
            await WriteJson(ctx, new { ok = r is not null && store.SetGroups(r.AgentId, (r.Groups ?? new()).Where(g => !config.AutoGroupsOn || !AutoGroups.IsAutoName(g))) });
            return;
        }

        case "/web/api/catalog":
            if (config.MigrateBaseToBundle()) config.Save(ServerPaths.DataDir);
            // A programok fejlesztővel és kategóriával (a csomag-szerkesztő és a telepítő-választó így csoportosít).
            await WriteJson(ctx, new { items = Winget.Describe(config.SoftwareCatalog), bundles = config.AppBundles,
                pendingCount = config.CatalogPending.Count, rejectedCount = config.CatalogRejected.Count + config.HubRejected.Count, winget = Winget.Status(), undecided = Winget.Undecided(config),
                // A Központ repója és csomagjai (a Központ a mester): itt csak olvashatók, a helyiek mellett telepíthetők.
                hubItems = Winget.Describe(config.HubCatalogItems), hubBundles = config.HubBundles, categories = WingetCatalog.CategoryNames });
            return;

        // ---- a teljes winget-tár: böngészés, döntés (repó / elutasítás), frissítés
        case "/web/api/winget-catalog":
            await WriteJson(ctx, Winget.Page(config, ProtocolJson.Deserialize<WingetCatalog.ListReq>(body) ?? new()));
            return;

        case "/web/api/catalog-category":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<CatCategoryReq>(body) ?? new();
            var ok = WingetCatalog.SetCategory(config, r.Id, r.Category);
            if (ok) config.Save(ServerPaths.DataDir);
            await WriteJson(ctx, new { ok, error = ok ? null : "Ez a program nincs a saját repóban." });
            return;
        }
        case "/web/api/catalog-decide":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<WingetCatalog.DecideReq>(body) ?? new();
            var n = Winget.Decide(config, r);
            config.Save(ServerPaths.DataDir);
            await WriteJson(ctx, new { ok = true, changed = n });
            return;
        }

        case "/web/api/winget-catalog-refresh":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            if (!Winget.Refreshing) _ = Task.Run(() => Winget.RefreshAndRecord(config, c => c.Save(ServerPaths.DataDir), SrvLog.Info));
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/catalog-save":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<CatalogReq>(body);
            config.SetCatalog(r?.Items, r?.Bundles);
            config.Save(ServerPaths.DataDir);
            await WriteJson(ctx, new { ok = true, count = config.SoftwareCatalog.Count, bundles = config.AppBundles.Count });
            return;
        }

        case "/web/api/inventory":
        {
            // A gepeken talalt, winget altal ismert (tehat telepitheto) programok - a programtar feltoltesehez.
            var apps = store.Agents.Where(a => !a.Revoked).SelectMany(a => a.LastStatus?.Installed ?? new())
                .Where(i => i.Source.Length > 0 && !i.Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) && WingetId.IsValid(i.Id))
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Id: g.Key, Name: g.GroupBy(x => x.Name).OrderByDescending(n => n.Count()).First().Key, Count: g.Count()))
                .Select(x => { var (pub, cat) = Winget.Meta(x.Id, x.Name); return new { id = x.Id, name = x.Name, count = x.Count, state = config.CatalogState(x.Id), publisher = pub, category = cat }; })
                .OrderBy(x => x.name, StringComparer.CurrentCultureIgnoreCase);
            await WriteJson(ctx, new { apps });
            return;
        }

        case "/web/api/app-search":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<SearchReq>(body);
            var (ok, hits, err) = await WingetSearch.RunAsync(r?.Q ?? "");
            await WriteJson(ctx, new { ok, error = err, results = hits.Select(h => new { id = h.Id, name = h.Name, version = h.Version }) });
            return;
        }

        case "/web/api/policies":
            await WriteJson(ctx, new { policies = config.AutoPolicies, hubPolicies = config.HubPolicies, groups = GroupOps.All(store, config) });
            return;

        case "/web/api/policy-save":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var p = ProtocolJson.Deserialize<AutoPolicy>(body);
            if (p is null || (!p.Apps && !p.Windows && !p.Reboot)) { await WriteJson(ctx, new { ok = false, error = "Válassz legalább egy műveletet." }); return; }
            if (p.Days.Count == 0 || p.StartHour is < 0 or > 23 || p.EndHour is < 0 or > 23 || p.StartHour == p.EndHour)
            { await WriteJson(ctx, new { ok = false, error = "Adj meg napokat és egy érvényes időablakot." }); return; }
            p.Days = p.Days.Where(d => d is >= 0 and <= 6).Distinct().ToList();
            if (p.Id.Length == 0) p.Id = Guid.NewGuid().ToString("N");
            p.Name = p.Name.Trim().Length > 0 ? p.Name.Trim() : "Szabály";
            config.AutoPolicies.RemoveAll(x => x.Id == p.Id);
            config.AutoPolicies.Add(p);
            config.Save(ServerPaths.DataDir);
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/policy-delete":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var p = ProtocolJson.Deserialize<AutoPolicy>(body);
            if (p is not null) { config.AutoPolicies.RemoveAll(x => x.Id == p.Id); config.Save(ServerPaths.DataDir); }
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/queue":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var req = ProtocolJson.Deserialize<QueueReq>(body);
            if (req is null || string.IsNullOrEmpty(req.AgentId)) { await WriteJson(ctx, new { ok = false }); return; }

            if (req.All)
                store.QueueTask(req.AgentId, new AgentTask { Kind = AgentTaskKind.UpgradeAll });
            else
                foreach (var id in req.Ids ?? new())
                    store.QueueTask(req.AgentId, new AgentTask { Kind = AgentTaskKind.UpgradeApp, Target = id });

            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/revoke":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var req = ProtocolJson.Deserialize<QueueReq>(body);
            if (req?.AgentId is { Length: > 0 }) store.RevokeAgent(req.AgentId);
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/hub-test":
        {
            // A Központ-kapcsolat tesztje a felületen beírt (még nem mentett) adatokkal - semmit nem ment, nem regisztrál.
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<HubSettingsReq>(body);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var (ok, steps) = await HubClient.Instance.TestAsync(r?.HubUrl ?? "", r?.HubFingerprint ?? "", cts.Token);
            await WriteJson(ctx, new { ok, steps });
            return;
        }

        case "/web/api/hub-dial":
        {
            // „Kapcsolódás most”: a betárcsázás azonnal indul; legfeljebb 30 mp-ig várunk az eredményre.
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            if (config.HubUrl.Length == 0) { await WriteJson(ctx, new { ok = false, error = "Nincs beállítva Központ." }); return; }
            var before = HubClient.Instance.Cycles;
            HubClient.Instance.Wake();
            for (int i = 0; i < 60 && HubClient.Instance.Cycles == before; i++) await Task.Delay(500);
            var done = HubClient.Instance.Cycles != before;
            AuditLog.Write(ServerPaths.DataDir, user, "Központ: kapcsolódás most");
            await WriteJson(ctx, new
            {
                ok = done && HubClient.Instance.LastError.Length == 0,
                error = !done ? "A betárcsázás még tart – nézd meg később." : HubClient.Instance.LastError.Length > 0 ? HubClient.Instance.LastError : null,
                lastContact = HubClient.Instance.LastContactUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss")
            });
            return;
        }

        // ---- 1.5.9: WireGuard-alagút a Központ felé (állapot: kezelő; beállítás / javítás: admin)
        case "/web/api/wg-status":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var st = SiteWg.HubStat();
            var o = config.WgOffer;
            await WriteJson(ctx, new
            {
                ok = true, enabled = config.WgEnabled, offered = o is not null, active = config.WgActive, running = SiteWg.Running,
                address = o is null ? "" : $"{o.SiteAddress}/{o.Prefix}", hubAddress = o?.HubAddress ?? "", hubUrl = o?.HubUrl ?? "",
                endpoint = SiteWg.Endpoint(config), endpointOverride = config.WgEndpoint, hubEndpoint = o?.Endpoint ?? "", required = o?.Required ?? false,
                publicKey = config.WgPublicKey, hubPublicKey = o?.HubPublicKey ?? "",
                handshake = st?.LastHandshakeUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss"), recent = SiteWg.HandshakeRecent,
                rx = (long)(st?.RxBytes ?? 0), tx = (long)(st?.TxBytes ?? 0), lastError = SiteWg.LastError
            });
            return;
        }
        case "/web/api/wg-save":
        case "/web/api/wg-repair":
        {
            if (NeedAdmin()) { await WriteJson(ctx, new { ok = false, error = "Ehhez admin jog kell." }); return; }
            var r = ProtocolJson.Deserialize<WgSiteReq>(body) ?? new();
            if (path.EndsWith("save"))
            {
                var ep = (r.Endpoint ?? "").Trim();
                if (ep.Length > 0 && (ep.Length > 200 || ep.Contains('/') || ep.Contains(' ') || !ep.Contains(':')))
                { await WriteJson(ctx, new { ok = false, error = "A végpont gépnév:port alakú legyen (pl. fleet.poltoli.hu:51820), vagy üres (a Központé)." }); return; }
                config.WgEndpoint = ep;
                config.Save(ServerPaths.DataDir);
                var err = SiteWg.Apply(config, ServerPaths.DataDir, forceResolve: true);
                AuditLog.Write(ServerPaths.DataDir, user, "WireGuard-végpont", ep.Length > 0 ? ep : "a Központé");
                await WriteJson(ctx, new { ok = err is null, error = err });
                return;
            }
            // Javítás: (új saját kulcs, ha kérik) közlés a Központtal HTTPS-en (aláírva), az ajánlat újra, újrakonfigurálás.
            var e2 = await HubClient.Instance.WgRepairAsync(config, ServerPaths.DataDir, r.NewKey, CancellationToken.None);
            AuditLog.Write(ServerPaths.DataDir, user, r.NewKey ? "WireGuard – új kulcs" : "WireGuard – újrakapcsolás", e2 ?? "rendben");
            await WriteJson(ctx, new { ok = e2 is null, error = e2 });
            return;
        }

        case "/web/api/hub-settings":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var req = ProtocolJson.Deserialize<HubSettingsReq>(body);
            if (req is not null)
            {
                var url = (req.HubUrl ?? "").Trim().TrimEnd('/');
                if (url.Length > 0 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJson(ctx, new { ok = false, error = "A Központ címe csak https:// lehet (publikus hálón titkosítás nélkül nem kapcsolódunk)." });
                    return;
                }
                var changed = url != config.HubUrl || (req.HubToken ?? "").Trim().Length > 0;
                config.HubUrl = url;
                config.HubSiteName = (req.HubSiteName ?? "").Trim();
                config.HubFingerprint = (req.HubFingerprint ?? "").Trim();
                if ((req.HubToken ?? "").Trim().Length > 0) config.HubToken = req.HubToken!.Trim();
                if (changed) { config.HubSiteId = ""; config.HubPublicKeyPem = ""; }   // uj regisztracio
                config.Save(ServerPaths.DataDir);
            }
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/settings":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var req = ProtocolJson.Deserialize<SettingsReq>(body);
            if (req is not null)
            {
                config.TokenLifetimeMinutes = Math.Clamp(req.TokenLifetimeMinutes, 0, 43200);
                config.TokenMaxUses = Math.Clamp(req.TokenMaxUses, 0, 10000);
                if (req.AgentAutoUpdate is bool au) config.AgentAutoUpdate = au;
                config.Save(ServerPaths.DataDir);
            }
            await WriteJson(ctx, new { ok = true });
            return;
        }

        case "/web/api/token":
        {
            if (NeedKezelo()) { await WriteJson(ctx, new { ok = false, error = "Ehhez kezelői vagy admin jog kell." }); return; }
            var req = ProtocolJson.Deserialize<TokenReq>(body);
            var label = req?.Label ?? "névtelen";
            // A lejarat/limit a beallitasokbol jon, de a keres feluldefinialhatja.
            var life = req?.LifetimeMinutes ?? config.TokenLifetimeMinutes;
            var uses = req?.MaxUses ?? config.TokenMaxUses;
            var token = store.CreateEnrollmentToken(label, life, uses);

            var baseUrl = ServerBaseUrl(ctx, config);

            await WriteJson(ctx, new
            {
                token,
                fingerprint = store.ServerKeys.Fingerprint,
                agentPort = config.AgentPort,
                label,
                lifetimeMinutes = life,
                maxUses = uses,
                installerPs = InstallerGen.PowerShell(baseUrl, config.AgentPort, token,
                    store.ServerKeys.Fingerprint, label),
                agentExeAvailable = AgentExePath() is not null,
                // A ZIP letoltesi URL-je: benne az exe + a TELEPIT.cmd a tokennel.
                zipUrl = AgentExePath() is not null
                    ? $"/web/agent-zip?token={Uri.EscapeDataString(token)}&label={Uri.EscapeDataString(label)}"
                    : null
            });
            return;
        }
    }

    ctx.Response.StatusCode = 404;
    await WriteJson(ctx, new { error = "ismeretlen" });
}

sealed class QueueReq { public string AgentId { get; set; } = ""; public bool All { get; set; } public List<string>? Ids { get; set; } }
sealed class TokenReq { public string Label { get; set; } = ""; public int? LifetimeMinutes { get; set; } public int? MaxUses { get; set; } }
sealed class HubSettingsReq { public string? HubUrl { get; set; } public string? HubToken { get; set; } public string? HubFingerprint { get; set; } public string? HubSiteName { get; set; } }
static string RoleName(Role r) => r switch { Role.Admin => "Adminisztrátor", Role.Kezelo => "Kezelő", _ => "Megfigyelő" };
sealed class UserReq { public string Username { get; set; } = ""; public string Password { get; set; } = ""; public int Role { get; set; } public bool Disabled { get; set; } }
sealed class TaskReq { public List<string>? AgentIds { get; set; } public string Group { get; set; } = ""; public string Kind { get; set; } = ""; public string Target { get; set; } = ""; }
sealed class GroupReq { public string AgentId { get; set; } = ""; public List<string>? Groups { get; set; } }
sealed class CatalogReq { public List<CatalogItem>? Items { get; set; } public List<AppBundle>? Bundles { get; set; } }
sealed class SearchReq { public string Q { get; set; } = ""; }
sealed class MetricReq { public string AgentId { get; set; } = ""; public string Key { get; set; } = ""; public int Hours { get; set; } }
sealed class AlertReq { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string MetricKey { get; set; } = ""; public int Op { get; set; } public double Threshold { get; set; } public string AgentId { get; set; } = ""; public int ForSamples { get; set; } = 1; public bool Enabled { get; set; } = true; public string Group { get; set; } = ""; public string? DisplayUnit { get; set; } }
sealed class WgSiteReq { public string? Endpoint { get; set; } public bool NewKey { get; set; } }
sealed class TerminalReq { public string AgentId { get; set; } = ""; public string? Command { get; set; } public string? TaskId { get; set; } public bool On { get; set; } }
sealed class RequestDecideReq { public string Id { get; set; } = ""; public bool Approve { get; set; } public string? Note { get; set; } }
sealed class GroupOpReq { public string Op { get; set; } = ""; public string Group { get; set; } = ""; public string NewName { get; set; } = ""; public List<string>? AgentIds { get; set; } }
sealed class AutoGroupsReq { public bool On { get; set; } }
sealed class NetWatchReq { public bool Enabled { get; set; } public List<string>? Targets { get; set; } public int Minutes { get; set; } = 5; }
sealed class SnmpReq { public bool Enabled { get; set; } public int Port { get; set; } = 162; public List<string>? AllowFrom { get; set; } public List<string>? Communities { get; set; } public List<string>? AlertOids { get; set; } }
sealed class SnmpUserReq { public string Name { get; set; } = ""; public string Auth { get; set; } = "none"; public string? AuthPassword { get; set; } public string Priv { get; set; } = "none"; public string? PrivPassword { get; set; } }
sealed class SnmpListReq { public string? Q { get; set; } public int Page { get; set; } }
sealed class CatCategoryReq { public string Id { get; set; } = ""; public string Category { get; set; } = ""; }
sealed class UsbListReq { public string? AgentId { get; set; } public string? Q { get; set; } public int Page { get; set; } }
sealed class PortReq { public int AgentPort { get; set; } public int WebPort { get; set; } public int? Days { get; set; } }
sealed class MonReq { public bool Cpu { get; set; } public bool Memory { get; set; } public bool Disk { get; set; } public bool DiskSmart { get; set; } public bool Temperature { get; set; } public bool EventLog { get; set; } public List<string>? Services { get; set; } public List<string>? Processes { get; set; } public int DetailDays { get; set; } = 7; public int KeepDays { get; set; } = 90; public bool Network { get; set; } = true; public bool DiskIo { get; set; } = true; public int SampleSeconds { get; set; } = 60; }
sealed class SettingsReq { public int TokenLifetimeMinutes { get; set; } public int TokenMaxUses { get; set; } public bool? AgentAutoUpdate { get; set; } }
sealed class OfflineReq { public int Minutes { get; set; } public List<string>? Groups { get; set; } }
sealed class KeySetReq { public int ServerDays { get; set; } = 7; public int PeerDays { get; set; } = 7; public string Id { get; set; } = ""; }

// ================================================================ HTTP segédek

/// <summary>A szerver bazis-URL-je abbol, ahogy a bongeszo elerte (Host fejlec).</summary>
public static string ServerBaseUrl(HttpListenerContext ctx, ServerConfig config)
{
    var host = ctx.Request.Headers["Host"];
    // Ha a webfelületet a szerveren "localhost"-ként nyitottad meg, a telepítőcsomagba NEM kerülhet
    // localhost: a többi gépen az önmagát jelentené, és csak a szerver saját agentje tudna csatlakozni.
    // Helyette a szerver belső IP-címe (a port marad).
    if (!string.IsNullOrEmpty(host) && Uri.TryCreate("http://" + host, UriKind.Absolute, out var hu) &&
        (hu.IsLoopback || hu.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) && ServerPaths.LanIp() is { } lan)
        host = $"{lan}:{hu.Port}";
    if (!string.IsNullOrEmpty(host))
    {
        // A Host tartalmazza a web-portot; a scheme-t a kapcsolatbol.
        var scheme = ctx.Request.IsSecureConnection ? "https" : "http";
        return $"{scheme}://{host}";
    }
    return $"http://localhost:{config.WebPort}";
}

/// <summary>
/// Az agent exe: alapból a szerver exéjébe beépített példány. Ha a szerver adatmappájában
/// vagy mellette ÚJABB verzió van (kézi gyorsjavítás), az nyer – a régi, ottfelejtett
/// DevToolboxAgent.exe viszont nem írhatja felül a beépítettet.
/// </summary>
static string? _lastAgentPick = "";

public static string? AgentExePath()
{
    var candidates = new[]
    {
        Path.Combine(DataDir, "DevToolboxAgent.exe"),
        Path.Combine(AppContext.BaseDirectory, "DevToolboxAgent.exe"),
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "DevToolboxAgent.exe")
    };
    string? file = null;
    foreach (var c in candidates)
        try { if (File.Exists(c)) { file = c; break; } } catch { }

    var embedded = EmbeddedAgent.Extract(DataDir);
    var pick = file is null ? embedded : embedded is null ? file
             : EmbeddedAgent.VersionOf(file) > EmbeddedAgent.VersionOf(embedded) ? file : embedded;
    // Melyik agentet adja a szerver (telepítőcsomag, önfrissítés) - változáskor naplózva, hogy kiderüljön, ha egy régi
    // kézi példány vagy a sikertelen kicsomagolás miatt nem a beépített megy ki.
    if (pick != _lastAgentPick) { _lastAgentPick = pick; SrvLog.Info($"Kiadott agent: {pick ?? "NINCS"} ({(pick is null ? "-" : EmbeddedAgent.VersionOf(pick).ToString())})"); }
    return pick;
}

public static async Task ServeAgentExe(HttpListenerContext ctx)
{
    var exe = AgentExePath();
    if (exe is null)
    {
        ctx.Response.StatusCode = 404;
        await WriteJson(ctx, new { error = "Ebben a szerverben nincs beépített agent. Másold a DevToolboxAgent.exe fájlt a szerver adatmappájába." });
        return;
    }

    try
    {
        var bytes = await File.ReadAllBytesAsync(exe);
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers["Content-Disposition"] = "attachment; filename=DevToolboxAgent.exe";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 500;
        await WriteJson(ctx, new { error = ex.Message });
    }
}

public static async Task HandleServerUpload(HttpListenerContext ctx)
{
    try
    {
        var installDir = ServerPaths.InstallDir;
        Directory.CreateDirectory(installDir);
        var target = Path.Combine(installDir, "FleetServer.new.exe");

        // A feltoltott exe eltarolasa .new nevvel - a futo exét nem lehet felulirni,
        // ezert a csere a kovetkezo indulaskor tortenik (lasd ApplyPendingUpdate).
        await using (var f = File.Create(target))
            await ctx.Request.InputStream.CopyToAsync(f);

        // Alap ellenorzes: PE-fajl (MZ fejlec) es ertelmes meret.
        var ok = new FileInfo(target).Length > 1_000_000;
        byte[] head = new byte[2];
        await using (var r = File.OpenRead(target)) { _ = r.Read(head, 0, 2); }
        ok &= head[0] == (byte)'M' && head[1] == (byte)'Z';

        if (!ok)
        {
            try { File.Delete(target); } catch { }
            ctx.Response.StatusCode = 400;
            await WriteJson(ctx, new { ok = false, error = "A feltöltött fájl nem érvényes Windows exe." });
            return;
        }

        // Szolgaltataskent azonnal indul a csere: az uj exe 2 mp mulva leallitja, kicserel, ujrainditja
        // (es ha nem indul el, visszaallitja a regit). Kezi futtatasnal marad a "kovetkezo inditaskor".
        var now = !Environment.UserInteractive && ServerPaths.ApplyPendingUpdate(ServerInstaller.ServiceName);
        await WriteJson(ctx, new { ok = true, message = now
            ? "Feltöltve – a frissítés elindult, a szerver néhány másodperc múlva újraindul (frissítsd az oldalt)."
            : "Feltöltve. A szerver újraindításakor lép életbe." });
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 500;
        await WriteJson(ctx, new { ok = false, error = ex.Message });
    }
}

public static Dictionary<string, string> ParseQuery(string query)
{
    var d = new Dictionary<string, string>();
    foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var i = part.IndexOf('=');
        if (i < 0) continue;
        d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
    }
    return d;
}

public static async Task ServeReport(HttpListenerContext ctx, FleetStore store, string user)
{
    var q = ParseQuery(ctx.Request.Url?.Query ?? "");
    var kind = q.GetValueOrDefault("r", "");
    var days = int.TryParse(q.GetValueOrDefault("napok", ""), out var nd) ? Math.Clamp(nd, 1, 3650) : 7;
    store.ProcessInbox();
    var agents = store.Agents.ToList();
    var machines = agents.Select(a => new CsvReport.Machine("", a.AgentId, a.MachineName, a.LastStatus?.OperatingSystem ?? a.OperatingSystem,
        a.AgentVersion, a.IsOnline, a.Revoked, a.LastSeenUtc, a.Groups, a.LastStatus?.Updates ?? new(), a.LastStatus?.WindowsUpdates ?? new(),
        a.LastStatus?.Installed ?? new(), a.LastStatus?.PendingReboot ?? false, a.LastStatus?.WuScannedUtc, a.EnrolledUtc)).ToList();
    byte[]? data;
    try
    {
        data = kind switch
        {
            "gepek" => CsvReport.Machines(machines, false),
            "programok" => CsvReport.Programs(machines, false),
            "frissitesek" => CsvReport.Updates(machines, false),
            "riasztasok" => CsvReport.Alerts(AlertLog.Entries(), false),
            "metrikak" => CsvReport.Metrics(Metrics.Daily(days), agents.ToDictionary(a => a.AgentId, a => ("", a.MachineName)), false),
            _ => null
        };
    }
    catch (Exception ex) { ctx.Response.StatusCode = 500; await WriteJson(ctx, new { error = "A jelentés nem készült el: " + ex.Message }); return; }
    if (data is null) { ctx.Response.StatusCode = 404; await WriteJson(ctx, new { error = "ismeretlen jelentés" }); return; }

    AuditLog.Write(DataDir, user, "jelentés letöltése", kind + (kind == "metrikak" ? $" ({days} nap)" : ""));
    ctx.Response.ContentType = "text/csv; charset=utf-8";
    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename={CsvReport.FileName("fleet", kind)}";
    ctx.Response.ContentLength64 = data.Length;
    await ctx.Response.OutputStream.WriteAsync(data);
}

public static async Task ServeAgentZip(HttpListenerContext ctx, ServerConfig config, FleetStore store)
{
    var exe = AgentExePath();
    var q = ParseQuery(ctx.Request.Url?.Query ?? "");
    var token = q.GetValueOrDefault("token", "");
    var label = q.GetValueOrDefault("label", "agent");

    if (exe is null)
    {
        ctx.Response.StatusCode = 404;
        await WriteJson(ctx, new { error = "Ebben a szerverben nincs beépített agent." });
        return;
    }

    var bytes = AgentZip.Build(exe, InstallerGen.AgentUrl(ServerBaseUrl(ctx, config), config.AgentPort),
        token, store.ServerKeys.Fingerprint, label);
    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers["Content-Disposition"] = "attachment; filename=DevToolboxAgent-telepito.zip";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
}

public static async Task<string> ReadBody(HttpListenerContext ctx)
{
    if (!ctx.Request.HasEntityBody) return "";
    using var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
    return await r.ReadToEndAsync();
}

public static async Task<Dictionary<string, string>> ReadForm(HttpListenerContext ctx)
{
    var body = await ReadBody(ctx);
    var dict = new Dictionary<string, string>();
    foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var i = pair.IndexOf('=');
        if (i < 0) continue;
        var k = Uri.UnescapeDataString(pair[..i].Replace('+', ' '));
        var v = Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
        dict[k] = v;
    }
    return dict;
}

public static async Task WriteJson(HttpListenerContext ctx, object payload)
{
    var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(payload));
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
}

public static async Task WriteHtml(HttpListenerContext ctx, string html)
{
    var bytes = Encoding.UTF8.GetBytes(html);
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
}

public static void Redirect(HttpListenerContext ctx, string url)
{
    ctx.Response.StatusCode = 302;
    ctx.Response.Headers["Location"] = url;
}

public static void SetCookie(HttpListenerContext ctx, string name, string value)
{
    // HttpOnly: JS nem fér hozzá. SameSite=Strict: CSRF ellen.
    ctx.Response.Headers["Set-Cookie"] =
        $"{name}={Uri.EscapeDataString(value)}; Path=/; HttpOnly; SameSite=Strict";
}

public static void ClearCookie(HttpListenerContext ctx) =>
    ctx.Response.Headers["Set-Cookie"] = "fleet_session=; Path=/; HttpOnly; Max-Age=0";

public static string? GetCookie(HttpListenerContext ctx, string name)
{
    var header = ctx.Request.Headers["Cookie"];
    if (header is null) return null;
    foreach (var part in header.Split(';'))
    {
        var kv = part.Trim().Split('=', 2);
        if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1]);
    }
    return null;
}



}

/// <summary>
/// Az agentek frissülései a telephelyen (gépenként a legutóbbi: honnan, hová, mikor) - a felület ebből mutatja, hogy egy
/// kért vagy automatikus frissítés megtörtént. Kis fájl az adatmappában (agent-frissitesek.json).
/// </summary>
static class AgentUpdates
{
    public sealed class Item { public string From { get; set; } = ""; public string To { get; set; } = ""; public DateTime AtUtc { get; set; } }
    private static readonly object Lock = new();
    private static Dictionary<string, Item>? _items;
    private static string FilePath => Path.Combine(ServerPaths.DataDir, "agent-frissitesek.json");

    private static Dictionary<string, Item> Items()
    {
        if (_items is not null) return _items;
        try { _items = File.Exists(FilePath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Item>>(File.ReadAllText(FilePath)) : null; } catch { }
        return _items ??= new();
    }

    public static void Record(string agentId, string from, string to)
    {
        lock (Lock)
        {
            Items()[agentId] = new Item { From = from, To = to, AtUtc = DateTime.UtcNow };
            try { File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(_items)); } catch { }
        }
    }

    public static Item? Last(string agentId) { lock (Lock) return Items().GetValueOrDefault(agentId); }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class FleetServerService : System.ServiceProcess.ServiceBase
{
    private Thread? _thread;
    public FleetServerService() { ServiceName = DevToolbox.Fleet.Server.ServerInstaller.ServiceName; }
    protected override void OnStart(string[] args)
    {
        // Ha van feltoltott frissites, a cserét az uj exe vegzi kulon folyamatban, es ez a
        // peldany leall (az uj exe ujrainditja). Igy a futo exe felulirhato.
        if (ServerPaths.ApplyPendingUpdate(ServiceName)) { Stop(); return; }

        _thread = new Thread(ServerHost.RunServer) { IsBackground = true };
        _thread.Start();
    }
    protected override void OnStop()
    {
        // A listener-ek leallitasa, hogy a Task.WaitAll visszaterjen es a szolgaltatas
        // tisztan alljon le (kulonben a Windows kenyszerbol oli meg).
        try
        {
            if (ServerHost.RunningListeners is { } ls)
                foreach (var l in ls) try { l.Stop(); } catch { }
        }
        catch { }
    }
}
