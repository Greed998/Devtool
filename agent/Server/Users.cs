using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Szerepkorok. A szamertek stabil (JSON-ban tarolodik) - uj szint csak a vegere.
/// Novekvo jogosultsag: minel nagyobb, annal tobbet szabad.
/// </summary>
public enum Role
{
    Megfigyelo = 0,   // csak nez
    Kezelo = 1,       // frissitest indithat, gyakorisagot allithat
    Admin = 2         // mindent, plusz felhasznalokat kezel
}

public sealed class UserAccount
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int Iterations { get; set; } = 210_000;
    public Role Role { get; set; } = Role.Megfigyelo;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool Disabled { get; set; }

    /// <summary>
    /// Csak a Kozponton: mely telephelyeket lathatja. Ures = MINDET. A FleetServeren
    /// nincs jelentosege (ott egy telephely van).
    /// </summary>
    public List<string> SiteScope { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore] public bool AllSites => SiteScope.Count == 0;
    public bool CanSeeSite(string siteId) => AllSites || SiteScope.Contains(siteId);

    public void SetPassword(string pw)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        PasswordSalt = Convert.ToBase64String(salt);
        PasswordHash = Convert.ToBase64String(
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pw), salt, Iterations, HashAlgorithmName.SHA256, 32));
    }

    public bool Verify(string pw)
    {
        if (PasswordHash.Length == 0 || Disabled) return false;
        try
        {
            var salt = Convert.FromBase64String(PasswordSalt);
            var expected = Convert.FromBase64String(PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pw), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }
}

/// <summary>
/// Felhasznalotar. Kulon JSON, hogy a config atirasa ne erintse. Az elso felhasznalot
/// (Admin) a setup hozza letre a meglevo szuperadmin jelszobol.
///
/// Szalbiztossag: a webkiszolgalo parhuzamos - minden muvelet lock alatt van.
/// </summary>
public sealed class UserStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<UserAccount> _users = new();

    public UserStore(string dir)
    {
        _path = Path.Combine(dir, "users.json");
        Load();
    }

    public bool IsEmpty { get { lock (_lock) return _users.Count == 0; } }

    public IReadOnlyList<UserAccount> All { get { lock (_lock) return _users.ToList(); } }

    public UserAccount? Find(string username)
    {
        lock (_lock)
            return _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bejelentkezes: sikeres eseten a felhasznalot adja vissza, kulonben null.</summary>
    public UserAccount? Authenticate(string username, string password)
    {
        var u = Find(username);
        // Idozites-tamadas ellen: akkor is hashelunk, ha a user nem letezik.
        if (u is null) { _ = new UserAccount().Verify(password); return null; }
        return u.Verify(password) ? u : null;
    }

    public (bool Ok, string Error) Create(string username, string password, Role role)
    {
        username = (username ?? "").Trim();
        if (username.Length < 3) return (false, "A felhasználónév legalább 3 karakter.");
        if (Find(username) is not null) return (false, "Ez a felhasználónév már létezik.");
        var st = ServerConfig.CheckPasswordStrength(password);
        if (!st.Ok) return (false, st.Message);

        var acc = new UserAccount { Username = username, Role = role };
        acc.SetPassword(password);
        lock (_lock) { _users.Add(acc); Save(); }
        return (true, "");
    }

    public (bool Ok, string Error) SetScope(string username, List<string> scope)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return (false, "Nincs ilyen felhasználó.");
            u.SiteScope = scope ?? new();
            Save();
        }
        return (true, "");
    }

    public (bool Ok, string Error) SetRole(string username, Role role)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return (false, "Nincs ilyen felhasználó.");
            // Az utolso admin ne veszitse el az admin jogot - kulonben senki nem kezel.
            if (u.Role == Role.Admin && role != Role.Admin && _users.Count(x => x.Role == Role.Admin && !x.Disabled) <= 1)
                return (false, "Ez az utolsó adminisztrátor – a jogát nem lehet elvenni.");
            u.Role = role; Save();
            return (true, "");
        }
    }

    public (bool Ok, string Error) SetPassword(string username, string password)
    {
        var st = ServerConfig.CheckPasswordStrength(password);
        if (!st.Ok) return (false, st.Message);
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return (false, "Nincs ilyen felhasználó.");
            u.SetPassword(password); Save();
            return (true, "");
        }
    }

    public (bool Ok, string Error) SetDisabled(string username, bool disabled)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return (false, "Nincs ilyen felhasználó.");
            if (disabled && u.Role == Role.Admin && _users.Count(x => x.Role == Role.Admin && !x.Disabled) <= 1)
                return (false, "Ez az utolsó aktív adminisztrátor – nem tiltható le.");
            u.Disabled = disabled; Save();
            return (true, "");
        }
    }

    public (bool Ok, string Error) Delete(string username)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (u is null) return (false, "Nincs ilyen felhasználó.");
            if (u.Role == Role.Admin && _users.Count(x => x.Role == Role.Admin && !x.Disabled) <= 1)
                return (false, "Ez az utolsó adminisztrátor – nem törölhető.");
            _users.Remove(u); Save();
            return (true, "");
        }
    }

    /// <summary>A setup hivja: az elso admin letrehozasa a megadott jelszobol.</summary>
    public void EnsureFirstAdmin(string username, string password)
    {
        lock (_lock)
        {
            if (_users.Count > 0) return;
            var a = new UserAccount { Username = username, Role = Role.Admin };
            a.SetPassword(password);
            _users.Add(a);
            Save();
        }
    }

    // ---- perzisztencia
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_users, Opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }

    private void Load()
    {
        try { if (File.Exists(_path)) _users = JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(_path), Opts) ?? new(); }
        catch { _users = new(); }
    }
}

/// <summary>
/// Munkamenet szerepkorrel. A sutiben a felhasznalonev + szerepkor + lejarat van,
/// alairva - a szerepkort nem lehet a kliensen atirni (az alairas vedi).
/// </summary>
public sealed class RoleSession
{
    private readonly byte[] _key;
    private readonly TimeSpan _life = TimeSpan.FromHours(8);

    public RoleSession(string base64Key) => _key = Convert.FromBase64String(base64Key);

    public string Issue(string user, Role role)
    {
        var exp = DateTimeOffset.UtcNow.Add(_life).ToUnixTimeSeconds();
        var payload = $"{user}|{(int)role}|{exp}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{Sign(payload)}"));
    }

    public (string User, Role Role)? Validate(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cookie)).Split('|');
            if (parts.Length != 4) return null;
            var payload = $"{parts[0]}|{parts[1]}|{parts[2]}";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(payload)), Encoding.UTF8.GetBytes(parts[3]))) return null;
            if (!long.TryParse(parts[2], out var exp) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;
            if (!int.TryParse(parts[1], out var r)) return null;
            return (parts[0], (Role)r);
        }
        catch { return null; }
    }

    private string Sign(string data)
    {
        using var h = new HMACSHA256(_key);
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }
}
