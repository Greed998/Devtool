using System.Text.RegularExpressions;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Automatikus gépcsoportok (a felhasználó kérése, 1.5.4): a gép az operációs rendszere és a hardvere alapján magától
/// kerül csoportba – Szerverek / Kliensek, Windows-verzió (Windows 11, Windows Server 2022…), Virtuális / Fizikai gépek.
/// A tagságot nem tároljuk: mindig a gép legutóbbi adataiból számoljuk (a szerver és a Központ ugyanígy), ezért
/// kézzel nem módosítható. A kézi csoportokkal együtt ugyanúgy hatókör (szabályok, küszöbök, kiesés-figyelés, műveletek).
/// </summary>
public static class AutoGroups
{
    public const string Servers = "Szerverek", Clients = "Kliensek", Virtual = "Virtuális gépek", Physical = "Fizikai gépek";

    /// <summary>A gép besorolása. os: a Win32_OperatingSystem.Caption (pl. „Microsoft Windows 11 Pro”,
    /// „Microsoft Windows Server 2022 Standard”); isVirtual: null = nem ismert (régi telephely nem küldi).</summary>
    public static (string? Kind, string? Os, string? Hw) Classify(string? os, bool? isVirtual)
    {
        string? kind = null, ver = null;
        var s = (os ?? "").Replace("®", "").Replace("™", "").Trim();
        if (s.Length > 0)
        {
            var server = s.Contains("Server", StringComparison.OrdinalIgnoreCase);
            kind = server ? Servers : Clients;
            var m = Regex.Match(s, @"Windows\s+(?:(Server)\s+)?(\d{4}(?:\s*R2)?|\d+(?:\.\d)?|XP|Vista)\b", RegexOptions.IgnoreCase);
            if (m.Success)
                ver = "Windows " + (m.Groups[1].Success ? "Server " : "") + Regex.Replace(m.Groups[2].Value, @"\s*R2", " R2", RegexOptions.IgnoreCase).ToUpperInvariant().Replace("VISTA", "Vista");
        }
        return (kind, ver, isVirtual is bool v ? (v ? Virtual : Physical) : null);
    }

    /// <summary>A gép automatikus csoportjai.</summary>
    public static List<string> For(string? os, bool? isVirtual)
    {
        var (k, o, h) = Classify(os, isVirtual);
        return new[] { k, o, h }.Where(x => x is not null).Select(x => x!).ToList();
    }

    /// <summary>Automatikus csoportnév-e (ilyen nevű kézi csoport nem hozható létre, amíg az automatikus csoportok be vannak kapcsolva).</summary>
    public static bool IsAutoName(string name)
    {
        var n = name.Trim();
        return n.Equals(Servers, StringComparison.OrdinalIgnoreCase) || n.Equals(Clients, StringComparison.OrdinalIgnoreCase)
            || n.Equals(Virtual, StringComparison.OrdinalIgnoreCase) || n.Equals(Physical, StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(n, @"^Windows\s+(Server\s+)?(\d{4}(\s*R2)?|\d+(\.\d)?|XP|Vista)$", RegexOptions.IgnoreCase);
    }

    /// <summary>A gép tényleges csoportjai: a kézi csoportok és (ha be vannak kapcsolva) az automatikusak.</summary>
    public static List<string> Effective(IEnumerable<string> manual, string? os, bool? isVirtual, bool enabled) =>
        (enabled ? manual.Concat(For(os, isVirtual)) : manual).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Gépcsoportok a telephelyen. A tagság a gépek tulajdonsága (EnrolledAgent.Groups); a létrehozott, akár üres csoportok
/// listája a konfigurációban (ServerConfig.Groups). Ugyanezt hívja a webes Csoportok oldal és a Központból érkező
/// csoport-művelet (HubGroupOp) is. Átnevezéskor / törléskor a csoportra hivatkozó beállítások (automatikus szabályok,
/// kiesés-figyelés, küszöb-szabályok) is követik a változást.
/// </summary>
public static class GroupOps
{
    public const int MaxName = 40;

    public static string Clean(string? name) => (name ?? "").Trim();

    public static bool ValidName(string name) =>
        name.Length is > 0 and <= MaxName && !name.Any(char.IsControl) && !name.Contains(',');

    /// <summary>A kézi csoportok: a létrehozottak és a gépeken szereplők, ábécérendben.</summary>
    public static List<string> Manual(FleetStore store, ServerConfig cfg) =>
        cfg.Groups.Concat(store.Agents.Where(a => !a.Revoked).SelectMany(a => a.Groups))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Az automatikus csoportok (amelyekben van gép), a kézi csoportokkal azonos nevűek nélkül.</summary>
    public static List<string> Auto(FleetStore store, ServerConfig cfg)
    {
        if (!cfg.AutoGroupsOn) return new();
        var manual = Manual(store, cfg).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return store.Agents.Where(a => !a.Revoked).SelectMany(a => AutoGroups.For(OsOf(a), a.LastStatus?.IsVirtual))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(g => !manual.Contains(g)).OrderBy(AutoOrder).ThenBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Az automatikus csoportok sorrendje a listákban: típus, Windows-verzió, hardver.</summary>
    public static int AutoOrder(string g) => g is AutoGroups.Servers or AutoGroups.Clients ? 0 : g is AutoGroups.Virtual or AutoGroups.Physical ? 2 : 1;

    /// <summary>Minden csoport (a választólistákhoz: szabály, küszöb, kiesés-figyelés): a kézi és az automatikus.</summary>
    public static List<string> All(FleetStore store, ServerConfig cfg) => Manual(store, cfg).Concat(Auto(store, cfg)).ToList();

    public static string OsOf(EnrolledAgent a) => a.LastStatus?.OperatingSystem is { Length: > 0 } os ? os : a.OperatingSystem;

    /// <summary>A gép tényleges csoportjai (kézi + automatikus) - minden hatókör-vizsgálat ezt használja.</summary>
    public static List<string> Of(EnrolledAgent a, ServerConfig cfg) =>
        AutoGroups.Effective(a.Groups, OsOf(a), a.LastStatus?.IsVirtual, cfg.AutoGroupsOn);

    /// <summary>
    /// Egy művelet végrehajtása. op: create / delete / rename (newName) / members (a csoport tagjai pontosan agentIds).
    /// Visszaadja a hibát (null = siker). A konfigurációt a hívó menti, ha changedConfig igaz.
    /// </summary>
    public static string? Apply(FleetStore store, ServerConfig cfg, AlertStore? alerts, string op, string group, string newName,
        IReadOnlyCollection<string>? agentIds, out bool changedConfig)
    {
        changedConfig = false;
        group = Clean(group);
        bool Is(string g) => g.Equals(group, StringComparison.OrdinalIgnoreCase);
        // Az automatikus csoport tagságát az operációs rendszer adja: kézzel nem hozható létre, nem nevezhető át, nem tölthető.
        // (A régi, azonos nevű kézi csoport kezelhető marad.)
        if (cfg.AutoGroupsOn && op is "create" or "members" or "rename" or "delete" && !Manual(store, cfg).Any(Is) && AutoGroups.IsAutoName(group))
            return "Ez automatikus csoport (az operációs rendszer szerint) – a tagsága magától alakul, kézzel nem módosítható.";
        if (cfg.AutoGroupsOn && op == "rename" && AutoGroups.IsAutoName(Clean(newName)))
            return "Ez a név az automatikus csoportoké – válassz másikat.";
        switch (op)
        {
            case "create":
                if (!ValidName(group)) return "A csoport neve 1–40 karakter lehet, vessző nélkül.";
                if (!All(store, cfg).Any(Is)) { cfg.Groups.Add(group); changedConfig = true; }
                return null;

            case "delete":
                if (group.Length == 0) return "Hiányzó csoportnév.";
                foreach (var a in store.Agents.Where(a => a.Groups.Any(Is)))
                    store.SetGroups(a.AgentId, a.Groups.Where(g => !Is(g)));
                changedConfig |= cfg.Groups.RemoveAll(Is) > 0;
                // A csoportra szűkített beállítások: a szabály nem tágulhat észrevétlenül „minden gép”-re, ezért kikapcsoljuk.
                foreach (var p in cfg.AutoPolicies.Where(p => Is(p.Group))) { p.Enabled = false; changedConfig = true; }
                changedConfig |= cfg.OfflineAlertGroups.RemoveAll(Is) > 0;
                if (alerts is not null)
                    foreach (var r in alerts.Rules.Where(r => Is(r.Group)).ToList()) { r.Enabled = false; alerts.Update(r); }
                return null;

            case "rename":
            {
                var nn = Clean(newName);
                if (!ValidName(nn)) return "Az új név 1–40 karakter lehet, vessző nélkül.";
                if (group.Length == 0) return "Hiányzó csoportnév.";
                if (!nn.Equals(group, StringComparison.OrdinalIgnoreCase) && All(store, cfg).Any(g => g.Equals(nn, StringComparison.OrdinalIgnoreCase)))
                    return "Már van ilyen nevű csoport.";
                foreach (var a in store.Agents.Where(a => a.Groups.Any(Is)))
                    store.SetGroups(a.AgentId, a.Groups.Select(g => Is(g) ? nn : g));
                for (int i = 0; i < cfg.Groups.Count; i++) if (Is(cfg.Groups[i])) cfg.Groups[i] = nn;
                foreach (var p in cfg.AutoPolicies.Where(p => Is(p.Group))) p.Group = nn;
                for (int i = 0; i < cfg.OfflineAlertGroups.Count; i++) if (Is(cfg.OfflineAlertGroups[i])) cfg.OfflineAlertGroups[i] = nn;
                if (alerts is not null)
                    foreach (var r in alerts.Rules.Where(r => Is(r.Group)).ToList()) { r.Group = nn; alerts.Update(r); }
                changedConfig = true;
                return null;
            }

            case "members":
            {
                if (!ValidName(group)) return "A csoport neve 1–40 karakter lehet, vessző nélkül.";
                var want = (agentIds ?? Array.Empty<string>()).ToHashSet();
                foreach (var a in store.Agents)
                {
                    var has = a.Groups.Any(Is);
                    if (want.Contains(a.AgentId) && !has) store.SetGroups(a.AgentId, a.Groups.Append(group));
                    else if (!want.Contains(a.AgentId) && has) store.SetGroups(a.AgentId, a.Groups.Where(g => !Is(g)));
                }
                // A csoport akkor is megmarad, ha kiürült (később újra feltölthető).
                if (!cfg.Groups.Any(Is)) { cfg.Groups.Add(group); changedConfig = true; }
                return null;
            }
            default: return "Ismeretlen csoport-művelet.";
        }
    }
}
