using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A monitorozasi metrikak idosoros tarolasa SQLite-ban. Beepitett, nincs kulso
/// adatbazis-szerver. A tabla szandekosan lapos (agent, kulcs, ido, ertek), igy
/// barmilyen metrika tarolhato a sema valtoztatasa nelkul.
///
/// Megorzes: a reszletes minta egy ideig marad, utana orankent atlagolodik, es egy
/// hosszabb hatarido utan torlodik. Igy a fajl nem hizik korlatlanul.
/// </summary>
public sealed class MetricStore : IDisposable
{
    private SqliteConnection _db;
    private readonly object _lock = new();
    private DateTime _lastRetention = DateTime.MinValue;
    private TimeSpan _retentionEvery = TimeSpan.FromHours(12);

    /// <summary>A metrics.db mappája (1.5.4-tól a Beállításokban áthelyezhető: Relocate).</summary>
    public string Dir { get; private set; }

    public MetricStore(string dir)
    {
        Directory.CreateDirectory(dir);
        Dir = dir;
        _db = Open(Path.Combine(dir, "metrics.db"));
        Init();
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        return db;
    }

    /// <summary>
    /// Az adatbázis áthelyezése futás közben (a felhasználó kérése: a tárolás helye a Beállításokban). A zár alatt a
    /// mérések addig várnak: WAL-ellenőrzőpont, lezárás, MÁSOLÁS az új helyre, megnyitás ott, végül a régi törlése -
    /// ha bármi elakad, a régi helyen nyílik meg újra, adatvesztés nélkül. Visszaadja a hibát (null = rendben).
    /// </summary>
    public string? Relocate(string newDir)
    {
        lock (_lock)
        {
            if (string.Equals(Path.GetFullPath(newDir).TrimEnd('\\'), Path.GetFullPath(Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            var src = Path.Combine(Dir, "metrics.db");
            var dst = Path.Combine(newDir, "metrics.db");
            if (File.Exists(dst)) return "A célmappában már van metrics.db – töröld, vagy válassz másik mappát.";
            try { Directory.CreateDirectory(newDir); Exec("PRAGMA wal_checkpoint(TRUNCATE);"); }
            catch (Exception ex) { return ex.Message; }
            _db.Close();
            SqliteConnection.ClearPool(_db);
            _db.Dispose();
            try
            {
                File.Copy(src, dst);
                foreach (var ext in new[] { "-wal", "-shm" }) if (File.Exists(src + ext)) File.Copy(src + ext, dst + ext);
                _db = Open(dst);
                Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            }
            catch (Exception ex)
            {
                foreach (var ext in new[] { "", "-wal", "-shm" }) try { File.Delete(dst + ext); } catch { }
                _db = Open(src);
                Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
                return ex.Message;
            }
            var old = Dir;
            Dir = newDir;
            foreach (var ext in new[] { "", "-wal", "-shm" }) try { File.Delete(Path.Combine(old, "metrics.db") + ext); } catch { }
            // 1.5.8: a napi fájlok (és az archívum) is költöznek – fájlonként, hogy más meghajtóra is menjen.
            try { MoveTree(Path.Combine(old, DaysFolder), Path.Combine(newDir, DaysFolder)); } catch (Exception ex) { return "A napi fájlok áthelyezése: " + ex.Message; }
            return null;
        }
    }

    private static void MoveTree(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(src)) { var t = Path.Combine(dst, Path.GetFileName(f)); if (!File.Exists(t)) File.Move(f, t); }
        foreach (var d in Directory.GetDirectories(src)) MoveTree(d, Path.Combine(dst, Path.GetFileName(d)));
        try { Directory.Delete(src, false); } catch { }
    }

    private void Init()
    {
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS samples(
                 agentId TEXT NOT NULL, key TEXT NOT NULL, ts INTEGER NOT NULL,
                 value REAL NOT NULL, category TEXT, label TEXT, unit TEXT);");
        Exec("CREATE INDEX IF NOT EXISTS ix_samples ON samples(agentId, key, ts);");
        Exec("CREATE INDEX IF NOT EXISTS ix_ts ON samples(ts);");
        // Gepenkent es kulcsonkent a legfrissebb ertek: a "mit merunk" lista es az aktualis ertek
        // innen jon, nem az osszes minta vegigolvasasabol (percenkenti mintavetelnel gepenkent
        // tobb szazezer sor lenne).
        Exec(@"CREATE TABLE IF NOT EXISTS latest(
                 agentId TEXT NOT NULL, key TEXT NOT NULL, ts INTEGER NOT NULL, value REAL NOT NULL,
                 category TEXT, label TEXT, unit TEXT, PRIMARY KEY(agentId, key));");
        if (Scalar("SELECT COUNT(*) FROM latest") == 0 && Scalar("SELECT COUNT(*) FROM samples") > 0)
            Exec(@"INSERT OR REPLACE INTO latest(agentId,key,ts,value,category,label,unit)
                   SELECT s.agentId,s.key,s.ts,s.value,s.category,s.label,s.unit FROM samples s
                   JOIN (SELECT agentId,key,MAX(ts) mts FROM samples GROUP BY agentId,key) m
                     ON s.agentId=m.agentId AND s.key=m.key AND s.ts=m.mts;");
    }

    private long Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Egy agent egy jelentkezesenek metrikai. Egyetlen tranzakcioban.</summary>
    public void Insert(string agentId, IReadOnlyList<Metric> metrics, DateTime whenUtc) =>
        InsertMany(agentId, new[] { (whenUtc, metrics) });

    /// <summary>Tobb idopont mintai (a jelentkezesek kozotti mintavetel) - egyetlen tranzakcioban.</summary>
    public void InsertMany(string agentId, IEnumerable<(DateTime When, IReadOnlyList<Metric> Metrics)> samples)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO samples(agentId,key,ts,value,category,label,unit) VALUES($a,$k,$t,$v,$c,$l,$u)";
            var pa = cmd.CreateParameter(); pa.ParameterName = "$a"; cmd.Parameters.Add(pa);
            var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; cmd.Parameters.Add(pk);
            var pt = cmd.CreateParameter(); pt.ParameterName = "$t"; cmd.Parameters.Add(pt);
            var pv = cmd.CreateParameter(); pv.ParameterName = "$v"; cmd.Parameters.Add(pv);
            var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
            var pl = cmd.CreateParameter(); pl.ParameterName = "$l"; cmd.Parameters.Add(pl);
            var pu = cmd.CreateParameter(); pu.ParameterName = "$u"; cmd.Parameters.Add(pu);
            pa.Value = agentId;
            var newest = new Dictionary<string, (long Ts, Metric M)>();
            foreach (var (when, metrics) in samples)
            {
                var ts = new DateTimeOffset(DateTime.SpecifyKind(when, DateTimeKind.Utc)).ToUnixTimeSeconds();
                pt.Value = ts;
                foreach (var m in metrics)
                {
                    pk.Value = m.Key; pv.Value = m.Value;
                    pc.Value = m.Category; pl.Value = m.Label; pu.Value = m.Unit;
                    cmd.ExecuteNonQuery();
                    if (!newest.TryGetValue(m.Key, out var n) || ts >= n.Ts) newest[m.Key] = (ts, m);
                }
            }

            // A legfrissebb ertek kulcsonkent - csak ha tenyleg ujabb (kesve erkezo minta nem irja felul).
            cmd.CommandText = @"INSERT INTO latest(agentId,key,ts,value,category,label,unit) VALUES($a,$k,$t,$v,$c,$l,$u)
                ON CONFLICT(agentId,key) DO UPDATE SET ts=excluded.ts,value=excluded.value,category=excluded.category,
                   label=excluded.label,unit=excluded.unit WHERE excluded.ts>=latest.ts";
            foreach (var (key, (ts, m)) in newest)
            {
                pk.Value = key; pt.Value = ts; pv.Value = m.Value;
                pc.Value = m.Category; pl.Value = m.Label; pu.Value = m.Unit;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Egy agent legfrissebb ertekei kulcsonkent (kulcsonkent a legutobbi meres).</summary>
    public Dictionary<string, (double Value, string Label, string Unit, string Category)> Latest(string agentId)
    {
        var d = new Dictionary<string, (double, string, string, string)>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT key,value,label,unit,category FROM latest WHERE agentId=$a";
            cmd.Parameters.AddWithValue("$a", agentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                d[r.GetString(0)] = (r.GetDouble(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4));
        }
        return d;
    }

    public sealed record Point(long Ts, double Value);

    /// <summary>
    /// Egy metrika idosora egy agentre, adott oratol. Grafikonhoz: legfeljebb kb. maxPoints pont -
    /// hosszu idoszaknal az egy savba eso mintak atlaga (percenkenti mintavetelnel 30 nap = 43 ezer
    /// pont lenne, amit a bongeszo feleslegesen rajzolna). Rovid idoszakon a nyers mintak.
    /// </summary>
    public List<Point> Series(string agentId, string key, int hours, int maxPoints = 720)
    {
        var from = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeSeconds();
        var bucket = Math.Max(1L, hours * 3600L / Math.Max(10, maxPoints));
        // 1.5.8: a napi fájlokból és a metrics.db-ből együtt; a nap határán átnyúló sáv két forrásból jön (összeg / darab).
        var acc = new SortedDictionary<long, (long MinTs, double Sum, long N)>();
        ForSources(from, db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT ts/$b,MIN(ts),SUM(value),COUNT(*) FROM samples WHERE agentId=$a AND key=$k AND ts>=$f GROUP BY ts/$b";
            cmd.Parameters.AddWithValue("$a", agentId);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$b", bucket);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long b = r.GetInt64(0), m = r.GetInt64(1), n = r.GetInt64(3); var s = r.GetDouble(2);
                acc[b] = acc.TryGetValue(b, out var o) ? (Math.Min(o.MinTs, m), o.Sum + s, o.N + n) : (m, s, n);
            }
        });
        return acc.Values.Where(v => v.N > 0).Select(v => new Point(v.MinTs, v.Sum / v.N)).ToList();
    }

    /// <summary>Egy idősor kiegészítése a megadott napi fájlokból (a Központ: a telephelyek feltöltött fájljai).</summary>
    public static List<Point> SeriesFromFiles(IEnumerable<DayFile> files, string agentId, string key, int hours, int maxPoints = 720)
    {
        var from = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeSeconds();
        var bucket = Math.Max(1L, hours * 3600L / Math.Max(10, maxPoints));
        var acc = new SortedDictionary<long, (long MinTs, double Sum, long N)>();
        foreach (var f in files.Where(f => f.Start + 86400 > from))
            try
            {
                using var db = OpenRead(f.Path);
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT ts/$b,MIN(ts),SUM(value),COUNT(*) FROM samples WHERE agentId=$a AND key=$k AND ts>=$f GROUP BY ts/$b";
                cmd.Parameters.AddWithValue("$a", agentId); cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$f", from); cmd.Parameters.AddWithValue("$b", bucket);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long b = r.GetInt64(0), m = r.GetInt64(1), n = r.GetInt64(3); var s = r.GetDouble(2);
                    acc[b] = acc.TryGetValue(b, out var o) ? (Math.Min(o.MinTs, m), o.Sum + s, o.N + n) : (m, s, n);
                }
            }
            catch { }
        return acc.Values.Where(v => v.N > 0).Select(v => new Point(v.MinTs, v.Sum / v.N)).ToList();
    }

    /// <summary>
    /// A Kozpontba kuldendo, LEZART savok atlagai fromTs-tol (a meg folyo savot nem kuldjuk, hogy
    /// ne keruljon fel ketszer). Ha a sorlimit egy sav kozepen vagna, azt a savot a kovetkezo
    /// korre hagyjuk. Visszaadja a sorokat es a kovetkezo kezdopontot.
    /// </summary>
    public (List<SiteMetricRow> Rows, long NextFrom) Buckets(long fromTs, int bucketSec, int maxRows)
    {
        var until = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / bucketSec * bucketSec;
        var rows = new List<SiteMetricRow>();
        // Időrendben (napi fájlok, majd a metrics.db); az 5 perces sáv a nap határán nem nyúlik át, így egy sáv egy forrásban van.
        ForSources(fromTs, db =>
        {
            var remaining = maxRows - rows.Count;
            if (remaining <= 0) return;
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT agentId,key,(ts/$b)*$b AS bt,AVG(value),MAX(category),MAX(label),MAX(unit)
                                FROM samples WHERE ts>=$f AND ts<$u GROUP BY agentId,key,bt ORDER BY bt LIMIT $m";
            cmd.Parameters.AddWithValue("$b", bucketSec);
            cmd.Parameters.AddWithValue("$f", fromTs);
            cmd.Parameters.AddWithValue("$u", until);
            cmd.Parameters.AddWithValue("$m", remaining);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new SiteMetricRow
                {
                    AgentId = r.GetString(0), Key = r.GetString(1), Ts = r.GetInt64(2), Value = Math.Round(r.GetDouble(3), 2),
                    Category = r.IsDBNull(4) ? "" : r.GetString(4), Label = r.IsDBNull(5) ? "" : r.GetString(5), Unit = r.IsDBNull(6) ? "" : r.GetString(6)
                });
        });
        if (rows.Count < maxRows) return (rows, Math.Max(fromTs, until));
        var last = rows[^1].Ts;
        var complete = rows.Where(x => x.Ts != last).ToList();
        return complete.Count > 0 ? (complete, last) : (rows, last + bucketSec);
    }

    public sealed record DailyRow(string AgentId, string Key, string Day, double Avg, double Min, double Max, long Count,
        string Label, string Unit, string Category);

    /// <summary>
    /// Napi osszesito (helyi ido szerinti nap) a CSV-jelenteshez: gepenkent, kulcsonkent atlag / min / max.
    /// Kulon, csak olvaso kapcsolaton fut (WAL mellett a jelentkezesek irasa kozben is), igy egy hosszabb
    /// lekerdezes nem tartja fel a gepek jelentkezeset.
    /// </summary>
    public List<DailyRow> Daily(int days, int maxRows = 500_000)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        // 1.5.8: a napi fájlokból és a metrics.db-ből (a helyi nap két UTC-fájlba is eshet: összeg / darab / min / max).
        var acc = new Dictionary<(string, string, string), (double Sum, double Min, double Max, long N, string L, string U, string C)>();
        var paths = DayFiles().Where(f => f.Start + 86400 > from).Select(f => f.Path).Append(System.IO.Path.Combine(Dir, "metrics.db"));
        foreach (var p in paths)
        {
            if (acc.Count >= maxRows) break;
            try
            {
                using var db = OpenRead(p);
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT agentId,key,date(ts,'unixepoch','localtime') d,SUM(value),MIN(value),MAX(value),COUNT(*),
                                           MAX(label),MAX(unit),MAX(category)
                                    FROM samples WHERE ts>=$f GROUP BY agentId,key,d LIMIT $m";
                cmd.Parameters.AddWithValue("$f", from);
                cmd.Parameters.AddWithValue("$m", maxRows);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var k = (r.GetString(0), r.GetString(1), r.GetString(2));
                    var v = (r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetInt64(6), r.IsDBNull(7) ? "" : r.GetString(7), r.IsDBNull(8) ? "" : r.GetString(8), r.IsDBNull(9) ? "" : r.GetString(9));
                    acc[k] = acc.TryGetValue(k, out var o) ? (o.Sum + v.Item1, Math.Min(o.Min, v.Item2), Math.Max(o.Max, v.Item3), o.N + v.Item4, o.L, o.U, o.C) : v;
                }
            }
            catch { }
        }
        return acc.OrderBy(x => x.Key.Item1).ThenBy(x => x.Key.Item2).ThenBy(x => x.Key.Item3).Take(maxRows)
            .Select(x => new DailyRow(x.Key.Item1, x.Key.Item2, x.Key.Item3, x.Value.Sum / Math.Max(1, x.Value.N), x.Value.Min, x.Value.Max, x.Value.N,
                x.Value.L, x.Value.U, x.Value.C)).ToList();
    }

    /// <summary>Mely kulcsokra van adat egy agentnel (a grafikon-valaszthoz).</summary>
    public List<(string Key, string Label, string Category, string Unit)> Keys(string agentId)
    {
        var list = new List<(string, string, string, string)>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT key, label, category, unit FROM latest WHERE agentId=$a ORDER BY category, label";
            cmd.Parameters.AddWithValue("$a", agentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
        }
        return list;
    }

    /// <summary>
    /// Az összes gép mért kulcsai (a szabály-szerkesztő metrika-választójához): kulcsonként a felirat, kategória, egység
    /// és hány gépen van. Csak az adott gépek (agentIds; null = mind) kulcsai.
    /// </summary>
    public List<(string Key, string Label, string Category, string Unit, int Machines)> AllKeys(ICollection<string>? agentIds = null)
    {
        var d = new Dictionary<string, (string Label, string Category, string Unit, int Machines)>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT agentId, key, label, category, unit FROM latest";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (agentIds is not null && !agentIds.Contains(r.GetString(0))) continue;
                var k = r.GetString(1);
                if (d.TryGetValue(k, out var c)) d[k] = c with { Machines = c.Machines + 1 };
                else d[k] = (r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), 1);
            }
        }
        return d.Select(kv => (kv.Key, kv.Value.Label, kv.Value.Category, kv.Value.Unit, kv.Value.Machines)).OrderBy(x => x.Category).ThenBy(x => x.Label).ToList();
    }

    /// <summary>
    /// Megorzesi politika. Naponta egyszer fut. detailDays napnal regebbi reszletes
    /// mintakat orankent atlagolja (egy pont per ora), keepDays utan mindent torol.
    /// </summary>
    public void ApplyRetention(int detailDays, int keepDays, int archiveDays = 0, bool holdForUpload = false)
    {
        if (DateTime.UtcNow - _lastRetention < _retentionEvery) return;
        _lastRetention = DateTime.UtcNow;
        // 1.5.8: a lezárt napok a napi fájlokba; ha maradt még (első indulás régi adatbázissal), 10 perc múlva folytatja.
        var more = false;
        try { more = CloseDays(); } catch { }
        _retentionEvery = more ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(12);
        try { MaintainDir(DaysDir, detailDays, keepDays, archiveDays, holdForUpload); } catch { }

        var detailBefore = DateTimeOffset.UtcNow.AddDays(-detailDays).ToUnixTimeSeconds();
        var keepBefore = DateTimeOffset.UtcNow.AddDays(-keepDays).ToUnixTimeSeconds();

        lock (_lock)
        {
            // 1. reszletes -> orankenti atlag: a detailDays-nel regebbi, de keepDays-nel ujabb
            //    mintakat orara kerekitve osszevonjuk.
            using (var tx = _db.BeginTransaction())
            {
                Exec(@"CREATE TEMP TABLE agg AS
                       SELECT agentId,key,(ts/3600)*3600 AS hts,AVG(value) AS av,
                              MAX(category) category,MAX(label) label,MAX(unit) unit
                       FROM samples WHERE ts<$d AND ts>=$k
                       GROUP BY agentId,key,ts/3600;", ("$d", detailBefore), ("$k", keepBefore));
                Exec("DELETE FROM samples WHERE ts<$d AND ts>=$k;", ("$d", detailBefore), ("$k", keepBefore));
                Exec(@"INSERT INTO samples(agentId,key,ts,value,category,label,unit)
                       SELECT agentId,key,hts,av,category,label,unit FROM agg;");
                Exec("DROP TABLE agg;");
                tx.Commit();
            }
            // 2. keepDays-nel regebbi: torles (a mar nem mert kulcsok is kikerulnek a listabol)
            Exec("DELETE FROM samples WHERE ts<$k;", ("$k", keepBefore));
            Exec("DELETE FROM latest WHERE ts<$k;", ("$k", keepBefore));
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    public void PurgeAgent(string agentId)
    {
        lock (_lock)
        {
            Exec("DELETE FROM samples WHERE agentId=$a;", ("$a", agentId));
            Exec("DELETE FROM latest WHERE agentId=$a;", ("$a", agentId));
        }
        foreach (var f in DayFiles()) EditDay(f.Path, db => Exec(db, "DELETE FROM samples WHERE agentId=$a;", ("$a", agentId)));
    }

    // ================================================================ napi fájlok (1.5.8)
    // A felhasználó kérése: a mérések naponta külön fájlba kerülnek (metrics-napok\metrics-ÉÉÉÉ-HH-NN.db, UTC-nap). Egy nap
    // fájlja 48 órával a nap vége után zárul le (addig késve érkező minták is jöhetnek: az agent 24 órát pufferel, a szerver
    // 48 óráig fogad) – ekkor a nap sorai a metrics.db-ből ÁTKERÜLNEK oda (nincs kettő belőlük). A lezárt fájlt a szerver
    // a megőrzés után a Központba tölti fel (és törli), a Központ a megőrzés után archiválja (ZIP), majd törli.

    public const string DaysFolder = "metrics-napok";
    public string DaysDir => Path.Combine(Dir, DaysFolder);
    private string ArchiveDir => Path.Combine(DaysDir, "archiv");

    public sealed record DayFile(string Day, long Start, string Path);

    private static string DayName(long start) => DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime.ToString("yyyy-MM-dd");

    public static bool TryDay(string day, out long start)
    {
        start = 0;
        if (!DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return false;
        start = new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return true;
    }

    /// <summary>A lezárt napi fájlok időrendben (a legrégebbi elöl).</summary>
    public List<DayFile> DayFiles() => DayFilesIn(DaysDir);

    public static List<DayFile> DayFilesIn(string dir)
    {
        var l = new List<DayFile>();
        if (!Directory.Exists(dir)) return l;
        foreach (var p in Directory.GetFiles(dir, "metrics-*.db"))
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(p);
            if (n.Length == 18 && TryDay(n[8..], out var s)) l.Add(new DayFile(n[8..], s, p));
        }
        return l.OrderBy(x => x.Start).ToList();
    }

    private static SqliteConnection OpenRead(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 10, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void EditDay(string path, Action<SqliteConnection> act)
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30, Pooling = false }.ToString());
            db.Open();
            act(db);
        }
        catch { }
    }

    private static void Exec(SqliteConnection db, string sql, params (string, object)[] ps)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A lekérdezés forrásai fromTs-től: a lezárt napi fájlok (csak olvasva) és a metrics.db, időrendben.</summary>
    private void ForSources(long fromTs, Action<SqliteConnection> query)
    {
        foreach (var f in DayFiles().Where(f => f.Start + 86400 > fromTs))
        {
            try { using var db = OpenRead(f.Path); query(db); }
            catch { }   // épp archiválják / törlik: kimarad
        }
        lock (_lock) query(_db);
    }

    /// <summary>
    /// A lezárt napok átköltöztetése a metrics.db-ből a napi fájlokba (egy körben legfeljebb maxDays nap, hogy a zár ne
    /// tartson sokáig – az első induláskor a régi adatbázisban sok nap lehet). Visszaadja, maradt-e még.
    /// </summary>
    public bool CloseDays(int maxDays = 7)
    {
        var cut = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 172800) / 86400 * 86400;
        lock (_lock)
        {
            var days = new List<long>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT ts/86400 FROM samples WHERE ts<$c ORDER BY 1";
                cmd.Parameters.AddWithValue("$c", cut);
                using var r = cmd.ExecuteReader();
                while (r.Read()) days.Add(r.GetInt64(0));
            }
            foreach (var d in days.Take(maxDays))
            {
                long s = d * 86400, e = s + 86400;
                Directory.CreateDirectory(DaysDir);
                var path = System.IO.Path.Combine(DaysDir, $"metrics-{DayName(s)}.db");
                Exec("ATTACH DATABASE $p AS d;", ("$p", path));
                try
                {
                    Exec(@"CREATE TABLE IF NOT EXISTS d.samples(agentId TEXT NOT NULL, key TEXT NOT NULL, ts INTEGER NOT NULL,
                             value REAL NOT NULL, category TEXT, label TEXT, unit TEXT);");
                    Exec("CREATE TABLE IF NOT EXISTS d.meta(k TEXT PRIMARY KEY, v TEXT);");
                    using (var tx = _db.BeginTransaction())
                    {
                        Exec("INSERT INTO d.samples SELECT agentId,key,ts,value,category,label,unit FROM main.samples WHERE ts>=$s AND ts<$e;", ("$s", s), ("$e", e));
                        Exec("DELETE FROM main.samples WHERE ts>=$s AND ts<$e;", ("$s", s), ("$e", e));
                        tx.Commit();
                    }
                    Exec("CREATE INDEX IF NOT EXISTS d.ix_samples ON samples(agentId, key, ts);");
                }
                finally { Exec("DETACH DATABASE d;"); }
            }
            return days.Count > maxDays;
        }
    }

    /// <summary>A lezárt napi fájl órás átlagra ritkítása (egyszer; utána a meta-táblában jelölve), és tömörítés.</summary>
    private static void Downsample(string path)
    {
        EditDay(path, db =>
        {
            using (var c = db.CreateCommand())
            {
                c.CommandText = "SELECT v FROM meta WHERE k='hourly'";
                if (c.ExecuteScalar() is not null) return;
            }
            using (var tx = db.BeginTransaction())
            {
                Exec(db, @"CREATE TEMP TABLE agg AS SELECT agentId,key,(ts/3600)*3600 AS hts,AVG(value) AS av,
                             MAX(category) category,MAX(label) label,MAX(unit) unit FROM samples GROUP BY agentId,key,ts/3600;");
                Exec(db, "DELETE FROM samples;");
                Exec(db, "INSERT INTO samples(agentId,key,ts,value,category,label,unit) SELECT agentId,key,hts,av,category,label,unit FROM agg;");
                Exec(db, "DROP TABLE agg;");
                Exec(db, "INSERT OR REPLACE INTO meta(k,v) VALUES('hourly','1');");
                tx.Commit();
            }
            Exec(db, "VACUUM;");
        });
    }

    /// <summary>A lezárt napi fájlok, amelyek a megőrzésnél régebbiek (a szerver ezeket tölti fel a Központba).</summary>
    public List<DayFile> DueFiles(int keepDays)
    {
        var before = DateTimeOffset.UtcNow.AddDays(-keepDays).ToUnixTimeSeconds();
        return DayFiles().Where(f => f.Start + 86400 <= before).ToList();
    }

    public void DeleteDayFile(string day)
    {
        if (!TryDay(day, out _)) return;
        var p = System.IO.Path.Combine(DaysDir, $"metrics-{day}.db");
        SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-journal", "-wal", "-shm" }) try { File.Delete(p + ext); } catch { }
    }

    /// <summary>
    /// A napi fájlok megőrzése: detailDays után órás átlag; keepDays után archívum (ZIP, archiveDays-ig), vagy – ha a
    /// Központba kell feltölteni (holdForUpload) – megmarad a feltöltésig (legfeljebb még 90 napig, ha a Központ nem érhető
    /// el), különben törlés. Ugyanez a Központ telephelyenkénti fájljaira is (MaintainDir).
    /// </summary>
    public static void MaintainDir(string dir, int detailDays, int keepDays, int archiveDays, bool holdForUpload)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var f in DayFilesIn(dir))
        {
            var ageEnd = now - (f.Start + 86400);   // a nap vége óta eltelt idő
            if (ageEnd >= keepDays * 86400L)
            {
                if (holdForUpload && ageEnd < (keepDays + 90) * 86400L) continue;
                if (archiveDays > keepDays && ageEnd < archiveDays * 86400L) Archive(f.Path, System.IO.Path.Combine(dir, "archiv"));
                else { SqliteConnection.ClearAllPools(); try { File.Delete(f.Path); } catch { } }
            }
            else if (ageEnd >= detailDays * 86400L) Downsample(f.Path);
        }
        var arch = System.IO.Path.Combine(dir, "archiv");
        if (Directory.Exists(arch))
            foreach (var z in Directory.GetFiles(arch, "metrics-*.zip"))
                if (TryDay(System.IO.Path.GetFileNameWithoutExtension(z)[8..], out var s) && now - (s + 86400) >= archiveDays * 86400L)
                    try { File.Delete(z); } catch { }
    }

    /// <summary>Archiválás: a napi fájl ZIP-be (tömörítve), utána az eredeti törlődik – egy példány marad.</summary>
    private static void Archive(string path, string archDir)
    {
        try
        {
            Downsample(path);
            Directory.CreateDirectory(archDir);
            var zip = System.IO.Path.Combine(archDir, System.IO.Path.GetFileNameWithoutExtension(path) + ".zip");
            var tmp = zip + ".tmp";
            SqliteConnection.ClearAllPools();
            using (var z = System.IO.Compression.ZipFile.Open(tmp, System.IO.Compression.ZipArchiveMode.Create))
                z.CreateEntryFromFile(path, System.IO.Path.GetFileName(path), System.IO.Compression.CompressionLevel.Optimal);
            File.Move(tmp, zip, true);
            File.Delete(path);
        }
        catch { }
    }

    /// <summary>Gépek adatainak törlése egy időszakra (a Központ: a feltöltött napi fájl kiváltja a saját 5 perces másolatát).</summary>
    public void PurgeRange(ICollection<string> agentIds, long from, long to)
    {
        if (agentIds.Count == 0) return;
        void Del(SqliteConnection db) { foreach (var a in agentIds) Exec(db, "DELETE FROM samples WHERE agentId=$a AND ts>=$f AND ts<$t;", ("$a", a), ("$f", from), ("$t", to)); }
        lock (_lock) Del(_db);
        foreach (var f in DayFiles().Where(f => f.Start < to && f.Start + 86400 > from)) EditDay(f.Path, Del);
    }

    private void Exec(string sql, params (string, object)[] ps)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { try { _db.Dispose(); } catch { } }
}

/// <summary>
/// A szabály-szerkesztő metrika-listája: a mért kulcsok, plusz a „bármelyik” minták a példányonkénti kulcsokhoz
/// (disk.C.usedpct → disk.*.usedpct = bármelyik lemez; hw.gpu0.temp → hw.gpu*.temp). Közös a szerverrel és a Központtal.
/// </summary>
public static class MetricCatalog
{
    // Ezek a kulcsrészek nem példánynevek (pl. net.tcp.established, hw.cpu.temp), belőlük nem lesz minta.
    private static readonly HashSet<string> Fixed = new(StringComparer.OrdinalIgnoreCase) { "tcp", "udp", "vms", "cpu", "rx", "tx" };

    public static object Build(IEnumerable<(string Key, string Label, string Category, string Unit, int Machines)> keys)
    {
        var list = keys.ToList();
        var patterns = new Dictionary<string, (string Label, string Category, string Unit, HashSet<string> Keys)>();
        foreach (var k in list)
        {
            var parts = k.Key.Split('.');
            if (parts.Length < 3) continue;
            var i = parts.Length - 2;
            var inst = parts[i];
            if (Fixed.Contains(inst)) continue;
            // Számozott példány (gpu0, core3, disk1): a minta a betűs rész + * (hw.gpu*.temp); különben az egész rész *.
            var m = System.Text.RegularExpressions.Regex.Match(inst, "^(gpu|core|disk|ram|cpu)[0-9]+$");
            parts[i] = m.Success ? m.Groups[1].Value + "*" : "*";
            var pat = string.Join('.', parts);
            if (!patterns.TryGetValue(pat, out var p))
            {
                // A felirat a példány nevét „bármelyik”-re cseréli (ha szerepel benne), különben elé írja.
                var num = m.Success ? inst[m.Groups[1].Length..] : "";
                var label = System.Text.RegularExpressions.Regex.Replace(k.Label,
                    @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(m.Success ? "#" + num : inst) + @"(?![\p{L}\p{N}])", "*");
                // A kulcsban a név tisztított (pl. a hálózati kártyáé): ha nem található, a kettőspont utáni rész lesz *.
                if (label == k.Label && !m.Success && label.IndexOf(':') is var c and > 0) label = label[..(c + 1)] + " *";
                patterns[pat] = p = ("Bármelyik – " + label, k.Category, k.Unit, new HashSet<string>());
            }
            p.Keys.Add(k.Key);
        }
        return new
        {
            keys = list.Select(k => new { key = k.Key, label = k.Label, category = k.Category, unit = k.Unit, machines = k.Machines }),
            patterns = patterns.OrderBy(x => x.Value.Category).ThenBy(x => x.Key)
                .Select(x => new { key = x.Key, label = x.Value.Label, category = x.Value.Category, unit = x.Value.Unit, count = x.Value.Keys.Count })
        };
    }
}
