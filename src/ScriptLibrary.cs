using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToolbox;

public enum ScriptKind
{
    // FIGYELEM: a JSON az enumot szamkent tarolja, ezert a meglevo sorrend
    // nem valtoztathato - uj tipus csak a vegere kerulhet.
    Batch = 0,
    PowerShell = 1,
    VBScript = 2,
    Python = 3,
    Unknown = 4,
    Bash = 5,
    PowerShellCore = 6,
    Sql = 7
}

public enum ScriptPlatform { Windows, Linux, Tobbplatformos, Egyeb }

public sealed class ScriptItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Általános";
    /// <summary>Csak fajlnev - a tenyleges fajl a scripts mappaban van.</summary>
    public string FileName { get; set; } = "";
    public ScriptKind Kind { get; set; } = ScriptKind.Unknown;
    public string Arguments { get; set; } = "";
    public bool RunAsAdmin { get; set; }
    /// <summary>Kimenet kodolasa: utf-8 (alap), cp852, cp1250, cp437.</summary>
    public string OutputEncoding { get; set; } = "utf-8";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastRun { get; set; }
    public int RunCount { get; set; }

    /// <summary>A programmal szallitott szkript (frissiteskor cserelheto).</summary>
    public bool IsSample { get; set; }

    [JsonIgnore]
    public string FullPath => Path.Combine(AppPaths.ScriptsDir, FileName);

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        ScriptKind.Batch => "Batch",
        ScriptKind.PowerShell => "PowerShell",
        ScriptKind.PowerShellCore => "PowerShell 7",
        ScriptKind.VBScript => "VBScript",
        ScriptKind.Python => "Python",
        ScriptKind.Bash => "Bash",
        ScriptKind.Sql => "SQL",
        _ => "Egyéb"
    };

    [JsonIgnore]
    public ScriptPlatform Platform => PlatformOf(Kind);

    [JsonIgnore]
    public string PlatformLabel => Platform switch
    {
        ScriptPlatform.Windows => "Windows",
        ScriptPlatform.Linux => "Linux / Unix",
        ScriptPlatform.Tobbplatformos => "Többplatformos",
        _ => "Egyéb"
    };

    public static ScriptPlatform PlatformOf(ScriptKind k) => k switch
    {
        ScriptKind.Batch or ScriptKind.PowerShell or ScriptKind.VBScript => ScriptPlatform.Windows,
        ScriptKind.Bash => ScriptPlatform.Linux,
        ScriptKind.Python or ScriptKind.PowerShellCore => ScriptPlatform.Tobbplatformos,
        _ => ScriptPlatform.Egyeb
    };

    public static string ExtensionOf(ScriptKind k) => k switch
    {
        ScriptKind.Batch => ".bat",
        ScriptKind.PowerShell or ScriptKind.PowerShellCore => ".ps1",
        ScriptKind.VBScript => ".vbs",
        ScriptKind.Python => ".py",
        ScriptKind.Bash => ".sh",
        ScriptKind.Sql => ".sql",
        _ => ".txt"
    };

    public static ScriptKind KindFromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bat" or ".cmd" => ScriptKind.Batch,
            ".ps1" => ScriptKind.PowerShell,
            ".vbs" => ScriptKind.VBScript,
            ".py" => ScriptKind.Python,
            ".sh" or ".bash" => ScriptKind.Bash,
            ".sql" => ScriptKind.Sql,
            _ => ScriptKind.Unknown
        };
}

public sealed class ScriptLibrary
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public List<ScriptItem> Items { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.LibraryFile)) { Items = new(); return; }
            var json = File.ReadAllText(AppPaths.LibraryFile);
            Items = JsonSerializer.Deserialize<List<ScriptItem>>(json, JsonOpts) ?? new();
        }
        catch
        {
            Items = new();
        }

        // Elveszett fajlok kiszurese (pl. kezzel torolt szkript).
        Items.RemoveAll(i => !File.Exists(i.FullPath));
    }

    public void Save()
    {
        AppPaths.EnsureDataDirs();
        var json = JsonSerializer.Serialize(Items, JsonOpts);
        File.WriteAllText(AppPaths.LibraryFile, json);
    }

    /// <summary>Meglevo fajl beimportalasa a konyvtarba (masolassal).</summary>
    public ScriptItem ImportFile(string sourcePath, string? name = null, string category = "Általános")
    {
        AppPaths.EnsureDataDirs();

        var fileName = MakeUniqueFileName(Path.GetFileName(sourcePath));
        var dest = Path.Combine(AppPaths.ScriptsDir, fileName);
        File.Copy(sourcePath, dest, overwrite: false);

        var item = new ScriptItem
        {
            Name = name ?? Path.GetFileNameWithoutExtension(sourcePath),
            FileName = fileName,
            Kind = ScriptItem.KindFromExtension(sourcePath),
            Category = category
        };
        Items.Add(item);
        Save();
        return item;
    }

    /// <summary>Uj, ures szkript letrehozasa.</summary>
    public ScriptItem CreateNew(string name, ScriptKind kind, string content, string category = "Általános")
    {
        AppPaths.EnsureDataDirs();

        var ext = ScriptItem.ExtensionOf(kind);

        var safe = SanitizeFileName(string.IsNullOrWhiteSpace(name) ? "szkript" : name);
        var fileName = MakeUniqueFileName(safe + ext);
        var dest = Path.Combine(AppPaths.ScriptsDir, fileName);

        // BOM nelkuli UTF-8 a batch fajloknak (a cmd.exe nem szereti a BOM-ot),
        // es mindig CRLF sorvegek.
        File.WriteAllText(dest, Normalize(content, kind), Encoder(kind));

        var item = new ScriptItem
        {
            Name = name,
            FileName = fileName,
            Kind = kind,
            Category = category
        };
        Items.Add(item);
        Save();
        return item;
    }

    public void Delete(ScriptItem item, bool deleteFile = true)
    {
        Items.Remove(item);
        if (deleteFile)
        {
            try { if (File.Exists(item.FullPath)) File.Delete(item.FullPath); } catch { }
        }
        Save();
    }

    public IEnumerable<string> Categories =>
        Items.Select(i => i.Category)
             .Where(c => !string.IsNullOrWhiteSpace(c))
             .Distinct()
             .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase);

    // ================================================================ minta szkriptek

    public static int SampleVersion => ScriptSamples.Version;
    public const string LegacyCategory = "Minta";

    /// <summary>Elso induláskor felveszi a szallitott szkripteket.</summary>
    public bool SeedSamples()
    {
        if (Items.Count > 0) return false;

        foreach (var sm in ScriptSamples.All) TryCreate(sm);
        Save();
        return true;
    }

    /// <summary>
    /// Uj vagy elavult szallitott szkriptek potlasa. A felhasznalo sajat
    /// szkriptjeihez nem nyul: csak azokat erinti, amiket mi hoztunk letre.
    /// </summary>
    public int RefreshSamples()
    {
        int changed = 0;

        foreach (var sm in ScriptSamples.All)
        {
            var item = Items.FirstOrDefault(i =>
                i.Name.Equals(sm.Name, StringComparison.OrdinalIgnoreCase) &&
                (i.IsSample || i.Category.Equals(LegacyCategory, StringComparison.OrdinalIgnoreCase)));

            if (item is null)
            {
                if (TryCreate(sm)) changed++;
                continue;
            }

            // Regi telepitesbol atvett minta: jeloljuk meg, es tegyuk a helyere.
            item.IsSample = true;
            if (!item.Category.Equals(sm.Category, StringComparison.OrdinalIgnoreCase))
                item.Category = sm.Category;

            try
            {
                var current = File.Exists(item.FullPath) ? File.ReadAllText(item.FullPath) : "";
                var wanted = Normalize(sm.Content, sm.Kind);

                if (current == wanted) continue;

                var outdated = current.Trim().Length == 0 ||
                               ObsoleteMarkers.Any(m =>
                                   current.Contains(m, StringComparison.OrdinalIgnoreCase));
                if (!outdated) continue;

                File.WriteAllText(item.FullPath, wanted, Encoder(sm.Kind));
                item.Description = sm.Description;
                item.RunAsAdmin = sm.RunAsAdmin;
                changed++;
            }
            catch { }
        }

        if (changed > 0) Save();
        return changed;
    }

    private bool TryCreate(ScriptSample sm)
    {
        try
        {
            var item = CreateNew(sm.Name, sm.Kind, sm.Content, sm.Category);
            item.Description = sm.Description;
            item.RunAsAdmin = sm.RunAsAdmin;
            item.IsSample = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Olyan parancsok, amiket a Windows ujabb kiadasai mar nem ismernek.</summary>
    private static readonly string[] ObsoleteMarkers = { "wmic" };

    /// <summary>
    /// Sorvegek tipus szerint. A .bat fajlokat a cmd.exe csak CRLF-fel dolgozza fel
    /// megbizhatoan - a bash viszont HIBARA FUT a CR karaktertol
    /// ("bad interpreter: /bin/bash^M"), ezert ott LF kell.
    /// </summary>
    internal static string Normalize(string content, ScriptKind kind = ScriptKind.Batch)
    {
        var lf = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return UsesUnixLineEndings(kind) ? lf : lf.Replace("\n", "\r\n");
    }

    internal static bool UsesUnixLineEndings(ScriptKind kind) =>
        kind is ScriptKind.Bash or ScriptKind.Sql;

    /// <summary>
    /// BOM nelkuli UTF-8 mindenhol, ahol az ertelmezo megakadna rajta:
    /// a cmd.exe es a bash egyarant elszall a BOM-tol.
    /// </summary>
    private static System.Text.Encoding Encoder(ScriptKind kind) =>
        new System.Text.UTF8Encoding(
            kind is not (ScriptKind.Batch or ScriptKind.Bash or ScriptKind.Sql));

    private static string MakeUniqueFileName(string desired)
    {
        var name = SanitizeFileName(Path.GetFileNameWithoutExtension(desired));
        var ext = Path.GetExtension(desired);
        var candidate = name + ext;
        var n = 1;
        while (File.Exists(Path.Combine(AppPaths.ScriptsDir, candidate)))
            candidate = $"{name}_{n++}{ext}";
        return candidate;
    }

    private static string SanitizeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
