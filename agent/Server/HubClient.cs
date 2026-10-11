using System.Net.Http.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A telephelyi FleetServer betarcsazasa a Kozpontba.
///
/// Mindig a telephely kezdemenyez (kimeno HTTPS), igy az ugyfelnel nem kell fix IP,
/// DDNS vagy nyitott port. A betarcsazasi idokozt a Kozpont kuldi le minden valaszban.
///
/// Vedelem: TLS (Let's Encrypt a Kozpont domainjen) + mindket iranyban alairt uzenet.
/// A Kozpont kulcsat az elso kapcsolodaskor ujjlenyomattal ellenorizzuk, utana csak
/// azzal alairt valaszt fogadunk el - egy hamis vagy megtort TLS-vegpont sem tud
/// feladatot kuldeni a telephelyre.
/// </summary>
public sealed class HubClient
{
    public static HubClient Instance { get; } = new();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>A telephely metrikatara (a szerver inditasakor allitja be) - innen mennek fel az 5 perces atlagok.</summary>
    public static MetricStore? Metrics { get; set; }
    /// <summary>A beepitett agent verzioja (a szerver allitja be) - a Kozpont ebbol latja, mire frissulnek az agentek.</summary>
    public static Func<string>? AgentPackageVersion { get; set; }
    /// <summary>1.5.6: a gép eszköz-összegzése (a szerver állítja be: ugyanaz, mint a szerver felületén).</summary>
    public static Func<EnrolledAgent, SiteScore>? ScoreOf { get; set; }
    /// <summary>A telephely riasztási tára (a szerver állítja be) - ide kerülnek a Központ küszöb-szabályai.</summary>
    public static AlertStore? Alerts { get; set; }

    /// <summary>A Központ szabálya a telephely riasztási motorjának (az azonosító „hub-” előtagot kap).</summary>
    public static AlertRule ToRule(SiteAlertRule r) => new()
    {
        Id = AlertStore.HubPrefix + r.Id, Name = r.Name, MetricKey = r.MetricKey, Op = (AlertOp)Math.Clamp(r.Op, 0, 2), Threshold = r.Threshold,
        Group = r.Group, AgentId = r.AgentId, ForSamples = Math.Clamp(r.ForSamples, 1, 60), Enabled = r.Enabled,
        DisplayUnit = AlertRule.CleanUnit(r.DisplayUnit), OriginRuleId = r.OriginRuleId.Length <= 64 ? r.OriginRuleId : ""
    };
    // A Kozpont exejenek letoltese (100+ MB) - kulon kliens hosszabb idokorlattal.
    private readonly HttpClient _download = new() { Timeout = TimeSpan.FromMinutes(15) };
    // A Kozpont az elozo valaszban jelezte, hogy fogadja a metrikakat (regi Kozpontnak nem kuldjuk).
    private bool _hubAcceptsMetrics;
    private const int MetricBucketSec = 300, MetricMaxRows = 20000;
    // A Kozpont kerte beleptetok, amelyek a kovetkezo betarcsazassal mennek fel.
    private readonly List<HubTokenResult> _tokenResults = new();
    // Gepenkent a legutobb felkuldott programlista lenyomata: valtozatlan listat nem kuldunk ujra
    // (a betarcsazas legnagyobb resze). 6 oranként biztonsagbol ujra mindet.
    private Dictionary<string, int> _installedSent = new();
    private DateTime _installedSentAt = DateTime.MinValue;
    private Dictionary<string, int> _componentsSent = new();
    private Dictionary<string, int> _servicesSent = new();
    // A Központ az előző válaszban jelezte, hogy fogadja a naplókat (régi Központnak nem küldjük).
    private bool _hubAcceptsLogs;

    public DateTime? LastContactUtc { get; private set; }
    public string LastError { get; private set; } = "";
    public int NextSeconds { get; private set; } = 300;
    public int LastTaskCount { get; private set; }

    private static string PrivPath(string dir) => Path.Combine(dir, "hub-client-private.pem");
    private static string PubPath(string dir) => Path.Combine(dir, "hub-client-public.pem");

    // „Kapcsolódás most” (webfelület): a várakozást megszakítja, a betárcsázás azonnal indul.
    private CancellationTokenSource _wake = new();
    public int Cycles { get; private set; }

    public void Wake() { try { _wake.Cancel(); } catch { } }

    public async Task RunLoop(FleetStore store, ServerConfig config, string dir, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var wait = 30;
            if (config.HubUrl.Length > 0)
            {
                try { wait = await CycleAsync(store, config, dir, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { LastError = Describe(ex); wait = 60; }
                Cycles++;
            }

            if (_wake.IsCancellationRequested) _wake = new CancellationTokenSource();
            using var both = CancellationTokenSource.CreateLinkedTokenSource(token, _wake.Token);
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 30, 86400)), both.Token); }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) break; }
        }
    }

    /// <summary>Egy kor: szukseg eseten regisztracio, majd alairt jelentkezes. Visszaadja a kovetkezo varakozast.</summary>
    public async Task<int> CycleAsync(FleetStore store, ServerConfig config, string dir, CancellationToken token)
    {
        var keys = EnsureKeys(dir);
        var baseUrl = config.HubUrl.TrimEnd('/');

        // ---- 1. regisztracio (egyszer)
        if (config.HubSiteId.Length == 0 || config.HubPublicKeyPem.Length == 0)
        {
            if (config.HubToken.Length == 0)
            { LastError = "Nincs Központ-token – adj meg újat a beállításokban."; return 300; }
            if (config.HubFingerprint.Length == 0)
            { LastError = "Nincs Központ-ujjlenyomat – enélkül nem csatlakozunk (hamis Központ elleni védelem)."; return 300; }

            var resp = await _http.PostAsJsonAsync(baseUrl + "/hub/enroll", new HubEnrollRequest
            {
                Token = config.HubToken,
                PublicKeyPem = keys.PublicKeyPem,
                SiteName = SiteName(config),
                ServerVersion = ServerBuild.Version
            }, ProtocolJson.Options, token);

            var er = await ReadHub<HubEnrollResponse>(resp, "Regisztráció", token);
            if (er is null || !er.Success)
            { LastError = "Regisztráció elutasítva: " + (er?.Message ?? resp.StatusCode.ToString()); return 300; }

            if (!KeyStore.FingerprintReaches(config.HubFingerprint, er.KeyChain, er.HubPublicKeyPem))
            { LastError = "BIZTONSÁGI HIBA: a Központ kulcsa nem egyezik a megadott ujjlenyomattal."; return 300; }

            config.HubSiteId = er.SiteId;
            config.HubPublicKeyPem = er.HubPublicKeyPem;
            config.HubToken = "";   // a token tobbe nem kell
            config.Save(dir);
        }

        // ---- 1.5.9: WireGuard – a betárcsázás az alagúton megy, ha van; ha egyszer már ment rajta (vagy a Központ kötelezővé
        // tette), CSAK azon (szigorú mód, a felhasználó döntése). A HTTPS-cím csak a regisztrációé és a kulcsfrissítésé.
        var httpsUrl = baseUrl;
        var viaTunnel = false;
        if (config.WgOffer is { HubUrl.Length: > 0 } wo)
        {
            if (!SiteWg.Running) SiteWg.Apply(config, dir);
            if (config.WgActive || wo.Required || SiteWg.HandshakeRecent) { baseUrl = wo.HubUrl.TrimEnd('/'); viaTunnel = true; }
        }

        // ---- 2. alairt jelentkezes az osszesitovel
        var nonce = Crypto.NewNonce();
        var body = new HubCheckinBody
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            Nonce = nonce,
            Summary = BuildSummary(store, config)
        };

        // Metrika-atlagok az elozo sikeres felkuldes ota (eloszor: az utolso 24 ora).
        long nextMetricsFrom = 0;
        if (_hubAcceptsMetrics && Metrics is not null)
        {
            var from = config.HubMetricsFromTs > 0 ? config.HubMetricsFromTs : DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
            var (rows, next) = Metrics.Buckets(from, MetricBucketSec, MetricMaxRows);
            if (rows.Count > 0) body.Metrics = rows;
            nextMetricsFrom = next;
        }
        // Valtozatlan programlista helyett csak jelzes (az 1.3.0+ Kozpont megtartja az elozot - ugyanaz a
        // Kozpont, amelyik a metrikakat is fogadja; regebbinek mindig a teljes listat kuldjuk).
        if (DateTime.UtcNow - _installedSentAt > TimeSpan.FromHours(6)) { _installedSent = new(); _componentsSent = new(); _servicesSent = new(); _installedSentAt = DateTime.UtcNow; }
        var installedNow = new Dictionary<string, int>();
        var componentsNow = new Dictionary<string, int>();
        var servicesNow = new Dictionary<string, int>();
        foreach (var a in body.Summary.Agents)
        {
            var h = InstalledHash(a.Installed);
            installedNow[a.AgentId] = h;
            if (_hubAcceptsMetrics && _installedSent.TryGetValue(a.AgentId, out var prev) && prev == h)
            { a.Installed = new(); a.InstalledOmitted = true; }
            // Driverek / firmware: ugyanígy (null = nem változott; a régi Központ nem ismeri a mezőt, neki mindegy).
            var ch = a.Components is null ? 0 : ComponentsHash(a.Components);
            componentsNow[a.AgentId] = ch;
            if (_hubAcceptsMetrics && _componentsSent.TryGetValue(a.AgentId, out var pc) && pc == ch) a.Components = null;
            // 1.5.10: a szolgáltatás-lista is csak változáskor megy fel (null = a régi marad a Központnál).
            var sh = a.ServiceList is null ? 0 : ServicesHash(a.ServiceList);
            servicesNow[a.AgentId] = sh;
            if (_hubAcceptsMetrics && _servicesSent.TryGetValue(a.AgentId, out var ps) && ps == sh) a.ServiceList = null;
        }

        // 1.5.7: a naplók új sorai (az USB-napló nem) – csak ha a Központ jelezte, hogy fogadja; a mutató a válasz után lép.
        Action? logCommit = null;
        if (_hubAcceptsLogs)
        {
            var (chunks, commit) = LogShip.Take(dir);
            if (chunks.Count > 0) body.Logs = chunks;
            logCommit = commit;
        }

        List<HubTokenResult> sentTokens;
        lock (_tokenResults) sentTokens = _tokenResults.ToList();
        if (sentTokens.Count > 0) body.TokenResults = sentTokens;
        var env = SignedEnvelope.Create(config.HubSiteId, body, keys.PrivateKeyPem);

        HttpResponseMessage cr;
        try { cr = await _http.PostAsJsonAsync(baseUrl + "/hub/checkin", env, ProtocolJson.Options, token); }
        catch (Exception ex) when (viaTunnel && ex is not OperationCanceledException { CancellationToken.IsCancellationRequested: true })
        {
            // Az alagút nem működik: a kulcsot / ajánlatot HTTPS-en (aláírva) frissítjük – a Központ kulcsa, címe, portja
            // változhatott –, és a következő körben újra az alagúton próbáljuk. HTTPS-en betárcsázni nem lehet.
            SiteWg.LastError = "A Központ az alagúton nem érhető el" + (SiteWg.HandshakeRecent ? "" : " (nincs WireGuard-kézfogás – port / végpont?)") + ": " + ex.Message;
            LastError = SiteWg.LastError;
            if (DateTime.UtcNow - _lastWgRefresh > TimeSpan.FromMinutes(10))
            {
                _lastWgRefresh = DateTime.UtcNow;
                try { await WgKeyRequestAsync(httpsUrl, config, keys, dir, token); } catch (Exception e2) when (e2 is not OperationCanceledException) { }
            }
            return 60;
        }
        var replyEnv = await ReadHub<SignedEnvelope>(cr, "Jelentkezés", token);

        // ---- 3. a valasz ellenorzese: Kozpont-alairas + a mi nonce-unk
        // Kozpont-kulcscsere: az uj kulcsot csak ellenorizheto lancon at fogadjuk el.
        var hubKey = config.HubPublicKeyPem;
        if (replyEnv?.SignerPublicKeyPem is { Length: > 0 } signer && signer != hubKey)
        {
            if (!KeyStore.ChainReaches(hubKey, replyEnv.KeyChain, signer))
            { LastError = "A Központ ismeretlen kulccsal válaszolt, érvényes átadás nélkül – elutasítva."; return 300; }
            hubKey = signer;
        }
        if (replyEnv is null || !replyEnv.Verify(hubKey))
        { LastError = "A Központ válaszának aláírása érvénytelen – elutasítva."; return 300; }

        var reply = replyEnv.Open<HubCheckinReply>();
        if (reply is null || reply.RequestNonce != nonce)
        { LastError = "A Központ válasza nem ehhez a kéréshez tartozik – elutasítva."; return 300; }

        if (hubKey != config.HubPublicKeyPem)
        {
            config.HubPublicKeyPem = hubKey;
            config.HubFingerprint = Crypto.PublicKeyFingerprint(hubKey);
            config.Save(dir);
        }

        // 1.5.9: az alagút ajánlata (aláírt válaszból) – a szigorú elutasításban is jön, hogy fel tudjuk építeni.
        if (reply.Wg is { } offer) ApplyWgOffer(config, dir, offer);
        else if (reply.Ok && !viaTunnel && config.WgOffer is not null)
        { config.WgOffer = null; config.WgActive = false; config.Save(dir); SiteWg.Stop(); SrvLog.Info("WireGuard: a Központban kikapcsolták – az alagút leállt."); }

        if (!reply.Ok)
        {
            LastError = reply.Message;
            if (reply.Message.Contains("regisztráció", StringComparison.OrdinalIgnoreCase))
            { config.HubSiteId = ""; config.Save(dir); }
            return reply.Wg is not null ? 30 : 300;   // az alagút felépülése után hamar újra
        }
        if (viaTunnel && !config.WgActive)
        {
            config.WgActive = true; config.Save(dir);
            SrvLog.Info("WireGuard: a betárcsázás az alagúton ment – innentől csak azon (szigorú mód).");
        }
        SiteWg.LastError = viaTunnel ? "" : SiteWg.LastError;

        // A felkuldott savok a Kozpontnal vannak - innen folytatjuk (csak sikeres valasz utan).
        _hubAcceptsMetrics = reply.AcceptsMetrics;
        foreach (var kv in installedNow) _installedSent[kv.Key] = kv.Value;   // ezek a listak mar a Kozpontnal vannak
        foreach (var kv in componentsNow) _componentsSent[kv.Key] = kv.Value;
        foreach (var kv in servicesNow) _servicesSent[kv.Key] = kv.Value;
        var changed = false;
        if (nextMetricsFrom > config.HubMetricsFromTs) { config.HubMetricsFromTs = nextMetricsFrom; changed = true; }
        // A Kozpont szabalyai: csak valtozaskor irunk (a betarcsazas 5 percenkent jon).
        if (reply.Policies is not null && ProtocolJson.Serialize(reply.Policies) != ProtocolJson.Serialize(config.HubPolicies))
        { config.HubPolicies = reply.Policies; changed = true; }
        // 1.5.0: a Központ programtára (a Központ a mester), küszöb-szabályai és mérés-kérése - itt csak olvashatók.
        if (reply.Catalog is { } cat && config.ApplyHubCatalog(cat))
        { changed = true; AuditLog.Write(dir, "Központ", "programtár a Központtól", $"{cat.Items.Count} program, {cat.Bundles.Count} csomag, {cat.Rejected.Count} elutasított"); }
        if (reply.AlertRules is { } ar)
        {
            var valid = ar.Where(r => r.Id.Length is > 0 and <= 64 && AlertRule.ValidKey(r.MetricKey)).Take(500).ToList();
            if (ProtocolJson.Serialize(valid) != ProtocolJson.Serialize(config.HubAlertRules))
            {
                config.HubAlertRules = valid; changed = true;
                Alerts?.SetHubRules(valid.Select(ToRule));
                AuditLog.Write(dir, "Központ", "küszöb-szabályok a Központtól", $"{valid.Count} szabály");
            }
        }
        if (reply.Monitor is { } mon && ProtocolJson.Serialize(mon) != ProtocolJson.Serialize(config.HubMonitor))
        { config.HubMonitor = mon; changed = true; }
        // 1.5.6: a Központ CVE-találatai a gépekre (csak az ismert gépekre, gépenként legfeljebb 300 tétel).
        if (reply.Cve is { } cve && cve.Hash != config.HubCveHash)
        {
            var known = store.Agents.Select(a => a.AgentId).ToHashSet();
            cve.Agents = cve.Agents.Where(a => known.Contains(a.AgentId)).ToList();
            foreach (var a in cve.Agents) if (a.Findings.Count > 300) a.Findings = a.Findings.Take(300).ToList();
            CveCounts.Apply(cve);
            config.HubCveHash = cve.Hash; changed = true;
        }
        // 1.5.7: a Központ műveletei (helyi szabály mentése / törlése, programkérés döntése) – itt fut le, itt íródik.
        if (reply.SiteOps is { Count: > 0 } sops)
            foreach (var op in sops.Take(200))
            {
                var err = SiteOps.Apply(store, Alerts, op, dir);
                if (err is not null) SrvLog.Warn($"Központ-művelet ({op.Kind}): {err}");
            }
        _hubAcceptsLogs = reply.AcceptsLogs;
        if (reply.AcceptsLogs && logCommit is not null) logCommit();
        // 1.5.8: a Központ megőrzés-beállítása (a szerveren) és hogy fogadja-e a lezárt napi mérési fájlokat
        var hubKeep = reply.MetricKeepDays is int mk && mk > 0 ? Math.Clamp(mk, 2, 3650) : 0;
        if (hubKeep != config.HubMetricKeepDays || reply.AcceptsMetricFiles != config.HubAcceptsMetricFiles)
        { config.HubMetricKeepDays = hubKeep; config.HubAcceptsMetricFiles = reply.AcceptsMetricFiles; changed = true; }
        if (reply.GroupOps is { Count: > 0 } ops)
            foreach (var op in ops.Take(200))
            {
                var err = GroupOps.Apply(store, config, Alerts, op.Op, op.Group, op.NewName, op.AgentIds, out var gc);
                changed |= gc;
                AuditLog.Write(dir, "Központ", "csoport: " + op.Op, op.Group + (op.NewName.Length > 0 ? " -> " + op.NewName : "") + (err is null ? "" : " – HIBA: " + err));
            }
        if (changed) config.Save(dir);

        // 1.5.8: a megőrzésnél régebbi, LEZÁRT napi mérési fájlok a Központba (a nyugta után a szerverről törlődnek).
        if (config.HubAcceptsMetricFiles && Metrics is not null)
            try { await UploadMetricFilesAsync(baseUrl, config, keys, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { LastError = "Mérési fájl feltöltése: " + ex.Message; }

        // Uj agent a Kozpontbol: a felkuldott beleptetok kivetele, az ujonnan kertek elkeszitese.
        lock (_tokenResults) _tokenResults.RemoveAll(sentTokens.Contains);
        var wait = reply.NextCheckinSeconds;
        foreach (var tr in reply.TokenRequests ?? new())
        {
            var res = new HubTokenResult { RequestId = tr.RequestId, AgentUrl = ServerPaths.LanAgentUrl(config.AgentPort), Fingerprint = store.ServerKeys.Fingerprint };
            try
            {
                res.Token = store.CreateEnrollmentToken("Központ: " + tr.Label, Math.Clamp(tr.LifetimeMinutes, 0, 7 * 24 * 60), Math.Clamp(tr.MaxUses, 0, 1000));
                AuditLog.Write(dir, "Központ", "agent-beléptető a Központ kérésére", tr.Label);
            }
            catch (Exception ex) { res.Error = ex.Message; }
            lock (_tokenResults) _tokenResults.Add(res);
            wait = Math.Min(wait, 30);   // a Kozpontban varnak ra - hamar visszaterunk vele
        }

        // ---- 4. a Kozpont feladatai a helyi agentekhez
        foreach (var t in reply.Tasks)
        {
            // Az agent-frissites keres a telephelynek szol: a kovetkezo jelentkezeskor ajanljuk fel.
            if (t.Kind == AgentTaskKind.UpdateAgent) store.RequestAgentUpdate(t.AgentId);
            else store.QueueTask(t.AgentId, new AgentTask { TaskId = t.TaskId, Kind = t.Kind, Target = t.Target });
        }

        // A Kozpontbol allitott automatikus agent-frissites.
        if (reply.AgentAutoUpdate is bool au && au != config.AgentAutoUpdate)
        { config.AgentAutoUpdate = au; config.Save(dir); AuditLog.Write(dir, "Központ", "agentek automatikus frissítése: " + (au ? "be" : "ki")); }

        // Szerverfrissites a Kozpont verziojara - ha kertek, es tenyleg ujabb.
        if (reply.ServerUpdate is { } su) await TryServerUpdateAsync(baseUrl, su, dir, token);

        LastContactUtc = DateTime.UtcNow;
        LastError = "";
        LastTaskCount = reply.Tasks.Count;
        NextSeconds = wait;
        return wait;
    }

    public sealed class HubKeyInfo { public string PublicKeyPem { get; set; } = ""; public string Fingerprint { get; set; } = ""; public List<KeyLink>? KeyChain { get; set; } }

    /// <summary>
    /// Kapcsolat-teszt mentés és regisztráció nélkül: elérhető-e a Központ (a proxyn át is), és a megadott ujjlenyomat
    /// egyezik-e a kulcsával (láncon át is). Visszaadja a lépések eredményét; semmit nem ír.
    /// </summary>
    public async Task<(bool Ok, List<string> Steps)> TestAsync(string url, string fingerprint, CancellationToken token)
    {
        var steps = new List<string>();
        url = (url ?? "").Trim().TrimEnd('/');
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { steps.Add("✗ A cím https:// kezdetű legyen."); return (false, steps); }
        try
        {
            var ping = await ReadHub<System.Text.Json.JsonElement>(await _http.PostAsJsonAsync(url + "/hub/ping", new { }, ProtocolJson.Options, token), "Elérés", token);
            if (!ping.TryGetProperty("service", out var svc) || svc.GetString() != "devtoolbox-hub")
            { steps.Add("✗ Válaszolt, de nem a Fleet HUB – rossz cím?"); return (false, steps); }
            steps.Add("✓ A Központ elérhető (TLS rendben).");
        }
        catch (Exception ex) { steps.Add("✗ " + Describe(ex)); return (false, steps); }

        if ((fingerprint ?? "").Trim().Length == 0) { steps.Add("✗ Nincs megadva a Központ ujjlenyomata – enélkül nem kapcsolódunk."); return (false, steps); }
        try
        {
            var resp = await _http.PostAsJsonAsync(url + "/hub/key", new { }, ProtocolJson.Options, token);
            if ((int)resp.StatusCode == 404) { steps.Add("• A Központ 1.5.3-nál régebbi: az ujjlenyomat a regisztrációkor ellenőrződik."); return (true, steps); }
            var key = await ReadHub<HubKeyInfo>(resp, "Kulcs", token);
            if (key is null || !KeyStore.FingerprintReaches(fingerprint!.Trim(), key.KeyChain, key.PublicKeyPem))
            { steps.Add("✗ Az ujjlenyomat NEM egyezik a Központ kulcsával – ellenőrizd (a Központ Beállítások / Biztonság oldalán látszik)."); return (false, steps); }
            steps.Add("✓ Az ujjlenyomat egyezik a Központ kulcsával.");
        }
        catch (Exception ex) { steps.Add("✗ " + Describe(ex)); return (false, steps); }
        return (true, steps);
    }

    /// <summary>
    /// A valasz ellenorzese. Ha a proxy (NPM/Cloudflare) HTML hibaoldalt ad vissza, a nyers
    /// JSON-hiba helyett megmondjuk, melyik reteg a hibas.
    /// </summary>
    private static async Task<T?> ReadHub<T>(HttpResponseMessage resp, string what, CancellationToken token)
    {
        var body = await resp.Content.ReadAsStringAsync(token);
        if (resp.IsSuccessStatusCode && body.TrimStart().StartsWith("{"))
            return ProtocolJson.Deserialize<T>(body);

        var code = (int)resp.StatusCode;
        var why = code switch
        {
            403 => "a proxy (NPM) elutasította – ha az Advanced fülön van korlátozás, töröld; a Központ maga védi az admint",
            404 => "a proxy nem a Központra irányít, vagy rossz a Központ címe",
            502 or 503 or 504 => "az NPM nem éri el a Központot – a Proxy Host IP/port rossz, vagy a Központ tűzfala zár (FleetHub.exe diag)",
            >= 520 and <= 530 => "Cloudflare-hiba – a DNS rekord legyen 'DNS only' (szürke felhő)",
            _ when body.TrimStart().StartsWith("<") => "nem a Központ válaszolt (HTML oldal jött vissza) – a proxy rossz helyre irányít",
            _ => "váratlan válasz"
        };
        throw new InvalidOperationException($"{what}: HTTP {code} – {why}.");
    }

    private static string Describe(Exception ex)
    {
        if (ex is InvalidOperationException) return ex.Message;
        if (ex is TaskCanceledException) return "Időtúllépés – a Központ nem válaszolt 60 mp-en belül.";
        if (ex is HttpRequestException h)
        {
            var inner = h.InnerException?.Message ?? h.Message;
            if (inner.Contains("SSL", StringComparison.OrdinalIgnoreCase) || h.InnerException is System.Security.Authentication.AuthenticationException)
                return "TLS-hiba: a Központ tanúsítványa nem érvényes (kiállította az NPM a Let's Encrypt tanúsítványt?) – " + inner;
            if (inner.Contains("No such host", StringComparison.OrdinalIgnoreCase) || inner.Contains("nem ismert", StringComparison.OrdinalIgnoreCase))
                return "A Központ domainje nem oldható fel (DNS) – " + inner;
            return "A Központ nem érhető el: " + inner;
        }
        return ex.Message;
    }

    private static string SiteName(ServerConfig c) =>
        c.HubSiteName.Length > 0 ? c.HubSiteName : Environment.MachineName;

    /// <summary>
    /// A Kozpont felajanlotta a sajat exejet. Csak szolgaltataskent, a telepitett exebol futva, es csak ujabb
    /// verziora: letoltes FleetServer.new.exe-kent, meret es (alairt) SHA-256 ellenorzes, majd a szokasos csere
    /// (ServiceSelfUpdate: leallitas, csere, inditas, hiba eseten visszaallitas).
    /// </summary>
    private async Task TryServerUpdateAsync(string baseUrl, HubServerUpdate su, string dir, CancellationToken token)
    {
        if (!Version.TryParse(su.Version, out var nv) || !Version.TryParse(ServerBuild.Version, out var cv) || nv <= cv) return;
        if (Environment.UserInteractive || !OperatingSystem.IsWindows()) return;
        var installed = Path.Combine(ServerPaths.InstallDir, "FleetServer.exe");
        if (!string.Equals(Environment.ProcessPath ?? "", installed, StringComparison.OrdinalIgnoreCase)) return;
        if (su.Sha256.Length != 64 || su.Size is <= 1_000_000 or > 400_000_000 || su.DownloadToken.Length == 0) return;

        var incoming = ServiceSelfUpdate.IncomingPath(installed);
        var tmp = incoming + ".tmp";
        try
        {
            using (var resp = await _download.GetAsync($"{baseUrl}/hub/fleet-exe?t={Uri.EscapeDataString(su.DownloadToken)}", HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (!resp.IsSuccessStatusCode) { LastError = $"Szerverfrissítés letöltése: HTTP {(int)resp.StatusCode}"; return; }
                await using var src = await resp.Content.ReadAsStreamAsync(token);
                await using var dst = File.Create(tmp);
                var buf = new byte[81920]; long total = 0; int n;
                while ((n = await src.ReadAsync(buf, token)) > 0)
                {
                    if ((total += n) > su.Size) { LastError = "Szerverfrissítés: a letöltés nagyobb az ajánlottnál – elvetve."; return; }
                    await dst.WriteAsync(buf.AsMemory(0, n), token);
                }
            }
            string sha; await using (var fs = File.OpenRead(tmp)) sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, token));
            if (!sha.Equals(su.Sha256, StringComparison.OrdinalIgnoreCase)) { LastError = "Szerverfrissítés: a lenyomat NEM egyezik az aláírttal – elvetve."; return; }
            File.Move(tmp, incoming, overwrite: true);
            AuditLog.Write(dir, "Központ", "szerverfrissítés a Központból", $"{ServerBuild.Version} -> {su.Version}");
            SrvLog.Info($"Szerverfrissítés a Központból: {ServerBuild.Version} -> {su.Version}");
            ServerPaths.ApplyPendingUpdate(ServerPaths.ServiceName);   // az uj exe 2 mp mulva leallitja es csereli a szolgaltatast
        }
        catch (Exception ex) { LastError = "Szerverfrissítés: " + ex.Message; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private static SiteSummary BuildSummary(FleetStore store, ServerConfig config) => new()
    {
        SiteName = SiteName(config),
        ServerVersion = ServerBuild.Version,
        AgentAutoUpdate = config.AgentAutoUpdate,
        AgentPackageVersion = AgentPackageVersion?.Invoke() ?? "",
        SupportsHubUpdates = true,
        // Csak a kézi csoportok: az automatikusakat a Központ maga számolja a gépek adataiból (OS, IsVirtual).
        GroupNames = GroupOps.Manual(store, config),
        SupportsGroupOps = true,
        HubCatalogHash = config.HubCatalogHash,
        HubCveHash = config.HubCveHash,
        AgentPort = config.AgentPort,
        OldAgentPort = config.OldAgentPort,
        Services = ServiceCatalogOf(store),
        // 1.5.5: internetkapcsolat és a riasztást adó SNMP-trapek (a Központ e-mailben / a Janusba jelzi)
        Internet = NetWatch.Summary(config),
        SnmpEnabled = config.SnmpEnabled,
        SnmpEvents = config.SnmpEnabled ? SnmpReceiver.AlertEvents(30) : null,
        ServiceEvents = ServiceWatch.Events(30),
        // 1.5.7: a helyi szabályok (a Központ szerkesztheti) és a tálca programkérései
        // 1.5.9: a WireGuard nyilvános kulcs (a Központ ehhez rendel alagút-címet) és az alagút állapota
        WgPublicKey = config.HubUrl.Length > 0 && config.WgEnabled ? SiteWg.Keys(config, ServerPaths.DataDir).Public : "",
        WgHandshakeUtc = SiteWg.HubStat()?.LastHandshakeUtc,
        WgError = SiteWg.LastError,
        LocalRules = Alerts?.Rules.Select(SiteOps.ToSite).ToList(),
        SupportsSiteOps = Alerts is not null,
        ProgramRequests = ProgramRequests.Summary(),
        Agents = store.Agents.Select(a => new SiteAgent
        {
            AgentId = a.AgentId,
            MachineName = a.MachineName,
            OperatingSystem = a.LastStatus?.OperatingSystem ?? a.OperatingSystem,
            AgentVersion = a.AgentVersion,
            Online = a.IsOnline,
            Revoked = a.Revoked,
            LastSeenUtc = a.LastSeenUtc,
            UpdatesAvailable = a.LastStatus?.UpdatesAvailable ?? 0,
            Updates = a.LastStatus?.Updates ?? new(),
            // A Központ ugyanazt a gépkezelést kínálja, mint a telephely - ehhez kellenek:
            Groups = a.Groups.ToList(),
            PendingReboot = a.LastStatus?.PendingReboot ?? false,
            WindowsUpdates = a.LastStatus?.WindowsUpdates ?? new(),
            Installed = a.LastStatus?.Installed ?? new(),
            IsVirtual = a.LastStatus?.IsVirtual,
            Score = a.Revoked ? null : TryScore(a),
            Components = a.LastStatus?.Components,
            ServiceList = a.LastStatus?.ServiceList
        }).ToList()
    };

    private DateTime _lastWgRefresh = DateTime.MinValue;

    /// <summary>Az ajánlat átvétele (csak változáskor ír és konfigurál).</summary>
    private static void ApplyWgOffer(ServerConfig config, string dir, HubWgOffer offer)
    {
        if (!WgKeys.IsKey(offer.HubPublicKey)) return;
        if (ProtocolJson.Serialize(offer) == ProtocolJson.Serialize(config.WgOffer)) { if (!SiteWg.Running) SiteWg.Apply(config, dir); return; }
        config.WgOffer = offer; config.Save(dir);
        if (SiteWg.Apply(config, dir) is { } err) SrvLog.Warn("WireGuard: " + err);
    }

    /// <summary>
    /// A saját WireGuard-kulcs közlése / az ajánlat frissítése HTTPS-en (POST /hub/wg-key, a telephely kulcsával aláírva; a
    /// válasz a Központ kulcsával aláírt és ehhez a kéréshez kötött). Ez a helyi javítás útja: kulcscsere, Központ-kulcs- vagy
    /// portváltás után így épül fel újra az alagút. Visszaadja a hibát, vagy null-t.
    /// </summary>
    public async Task<string?> WgKeyRequestAsync(string httpsUrl, ServerConfig config, Crypto.KeyPair keys, string dir, CancellationToken token)
    {
        var (_, pub) = SiteWg.Keys(config, dir);
        var req = new HubWgKeyRequest { WgPublicKey = pub, Timestamp = DateTime.UtcNow.ToString("o"), Nonce = Crypto.NewNonce() };
        var resp = await _http.PostAsJsonAsync(httpsUrl.TrimEnd('/') + "/hub/wg-key", SignedEnvelope.Create(config.HubSiteId, req, keys.PrivateKeyPem), ProtocolJson.Options, token);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return "A Központ még nem ismeri a WireGuardot (1.5.9-től).";
        var renv = await resp.Content.ReadFromJsonAsync<SignedEnvelope>(ProtocolJson.Options, token);
        if (renv is null || !renv.Verify(config.HubPublicKeyPem)) return "A Központ válasza nem ellenőrizhető.";
        var r = renv.Open<HubWgKeyReply>();
        if (r is null || r.RequestNonce != req.Nonce) return "A Központ válasza nem ehhez a kéréshez tartozik.";
        if (!r.Ok || r.Offer is null)
        {
            // A Központban kikapcsolták a WireGuardot: vissza a HTTPS-re (különben a szigorú mód örökre elzárna).
            if (r.Message.Contains("nincs bekapcsolva")) { config.WgOffer = null; config.WgActive = false; config.Save(dir); SiteWg.Stop(); }
            return SiteWg.LastError = r.Message.Length > 0 ? r.Message : "A Központ elutasította.";
        }
        ApplyWgOffer(config, dir, r.Offer);
        SiteWg.Apply(config, dir, forceResolve: true);
        return null;
    }

    /// <summary>A webfelület „Kulcscsere” / „Újrakapcsolás” gombja: új saját kulcs (ha kérik), közlés a Központtal, újrakonfigurálás.</summary>
    public async Task<string?> WgRepairAsync(ServerConfig config, string dir, bool newKey, CancellationToken token)
    {
        if (config.HubUrl.Length == 0 || config.HubSiteId.Length == 0) return "A szerver nincs a Központhoz kapcsolva.";
        if (newKey) SiteWg.NewKey(config, dir);
        var err = await WgKeyRequestAsync(config.HubUrl, config, EnsureKeys(dir), dir, token);
        Wake();
        return err;
    }

    /// <summary>
    /// 1.5.8: lezárt napi mérési fájlok feltöltése (körönként legfeljebb 3): gzip, a telephely kulcsával aláírt fejléc
    /// (nap, SHA-256, méret, nonce), és csak a Központ ALÁÍRT, ehhez a kéréshez és lenyomathoz kötött nyugtája után törlünk.
    /// </summary>
    private readonly Dictionary<string, DateTime> _fileRetryAt = new();

    private async Task UploadMetricFilesAsync(string baseUrl, ServerConfig config, Crypto.KeyPair keys, CancellationToken token)
    {
        foreach (var f in Metrics!.DueFiles(config.EffectiveKeepDays()).Where(x => !(_fileRetryAt.TryGetValue(x.Day, out var at) && at > DateTime.UtcNow)).Take(3))
        {
            // 1.5.13: sikertelen feltöltés után ezt a napot 1 óráig nem küldjük újra – egy tartós elutasítás (pl. a Központ
            // nem fogadja) eddig minden betárcsázáskor újra felküldte a teljes, akár több száz MB-os fájlt.
            _fileRetryAt[f.Day] = DateTime.UtcNow.AddHours(1);
            // Csak ha az 5 perces átlagai már fent vannak (a mutató túl van a napon): különben a Központ a fájl után még
            // megkapná ugyanannak a napnak az átlagait is – két példány lenne.
            if (f.Start + 86400 > config.HubMetricsFromTs) break;
            var gz = Path.Combine(Path.GetTempPath(), $"fleet-meres-{f.Day}-{Guid.NewGuid():N}.gz");
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await using (var src = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var dst = File.Create(gz))
                await using (var z = new System.IO.Compression.GZipStream(dst, System.IO.Compression.CompressionLevel.Optimal))
                    await src.CopyToAsync(z, token);
                string sha;
                await using (var fs = File.OpenRead(gz)) sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, token));
                var hdr = new MetricFileHeader { Day = f.Day, Sha256 = sha, Size = new FileInfo(gz).Length, Timestamp = DateTime.UtcNow.ToString("o"), Nonce = Crypto.NewNonce() };
                var env = SignedEnvelope.Create(config.HubSiteId, hdr, keys.PrivateKeyPem);
                await using var body = File.OpenRead(gz);
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/hub/metric-file") { Content = new StreamContent(body) };
                req.Headers.TryAddWithoutValidation("X-Fleet-Envelope", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ProtocolJson.Serialize(env))));
                using var resp = await _download.SendAsync(req, token);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { config.HubAcceptsMetricFiles = false; return; }
                var renv = await resp.Content.ReadFromJsonAsync<SignedEnvelope>(ProtocolJson.Options, token);
                if (renv is null || !renv.Verify(config.HubPublicKeyPem)) { LastError = $"Mérési fájl ({f.Day}): a Központ nyugtája nem ellenőrizhető – a fájl marad."; return; }
                var rc = renv.Open<MetricFileReceipt>();
                if (rc is null || rc.RequestNonce != hdr.Nonce || rc.Day != f.Day || rc.Sha256 != sha) { LastError = $"Mérési fájl ({f.Day}): a nyugta nem ehhez a feltöltéshez tartozik – a fájl marad."; return; }
                if (!rc.Ok) { LastError = $"Mérési fájl ({f.Day}): {rc.Message}"; return; }
                Metrics.DeleteDayFile(f.Day);
                _fileRetryAt.Remove(f.Day);
                SrvLog.Info($"Mérési adatok ({f.Day}) a Központban – a helyi napi fájl törölve ({hdr.Size / 1024} KB tömörítve).");
            }
            finally { try { File.Delete(gz); } catch { } }
        }
    }

    // 1.5.11: a változás-detektálás hash-e allokáció nélkül (eddig gépenként egy nagy ideiglenes string épült
    // csak a GetHashCode miatt). A HashCode folyamatonként véletlen magú – a lenyomatokat csak ugyanabban a
    // futásban hasonlítjuk össze (újraindításkor és 6 óránként úgyis teljes küldés megy), így ez elég.
    private static int InstalledHash(List<InstalledApp> l)
    {
        var hc = new HashCode();
        hc.Add(l.Count);
        foreach (var i in l) { hc.Add(i.Id); hc.Add(i.Version); }
        return hc.ToHashCode();
    }

    private static int ComponentsHash(List<ComponentInfo> l)
    {
        var hc = new HashCode();
        hc.Add(l.Count);
        foreach (var c in l) { hc.Add(c.Kind); hc.Add(c.Name); hc.Add(c.Version); }
        return hc.ToHashCode();
    }

    private static int ServicesHash(List<ServiceEntry> l)
    {
        var hc = new HashCode();
        hc.Add(l.Count);
        foreach (var s in l) { hc.Add(s.Name); hc.Add(s.Running); hc.Add(s.Auto); }
        return hc.ToHashCode();
    }

    /// <summary>Az összegzés hibája ne akassza meg a betárcsázást: akkor egyszerűen nem megy fel.</summary>
    private static SiteScore? TryScore(EnrolledAgent a)
    {
        try { return ScoreOf?.Invoke(a); } catch { return null; }
    }

    /// <summary>A telephely gépeinek szolgáltatásai összesítve (név, megjelenített név, hány gépen van / fut).</summary>
    private static List<SiteService> ServiceCatalogOf(FleetStore store)
    {
        var d = new Dictionary<string, SiteService>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in store.Agents.Where(a => !a.Revoked))
            foreach (var s in a.LastStatus?.ServiceList ?? new())
            {
                if (!d.TryGetValue(s.Name, out var x)) d[s.Name] = x = new SiteService { Name = s.Name, DisplayName = s.DisplayName };
                x.Machines++;
                if (s.Running) x.Running++;
            }
        return d.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Crypto.KeyPair EnsureKeys(string dir)
    {
        if (File.Exists(PrivPath(dir)) && File.Exists(PubPath(dir)))
        {
            var pub = File.ReadAllText(PubPath(dir));
            return new Crypto.KeyPair(pub, File.ReadAllText(PrivPath(dir)), Crypto.PublicKeyFingerprint(pub));
        }
        var k = Crypto.NewKeyPair();
        File.WriteAllText(PubPath(dir), k.PublicKeyPem);
        File.WriteAllText(PrivPath(dir), k.PrivateKeyPem);
        return k;
    }
}
