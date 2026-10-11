using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DevToolbox;

public sealed class LicenseEntry
{
    public string Product = "";
    public string Key = "";
    public string Status = "";
    public string Channel = "";
    public string Note = "";
}

public sealed class InstalledApp
{
    public string Name = "";
    public string Version = "";
    public string Publisher = "";
    public string InstallDate = "";
}

[SupportedOSPlatform("windows")]
public static class LicenseInfo
{
    private const uint HKLM = 0x80000002;

    // ================================================================ kulcsok

    public static List<LicenseEntry> Collect(WmiContext ctx)
    {
        var list = new List<LicenseEntry>();

        // ---- 1. Az alaplap firmware-ébe égetett OEM kulcs (MSDM/ACPI tábla).
        //         Gyári Windowsnál ez az igazi, újratelepítéshez használható kulcs.
        foreach (var o in ctx.Query("SELECT * FROM SoftwareLicensingService"))
        {
            var oem = o["OA3xOriginalProductKey"]?.ToString();
            if (!string.IsNullOrWhiteSpace(oem))
            {
                list.Add(new LicenseEntry
                {
                    Product = "Windows – gyári (OEM) kulcs",
                    Key = oem.Trim(),
                    Status = "firmware-ben tárolva",
                    Channel = "OEM / MSDM",
                    Note = "Az alaplapba égetett kulcs. Újratelepítéskor a Windows ezt " +
                           "automatikusan felismeri. Ez a gép „igazi” kulcsa."
                });
            }
            else
            {
                list.Add(new LicenseEntry
                {
                    Product = "Windows – gyári (OEM) kulcs",
                    Key = "—",
                    Status = "nincs",
                    Note = "Az alaplapon nincs beégetett kulcs. Ez normális dobozos, MSDN, " +
                           "volumen licenc, vagy frissítéssel telepített Windows esetén."
                });
            }
            break;
        }

        // ---- 2. A registryben tárolt telepítési azonosítóból visszafejtett kulcs.
        try
        {
            var raw = ReadDigitalProductId(ctx);
            if (raw is { Length: > 66 })
            {
                var key = DecodeProductKey(raw);
                if (!string.IsNullOrWhiteSpace(key))
                    list.Add(new LicenseEntry
                    {
                        Product = "Windows – telepített kulcs",
                        Key = key,
                        Status = "registryből visszafejtve",
                        Channel = "DigitalProductId",
                        Note = "FIGYELEM: Windows 10/11 alatt ez gyakran nem a vásárolt kulcs, " +
                               "hanem egy általános telepítési kulcs, mert az aktiválás digitális " +
                               "licenchez (Microsoft-fiók / hardverazonosító) kötött. " +
                               "Ha van OEM kulcs a fenti sorban, azt használd."
                    });
            }
        }
        catch { }

        // ---- 3. Aktiválási állapot termékenként (Windows és Office is idelátszik).
        foreach (var o in ctx.Query(
                     "SELECT Name, Description, PartialProductKey, LicenseStatus, ProductKeyChannel, " +
                     "GracePeriodRemaining FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
        {
            var name = o["Name"]?.ToString() ?? "ismeretlen termék";
            var partial = o["PartialProductKey"]?.ToString() ?? "";
            var status = LicenseStatusText(HardwareInfo.ToUInt(o["LicenseStatus"]));
            var channel = o["ProductKeyChannel"]?.ToString() ?? "";
            var grace = HardwareInfo.ToUInt(o["GracePeriodRemaining"]);

            list.Add(new LicenseEntry
            {
                Product = name,
                Key = partial.Length > 0 ? "XXXXX-XXXXX-XXXXX-XXXXX-" + partial : "—",
                Status = status + (grace > 0 ? $" ({grace / 60} nap türelmi idő)" : ""),
                Channel = channel,
                Note = "A Windows csak a kulcs utolsó 5 karakterét tárolja visszafejthető módon. " +
                       "Ez az azonosításhoz elég, újratelepítéshez nem."
            });
        }

        if (list.Count == 0)
            list.Add(new LicenseEntry
            {
                Product = "Nincs adat",
                Key = "—",
                Status = "sikertelen lekérdezés",
                Note = "A licencszolgáltatás nem válaszolt. Próbáld rendszergazdaként futtatni."
            });

        return list;
    }

    private static byte[]? ReadDigitalProductId(WmiContext ctx)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        if (!ctx.IsRemote)
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            return k?.GetValue("DigitalProductId") as byte[];
        }

        // Távoli gépen a registryt a StdRegProv WMI szolgáltatón át olvassuk.
        try
        {
            var scope = ctx.BuildScope(@"root\default");
            scope.Connect();
            using var reg = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);
            using var inParams = reg.GetMethodParameters("GetBinaryValue");
            inParams["hDefKey"] = HKLM;
            inParams["sSubKeyName"] = subKey;
            inParams["sValueName"] = "DigitalProductId";
            using var outParams = reg.InvokeMethod("GetBinaryValue", inParams, null);
            return outParams?["uValue"] as byte[];
        }
        catch { return null; }
    }

    /// <summary>
    /// A DigitalProductId base-24 visszafejtése (Windows 8 és újabb változat,
    /// az "N" karakter beszúrásával).
    /// </summary>
    public static string DecodeProductKey(byte[] digitalProductId)
    {
        const string digits = "BCDFGHJKMPQRTVWXY2346789";
        const int keyOffset = 52;

        // Nem módosítjuk az eredeti tömböt.
        var data = (byte[])digitalProductId.Clone();

        var isWin8 = (byte)((data[66] / 6) & 1);
        data[66] = (byte)((data[66] & 0xF7) | (isWin8 & 2) * 4);

        var key = "";
        var last = 0;

        for (var i = 24; i >= 0; i--)
        {
            var current = 0;
            for (var j = 14; j >= 0; j--)
            {
                current = current * 256;
                current = data[j + keyOffset] + current;
                data[j + keyOffset] = (byte)(current / 24);
                current %= 24;
                last = current;
            }
            key = digits[current] + key;
        }

        if (last < key.Length)
        {
            var part1 = key[1..(last + 1)];
            var part2 = key[(last + 1)..];
            key = part1 + "N" + part2;
        }

        for (var i = 5; i < key.Length; i += 6) key = key.Insert(i, "-");
        return key;
    }

    // ================================================================ Office kulcsok

    private static readonly (string Ver, string Label)[] OfficeVersions =
    {
        ("14.0", "Office 2010"), ("15.0", "Office 2013"), ("16.0", "Office 2016 / 2019 / 365")
    };

    /// <summary>
    /// Az Office a sajat Registration agaban tarolja a DigitalProductID-t.
    /// A 2010/2013 a regi base-24 valtozatot hasznalja, a 2016+ az "N" beszurasosat.
    /// </summary>
    public static List<LicenseEntry> CollectOffice()
    {
        var list = new List<LicenseEntry>();

        foreach (var root in new[] { @"SOFTWARE\Microsoft\Office", @"SOFTWARE\WOW6432Node\Microsoft\Office" })
        foreach (var (ver, label) in OfficeVersions)
        {
            var path = $@"{root}\{ver}\Registration";
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) continue;

            foreach (var sub in key.GetSubKeyNames())
            {
                using var reg = key.OpenSubKey(sub);
                if (reg?.GetValue("DigitalProductID") is not byte[] raw || raw.Length < 67) continue;

                var name = reg.GetValue("ProductName") as string
                           ?? reg.GetValue("ConvertToEdition") as string
                           ?? label;

                string decoded;
                try
                {
                    decoded = ver == "16.0" ? DecodeProductKey(raw) : DecodeProductKeyLegacy(raw);
                }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(decoded) || decoded.Replace("-", "").Length != 25) continue;
                if (list.Any(e => e.Key == decoded)) continue;

                list.Add(new LicenseEntry
                {
                    Product = name,
                    Key = decoded,
                    Status = "registryből visszafejtve",
                    Channel = label,
                    Note = ver == "16.0"
                        ? "Office 2016 és újabb: ez gyakran csak a telepítéshez használt " +
                          "általános kulcs. Microsoft 365 előfizetésnél nincs is valódi kulcs – " +
                          "az aktiválás fiókhoz kötött."
                        : "A telepítéskor megadott kulcs. Újratelepítéshez általában használható."
                });
            }
        }

        return list;
    }

    /// <summary>A Windows 8 elotti / Office 2010-2013 valtozat: nincs "N" beszuras.</summary>
    public static string DecodeProductKeyLegacy(byte[] digitalProductId)
    {
        const string digits = "BCDFGHJKMPQRTVWXY2346789";
        const int keyOffset = 52;

        var data = (byte[])digitalProductId.Clone();
        var key = "";

        for (var i = 24; i >= 0; i--)
        {
            var current = 0;
            for (var j = 14; j >= 0; j--)
            {
                current = current * 256;
                current = data[j + keyOffset] + current;
                data[j + keyOffset] = (byte)(current / 24);
                current %= 24;
            }
            key = digits[current] + key;
        }

        for (var i = 5; i < key.Length; i += 6) key = key.Insert(i, "-");
        return key;
    }

    // ================================================================ általános registry-keresés

    private static readonly string[] KeyValueNames =
    {
        "ProductKey", "ProductID", "SerialNumber", "Serial", "SerialKey",
        "LicenseKey", "LicenceKey", "License", "Licence", "RegKey",
        "RegistrationKey", "RegisteredKey", "CDKey", "CD Key", "PIDKEY",
        "ActivationKey", "AuthCode", "InstallKey"
    };

    /// <summary>Ezeket kihagyjuk: hatalmasak es csak zajt adnanak.</summary>
    private static readonly string[] SkipPaths =
    {
        @"\Classes", @"\CurrentVersion\Uninstall", @"\Cryptography",
        @"\Windows NT\CurrentVersion\Fonts", @"\Installer"
    };

    /// <summary>
    /// Vegigmegy a registry szoftver-again, es osszeszedi a kulcsnak tuno ertekeket.
    /// Idokorlatos, hogy ne akassza meg a feluletet.
    /// </summary>
    public static List<LicenseEntry> ScanRegistry(int maxResults = 150, int timeoutSeconds = 12)
    {
        var found = new List<LicenseEntry>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var roots = new (RegistryKey Hive, string Path, string Label)[]
        {
            (Registry.LocalMachine, @"SOFTWARE", "HKLM"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node", "HKLM (32 bit)"),
            (Registry.CurrentUser,  @"SOFTWARE", "HKCU")
        };

        foreach (var (hive, path, label) in roots)
        {
            if (found.Count >= maxResults || sw.Elapsed.TotalSeconds > timeoutSeconds) break;
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is not null)
                    Walk(key, path, label, found, maxResults, sw, timeoutSeconds, 0);
            }
            catch { }
        }

        return found;
    }

    private static void Walk(RegistryKey key, string path, string hive,
        List<LicenseEntry> found, int max, System.Diagnostics.Stopwatch sw, int timeout, int depth)
    {
        if (depth > 4 || found.Count >= max || sw.Elapsed.TotalSeconds > timeout) return;
        if (SkipPaths.Any(sp => path.Contains(sp, StringComparison.OrdinalIgnoreCase))) return;

        try
        {
            foreach (var valueName in key.GetValueNames())
            {
                if (!KeyValueNames.Contains(valueName, StringComparer.OrdinalIgnoreCase)) continue;
                if (key.GetValue(valueName) is not string value) continue;
                if (!LooksLikeKey(value)) continue;
                if (found.Any(f => f.Key == value)) continue;

                found.Add(new LicenseEntry
                {
                    Product = FriendlyName(path),
                    Key = value.Trim(),
                    Status = valueName,
                    Channel = hive,
                    Note = "A szoftver a registryben tárolja. Az érték lehet licenckulcs, " +
                           "de sorozatszám vagy telepítési azonosító is – ellenőrizd a " +
                           "gyártó felületén. Útvonal: " + path
                });

                if (found.Count >= max) return;
            }

            foreach (var sub in key.GetSubKeyNames())
            {
                if (found.Count >= max || sw.Elapsed.TotalSeconds > timeout) return;
                try
                {
                    using var child = key.OpenSubKey(sub);
                    if (child is not null)
                        Walk(child, path + "\\" + sub, hive, found, max, sw, timeout, depth + 1);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Kiszuri a verziószámokat, útvonalakat, GUID-okat es egyeb zajt.</summary>
    private static bool LooksLikeKey(string v)
    {
        v = v.Trim();
        if (v.Length is < 8 or > 80) return false;
        if (v.Contains('\\') || v.Contains('/') || v.Contains("://")) return false;
        if (v.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
            v.Contains(".dll", StringComparison.OrdinalIgnoreCase)) return false;

        // Tiszta GUID: tul sok van beloluk, es szinte soha nem licenckulcs.
        if (Guid.TryParse(v, out _)) return false;

        if (!v.Any(char.IsDigit)) return false;
        if (v.Distinct().Count() < 4) return false;          // pl. 0000000000

        var allowed = v.Count(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
        if (allowed < v.Length - 2) return false;            // legfeljebb pár egyéb karakter

        // Csak szamok es pontok -> verziószám
        if (v.All(c => char.IsDigit(c) || c == '.')) return false;

        return true;
    }

    private static string FriendlyName(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !p.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                                 && !p.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
                        .ToList();
        if (parts.Count == 0) return path;
        return parts.Count <= 2 ? string.Join(" – ", parts)
                                : string.Join(" – ", parts.Take(2));
    }

    private static string LicenseStatusText(uint s) => s switch
    {
        0 => "nincs licencelve",
        1 => "aktiválva",
        2 => "türelmi idő (első telepítés)",
        3 => "türelmi idő (lejárt)",
        4 => "nem eredeti – türelmi idő",
        5 => "értesítési állapot (nem aktivált)",
        6 => "kiterjesztett türelmi idő",
        _ => "ismeretlen (" + s + ")"
    };

    // ================================================================ telepített szoftverek

    private static readonly string[] UninstallPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public static List<InstalledApp> GetInstalledApps(WmiContext ctx, int max = 500)
    {
        return ctx.IsRemote ? RemoteApps(ctx, max) : LocalApps(max);
    }

    private static List<InstalledApp> LocalApps(int max)
    {
        var list = new List<InstalledApp>();

        foreach (var path in UninstallPaths)
        {
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root is null) continue;

            foreach (var name in root.GetSubKeyNames())
            {
                if (list.Count >= max) break;
                using var k = root.OpenSubKey(name);
                if (k is null) continue;

                var display = k.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(display)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;

                list.Add(new InstalledApp
                {
                    Name = display.Trim(),
                    Version = k.GetValue("DisplayVersion")?.ToString() ?? "",
                    Publisher = k.GetValue("Publisher")?.ToString() ?? "",
                    InstallDate = FormatDate(k.GetValue("InstallDate")?.ToString())
                });
            }
        }

        return Dedupe(list);
    }

    private static List<InstalledApp> RemoteApps(WmiContext ctx, int max)
    {
        var list = new List<InstalledApp>();
        try
        {
            var scope = ctx.BuildScope(@"root\default");
            scope.Connect();
            using var reg = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);

            foreach (var path in UninstallPaths)
            {
                using var enumIn = reg.GetMethodParameters("EnumKey");
                enumIn["hDefKey"] = HKLM;
                enumIn["sSubKeyName"] = path;
                using var enumOut = reg.InvokeMethod("EnumKey", enumIn, null);
                if (enumOut?["sNames"] is not string[] names) continue;

                foreach (var name in names)
                {
                    if (list.Count >= max) break;

                    var display = GetRemoteString(reg, path + "\\" + name, "DisplayName");
                    if (string.IsNullOrWhiteSpace(display)) continue;

                    list.Add(new InstalledApp
                    {
                        Name = display.Trim(),
                        Version = GetRemoteString(reg, path + "\\" + name, "DisplayVersion") ?? "",
                        Publisher = GetRemoteString(reg, path + "\\" + name, "Publisher") ?? "",
                        InstallDate = FormatDate(GetRemoteString(reg, path + "\\" + name, "InstallDate"))
                    });
                }
            }
        }
        catch { }

        return Dedupe(list);
    }

    private static string? GetRemoteString(ManagementClass reg, string subKey, string valueName)
    {
        try
        {
            using var inP = reg.GetMethodParameters("GetStringValue");
            inP["hDefKey"] = HKLM;
            inP["sSubKeyName"] = subKey;
            inP["sValueName"] = valueName;
            using var outP = reg.InvokeMethod("GetStringValue", inP, null);
            return outP?["sValue"] as string;
        }
        catch { return null; }
    }

    private static List<InstalledApp> Dedupe(List<InstalledApp> list) =>
        list.GroupBy(a => a.Name + "|" + a.Version)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 8) return raw ?? "";
        return $"{raw[..4]}.{raw.Substring(4, 2)}.{raw.Substring(6, 2)}";
    }
}
