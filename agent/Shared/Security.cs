using System.Collections.Concurrent;
using System.Text.Json;

namespace DevToolbox.Fleet;

// =====================================================================================
//  KULCSTÁR – verziózott, láncolt kulcscserével
// =====================================================================================

/// <summary>Egy kulcs az elozmenyben: mikor lett, mikor cserelodott le, miert (a kulcslista feluletnek).</summary>
public sealed class KeyHistoryItem
{
    public string Fingerprint { get; set; } = "";
    public string Algorithm { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? RetiredUtc { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>Egy lancszem: publikus kulcs + az ELOZO kulccsal keszult alairasa.</summary>
public sealed class KeyLink
{
    public string PublicKeyPem { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Az elozo kulcs alairasa ennek a kulcsnak a PEM-jen. Az elso lancszemnel ures.</summary>
    public string SignedByPrevious { get; set; } = "";
}

/// <summary>
/// A szerver/Kozpont kulcstara. A kulcscsere NEM tori el a mar regisztralt agenteket es
/// telephelyeket: az uj kulcsot a regi alairja ("atadas"), a kliens a nala tarolt kulcstol
/// vegighalad a lancon, es csak akkor fogadja el az uj kulcsot, ha minden lancszem
/// ellenorizheto. Regi privat kulcsot nem tarolunk - csak a publikus lancot.
///
/// Verziozott: egy kesobbi formatum-valtozas inditaskor automatikusan atalakul.
/// Az 1-es verzio (kulon .pem fajlok) automatikusan 2-esre migralodik.
/// </summary>
public sealed class KeyStore
{
    public const int CurrentVersion = 2;
    /// <summary>
    /// Ennyi átadás marad a láncban. 1.4.5-től hetente automatikus csere van, ezért 60 (kb. 14 hónap): eddig
    /// offline gép (régi telepítőcsomag) is ellenőrizhetően átáll; ennél régebben offline gépet újra kell regisztrálni.
    /// </summary>
    private const int MaxLinks = 60;

    public int Version { get; set; } = CurrentVersion;
    public List<KeyLink> Links { get; set; } = new();
    public string CurrentPrivateKeyPem { get; set; } = "";
    /// <summary>Minden valaha használt kulcs (csak ujjlenyomat, algoritmus, időpontok) - a felület kulcslistájához; a láncból kieső is.</summary>
    public List<KeyHistoryItem> History { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore] public KeyLink Current => Links[^1];
    [System.Text.Json.Serialization.JsonIgnore] public Crypto.KeyPair Pair =>
        new(Current.PublicKeyPem, CurrentPrivateKeyPem, Crypto.PublicKeyFingerprint(Current.PublicKeyPem));

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string PathFor(string dir, string prefix) => Path.Combine(dir, $"{prefix}-keystore.json");

    /// <summary>Betoltes; ha nincs, a regi .pem fajlokbol migral; ha azok sincsenek, uj kulcsot general.</summary>
    public static KeyStore LoadOrCreate(string dir, string prefix)
    {
        Directory.CreateDirectory(dir);
        var path = PathFor(dir, prefix);
        KeyStore? ks = null;

        if (File.Exists(path))
        {
            try { ks = JsonSerializer.Deserialize<KeyStore>(File.ReadAllText(path), Opts); } catch { }
        }

        var pub = Path.Combine(dir, $"{prefix}-public.pem");
        var priv = Path.Combine(dir, $"{prefix}-private.pem");

        if (ks is null && File.Exists(pub) && File.Exists(priv))
        {
            // 1 -> 2 migracio: a meglevo kulcs marad, csak a tarolas valtozik.
            ks = new KeyStore
            {
                Links = { new KeyLink { PublicKeyPem = File.ReadAllText(pub), CreatedUtc = File.GetCreationTimeUtc(pub) } },
                CurrentPrivateKeyPem = File.ReadAllText(priv)
            };
        }

        if (ks is null || ks.Links.Count == 0 || ks.CurrentPrivateKeyPem.Length == 0)
        {
            var k = Crypto.NewKeyPair();
            ks = new KeyStore { Links = { new KeyLink { PublicKeyPem = k.PublicKeyPem } }, CurrentPrivateKeyPem = k.PrivateKeyPem };
        }

        // Kesobbi verziok migracioi ide kerulnek (ks.Version < CurrentVersion).
        ks.Version = CurrentVersion;
        // Az előzmény 1.4.5-től van: addig a láncból pótoljuk (ami még benne van).
        if (ks.History.Count == 0)
            for (int i = 0; i < ks.Links.Count; i++)
                ks.History.Add(new KeyHistoryItem
                {
                    Fingerprint = Crypto.PublicKeyFingerprint(ks.Links[i].PublicKeyPem), Algorithm = Crypto.Algorithm(ks.Links[i].PublicKeyPem),
                    CreatedUtc = ks.Links[i].CreatedUtc, RetiredUtc = i + 1 < ks.Links.Count ? ks.Links[i + 1].CreatedUtc : null,
                    Reason = i == 0 ? "első kulcs" : "csere (1.4.5 előtt)"
                });
        ks.Save(dir, prefix);
        return ks;
    }

    public void Save(string dir, string prefix)
    {
        var path = PathFor(dir, prefix);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, path, overwrite: true);
        SecureFile(path);

        // Tukor a regi fajlnevekre: a DevToolbox Flotta fule es a regi eszkozok ezeket olvassak.
        File.WriteAllText(Path.Combine(dir, $"{prefix}-public.pem"), Current.PublicKeyPem);
        var privPath = Path.Combine(dir, $"{prefix}-private.pem");
        File.WriteAllText(privPath, CurrentPrivateKeyPem);
        SecureFile(privPath);
    }

    /// <summary>
    /// Kulcscsere: uj kulcspar, amit a jelenlegi kulcs alair. A regi privat kulcs torlodik.
    /// strong: P-384 (csak ha minden kliens ellenorizni tudja); reason: „kézi” / „automatikus (heti)” - az elozmenybe.
    /// </summary>
    public void Rotate(string dir, string prefix, bool strong = false, string reason = "kézi")
    {
        var k = Crypto.NewKeyPair(strong);
        var handover = Crypto.Sign(CurrentPrivateKeyPem, k.PublicKeyPem);
        var now = DateTime.UtcNow;
        Links.Add(new KeyLink { PublicKeyPem = k.PublicKeyPem, SignedByPrevious = handover, CreatedUtc = now });
        while (Links.Count > MaxLinks) Links.RemoveAt(0);
        CurrentPrivateKeyPem = k.PrivateKeyPem;
        if (History.Count > 0 && History[^1].RetiredUtc is null) History[^1].RetiredUtc = now;
        History.Add(new KeyHistoryItem { Fingerprint = k.Fingerprint, Algorithm = Crypto.Algorithm(k.PublicKeyPem), CreatedUtc = now, Reason = reason });
        if (History.Count > 500) History.RemoveRange(0, History.Count - 500);
        Save(dir, prefix);
    }

    /// <summary>
    /// Kliensoldali ellenorzes: a nala tarolt kulcstol eljut-e ellenorizheto lancon a
    /// szerver jelenlegi kulcsaig. Ha igen, az uj kulcs megbizhato.
    /// </summary>
    public static bool ChainReaches(string knownPub, IReadOnlyList<KeyLink>? chain, string targetPub)
    {
        if (Same(knownPub, targetPub)) return true;
        if (chain is null || chain.Count == 0) return false;

        var start = -1;
        for (int i = 0; i < chain.Count; i++)
            if (Same(chain[i].PublicKeyPem, knownPub)) { start = i; break; }
        if (start < 0) return false;

        var known = knownPub;
        for (int i = start + 1; i < chain.Count; i++)
        {
            if (!Crypto.Verify(known, chain[i].PublicKeyPem, chain[i].SignedByPrevious)) return false;
            known = chain[i].PublicKeyPem;
        }
        return Same(known, targetPub);
    }

    /// <summary>A lanc egy regi ujjlenyomatrol (pl. regi telepitocsomag) eljut-e a jelenlegi kulcsig.</summary>
    public static bool FingerprintReaches(string fingerprint, IReadOnlyList<KeyLink>? chain, string targetPub)
    {
        if (Crypto.FingerprintMatches(fingerprint, targetPub)) return true;
        var start = chain?.FirstOrDefault(l => Crypto.FingerprintMatches(fingerprint, l.PublicKeyPem));
        return start is not null && ChainReaches(start.PublicKeyPem, chain, targetPub);
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Replace("\r", "").Trim(), b.Replace("\r", "").Trim(), StringComparison.Ordinal);

    /// <summary>Csak a jelenlegi felhasznalo (a szolgaltatas: SYSTEM) es a rendszergazdak.</summary>
    public static void SecureFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(true, false);
            foreach (System.Security.AccessControl.FileSystemAccessRule r in
                     sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                sec.RemoveAccessRule(r);
            var sids = new List<System.Security.Principal.SecurityIdentifier>
            {
                new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            };
            // A letrehozo fiok is: elesben ez a SYSTEM (nem valtozik semmi), de a nem emelt joggal
            // futo folyamat (pl. teszt) igy vissza tudja olvasni a sajat kulcsat.
            var current = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (current is not null && !sids.Contains(current)) sids.Add(current);
            foreach (var sid in sids)
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch { }
    }
}

// =====================================================================================
//  ADATMENTÉS – frissítés, kulcscsere és törlés előtt
// =====================================================================================

public static class DataBackup
{
    private static readonly string[] Patterns =
        { "*-keystore.json", "*.pem", "*.json", "*.log" };

    /// <summary>A mentések mappája (1.5.4: a Beállításokban áthelyezhető); üres = az adatmappa backup\ almappája.</summary>
    public static string CustomDir { get; set; } = "";
    public static string Root(string dataDir) => CustomDir.Length > 0 ? CustomDir : Path.Combine(dataDir, "backup");

    /// <summary>Egy mentés-almappa neve: yyyyMMdd-HHmmss-ok (a Create így nevezi el).</summary>
    public static bool IsBackupDir(string? name) => name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{8}-\d{6}-");

    /// <summary>Kulcsok, beallitasok, felhasznalok, szabalyok mentese egy datumozott mappaba. Az utolso 10 marad.</summary>
    public static string? Create(string dataDir, string reason)
    {
        try
        {
            var root = Root(dataDir);
            var dest = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Safe(reason)}");
            Directory.CreateDirectory(dest);
            foreach (var pat in Patterns)
                foreach (var f in Directory.GetFiles(dataDir, pat))
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
            // A naplók (1.5.4-tól a naplómappában) - a forgatott régiek nélkül, mint eddig.
            var logs = LogFolder.Dir(dataDir);
            if (Directory.Exists(logs) && !LogFolder.Same(logs, dataDir))
                foreach (var f in Directory.GetFiles(logs, "*.log"))
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);

            // 1.5.12: csak a mentés-almappák (yyyyMMdd-HHmmss-…) közül törlünk – eddig a mappa MINDEN almappájából,
            // így ha a mentésmappában más is volt (pl. a mérési adatok napi fájljai), az is törlődhetett.
            foreach (var old in Directory.GetDirectories(root).Where(d => IsBackupDir(Path.GetFileName(d))).OrderByDescending(d => d).Skip(10))
                try { Directory.Delete(old, true); } catch { }
            return dest;
        }
        catch { return null; }
    }

    /// <summary>
    /// Verziovaltas eszlelese induláskor. Barmelyik frissitesi uton (webes feltoltes, install,
    /// kezi csere) elotte mentes keszul, igy egy hibas frissites utan visszaallithato.
    /// </summary>
    public static void OnStartup(string dataDir, string currentVersion)
    {
        try
        {
            var marker = Path.Combine(dataDir, "version.txt");
            var prev = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            if (prev != currentVersion)
            {
                if (prev.Length > 0) Create(dataDir, $"frissites-{prev}-{currentVersion}");
                File.WriteAllText(marker, currentVersion);
            }
        }
        catch { }
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());
}

// =====================================================================================
//  BELÉPÉS-KORLÁTOZÁS
// =====================================================================================

/// <summary>
/// Talalgatas elleni vedelem: kulcsonkent (IP-cim) 15 percen belul 5 hibas probalkozas
/// utan 15 percig nem enged. Minden tovabbi hibas kiserlet a zarolas alatt meghosszabbitja.
/// </summary>
public static class LoginThrottle
{
    private const int MaxFails = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lock = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, (int Fails, DateTime First, DateTime? LockedUntil)> State = new();

    /// <summary>Ha zarolva van, visszaadja a hatralevo idot.</summary>
    public static TimeSpan? Blocked(string key)
    {
        if (State.TryGetValue(key, out var s) && s.LockedUntil is { } u && u > DateTime.UtcNow)
            return u - DateTime.UtcNow;
        return null;
    }

    public static void Fail(string key)
    {
        var now = DateTime.UtcNow;
        State.AddOrUpdate(key,
            _ => (1, now, null),
            (_, s) =>
            {
                if (s.LockedUntil is { } u && u > now) return (s.Fails + 1, s.First, now + Lock);   // zarolas alatt: hosszabbit
                if (now - s.First > Window) return (1, now, null);
                var fails = s.Fails + 1;
                return (fails, s.First, fails >= MaxFails ? now + Lock : null);
            });
    }

    public static void Success(string key) => State.TryRemove(key, out _);
}

// =====================================================================================
//  MŰVELETI NAPLÓ
// =====================================================================================

/// <summary>A naplofajlok (muveleti, riasztas, szolgaltatas) legnagyobb merete forgatas elott - a beallitasokbol.</summary>
public static class LogLimits
{
    public static long MaxBytes { get; set; } = 2L * 1_048_576;
    // 1 MB = 1024 × 1024 bájt (a fájlkezelő szerint): a beállított naplóméret ott is ennyinek látszik.
    public static void SetMb(int mb) => MaxBytes = Math.Clamp(mb, 1, 100) * 1_048_576L;
}

/// <summary>
/// A naplófájlok helye (FleetServer és Központ; a felhasználó kérése, 1.5.4): alapból az adatmappa logs\ almappája,
/// a Beállításokban máshová tehető. Minden naplóíró innen kéri az útvonalat, és a Gate zárral ír – így áthelyezés
/// közben sem vész el, és nem kettőződik egy sor sem. Az 1.5.4 előtti, az adatmappa gyökerében levő naplók az
/// első induláskor ide költöznek.
/// </summary>
public static class LogFolder
{
    public static readonly object Gate = new();
    private static string _custom = "";

    /// <summary>Az ismert naplófájlok a forgatott párjukkal - ezek költöznek.</summary>
    public static readonly string[] Files =
        { "fleet-server.log", "fleet-server.log.old", "fleet-hub.log", "fleet-hub.log.old", "audit.log", "audit.log.1", "alerts.log", "alerts.log.1",
          "snmp.log", "snmp.log.1", "usb.log", "usb.log.1" };

    /// <summary>Az új (nem az adatmappán belüli) mappa jogainak szűkítése - a Fleet állítja be (NativeAcl), itt nincs Windows-hívás.</summary>
    public static Action<string>? SecureDir { get; set; }

    public static string Default(string dataDir) => Path.Combine(dataDir, "logs");
    public static string Dir(string dataDir) => _custom.Length > 0 ? _custom : Default(dataDir);
    public static string PathOf(string dataDir, string file) => Path.Combine(Dir(dataDir), file);

    /// <summary>
    /// A beállított hely átvétele (üres = alapértelmezés) és a naplók átköltöztetése a régi helyről és az adatmappa
    /// gyökeréből. Ha a cél már tartalmazza a naplót, a régi tartalom elé kerül (időrend). Hibánál a régi hely marad;
    /// visszaadja a hibát (null = rendben).
    /// </summary>
    public static string? Use(string dataDir, string configured)
    {
        lock (Gate)
        {
            var oldDir = Dir(dataDir);
            var want = configured.Trim();
            var newDir = want.Length > 0 ? want : Default(dataDir);
            try
            {
                Directory.CreateDirectory(newDir);
                if (!IsInside(newDir, dataDir)) SecureDir?.Invoke(newDir);
                foreach (var src in new[] { dataDir, Default(dataDir), oldDir }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (Same(src, newDir)) continue;
                    foreach (var f in Files)
                        if (File.Exists(Path.Combine(src, f))) MoveOrMerge(Path.Combine(src, f), Path.Combine(newDir, f));
                    // 1.5.7: a Központ telephelyenkénti naplói (telephelyek\<id>\*.log) is költöznek – az adatmappa gyökeréből nem.
                    if (!Same(src, dataDir))
                        foreach (var d in Dirs)
                            if (Directory.Exists(Path.Combine(src, d))) MoveDir(Path.Combine(src, d), Path.Combine(newDir, d));
                }
                _custom = want.Length > 0 && !Same(newDir, Default(dataDir)) ? newDir : "";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    /// <summary>Csak a hely beállítása, költöztetés nélkül (parancssori módok: telepítés, frissítés-csere - a futó
    /// szolgáltatás közben ír, ezért ott nem mozgatunk fájlt).</summary>
    public static void Point(string dataDir, string configured)
    {
        lock (Gate) _custom = configured.Trim().Length > 0 && !Same(configured.Trim(), Default(dataDir)) ? configured.Trim() : "";
    }

    /// <summary>A naplómappa almappái, amelyek a naplókkal együtt költöznek (a Központ telephelyenkénti naplói).</summary>
    public static readonly string[] Dirs = { "telephelyek" };

    /// <summary>Mappa áthelyezése; ha a cél létezik, fájlonként (MoveOrMerge), és a kiürült forrás törlődik.</summary>
    public static void MoveDir(string src, string dst)
    {
        if (!Directory.Exists(dst))
        {
            // Más meghajtóra a Directory.Move nem visz: akkor fájlonként (a File.Move igen).
            try { Directory.Move(src, dst); return; } catch (IOException) { Directory.CreateDirectory(dst); }
        }
        foreach (var f in Directory.GetFiles(src)) MoveOrMerge(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) MoveDir(d, Path.Combine(dst, Path.GetFileName(d)));
        try { Directory.Delete(src); } catch { }
    }

    /// <summary>Áthelyezés; ha a cél létezik, a forrás (régebbi) tartalma elé kerül.</summary>
    public static void MoveOrMerge(string src, string dst)
    {
        if (!File.Exists(dst)) { File.Move(src, dst); return; }
        var tmp = dst + ".merge";
        using (var o = File.Create(tmp))
        {
            using (var a = File.OpenRead(src)) a.CopyTo(o);
            using (var b = File.OpenRead(dst)) b.CopyTo(o);
        }
        File.Move(tmp, dst, overwrite: true);
        File.Delete(src);
    }

    /// <summary>Ugyanaz a mappa-e (teljes útvonalra bontva, kis-nagybetű nélkül). Hibás útvonalnál szövegként hasonlít.</summary>
    public static bool Same(string a, string b)
    {
        static string N(string p) { try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p.Trim().TrimEnd('\\', '/'); } }
        return a.Length > 0 && b.Length > 0 && string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A dir a root alatt van-e (a root maga nem számít).</summary>
    public static bool IsInside(string dir, string root)
    {
        try
        {
            var d = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return d.Length > r.Length && d.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>1.5.7: forgatáskor (a napló a „rotated” párjába került) – a Központba küldő ebből tudja, hol tart a fájlban.</summary>
    public static Action<string>? OnRotate { get; set; }

    /// <summary>Egy napló (és forgatott párja) írása a közös zár alatt; maxBytes felett a régi rotated-re nevezve.</summary>
    public static void Append(string dataDir, string file, string rotated, string text)
    {
        lock (Gate)
        {
            try
            {
                var dir = Dir(dataDir);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, file);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > LogLimits.MaxBytes)
                {
                    File.Move(path, Path.Combine(dir, rotated), true);
                    try { OnRotate?.Invoke(file); } catch { }
                }
                File.AppendAllText(path, text);
            }
            catch { }
        }
    }
}

/// <summary>Ki, mikor, mit csinalt. Soronkent egy bejegyzes, LogLimits.MaxBytes felett forgatva.</summary>
public static class AuditLog
{
    public static void Write(string dataDir, string user, string action, string detail = "")
    {
        // A naplómappa (LogFolder) a beállításokból jön; a közös zár védi az áthelyezés alatt is.
        detail = detail.Replace("\r", " ").Replace("\n", " ");
        LogFolder.Append(dataDir, "audit.log", "audit.log.1", $"{DateTime.Now:yyyy.MM.dd HH:mm:ss} | {user} | {action} | {detail}\n");
    }

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["/web/api/queue"] = "frissítés kiadva", ["/web/api/revoke"] = "visszavonás",
        ["/web/api/token"] = "telepítőcsomag / token készítése", ["/web/api/settings"] = "beállítás módosítása",
        ["/web/api/hub-settings"] = "Központ-kapcsolat módosítása", ["/web/api/monitor-save"] = "mérési beállítás módosítása",
        ["/web/api/alert-save"] = "riasztási küszöb mentése", ["/web/api/alert-delete"] = "riasztási küszöb törlése",
        ["/web/api/user-create"] = "felhasználó létrehozása", ["/web/api/user-role"] = "szerepkör módosítása",
        ["/web/api/user-password"] = "jelszó módosítása", ["/web/api/user-disable"] = "felhasználó tiltása/engedélyezése",
        ["/web/api/user-delete"] = "felhasználó törlése", ["/web/api/user-scope"] = "hatókör módosítása",
        ["/web/api/rotate-keys"] = "KULCSCSERE",
        ["/web/api/task"] = "feladat kiadva", ["/web/api/agent-groups"] = "gépcsoport módosítása",
        ["/web/api/catalog-save"] = "programkatalógus módosítása", ["/web/api/policy-save"] = "automatikus szabály mentése",
        ["/web/api/policy-delete"] = "automatikus szabály törlése", ["/web/api/interval"] = "betárcsázási gyakoriság módosítása",
        ["/web/api/offline-save"] = "kiesés-riasztás beállítása", ["/web/api/mail-save"] = "e-mail értesítés beállítása",
        ["/web/api/mail-test"] = "teszt e-mail küldése", ["/web/api/catalog-decide"] = "programtár-döntés (repó / elutasítás)",
        ["/web/api/winget-catalog-refresh"] = "winget-tár frissítése",
        ["/web/api/key-settings"] = "automatikus kulcscsere beállítása", ["/web/api/token-disable"] = "beléptető token letiltása", ["/web/api/token-delete"] = "token törlése",
        ["/web/api/agent-update"] = "agent frissítése most",
    };

    /// <summary>Webes muvelet naplozasa. A jelszo-mezok kimaradnak, a tartalom rovidul.</summary>
    public static void Api(string dataDir, string user, string path, string body)
    {
        if (!Labels.TryGetValue(path, out var label)) return;   // csak a modosito muveletek
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
            var parts = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject()
                    .Where(p => !p.Name.Contains("pass", StringComparison.OrdinalIgnoreCase))
                    .Select(p => $"{p.Name}={p.Value}")
                : Enumerable.Empty<string>();
            detail = string.Join(", ", parts);
        }
        catch { }
        if (detail.Length > 300) detail = detail[..300] + "…";
        Write(dataDir, user, label, detail);
    }

    public static string Tail(string dataDir, int lines)
    {
        try
        {
            var path = LogFolder.PathOf(dataDir, "audit.log");
            if (!File.Exists(path)) return "";
            var all = File.ReadAllLines(path);
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)).Reverse());
        }
        catch { return ""; }
    }
}
