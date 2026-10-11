using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A gépek tálca-felületének kérései (1.5.7, a felhasználó kérése és döntései). Az agent a named pipe-on kapja a
/// bejelentkezett felhasználó kérését, és a saját kulcsával aláírva küldi ide (POST /api/user); a válasz a szerver
/// kulcsával aláírt, a kérés nonce-ához kötött. Sima felhasználó: a repó programjai azonnal telepíthetők, más program
/// kérhető (jóváhagyási sor: ProgramRequests). „Belépés adminként”: kihívás–válasz – a jelszó nem megy át a hálón
/// (TrayLogin), a belépés 15 percig érvényes, csak erre a gépre, kezelői / admin joggal; a hibás próbálkozás korlátozva.
/// </summary>
public static class TrayApi
{
    public static UserStore? Users { get; set; }
    public static string SessionKey { get; set; } = "";
    /// <summary>A teljes winget-tár (a szerver állítja be) – a „Program kérése” keresője és az azonosítók ellenőrzése.</summary>
    public static WingetCatalog? Winget { get; set; }

    private static readonly ConcurrentDictionary<string, (string AgentId, string User, DateTime ExpUtc)> Challenges = new();
    private static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(15);

    public static AgentUserResponse Handle(FleetStore store, ServerConfig cfg, AgentUserRequest req)
    {
        bool ok; string msg, data;
        try { (ok, msg, data) = Process(store, cfg, req); }
        catch (Exception ex) { (ok, msg, data) = (false, "Szerverhiba: " + ex.Message, ""); }
        var body = new AgentUserReplyBody { AgentId = req.AgentId, RequestNonce = req.Nonce, Ok = ok, Message = msg, Data = data };
        var payload = ProtocolJson.Serialize(body);
        return new AgentUserResponse
        {
            SignedPayload = payload, Signature = Crypto.Sign(store.ServerKeys.PrivateKeyPem, payload),
            SignerPublicKeyPem = store.ServerKeys.PublicKeyPem, KeyChain = store.KeyChain
        };
    }

    private static (bool, string, string) Fail(string msg) => (false, msg, "");
    private static (bool, string, string) Ok(object data, string msg = "") => (true, msg, ProtocolJson.Serialize(data));

    private sealed class Req
    {
        public string Q { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Username { get; set; } = "";
        public string Challenge { get; set; } = "";
        public string Proof { get; set; } = "";
        public string Token { get; set; } = "";
        public bool Approve { get; set; }
        public string Note { get; set; } = "";
    }

    private static (bool, string, string) Process(FleetStore store, ServerConfig cfg, AgentUserRequest req)
    {
        if (req.Data.Length > 16_384) return Fail("Túl nagy kérés.");
        if (store.VerifyAgentRequest(req.AgentId, req.CanonicalPayload(), req.Signature, req.Timestamp, req.Nonce) is { } verr) return Fail(verr);
        if (!cfg.TrayEnabled) return Fail("A tálca-felület ezen a telephelyen ki van kapcsolva.");
        var agent = store.GetAgent(req.AgentId)!;
        var user = Clean(req.User, 120);
        Req r;
        try { r = req.Data.Length > 0 ? JsonSerializer.Deserialize<Req>(req.Data, ProtocolJson.Options) ?? new() : new(); }
        catch { return Fail("Hibás kérés."); }

        switch (req.Kind)
        {
            case "catalog":
            {
                var installed = (agent.LastStatus?.Installed ?? new()).Select(i => i.Id).Where(i => i.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Ok(new
                {
                    items = Repo(cfg).Select(c => new { id = c.Id, name = c.Name, category = CategoryOf(c), installed = installed.Contains(c.Id) }),
                    machine = agent.MachineName
                });
            }
            case "search":
            {
                if (Winget is not { } w || w.Count == 0) return Fail("A winget-tár még nincs letöltve a szerveren – próbáld később.");
                var repo = Repo(cfg).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rejected = cfg.CatalogRejected.Concat(cfg.HubRejected).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Ok(new
                {
                    items = w.Search(Clean(r.Q, 100), 60).Where(p => !rejected.Contains(p.Id))
                        .Select(p => new { id = p.Id, name = p.Name, version = p.Version, category = p.Category, inRepo = repo.Contains(p.Id) })
                });
            }
            case "install":
            {
                // Sima felhasználó: csak a repóban levő (jóváhagyott) program, azonnal.
                var item = Repo(cfg).FirstOrDefault(c => c.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
                if (item is null) return Fail("Ez a program nincs a jóváhagyott programok között – kérni lehet (Program kérése).");
                var (pr, err) = ProgramRequests.Add(store, agent.AgentId, user, item.Id, item.Name, "", autoApprove: true);
                return err is not null ? Fail(err) : Ok(View(pr!), "A telepítés elindult – a gép a következő jelentkezéskor (néhány percen belül) telepíti.");
            }
            case "request":
            {
                if (!WingetId.IsValid(r.Id)) return Fail("Érvénytelen programazonosító.");
                if (cfg.CatalogRejected.Concat(cfg.HubRejected).Any(c => c.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase)))
                    return Fail("Ezt a programot a rendszergazda letiltotta (elutasított programok).");
                var pkg = Winget?.Find(r.Id);
                if (Winget is { Count: > 0 } && pkg is null) return Fail("Ez a program nincs a winget-tárban.");
                var inRepo = Repo(cfg).FirstOrDefault(c => c.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
                var (pr, err) = ProgramRequests.Add(store, agent.AgentId, user, pkg?.Id ?? r.Id, pkg?.Name ?? Clean(r.Name, 200), Clean(r.Reason, 500), autoApprove: inRepo is not null);
                return err is not null ? Fail(err) : Ok(View(pr!), inRepo is not null ? "A program jóváhagyott – a telepítés elindult." : "A kérést elküldtük, a rendszergazda dönt róla.");
            }
            case "requests":
                return Ok(new { items = ProgramRequests.List(agent.AgentId).Take(40).Select(View) });

            case "login-start":
            {
                var name = Clean(r.Username, 100);
                if (name.Length < 3) return Fail("Add meg a felhasználónevet.");
                if (LoginThrottle.Blocked("tray|" + agent.AgentId) is not null || LoginThrottle.Blocked("tray-user|" + name.ToLowerInvariant()) is not null)
                    return Fail("Túl sok sikertelen próbálkozás – próbáld később.");
                var u = Users?.Find(name);
                // Nem létező felhasználónál is ugyanúgy válaszolunk (álsó): a felhasználónevek nem kérdezhetők ki.
                var salt = u is { PasswordSalt.Length: > 0 } ? u.PasswordSalt : Convert.ToBase64String(Hmac("salt|" + name.ToLowerInvariant())[..16]);
                var iter = u?.Iterations ?? 210_000;
                var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                foreach (var kv in Challenges) if (kv.Value.ExpUtc < DateTime.UtcNow) Challenges.TryRemove(kv.Key, out _);
                Challenges[challenge] = (agent.AgentId, name.ToLowerInvariant(), DateTime.UtcNow.AddMinutes(2));
                return Ok(new { salt, iterations = iter, challenge, agentId = agent.AgentId });
            }
            case "login":
            {
                var name = Clean(r.Username, 100);
                var keyA = "tray|" + agent.AgentId; var keyU = "tray-user|" + name.ToLowerInvariant();
                if (LoginThrottle.Blocked(keyA) is not null || LoginThrottle.Blocked(keyU) is not null) return Fail("Túl sok sikertelen próbálkozás – próbáld később.");
                // A kihívás egyszer használható, 2 percig, csak ehhez a géphez és felhasználóhoz.
                if (!Challenges.TryRemove(r.Challenge, out var ch) || ch.ExpUtc < DateTime.UtcNow || ch.AgentId != agent.AgentId || ch.User != name.ToLowerInvariant())
                    return Fail("A belépés lejárt – próbáld újra.");
                var u = Users?.Find(name);
                var good = false;
                if (u is { Disabled: false, PasswordHash.Length: > 0 })
                {
                    try
                    {
                        var expected = Encoding.UTF8.GetBytes(TrayLogin.Proof(Convert.FromBase64String(u.PasswordHash), agent.AgentId, name, r.Challenge));
                        good = CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(r.Proof ?? ""));
                    }
                    catch { good = false; }
                }
                if (!good) { LoginThrottle.Fail(keyA); LoginThrottle.Fail(keyU); AuditLog.Write(ServerPaths.DataDir, name, "sikertelen belépés a tálcán", agent.MachineName + (user.Length > 0 ? " · " + user : "")); return Fail("Hibás felhasználónév vagy jelszó."); }
                LoginThrottle.Success(keyA); LoginThrottle.Success(keyU);
                if (u!.Role < Role.Kezelo) return Fail("Ehhez kezelői vagy admin jog kell.");
                AuditLog.Write(ServerPaths.DataDir, u.Username, "belépés a tálcán", agent.MachineName + (user.Length > 0 ? " · Windows: " + user : ""));
                return Ok(new { token = Issue(agent.AgentId, u.Username, u.Role), user = u.Username, role = (int)u.Role, minutes = (int)TokenLife.TotalMinutes });
            }
            case "admin-install":
            case "admin-requests":
            case "admin-decide":
            {
                if (Validate(r.Token, agent.AgentId) is not { } adm) return Fail("A belépés lejárt – lépj be újra.");
                if (req.Kind == "admin-requests")
                    return Ok(new { items = ProgramRequests.List(agent.AgentId).Where(i => i.State == "pending").Select(View) });
                if (req.Kind == "admin-decide")
                {
                    if (!ProgramRequests.List(agent.AgentId).Any(i => i.Id == r.Id)) return Fail("Nincs ilyen kérés ezen a gépen.");
                    var err = ProgramRequests.Decide(store, r.Id, r.Approve, adm.User + " (tálca)", Clean(r.Note, 500));
                    return err is not null ? Fail(err) : Ok(new { }, r.Approve ? "Jóváhagyva – a telepítés elindult." : "Elutasítva.");
                }
                if (!WingetId.IsValid(r.Id)) return Fail("Érvénytelen programazonosító.");
                var pkg = Winget?.Find(r.Id);
                var (pr, e2) = ProgramRequests.Add(store, agent.AgentId, user, pkg?.Id ?? r.Id, pkg?.Name ?? Clean(r.Name, 200), "", autoApprove: true, approvedBy: adm.User + " (tálca)");
                return e2 is not null ? Fail(e2) : Ok(View(pr!), "A telepítés elindult.");
            }
            default:
                return Fail("Ismeretlen kérés: " + Clean(req.Kind, 40));
        }
    }

    /// <summary>A telephely jóváhagyott programjai: a helyi repó és a Központé (az elutasítottak nélkül).</summary>
    private static List<CatalogItem> Repo(ServerConfig cfg)
    {
        var rejected = cfg.CatalogRejected.Concat(cfg.HubRejected).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return cfg.SoftwareCatalog.Concat(cfg.HubCatalogItems).Where(c => WingetId.IsValid(c.Id) && !rejected.Contains(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string CategoryOf(CatalogItem c) =>
        c.Category.Length > 0 ? c.Category : Winget?.Find(c.Id)?.Category is { Length: > 0 } k ? k : "Egyéb";

    private static object View(ProgramRequestInfo i) => new
    {
        id = i.Id, packageId = i.PackageId, name = i.Name, state = i.State, user = i.User, reason = i.Reason, note = i.Note,
        created = i.CreatedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm"), decided = i.DecidedUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm"), decidedBy = i.DecidedBy
    };

    private static byte[] Hmac(string s)
    {
        using var h = new HMACSHA256(Convert.FromBase64String(SessionKey.Length > 0 ? SessionKey : Convert.ToBase64String(new byte[32])));
        return h.ComputeHash(Encoding.UTF8.GetBytes(s));
    }

    /// <summary>A tálcás admin-belépés: gépre kötött, 15 percig érvényes, aláírt jegy (a szerver kulcsával HMAC-olva).</summary>
    private static string Issue(string agentId, string user, Role role)
    {
        var exp = DateTimeOffset.UtcNow.Add(TokenLife).ToUnixTimeSeconds();
        var payload = $"tray|{agentId}|{user}|{(int)role}|{exp}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload + "|" + Convert.ToBase64String(Hmac(payload))));
    }

    private static (string User, Role Role)? Validate(string token, string agentId)
    {
        try
        {
            var p = Encoding.UTF8.GetString(Convert.FromBase64String(token ?? "")).Split('|');
            if (p.Length != 6 || p[0] != "tray" || p[1] != agentId) return null;
            var payload = string.Join('|', p[..5]);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Convert.ToBase64String(Hmac(payload))), Encoding.UTF8.GetBytes(p[5]))) return null;
            if (!long.TryParse(p[4], out var exp) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;
            // A jog a belépés óta megváltozhatott (letiltották, jogot vettek el): mindig a mostani számít.
            var u = Users?.Find(p[2]);
            if (u is null || u.Disabled || u.Role < Role.Kezelo) return null;
            return (u.Username, u.Role);
        }
        catch { return null; }
    }

    private static string Clean(string? s, int max)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }
}
