using System.Collections.Concurrent;
using System.Text.Json;

namespace DevToolbox.Fleet.Server;

public sealed class EnrolledAgent
{
    public string AgentId { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTime EnrolledUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
    public bool Revoked { get; set; }

    // A legutobbi allapot, amit az agent kuldott
    public AgentStatus? LastStatus { get; set; }

    /// <summary>Gepcsoportok (pl. "Könyvelés", "Szerverek") - csoportos muveletekhez es szabalyokhoz.</summary>
    public List<string> Groups { get; set; } = new();
    /// <summary>Automatikus szabalyok utolso kiadasa (szabaly:tipus -> ido), hogy egy ablakban egyszer fusson.</summary>
    public Dictionary<string, DateTime> PolicyRuns { get; set; } = new();
    /// <summary>A Központ kérte: a következő jelentkezéskor ajánljuk fel az agent-frissítést (az automatikus frissítéstől függetlenül).</summary>
    public bool ForceUpdate { get; set; }

    // ---- az agent kulcsának cseréje (1.4.5-től hetente)
    /// <summary>
    /// Kulcscsere után a RÉGI kulcs, amíg az agent az újjal nem jelentkezik: ha a visszaigazolás elveszett, az
    /// agent a régivel folytatja - kizárni nem szabad. Az első, új kulccsal aláírt kérésnél törlődik.
    /// </summary>
    public string PreviousPublicKeyPem { get; set; } = "";
    /// <summary>A jelenlegi kulcs mióta él (null: a regisztráció óta ugyanaz).</summary>
    public DateTime? KeyChangedUtc { get; set; }
    /// <summary>Az agent kulcsai időrendben (ujjlenyomat, algoritmus, mikor, miért cserélődött) - a kulcslistához.</summary>
    public List<KeyHistoryItem> KeyHistory { get; set; } = new();

    public bool IsOnline => LastSeenUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromMinutes(15);
}

/// <summary>
/// A szerver perzisztens állapota: regisztrált agentek, függőben lévő feladatok,
/// eredmények. Egyszerű JSON-fájl a tárolás – nincs külső adatbázis-függés, ezért
/// egy kis szerveren is elfut.
///
/// Szálbiztos: a HTTP-kiszolgáló több kérést párhuzamosan dolgozhat fel.
/// </summary>
public sealed class FleetStore
{
    private readonly string _dir;
    private readonly object _lock = new();

    private readonly ConcurrentDictionary<string, EnrolledAgent> _agents = new();
    private readonly ConcurrentDictionary<string, List<AgentTask>> _pending = new();
    private readonly ConcurrentQueue<TaskResult> _results = new();

    // Ismetles-vedelem: a mar latott nonce-okat rovid ideig megjegyezzuk.
    private readonly ConcurrentDictionary<string, DateTime> _seenNonces = new();

    // A token-lenyomatok, amivel regisztralni lehet. Tobb is lehet (pl. lejaro + uj).
    private readonly ConcurrentDictionary<string, TokenInfo> _tokens = new();

    public sealed record TokenInfo(string Fingerprint, DateTime CreatedUtc, string Label, bool Enabled)
    {
        /// <summary>Lejarat UTC. Null = nincs lejarat (regi tokenek).</summary>
        public DateTime? ExpiresUtc { get; init; }
        /// <summary>Hany gepre hasznalhato. 0 vagy null = korlatlan (regi tokenek).</summary>
        public int MaxUses { get; init; }
        /// <summary>Eddig hanyszor hasznaltak fel sikeresen.</summary>
        public int Uses { get; init; }

        public bool IsExpired => ExpiresUtc is { } e && DateTime.UtcNow > e;
        public bool IsUsedUp => MaxUses > 0 && Uses >= MaxUses;
        public bool IsUsable => Enabled && !IsExpired && !IsUsedUp;
    }

    public Crypto.KeyPair ServerKeys { get; private set; }
    private KeyStore? _keys;

    /// <summary>A kulcslanc (kulcscsere utan kuldjuk az agenteknek). Null, ha meg nem volt csere.</summary>
    public List<KeyLink>? KeyChain => _keys is { Links.Count: > 1 } k ? k.Links.ToList() : null;

    /// <summary>
    /// Kulcscsere az agentek ujraregisztralasa nelkul: az uj kulcsot a regi alairja, az
    /// agentek a kovetkezo jelentkezeskor ellenorzik a lancot es atallnak. Elotte mentes keszul.
    /// </summary>
    public void RotateKeys() => RotateKeys("kézi");

    /// <summary>
    /// Igaz: ez a peldany a fleet.json EGYETLEN irója (a szerver-szolgaltatas).
    /// Hamis: kliens (DevToolbox Flotta ful, parancssor) - csak olvas, a modositasait
    /// parancsfajlkent a postaladaba teszi, amit a szerver vesz fel.
    ///
    /// Miert: ha tobb folyamat is sajat memoriabeli peldanyt tart es mind menti a fajlt,
    /// felulirjak egymast - a kliensbol letrehozott token vagy feladat nyomtalanul eltunik.
    /// </summary>
    public bool IsOwner { get; }

    private string InboxDir => Path.Combine(_dir, "inbox");

    public FleetStore(string dataDir, bool owner = true)
    {
        _dir = dataDir;
        IsOwner = owner;
        Directory.CreateDirectory(_dir);

        if (owner)
        {
            // Verziozott kulcstar: a meglevo server-*.pem automatikusan atkerul, a kulcs nem valtozik.
            _keys = KeyStore.LoadOrCreate(_dir, "server");
            ServerKeys = _keys.Pair;
        }
        else ServerKeys = LoadPublicKeyOnly();
        Load();

        if (owner) ProcessInbox();
    }

    /// <summary>A kliensnek csak a publikus kulcs kell (az ujjlenyomathoz) - a privat kulcsot nem olvassa.</summary>
    private Crypto.KeyPair LoadPublicKeyOnly()
    {
        var pubPath = Path.Combine(_dir, "server-public.pem");
        if (!File.Exists(pubPath))
            throw new InvalidOperationException(
                "Nincs szerverkulcs ebben a mappában – a Fleet Server még nem futott.");
        var pub = File.ReadAllText(pubPath);
        return new Crypto.KeyPair(pub, "", Crypto.PublicKeyFingerprint(pub));
    }

    // ---------------------------------------------------------------- szerverkulcs

    private Crypto.KeyPair LoadOrCreateServerKeys()
    {
        var pubPath = Path.Combine(_dir, "server-public.pem");
        var privPath = Path.Combine(_dir, "server-private.pem");

        if (File.Exists(pubPath) && File.Exists(privPath))
        {
            var pub = File.ReadAllText(pubPath);
            var priv = File.ReadAllText(privPath);
            return new Crypto.KeyPair(pub, priv, Crypto.PublicKeyFingerprint(pub));
        }

        var keys = Crypto.NewKeyPair();
        File.WriteAllText(pubPath, keys.PublicKeyPem);
        File.WriteAllText(privPath, keys.PrivateKeyPem);

        // A szerver privat kulcsa a legerzekenyebb titok: ezzel adja ki magat a szerver.
        // A ProgramData alapbol MINDENKI altal olvashato, ezert a fajl jogait leszukitjuk
        // a jelenlegi felhasznalora (a szolgaltatas fioka) es a rendszergazdakra.
        // Ha a platform nem Windows vagy nem sikerul, a hiba nem vegzetes, de logoljuk.
        SecureFile(privPath);
        return keys;
    }

    /// <summary>
    /// Egy fajl jogosultsagainak leszukitese: csak a jelenlegi felhasznalo (a szolgaltatas
    /// fiokja) es a beepitett rendszergazdak fernek hozza. Az oroklodest megszakitja.
    /// </summary>
    private static void SecureFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // meglevo szabalyok torlese
            foreach (System.Security.AccessControl.FileSystemAccessRule r in
                     sec.GetAccessRules(true, false, typeof(System.Security.Principal.NTAccount)))
                sec.RemoveAccessRule(r);

            var current = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (current is not null)
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    current,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));

            var admins = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                admins,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));

            fi.SetAccessControl(sec);
        }
        catch { /* nem vegzetes, de a telepito ACL-je a masodik vedvonal */ }
    }

    // ---------------------------------------------------------------- token kezelés

    public string CreateEnrollmentToken(string label) => CreateEnrollmentToken(label, 0, 0);

    /// <summary>
    /// Belepteto token lejarattal es felhasznalasi limittel. lifetimeMinutes=0 -> nincs
    /// lejarat, maxUses=0 -> korlatlan. A rovid eletu, egyszer hasznalatos token a
    /// bevalt minta: ha a telepitocsomag kikerul, par perc mulva ugyis ervenytelen.
    /// </summary>
    public string CreateEnrollmentToken(string label, int lifetimeMinutes, int maxUses)
    {
        var token = Crypto.NewEnrollmentToken();
        var fp = Crypto.TokenFingerprint(token);
        var info = new TokenInfo(fp, DateTime.UtcNow, label, true)
        {
            ExpiresUtc = lifetimeMinutes > 0 ? DateTime.UtcNow.AddMinutes(lifetimeMinutes) : null,
            MaxUses = maxUses
        };
        _tokens[fp] = info;

        if (IsOwner) Save();
        else PostInbox(new InboxCmd { Kind = "token", Token = info });

        return token;
    }

    public bool IsValidToken(string token)
    {
        if (!Crypto.IsWellFormedToken(token)) return false;
        var fp = Crypto.TokenFingerprint(token);
        return _tokens.TryGetValue(fp, out var info) && info.IsUsable;
    }

    /// <summary>Sikeres regisztracio utan a token felhasznalasszamanak novelese.</summary>
    private void ConsumeToken(string token)
    {
        var fp = Crypto.TokenFingerprint(token);
        if (_tokens.TryGetValue(fp, out var info))
            _tokens[fp] = info with { Uses = info.Uses + 1 };
    }

    public IReadOnlyCollection<TokenInfo> Tokens => _tokens.Values.ToList();

    /// <summary>Kiadott token végleges törlése a listából (a felhasználó kérése) - utána nem használható, és nem is látszik.</summary>
    public bool RemoveToken(string fingerprint)
    {
        if (!_tokens.TryRemove(fingerprint, out _)) return false;
        if (IsOwner) Save();
        else PostInbox(new InboxCmd { Kind = "token-remove", Fingerprint = fingerprint });
        return true;
    }

    public void SetTokenEnabled(string fingerprint, bool enabled)
    {
        if (_tokens.TryGetValue(fingerprint, out var info))
        {
            _tokens[fingerprint] = info with { Enabled = enabled };
            if (IsOwner) Save();
            else PostInbox(new InboxCmd { Kind = "token-enabled", Fingerprint = fingerprint, Enabled = enabled });
        }
    }

    // ---------------------------------------------------------------- regisztráció

    public EnrollResponse Enroll(EnrollRequest req)
    {
        ProcessInbox();   // egy kliensbol most letrehozott token is ervenyes legyen

        if (!IsValidToken(req.Token))
            return new EnrollResponse { Success = false, Message = "Érvénytelen vagy letiltott beléptető-token." };

        if (string.IsNullOrWhiteSpace(req.PublicKeyPem))
            return new EnrollResponse { Success = false, Message = "Hiányzó publikus kulcs." };

        // A bekuldott ujjlenyomatnak egyeznie kell a kulccsal - nem bizunk a kliens szavaban.
        string realFp;
        try { realFp = Crypto.PublicKeyFingerprint(req.PublicKeyPem); }
        catch { return new EnrollResponse { Success = false, Message = "Hibás publikus kulcs." }; }

        // Ha ezt a kulcsot mar regisztraltak, ugyanazt az agentet adjuk vissza (idempotens).
        var existing = _agents.Values.FirstOrDefault(a => a.Fingerprint == realFp);
        if (existing is not null)
        {
            existing.MachineName = req.MachineName;
            existing.LastSeenUtc = DateTime.UtcNow;
            Save();
            return new EnrollResponse
            {
                Success = true,
                AgentId = existing.AgentId,
                Message = "Már regisztrált agent.",
                ServerPublicKeyPem = ServerKeys.PublicKeyPem,
                KeyChain = KeyChain
            };
        }

        var agent = new EnrolledAgent
        {
            AgentId = Guid.NewGuid().ToString("N"),
            PublicKeyPem = req.PublicKeyPem,
            Fingerprint = realFp,
            MachineName = req.MachineName,
            OperatingSystem = req.OperatingSystem,
            AgentVersion = req.AgentVersion,
            EnrolledUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            KeyHistory = { new KeyHistoryItem { Fingerprint = realFp, Algorithm = Crypto.Algorithm(req.PublicKeyPem), CreatedUtc = DateTime.UtcNow, Reason = "regisztráció" } }
        };

        _agents[agent.AgentId] = agent;
        ConsumeToken(req.Token);   // egy uj gep -> egy felhasznalas
        Save();

        return new EnrollResponse
        {
            Success = true,
            AgentId = agent.AgentId,
            Message = "Sikeres regisztráció.",
            ServerPublicKeyPem = ServerKeys.PublicKeyPem,
            KeyChain = KeyChain
        };
    }

    // ---------------------------------------------------------------- jelentkezés

    public CheckinResponse Checkin(CheckinRequest req)
    {
        ProcessInbox();   // visszavonas es feladat a kliensekbol

        if (!_agents.TryGetValue(req.AgentId, out var agent))
            return new CheckinResponse { Ok = false, Message = "Ismeretlen agent – regisztráció szükséges." };

        if (agent.Revoked)
            return new CheckinResponse { Ok = false, Message = "Az agent visszavonva." };

        // 1. Aláírás ellenőrzése az agent TÁROLT publikus kulcsával (kulcscsere után a megerősítésig a régivel is).
        if (VerifyAgent(agent, req.CanonicalPayload(), req.Signature) == KeyMatch.None)
            return new CheckinResponse { Ok = false, Message = "Érvénytelen aláírás." };

        // 2. Ismétlés-védelem: időbélyeg + nonce.
        if (!CheckFreshness(req.Timestamp, req.Nonce))
            return new CheckinResponse { Ok = false, Message = "Elavult vagy ismételt kérés." };

        // 3. Állapot frissítése.
        agent.LastSeenUtc = DateTime.UtcNow;
        // A percenkenti mintak a metrikatarba mennek (a hivo dolgozza fel a keresbol), a fleet.json-ba nem.
        agent.LastStatus = req.Status.WithoutSamples();
        agent.AgentVersion = req.Status.AgentVersion;

        // 4. Függőben lévő feladatok átadása.
        var tasks = new List<AgentTask>();
        if (_pending.TryRemove(req.AgentId, out var queued)) tasks = queued;

        // Kiadott feladatnal azonnal mentunk (egy osszeomlas utan ne menjen ki ujra, pl. ujrainditas),
        // kulonben eleg nehany masodperc mulva egyszer - sok gep jelentkezese ne irja ujra minden alkalommal.
        if (tasks.Count > 0) Save(); else SaveSoon();
        return new CheckinResponse { Ok = true, Tasks = tasks, NextCheckinSeconds = 300 };
    }

    public bool RecordResult(TaskResult result)
    {
        if (!_agents.TryGetValue(result.AgentId, out var agent) || agent.Revoked) return false;
        if (VerifyAgent(agent, result.CanonicalPayload(), result.Signature) == KeyMatch.None) return false;
        if (!CheckFreshness(result.Timestamp, result.Nonce)) return false;

        _results.Enqueue(result);
        while (_results.Count > 5000) _results.TryDequeue(out _);
        SaveSoon();
        return true;
    }

    /// <summary>
    /// 1.5.7: egy agent aláírt kérésének ellenőrzése (a tálca-felület kérései): ismert, nem visszavont gép, az agent
    /// kulcsa, friss és nem ismételt. Hibánál a hiba szövege, különben null.
    /// </summary>
    public string? VerifyAgentRequest(string agentId, string payload, string signature, string timestamp, string nonce)
    {
        if (!_agents.TryGetValue(agentId, out var a)) return "Ismeretlen agent.";
        if (a.Revoked) return "Az agent visszavonva.";
        if (VerifyAgent(a, payload, signature) == KeyMatch.None) return "Érvénytelen aláírás.";
        if (!CheckFreshness(timestamp, nonce)) return "Elavult vagy ismételt kérés.";
        return null;
    }

    private bool CheckFreshness(string timestampIso, string nonce)
    {
        if (!DateTime.TryParse(timestampIso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            return false;

        // 5 perces ablak: ennyit tur a kesleltetes es az oracsuszas.
        if (Math.Abs((DateTime.UtcNow - ts.ToUniversalTime()).TotalMinutes) > 5) return false;

        // A nonce csak egyszer fogadhato el.
        var now = DateTime.UtcNow;
        foreach (var kv in _seenNonces)
            if (now - kv.Value > TimeSpan.FromMinutes(10)) _seenNonces.TryRemove(kv.Key, out _);

        return _seenNonces.TryAdd(nonce, now);
    }

    // ---------------------------------------------------------------- agent-kulcscsere

    public enum KeyMatch { None, Current, Previous }

    /// <summary>
    /// Aláírás ellenőrzése az agent kulcsával. Kulcscsere után a megerősítésig a régi kulcs is érvényes (ha a
    /// visszaigazolás elveszett, az agent a régivel folytatja). Az első, ÚJ kulccsal aláírt kérés megerősíti a
    /// cserét: a régi kulcs onnantól semmire nem jó.
    /// </summary>
    public KeyMatch VerifyAgent(EnrolledAgent a, string payload, string signature)
    {
        if (Crypto.Verify(a.PublicKeyPem, payload, signature))
        {
            if (a.PreviousPublicKeyPem.Length > 0)
            {
                a.PreviousPublicKeyPem = "";
                if (a.KeyHistory.Count > 0) a.KeyHistory[^1].Reason += " – megerősítve";
                SaveSoon();
            }
            return KeyMatch.Current;
        }
        return a.PreviousPublicKeyPem.Length > 0 && Crypto.Verify(a.PreviousPublicKeyPem, payload, signature) ? KeyMatch.Previous : KeyMatch.None;
    }

    /// <summary>
    /// Az agent kulcscseréje. Feltételek: ismert, nem visszavont agent; friss, nem ismételt kérés; a jelenlegi (vagy
    /// a még meg nem erősített csere előtti) kulcs aláírása; az ÚJ kulcs aláírása is (birtoklás-bizonyítás); az új
    /// kulcs érvényes, legalább P-256, és más gépnél nincs használatban. Visszaadja: siker, üzenet, új ujjlenyomat.
    /// </summary>
    public (bool Ok, string Message, string Fingerprint) RotateAgentKey(KeyRotateRequest r)
    {
        if (!IsOwner) return (false, "Csak a szerver-szolgáltatás cserélhet kulcsot.", "");
        lock (_lock)
        {
            if (!_agents.TryGetValue(r.AgentId, out var a)) return (false, "Ismeretlen agent – regisztráció szükséges.", "");
            if (a.Revoked) return (false, "Az agent visszavonva.", "");
            var payload = r.CanonicalPayload();
            var match = VerifyAgent(a, payload, r.Signature);
            if (match == KeyMatch.None) return (false, "Érvénytelen aláírás.", "");
            if (!CheckFreshness(r.Timestamp, r.Nonce)) return (false, "Elavult vagy ismételt kérés.", "");

            string fp;
            try { fp = Crypto.PublicKeyFingerprint(r.NewPublicKeyPem); } catch { return (false, "Hibás új kulcs.", ""); }
            if (Crypto.KeyBits(r.NewPublicKeyPem) < 256) return (false, "Túl gyenge új kulcs.", "");
            if (!Crypto.Verify(r.NewPublicKeyPem, payload, r.NewKeySignature))
                return (false, "Az új kulcs aláírása hiányzik vagy hibás (birtoklás-bizonyítás).", "");
            if (fp == a.Fingerprint || _agents.Values.Any(x => x.AgentId != a.AgentId && x.Fingerprint == fp))
                return (false, "Ez a kulcs már használatban van.", "");

            var now = DateTime.UtcNow;
            if (a.KeyHistory.Count == 0)   // 1.4.5 előtt regisztrált agent: az eddigi kulcs utólag az előzménybe
                a.KeyHistory.Add(new KeyHistoryItem { Fingerprint = a.Fingerprint, Algorithm = Crypto.Algorithm(a.PublicKeyPem), CreatedUtc = a.EnrolledUtc, Reason = "regisztráció" });
            if (match == KeyMatch.Previous)
            {
                // Az előző cserét az agent nem kapta meg (a régi kulccsal jön): az az új kulcs soha nem lett használva - eldobjuk.
                var last = a.KeyHistory[^1];
                last.RetiredUtc = now; last.Reason += " – az agent nem kapta meg, elvetve";
            }
            else
            {
                a.PreviousPublicKeyPem = a.PublicKeyPem;
                if (a.KeyHistory[^1].RetiredUtc is null) a.KeyHistory[^1].RetiredUtc = now;
            }
            a.PublicKeyPem = r.NewPublicKeyPem;
            a.Fingerprint = fp;
            a.KeyChangedUtc = now;
            a.KeyHistory.Add(new KeyHistoryItem { Fingerprint = fp, Algorithm = Crypto.Algorithm(r.NewPublicKeyPem), CreatedUtc = now, Reason = "heti csere" });
            if (a.KeyHistory.Count > 200) a.KeyHistory.RemoveRange(0, a.KeyHistory.Count - 200);
            Save();
            return (true, "Kulcs lecserélve.", fp);
        }
    }

    /// <summary>
    /// A szerver kulcsa csak akkor lehet P-384-es, ha minden (nem visszavont) agent ellenőrizni tudja: 1.4.5 előtti agent
    /// SHA-256-tal ellenőriz, és többé nem fogadná el a szervert. Visszaadja a „visszatartó” gépek nevét (üres = mehet).
    /// </summary>
    public List<string> StrongKeyBlockers() =>
        _agents.Values.Where(a => !a.Revoked && !(Version.TryParse(a.AgentVersion, out var v) && v >= StrongKeyMinVersion))
            .Select(a => $"{a.MachineName} ({(a.AgentVersion.Length > 0 ? a.AgentVersion : "?")})").OrderBy(x => x).ToList();

    public static readonly Version StrongKeyMinVersion = new(1, 4, 5);

    /// <summary>A szerver kulcsának cseréje (kézi vagy automatikus); erős kulcs, ha minden agent tudja ellenőrizni.</summary>
    public void RotateKeys(string reason)
    {
        if (!IsOwner || _keys is null) throw new InvalidOperationException("Kulcscsere csak a szerver-szolgáltatásból.");
        DataBackup.Create(_dir, "kulcscsere-elott");
        _keys.Rotate(_dir, "server", strong: StrongKeyBlockers().Count == 0, reason: reason);
        ServerKeys = _keys.Pair;
    }

    /// <summary>A szerver kulcsainak előzménye és a jelenlegi kulcs kora (a kulcslistához és az automatikus cseréhez).</summary>
    public IReadOnlyList<KeyHistoryItem> ServerKeyHistory => _keys?.History.ToList() ?? new();
    public DateTime ServerKeyCreatedUtc => _keys?.Current.CreatedUtc ?? DateTime.UtcNow;

    // ---------------------------------------------------------------- feladat-kiosztás

    /// <summary>Csoportok beallitasa (csak a szerver-szolgaltatasbol).</summary>
    public bool SetGroups(string agentId, IEnumerable<string> groups)
    {
        if (!_agents.TryGetValue(agentId, out var a)) return false;
        a.Groups = groups.Select(g => g.Trim()).Where(g => g.Length is > 0 and <= 40)
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToList();
        Save();
        return true;
    }

    public void SaveState() => Save();

    /// <summary>Agent-frissítés kérése (a Központból): a következő jelentkezéskor felajánljuk.</summary>
    public void RequestAgentUpdate(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var a) && !a.Revoked) { a.ForceUpdate = true; Save(); }
    }

    public void QueueTask(string agentId, AgentTask task)
    {
        if (!IsOwner)
        {
            PostInbox(new InboxCmd { Kind = "task", AgentId = agentId, Task = task });
            return;
        }

        _pending.AddOrUpdate(agentId,
            _ => new List<AgentTask> { task },
            (_, list) => { lock (list) { list.Add(task); } return list; });
        Save();
    }

    public IReadOnlyCollection<EnrolledAgent> Agents => _agents.Values.ToList();
    public IReadOnlyCollection<TaskResult> RecentResults(int count) =>
        _results.Reverse().Take(count).ToList();

    /// <summary>1.5.9: egy feladat eredménye azonosító szerint (a webes admin terminál ebből olvassa a parancs kimenetét).</summary>
    public TaskResult? GetResult(string taskId) =>
        string.IsNullOrEmpty(taskId) ? null : _results.FirstOrDefault(r => r.TaskId == taskId);

    public EnrolledAgent? GetAgent(string id) =>
        _agents.TryGetValue(id, out var a) ? a : null;

    public void RevokeAgent(string id)
    {
        if (_agents.TryGetValue(id, out var a)) a.Revoked = true;
        if (IsOwner) Save();
        else PostInbox(new InboxCmd { Kind = "revoke", AgentId = id });
    }

    public void RemoveAgent(string id)
    {
        _agents.TryRemove(id, out _);
        _pending.TryRemove(id, out _);
        if (IsOwner) Save();
        else PostInbox(new InboxCmd { Kind = "remove", AgentId = id });
    }

    // ---------------------------------------------------------------- postaláda

    private sealed class InboxCmd
    {
        public string Kind { get; set; } = "";
        public TokenInfo? Token { get; set; }
        public string? Fingerprint { get; set; }
        public bool Enabled { get; set; }
        public string? AgentId { get; set; }
        public AgentTask? Task { get; set; }
    }

    /// <summary>Parancsfajl atomikus irasa: .tmp-be, majd atnevezes - a szerver felkesz fajlt nem lat.</summary>
    private void PostInbox(InboxCmd cmd)
    {
        Directory.CreateDirectory(InboxDir);
        var name = $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}";
        var tmp = Path.Combine(InboxDir, name + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(cmd, ProtocolJson.Options));
        File.Move(tmp, Path.Combine(InboxDir, name + ".json"));
    }

    /// <summary>
    /// A kliensek parancsainak vegrehajtasa (csak a szerverben). Idorendben, egyenkent;
    /// a feldolgozott fajl torlodik. Olcso: egy kis mappa listazasa.
    /// </summary>
    public void ProcessInbox()
    {
        if (!IsOwner || !Directory.Exists(InboxDir)) return;

        string[] files;
        try { files = Directory.GetFiles(InboxDir, "*.json"); }
        catch { return; }
        if (files.Length == 0) return;

        lock (_lock)
        {
            foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    var cmd = JsonSerializer.Deserialize<InboxCmd>(File.ReadAllText(f), ProtocolJson.Options);
                    if (cmd is not null) Apply(cmd);
                }
                catch { /* serult parancs: kihagyjuk, de toroljuk, hogy ne akadjon be */ }
                try { File.Delete(f); } catch { }
            }
        }
        Save();
    }

    private void Apply(InboxCmd cmd)
    {
        switch (cmd.Kind)
        {
            case "token" when cmd.Token is not null:
                _tokens[cmd.Token.Fingerprint] = cmd.Token;
                break;
            case "token-remove" when cmd.Fingerprint is not null:
                _tokens.TryRemove(cmd.Fingerprint, out _);
                break;
            case "token-enabled" when cmd.Fingerprint is not null:
                if (_tokens.TryGetValue(cmd.Fingerprint, out var t))
                    _tokens[cmd.Fingerprint] = t with { Enabled = cmd.Enabled };
                break;
            case "task" when cmd.AgentId is not null && cmd.Task is not null:
                _pending.AddOrUpdate(cmd.AgentId,
                    _ => new List<AgentTask> { cmd.Task },
                    (_, list) => { lock (list) { list.Add(cmd.Task); } return list; });
                break;
            case "revoke" when cmd.AgentId is not null:
                if (_agents.TryGetValue(cmd.AgentId, out var a)) a.Revoked = true;
                break;
            case "remove" when cmd.AgentId is not null:
                _agents.TryRemove(cmd.AgentId, out _);
                _pending.TryRemove(cmd.AgentId, out _);
                break;
        }
    }

    // ---------------------------------------------------------------- perzisztencia

    private sealed class Persist
    {
        public List<EnrolledAgent> Agents { get; set; } = new();
        public List<TokenInfo> Tokens { get; set; } = new();
        public Dictionary<string, List<AgentTask>> Pending { get; set; } = new();
    }

    private int _saveScheduled;

    /// <summary>Kesleltetett mentes: a kovetkezo 5 mp-ben erkezo valtozasok egyetlen irassal mennek ki.</summary>
    private void SaveSoon()
    {
        if (!IsOwner || Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => { Interlocked.Exchange(ref _saveScheduled, 0); Save(); });
    }

    private void Save()
    {
        if (!IsOwner) return;   // kliens soha nem irja a fleet.json-t

        lock (_lock)
        {
            try
            {
                var p = new Persist
                {
                    Agents = _agents.Values.ToList(),
                    Tokens = _tokens.Values.ToList(),
                    Pending = _pending.ToDictionary(kv => kv.Key, kv => { lock (kv.Value) return kv.Value.ToList(); })
                };
                var tmp = Path.Combine(_dir, "fleet.json.tmp");
                File.WriteAllText(tmp, JsonSerializer.Serialize(p, ProtocolJson.Options));
                File.Move(tmp, Path.Combine(_dir, "fleet.json"), overwrite: true);
            }
            catch { }
        }
    }

    private void Load()
    {
        try
        {
            var path = Path.Combine(_dir, "fleet.json");
            if (!File.Exists(path)) return;
            var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(path), ProtocolJson.Options);
            if (p is null) return;

            foreach (var a in p.Agents) _agents[a.AgentId] = a;
            foreach (var t in p.Tokens) _tokens[t.Fingerprint] = t;
            foreach (var (id, tasks) in p.Pending ?? new()) _pending[id] = tasks;
        }
        catch { }
    }
}
