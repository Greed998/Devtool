using System.Collections.Concurrent;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A webes admin terminál segéd-állapota (1.5.9, a felhasználó kérése és döntése: admin, PowerShell LocalSystemként, alapból
/// kikapcsolva, gépenként engedélyezve). A parancs és a kimenet a MEGLÉVŐ, aláírt feladat-/eredmény-csatornán megy: a szerver
/// a RunCommand feladatot a postaládába teszi, az agent a következő (gyors) betárcsázáskor futtatja, és az eredményt aláírva
/// visszaküldi. Ez az osztály csak azt tartja nyilván, melyik gépen van épp NYITOTT terminál-ablak – akkor a szerver 2 mp-es
/// betárcsázást kér az agenttől, hogy a parancs és a válasz hamar átmenjen; ablak nélkül marad a szokásos ütem.
/// </summary>
public static class Terminal
{
    // agentId -> meddig tekintjük nyitottnak a terminál-ablakot (a böngésző „életben tartja”, lásd /web/api/terminal-ping)
    private static readonly ConcurrentDictionary<string, DateTime> _open = new();
    // agentId -> mikor jelentkezett utoljára (a felület jelzi, ha a gép nem jelentkezik, a parancs várakozik)
    private static readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(45);

    /// <summary>A böngésző terminál-ablaka nyitva (parancs kiadásakor és pingkor).</summary>
    public static void Open(string agentId) => _open[agentId] = DateTime.UtcNow + Window;

    /// <summary>Van-e nyitott terminál-ablak erre a gépre (akkor gyors betárcsázás).</summary>
    public static bool Active(string agentId) => _open.TryGetValue(agentId, out var t) && t > DateTime.UtcNow;

    public static void Seen(string agentId) => _seen[agentId] = DateTime.UtcNow;

    /// <summary>Jelentkezett-e a gép az elmúlt percben (a felület ebből látja, hogy a parancs hamar lefuthat).</summary>
    public static bool Online(string agentId) => _seen.TryGetValue(agentId, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(1);
}
