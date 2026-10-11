using System.Net;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;

// A belepesi pont a kozos Fleet exe-ben van (agent/Fleet/FleetMain.cs); ez a Kozpont szerepe.
static class HubEntry
{
    public static void Run(string[] args)
    {
        // A naplók / mentések helye a beállításból (1.5.4) - a parancssori módok is oda írnak; költöztetni csak a szolgáltatás indulása fog.
        Storage.PointOnly(HubPaths.DataDir);
        #if GUI_SETUP
        if ((args.Length == 0 && Environment.UserInteractive) || (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase)))
        {
            WizardForm.FreeConsole();
            ServiceWizard.OnSta(() =>
            {
                var cfg0 = ServerConfig.Load(HubPaths.DataDir);
                ServiceWizard.Run("Fleet HUB", HubPaths.Version, HubPaths.ServiceName,
                    "A Központ fogadja a telephelyi Fleet Serverek betárcsázását, és egy felületen mutatja az összes ügyfelet.",
                    new[] { new ServiceWizard.PortField("Port (admin felület + telephelyek /hub/)", HubProgram.Port(cfg0), "Alapból 8090. Az NGINX Proxy Manager erre irányít.") },
                    v => { var c = ServerConfig.Load(HubPaths.DataDir); c.WebPort = v[0]; c.Save(HubPaths.DataDir); },
                    HubInstaller.Install,
                    v => $"http://localhost:{v[0]}/web",
                    "Az NGINX Proxy Manager beállítási útmutatóját a webes beállítás (domain megadása) után itt találod:\n" + HubPaths.CaddyDir + "\\NPM-beallitas.txt");
            });
            return;
        }
        #endif
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "install": HubInstaller.Install(); return;
                case "uninstall": HubInstaller.Uninstall(); return;
                case "reinstall": HubInstaller.Reinstall(); return;
                case "run": HubProgram.Run(); return;
                case "diag": HubDiag.RunAsync().GetAwaiter().GetResult(); return;
                // A feltoltott uj exe (FleetHub.new.exe) ebben a modban cserel - lasd ServiceSelfUpdate.
                case "apply-update": Environment.ExitCode = ServiceSelfUpdate.Apply(args, HubLog.Write); return;
            }
        }

        if (!Environment.UserInteractive)
        {
            System.ServiceProcess.ServiceBase.Run(new HubService());
            return;
        }
        HubProgram.Run();
    }
}

// ============================================================================

static class HubProgram
{
    public static int Port(ServerConfig c) => c.WebPort == 0 ? 8090 : c.WebPort;

    private static HubStore _store = null!;
    private static ServerConfig _config = null!;
    private static RoleSession _sessions = null!;
    private static UserStore _users = null!;
    private static MetricStore _metrics = null!;
    private static AlertStore _alerts = null!;
    private static WingetCatalog _winget = null!;
    public static HttpListener[]? Listeners;

    /// <summary>
    /// Egy telephely metrika-atlagainak tarolasa es a kuszobok kiertekelese. Csak a telephely SAJAT
    /// gepeire fogadunk adatot (a legutobbi osszesito szerint) - egy telephely nem irhat mas gepere.
    /// </summary>
    /// <summary>A gép tényleges csoportjai a Központban: a telephelyről jött kézi csoportok + az automatikusak (OS, virtuális-e).</summary>
    private static List<string> GroupsOf(SiteAgent a) => AutoGroups.Effective(a.Groups, a.OperatingSystem, a.IsVirtual, _config.AutoGroupsOn);

    /// <summary>A telephely automatikus csoportjai (amelyekben van gép), a kézi csoportokkal azonos nevűek nélkül.</summary>
    private static List<string> AutoGroupsOf(HubSite s, IEnumerable<string> manual)
    {
        if (!_config.AutoGroupsOn) return new();
        var m = manual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (s.LastSummary?.Agents ?? new()).Where(a => !a.Revoked).SelectMany(a => AutoGroups.For(a.OperatingSystem, a.IsVirtual))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(g => !m.Contains(g))
            .OrderBy(GroupOps.AutoOrder).ThenBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void StoreMetrics(HubSite site, List<SiteMetricRow> rows)
    {
        var names = new Dictionary<string, string>();
        var groups = new Dictionary<string, List<string>>();
        foreach (var a in site.LastSummary?.Agents ?? new()) { names[a.AgentId] = a.MachineName; groups[a.AgentId] = GroupsOf(a); }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var g in rows.Where(r => names.ContainsKey(r.AgentId) && r.Ts > now - 30 * 86400 && r.Ts <= now + 300)
                              .GroupBy(r => r.AgentId))
        {
            var batch = g.GroupBy(r => r.Ts).OrderBy(x => x.Key)
                .Select(x => (DateTimeOffset.FromUnixTimeSeconds(x.Key).UtcDateTime,
                              (IReadOnlyList<Metric>)x.Select(r => new Metric { Key = r.Key, Value = r.Value, Category = r.Category, Label = r.Label, Unit = r.Unit }).ToList()))
                .ToList();
            _metrics.InsertMany(g.Key, batch);
            foreach (var (_, ms) in batch)
                foreach (var ev in _alerts.Evaluate(g.Key, $"{site.Name} / {names[g.Key]}", ms, groups[g.Key], site.SiteId))
                    AlertLog.Write(ev);
        }
        // 1.5.8: a Központ saját napi fájljai és a telephelyekről feltöltöttek: MonKeepDays-ig olvasható, utána ZIP-archívum
        // a MetricArchiveDays-ig, majd törlés (a felhasználó kérése: alap 90 / 180 nap).
        _metrics.ApplyRetention(_config.MonDetailDays, _config.MonKeepDays, _config.MetricArchiveDays);
        HubMetricArchive.Maintain(_config.MonKeepDays, _config.MetricArchiveDays);
    }

    /// <summary>A kiesés-figyelésben a telephely (a Fleet szervere) kulcsa és neve - a név a riasztásnaplóban
    /// „Telephely / …” alakú, hogy a telephely-hatókörös szűrés ugyanúgy működjön, mint a gépeknél.</summary>
    private static string SiteKey(HubSite s) => "site:" + s.SiteId;
    private static string SiteLabel(HubSite s) => s.Name + " / Fleet szerver";

    /// <summary>
    /// Kiesés: a telephely nem tárcsázott be, illetve (ha be van kapcsolva) a telephely egy gépe nem jelentkezett.
    /// A telephely küszöbe legalább a betárcsázási időköz háromszorosa (ennyi alatt számít online-nak).
    /// Kiesett telephely gépeiről nincs friss adat - az állapotuk marad, amíg a telephely vissza nem tér.
    /// </summary>
    private static void CheckOffline()
    {
        var items = new List<OfflineCheck.Item>();
        if (_config.OfflineAlertMinutes > 0)
        {
            var limit = TimeSpan.FromMinutes(_config.OfflineAlertMinutes);
            foreach (var s in _store.Sites.Where(s => !s.Revoked))
            {
                var siteLimit = TimeSpan.FromSeconds(Math.Max(limit.TotalSeconds, Math.Max(s.IntervalSeconds, 60) * 3));
                items.Add(new(SiteKey(s), SiteLabel(s), s.LastSeenUtc, siteLimit));
                if (!_config.OfflineAlertMachines) continue;
                var siteDown = s.LastSeenUtc is not { } ls || DateTime.UtcNow - ls >= siteLimit;
                foreach (var a in s.LastSummary?.Agents ?? new())
                    if (!a.Revoked && OfflineCheck.Watched(_config.OfflineAlertGroups, GroupsOf(a)))
                        items.Add(new(a.AgentId, $"{s.Name} / {a.MachineName}", s.AgentLastSeenHubUtc(a), limit, Freeze: siteDown));
            }
        }
        foreach (var ev in OfflineCheck.Run(_alerts, items)) AlertLog.Write(ev);
    }

    /// <summary>
    /// A Központ kulcsának automatikus cseréje (alapból hetente), láncolt átadással: a telephelyek a következő
    /// betárcsázáskor ellenőrzötten átállnak. Erős (P-384) kulcs csak ha minden telephely tudja ellenőrizni.
    /// </summary>
    private static async Task HubKeyLoop()
    {
        await Task.Delay(TimeSpan.FromMinutes(5));
        while (true)
        {
            try
            {
                var days = _config.ServerKeyRotationDays;
                if (days > 0 && DateTime.UtcNow - _store.KeyCreatedUtc >= TimeSpan.FromDays(days))
                {
                    _store.RotateKeys($"automatikus ({days} naponta)");
                    AuditLog.Write(HubPaths.DataDir, "rendszer", "automatikus kulcscsere", $"új Központ-kulcs {_store.Keys.Fingerprint[..16]}… ({Crypto.Algorithm(_store.Keys.PublicKeyPem)})");
                    HubLog.Write($"Automatikus kulcscsere: {Crypto.Algorithm(_store.Keys.PublicKeyPem)}, új ujjlenyomat {_store.Keys.Fingerprint}");
                }
            }
            catch (Exception ex) { HubLog.Write("Automatikus kulcscsere: " + ex.Message); }
            await Task.Delay(TimeSpan.FromHours(1));
        }
    }

    private static async Task OfflineLoop()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            try { CheckOffline(); } catch (Exception ex) { HubLog.Write("Kiesés-figyelés: " + ex.Message); }
        }
    }

    /// <summary>A felhasznalo hatokorebe tartozik-e a telephely (a telephely-hatokorrel korlatozott felhasznaloknal).</summary>
    private static bool CanTouch(string? u, string siteId) { var me = _users.Find(u ?? ""); return me is null || me.CanSeeSite(siteId); }

    // ---- Szerverfrissítés a Központból: a Központ a SAJÁT exéjét ajánlja (ugyanaz a Fleet.exe fut mindkét szerepben).
    // A letöltéshez rövid életű azonosító kell (az aláírt válaszban megy le) - a /hub/ útvonal kívülről is elérhető.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _dlTokens = new();
    private static readonly Lazy<(string Path, string Sha, long Size)?> _selfExe = new(() =>
    {
        try
        {
            var p = Environment.ProcessPath ?? "";
            using var fs = File.OpenRead(p);
            return (p, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)), new FileInfo(p).Length);
        }
        catch { return null; }
    });

    private static HubServerUpdate? OfferServerUpdate(HubSite site)
    {
        if (_selfExe.Value is not { } me) return null;
        if (Version.TryParse(site.ServerVersion, out var sv) && Version.TryParse(HubPaths.Version, out var hv) && sv >= hv) return null;
        foreach (var kv in _dlTokens) if (kv.Value < DateTime.UtcNow) _dlTokens.TryRemove(kv.Key, out _);
        var t = Crypto.NewNonce();
        _dlTokens[t] = DateTime.UtcNow.AddMinutes(30);
        return new HubServerUpdate { Version = HubPaths.Version, Sha256 = me.Sha, Size = me.Size, DownloadToken = t };
    }

    private static async Task ServeFleetExe(HttpListenerContext ctx)
    {
        var t = ctx.Request.QueryString["t"] ?? "";
        if (t.Length == 0 || !_dlTokens.TryGetValue(t, out var exp) || exp < DateTime.UtcNow || _selfExe.Value is not { } me)
        { ctx.Response.StatusCode = 404; await Json(ctx, new { error = "nincs ilyen letöltés (lejárt?)" }); return; }
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength64 = me.Size;
        await using var fs = File.OpenRead(me.Path);
        await fs.CopyToAsync(ctx.Response.OutputStream);
    }

    // A programtár lenyomata csak változáskor számolódik újra (minden betárcsázás kéri).
    private static (string Json, HubCatalog Cat)? _catCache;
    private static HubCatalog HubCatalogNow()
    {
        lock (ServerConfig.CatalogLock)
        {
            var json = ProtocolJson.Serialize(new { _config.SoftwareCatalog, _config.AppBundles, _config.CatalogRejected });
            if (_catCache is { } c && c.Json == json) return c.Cat;
            var cat = new HubCatalog
            {
                Items = _config.SoftwareCatalog.Select(i => new CatalogItem { Id = i.Id, Name = i.Name }).ToList(),
                Bundles = _config.AppBundles.Select(b => new AppBundle { Id = b.Id, Name = b.Name, Apps = b.Apps.ToList() }).ToList(),
                Rejected = _config.CatalogRejected.Select(i => new CatalogItem { Id = i.Id, Name = i.Name }).ToList()
            };
            cat.Hash = ServerConfig.CatalogHash(cat.Items, cat.Bundles, cat.Rejected);
            _catCache = (json, cat);
            return cat;
        }
    }

    /// <summary>
    /// A telephelyre leküldendő küszöb-szabályok: a leküldésre jelöltek, amelyek hatókörébe a telephely beletartozik
    /// (gépre szóló szabály csak a gép telephelyére megy).
    /// </summary>
    private static List<SiteAlertRule> AlertRulesFor(HubSite s) =>
        _alerts.Rules.Where(r => r.PushDown && (r.Sites.Count == 0 || r.Sites.Contains(s.SiteId)) &&
                                 (r.AgentId.Length == 0 || s.LastSummary?.Agents.Any(a => a.AgentId == r.AgentId) == true))
            .Select(r => new SiteAlertRule
            {
                Id = r.Id, Name = r.Name, MetricKey = r.MetricKey, Op = (int)r.Op, Threshold = r.Threshold, Group = r.Group,
                AgentId = r.AgentId, ForSamples = r.ForSamples, Enabled = r.Enabled, DisplayUnit = r.DisplayUnit,
                OriginRuleId = r.OriginSite == s.SiteId ? r.OriginRuleId : ""
            }).ToList();

    /// <summary>
    /// 1.5.13: egy telephely helyi szabályából központi szabály. Biztonsági alapállás: csak az adott telephelyre szól, és
    /// csak a Központban fut (nem megy le) – a többi telephelyre a Központ kezelője terjeszti ki (Telephelyek, „Leküldés”).
    /// A telephelyről jött kérés (token) csak egyszer kerül át, akkor sem, ha a központi szabályt később törlik.
    /// by: a Központ felhasználója (a Központ felületéről), üres = a telephely kérése. Visszaadja a hibát, vagy null-t.
    /// </summary>
    private static string? PromoteFromSite(HubSite site, SiteAlertRule r, string by, out AlertRule? created)
    {
        created = null;
        var key = (r.MetricKey ?? "").Trim();
        if (r.Id.Length is 0 or > 64 || !AlertRule.ValidKey(key) || !double.IsFinite(r.Threshold)) return "Hibás szabály.";
        lock (_promoteLock)
        {
            if (by.Length == 0)
            {
                var mark = $"{site.SiteId}|{r.Id}|{r.PromoteToken}";
                if (r.PromoteToken.Length is 0 or > 64 || _config.HubPromotedRules.Contains(mark)) return null;
                _config.HubPromotedRules.Add(mark);
                if (_config.HubPromotedRules.Count > 2000) _config.HubPromotedRules.RemoveRange(0, _config.HubPromotedRules.Count - 2000);
                _config.Save(HubPaths.DataDir);
            }
            if (_alerts.Rules.FirstOrDefault(x => x.OriginSite == site.SiteId && x.OriginRuleId == r.Id) is { } have)
                return by.Length > 0 ? $"Ebből már van központi szabály: {have.Name}" : null;
            var agentId = (r.AgentId ?? "").Trim();
            if (agentId.Length > 0 && !SiteHasAgent(site.SiteId, agentId)) agentId = "";   // idegen gép: ne szűkítsen máshová
            var name = (r.Name ?? "").Trim();
            created = new AlertRule
            {
                Name = name.Length == 0 ? key : name.Length > 120 ? name[..120] : name,
                MetricKey = key, Op = (AlertOp)Math.Clamp(r.Op, 0, 2), Threshold = r.Threshold, AgentId = agentId,
                Group = agentId.Length > 0 ? "" : GroupOps.Clean(r.Group), ForSamples = Math.Clamp(r.ForSamples, 1, 60), Enabled = r.Enabled,
                DisplayUnit = AlertRule.CleanUnit(r.DisplayUnit), Sites = agentId.Length > 0 ? new() : new() { site.SiteId }, PushDown = false,
                OriginSite = site.SiteId, OriginRuleId = r.Id
            };
            _alerts.Add(created);
        }
        HubLog.Write($"Küszöb-szabály a telephelyről ({site.Name}): {created.Name} – központi szabály lett");
        AuditLog.Write(HubPaths.DataDir, by.Length > 0 ? by : "telephely: " + site.Name, "küszöb felküldése központi szabálynak", $"{site.Name}: {created.Name}");
        return null;
    }
    private static readonly object _promoteLock = new();

    private static bool SiteHasAgent(string siteId, string agentId) =>
        _store.Sites.FirstOrDefault(s => s.SiteId == siteId)?.LastSummary?.Agents.Any(a => a.AgentId == agentId) == true;

    /// <summary>A felhasznalo altal lathato gepek: agentId -> "Telephely / Gep".</summary>
    private static Dictionary<string, string> VisibleMachines(string? user)
    {
        var me = _users.Find(user ?? "");
        var d = new Dictionary<string, string>();
        foreach (var s in _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId)))
            foreach (var a in s.LastSummary?.Agents ?? new()) d[a.AgentId] = $"{s.Name} / {a.MachineName}";
        return d;
    }

    public static void Run()
    {
        var dir = HubPaths.DataDir;
        // 1.5.4: a naplók és a mentések helye a beállításból (a régi, gyökérbeli naplók átköltöznek logs\ alá) - minden más előtt.
        var storageNotes = Storage.ApplyAtStartup(dir, ServerConfig.Load(dir), secure: Environment.IsPrivilegedProcess);
        foreach (var n in storageNotes) HubLog.Write("FIGY " + n);
        DataBackup.OnStartup(dir, HubPaths.Version);
        _store = new HubStore(dir);
        _config = ServerConfig.Load(dir);
        if (_config.WebPort == 8080) _config.WebPort = 8090;   // alapertelmezett elteres a FleetServertol
        if (_config.AgentPort == 8443) _config.AgentPort = 8453;
        _config.EnsureSessionKey();
        _config.Save(dir);
        _users = new UserStore(dir);
        _sessions = new RoleSession(_config.SessionKey);

        // Monitorozas: a telephelyekrol erkezo 5 perces atlagok idosorban + a Kozpont sajat kuszobei.
        var metNotes = new List<string>();
        _metrics = new MetricStore(Storage.MetricsDirAtStartup(dir, _config, metNotes));
        foreach (var n in metNotes) HubLog.Write("FIGY " + n);
        _alerts = new AlertStore(dir);
        AlertLog.Dir = dir;
        // E-mail értesítés (csak a Központ küld): minden riasztásnapló-esemény a levélsorba megy.
        // A riasztások a Janusba is mennek (ha be van állítva) - ott hibajegy lesz belőlük.
        AlertLog.Notify = ev => { HubMailer.Enqueue(ev); HubJanus.Enqueue(ev); };
        HubMailer.Start(() => _config);
        _ = Task.Run(OfflineLoop);
        // A teljes winget-tár a Központ programtárához (naponta; az újonnan megjelentek döntésre várnak).
        _winget = new WingetCatalog(dir);
        EmbeddedAgent.Log = HubLog.Write;
        _ = Task.Run(() => _winget.RunLoop(() => _config, c => c.Save(HubPaths.DataDir), HubLog.Write, CancellationToken.None));
        _ = Task.Run(HubKeyLoop);
        // 1.5.6: sérülékenység-keresés (NVD + CISA KEV + EPSS) naponta; az eredmény a betárcsázás válaszában megy le.
        HubCve.Load();
        _store.CveFor = (s, hash) => HubCve.ForSite(s.SiteId, hash);
        // 1.5.7: központi napló – a telephelyek naplói (az USB-napló nélkül) a Központ naplómappájába
        _store.OnLogs = HubSiteLogs.Append;
        // 1.5.8: a telephelyek mérési megőrzése innen (alap 30 nap), utána a lezárt napi fájl ide kerül
        HubMetricArchive.Init(_metrics.Dir);
        _store.MetricKeepDaysFor = _ => Math.Clamp(_config.SiteMetricKeepDays, 2, 3650);
        // 1.5.9: WireGuard-alagút a telephelyeknek (szigorú mód: ami egyszer az alagúton jött, csak azon jöhet)
        _store.WgNet = () => HubWg.Net(_config);
        _store.WgRequired = s => HubWg.Required(_config, s);
        _store.WgOfferFor = s => HubWg.OfferFor(_config, s);
        _store.OnWgPeersChanged = () => HubWg.Apply(_config, _store);
        if (_config.WgEnabled) _ = Task.Run(() => HubWg.Apply(_config, _store));
        _ = Task.Run(() => HubCve.LoopAsync(() => _store.Sites, () => _config, HubLog.Write, CancellationToken.None));
        _store.OnMetrics = StoreMetrics;
        // 1.5.5: a telephely lezárult internet-kiesései és riasztást adó SNMP-trapjei a riasztásnaplóba (→ e-mail, Janus).
        // A kiesés alatt a telephely nem tud betárcsázni: a hír a visszatérés utáni első betárcsázással jön, mettől meddig tartott.
        _store.OnSiteEvents = (s, outs, snmp, svc) =>
        {
            // 1.5.6: szolgáltatás leállt / újra fut - ugyanazzal a névvel, hogy a megszűnés a Janusban a jegyet is lezárja.
            foreach (var e in svc)
                AlertLog.Write(new AlertEvent($"Szolgáltatás leállt: {e.Name} ({e.Source})", SiteKey(s), $"{s.Name} / {e.Source}", 0, 0, "", !e.Cleared, e.AtUtc, e.Text));
            foreach (var o in outs)
            {
                var min = Math.Max(1, (int)Math.Round(((o.EndUtc ?? o.StartUtc) - o.StartUtc).TotalMinutes));
                AlertLog.Write(new AlertEvent(HubJanus.InternetOutageRule, SiteKey(s), SiteLabel(s), min, 0, "", true, DateTime.UtcNow,
                    $"az internet nem volt elérhető: {o.StartUtc.ToLocalTime():yyyy.MM.dd HH:mm} – {o.EndUtc?.ToLocalTime():HH:mm} ({min} perc)"));
            }
            foreach (var e in snmp)
                AlertLog.Write(new AlertEvent($"SNMP: {e.Name} ({e.Source})", SiteKey(s), $"{s.Name} / SNMP {e.Source}", 0, 0, "", true, e.AtUtc, e.Text));
        };
        HubJanus.Start(() => _config, _store, _metrics);
        LogLimits.SetMb(_config.LogMaxMb);
        _store.HubVersion = HubPaths.Version;
        _store.ServerUpdateFor = OfferServerUpdate;
        // A Kozpont szabalyai a hatokorukbe tartozo telephelyekre mennek le, ott futnak (helyi idoben).
        _store.PoliciesFor = s => _config.AutoPolicies.Where(p => p.Sites.Count == 0 || p.Sites.Contains(s.SiteId)).ToList();
        // 1.5.0: a Központ a programtár mestere; a küszöb-szabályok (leküldésre jelöltek) és a mérés-kérés is lemegy.
        _store.CatalogFor = HubCatalogNow;
        _store.AlertRulesFor = AlertRulesFor;
        _store.OnPromoteRules = (site, rules) => { foreach (var r in rules) PromoteFromSite(site, r, "", out _); };
        _store.MonitorFor = s => _config.HubMonEnabled && (_config.HubMonSites.Count == 0 || _config.HubMonSites.Contains(s.SiteId))
            ? _config.MonitorAsConfig() : ServerConfig.NoMonitor();

        // EGYETLEN port: a telephelyek (/hub/...) es az admin web ugyanazon a porton mennek,
        // igy az NGINX Proxy Managernek egyetlen proxy host kell. A szetvalasztas utvonal
        // alapjan tortenik (lasd Dispatch).
        var listener = new HttpListener();
        // Tuzfal es URL-foglalas minden inditaskor - portvaltas utan is naprakesz.
        HubFirewall.Ensure(_config.WebPort);
        listener.Prefixes.Add($"http://+:{_config.WebPort}/");
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"Nem sikerült a(z) {_config.WebPort} porton figyelni: {ex.Message}");
            Console.WriteLine($"   netsh http add urlacl url=http://+:{_config.WebPort}/ sddl=D:(A;;GX;;;SY)");
            return;
        }
        Listeners = new[] { listener };

        Console.WriteLine();
        Console.WriteLine("  Fleet HUB");
        Console.WriteLine($"  Port: {_config.WebPort}   ·   admin: http://localhost:{_config.WebPort}   ·   telephely API: /hub/...");
        Console.WriteLine($"  Ujjlenyomat:    {_store.Keys.Fingerprint}");
        Console.WriteLine($"  Setup kész:     {(_config.SetupDone ? "igen" : "NEM – nyisd meg a webet")}");
        Console.WriteLine();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); try { listener.Stop(); } catch { } };

        Accept(listener, Dispatch, cts.Token).GetAwaiter().GetResult();
    }

    private static async Task Accept(HttpListener l, Func<HttpListenerContext, Task> h, CancellationToken t)
    {
        while (!t.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync(); } catch { break; }
            _ = Task.Run(async () => { try { await h(ctx); } catch (Exception ex) { try { ctx.Response.StatusCode = 500; await Json(ctx, new { error = ex.Message }); } catch { } } finally { try { ctx.Response.Close(); } catch { } } });
        }
    }

    /// <summary>Utvonal szerinti szetvalasztas: /hub/... a telephelyeke, minden mas a webe.</summary>
    private static async Task Dispatch(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSite(ctx);
            return;
        }
        // 1.5.9: az alagútból (a telephelyek felől) az admin felület nem érhető el – a 10.x cím „belsőnek” látszana.
        if (HubWg.FromTunnelRange(ctx, _config))
        {
            try { ctx.Response.StatusCode = 404; ctx.Response.ContentLength64 = 0; ctx.Response.Abort(); } catch { }
            return;
        }

        // Az admin felulet csak belso halozatrol (a proxy mogotti valodi kliens-IP alapjan).
        if (_config.HubAdminLanOnly &&
            !HubAccess.AdminAllowed(ctx.Request.RemoteEndPoint?.Address,
                                    ctx.Request.Headers["X-Real-IP"], ctx.Request.Headers["X-Forwarded-For"]))
        {
            // Kivulrol semmit nem arulunk el (se oldalt, se belso IP-t): a kapcsolatot valasz
            // nelkul lezarjuk. Az NPM mogott a latogato az NPM sajat hibaoldalat latja.
            // Windowson (http.sys) az Abort valasz nelkul bontja a kapcsolatot. Ha egy platform
            // megis kuldene valamit, az csak egy ures 404 legyen - oldal es belso IP semmikepp.
            try
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentLength64 = 0;
                ctx.Response.Abort();
            }
            catch { }
            return;
        }
        await HandleWeb(ctx);
    }

    // ---- telephelyek betárcsázása (aláírt borítékok)
    private static async Task HandleSite(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "";
        if (path == "/hub/fleet-exe") { await ServeFleetExe(ctx); return; }
        // 1.5.8: lezárt napi mérési fájl a telephelyről (aláírt fejléc + gzip-törzs; a nyugta a Központ kulcsával aláírt)
        // 1.5.9: a küldő alagút-címe, ha a WireGuard-alagúton jött (szigorú mód: a telephely csak onnan tárcsázhat be)
        var tunnelIp = HubWg.TunnelIp(ctx, _config);
        if (path == "/hub/metric-file" && ctx.Request.HttpMethod == "POST") { await Json(ctx, await HubMetricArchive.Receive(ctx, _store, _metrics, tunnelIp)); return; }
        var body = await Body(ctx);
        object? resp = path switch
        {
            "/hub/enroll" => _store.Enroll(ProtocolJson.Deserialize<HubEnrollRequest>(body) ?? new()),
            "/hub/checkin" => _store.Checkin(ProtocolJson.Deserialize<SignedEnvelope>(body) ?? new(), tunnelIp),
            // alagúton kívül is: a telephely WireGuard-kulcsának közlése / frissítése (aláírt; a helyi javítás útja)
            "/hub/wg-key" => HubWg.KeyRequest(body, _store, _config),
            "/hub/ping" => new { ok = true, service = "devtoolbox-hub" },
            // A telephely kapcsolat-tesztje ezzel ellenőrzi az ujjlenyomatot regisztráció előtt. Csak publikus adat.
            "/hub/key" => new { publicKeyPem = _store.Keys.PublicKeyPem, fingerprint = _store.Keys.Fingerprint, keyChain = _store.KeyChain },
            _ => null
        };
        if (resp is null) { ctx.Response.StatusCode = 404; await Json(ctx, new { error = "ismeretlen" }); return; }
        await Json(ctx, resp);
    }

    // ---- webes admin
    private static async Task HandleWeb(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "/";
        var method = ctx.Request.HttpMethod;

        if (!_config.SetupDone)
        {
            if (path == "/web/setup" && method == "POST") { await Setup(ctx); return; }
            await Html(ctx, HubWebUi.SetupPage(null, _config.WebPort, _config.AgentPort, _config.HubDomain));
            return;
        }
        if (path == "/web/login" && method == "POST") { await Login(ctx); return; }
        if (path == "/web/logout") { ClearCookie(ctx); Redirect(ctx, "/web"); return; }

        var sess = _sessions.Validate(Cookie(ctx, "hub_session"));
        var user = sess?.User;
        var role = sess?.Role ?? Role.Megfigyelo;
        if (path is "/web" or "/" or "/web/")
        {
            await Html(ctx, user is null ? HubWebUi.LoginPage(null) : HubWebUi.Dashboard(user, role));
            return;
        }
        // Uj agent telepitocsomag a Kozpontbol: a beagyazott agent + a telephelytol kapott beleptetok.
        // Az URL-ben csak a kerelem azonositoja van, a token nem.
        if (path == "/web/agent-zip" && method == "GET")
        {
            if (user is null) { ctx.Response.StatusCode = 401; await Json(ctx, new { error = "nincs bejelentkezve" }); return; }
            if (role < Role.Kezelo) { ctx.Response.StatusCode = 403; await Json(ctx, new { error = "kezelői jog kell" }); return; }
            var job = _store.GetTokenJob(ctx.Request.QueryString["req"] ?? "");
            var res = job?.Result;
            if (job is null || res is null || res.Token.Length == 0 || !CanTouch(user, job.SiteId))
            { ctx.Response.StatusCode = 404; await Json(ctx, new { error = "nincs ilyen kész csomag (lejárt?)" }); return; }
            var exe = EmbeddedAgent.Extract(HubPaths.DataDir);
            if (exe is null) { ctx.Response.StatusCode = 404; await Json(ctx, new { error = "Ebben a Központban nincs beépített agent." }); return; }
            var zip = AgentZip.Build(exe, res.AgentUrl, res.Token, res.Fingerprint, job.Label);
            AuditLog.Write(HubPaths.DataDir, user, "agent-telepítőcsomag letöltése: " + job.Label);
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=DevToolboxAgent-telepito.zip";
            ctx.Response.ContentLength64 = zip.Length;
            await ctx.Response.OutputStream.WriteAsync(zip);
            return;
        }
        if (path == "/web/api/upload-hub" && method == "POST")
        {
            if (user is null) { ctx.Response.StatusCode = 401; await Json(ctx, new { error = "nincs bejelentkezve" }); return; }
            if (role < Role.Kezelo) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            AuditLog.Write(HubPaths.DataDir, user!, "Központ frissítés feltöltése");
            await UploadHub(ctx);
            return;
        }
        // CSV-jelentés (GET link, a munkamenet-süti védi) - csak a felhasználó hatókörébe tartozó telephelyek.
        if (path == "/web/report" && method == "GET")
        {
            if (user is null) { ctx.Response.StatusCode = 401; await Json(ctx, new { error = "nincs bejelentkezve" }); return; }
            await ServeReport(ctx, user);
            return;
        }
        if (path.StartsWith("/web/api/"))
        {
            if (user is null) { ctx.Response.StatusCode = 401; await Json(ctx, new { error = "nincs bejelentkezve" }); return; }
            await WebApi(ctx, role, user!, path);
            return;
        }
        Redirect(ctx, "/web");
    }

    private static async Task WebApi(HttpListenerContext ctx, Role role, string user, string path)
    {
        var body = await Body(ctx);
        AuditLog.Api(HubPaths.DataDir, user, path, body);
        bool NeedKezelo() { if (role < Role.Kezelo) { ctx.Response.StatusCode = 403; return true; } return false; }
        bool NeedAdmin()  { if (role < Role.Admin)  { ctx.Response.StatusCode = 403; return true; } return false; }

        switch (path)
        {
            case "/web/api/users":
                if (NeedAdmin()) { await Json(ctx, new { error = "admin jog kell" }); return; }
                await Json(ctx, new { users = _users.All.Select(u => new { u.Username, role = (int)u.Role,
                    roleName = u.Role == Role.Admin ? "Adminisztrátor" : u.Role == Role.Kezelo ? "Kezelő" : "Megfigyelő",
                    u.Disabled, siteScope = u.SiteScope, allSites = u.AllSites }),
                    allSites = _store.Sites.Where(x => !x.Revoked).Select(x => new { id = x.SiteId, name = x.Name }) });
                return;
            case "/web/api/user-create":
            { if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
              var r = ProtocolJson.Deserialize<UsrReq>(body); var x = _users.Create(r?.Username ?? "", r?.Password ?? "", (Role)(r?.Role ?? 0));
              await Json(ctx, new { ok = x.Ok, error = x.Error }); return; }
            case "/web/api/user-role":
            { if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
              var r = ProtocolJson.Deserialize<UsrReq>(body); var x = _users.SetRole(r?.Username ?? "", (Role)(r?.Role ?? 0));
              await Json(ctx, new { ok = x.Ok, error = x.Error }); return; }
            case "/web/api/user-password":
            { if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
              var r = ProtocolJson.Deserialize<UsrReq>(body); var x = _users.SetPassword(r?.Username ?? "", r?.Password ?? "");
              await Json(ctx, new { ok = x.Ok, error = x.Error }); return; }
            case "/web/api/user-disable":
            { if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
              var r = ProtocolJson.Deserialize<UsrReq>(body); var x = _users.SetDisabled(r?.Username ?? "", r?.Disabled ?? false);
              await Json(ctx, new { ok = x.Ok, error = x.Error }); return; }
            case "/web/api/user-delete":
            { if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
              var r = ProtocolJson.Deserialize<UsrReq>(body); var x = _users.Delete(r?.Username ?? "");
              await Json(ctx, new { ok = x.Ok, error = x.Error }); return; }
            case "/web/api/user-scope":
            { if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
              var r = ProtocolJson.Deserialize<UsrReq>(body); var x = _users.SetScope(r?.Username ?? "", r?.SiteScope ?? new());
              await Json(ctx, new { ok = x.Ok, error = x.Error }); return; }
        }
        switch (path)
        {
            case "/web/api/sites":
            {
                var me = _users.Find(user);
                var visible = _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId));
                await Json(ctx, new { sites = visible.OrderByDescending(s => s.LastSeenUtc).Select(s => new
                {
                    siteId = s.SiteId, name = s.Name, serverVersion = s.ServerVersion,
                    fingerprint = s.Fingerprint[..Math.Min(16, s.Fingerprint.Length)],
                    online = s.IsOnline, revoked = s.Revoked, intervalSeconds = s.IntervalSeconds,
                    enrolled = s.EnrolledUtc.ToLocalTime().ToString("yyyy.MM.dd"),
                    lastSeen = s.LastSeenUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                    agentCount = s.LastSummary?.Agents.Count ?? 0,
                    hubVersion = HubPaths.Version, updateRequested = s.UpdateRequested,
                    supportsHubUpdates = s.LastSummary?.SupportsHubUpdates ?? false,
                    agentAutoUpdate = s.AgentAutoUpdateWanted ?? s.LastSummary?.AgentAutoUpdate ?? false,
                    agentAutoUpdatePending = s.AgentAutoUpdateWanted is not null,
                    agentPackageVersion = s.LastSummary?.AgentPackageVersion ?? "",
                    agentPort = s.LastSummary?.AgentPort ?? 0, oldAgentPort = s.LastSummary?.OldAgentPort ?? 0,
                    // 1.5.3: gyors betárcsázás és a frissülések (mikor, mire)
                    fastUntil = s.FastUntilUtc is { } fu && fu > DateTime.UtcNow ? fu.ToLocalTime().ToString("HH:mm") : null,
                    nextDial = s.LastSeenUtc?.AddSeconds(s.FastUntilUtc > DateTime.UtcNow ? Math.Min(30, s.IntervalSeconds) : s.IntervalSeconds).ToLocalTime().ToString("HH:mm:ss"),
                    updateRequestedAt = s.UpdateRequestedUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                    serverUpdate = s.LastServerUpdate is { } su ? new { from = su.From, to = su.To, at = su.AtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm") } : null,
                    agentUpdates = s.AgentUpdates.AsEnumerable().Reverse().Take(10).Select(u => new { machine = u.Machine, from = u.From, to = u.To, at = u.AtUtc.ToLocalTime().ToString("MM.dd HH:mm") }),
                    agentUpdatePending = s.AgentUpdateRequests.Count,
                    updatesAvailable = s.LastSummary?.Agents.Sum(a => a.UpdatesAvailable) ?? 0,
                    // 1.5.5: a telephely internetkapcsolata (ha figyeli) és a legutóbbi riasztást adó SNMP-trapek
                    internet = s.LastSummary?.Internet is { } inet ? new
                    {
                        online = inet.Online, since = inet.SinceUtc?.ToLocalTime().ToString("MM.dd HH:mm"), lastCheck = inet.LastCheckUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                        target = inet.Target, rtt = inet.RttMs, minutes = inet.IntervalMinutes,
                        outages = inet.Outages.AsEnumerable().Reverse().Take(5).Select(o => new
                        {
                            start = o.StartUtc.ToLocalTime().ToString("MM.dd HH:mm"), end = o.EndUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                            minutes = Math.Max(1, (int)Math.Round(((o.EndUtc ?? DateTime.UtcNow) - o.StartUtc).TotalMinutes))
                        })
                    } : null,
                    snmpEnabled = s.LastSummary?.SnmpEnabled ?? false,
                    snmpEvents = (s.LastSummary?.SnmpEvents ?? new()).AsEnumerable().Reverse().Take(10)
                        .Select(e => new { at = e.AtUtc.ToLocalTime().ToString("MM.dd HH:mm"), source = e.Source, name = e.Name, text = e.Text }),
                    // A lista 15 mp-enkent frissul: itt csak az attekintes kell, a reszletes listak
                    // (programok, frissitesek) a gep megnyitasakor jonnek (/web/api/machine).
                    agents = s.LastSummary?.Agents.Select(a => new
                    {
                        a.AgentId, a.MachineName, a.OperatingSystem, a.UpdatesAvailable, a.AgentVersion, a.Online,
                        a.Groups, a.PendingReboot, wuCount = a.WindowsUpdates.Count,
                        // a gépek csoportosításához (típus, Windows-verzió, hardver)
                        classes = AutoGroups.For(a.OperatingSystem, a.IsVirtual),
                        // 1.5.6: eszköz-összegzés (a tételek a gép oldalán: /web/api/machine)
                        score = a.Score is { } sc ? new { score = sc.Score, security = sc.Security, health = sc.Health, grade = sc.Grade } : null
                    })
                }) });
                return;
            }

            case "/web/api/audit":
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                await Json(ctx, new { log = AuditLog.Tail(HubPaths.DataDir, 400) });
                return;

            case "/web/api/rotate-keys":
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                try { _store.RotateKeys("kézi"); await Json(ctx, new { ok = true, fingerprint = _store.Keys.Fingerprint }); }
                catch (Exception ex) { await Json(ctx, new { ok = false, error = ex.Message }); }
                return;

            // ---- kulcslista: a Központ kulcsai (előzmény), a telephelyek kulcsai, a kiadott telephely-tokenek
            case "/web/api/keys":
            {
                if (NeedAdmin()) { await Json(ctx, new { error = "admin jog kell" }); return; }
                static string? L(DateTime? t) => t?.ToLocalTime().ToString("yyyy.MM.dd HH:mm");
                var blockers = _store.StrongKeyBlockers();
                var days = _config.ServerKeyRotationDays;
                await Json(ctx, new
                {
                    owner = new
                    {
                        title = "A Központ kulcsa", fingerprint = _store.Keys.Fingerprint, algorithm = Crypto.Algorithm(_store.Keys.PublicKeyPem),
                        created = L(_store.KeyCreatedUtc), rotateDays = days, next = days > 0 ? L(_store.KeyCreatedUtc.AddDays(days)) : null,
                        history = _store.KeyHistory.Reverse().Select(k => new { fingerprint = k.Fingerprint, algorithm = k.Algorithm, created = L(k.CreatedUtc), retired = L(k.RetiredUtc), reason = k.Reason }),
                        strongNote = blockers.Count == 0 ? "" : $"A következő Központ-kulcs is P-256 lesz, amíg {blockers.Count} régi (1.4.5 előtti) telephely van: " +
                            string.Join(", ", blockers.Take(10)) + (blockers.Count > 10 ? " …" : "") + " – frissítsd őket (vagy vond vissza)."
                    },
                    peerKind = "site",
                    peers = _store.Sites.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new
                    {
                        name = s.Name, fingerprint = s.Fingerprint, algorithm = Crypto.Algorithm(s.PublicKeyPem), since = L(s.EnrolledUtc),
                        pending = false, revoked = s.Revoked, version = s.ServerVersion,
                        history = new[] { new { fingerprint = s.Fingerprint, algorithm = Crypto.Algorithm(s.PublicKeyPem), created = L(s.EnrolledUtc), retired = (string?)null, reason = "regisztráció" } }
                    }),
                    tokens = _store.Tokens.OrderByDescending(t => t.CreatedUtc).Select(t => new
                    {
                        id = t.Fingerprint, label = t.Label, created = L(t.CreatedUtc), expires = L(t.ExpiresUtc), uses = t.Uses, maxUses = t.MaxUses,
                        status = !t.Enabled ? "letiltva" : t.ExpiresUtc is { } e && DateTime.UtcNow > e ? "lejárt" : t.MaxUses > 0 && t.Uses >= t.MaxUses ? "elhasználva" : "aktív"
                    })
                });
                return;
            }
            case "/web/api/key-settings":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<HubKeyReq>(body);
                if (r is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                _config.ServerKeyRotationDays = ServerConfig.ClampRotationDays(r.ServerDays);
                _config.Save(HubPaths.DataDir);
                await Json(ctx, new { ok = true });
                return;
            }
            case "/web/api/token-disable":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<HubKeyReq>(body);
                await Json(ctx, _store.DisableToken(r?.Id ?? "") ? new { ok = true, error = (string?)null } : new { ok = false, error = (string?)"Nincs ilyen token." });
                return;
            }
            case "/web/api/token-delete":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<HubKeyReq>(body);
                await Json(ctx, _store.RemoveToken(r?.Id ?? "") ? new { ok = true, error = (string?)null } : new { ok = false, error = (string?)"Nincs ilyen token." });
                return;
            }

            case "/web/api/hub-info":
                await Json(ctx, new
                {
                    version = HubPaths.Version, domain = _config.HubDomain,
                    webPort = _config.WebPort, localIp = HubInstaller.LocalIp(),
                    fingerprint = _store.Keys.Fingerprint, caddyDir = HubPaths.CaddyDir,
                    pendingUpdate = File.Exists(Path.Combine(HubPaths.InstallDir, "FleetHub.new.exe")),
                    tokenLifetimeMinutes = _config.TokenLifetimeMinutes, tokenMaxUses = _config.TokenMaxUses,
                    detailDays = _config.MonDetailDays, keepDays = _config.MonKeepDays, logMaxMb = _config.LogMaxMb,
                    siteKeepDays = _config.SiteMetricKeepDays, archiveDays = _config.MetricArchiveDays,
                    policyCount = _config.AutoPolicies.Count
                });
                return;

            case "/web/api/token":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<TokReq>(body);
                var tok = _store.CreateToken(r?.Label ?? "telephely", _config.TokenLifetimeMinutes, _config.TokenMaxUses);
                await Json(ctx, new { token = tok, fingerprint = _store.Keys.Fingerprint, domain = _config.HubDomain,
                    lifetimeMinutes = _config.TokenLifetimeMinutes });
                return;
            }
            case "/web/api/interval":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<IvalReq>(body);
                if (r is not null && !CanTouch(user, r.SiteId)) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                if (r is not null) _store.SetInterval(r.SiteId, r.Seconds);
                await Json(ctx, new { ok = true });
                return;
            }
            case "/web/api/queue":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<QReq>(body);
                if (r is not null && !CanTouch(user, r.SiteId)) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                if (r is not null)
                {
                    if (r.All)
                        _store.QueueTask(r.SiteId, new HubTask { AgentId = r.AgentId, Kind = AgentTaskKind.UpgradeAll });
                    else foreach (var id in r.Ids ?? new())
                        _store.QueueTask(r.SiteId, new HubTask { AgentId = r.AgentId, Kind = AgentTaskKind.UpgradeApp, Target = id });
                }
                await Json(ctx, new { ok = true });
                return;
            }
            case "/web/api/task":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                // Ugyanaz a műveletkészlet és ellenőrzés, mint a telephelyi szerver /web/api/task-ja;
                // a feladat a telephely következő betárcsázásakor megy le, onnan az agenthez.
                var r = ProtocolJson.Deserialize<HubTaskReq>(body);
                if (r is null || !CanTouch(user, r.SiteId)) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                if (r.Kind is "servicestart" or "servicestop" or "servicerestart" && role < Role.Admin)
                { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "Szolgáltatás kezeléséhez admin jog kell." }); return; }
                var site = _store.Sites.FirstOrDefault(s => s.SiteId == r.SiteId && !s.Revoked);
                var targets = site?.LastSummary?.Agents.Where(a => !a.Revoked && (r.AgentIds?.Contains(a.AgentId) ?? false)).ToList() ?? new();
                if (targets.Count == 0) { await Json(ctx, new { ok = false, error = "Nincs célgép." }); return; }
                List<(AgentTaskKind Kind, string Target)> work;
                if (r.Kind == "updateagent")
                {
                    // A telephelynek szól (nem az agentnek) - régi telephely nem ismeri, ott előbb a szervert kell frissíteni.
                    if (site?.LastSummary?.SupportsHubUpdates != true)
                    { await Json(ctx, new { ok = false, error = "Ez a telephely még nem ismeri – előbb frissítsd a telephely szerverét." }); return; }
                    work = new() { (AgentTaskKind.UpdateAgent, "") };
                }
                else if (r.Kind == "installbundle")
                {
                    // Alkalmazascsomag - programonkent egy telepitesi feladat.
                    work = _config.BundleApps(r.Target).Select(id => (AgentTaskKind.InstallApp, id)).ToList();
                    if (work.Count == 0) { await Json(ctx, new { ok = false, error = "A csomag üres vagy nem létezik." }); return; }
                }
                else if (r.Kind == "installbase")
                {
                    // A Kozpont katalogusanak alapcsomagja - programonkent egy telepitesi feladat.
                    work = _config.SoftwareCatalog.Where(c => c.Base && WingetId.IsValid(c.Id)).Select(c => (AgentTaskKind.InstallApp, c.Id)).ToList();
                    if (work.Count == 0) { await Json(ctx, new { ok = false, error = "Az alapcsomag üres – a Programok menüben jelöld ki." }); return; }
                }
                else
                {
                    var err = ParseTask(r.Kind, r.Target, out var kind, out var target);
                    if (err is not null) { await Json(ctx, new { ok = false, error = err }); return; }
                    work = new() { (kind, target) };
                }
                foreach (var a in targets)
                    foreach (var (kind, target) in work)
                        _store.QueueTask(r.SiteId, new HubTask { AgentId = a.AgentId, Kind = kind, Target = target });
                AuditLog.Write(HubPaths.DataDir, user!, $"feladat: {r.Kind} {(work.Count == 1 ? work[0].Target : work.Count + " program")} – {targets.Count} gép ({site!.Name})");
                await Json(ctx, new { ok = true, machines = targets.Count, tasks = work.Count });
                return;
            }
            // ---- 1.5.10: egy gép szolgáltatásai a Központ szolgáltatás-kezelő paneljéhez (a telephely felküldte)
            case "/web/api/machine-services":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<HubTaskReq>(body);
                var aid = r?.AgentIds?.FirstOrDefault();
                if (r is null || aid is null || !CanTouch(user, r.SiteId)) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                var ag = _store.Sites.FirstOrDefault(s => s.SiteId == r.SiteId && !s.Revoked)?.LastSummary?.Agents.FirstOrDefault(a => a.AgentId == aid);
                if (ag is null) { await Json(ctx, new { ok = false, error = "Nincs ilyen gép." }); return; }
                await Json(ctx, new
                {
                    ok = true,
                    reported = ag.ServiceList is not null,
                    services = (ag.ServiceList ?? new List<ServiceEntry>())
                        .OrderBy(s => s.DisplayName.Length > 0 ? s.DisplayName : s.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(s => new { name = s.Name, displayName = s.DisplayName, running = s.Running, auto = s.Auto })
                });
                return;
            }

            // ---- új agent a Központból (a beléptetőt a telephely készíti, a következő betárcsázáskor)
            // ---- frissítés a Központból: telephely szervere (a Központ saját exéjére) és az agentek
            case "/web/api/site-update":
            case "/web/api/site-agent-autoupdate":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<HubTaskReq>(body);
                var s = r is null || !CanTouch(user, r.SiteId) ? null : _store.Sites.FirstOrDefault(x => x.SiteId == r.SiteId && !x.Revoked);
                if (s is null) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                var on = r!.Target == "on";
                if (path.EndsWith("site-update")) _store.SetServerUpdate(s.SiteId, on); else _store.SetAgentAutoUpdate(s.SiteId, on);
                AuditLog.Write(HubPaths.DataDir, user!, $"{(path.EndsWith("site-update") ? "szerverfrissítés kérése" : "agentek automatikus frissítése")}: {(on ? "be" : "ki")} ({s.Name})");
                await Json(ctx, new { ok = true });
                return;
            }
            case "/web/api/site-fast":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                // A Központ nem hívhatja fel a telephelyet: a következő betárcsázástól 10 percig 30 mp-enként jelentkezzen.
                var r = ProtocolJson.Deserialize<HubTaskReq>(body);
                if (r is null || !CanTouch(user, r.SiteId) || !_store.Sites.Any(s => s.SiteId == r.SiteId && !s.Revoked))
                { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                _store.SetFast(r.SiteId);
                await Json(ctx, new { ok = true });
                return;
            }
            case "/web/api/agent-token-request":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<HubTaskReq>(body);
                if (r is null || !CanTouch(user, r.SiteId) || !_store.Sites.Any(s => s.SiteId == r.SiteId && !s.Revoked))
                { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                var label = r.Target.Trim().Length > 0 ? r.Target.Trim() : "agent";
                var id = _store.RequestAgentToken(r.SiteId, label, _config.TokenLifetimeMinutes, _config.TokenMaxUses);
                AuditLog.Write(HubPaths.DataDir, user!, "agent-beléptető kérése: " + label);
                await Json(ctx, new { ok = true, requestId = id });
                return;
            }
            case "/web/api/agent-tokens":
            {
                var r = ProtocolJson.Deserialize<HubTaskReq>(body);
                if (r is null || !CanTouch(user, r.SiteId)) { ctx.Response.StatusCode = 403; await Json(ctx, new { error = "nincs jogod ehhez a telephelyhez" }); return; }
                await Json(ctx, new
                {
                    jobs = _store.TokenJobs(r.SiteId).Select(x => new
                    {
                        id = x.Id, label = x.Job.Label, created = x.Job.CreatedUtc.ToLocalTime().ToString("MM.dd HH:mm"),
                        state = x.Job.Result is null ? (x.Job.SentUtc is null ? "vár a betárcsázásra" : "a telephely készíti")
                              : x.Job.Result.Error.Length > 0 ? "hiba: " + x.Job.Result.Error : "kész",
                        ready = x.Job.Result is { Token.Length: > 0 }
                    })
                });
                return;
            }

            // ---- programkatalógus és automatikus szabályok (a Központ szintjén; módosítás: admin)
            case "/web/api/catalog":
                if (_config.MigrateBaseToBundle()) _config.Save(HubPaths.DataDir);
                await Json(ctx, new { items = _winget.Describe(_config.SoftwareCatalog), bundles = _config.AppBundles,
                    pendingCount = _config.CatalogPending.Count, rejectedCount = _config.CatalogRejected.Count, winget = _winget.Status(), undecided = _winget.Undecided(_config),
                    categories = WingetCatalog.CategoryNames });
                return;
            // ---- a teljes winget-tár: böngészés mindenkinek, döntés és frissítés csak adminnak (mint a programtár mentése)
            case "/web/api/winget-catalog":
                await Json(ctx, _winget.Page(_config, ProtocolJson.Deserialize<WingetCatalog.ListReq>(body) ?? new()));
                return;
            case "/web/api/catalog-category":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var cr = ProtocolJson.Deserialize<HubCatCategoryReq>(body) ?? new();
                var cok = WingetCatalog.SetCategory(_config, cr.Id, cr.Category);
                if (cok) _config.Save(HubPaths.DataDir);
                await Json(ctx, new { ok = cok, error = cok ? null : "Ez a program nincs a repóban." });
                return;
            }
            case "/web/api/catalog-decide":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var n = _winget.Decide(_config, ProtocolJson.Deserialize<WingetCatalog.DecideReq>(body) ?? new());
                _config.Save(HubPaths.DataDir);
                await Json(ctx, new { ok = true, changed = n });
                return;
            }
            case "/web/api/winget-catalog-refresh":
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                if (!_winget.Refreshing) _ = Task.Run(() => _winget.RefreshAndRecord(_config, c => c.Save(HubPaths.DataDir), HubLog.Write));
                await Json(ctx, new { ok = true });
                return;
            case "/web/api/catalog-save":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<HubCatalogReq>(body);
                _config.SetCatalog(r?.Items, r?.Bundles);
                _config.Save(HubPaths.DataDir);
                AuditLog.Write(HubPaths.DataDir, user!, $"programtár mentése ({_config.SoftwareCatalog.Count} program, {_config.AppBundles.Count} csomag)");
                await Json(ctx, new { ok = true, count = _config.SoftwareCatalog.Count, bundles = _config.AppBundles.Count });
                return;
            }
            case "/web/api/inventory":
            {
                // A lathato telephelyek gepein talalt, winget altal ismert programok - a programtar feltoltesehez.
                var me = _users.Find(user ?? "");
                var apps = _store.Sites.Where(s => !s.Revoked && (me is null || me.CanSeeSite(s.SiteId)))
                    .SelectMany(s => s.LastSummary?.Agents ?? new()).SelectMany(a => a.Installed)
                    .Where(i => i.Source.Length > 0 && !i.Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) && WingetId.IsValid(i.Id))
                    .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Id: g.Key, Name: g.GroupBy(x => x.Name).OrderByDescending(n => n.Count()).First().Key, Count: g.Count()))
                    .Select(x => { var (pub, cat) = _winget.Meta(x.Id, x.Name); return new { id = x.Id, name = x.Name, count = x.Count, state = _config.CatalogState(x.Id), publisher = pub, category = cat }; })
                    .OrderBy(x => x.name, StringComparer.CurrentCultureIgnoreCase);
                await Json(ctx, new { apps });
                return;
            }
            case "/web/api/app-search":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<HubSearchReq>(body);
                var (ok, hits, err) = await WingetSearch.RunAsync(r?.Q ?? "");
                await Json(ctx, new { ok, error = err, results = hits.Select(h => new { id = h.Id, name = h.Name, version = h.Version }) });
                return;
            }
            case "/web/api/policies":
            {
                var me = _users.Find(user ?? "");
                var vis = _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId)).ToList();
                await Json(ctx, new
                {
                    policies = _config.AutoPolicies,
                    // A Központ szabálya a telephelyen fut: az automatikus csoportokat az 1.5.4-s telephely ismeri.
                    groups = vis.SelectMany(s => s.LastSummary?.Agents ?? new()).SelectMany(GroupsOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g),
                    sites = vis.Select(s => new { siteId = s.SiteId, name = s.Name })
                });
                return;
            }
            case "/web/api/policy-save":
            case "/web/api/policy-delete":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var p = ProtocolJson.Deserialize<AutoPolicy>(body);
                if (p is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                if (path.EndsWith("delete"))
                {
                    _config.AutoPolicies.RemoveAll(x => x.Id == p.Id); _config.Save(HubPaths.DataDir);
                    AuditLog.Write(HubPaths.DataDir, user!, "szabály törlése: " + p.Name);
                    await Json(ctx, new { ok = true }); return;
                }
                if (!p.Apps && !p.Windows && !p.Reboot) { await Json(ctx, new { ok = false, error = "Válassz legalább egy műveletet." }); return; }
                if (p.Days.Count == 0 || p.StartHour is < 0 or > 23 || p.EndHour is < 0 or > 23 || p.StartHour == p.EndHour)
                { await Json(ctx, new { ok = false, error = "Adj meg napokat és egy érvényes időablakot." }); return; }
                p.Days = p.Days.Where(d => d is >= 0 and <= 6).Distinct().ToList();
                p.Sites = p.Sites.Where(id => _store.Sites.Any(s => s.SiteId == id)).Distinct().ToList();
                if (p.Id.Length == 0) p.Id = Guid.NewGuid().ToString("N");
                p.Name = p.Name.Trim().Length > 0 ? p.Name.Trim() : "Központi szabály";
                _config.AutoPolicies.RemoveAll(x => x.Id == p.Id);
                _config.AutoPolicies.Add(p);
                _config.Save(HubPaths.DataDir);
                AuditLog.Write(HubPaths.DataDir, user!, "szabály mentése: " + p.Name);
                await Json(ctx, new { ok = true });
                return;
            }

            case "/web/api/machine":
            {
                var r = ProtocolJson.Deserialize<HubMetricReq>(body);
                var a = r is null || !CanTouch(user, r.SiteId) ? null
                      : _store.Sites.FirstOrDefault(s => s.SiteId == r.SiteId)?.LastSummary?.Agents.FirstOrDefault(x => x.AgentId == r.AgentId);
                if (a is null) { ctx.Response.StatusCode = 404; await Json(ctx, new { error = "nincs ilyen gép" }); return; }
                var site = _store.Sites.First(s => s.SiteId == r!.SiteId);
                var lu = site.AgentUpdates.LastOrDefault(u => u.AgentId == a.AgentId);
                await Json(ctx, new
                {
                    agent = a,
                    cve = HubCve.ForAgent(r!.SiteId, a.AgentId),
                    updateRequestedAt = site.AgentUpdateRequests.TryGetValue(a.AgentId, out var rq) ? rq.ToLocalTime().ToString("MM.dd HH:mm") : null,
                    lastUpdate = lu is null ? null : new { from = lu.From, to = lu.To, at = lu.AtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm") }
                });
                return;
            }

            // ---- monitorozás (a telephelyekről érkező 5 perces átlagok)
            case "/web/api/metric-keys":
            case "/web/api/metric-series":
            {
                var r = ProtocolJson.Deserialize<HubMetricReq>(body);
                if (r is null || !CanTouch(user, r.SiteId) || !SiteHasAgent(r.SiteId, r.AgentId))
                { ctx.Response.StatusCode = 403; await Json(ctx, new { error = "nincs jogod ehhez a géphez" }); return; }
                if (path.EndsWith("keys"))
                {
                    var latest = _metrics.Latest(r.AgentId);
                    await Json(ctx, new { keys = _metrics.Keys(r.AgentId).Select(k => new
                    {
                        key = k.Key, label = k.Label, category = k.Category, unit = k.Unit,
                        latest = latest.TryGetValue(k.Key, out var v) ? (double?)v.Value : null
                    }) });
                }
                else
                {
                    // 1.5.8: a Központ saját adata + a telephely feltöltött napi fájljai (egy nap csak az egyikben van meg).
                    var hrs = Math.Clamp(r.Hours, 1, 24 * 400);
                    var pts = _metrics.Series(r.AgentId, r.Key, hrs).Concat(HubMetricArchive.Series(r.SiteId, r.AgentId, r.Key, hrs)).OrderBy(p => p.Ts);
                    await Json(ctx, new { series = pts.Select(p => new { t = p.Ts, v = p.Value }) });
                }
                return;
            }
            case "/web/api/alerts":
            {
                var mach = VisibleMachines(user);
                // Az aktív listában a látható telephelyek kiesése is (kulcs: site:<id>).
                var names = new Dictionary<string, string>(mach);
                var me = _users.Find(user ?? "");
                foreach (var s in _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId))) names[SiteKey(s)] = SiteLabel(s);
                var active = _alerts.ActiveStates().Where(a => names.ContainsKey(a.AgentId)).OrderByDescending(a => a.SinceUtc).ToList();
                var all = _alerts.Rules;
                var vis = _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId)).ToList();
                var visIds = vis.Select(s => s.SiteId).ToHashSet();
                await Json(ctx, new
                {
                    // Korlátozott felhasználó csak azokat a szabályokat látja, amelyek a látható telephelyeire vonatkoznak.
                    rules = all.Where(r => (r.AgentId.Length == 0 || mach.ContainsKey(r.AgentId)) && (r.Sites.Count == 0 || r.Sites.Any(visIds.Contains))).Select(r => new
                    {
                        id = r.Id, name = r.Name, metricKey = r.MetricKey, op = (int)r.Op, opText = r.OpText, threshold = r.Threshold,
                        agentId = r.AgentId, machine = mach.GetValueOrDefault(r.AgentId, ""), forSamples = r.ForSamples, group = r.Group, displayUnit = r.DisplayUnit,
                        sites = r.Sites, pushDown = r.PushDown, enabled = r.Enabled, activeCount = active.Count(a => a.RuleId == r.Id),
                        origin = r.OriginSite.Length > 0 ? _store.Sites.FirstOrDefault(x => x.SiteId == r.OriginSite)?.Name ?? "?" : ""
                    }),
                    active = active.Select(a => new
                    {
                        a.RuleId, machine = names[a.AgentId], a.LastValue, since = a.SinceUtc?.ToLocalTime().ToString("MM.dd HH:mm"),
                        offline = a.RuleId == AlertStore.OfflineRuleId, metricKey = a.MetricKey,
                        name = a.RuleId == AlertStore.OfflineRuleId ? AlertStore.OfflineRuleName : all.FirstOrDefault(r => r.Id == a.RuleId)?.Name ?? ""
                    }),
                    offlineMinutes = _config.OfflineAlertMinutes, offlineGroups = _config.OfflineAlertGroups, offlineMachines = _config.OfflineAlertMachines,
                    groups = vis.SelectMany(s => (s.LastSummary?.GroupNames ?? new()).Concat((s.LastSummary?.Agents ?? new()).SelectMany(GroupsOf)))
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g),
                    machines = mach.OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase).Select(kv => new { agentId = kv.Key, name = kv.Value }),
                    sites = vis.Where(s => !s.Revoked).OrderBy(s => s.Name).Select(s => new { siteId = s.SiteId, name = s.Name })
                });
                return;
            }
            case "/web/api/alert-save":
            case "/web/api/alert-delete":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<HubAlertReq>(body);
                if (r is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                var mach = VisibleMachines(user);
                var existing = _alerts.Rules.FirstOrDefault(x => x.Id == r.Id);
                // Minden telephely minden gépére szóló szabályt csak admin ad / töröl; telephelyre szólót az, aki minden
                // érintett telephelyet lát; gépre szólót az, aki látja a gépet.
                bool Allowed(string agentId, List<string> sites) => agentId.Length > 0 ? mach.ContainsKey(agentId)
                    : sites.Count == 0 ? role >= Role.Admin : sites.All(s => CanTouch(user, s));
                if (path.EndsWith("delete"))
                {
                    if (existing is not null && !Allowed(existing.AgentId, existing.Sites)) { await Json(ctx, new { ok = false, error = "nincs jogod ehhez a szabályhoz" }); return; }
                    if (existing is not null) { _alerts.Remove(existing.Id); AuditLog.Write(HubPaths.DataDir, user!, "küszöb törlése: " + existing.Name); }
                    await Json(ctx, new { ok = true }); return;
                }
                if (r.MetricKey.Trim().Length == 0) { await Json(ctx, new { ok = false, error = "Hiányzó metrika." }); return; }
                if (!AlertRule.ValidKey(r.MetricKey.Trim())) { await Json(ctx, new { ok = false, error = "Érvénytelen metrika-kulcs." }); return; }
                var sites = (r.Sites ?? new()).Where(id => _store.Sites.Any(s => s.SiteId == id)).Distinct().ToList();
                if (!Allowed(r.AgentId ?? "", sites) || (existing is not null && !Allowed(existing.AgentId, existing.Sites)))
                { await Json(ctx, new { ok = false, error = "Minden telephelyre szóló küszöböt csak admin adhat meg (vagy válassz telephelyet, amelyhez jogod van)." }); return; }
                var rule = new AlertRule
                {
                    Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                    Name = r.Name.Trim().Length > 0 ? r.Name.Trim() : r.MetricKey.Trim(),
                    MetricKey = r.MetricKey.Trim(), Op = (AlertOp)Math.Clamp(r.Op, 0, 2), Threshold = r.Threshold,
                    AgentId = r.AgentId ?? "", ForSamples = Math.Clamp(r.ForSamples, 1, 60), Enabled = r.Enabled,
                    Group = (r.AgentId ?? "").Length > 0 ? "" : GroupOps.Clean(r.Group), Sites = (r.AgentId ?? "").Length > 0 ? new() : sites, PushDown = r.PushDown,
                    DisplayUnit = AlertRule.CleanUnit(r.DisplayUnit),
                    OriginSite = existing?.OriginSite ?? "", OriginRuleId = existing?.OriginRuleId ?? ""
                };
                if (existing is not null) _alerts.Update(rule); else _alerts.Add(rule);
                AuditLog.Write(HubPaths.DataDir, user!, "küszöb mentése: " + rule.Name);
                await Json(ctx, new { ok = true });
                return;
            }
            case "/web/api/alert-log":
            {
                // Korlátozott felhasználó csak a látható telephelyek sorait kapja.
                var me = _users.Find(user ?? "");
                var sitesSeen = _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId)).Select(s => s.Name + " / ").ToList();
                var lines = AlertLog.Tail(400).Split('\n').Where(l => l.Length > 0 && sitesSeen.Any(l.Contains)).TakeLast(200);
                await Json(ctx, new { log = string.Join("\n", lines) });
                return;
            }
            case "/web/api/offline-save":
            {
                // Minden telephelyre hat - mint a Központ többi közös beállítása, csak admin.
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<HubOfflineReq>(body);
                if (r is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                _config.OfflineAlertMinutes = ServerConfig.ClampOfflineMinutes(r.Minutes);
                _config.OfflineAlertMachines = r.Machines;
                _config.OfflineAlertGroups = (r.Groups ?? new()).Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _config.Save(HubPaths.DataDir);
                CheckOffline();
                await Json(ctx, new { ok = true, minutes = _config.OfflineAlertMinutes });
                return;
            }

            // ---- 1.5.7: a telephely saját küszöb-szabályai a Központból (a telephely hajtja végre és írja – HubSiteOp)
            case "/web/api/site-rules":
            {
                var r = ProtocolJson.Deserialize<SiteRuleReq>(body);
                var s = r is null || !CanTouch(user, r.SiteId) ? null : _store.Sites.FirstOrDefault(x => x.SiteId == r.SiteId && !x.Revoked);
                if (s is null) { await Json(ctx, new { ok = false, error = "Nincs ilyen telephely." }); return; }
                var ag = s.LastSummary?.Agents.Where(a => !a.Revoked).ToList() ?? new();
                await Json(ctx, new
                {
                    ok = true, siteId = s.SiteId, name = s.Name, supports = s.LastSummary?.SupportsSiteOps == true,
                    pending = s.PendingSiteOps.Count,
                    nextDial = s.LastSeenUtc?.AddSeconds(s.FastUntilUtc > DateTime.UtcNow ? Math.Min(30, s.IntervalSeconds) : s.IntervalSeconds).ToLocalTime().ToString("HH:mm:ss"),
                    rules = (s.LastSummary?.LocalRules ?? new()).Select(x => new
                    {
                        id = x.Id, name = x.Name, metricKey = x.MetricKey, op = x.Op, opText = x.Op switch { 1 => "<", 2 => "=", _ => ">" },
                        threshold = x.Threshold, agentId = x.AgentId, machine = ag.FirstOrDefault(a => a.AgentId == x.AgentId)?.MachineName ?? "",
                        group = x.Group, forSamples = x.ForSamples, enabled = x.Enabled, displayUnit = x.DisplayUnit, activeCount = 0,
                        central = _alerts.Rules.FirstOrDefault(h => h.OriginSite == s.SiteId && h.OriginRuleId == x.Id)?.Name ?? ""
                    }),
                    groups = (s.LastSummary?.GroupNames ?? new()).Concat(ag.SelectMany(GroupsOf)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g),
                    machines = ag.OrderBy(a => a.MachineName, StringComparer.CurrentCultureIgnoreCase).Select(a => new { agentId = a.AgentId, name = a.MachineName })
                });
                return;
            }
            case "/web/api/site-rule-promote":
            {
                // 1.5.13: a telephely helyi szabályából központi szabály (a Központ felületéről)
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
                var r = ProtocolJson.Deserialize<SiteRuleReq>(body);
                var s = r is null || !CanTouch(user, r.SiteId) ? null : _store.Sites.FirstOrDefault(x => x.SiteId == r.SiteId && !x.Revoked);
                var lr = s?.LastSummary?.LocalRules?.FirstOrDefault(x => x.Id == r!.Id);
                if (s is null || lr is null) { await Json(ctx, new { ok = false, error = "Nincs ilyen szabály ezen a telephelyen." }); return; }
                var err = PromoteFromSite(s, lr, user ?? "", out _);
                await Json(ctx, new { ok = err is null, error = err });
                return;
            }
            case "/web/api/site-rule-save":
            case "/web/api/site-rule-delete":
            {
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
                var r = ProtocolJson.Deserialize<SiteRuleReq>(body);
                if (r is null || !CanTouch(user, r.SiteId)) { await Json(ctx, new { ok = false, error = "Nincs jogod ehhez a telephelyhez." }); return; }
                HubSiteOp op;
                if (path.EndsWith("delete"))
                {
                    if (r.Id.Length == 0) { await Json(ctx, new { ok = false, error = "Hiányzó szabály." }); return; }
                    op = new HubSiteOp { Kind = "rule-delete", Id = r.Id, By = user ?? "" };
                }
                else
                {
                    var key = r.MetricKey.Trim();
                    if (!AlertRule.ValidKey(key)) { await Json(ctx, new { ok = false, error = "Érvénytelen metrika-kulcs." }); return; }
                    if (!double.IsFinite(r.Threshold)) { await Json(ctx, new { ok = false, error = "Hibás küszöbérték." }); return; }
                    if (r.Id.StartsWith(AlertStore.HubPrefix)) { await Json(ctx, new { ok = false, error = "A Központ saját szabálya a Küszöb-szabályok lapon módosítható." }); return; }
                    // A végleges ellenőrzés a telephelyen (SiteOps.SaveLocalRule) – az azonosítót itt adjuk, hogy a felület kövesse.
                    op = new HubSiteOp
                    {
                        Kind = "rule-save", By = user ?? "",
                        Rule = new SiteAlertRule
                        {
                            Id = r.Id.Length > 0 ? r.Id : Guid.NewGuid().ToString("N"), Name = r.Name.Trim(), MetricKey = key, Op = Math.Clamp(r.Op, 0, 2),
                            Threshold = r.Threshold, Group = r.AgentId.Length > 0 ? "" : r.Group.Trim(), AgentId = r.AgentId.Trim(),
                            ForSamples = Math.Clamp(r.ForSamples, 1, 60), Enabled = r.Enabled, DisplayUnit = AlertRule.CleanUnit(r.DisplayUnit)
                        }
                    };
                }
                var err = _store.QueueSiteOp(r.SiteId, op);
                if (err is null) AuditLog.Write(HubPaths.DataDir, user!, op.Kind == "rule-save" ? "telephely-szabály mentése" : "telephely-szabály törlése",
                    $"{_store.Sites.FirstOrDefault(x => x.SiteId == r.SiteId)?.Name}: {op.Rule?.Name ?? op.Id}");
                await Json(ctx, new { ok = err is null, error = err });
                return;
            }

            // ---- 1.5.7: programkérések a gépek tálca-ikonjáról (a telephelyek jóváhagyási sora)
            case "/web/api/requests":
            {
                var me = _users.Find(user ?? "");
                var list = _store.Sites.Where(s => !s.Revoked && (me is null || me.CanSeeSite(s.SiteId)))
                    .SelectMany(s => (s.LastSummary?.ProgramRequests ?? new()).Select(p => (s, p)))
                    .OrderBy(x => x.p.State == "pending" ? 0 : 1).ThenByDescending(x => x.p.CreatedUtc).Take(500).ToList();
                await Json(ctx, new
                {
                    items = list.Select(x => new
                    {
                        siteId = x.s.SiteId, site = x.s.Name, id = x.p.Id, machine = x.p.Machine, user = x.p.User, packageId = x.p.PackageId, name = x.p.Name,
                        reason = x.p.Reason, state = x.p.State, note = x.p.Note, decidedBy = x.p.DecidedBy, supports = x.s.LastSummary?.SupportsSiteOps == true,
                        created = x.p.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm"), decided = x.p.DecidedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm")
                    }),
                    pending = list.Count(x => x.p.State == "pending")
                });
                return;
            }
            case "/web/api/request-decide":
            {
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
                var r = ProtocolJson.Deserialize<SiteRuleReq>(body);
                if (r is null || !CanTouch(user, r.SiteId) || r.Id.Length == 0) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                var note = r.Note.Trim(); if (note.Length > 500) note = note[..500];
                var err = _store.QueueSiteOp(r.SiteId, new HubSiteOp { Kind = r.Approve ? "request-approve" : "request-deny", Id = r.Id, Note = note, By = user ?? "" });
                if (err is null) AuditLog.Write(HubPaths.DataDir, user!, r.Approve ? "programkérés jóváhagyása" : "programkérés elutasítása", r.Id);
                await Json(ctx, new { ok = err is null, error = err });
                return;
            }

            // ---- 1.5.7: a telephelyek naplói (központi napló; az USB-napló a telephelyen marad) – csak admin
            case "/web/api/site-logs":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<SiteRuleReq>(body);
                var s = r is null || !CanTouch(user, r.SiteId) ? null : _store.Sites.FirstOrDefault(x => x.SiteId == r.SiteId);
                if (s is null) { await Json(ctx, new { ok = false, error = "Nincs ilyen telephely." }); return; }
                var log = HubSiteLogs.Logs.Any(l => l.Log == r!.Log) ? r!.Log : "audit";
                var (lines, scanned, total) = HubSiteLogs.Tail(s.SiteId, log, r!.Q, 500);
                await Json(ctx, new
                {
                    ok = true, name = s.Name, log, lines, scanned, total, supports = s.LastSummary?.SupportsSiteOps == true,
                    logs = HubSiteLogs.Logs.Select(l => new { log = l.Log, label = l.Label }), sizes = HubSiteLogs.Sizes(s.SiteId)
                });
                return;
            }

            // ---- sérülékenységek (1.5.6): állapot + összesítés mindenkinek (a saját telephelyeire), beállítás adminnak
            case "/web/api/cve-status":
            {
                var me = _users.Find(user ?? "");
                var siteName = _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId)).ToDictionary(s => s.SiteId, s => s);
                var all = HubCve.All().Where(x => siteName.ContainsKey(x.SiteId)).ToList();
                string MachineOf(string sid, string aid) => siteName[sid].LastSummary?.Agents.FirstOrDefault(a => a.AgentId == aid)?.MachineName ?? aid;
                var st = HubCve.Status;
                await Json(ctx, new
                {
                    enabled = _config.CveEnabled, hour = _config.CveHour, hasKey = _config.NvdKeyProtected.Length > 0,
                    running = st.Running, phase = st.Phase, done = st.Done, total = st.Total, current = st.Current,
                    lastRun = st.LastRunUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"), lastSeconds = st.LastSeconds,
                    lastError = st.LastError, products = st.Products, requests = st.Requests,
                    machines = all.Where(x => x.Agent.Findings.Count > 0)
                        .OrderByDescending(x => x.Agent.Exploited).ThenByDescending(x => x.Agent.Critical).ThenByDescending(x => x.Agent.High).Take(300)
                        .Select(x => new
                        {
                            siteId = x.SiteId, site = siteName[x.SiteId].Name, agentId = x.Agent.AgentId, machine = MachineOf(x.SiteId, x.Agent.AgentId),
                            critical = x.Agent.Critical, high = x.Agent.High, medium = x.Agent.Medium, low = x.Agent.Low, exploited = x.Agent.Exploited
                        }),
                    // a leggyakoribb / legsúlyosabb CVE-k a teljes flottán (hány gépet érint)
                    top = all.SelectMany(x => x.Agent.Findings).GroupBy(f => f.Id)
                        .Select(g => new { f = g.First(), n = g.Count(), products = g.Select(x => x.Product).Distinct().Take(3) })
                        .OrderByDescending(x => x.f.Kev).ThenByDescending(x => x.f.Score).ThenByDescending(x => x.n).Take(150)
                        .Select(x => new
                        {
                            id = x.f.Id, score = x.f.Score, severity = x.f.Severity, kev = x.f.Kev, ransomware = x.f.Ransomware, epss = x.f.Epss,
                            machines = x.n, product = string.Join(", ", x.products), fixedIn = x.f.FixedIn, summary = x.f.Summary
                        }),
                    totals = new
                    {
                        machines = all.Count(x => x.Agent.Findings.Count > 0), critical = all.Sum(x => x.Agent.Critical), high = all.Sum(x => x.Agent.High),
                        exploited = all.Sum(x => x.Agent.Exploited)
                    }
                });
                return;
            }
            case "/web/api/cve-run":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
                if (!HubCve.Status.Running)
                    _ = Task.Run(() => HubCve.RunAsync(() => _store.Sites, _config, HubLog.Write, CancellationToken.None));
                AuditLog.Write(HubPaths.DataDir, user!, "CVE-ellenőrzés indítása");
                await Json(ctx, new { ok = true });
                return;
            case "/web/api/cve-save":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<CveReq>(body);
                if (r is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                var key = (r.NvdPassword ?? "").Trim();
                // Az NVD API-kulcs egy GUID (36 karakter); más formátum elírás.
                if (key.Length > 0 && !Guid.TryParse(key, out _))
                { await Json(ctx, new { ok = false, error = "Érvénytelen NVD API-kulcs: a kulcs 36 karakteres azonosító (xxxxxxxx-xxxx-…), amit az NVD e-mailben küld." }); return; }
                _config.CveEnabled = r.Enabled;
                _config.CveHour = Math.Clamp(r.Hour, 0, 23);
                if (r.ClearKey) _config.NvdKeyProtected = "";
                else if (key.Length > 0) _config.NvdKeyProtected = NativeDpapi.Protect(key);
                _config.Save(HubPaths.DataDir);
                AuditLog.Write(HubPaths.DataDir, user!, $"CVE-keresés: {(r.Enabled ? "be" : "ki")}, {_config.CveHour} órától" +
                    (r.ClearKey ? ", NVD-kulcs törölve" : key.Length > 0 ? ", NVD-kulcs megadva" : ""));
                await Json(ctx, new { ok = true });
                return;
            }

            // ---- 1.5.9: WireGuard (csak admin; a privát kulcs soha nem megy a felületre)
            case "/web/api/wg-settings":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var stats = HubWg.Stats().ToDictionary(x => x.PublicKey);
                await Json(ctx, new
                {
                    ok = true, enabled = _config.WgEnabled, port = _config.WgListenPort, subnet = _config.WgSubnet, endpoint = _config.WgEndpoint,
                    effectiveEndpoint = HubWg.Endpoint(_config), keepalive = _config.WgKeepalive, strictAll = _config.WgStrictAll,
                    publicKey = _config.WgPublicKey, hubAddress = HubWg.HubAddress(_config), running = HubWg.Running, lastError = HubWg.LastError,
                    sites = _store.Sites.Where(s => !s.Revoked).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new
                    {
                        siteId = s.SiteId, name = s.Name, address = s.WgAddress, hasKey = WgKeys.IsKey(s.WgPublicKey), active = s.WgActive,
                        required = HubWg.Required(_config, s), siteError = s.LastSummary?.WgError ?? "",
                        handshake = stats.TryGetValue(s.WgPublicKey, out var st) ? st.LastHandshakeUtc?.ToLocalTime().ToString("MM.dd HH:mm:ss") : null,
                        rx = stats.TryGetValue(s.WgPublicKey, out var st2) ? (long)st2.RxBytes : 0, tx = stats.TryGetValue(s.WgPublicKey, out var st3) ? (long)st3.TxBytes : 0
                    })
                });
                return;
            }
            case "/web/api/wg-save":
            case "/web/api/wg-site":
            case "/web/api/wg-rekey":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<WgReq>(body) ?? new();
                if (path.EndsWith("site"))
                {
                    if (!CanTouch(user, r.SiteId)) { await Json(ctx, new { ok = false, error = "Nincs jogod ehhez a telephelyhez." }); return; }
                    _store.SetWgActive(r.SiteId, r.Active);
                    AuditLog.Write(HubPaths.DataDir, user!, $"WireGuard – telephely szigorú módja: {(r.Active ? "be" : "ki")}", r.SiteId);
                    await Json(ctx, new { ok = true }); return;
                }
                if (path.EndsWith("rekey"))
                {
                    // Új Központ-kulcs: a telephelyek a sikertelen kézfogás után a /hub/wg-key-en át (aláírva) kapják meg.
                    _config.WgPrivateKeyProtected = ""; _config.WgPublicKey = "";
                    HubWg.Keys(_config); HubWg.Apply(_config, _store);
                    AuditLog.Write(HubPaths.DataDir, user!, "WireGuard – új Központ-kulcs", _config.WgPublicKey[..8] + "…");
                    await Json(ctx, new { ok = true }); return;
                }
                if (r.Port is < 1024 or > 65535) { await Json(ctx, new { ok = false, error = "A port 1024 és 65535 között legyen." }); return; }
                var net = WgNet.Parse(r.Subnet);
                if (net is null) { await Json(ctx, new { ok = false, error = "A címtartomány magáncímes IPv4 CIDR legyen /16 és /29 között (pl. 10.77.0.0/24)." }); return; }
                var ep = (r.Endpoint ?? "").Trim();
                if (ep.Length > 0 && (ep.Length > 200 || ep.Contains('/') || ep.Contains(' ') || !ep.Contains(':')))
                { await Json(ctx, new { ok = false, error = "A végpont gépnév:port alakú legyen (pl. fleet.poltoli.hu:51820), vagy üres." }); return; }
                var subnetChanged = net.Cidr != (WgNet.Parse(_config.WgSubnet)?.Cidr ?? "");
                _config.WgEnabled = r.Enabled; _config.WgListenPort = r.Port; _config.WgSubnet = net.Cidr; _config.WgEndpoint = ep;
                _config.WgKeepalive = Math.Clamp(r.Keepalive, 0, 600); _config.WgStrictAll = r.StrictAll;
                _config.Save(HubPaths.DataDir);
                // Új címtartomány: a telephelyek új címet kapnak (a következő betárcsázás / kulcsfrissítés során).
                if (subnetChanged) foreach (var s in _store.Sites.Where(s => WgKeys.IsKey(s.WgPublicKey))) _store.SetWgKey(s, s.WgPublicKey);
                if (_config.WgEnabled) HubWg.Apply(_config, _store); else HubWg.Stop();
                AuditLog.Write(HubPaths.DataDir, user!, $"WireGuard: {(_config.WgEnabled ? "be" : "ki")}, UDP {_config.WgListenPort}, {_config.WgSubnet}, végpont {HubWg.Endpoint(_config)}, mindenkinek kötelező: {(_config.WgStrictAll ? "igen" : "nem")}");
                await Json(ctx, new { ok = true });
                return;
            }

            // ---- Janus-kapcsolat (csak admin; a titok soha nem megy vissza a felületre)
            case "/web/api/janus-settings":
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                await Json(ctx, new
                {
                    url = _config.JanusUrl, enabled = _config.JanusEnabled,
                    sites = _store.Sites.Where(s => !s.Revoked).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => new
                    {
                        siteId = s.SiteId, name = s.Name, keyId = s.JanusKeyId, hasSecret = s.JanusSecretProtected.Length > 0,
                        lastOk = HubJanus.State.TryGetValue(s.SiteId, out var st) ? st.Ok?.ToLocalTime().ToString("MM.dd HH:mm") : null,
                        lastError = HubJanus.State.TryGetValue(s.SiteId, out var st2) ? st2.Error : ""
                    })
                });
                return;
            case "/web/api/janus-save":
            case "/web/api/janus-site":
            case "/web/api/janus-test":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<JanusReq>(body);
                if (r is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                if (path.EndsWith("save"))
                {
                    var url = r.Url.Trim();
                    if (HubJanus.ValidUrl(url) is { } e) { await Json(ctx, new { ok = false, error = e }); return; }
                    _config.JanusUrl = url; _config.JanusEnabled = r.Enabled && url.Length > 0;
                    _config.Save(HubPaths.DataDir);
                    AuditLog.Write(HubPaths.DataDir, user!, $"Janus-kapcsolat: {(_config.JanusEnabled ? "be" : "ki")} ({url})");
                    await Json(ctx, new { ok = true }); return;
                }
                var s = _store.Sites.FirstOrDefault(x => x.SiteId == r.SiteId && !x.Revoked);
                if (s is null) { await Json(ctx, new { ok = false, error = "Nincs ilyen telephely." }); return; }
                if (path.EndsWith("site"))
                {
                    var key = r.KeyId.Trim().ToLowerInvariant();
                    if (key.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-f0-9]{16}$"))
                    { await Json(ctx, new { ok = false, error = "A kulcsazonosító 16 hexadecimális karakter (a Janus adja)." }); return; }
                    var sec = r.JanusPassword.Trim();
                    // A Janus titka 32 véletlen bájt base64url-ben (43 karakter): más formátum elírás / rossz mező.
                    if (sec.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(sec, "^[A-Za-z0-9_-]{32,128}$"))
                    { await Json(ctx, new { ok = false, error = "Érvénytelen titok: a Janus által adott, 43 karakteres titkot másold be (szóköz nélkül)." }); return; }
                    // Egy Janus-kulcs egy ügyfélhez (telephelyhez) tartozik: két telephely gépei ne keveredjenek egy ügyfélnél.
                    if (key.Length > 0 && !r.Clear && _store.Sites.FirstOrDefault(x => x.SiteId != s.SiteId && !x.Revoked && x.JanusKeyId == key) is { } other)
                    { await Json(ctx, new { ok = false, error = $"Ez a Janus-kulcs már a(z) „{other.Name}” telephelynél van megadva – telephelyenként külön kulcs kell." }); return; }
                    _store.SetJanus(s.SiteId, key, r.Clear || key.Length == 0 ? "" : sec.Length > 0 ? NativeDpapi.Protect(sec) : null);
                    AuditLog.Write(HubPaths.DataDir, user!, $"Janus-kulcs ({s.Name}): {(key.Length == 0 || r.Clear ? "törölve" : key)}");
                    await Json(ctx, new { ok = true }); return;
                }
                var (ok, msg) = await HubJanus.Test(s);
                if (ok) _ = HubJanus.Inventory(true, s.SiteId);   // sikeres próba után a géplista is azonnal megy
                await Json(ctx, new { ok, error = ok ? null : msg, message = ok ? msg : null });
                return;
            }

            // ---- e-mail értesítés (csak admin; a jelszó soha nem megy vissza a felületre)
            case "/web/api/mail-settings":
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                await Json(ctx, new
                {
                    enabled = _config.MailEnabled, host = _config.SmtpHost, port = _config.SmtpPort, security = _config.SmtpSecurity,
                    user = _config.SmtpUser, hasPassword = _config.SmtpPasswordProtected.Length > 0, from = _config.MailFrom,
                    to = string.Join(", ", _config.MailTo), lastSent = HubMailer.LastSentUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"),
                    lastError = HubMailer.LastError, queued = HubMailer.Queued
                });
                return;
            case "/web/api/mail-save":
            case "/web/api/mail-test":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<MailReq>(body);
                if (r is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                var err = HubMailer.Apply(_config, r.Enabled, r.Host, r.Port, r.Security, r.User, r.SmtpPassword, r.ClearPassword, r.From, r.To);
                if (err is not null) { await Json(ctx, new { ok = false, error = err }); return; }
                _config.Save(HubPaths.DataDir);
                if (path.EndsWith("save")) { await Json(ctx, new { ok = true }); return; }
                var (ok, msg) = await HubMailer.SendTestAsync(_config, user!);
                await Json(ctx, new { ok, error = ok ? null : msg, message = ok ? msg : null });
                return;
            }

            // ---- a látható telephelyek gépein levő szolgáltatások (a „Mit mérjen?” választólistájához)
            case "/web/api/services-catalog":
            {
                var me = _users.Find(user ?? "");
                var d = new Dictionary<string, SiteService>(StringComparer.OrdinalIgnoreCase);
                var vis = _store.Sites.Where(s => !s.Revoked && (me is null || me.CanSeeSite(s.SiteId))).ToList();
                foreach (var s in vis)
                    foreach (var x in s.LastSummary?.Services ?? new())
                    {
                        if (!d.TryGetValue(x.Name, out var y)) d[x.Name] = y = new SiteService { Name = x.Name, DisplayName = x.DisplayName };
                        y.Machines += x.Machines; y.Running += x.Running;
                    }
                await Json(ctx, new
                {
                    services = d.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                    machines = vis.Sum(s => s.LastSummary?.Agents.Count ?? 0), reported = vis.Count(s => s.LastSummary?.Services is { Count: > 0 })
                });
                return;
            }

            // ---- metrika-katalógus a szabály-szerkesztőhöz: a látható telephelyek gépeinek kulcsai + „bármelyik” minták
            case "/web/api/metric-catalog":
                await Json(ctx, MetricCatalog.Build(_metrics.AllKeys(VisibleMachines(user).Keys.ToHashSet())));
                return;

            // ---- a Központ mérés-kérése a telephelyeknek („Mit mérjen?”): a telephely a sajátjával összefésüli
            case "/web/api/monitor-config":
            {
                var me = _users.Find(user ?? "");
                var m = _config.MonitorAsConfig();
                await Json(ctx, new
                {
                    cpu = m.Cpu, memory = m.Memory, disk = m.Disk, diskSmart = m.DiskSmart, temperature = m.Temperature, eventLog = m.EventLog,
                    network = m.Network, diskIo = m.DiskIo, cpuDetail = m.CpuDetail, perCore = m.PerCore, hardware = m.Hardware,
                    sensorDriver = m.SensorDriver, gpu = m.Gpu, security = m.Security, hyperV = m.HyperV, ping = m.Ping, pingTargets = m.PingTargets,
                    services = m.Services, processes = m.Processes, sampleSeconds = m.SampleSeconds,
                    appUpdateMinutes = m.AppUpdateMinutes, wuScanHours = m.WuScanHours, watchSeconds = m.WatchSeconds, usbAudit = m.UsbAudit,
                    pushEnabled = _config.HubMonEnabled, pushSites = _config.HubMonSites,
                    allSites = _store.Sites.Where(s => !s.Revoked && (me is null || me.CanSeeSite(s.SiteId))).OrderBy(s => s.Name).Select(s => new { siteId = s.SiteId, name = s.Name })
                });
                return;
            }
            case "/web/api/monitor-save":
            {
                // Több telephelyre hat - mint a Központ többi közös beállítása, csak admin.
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var mc = ProtocolJson.Deserialize<MetricConfig>(body);
                var pr = ProtocolJson.Deserialize<HubMonPushReq>(body);
                if (mc is null || pr is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                _config.ApplyMonitor(mc);
                _config.HubMonEnabled = pr.Enabled;
                _config.HubMonSites = (pr.Sites ?? new()).Where(id => _store.Sites.Any(s => s.SiteId == id)).Distinct().ToList();
                _config.Save(HubPaths.DataDir);
                AuditLog.Write(HubPaths.DataDir, user!, $"mérés-kérés a telephelyeknek: {(pr.Enabled ? "be" : "ki")}" +
                    (_config.HubMonSites.Count > 0 ? $" ({_config.HubMonSites.Count} telephely)" : " (minden telephely)"));
                await Json(ctx, new { ok = true });
                return;
            }

            // ---- csoportok telephelyenként: a telephely összesítőjéből; módosítás a telephelyre megy le
            case "/web/api/groups":
            {
                var me = _users.Find(user ?? "");
                await Json(ctx, new
                {
                    sites = _store.Sites.Where(s => !s.Revoked && (me is null || me.CanSeeSite(s.SiteId))).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s =>
                    {
                        var ag = s.LastSummary?.Agents.Where(a => !a.Revoked).ToList() ?? new();
                        var names = (s.LastSummary?.GroupNames ?? new()).Concat(ag.SelectMany(a => a.Groups))
                            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
                        // Előbb a kézi csoportok, utána az automatikusak (a gépek adataiból, a Központban számolva - csak olvasható).
                        return new
                        {
                            siteId = s.SiteId, name = s.Name, online = s.IsOnline,
                            supports = s.LastSummary?.SupportsGroupOps == true, pending = s.PendingGroupOps.Count,
                            groups = names.Select(g => new { name = g, auto = false, members = ag.Where(a => a.Groups.Contains(g, StringComparer.OrdinalIgnoreCase)).Select(a => a.AgentId) })
                                .Concat(AutoGroupsOf(s, names).Select(g => new { name = g, auto = true, members = ag.Where(a => GroupsOf(a).Contains(g, StringComparer.OrdinalIgnoreCase)).Select(a => a.AgentId) })),
                            agents = ag.OrderBy(a => a.MachineName, StringComparer.CurrentCultureIgnoreCase)
                                .Select(a => new { agentId = a.AgentId, machineName = a.MachineName, online = a.Online && s.IsOnline, groups = a.Groups })
                        };
                    }),
                    autoOn = _config.AutoGroupsOn
                });
                return;
            }
            // ---- automatikus csoportok be/ki a Központban (a telephelyek a sajátjukat a saját beállításuk szerint számolják)
            case "/web/api/auto-groups":
            {
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
                var r = ProtocolJson.Deserialize<HubAutoGroupsReq>(body);
                if (r is null) { await Json(ctx, new { ok = false, error = "Hibás kérés." }); return; }
                _config.AutoGroupsOn = r.On;
                _config.Save(HubPaths.DataDir);
                AuditLog.Write(HubPaths.DataDir, user!, "automatikus csoportok: " + (r.On ? "be" : "ki"));
                await Json(ctx, new { ok = true });
                return;
            }
            // ---- tárolási helyek (admin): naplók, mentések, mérési adatbázis - áthelyezés átköltöztetéssel
            case "/web/api/storage":
                if (NeedAdmin()) { await Json(ctx, new { error = "admin jog kell" }); return; }
                await Json(ctx, Storage.Info(HubPaths.DataDir, _config, _metrics));
                return;
            case "/web/api/storage-save":
            {
                if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                var r = ProtocolJson.Deserialize<Storage.Req>(body) ?? new();
                var changes = new List<string>();
                var err = Storage.Save(HubPaths.DataDir, _config, _metrics, r, changes);
                _config.Save(HubPaths.DataDir);   // a sikeres részlépések akkor is megmaradnak, ha egy későbbi elakadt
                if (changes.Count > 0)
                {
                    AuditLog.Write(HubPaths.DataDir, user!, "tárolási helyek módosítása: " + string.Join("; ", changes));
                    HubLog.Write("Tárolás: " + string.Join("; ", changes));
                }
                await Json(ctx, new { ok = err is null, error = err, changed = changes });
                return;
            }
            case "/web/api/group-op":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<HubGroupReq>(body);
                var s = r is null || !CanTouch(user, r.SiteId) ? null : _store.Sites.FirstOrDefault(x => x.SiteId == r.SiteId && !x.Revoked);
                if (s is null || r is null) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                if (s.LastSummary?.SupportsGroupOps != true)
                { await Json(ctx, new { ok = false, error = "Ez a telephely még nem ismeri (1.5.0 előtti) – előbb frissítsd a telephely szerverét." }); return; }
                var g = GroupOps.Clean(r.Group);
                var nn = GroupOps.Clean(r.NewName);
                if (r.Op is not ("create" or "delete" or "rename" or "members")) { await Json(ctx, new { ok = false, error = "Ismeretlen csoport-művelet." }); return; }
                if (!GroupOps.ValidName(g) || (r.Op == "rename" && !GroupOps.ValidName(nn)))
                { await Json(ctx, new { ok = false, error = "A csoport neve 1–40 karakter lehet, vessző nélkül." }); return; }
                // Automatikus csoport (Szerverek, Windows 11…) tagsága nem küldhető le kézzel; a régi, azonos nevű kézi csoport kezelhető marad.
                var manualNames = (s.LastSummary?.GroupNames ?? new()).Concat((s.LastSummary?.Agents ?? new()).SelectMany(a => a.Groups));
                if (_config.AutoGroupsOn && ((AutoGroups.IsAutoName(g) && !manualNames.Contains(g, StringComparer.OrdinalIgnoreCase)) || (r.Op == "rename" && AutoGroups.IsAutoName(nn))))
                { await Json(ctx, new { ok = false, error = "Ez automatikus csoport (az operációs rendszer szerint) – a tagsága magától alakul, kézzel nem módosítható." }); return; }
                var ids = (r.AgentIds ?? new()).Where(id => s.LastSummary?.Agents.Any(a => a.AgentId == id) == true).Distinct().ToList();
                _store.QueueGroupOp(s.SiteId, new HubGroupOp { Op = r.Op, Group = g, NewName = nn, AgentIds = ids });
                AuditLog.Write(HubPaths.DataDir, user!, $"csoport ({s.Name}): {r.Op} {g}" + (nn.Length > 0 ? " -> " + nn : "") + (r.Op == "members" ? $" ({ids.Count} gép)" : ""));
                await Json(ctx, new { ok = true });
                return;
            }

            case "/web/api/monitor-settings":
            {
                var r = ProtocolJson.Deserialize<HubMonReq>(body);
                if (r is { DetailDays: > 0 })
                {
                    if (NeedAdmin()) { await Json(ctx, new { ok = false, error = "admin jog kell" }); return; }
                    _config.MonDetailDays = Math.Clamp(r.DetailDays, 1, 365);
                    _config.MonKeepDays = Math.Clamp(r.KeepDays, _config.MonDetailDays, 3650);
                    _config.Save(HubPaths.DataDir);
                    AuditLog.Write(HubPaths.DataDir, user!, $"megőrzés: {_config.MonDetailDays} / {_config.MonKeepDays} nap");
                }
                await Json(ctx, new { ok = true, detailDays = _config.MonDetailDays, keepDays = _config.MonKeepDays });
                return;
            }
            case "/web/api/revoke":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<QReq>(body);
                if (r?.SiteId is { Length: > 0 } && !CanTouch(user, r.SiteId)) { ctx.Response.StatusCode = 403; await Json(ctx, new { ok = false, error = "nincs jogod ehhez a telephelyhez" }); return; }
                if (r?.SiteId is { Length: > 0 }) _store.Revoke(r.SiteId);
                await Json(ctx, new { ok = true });
                return;
            }
            case "/web/api/settings":
                if (NeedKezelo()) { await Json(ctx, new { ok = false, error = "kezelői jog kell" }); return; }
            {
                var r = ProtocolJson.Deserialize<SetReq>(body);
                if (r is not null)
                {
                    _config.TokenLifetimeMinutes = Math.Clamp(r.TokenLifetimeMinutes, 0, 43200);
                    _config.TokenMaxUses = Math.Clamp(r.TokenMaxUses, 0, 100000);
                    // Adatmegőrzés és naplóméret: csak admin (a régi adatok törlését is jelentheti).
                    if (r.DetailDays is not null || r.KeepDays is not null || r.LogMaxMb is not null || r.SiteKeepDays is not null || r.ArchiveDays is not null)
                    {
                        if (role < Role.Admin) { await Json(ctx, new { ok = false, error = "Az adatmegőrzést és a naplóméretet csak admin állíthatja." }); return; }
                        _config.MonDetailDays = Math.Clamp(r.DetailDays ?? _config.MonDetailDays, 1, 365);
                        _config.MonKeepDays = Math.Clamp(r.KeepDays ?? _config.MonKeepDays, _config.MonDetailDays, 3650);
                        // 1.5.8: a telephelyek szerverein (utána a lezárt napi fájl ide jön) és az archívum összesen
                        _config.SiteMetricKeepDays = Math.Clamp(r.SiteKeepDays ?? _config.SiteMetricKeepDays, 2, 3650);
                        _config.MetricArchiveDays = Math.Clamp(r.ArchiveDays ?? _config.MetricArchiveDays, _config.MonKeepDays, 3650);
                        _config.LogMaxMb = Math.Clamp(r.LogMaxMb ?? _config.LogMaxMb, 1, 100);
                        LogLimits.SetMb(_config.LogMaxMb);
                        AuditLog.Write(HubPaths.DataDir, user!, $"adatmegőrzés: {_config.MonDetailDays}/{_config.MonKeepDays} nap, archívum {_config.MetricArchiveDays} nap, telephelyeken {_config.SiteMetricKeepDays} nap, napló {_config.LogMaxMb} MB");
                    }
                    _config.Save(HubPaths.DataDir);
                }
                await Json(ctx, new { ok = true });
                return;
            }
        }
        ctx.Response.StatusCode = 404; await Json(ctx, new { error = "ismeretlen" });
    }

    private static async Task ServeReport(HttpListenerContext ctx, string user)
    {
        var kind = ctx.Request.QueryString["r"] ?? "";
        var days = int.TryParse(ctx.Request.QueryString["napok"], out var nd) ? Math.Clamp(nd, 1, 3650) : 7;
        var me = _users.Find(user);
        var sites = _store.Sites.Where(s => me is null || me.CanSeeSite(s.SiteId)).ToList();
        // A telephely összesítője a legutóbbi betárcsázáskori állapot: kiesett telephely gépe nem számít online-nak.
        var machines = sites.SelectMany(s => (s.LastSummary?.Agents ?? new()).Select(a => new CsvReport.Machine(s.Name, a.AgentId, a.MachineName,
            a.OperatingSystem, a.AgentVersion, a.Online && s.IsOnline, a.Revoked, s.AgentLastSeenHubUtc(a), a.Groups, a.Updates,
            a.WindowsUpdates, a.Installed, a.PendingReboot))).ToList();
        byte[]? data;
        try
        {
            data = kind switch
            {
                "gepek" => CsvReport.Machines(machines, true),
                "programok" => CsvReport.Programs(machines, true),
                "frissitesek" => CsvReport.Updates(machines, true),
                "riasztasok" => CsvReport.Alerts(AlertLog.Entries().Where(e => sites.Any(s => e.Raw.Contains(s.Name + " / "))), true),
                "metrikak" => CsvReport.Metrics(_metrics.Daily(days), machines.GroupBy(m => m.AgentId).ToDictionary(g => g.Key, g => (g.First().Site, g.First().Name)), true),
                _ => null
            };
        }
        catch (Exception ex) { ctx.Response.StatusCode = 500; await Json(ctx, new { error = "A jelentés nem készült el: " + ex.Message }); return; }
        if (data is null) { ctx.Response.StatusCode = 404; await Json(ctx, new { error = "ismeretlen jelentés" }); return; }

        AuditLog.Write(HubPaths.DataDir, user, "jelentés letöltése", kind + (kind == "metrikak" ? $" ({days} nap)" : ""));
        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers["Content-Disposition"] = $"attachment; filename={CsvReport.FileName("kozpont", kind)}";
        ctx.Response.ContentLength64 = data.Length;
        await ctx.Response.OutputStream.WriteAsync(data);
    }

    private static async Task UploadHub(HttpListenerContext ctx)
    {
        try
        {
            Directory.CreateDirectory(HubPaths.InstallDir);
            var target = Path.Combine(HubPaths.InstallDir, "FleetHub.new.exe");
            await using (var fs = File.Create(target))
                await ctx.Request.InputStream.CopyToAsync(fs);

            var ok = new FileInfo(target).Length > 1_000_000;
            byte[] head = new byte[2];
            await using (var r = File.OpenRead(target)) { _ = r.Read(head, 0, 2); }
            ok &= head[0] == (byte)'M' && head[1] == (byte)'Z';
            if (!ok) { try { File.Delete(target); } catch { } ctx.Response.StatusCode = 400; await Json(ctx, new { ok = false, error = "Nem érvényes Windows exe." }); return; }

            // Szolgaltataskent azonnal indul a csere: az uj exe 2 mp mulva leallitja, kicserel, ujrainditja
            // (es ha nem indul el, visszaallitja a regit). Kezi futtatasnal marad a "kovetkezo inditaskor".
            var now = !Environment.UserInteractive &&
                      ServiceSelfUpdate.LaunchIfPending(HubPaths.ServiceName, Path.Combine(HubPaths.InstallDir, "FleetHub.exe"), HubLog.Write);
            await Json(ctx, new { ok = true, message = now
                ? "Feltöltve – a frissítés elindult, a Központ néhány másodperc múlva újraindul (frissítsd az oldalt)."
                : "Feltöltve. A Központ újraindításakor lép életbe." });
        }
        catch (Exception ex) { ctx.Response.StatusCode = 500; await Json(ctx, new { ok = false, error = ex.Message }); }
    }

    private static async Task Setup(HttpListenerContext ctx)
    {
        var f = await Form(ctx);
        var pw = f.GetValueOrDefault("password", ""); var pw2 = f.GetValueOrDefault("password2", "");
        var domain = f.GetValueOrDefault("domain", "").Trim();
        if (pw != pw2) { await Html(ctx, HubWebUi.SetupPage("A két jelszó nem egyezik.", _config.WebPort, _config.AgentPort, domain)); return; }
        var st = ServerConfig.CheckPasswordStrength(pw);
        if (!st.Ok) { await Html(ctx, HubWebUi.SetupPage(st.Message, _config.WebPort, _config.AgentPort, domain)); return; }
        if (domain.Length < 3 || !domain.Contains('.')) { await Html(ctx, HubWebUi.SetupPage("Adj meg egy valódi domaint (pl. fleet.poltoli.hu).", _config.WebPort, _config.AgentPort, domain)); return; }
        int wp = int.TryParse(f.GetValueOrDefault("webPort", ""), out var a) ? a : 8090;

        _config.AdminUser = f.GetValueOrDefault("user", "admin").Trim();
        _config.SetPassword(pw);
        _users.EnsureFirstAdmin(_config.AdminUser, pw);
        _config.WebPort = wp; _config.HubDomain = domain;
        _config.Save(HubPaths.DataDir);

        // NGINX Proxy Manager utmutato generalasa (a te NPM-ed adja a Let's Encrypt tanusitvanyt).
        ProxyHint.Write(domain, wp, HubInstaller.LocalIp());

        SetCookie(ctx, "hub_session", _sessions.Issue(_config.AdminUser, Role.Admin));
        await Html(ctx, $$"""
        <!DOCTYPE html><html lang="hu"><head><meta charset="utf-8"><meta http-equiv="refresh" content="5;url=/web">
        <title>Kész</title><style>body{background:#0f1116;color:#e6e9ef;font-family:"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh;text-align:center}a{color:#5b8cff}code{color:#7c5cff}</style></head><body><div>
        <h1>A Központ beállítva</h1>
        <p>A Központ a <b>{{HubInstaller.LocalIp()}}:{{wp}}</b> címen figyel (minden gépről elérhető a belső hálón).</p>
        <p>Az NGINX Proxy Manager beállításához az útmutató itt van:</p>
        <p><code>{{HubPaths.CaddyDir}}\NPM-beallitas.txt</code></p>
        <p>Röviden: hozz létre egy Proxy Host-ot a(z) <b>{{domain}}</b> domainhez, ami a fenti címre
        irányít, és kérj rá Let's Encrypt tanúsítványt (SSL fül). A Cloudflare maradjon <b>DNS only</b>.</p>
        <p>Ha portot változtattál, indítsd újra a Központ szolgáltatást.</p>
        <p style="color:#8a93a3">Átirányítás…</p></div></body></html>
        """);
    }

    private static async Task Login(HttpListenerContext ctx)
    {
        var f = await Form(ctx);
        var name = f.GetValueOrDefault("user", "").Trim();
        var ip = HubAccess.ClientIp(ctx.Request.RemoteEndPoint?.Address, ctx.Request.Headers["X-Real-IP"], ctx.Request.Headers["X-Forwarded-For"])?.ToString() ?? "?";
        if (LoginThrottle.Blocked(ip) is { } left)
        {
            LoginThrottle.Fail(ip);
            AuditLog.Write(HubPaths.DataDir, name, "belépés tiltva (túl sok próbálkozás)", ip);
            await Html(ctx, HubWebUi.LoginPage($"Túl sok sikertelen próbálkozás. Próbáld újra {Math.Ceiling(left.TotalMinutes)} perc múlva."));
            return;
        }
        var acc = _users.Authenticate(name, f.GetValueOrDefault("password", ""));
        if (acc is not null)
        {
            LoginThrottle.Success(ip);
            AuditLog.Write(HubPaths.DataDir, acc.Username, "belépés", ip);
            SetCookie(ctx, "hub_session", _sessions.Issue(acc.Username, acc.Role)); Redirect(ctx, "/web");
        }
        else
        {
            LoginThrottle.Fail(ip);
            AuditLog.Write(HubPaths.DataDir, name, "sikertelen belépés", ip);
            await Html(ctx, HubWebUi.LoginPage("Hibás felhasználónév, jelszó, vagy letiltott fiók."));
        }
    }

    // ---- HTTP segédek
    static async Task<string> Body(HttpListenerContext c){ if(!c.Request.HasEntityBody) return ""; using var r=new StreamReader(c.Request.InputStream,c.Request.ContentEncoding??Encoding.UTF8); return await r.ReadToEndAsync(); }
    static async Task<Dictionary<string,string>> Form(HttpListenerContext c){ var d=new Dictionary<string,string>(); foreach(var p in (await Body(c)).Split('&',StringSplitOptions.RemoveEmptyEntries)){ var i=p.IndexOf('='); if(i<0)continue; d[Uri.UnescapeDataString(p[..i].Replace('+',' '))]=Uri.UnescapeDataString(p[(i+1)..].Replace('+',' ')); } return d; }
    static async Task Json(HttpListenerContext c,object o){ var b=Encoding.UTF8.GetBytes(ProtocolJson.Serialize(o)); c.Response.ContentType="application/json"; c.Response.ContentLength64=b.Length; await c.Response.OutputStream.WriteAsync(b); }
    static async Task Html(HttpListenerContext c,string h){ var b=Encoding.UTF8.GetBytes(h); c.Response.ContentType="text/html; charset=utf-8"; c.Response.Headers["X-Content-Type-Options"]="nosniff"; c.Response.Headers["X-Frame-Options"]="DENY"; c.Response.ContentLength64=b.Length; await c.Response.OutputStream.WriteAsync(b); }
    static void Redirect(HttpListenerContext c,string u){ c.Response.StatusCode=302; c.Response.Headers["Location"]=u; }
    static void SetCookie(HttpListenerContext c,string n,string v)=>c.Response.Headers["Set-Cookie"]=$"{n}={Uri.EscapeDataString(v)}; Path=/; HttpOnly; SameSite=Strict";
    static void ClearCookie(HttpListenerContext c)=>c.Response.Headers["Set-Cookie"]="hub_session=; Path=/; HttpOnly; Max-Age=0";
    static string? Cookie(HttpListenerContext c,string n){ var h=c.Request.Headers["Cookie"]; if(h is null)return null; foreach(var p in h.Split(';')){ var kv=p.Trim().Split('=',2); if(kv.Length==2&&kv[0]==n)return Uri.UnescapeDataString(kv[1]); } return null; }

    sealed class TokReq { public string Label { get; set; } = ""; }
    sealed class IvalReq { public string SiteId { get; set; } = ""; public int Seconds { get; set; } }
    sealed class QReq { public string SiteId { get; set; } = ""; public string AgentId { get; set; } = ""; public bool All { get; set; } public List<string>? Ids { get; set; } }
    sealed class HubMetricReq { public string SiteId { get; set; } = ""; public string AgentId { get; set; } = ""; public string Key { get; set; } = ""; public int Hours { get; set; } = 24; }
    sealed class HubAlertReq { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string MetricKey { get; set; } = ""; public int Op { get; set; } public double Threshold { get; set; } public string? AgentId { get; set; } = ""; public int ForSamples { get; set; } = 1; public bool Enabled { get; set; } = true; public string? DisplayUnit { get; set; }
        public string Group { get; set; } = ""; public List<string>? Sites { get; set; } public bool PushDown { get; set; } = true; }
    sealed class HubGroupReq { public string SiteId { get; set; } = ""; public string Op { get; set; } = ""; public string Group { get; set; } = ""; public string NewName { get; set; } = ""; public List<string>? AgentIds { get; set; } }
    sealed class HubAutoGroupsReq { public bool On { get; set; } }
    sealed class HubCatCategoryReq { public string Id { get; set; } = ""; public string Category { get; set; } = ""; }
    // A titok mezőneve „pass”-t tartalmaz: a műveleti napló (AuditLog.Api) így kihagyja.
    sealed class JanusReq { public string Url { get; set; } = ""; public bool Enabled { get; set; } public string SiteId { get; set; } = ""; public string KeyId { get; set; } = "";
        public string JanusPassword { get; set; } = ""; public bool Clear { get; set; } }
    sealed class WgReq
    {
        public bool Enabled { get; set; } = true; public int Port { get; set; } = 51820; public string Subnet { get; set; } = "10.77.0.0/24";
        public string? Endpoint { get; set; } public int Keepalive { get; set; } = 25; public bool StrictAll { get; set; }
        public string SiteId { get; set; } = ""; public bool Active { get; set; }
    }
    sealed class SiteRuleReq
    {
        public string SiteId { get; set; } = ""; public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string MetricKey { get; set; } = "";
        public int Op { get; set; } public double Threshold { get; set; } public string AgentId { get; set; } = ""; public string Group { get; set; } = "";
        public int ForSamples { get; set; } = 1; public bool Enabled { get; set; } = true; public string? DisplayUnit { get; set; }
        public bool Approve { get; set; } public string Note { get; set; } = ""; public string Log { get; set; } = ""; public string Q { get; set; } = "";
    }
    // A kulcs mezőneve „…Password”: így a műveleti napló (AuditLog.Api) akkor sem írná ki, ha egyszer felvennénk a listájára.
    sealed class CveReq { public bool Enabled { get; set; } = true; public int Hour { get; set; } = 3; public string? NvdPassword { get; set; } public bool ClearKey { get; set; } }
    sealed class HubMonPushReq { public bool Enabled { get; set; } public List<string>? Sites { get; set; } }
    sealed class HubCatalogReq { public List<CatalogItem>? Items { get; set; } public List<AppBundle>? Bundles { get; set; } }
    sealed class HubSearchReq { public string Q { get; set; } = ""; }
    sealed class HubMonReq { public int DetailDays { get; set; } public int KeepDays { get; set; } }
    sealed class HubTaskReq { public string SiteId { get; set; } = ""; public List<string>? AgentIds { get; set; } public string Kind { get; set; } = ""; public string Target { get; set; } = ""; }
    sealed class HubKeyReq { public int ServerDays { get; set; } = 7; public string Id { get; set; } = ""; }
    sealed class HubOfflineReq { public int Minutes { get; set; } public List<string>? Groups { get; set; } public bool Machines { get; set; } = true; }
    // A jelszó mezőneve „pass”-t tartalmaz: a műveleti napló (AuditLog.Api) így kihagyja.
    sealed class MailReq { public bool Enabled { get; set; } public string Host { get; set; } = ""; public int Port { get; set; } = 587; public string Security { get; set; } = "starttls";
        public string User { get; set; } = ""; public string SmtpPassword { get; set; } = ""; public bool ClearPassword { get; set; } public string From { get; set; } = ""; public string To { get; set; } = ""; }

    /// <summary>A webes művelet ellenőrzése és feladattá alakítása (a telephelyi szerverrel azonos szabályok).</summary>
    static string? ParseTask(string kindName, string rawTarget, out AgentTaskKind kind, out string target)
    {
        kind = default; target = (rawTarget ?? "").Trim();
        switch (kindName)
        {
            case "install":
            case "uninstall":
                if (!WingetId.IsValid(target)) return "Érvénytelen programazonosító.";
                kind = kindName == "install" ? AgentTaskKind.InstallApp : AgentTaskKind.UninstallApp; return null;
            case "upgradeall": kind = AgentTaskKind.UpgradeAll; target = ""; return null;
            case "wuscan": kind = AgentTaskKind.WindowsUpdateScan; target = ""; return null;
            case "wuinstall":
                if (target.Length == 0) target = "all";
                if (!target.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                    !target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(x => Guid.TryParse(x, out _)))
                    return "Érvénytelen frissítés-azonosító.";
                kind = AgentTaskKind.WindowsUpdateInstall; return null;
            case "reboot":
                kind = AgentTaskKind.Reboot;
                target = (int.TryParse(target, out var d) ? Math.Clamp(d, 0, 3600) : 300).ToString(); return null;
            case "servicestart":   // 1.5.10: Windows-szolgáltatás kezelése (a végpont külön admin-jogot is ellenőriz)
            case "servicestop":
            case "servicerestart":
                if (!ServiceName.IsValid(target)) return "Érvénytelen szolgáltatásnév.";
                kind = kindName == "servicestart" ? AgentTaskKind.ServiceStart
                     : kindName == "servicestop" ? AgentTaskKind.ServiceStop : AgentTaskKind.ServiceRestart;
                return null;
            default: return "Ismeretlen művelet.";
        }
    }
    sealed class UsrReq { public string Username { get; set; } = ""; public string Password { get; set; } = ""; public int Role { get; set; } public bool Disabled { get; set; } public List<string>? SiteScope { get; set; } }
    sealed class SetReq { public int TokenLifetimeMinutes { get; set; } public int TokenMaxUses { get; set; } public int? DetailDays { get; set; } public int? KeepDays { get; set; } public int? LogMaxMb { get; set; }
        public int? SiteKeepDays { get; set; } public int? ArchiveDays { get; set; } }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class HubService : System.ServiceProcess.ServiceBase
{
    private Thread? _t;
    public HubService() { ServiceName = HubPaths.ServiceName; }
    protected override void OnStart(string[] args)
    {
        // Feltoltott frissites (FleetHub.new.exe): a cserét az uj exe vegzi, ez a peldany leall.
        if (ServiceSelfUpdate.LaunchIfPending(HubPaths.ServiceName, Path.Combine(HubPaths.InstallDir, "FleetHub.exe"), HubLog.Write))
        { Stop(); return; }
        _t = new Thread(HubProgram.Run) { IsBackground = true }; _t.Start();
    }
    protected override void OnStop() { try { if (HubProgram.Listeners is { } ls) foreach (var l in ls) l.Stop(); } catch { } }
    protected override void OnShutdown() => OnStop();
}
