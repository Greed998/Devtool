using System.Text;
using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A telephely naplóinak felküldése a Központba (1.5.7, a felhasználó döntése: központi napló a HUB-on, az USB-napló
/// NÉLKÜL – az személyes adat, a telephelyen marad). A naplófájlok helyben is megmaradnak (méretkorláttal forgatva: ez a
/// puffer, amíg a Központ nem érhető el). Fájlonként egy bájt-mutató jelzi, meddig ment már fel (hub-naplo.json):
/// forgatáskor (LogFolder.OnRotate) a mutató a forgatott párra vált, így egy sor sem vész el és nem duplázódik.
/// A felküldés csak egész sorokat visz, betárcsázásonként naplónként legfeljebb 256 KB-ot; a mutató csak a Központ
/// sikeres válasza után lép tovább.
/// </summary>
public static class LogShip
{
    /// <summary>A felküldött naplók: fájl, a forgatott párja, a Központban használt név.</summary>
    public static readonly (string File, string Rotated, string Log)[] Logs =
    {
        ("audit.log", "audit.log.1", "audit"), ("alerts.log", "alerts.log.1", "alerts"),
        ("snmp.log", "snmp.log.1", "snmp"), ("fleet-server.log", "fleet-server.log.old", "server")
    };

    public const int MaxChunk = 256 * 1024;

    public sealed class Cursor
    {
        public long Offset { get; set; }
        /// <summary>A forgatott fájlban ennyiig ment fel (-1: nincs fel nem küldött forgatott rész).</summary>
        public long RotOffset { get; set; } = -1;
        public int Gen { get; set; }
    }

    private static readonly object Lock = new();
    private static Dictionary<string, Cursor> _cur = new();
    private static string _stateFile = "";

    /// <summary>Indításkor: a mutatók betöltése, és feliratkozás a forgatásra. Az első alkalommal a mostani naplók elejéről indul.</summary>
    public static void Init(string dataDir)
    {
        _stateFile = Path.Combine(dataDir, "hub-naplo.json");
        try
        {
            if (File.Exists(_stateFile))
                lock (Lock) _cur = JsonSerializer.Deserialize<Dictionary<string, Cursor>>(File.ReadAllText(_stateFile), ProtocolJson.Options) ?? new();
        }
        catch { }
        LogFolder.OnRotate = OnRotate;
    }

    private static void OnRotate(string file)
    {
        lock (Lock)
        {
            if (!Logs.Any(l => l.File == file)) return;
            var c = _cur.TryGetValue(file, out var x) ? x : _cur[file] = new Cursor();
            // A most forgatott fájlból még a mutatóig ment fel; ha előtte is volt fel nem küldött forgatott rész, az most
            // felülíródott (két forgatás egy betárcsázás alatt – nagyon sok napló): az elveszett, a Központ hiányt lát.
            c.RotOffset = c.Offset; c.Offset = 0; c.Gen++;
            SaveState();
        }
    }

    private static void SaveState()
    {
        if (_stateFile.Length == 0) return;
        try
        {
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cur, ProtocolJson.Options));
            File.Move(tmp, _stateFile, true);
        }
        catch { }
    }

    /// <summary>
    /// A még fel nem küldött sorok naplónként; a visszaadott commit a Központ sikeres válasza után hívandó (akkor lép a
    /// mutató). Ha nincs új sor, üres lista és null.
    /// </summary>
    public static (List<SiteLogChunk> Chunks, Action? Commit) Take(string dataDir, int maxChunk = MaxChunk)
    {
        var chunks = new List<SiteLogChunk>();
        var plans = new List<(string File, int Gen, long? NewRot, long? NewOff)>();
        lock (LogFolder.Gate)
        lock (Lock)
        {
            foreach (var (file, rotated, log) in Logs)
            {
                var c = _cur.TryGetValue(file, out var x) ? x : new Cursor();
                var sb = new StringBuilder();
                long? newRot = null, newOff = null;
                var budget = maxChunk;
                if (c.RotOffset >= 0)
                {
                    var (text, end, eof) = ReadLines(LogFolder.PathOf(dataDir, rotated), c.RotOffset, budget);
                    sb.Append(text); budget -= Encoding.UTF8.GetByteCount(text);
                    newRot = eof ? -1 : end;
                }
                if ((newRot ?? -1) < 0 && budget > 0)
                {
                    var (text, end, _) = ReadLines(LogFolder.PathOf(dataDir, file), c.Offset, budget);
                    sb.Append(text);
                    newOff = end;
                }
                if (sb.Length > 0) chunks.Add(new SiteLogChunk { Log = log, Text = sb.ToString() });
                if (newRot is not null || (newOff is not null && newOff != c.Offset)) plans.Add((file, c.Gen, newRot, newOff));
            }
        }
        if (plans.Count == 0) return (chunks, null);
        return (chunks, () => Commit(plans));
    }

    private static void Commit(List<(string File, int Gen, long? NewRot, long? NewOff)> plans)
    {
        lock (Lock)
        {
            foreach (var (file, gen, newRot, newOff) in plans)
            {
                var c = _cur.TryGetValue(file, out var x) ? x : _cur[file] = new Cursor();
                if (c.Gen == gen)
                {
                    if (newRot is not null) c.RotOffset = newRot.Value;
                    if (newOff is not null) c.Offset = newOff.Value;
                }
                else if (c.Gen == gen + 1 && newOff is not null)
                    // Közben forgatás volt: amit a (most már forgatott) fájlból felküldtünk, az ott számít.
                    c.RotOffset = newOff.Value;
            }
            SaveState();
        }
    }

    /// <summary>
    /// Egész sorok a fájlból from-tól, legfeljebb max bájt (a félbe vágott utolsó sor a következő körre marad). Ha a fájl
    /// rövidebb a mutatónál (kézzel törölték / kicserélték), az elejéről. Visszaadja: szöveg, az új mutató, a végére ért-e.
    /// </summary>
    public static (string Text, long End, bool Eof) ReadLines(string path, long from, int max)
    {
        try
        {
            if (!File.Exists(path)) return ("", 0, true);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (from > fs.Length) from = 0;
            fs.Position = from;
            var want = (int)Math.Min(max, fs.Length - from);
            if (want <= 0) return ("", from, true);
            var buf = new byte[want];
            var n = 0;
            while (n < want) { var r = fs.Read(buf, n, want - n); if (r <= 0) break; n += r; }
            var last = Array.LastIndexOf(buf, (byte)'\n', n - 1);
            if (last < 0)
                // Egyetlen, a korlátnál hosszabb sor: darabolva megy (különben örökre elakadna).
                return n >= max ? (Encoding.UTF8.GetString(buf, 0, n) + "\n", from + n, false) : ("", from, true);
            var text = Encoding.UTF8.GetString(buf, 0, last + 1);
            var end = from + last + 1;
            return (text, end, end >= fs.Length);
        }
        catch { return ("", from, false); }
    }
}
