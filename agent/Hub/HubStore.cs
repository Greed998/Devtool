using System.Collections.Concurrent;
using System.Text.Json;
using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

public sealed class HubSite
{
    public string SiteId { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public DateTime EnrolledUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
    public bool Revoked { get; set; }

    /// <summary>Betarcsazasi ido masodpercben - a Kozpontban allithato, a telephely koveti.</summary>
    public int IntervalSeconds { get; set; } = 300;

    public SiteSummary? LastSummary { get; set; }
    /// <summary>
    /// Az összesítő a telephely órája szerint (a kérés időbélyege). A gépek LastSeenUtc-je is a telephely
    /// órája szerinti, így a kiesés ebből az eltolással számolható - a két óra eltérése nem okoz téves riasztást.
    /// </summary>
    public DateTime? SummarySiteUtc { get; set; }

    /// <summary>Egy gép utolsó jelentkezése a Központ órája szerint (a telephely órájának eltérésével korrigálva).</summary>
    public DateTime? AgentLastSeenHubUtc(SiteAgent a) =>
        a.LastSeenUtc is { } t && SummarySiteUtc is { } st && LastSeenUtc is { } hs ? t + (hs - st) : a.LastSeenUtc;
    /// <summary>A Központból kérték: a telephely frissítse a FleetServert a Központ verziójára.</summary>
    public bool UpdateRequested { get; set; }
    /// <summary>A Központból állított automatikus agent-frissítés (null: a telephely saját beállítása marad).</summary>
    public bool? AgentAutoUpdateWanted { get; set; }
    /// <summary>A Központból kért, még le nem küldött csoport-műveletek (a következő betárcsázáskor mennek le).</summary>
    public List<HubGroupOp> PendingGroupOps { get; set; } = new();

    // ---- 1.5.3: gyors betárcsázás és a frissülések nyilvántartása
    /// <summary>Eddig 30 mp-enként tárcsázzon be (a Központban kért művelet után, hogy hamar átmenjen).</summary>
    public DateTime? FastUntilUtc { get; set; }
    /// <summary>A telephely szerverének legutóbbi frissülése (a Központ látta: verzióváltás betárcsázáskor).</summary>
    public HubUpdateEvent? LastServerUpdate { get; set; }
    /// <summary>A Központból kért szerverfrissítés ideje (a „vár” állapothoz).</summary>
    public DateTime? UpdateRequestedUtc { get; set; }
    /// <summary>Az agentek frissülései (legutóbbi 200), és a Központból kért, még nem teljesült agent-frissítések.</summary>
    public List<HubUpdateEvent> AgentUpdates { get; set; } = new();
    public Dictionary<string, DateTime> AgentUpdateRequests { get; set; } = new();
    /// <summary>A Janusban ehhez a telephelyhez (ügyfélhez) kapott kulcsazonosító és titok (DPAPI-val titkosítva).</summary>
    public string JanusKeyId { get; set; } = "";
    public string JanusSecretProtected { get; set; } = "";

    // ---- 1.5.5: a telephely eseményei, amelyeket a Központ már jelzett (e-mail / Janus) - ugyanazt ne jelezze kétszer
    /// <summary>A legutóbb jelzett (lezárult) internet-kiesés kezdete.</summary>
    public DateTime? LastOutageStartUtc { get; set; }
    /// <summary>A legutóbb átvett SNMP-riasztás azonosítója.</summary>
    public long LastSnmpEventId { get; set; }
    /// <summary>A legutóbb átvett szolgáltatás-esemény (leállás / újraindulás) azonosítója (1.5.6).</summary>
    public long LastServiceEventId { get; set; }
    /// <summary>1.5.7: a Központból kért, még le nem küldött műveletek (helyi szabály, programkérés döntése).</summary>
    public List<HubSiteOp> PendingSiteOps { get; set; } = new();

    // ---- 1.5.9: WireGuard-alagút
    /// <summary>A telephely WireGuard nyilvános kulcsa és az alagútban kiosztott címe.</summary>
    public string WgPublicKey { get; set; } = "";
    public string WgAddress { get; set; } = "";
    /// <summary>Már tárcsázott be az alagúton: innentől CSAK azon fogadjuk (szigorú mód, a felhasználó döntése).</summary>
    public bool WgActive { get; set; }
    public DateTime? WgActiveSinceUtc { get; set; }

    /// <summary>Online, ha a beallitott idokoz haromszorosan belul jelentkezett.</summary>
    public bool IsOnline => LastSeenUtc is { } t &&
        DateTime.UtcNow - t < TimeSpan.FromSeconds(Math.Max(IntervalSeconds, 60) * 3);
}

/// <summary>Egy frissülés: melyik gép (szervernél üres), honnan, hová, mikor.</summary>
public sealed class HubUpdateEvent
{
    public string AgentId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

public sealed record HubToken(string Fingerprint, DateTime CreatedUtc, string Label, bool Enabled)
{
    public DateTime? ExpiresUtc { get; init; }
    public int MaxUses { get; init; }
    public int Uses { get; init; }
    public bool IsUsable => Enabled &&
        (ExpiresUtc is null || DateTime.UtcNow <= ExpiresUtc) &&
        (MaxUses <= 0 || Uses < MaxUses);
}

/// <summary>
/// A Kozpont allapota. Egyetlen iro: a Hub szolgaltatas. Szalbiztos.
/// </summary>
public sealed class HubStore
{
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, HubSite> _sites = new();
    private readonly ConcurrentDictionary<string, HubToken> _tokens = new();
    private readonly ConcurrentDictionary<string, List<HubTask>> _pending = new();
    private readonly ConcurrentDictionary<string, DateTime> _nonces = new();

    public Crypto.KeyPair Keys { get; private set; }
    private KeyStore _ks = null!;

    /// <summary>
    /// A telephelyrol erkezett metrika-atlagok feldolgozoja (tarolas, kuszobok). Ha be van allitva,
    /// a valaszban jelezzuk a telephelynek, hogy kuldheti oket. Hiba itt nem rontja el a jelentkezest.
    /// </summary>
    public Action<HubSite, List<SiteMetricRow>>? OnMetrics { get; set; }

    /// <summary>A telephelyre vonatkozo Kozpont-szabalyok (minden valaszban lemennek; null = nem kuldunk).</summary>
    public Func<HubSite, List<AutoPolicy>>? PoliciesFor { get; set; }

    /// <summary>A Kozpont sajat exejenek ajanlata (ha a telephelynek kertek frissitest); a Kozpont-szerep allitja be.</summary>
    public Func<HubSite, HubServerUpdate?>? ServerUpdateFor { get; set; }
    /// <summary>A Kozpont sajat verzioja - ha a telephely mar elerte, a frissitesi keres torlodik.</summary>
    public string HubVersion { get; set; } = "";

    /// <summary>A Központ programtára (a Központ a mester) - csak akkor megy le, ha a telephelynél levő lenyomat eltér.</summary>
    public Func<HubCatalog>? CatalogFor { get; set; }
    /// <summary>A telephelyre leküldendő küszöb-szabályok (null: nem küldünk).</summary>
    public Func<HubSite, List<SiteAlertRule>>? AlertRulesFor { get; set; }
    /// <summary>A Központ mérés-kérése a telephelynek (a telephely a sajátjával összefésüli).</summary>
    public Func<HubSite, MetricConfig>? MonitorFor { get; set; }
    /// <summary>1.5.6: a telephely gépeinek CVE-találatai (a második paraméter a telephelynél levő lenyomat; null = nem változott).</summary>
    public Func<HubSite, string, HubCveData?>? CveFor { get; set; }
    /// <summary>1.5.7: a telephely naplóinak tárolója (központi napló). Ha be van állítva, a válasz jelzi: AcceptsLogs.</summary>
    public Action<HubSite, List<SiteLogChunk>>? OnLogs { get; set; }
    /// <summary>1.5.8: a telephely szerverének mérési megőrzése (a Központ állítja); ha be van állítva, a lezárt napi fájlokat is fogadjuk.</summary>
    public Func<HubSite, int?>? MetricKeepDaysFor { get; set; }

    /// <summary>A telephely új eseményei (lezárult internet-kiesések, riasztást adó SNMP-trapek) - a Központ-szerep jelzi őket.</summary>
    public Action<HubSite, List<SiteOutage>, List<SiteSnmpEvent>, List<SiteSnmpEvent>>? OnSiteEvents { get; set; }

    /// <summary>
    /// Az összesítőből az eddig nem jelzett események; a jelzettek határa a telephelynél (LastOutageStartUtc, LastSnmpEventId).
    /// Az első alkalommal (frissítés után) csak a friss eseményeket vesszük át, hogy a régiek ne menjenek ki tömegesen.
    /// </summary>
    internal static (List<SiteOutage> Outages, List<SiteSnmpEvent> Snmp, List<SiteSnmpEvent> Services) NewSiteEvents(HubSite site, SiteSummary sum, DateTime nowUtc)
    {
        var outs = (sum.Internet?.Outages ?? new()).Where(o => o.EndUtc is not null &&
            (site.LastOutageStartUtc is { } last ? o.StartUtc > last : o.EndUtc > nowUtc.AddHours(-6))).OrderBy(o => o.StartUtc).ToList();
        if (outs.Count > 0) site.LastOutageStartUtc = outs[^1].StartUtc;
        var snmp = (sum.SnmpEvents ?? new()).Where(e => site.LastSnmpEventId > 0 ? e.Id > site.LastSnmpEventId : e.AtUtc > nowUtc.AddHours(-1))
            .OrderBy(e => e.Id).Take(30).ToList();
        if (sum.SnmpEvents is { Count: > 0 } all) site.LastSnmpEventId = Math.Max(site.LastSnmpEventId, all.Max(e => e.Id));
        var svc = (sum.ServiceEvents ?? new()).Where(e => site.LastServiceEventId > 0 ? e.Id > site.LastServiceEventId : e.AtUtc > nowUtc.AddHours(-1))
            .OrderBy(e => e.Id).Take(30).ToList();
        if (sum.ServiceEvents is { Count: > 0 } sall) site.LastServiceEventId = Math.Max(site.LastServiceEventId, sall.Max(e => e.Id));
        return (outs, snmp, svc);
    }

    /// <summary>Csoport-művelet a telephelynek (a következő betárcsázáskor megy le, ha a telephely ismeri).</summary>
    public void QueueGroupOp(string siteId, HubGroupOp op)
    {
        if (!_sites.TryGetValue(siteId, out var s)) return;
        lock (_lock)
        {
            s.PendingGroupOps.Add(op);
            Fast(s);
            // Amíg le nem megy, a Központ felülete is az új állapotot mutassa (a következő összesítő úgyis felülírja).
            if (s.LastSummary is { } sum) ApplyGroupOpLocally(sum, op);
        }
        Save();
    }

    /// <summary>
    /// 1.5.7: a telephely műveletének sorba állítása (helyi szabály mentése / törlése, programkérés döntése). Csak az
    /// 1.5.7+ telephely ismeri (SupportsSiteOps); hibánál a hiba szövege, különben null.
    /// </summary>
    public string? QueueSiteOp(string siteId, HubSiteOp op)
    {
        if (!_sites.TryGetValue(siteId, out var s)) return "Nincs ilyen telephely.";
        if (s.LastSummary is not { SupportsSiteOps: true }) return "A telephely szervere még nem ismeri ezt (1.5.7-től) – előbb frissítsd.";
        lock (_lock)
        {
            if (s.PendingSiteOps.Count >= 200) return "Túl sok, még le nem küldött művelet – várd meg a telephely betárcsázását.";
            s.PendingSiteOps.Add(op);
            Fast(s);
            ApplySiteOpLocally(s.LastSummary, op);
        }
        Save();
        return null;
    }

    /// <summary>Amíg le nem megy, a Központ felülete az új állapotot mutassa (a következő összesítő úgyis felülírja).</summary>
    internal static void ApplySiteOpLocally(SiteSummary sum, HubSiteOp op)
    {
        var rules = sum.LocalRules ??= new();
        switch (op.Kind)
        {
            case "rule-save" when op.Rule is not null:
                var i = rules.FindIndex(r => r.Id == op.Rule.Id);
                if (i >= 0) rules[i] = op.Rule; else rules.Add(op.Rule);
                break;
            case "rule-delete": rules.RemoveAll(r => r.Id == op.Id); break;
            case "request-approve":
            case "request-deny":
                if ((sum.ProgramRequests ?? new()).FirstOrDefault(r => r.Id == op.Id && r.State == "pending") is { } pr)
                { pr.State = op.Kind == "request-approve" ? "installing" : "denied"; pr.DecidedBy = "Központ: " + op.By; pr.DecidedUtc = DateTime.UtcNow; pr.Note = op.Note; }
                break;
        }
    }

    private static void ApplyGroupOpLocally(SiteSummary sum, HubGroupOp op)
    {
        bool Is(string g) => g.Equals(op.Group, StringComparison.OrdinalIgnoreCase);
        var names = sum.GroupNames ??= sum.Agents.SelectMany(a => a.Groups).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        switch (op.Op)
        {
            case "create": if (!names.Any(Is)) names.Add(op.Group); break;
            case "delete": names.RemoveAll(Is); foreach (var a in sum.Agents) a.Groups.RemoveAll(Is); break;
            case "rename":
                for (int i = 0; i < names.Count; i++) if (Is(names[i])) names[i] = op.NewName;
                foreach (var a in sum.Agents) for (int i = 0; i < a.Groups.Count; i++) if (Is(a.Groups[i])) a.Groups[i] = op.NewName;
                break;
            case "members":
                if (!names.Any(Is)) names.Add(op.Group);
                foreach (var a in sum.Agents)
                {
                    var want = op.AgentIds.Contains(a.AgentId);
                    if (want && !a.Groups.Any(Is)) a.Groups.Add(op.Group);
                    else if (!want) a.Groups.RemoveAll(Is);
                }
                break;
        }
    }

    public void SetServerUpdate(string siteId, bool on) { if (_sites.TryGetValue(siteId, out var s)) { s.UpdateRequested = on; s.UpdateRequestedUtc = on ? DateTime.UtcNow : null; Fast(s); Save(); } }
    public void SetAgentAutoUpdate(string siteId, bool? on) { if (_sites.TryGetValue(siteId, out var s)) { s.AgentAutoUpdateWanted = on; Fast(s); Save(); } }

    /// <summary>
    /// A telephely mindig maga tárcsáz be (a Központ nem érheti el). Kérés után 10 percig 30 mp-enként jelentkezzen, hogy a
    /// kért művelet és az eredménye hamar átmenjen; utána visszaáll a beállított időközre.
    /// </summary>
    /// <summary>A telephely Janus-kulcsa (a titok már titkosítva érkezik; null = marad a régi, "" = törlés).</summary>
    public void SetJanus(string siteId, string keyId, string? secretProtected)
    {
        if (!_sites.TryGetValue(siteId, out var s)) return;
        s.JanusKeyId = keyId;
        if (secretProtected is not null) s.JanusSecretProtected = secretProtected;
        Save();
    }

    public void SetFast(string siteId) { if (_sites.TryGetValue(siteId, out var s)) { Fast(s); Save(); } }
    private static void Fast(HubSite s) => s.FastUntilUtc = DateTime.UtcNow.AddMinutes(10);

    // ---- Uj agent a Kozpontbol: a beleptetot csak a telephely keszitheti. A keres a kovetkezo
    // betarcsazaskor megy le, a token az azutanival jon fel. Csak memoriaban (lejar, nem irjuk lemezre).
    public sealed class TokenJob
    {
        public string SiteId = "", Label = "";
        public HubTokenRequest Request = new();
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime? SentUtc;
        public HubTokenResult? Result;
    }
    private readonly ConcurrentDictionary<string, TokenJob> _tokenJobs = new();

    public string RequestAgentToken(string siteId, string label, int lifetimeMinutes, int maxUses)
    {
        PurgeTokenJobs();
        if (_sites.TryGetValue(siteId, out var fs)) Fast(fs);
        var id = Guid.NewGuid().ToString("N");
        _tokenJobs[id] = new TokenJob
        {
            SiteId = siteId, Label = label,
            Request = new HubTokenRequest { RequestId = id, Label = label, LifetimeMinutes = lifetimeMinutes, MaxUses = maxUses }
        };
        return id;
    }

    public List<(string Id, TokenJob Job)> TokenJobs(string siteId)
    {
        PurgeTokenJobs();
        return _tokenJobs.Where(kv => kv.Value.SiteId == siteId).OrderByDescending(kv => kv.Value.CreatedUtc).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public TokenJob? GetTokenJob(string id) => _tokenJobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>A lejart (vagy 24 oranal regebben valaszt nem kapott) kerelmek torlese.</summary>
    private void PurgeTokenJobs()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _tokenJobs)
        {
            var life = kv.Value.Request.LifetimeMinutes > 0 ? TimeSpan.FromMinutes(kv.Value.Request.LifetimeMinutes) : TimeSpan.FromHours(24);
            if (now - kv.Value.CreatedUtc > (kv.Value.Result is null ? TimeSpan.FromHours(24) : life)) _tokenJobs.TryRemove(kv.Key, out _);
        }
    }

    public List<KeyLink>? KeyChain => _ks.Links.Count > 1 ? _ks.Links.ToList() : null;

    /// <summary>
    /// Kulcscsere a telephelyek ujraregisztralasa nelkul (a regi kulcs alairja az ujat). Elotte mentes. Eros (P-384)
    /// kulcs csak ha minden telephely tudja ellenorizni (1.4.5+); kulonben a regi telephely kizarna magat.
    /// </summary>
    public void RotateKeys(string reason = "kézi")
    {
        DataBackup.Create(_dir, "kulcscsere-elott");
        _ks.Rotate(_dir, "hub", strong: StrongKeyBlockers().Count == 0, reason: reason);
        Keys = _ks.Pair;
    }

    /// <summary>Azok a (nem visszavont) telephelyek, amelyek még nem tudnak P-384-es aláírást ellenőrizni.</summary>
    public List<string> StrongKeyBlockers() =>
        _sites.Values.Where(s => !s.Revoked && !(Version.TryParse(s.ServerVersion, out var v) && v >= new Version(1, 4, 5)))
            .Select(s => $"{s.Name} ({(s.ServerVersion.Length > 0 ? s.ServerVersion : "?")})").OrderBy(x => x).ToList();

    public IReadOnlyList<KeyHistoryItem> KeyHistory => _ks.History.ToList();
    public DateTime KeyCreatedUtc => _ks.Current.CreatedUtc;

    /// <summary>A kiadott telephely-tokenek (csak a lenyomatuk van meg) - a kulcslistához.</summary>
    public IReadOnlyCollection<HubToken> Tokens => _tokens.Values.ToList();
    public bool RemoveToken(string fingerprint)
    {
        if (!_tokens.TryRemove(fingerprint, out _)) return false;
        Save();
        return true;
    }

    public bool DisableToken(string fingerprint)
    {
        if (!_tokens.TryGetValue(fingerprint, out var t)) return false;
        _tokens[fingerprint] = t with { Enabled = false };
        Save();
        return true;
    }

    public HubStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _ks = KeyStore.LoadOrCreate(dir, "hub");   // a meglevo hub-*.pem automatikusan atkerul
        Keys = _ks.Pair;
        Load();
    }

    private Crypto.KeyPair LoadOrCreateKeys()
    {
        var pub = Path.Combine(_dir, "hub-public.pem");
        var priv = Path.Combine(_dir, "hub-private.pem");
        if (File.Exists(pub) && File.Exists(priv))
        {
            var p = File.ReadAllText(pub);
            return new Crypto.KeyPair(p, File.ReadAllText(priv), Crypto.PublicKeyFingerprint(p));
        }
        var k = Crypto.NewKeyPair();
        File.WriteAllText(pub, k.PublicKeyPem);
        File.WriteAllText(priv, k.PrivateKeyPem);
        return k;
    }

    // ---------------------------------------------------------------- tokenek

    public string CreateToken(string label, int lifetimeMinutes, int maxUses)
    {
        var token = Crypto.NewEnrollmentToken();
        var fp = Crypto.TokenFingerprint(token);
        _tokens[fp] = new HubToken(fp, DateTime.UtcNow, label, true)
        {
            ExpiresUtc = lifetimeMinutes > 0 ? DateTime.UtcNow.AddMinutes(lifetimeMinutes) : null,
            MaxUses = maxUses
        };
        Save();
        return token;
    }

    // ---------------------------------------------------------------- regisztráció

    public HubEnrollResponse Enroll(HubEnrollRequest req)
    {
        if (!Crypto.IsWellFormedToken(req.Token))
            return Fail("Érvénytelen token.");

        var tfp = Crypto.TokenFingerprint(req.Token);
        if (!_tokens.TryGetValue(tfp, out var tok) || !tok.IsUsable)
            return Fail("Érvénytelen, lejárt vagy elhasznált token.");

        string fp;
        try { fp = Crypto.PublicKeyFingerprint(req.PublicKeyPem); }
        catch { return Fail("Hibás publikus kulcs."); }

        lock (_lock)
        {
            var existing = _sites.Values.FirstOrDefault(s => s.Fingerprint == fp);
            if (existing is null)
            {
                existing = new HubSite
                {
                    SiteId = Guid.NewGuid().ToString("N"),
                    PublicKeyPem = req.PublicKeyPem,
                    Fingerprint = fp
                };
                _sites[existing.SiteId] = existing;
                _tokens[tfp] = tok with { Uses = tok.Uses + 1 };
            }
            existing.Name = req.SiteName;
            existing.ServerVersion = req.ServerVersion;
            existing.LastSeenUtc = DateTime.UtcNow;
            Save();

            return new HubEnrollResponse
            {
                Success = true,
                SiteId = existing.SiteId,
                Message = "Telephely regisztrálva.",
                HubPublicKeyPem = Keys.PublicKeyPem,
                KeyChain = KeyChain
            };
        }

        static HubEnrollResponse Fail(string m) => new() { Success = false, Message = m };
    }

    // ---------------------------------------------------------------- jelentkezés

    /// <summary>
    /// Alairt jelentkezes feldolgozasa. A valasz is alairt boritek (a Kozpont kulcsaval),
    /// benne a keres nonce-a, hogy a telephely a valaszt ehhez a kereshez kosse.
    /// </summary>
    /// <summary>
    /// A telephely betárcsázása. tunnelIp: ha a kérés a WireGuard-alagútból jött, a küldő alagút-címe (a WireGuard a társ
    /// kulcsához köti, nem hamisítható), különben null. Szigorú módban csak a telephely saját alagút-címéről fogadjuk.
    /// </summary>
    public SignedEnvelope Checkin(SignedEnvelope env, string? tunnelIp = null)
    {
        HubCheckinReply reply;

        if (!_sites.TryGetValue(env.SenderId, out var site))
            reply = new HubCheckinReply { Ok = false, Message = "Ismeretlen telephely – regisztráció szükséges." };
        else if (site.Revoked)
            reply = new HubCheckinReply { Ok = false, Message = "A telephely visszavonva." };
        else if (!env.Verify(site.PublicKeyPem))
            reply = new HubCheckinReply { Ok = false, Message = "Érvénytelen aláírás." };
        else
        {
            var body = env.Open<HubCheckinBody>();
            if (body is null)
                reply = new HubCheckinReply { Ok = false, Message = "Hibás tartalom." };
            else if (!Fresh(body.Timestamp, body.Nonce))
                reply = new HubCheckinReply { Ok = false, Message = "Elavult vagy ismételt kérés.", RequestNonce = body.Nonce };
            else if (WgRequired?.Invoke(site) == true && (tunnelIp is null || tunnelIp != site.WgAddress))
            {
                // Szigorú mód: alagúton kívül csak a kulcs frissíthető (/hub/wg-key); az ajánlat itt is lemegy, hogy a
                // telephely (ha még nincs alagútja) fel tudja építeni.
                if (WgKeys.IsKey(body.Summary.WgPublicKey)) SetWgKey(site, body.Summary.WgPublicKey);
                reply = new HubCheckinReply
                {
                    Ok = false, RequestNonce = body.Nonce, Wg = WgOfferFor?.Invoke(site),
                    Message = "Ez a telephely csak a WireGuard-alagúton tárcsázhat be – az alagút még nem épült fel, vagy más címről jött."
                };
            }
            else
            {
                // 1.5.9: az alagút kulcsa (az aláírt összesítőből), és ha az alagúton jött, innentől csak azon fogadjuk.
                if (WgKeys.IsKey(body.Summary.WgPublicKey)) SetWgKey(site, body.Summary.WgPublicKey);
                if (tunnelIp is not null && tunnelIp == site.WgAddress && !site.WgActive)
                { site.WgActive = true; site.WgActiveSinceUtc = DateTime.UtcNow; }
                List<HubTask> tasks;
                List<HubGroupOp>? groupOps = null;
                List<HubSiteOp>? siteOps = null;
                (List<SiteOutage> Outages, List<SiteSnmpEvent> Snmp, List<SiteSnmpEvent> Services) siteEvents;
                lock (_lock)
                {
                    site.LastSeenUtc = DateTime.UtcNow;
                    siteEvents = NewSiteEvents(site, body.Summary, DateTime.UtcNow);
                    // A telephely a valtozatlan programlistat nem kuldi ujra - az elozot tartjuk meg.
                    if (site.LastSummary is { } prevSum)
                        foreach (var a in body.Summary.Agents.Where(x => x.InstalledOmitted))
                        {
                            a.Installed = prevSum.Agents.FirstOrDefault(p => p.AgentId == a.AgentId)?.Installed ?? new();
                            a.InstalledOmitted = false;
                        }
                    // 1.5.6: a driverek / firmware is csak változáskor jönnek (null = a régi marad).
                    if (site.LastSummary is { } prevSum2)
                        foreach (var a in body.Summary.Agents.Where(x => x.Components is null))
                            a.Components = prevSum2.Agents.FirstOrDefault(p => p.AgentId == a.AgentId)?.Components;
                    TrackUpdates(site, body.Summary);
                    site.LastSummary = body.Summary;
                    site.SummarySiteUtc = DateTime.TryParse(body.Timestamp, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var sts) ? sts.ToUniversalTime() : null;
                    // A kért szerverfrissítés megtörtént (a telephely már a Központ verzióján van).
                    if (site.UpdateRequested && Version.TryParse(body.Summary.ServerVersion, out var sv) &&
                        Version.TryParse(HubVersion, out var hv) && sv >= hv) site.UpdateRequested = false;
                    // A Központból kért automatikus agent-frissítés átment: nem kell tovább küldeni.
                    if (site.AgentAutoUpdateWanted is bool w && body.Summary.SupportsHubUpdates && body.Summary.AgentAutoUpdate == w)
                        site.AgentAutoUpdateWanted = null;
                    site.ServerVersion = body.Summary.ServerVersion;
                    if (body.Summary.SiteName.Length > 0) site.Name = body.Summary.SiteName;
                    tasks = _pending.TryRemove(site.SiteId, out var q) ? q : new();
                    // Csoport-műveletek: csak az 1.5.0+ telephely ismeri. Az összesítő még a végrehajtás előtti állapot,
                    // ezért a most leküldötteket itt is alkalmazzuk (különben a felület 5 percig a régit mutatná).
                    if (body.Summary.SupportsGroupOps && site.PendingGroupOps.Count > 0)
                    {
                        groupOps = site.PendingGroupOps.ToList();
                        site.PendingGroupOps.Clear();
                        foreach (var op in groupOps) ApplyGroupOpLocally(body.Summary, op);
                    }
                    // 1.5.7: ugyanígy a helyi szabályok és a programkérés-döntések (az összesítő még a végrehajtás előtti)
                    if (body.Summary.SupportsSiteOps && site.PendingSiteOps.Count > 0)
                    {
                        siteOps = site.PendingSiteOps.ToList();
                        site.PendingSiteOps.Clear();
                        foreach (var op in siteOps) ApplySiteOpLocally(body.Summary, op);
                    }
                    // Kiadott feladatnal azonnal (osszeomlas utan ne menjen le ujra), kulonben kesleltetve:
                    // sok telephely betarcsazasa ne irja ujra minden alkalommal a teljes allapotot.
                    if (tasks.Count > 0 || groupOps is not null || siteOps is not null) Save(); else SaveSoon();
                }
                HubCatalog? catalog = null;
                try { if (CatalogFor?.Invoke() is { } c && c.Hash != body.Summary.HubCatalogHash) catalog = c; } catch { }

                if (body.Metrics is { Count: > 0 } rows && OnMetrics is { } h)
                    try { h(site, rows); } catch { }
                // 1.5.7: a telephely naplói (központi napló) – csak ha eltároltuk, jelezzük vissza (a telephely csak akkor lép tovább)
                var logsOk = OnLogs is not null;
                if (body.Logs is { Count: > 0 } logs && OnLogs is { } lh)
                    try { lh(site, logs); } catch { logsOk = false; }
                if ((siteEvents.Outages.Count > 0 || siteEvents.Snmp.Count > 0 || siteEvents.Services.Count > 0) && OnSiteEvents is { } se)
                    try { se(site, siteEvents.Outages, siteEvents.Snmp, siteEvents.Services); } catch { }

                // Uj agent: a felhozott beleptetok a sajat kerelmukhoz (csak ugyanattol a telephelytol),
                // es a meg le nem kuldott kerelmek.
                foreach (var res in body.TokenResults ?? new())
                    if (_tokenJobs.TryGetValue(res.RequestId, out var job) && job.SiteId == site.SiteId) job.Result = res;
                // Ha 15 percig nem jott valasz (elveszett a valasz), ujra lekuldjuk.
                var now = DateTime.UtcNow;
                var newReqs = _tokenJobs.Values.Where(j => j.SiteId == site.SiteId && j.Result is null &&
                                                           (j.SentUtc is null || now - j.SentUtc > TimeSpan.FromMinutes(15))).ToList();
                foreach (var j in newReqs) j.SentUtc = now;

                reply = new HubCheckinReply
                {
                    Ok = true,
                    RequestNonce = body.Nonce,
                    NextCheckinSeconds = site.FastUntilUtc is { } fu && fu > DateTime.UtcNow ? Math.Min(30, site.IntervalSeconds) : site.IntervalSeconds,
                    Tasks = tasks,
                    AcceptsMetrics = OnMetrics is not null,
                    Policies = PoliciesFor?.Invoke(site),
                    TokenRequests = newReqs.Count > 0 ? newReqs.Select(j => j.Request).ToList() : null,
                    ServerUpdate = site.UpdateRequested ? ServerUpdateFor?.Invoke(site) : null,
                    AgentAutoUpdate = site.AgentAutoUpdateWanted,
                    Catalog = catalog,
                    AlertRules = AlertRulesFor?.Invoke(site),
                    Monitor = MonitorFor?.Invoke(site),
                    GroupOps = groupOps,
                    Cve = CveFor?.Invoke(site, body.Summary.HubCveHash),
                    SiteOps = siteOps,
                    AcceptsLogs = logsOk,
                    MetricKeepDays = MetricKeepDaysFor?.Invoke(site),
                    AcceptsMetricFiles = MetricKeepDaysFor is not null,
                    Wg = WgOfferFor?.Invoke(site)
                };
            }
        }

        var envOut = SignedEnvelope.Create("hub", reply, Keys.PrivateKeyPem);
        envOut.SignerPublicKeyPem = Keys.PublicKeyPem;
        envOut.KeyChain = KeyChain;
        return envOut;
    }

    /// <summary>
    /// A frissülések felismerése az új összesítőből: a szerver és az agentek verzióváltása (a felület mutatja: mikor, mire),
    /// és a Központból kért agent-frissítés teljesülése (elérte a beépített agent verzióját, vagy eltűnt a gép).
    /// </summary>
    private static void TrackUpdates(HubSite site, SiteSummary sum)
    {
        var now = DateTime.UtcNow;
        static bool Newer(string a, string b) => Version.TryParse(a, out var x) && Version.TryParse(b, out var y) && x > y;
        if (site.ServerVersion.Length > 0 && Newer(sum.ServerVersion, site.ServerVersion))
            site.LastServerUpdate = new HubUpdateEvent { From = site.ServerVersion, To = sum.ServerVersion, AtUtc = now };
        var prev = new Dictionary<string, string>();
        foreach (var a in site.LastSummary?.Agents ?? new()) prev[a.AgentId] = a.AgentVersion;
        foreach (var a in sum.Agents)
            if (prev.TryGetValue(a.AgentId, out var pv) && Newer(a.AgentVersion, pv))
                site.AgentUpdates.Add(new HubUpdateEvent { AgentId = a.AgentId, Machine = a.MachineName, From = pv, To = a.AgentVersion, AtUtc = now });
        if (site.AgentUpdates.Count > 200) site.AgentUpdates.RemoveRange(0, site.AgentUpdates.Count - 200);
        foreach (var id in site.AgentUpdateRequests.Keys.ToList())
        {
            var ag = sum.Agents.FirstOrDefault(x => x.AgentId == id);
            if (ag is null || !Newer(sum.AgentPackageVersion, ag.AgentVersion)) site.AgentUpdateRequests.Remove(id);
        }
    }

    // ================================================================ WireGuard (1.5.9)
    /// <summary>Az alagút címtartománya (a Központ állítja be; null = nincs WireGuard).</summary>
    public Func<WgNet?>? WgNet { get; set; }
    /// <summary>Kell-e ehhez a telephelyhez alagút (szigorú mód: már ment rajta, vagy a Központban mindenkire kötelező).</summary>
    public Func<HubSite, bool>? WgRequired { get; set; }
    /// <summary>Az alagút ajánlata a telephelynek (a betárcsázás válaszában és a /hub/wg-key-re).</summary>
    public Func<HubSite, HubWgOffer?>? WgOfferFor { get; set; }
    /// <summary>A társak (telephely-kulcsok / címek) változtak: a Központ adapterét újra kell konfigurálni.</summary>
    public Action? OnWgPeersChanged { get; set; }

    /// <summary>
    /// A telephely WireGuard-kulcsának átvétele (az aláírt összesítőből vagy a /hub/wg-key-ből) és cím kiosztása az alagútban.
    /// Ugyanaz a kulcs két telephelynél nem lehet. Visszaadja, változott-e.
    /// </summary>
    public bool SetWgKey(HubSite site, string publicKey)
    {
        if (!WgKeys.IsKey(publicKey) || WgNet?.Invoke() is not { } net) return false;
        bool changed;
        lock (_lock)
        {
            if (_sites.Values.Any(x => x.SiteId != site.SiteId && x.WgPublicKey == publicKey)) return false;
            changed = site.WgPublicKey != publicKey;
            site.WgPublicKey = publicKey;
            if (!System.Net.IPAddress.TryParse(site.WgAddress, out var cur) || !net.Contains(cur))
            {
                var used = _sites.Values.Where(x => x.SiteId != site.SiteId).Select(x => x.WgAddress).ToHashSet();
                // 1 = a Központ; a telephelyek 2-től
                for (var n = 2; net.Host(n) is { } ip; n++) if (!used.Contains(ip)) { site.WgAddress = ip; changed = true; break; }
            }
        }
        if (changed) { Save(); try { OnWgPeersChanged?.Invoke(); } catch { } }
        return changed;
    }

    /// <summary>A Központ adapterének társai: minden nem visszavont telephely kulccsal és címmel.</summary>
    public List<(string PublicKey, string Address, string Name)> WgPeers()
    {
        lock (_lock)
            return _sites.Values.Where(s => !s.Revoked && WgKeys.IsKey(s.WgPublicKey) && s.WgAddress.Length > 0)
                .Select(s => (s.WgPublicKey, s.WgAddress, s.Name)).ToList();
    }

    /// <summary>A szigorú mód visszavonása egy telephelynél (a Központ felületén) – újra HTTPS-en is kezdhet.</summary>
    public void SetWgActive(string siteId, bool on)
    {
        if (!_sites.TryGetValue(siteId, out var s)) return;
        lock (_lock) { s.WgActive = on; s.WgActiveSinceUtc = on ? DateTime.UtcNow : null; }
        Save();
    }

    /// <summary>1.5.8: egy telephely aláírt kérése (nem a betárcsázás): ismert, nem visszavont, a kulcsa, friss és egyszeri.</summary>
    public HubSite? VerifySite(SignedEnvelope env, string ts, string nonce) =>
        _sites.TryGetValue(env.SenderId, out var s) && !s.Revoked && env.Verify(s.PublicKeyPem) && Fresh(ts, nonce) ? s : null;

    /// <summary>A Központ aláírt válasza (a kulcslánccal együtt, mint a betárcsázásnál).</summary>
    public SignedEnvelope Sign<T>(T body)
    {
        var e = SignedEnvelope.Create("hub", body, Keys.PrivateKeyPem);
        e.SignerPublicKeyPem = Keys.PublicKeyPem;
        e.KeyChain = KeyChain;
        return e;
    }

    private bool Fresh(string ts, string nonce)
    {
        if (!DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return false;
        if (Math.Abs((DateTime.UtcNow - t.ToUniversalTime()).TotalMinutes) > 5) return false;

        var now = DateTime.UtcNow;
        foreach (var kv in _nonces)
            if (now - kv.Value > TimeSpan.FromMinutes(10)) _nonces.TryRemove(kv.Key, out _);
        return _nonces.TryAdd(nonce, now);
    }

    // ---------------------------------------------------------------- vezérlés a felületről

    public IReadOnlyCollection<HubSite> Sites => _sites.Values.ToList();

    public void SetInterval(string siteId, int seconds)
    {
        if (_sites.TryGetValue(siteId, out var s)) { s.IntervalSeconds = Math.Clamp(seconds, 30, 86400); Save(); }
    }

    public void QueueTask(string siteId, HubTask task)
    {
        _pending.AddOrUpdate(siteId, _ => new() { task }, (_, l) => { lock (l) l.Add(task); return l; });
        if (_sites.TryGetValue(siteId, out var s))
        {
            Fast(s);
            if (task.Kind == AgentTaskKind.UpdateAgent) lock (_lock) s.AgentUpdateRequests[task.AgentId] = DateTime.UtcNow;
        }
        Save();
    }

    public void Revoke(string siteId)
    {
        if (_sites.TryGetValue(siteId, out var s)) { s.Revoked = true; Save(); }
    }

    public void Remove(string siteId)
    {
        _sites.TryRemove(siteId, out _);
        _pending.TryRemove(siteId, out _);
        Save();
    }

    // ---------------------------------------------------------------- perzisztencia

    private sealed class Persist
    {
        public List<HubSite> Sites { get; set; } = new();
        public List<HubToken> Tokens { get; set; } = new();
        public Dictionary<string, List<HubTask>> Pending { get; set; } = new();
    }

    private int _saveScheduled;

    /// <summary>Kesleltetett mentes: a kovetkezo 5 mp valtozasai egyetlen irassal mennek ki.</summary>
    private void SaveSoon()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => { Interlocked.Exchange(ref _saveScheduled, 0); Save(); });
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var p = new Persist
                {
                    Sites = _sites.Values.ToList(),
                    Tokens = _tokens.Values.ToList(),
                    Pending = _pending.ToDictionary(k => k.Key, k => { lock (k.Value) return k.Value.ToList(); })
                };
                var tmp = Path.Combine(_dir, "hub.json.tmp");
                File.WriteAllText(tmp, JsonSerializer.Serialize(p, ProtocolJson.Options));
                File.Move(tmp, Path.Combine(_dir, "hub.json"), overwrite: true);
            }
            catch { }
        }
    }

    private void Load()
    {
        try
        {
            var path = Path.Combine(_dir, "hub.json");
            if (!File.Exists(path)) return;
            var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(path), ProtocolJson.Options);
            if (p is null) return;
            foreach (var s in p.Sites) _sites[s.SiteId] = s;
            foreach (var t in p.Tokens) _tokens[t.Fingerprint] = t;
            foreach (var (k, v) in p.Pending ?? new()) _pending[k] = v;
        }
        catch { }
    }
}
