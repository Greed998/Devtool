namespace DevToolbox.Fleet.Server;

/// <summary>
/// Eszköz-összegzés (1.5.6, a felhasználó kérése): mennyire biztonságos és mennyire van jó állapotban egy gép – két
/// részpontszám (Biztonság, Állapot; 0–100) és az átlaguk, tételes indoklással (mi vonta le a pontot). A szerver és a
/// Központ ugyanezzel számol, a gép legutóbbi adataiból és méréseiből (a Központban az 5 perces átlagokból).
/// A levonások szándékosan egyszerűek és átláthatók: a felületen minden tétel látszik.
/// </summary>
public static class DeviceScore
{
    public sealed record Input(bool Online, DateTime? LastSeenUtc, int AppUpdates, int WuImportant, int WuOther, bool PendingReboot,
        bool AgentOutdated, int ActiveAlerts, IReadOnlyDictionary<string, double> Latest,
        int CveCritical = 0, int CveHigh = 0, int CveExploited = 0);

    /// <summary>Area: Biztonság | Állapot; Level: err | warn.</summary>
    public sealed record Item(string Area, string Text, int Penalty, string Level);

    public sealed record Result(int Score, int Security, int Health, string Grade, List<Item> Items);

    public static string GradeOf(int s) => s >= 85 ? "jó" : s >= 65 ? "közepes" : s >= 40 ? "gyenge" : "rossz";

    public static Result Compute(Input i)
    {
        var items = new List<Item>();
        void S(int p, string t, string lvl = "warn") => items.Add(new Item("Biztonság", t, p, lvl));
        void H(int p, string t, string lvl = "warn") => items.Add(new Item("Állapot", t, p, lvl));
        double? V(string k) => i.Latest.TryGetValue(k, out var v) ? v : null;
        List<double> All(string prefix, string suffix) =>
            i.Latest.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.EndsWith(suffix, StringComparison.Ordinal)).Select(kv => kv.Value).ToList();

        // ---- Biztonság
        if (i.WuImportant > 0) S(25, $"{i.WuImportant} fontos / kritikus Windows-frissítés vár", "err");
        else if (i.WuOther > 0) S(8, $"{i.WuOther} Windows-frissítés vár");
        if (i.AppUpdates > 0) S(Math.Min(15, i.AppUpdates * 2), $"{i.AppUpdates} program frissíthető");
        if (V("sec.av.enabled") == 0) S(30, "A vírusvédelem ki van kapcsolva", "err");
        else if (V("sec.av.realtime") == 0) S(20, "A valós idejű vírusvédelem ki van kapcsolva", "err");
        if (V("sec.av.sigage") is double sa && sa > 7) S(sa > 30 ? 15 : 8, $"A vírusdefiníciók {sa:0} naposak");
        if (V("sec.fw.offprofiles") is double fw && fw > 0) S(15, $"{fw:0} tűzfal-profil ki van kapcsolva", "err");
        if (V("sec.bitlocker.sys") == 0) S(8, "A rendszermeghajtó nincs titkosítva (BitLocker)");
        if (V("sec.failmaxsrc1h") is double fm && fm >= 20) S(15, $"Jelszó-próbálkozás egy forrásból: {fm:0} az elmúlt órában", "err");
        else if (V("sec.logonfail1h") is double lf && lf >= 10) S(5, $"{lf:0} sikertelen belépés az elmúlt órában");
        if (V("sec.lockout1h") is double lo && lo > 0) S(5, $"{lo:0} fiókzárolás az elmúlt órában");
        if (i.CveExploited > 0) S(30, $"{i.CveExploited} ténylegesen kihasznált sérülékenység (CISA KEV)", "err");
        if (i.CveCritical > 0) S(Math.Min(25, i.CveCritical * 5), $"{i.CveCritical} kritikus CVE", "err");
        if (i.CveHigh > 0) S(Math.Min(15, i.CveHigh * 2), $"{i.CveHigh} magas súlyosságú CVE");
        if (i.AgentOutdated) S(5, "Az agent régebbi a szerverbe épített verziónál");

        // ---- Állapot
        if (!i.Online)
            H(i.LastSeenUtc is { } ls && DateTime.UtcNow - ls > TimeSpan.FromDays(1) ? 25 : 10,
              "A gép nem jelentkezik" + (i.LastSeenUtc is { } l2 ? $" ({l2.ToLocalTime():MM.dd HH:mm} óta)" : ""), "err");
        var used = All("disk.", ".usedpct");
        if (used.Count > 0 && used.Max() is var um && um >= 90) H(um >= 95 ? 25 : 12, $"Majdnem tele egy lemez ({um:0}%)", um >= 95 ? "err" : "warn");
        var health = All("smart.", ".health");
        if (health.Count > 0 && health.Max() is var hm && hm >= 1) H(hm >= 2 ? 40 : 20, hm >= 2 ? "Hibás lemez (SMART)" : "Lemez-figyelmeztetés (SMART)", hm >= 2 ? "err" : "warn");
        var life = All("smart.", ".life").Concat(All("smart.", ".wearlevel")).Concat(All("smart.", ".wearpct").Select(w => 100 - w)).ToList();
        if (life.Count > 0 && life.Min() is var lm && lm < 30) H(lm < 10 ? 30 : 12, $"Egy SSD hátralévő élettartama {lm:0}%", lm < 10 ? "err" : "warn");
        var bad = All("smart.", ".realloc").Concat(All("smart.", ".pending")).Concat(All("smart.", ".offunc")).Sum();
        if (bad > 0) H(20, $"Hibás / áthelyezett szektorok: {bad:0}", "err");
        var temps = All("smart.", ".temp");
        if (temps.Count > 0 && temps.Max() is var tm && tm >= 60) H(10, $"Forró lemez ({tm:0} °C)");
        if (i.PendingReboot) H(5, "Újraindításra vár");
        if (i.ActiveAlerts > 0) H(Math.Min(15, i.ActiveAlerts * 3), $"{i.ActiveAlerts} aktív riasztás");
        if (V("mem.usedpct") is double mu && mu >= 95) H(5, $"Kevés a szabad memória ({mu:0}%)");

        int sec = Math.Max(0, 100 - items.Where(x => x.Area == "Biztonság").Sum(x => x.Penalty));
        int hea = Math.Max(0, 100 - items.Where(x => x.Area == "Állapot").Sum(x => x.Penalty));
        var score = (sec + hea + 1) / 2;
        return new Result(score, sec, hea, GradeOf(score), items.OrderByDescending(x => x.Penalty).ToList());
    }
}
