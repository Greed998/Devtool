using System.Text.RegularExpressions;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A tárolási helyek (a felhasználó kérése, 1.5.4): naplók, mentések (frissítés / kulcscsere előtt) és a mérési adatbázis
/// (metrics.db). A Beállításokban állítható (admin); mentéskor és induláskor alkalmazva, a régi helyről mindent átköltöztet.
/// Közös a FleetServer és a Központ szerepben (mindkettő ugyanazt a ServerConfig-ot használja).
///
/// Ami az adatmappában marad: a kulcsok és a beállítások (server-config.json / hub.json, *.pem, fleet.json, users.json,
/// szabályok). Ezek helye a szolgáltatás környezetéhez kötött (ProgramData, ill. FLEET_DATA / FLEET_HUB_DATA), a mappa
/// jogai ott vannak szűkítve – a beállításokból áthelyezve a kulcsok elérhetetlenné válhatnának.
/// Új mappa jogai: csak a SYSTEM és a rendszergazdák (mint az adatmappáé); ezért idegen, nem üres mappa nem választható.
/// </summary>
public static class Storage
{
    public const string Logs = "logs", Backup = "backup", MetricsKey = "metrics";

    /// <summary>A mappa alapértelmezése (üres beállítás).</summary>
    public static string DefaultOf(string key, string dataDir) => key switch
    {
        Logs => LogFolder.Default(dataDir),
        Backup => Path.Combine(dataDir, "backup"),
        _ => dataDir
    };

    /// <summary>
    /// Útvonal ellenőrzése. Üres = alapértelmezés (normalized is üres). Helyi meghajtón levő teljes útvonal kell
    /// (a SYSTEM-fiók hálózati mappát nem ér el), nem lehet meghajtó-gyökér, rendszermappa, és nem lehet idegen,
    /// nem üres mappa (a jogait szűkítenénk). Visszaadja a hibát (null = rendben).
    /// </summary>
    public static string? Validate(string key, string? raw, string dataDir, out string normalized, string? current = null)
    {
        normalized = "";
        var s = (raw ?? "").Trim().Trim('"').Trim();
        if (s.Length == 0) return null;
        if (s.Length > 200 || s.Any(char.IsControl) || s.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "Érvénytelen útvonal.";
        if (!Regex.IsMatch(s, @"^[A-Za-z]:\\"))
            return "Helyi meghajtón levő teljes útvonalat adj meg (pl. D:\\Fleet\\Naplok). Hálózati mappa nem jó: a szolgáltatás (SYSTEM) nem éri el.";
        string full;
        try { full = Path.GetFullPath(s).TrimEnd('\\'); } catch { return "Érvénytelen útvonal."; }
        if (full.Length <= 3) return "A meghajtó gyökere nem lehet – adj meg egy mappát benne.";
        if (!Directory.Exists(Path.GetPathRoot(full)!)) return $"Nincs ilyen meghajtó: {Path.GetPathRoot(full)}";
        if (LogFolder.Same(full, DefaultOf(key, dataDir))) return null;   // az alapértelmezés kézzel beírva
        if (key != MetricsKey && LogFolder.Same(full, dataDir)) return "Az adatmappa gyökere nem lehet – az alapértelmezéshez hagyd üresen.";
        foreach (var sys in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 }.Where(x => x.Length > 0))
            if (LogFolder.Same(full, sys) || LogFolder.IsInside(full, sys)) return "Rendszermappába (Windows, Program Files) nem tehető.";
        if (LogFolder.Same(full, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)) ||
            LogFolder.Same(full, Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? ""))
            return "Ez a mappa közös rendszermappa – adj meg egy saját almappát benne.";
        // 1.5.13: a jelenleg is használt mappa (mentéskor) a miénk – azt nem vizsgáljuk „idegen, nem üres" mappaként. Eddig a
        // változatlanul hagyott mérési-adatbázis-hely is „nem üres" hibát adott, és emiatt a TELJES mentés elbukott.
        if (current is { Length: > 0 } && LogFolder.Same(full, current)) { normalized = full; return null; }
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !LooksOurs(full))
            return "A mappa nem üres. Adj meg új vagy üres mappát: a jogait a program a SYSTEM-re és a rendszergazdákra szűkíti.";
        normalized = full;
        return null;
    }

    /// <summary>A mappában csak a mi fájljaink vannak-e (napló, mérési adatbázis, mentés-almappák) - ilyen mappa újra választható.</summary>
    private static bool LooksOurs(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
            var dirs = Directory.GetDirectories(dir).Select(Path.GetFileName).ToList();
            bool OurFile(string? f) => f is not null && (LogFolder.Files.Contains(f, StringComparer.OrdinalIgnoreCase)
                || f.StartsWith("metrics.db", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".merge", StringComparison.OrdinalIgnoreCase));
            // mentés-almappa; 1.5.8 óta a napi mérési fájlok (metrics-napok) és a Központban a telephelyek fájljai
            // (metrics-telephelyek) – ezek nélkül a saját mérési mappa induláskor „idegennek" számított, és a szerver
            // csendben visszaállt az alapértelmezett helyre.
            bool OurDir(string? d) => d is not null && (DataBackup.IsBackupDir(d)
                || d.Equals(MetricStore.DaysFolder, StringComparison.OrdinalIgnoreCase) || d.Equals("metrics-telephelyek", StringComparison.OrdinalIgnoreCase));
            return files.All(OurFile) && dirs.All(OurDir);
        }
        catch { return false; }
    }

    private static void Secure(string dir, string dataDir)
    {
        if (LogFolder.IsInside(dir, dataDir) || !OperatingSystem.IsWindows()) return;
        LogFolder.SecureDir?.Invoke(dir);
    }

    /// <summary>
    /// Induláskor, a legelső napló előtt: a naplómappa és a mentések helye a beállításból, a régi (1.5.4 előtti,
    /// az adatmappa gyökerében levő) naplók átköltöznek. secure: a Windows-szolgáltatásnál a mappajogok szűkítése.
    /// </summary>
    public static List<string> ApplyAtStartup(string dataDir, ServerConfig cfg, bool secure)
    {
        var notes = new List<string>();
        if (secure && OperatingSystem.IsWindows()) LogFolder.SecureDir = d => { try { NativeAcl.RestrictToSystemAndAdmins(d); } catch { } };
        var logDir = Validate(Logs, cfg.LogDir, dataDir, out var ln) is null ? ln : "";
        if (cfg.LogDir.Length > 0 && logDir.Length == 0 && !LogFolder.Same(cfg.LogDir, LogFolder.Default(dataDir)))
            notes.Add($"A beállított naplómappa ({cfg.LogDir}) nem használható – az alapértelmezett marad.");
        if (LogFolder.Use(dataDir, logDir) is { } err) notes.Add("A naplók áthelyezése nem sikerült: " + err);
        var bak = Validate(Backup, cfg.BackupDir, dataDir, out var bn) is null ? bn : "";
        if (cfg.BackupDir.Length > 0 && bak.Length == 0 && !LogFolder.Same(cfg.BackupDir, DefaultOf(Backup, dataDir)))
            notes.Add($"A beállított mentésmappa ({cfg.BackupDir}) nem használható – az alapértelmezett marad.");
        if (bak.Length > 0) { try { Directory.CreateDirectory(bak); Secure(bak, dataDir); } catch { bak = ""; } }
        DataBackup.CustomDir = bak;
        return notes;
    }

    /// <summary>A parancssori módokhoz (telepítés, frissítés-csere, diag): a naplók és mentések helye a beállításból,
    /// költöztetés nélkül - a fájlokat csak a szolgáltatás indulása mozgatja.</summary>
    public static void PointOnly(string dataDir)
    {
        try
        {
            var cfg = ServerConfig.Load(dataDir);
            LogFolder.Point(dataDir, Validate(Logs, cfg.LogDir, dataDir, out var l) is null ? l : "");
            DataBackup.CustomDir = Validate(Backup, cfg.BackupDir, dataDir, out var b) is null ? b : "";
        }
        catch { }
    }

    /// <summary>
    /// Induláskor: a metrics.db helye. Ha a beállított helyen még nincs, de az adatmappában van (pl. kézzel írt
    /// beállítás), átköltözik - még a megnyitás előtt, így nincs nyitott fájl.
    /// </summary>
    public static string MetricsDirAtStartup(string dataDir, ServerConfig cfg, List<string> notes)
    {
        if (Validate(MetricsKey, cfg.MetricsDir, dataDir, out var dir) is not null || dir.Length == 0)
        {
            if (cfg.MetricsDir.Length > 0 && !LogFolder.Same(cfg.MetricsDir, dataDir)) notes.Add($"A beállított mérési adatbázis-mappa ({cfg.MetricsDir}) nem használható – az adatmappa marad.");
            return dataDir;
        }
        try
        {
            Directory.CreateDirectory(dir);
            Secure(dir, dataDir);
            var src = Path.Combine(dataDir, "metrics.db");
            var dst = Path.Combine(dir, "metrics.db");
            if (File.Exists(src) && !File.Exists(dst))
                foreach (var ext in new[] { "", "-wal", "-shm" })
                    if (File.Exists(src + ext)) File.Move(src + ext, dst + ext);
            return dir;
        }
        catch (Exception ex) { notes.Add("A mérési adatbázis áthelyezése nem sikerült: " + ex.Message); return dataDir; }
    }

    /// <summary>A mentések (dátumozott almappák) átköltöztetése az új helyre; más meghajtóra másolással.</summary>
    public static string? MoveBackups(string from, string to)
    {
        try
        {
            Directory.CreateDirectory(to);
            if (!Directory.Exists(from) || LogFolder.Same(from, to)) return null;
            foreach (var d in Directory.GetDirectories(from))
            {
                var target = Path.Combine(to, Path.GetFileName(d));
                if (Directory.Exists(target)) continue;
                if (string.Equals(Path.GetPathRoot(Path.GetFullPath(d)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
                    Directory.Move(d, target);
                else { CopyDir(d, target); Directory.Delete(d, true); }
            }
            try { if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from); } catch { }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static long Size(string path, bool recursive)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return Directory.GetFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    // 1 MB = 1024 × 1024 bájt, mint a Windows fájlkezelőjében.
    private static string Mb(long b) => b < 1_048_576 ? $"{b / 1024.0:0.#} KB" : $"{b / 1_048_576.0:0.#} MB";

    /// <summary>A Beállítások „Tárolás” dobozának adatai.</summary>
    public static object Info(string dataDir, ServerConfig cfg, MetricStore metrics)
    {
        var logs = LogFolder.Dir(dataDir);
        var logBytes = LogFolder.Files.Sum(f => Size(Path.Combine(logs, f), false));
        var bak = DataBackup.Root(dataDir);
        var db = Path.Combine(metrics.Dir, "metrics.db");
        return new
        {
            dataDir, logMaxMb = cfg.LogMaxMb,
            dataSize = Mb(Directory.Exists(dataDir) ? Directory.GetFiles(dataDir).Sum(f => Size(f, false)) : 0),
            items = new object[]
            {
                new { key = Logs, label = "Naplók", path = logs, custom = cfg.LogDir, def = LogFolder.Default(dataDir), size = Mb(logBytes),
                      what = "szolgáltatásnapló, műveleti napló (audit), riasztásnapló – a forgatott régiekkel" },
                new { key = Backup, label = "Mentések", path = bak, custom = cfg.BackupDir, def = DefaultOf(Backup, dataDir), size = Mb(Size(bak, true)),
                      what = $"frissítés és kulcscsere előtti mentések (az utolsó 10; most {(Directory.Exists(bak) ? Directory.GetDirectories(bak).Length : 0)} db)" },
                new { key = MetricsKey, label = "Mérési adatbázis", path = metrics.Dir, custom = cfg.MetricsDir, def = dataDir,
                      size = Mb(Size(db, false) + Size(db + "-wal", false)), what = "metrics.db – a monitorozás idősorai (a megőrzés a Monitorozásnál állítható)" },
            }
        };
    }

    private static bool SameDir(string a, string b) => a.Length > 0 && b.Length > 0 && LogFolder.Same(a, b);

    public sealed class Req { public string? LogDir { get; set; } public string? BackupDir { get; set; } public string? MetricsDir { get; set; } public int? LogMaxMb { get; set; } }

    /// <summary>
    /// Mentés a webről: ellenőrzés, átköltöztetés (naplók azonnal, mentések, mérési adatbázis rövid zárolással),
    /// és a beállítás mentése. Hibánál az adott tétel a régi helyén marad. changes: a műveleti naplóba.
    /// </summary>
    public static string? Save(string dataDir, ServerConfig cfg, MetricStore metrics, Req r, List<string> changes)
    {
        // Előbb minden útvonal ellenőrzése: hibánál semmi nem változik. A jelenlegi hely a miénk (nem „idegen" mappa).
        if (Validate(Logs, r.LogDir, dataDir, out var logDir, LogFolder.Dir(dataDir)) is { } e1) return "Naplók: " + e1;
        if (Validate(Backup, r.BackupDir, dataDir, out var bakDir, DataBackup.Root(dataDir)) is { } e2) return "Mentések: " + e2;
        if (Validate(MetricsKey, r.MetricsDir, dataDir, out var metDir, metrics.Dir) is { } e3) return "Mérési adatbázis: " + e3;
        // 1.5.13: a három hely nem lehet ugyanaz a mappa (a fájlok összekeverednének, és egymás költöztetése / takarítása
        // a másikét is érintené).
        if (SameDir(logDir, bakDir)) return "A naplók és a mentések nem lehetnek ugyanabban a mappában – adj meg külön mappát (pl. D:\\Fleet\\Mentesek).";
        if (SameDir(logDir, metDir)) return "A naplók és a mérési adatbázis nem lehetnek ugyanabban a mappában – adj meg külön mappát.";
        if (SameDir(bakDir, metDir)) return "A mentések és a mérési adatbázis nem lehetnek ugyanabban a mappában – adj meg külön mappát.";

        if (r.LogMaxMb is int mb) { var v = Math.Clamp(mb, 1, 100); if (v != cfg.LogMaxMb) { cfg.LogMaxMb = v; LogLimits.SetMb(v); changes.Add($"naplóméret {v} MB"); } }

        if (!LogFolder.Same(LogFolder.Dir(dataDir), logDir.Length > 0 ? logDir : LogFolder.Default(dataDir)))
        {
            if (LogFolder.Use(dataDir, logDir) is { } err) return "A naplók áthelyezése nem sikerült: " + err;
            cfg.LogDir = logDir; changes.Add("naplók: " + LogFolder.Dir(dataDir));
        }
        else cfg.LogDir = logDir;

        var newBak = bakDir.Length > 0 ? bakDir : DefaultOf(Backup, dataDir);
        if (!LogFolder.Same(DataBackup.Root(dataDir), newBak))
        {
            try { Directory.CreateDirectory(newBak); Secure(newBak, dataDir); } catch (Exception ex) { return "A mentésmappa nem hozható létre: " + ex.Message; }
            if (MoveBackups(DataBackup.Root(dataDir), newBak) is { } err) return "A mentések áthelyezése nem sikerült: " + err;
            DataBackup.CustomDir = bakDir; cfg.BackupDir = bakDir; changes.Add("mentések: " + newBak);
        }
        else { DataBackup.CustomDir = bakDir; cfg.BackupDir = bakDir; }

        var newMet = metDir.Length > 0 ? metDir : dataDir;
        if (!LogFolder.Same(metrics.Dir, newMet))
        {
            try { Directory.CreateDirectory(newMet); Secure(newMet, dataDir); } catch (Exception ex) { return "A mérési adatbázis mappája nem hozható létre: " + ex.Message; }
            if (metrics.Relocate(newMet) is { } err) return "A mérési adatbázis áthelyezése nem sikerült: " + err;
            cfg.MetricsDir = metDir; changes.Add("mérési adatbázis: " + newMet);
        }
        else cfg.MetricsDir = metDir;
        return null;
    }
}
