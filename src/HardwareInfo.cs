using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace DevToolbox;

public sealed record HwRow(string Key, string Value);
public sealed class HwSection
{
    public string Title { get; init; } = "";

    /// <summary>Alapbol csukva jelenjen-e meg a lista csoportja.</summary>
    public bool Collapsed { get; init; }
    public List<HwRow> Rows { get; } = new();
    public void Add(string k, object? v)
    {
        var s = v?.ToString();
        if (!string.IsNullOrWhiteSpace(s)) Rows.Add(new HwRow(k, s.Trim()));
    }
}

[SupportedOSPlatform("windows")]
public static class HardwareInfo
{
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static List<HwSection> Collect() => Collect(WmiContext.Local);

    public static List<HwSection> Collect(WmiContext ctx)
    {
        var list = new List<HwSection>();
        list.Add(System(ctx));
        list.Add(Cpu(ctx));
        list.Add(Memory(ctx));
        list.Add(Gpu(ctx));
        list.Add(Disks(ctx));
        list.Add(Network(ctx));
        return list.Where(s => s.Rows.Count > 0).ToList();
    }

    // ---------------------------------------------------------------- rendszer

    private static HwSection System(WmiContext ctx)
    {
        var s = new HwSection { Title = "Rendszer" };
        if (ctx.IsRemote)
        {
            s.Add("Gépnév", ctx.Host);
            s.Add("Lekérdezés", "távoli (WMI)");
        }
        else
        {
            s.Add("Gépnév", Environment.MachineName);
            s.Add("Felhasználó", Environment.UserName);
            s.Add("Jogosultság", IsElevated() ? "Rendszergazda" : "Normál felhasználó");
        }

        foreach (var o in ctx.Query("SELECT * FROM Win32_OperatingSystem"))
        {
            s.Add("Operációs rendszer", o["Caption"]);
            s.Add("Verzió / build", $"{o["Version"]} ({o["BuildNumber"]})");
            s.Add("Architektúra", o["OSArchitecture"]);
            var boot = ToDate(o["LastBootUpTime"]);
            if (boot.HasValue)
            {
                var up = DateTime.Now - boot.Value;
                s.Add("Utolsó indítás", boot.Value.ToString("yyyy.MM.dd HH:mm"));
                s.Add("Üzemidő", $"{(int)up.TotalDays} nap {up.Hours} óra {up.Minutes} perc");
            }
            break;
        }

        foreach (var o in ctx.Query("SELECT * FROM Win32_ComputerSystem"))
        {
            s.Add("Gyártó", o["Manufacturer"]);
            s.Add("Modell", o["Model"]);
            s.Add("Rendszer típusa", o["SystemType"]);
            break;
        }

        foreach (var o in ctx.Query("SELECT * FROM Win32_BaseBoard"))
        {
            s.Add("Alaplap", $"{o["Manufacturer"]} {o["Product"]}");
            break;
        }

        foreach (var o in ctx.Query("SELECT * FROM Win32_BIOS"))
        {
            s.Add("BIOS", $"{o["Manufacturer"]} {o["SMBIOSBIOSVersion"]}");
            var d = ToDate(o["ReleaseDate"]);
            if (d.HasValue) s.Add("BIOS dátum", d.Value.ToString("yyyy.MM.dd"));
            break;
        }

        return s;
    }

    // ---------------------------------------------------------------- cpu

    private static HwSection Cpu(WmiContext ctx)
    {
        var s = new HwSection { Title = "Processzor" };
        foreach (var o in ctx.Query("SELECT * FROM Win32_Processor"))
        {
            s.Add("Megnevezés", o["Name"]);
            s.Add("Gyártó", o["Manufacturer"]);
            s.Add("Fizikai magok", o["NumberOfCores"]);
            s.Add("Logikai szálak", o["NumberOfLogicalProcessors"]);
            s.Add("Alap órajel", $"{o["MaxClockSpeed"]} MHz");
            s.Add("Foglalat", o["SocketDesignation"]);
            if (o["L2CacheSize"] is uint l2 && l2 > 0) s.Add("L2 gyorsítótár", $"{l2 / 1024.0:0.#} MB");
            if (o["L3CacheSize"] is uint l3 && l3 > 0) s.Add("L3 gyorsítótár", $"{l3 / 1024.0:0.#} MB");
            s.Add("Virtualizáció", o["VirtualizationFirmwareEnabled"] is bool b
                ? (b ? "engedélyezve" : "letiltva") : null);
            break;
        }
        return s;
    }

    // ---------------------------------------------------------------- memoria

    private static HwSection Memory(WmiContext ctx)
    {
        var s = new HwSection { Title = "Memória" };

        ulong total = 0, free = 0;
        foreach (var o in ctx.Query("SELECT * FROM Win32_OperatingSystem"))
        {
            total = ToULong(o["TotalVisibleMemorySize"]) * 1024;
            free = ToULong(o["FreePhysicalMemory"]) * 1024;
            break;
        }
        if (total > 0)
        {
            var used = total - free;
            s.Add("Összes RAM", Fmt.Bytes(total));
            s.Add("Használatban", $"{Fmt.Bytes(used)}  ({used * 100.0 / total:0.#}%)");
            s.Add("Szabad", Fmt.Bytes(free));
        }

        int i = 1;
        foreach (var o in ctx.Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            var cap = ToULong(o["Capacity"]);
            var speed = o["ConfiguredClockSpeed"] ?? o["Speed"];
            s.Add($"{i}. modul",
                $"{Fmt.Bytes(cap)} · {speed} MT/s · {o["Manufacturer"]} {o["PartNumber"]}".Replace("  ", " ").Trim());
            i++;
        }

        return s;
    }

    // ---------------------------------------------------------------- gpu

    private static HwSection Gpu(WmiContext ctx)
    {
        var s = new HwSection { Title = "Videokártya" };
        int idx = 0;
        foreach (var o in ctx.Query("SELECT * FROM Win32_VideoController"))
        {
            var prefix = idx == 0 ? "" : $"({idx + 1}) ";
            s.Add(prefix + "Megnevezés", o["Name"]);

            var vram = ctx.IsRemote ? 0UL : ReadVramFromRegistry(idx);
            if (vram == 0) vram = ToULong(o["AdapterRAM"]);
            if (vram > 0) s.Add(prefix + "Videómemória", Fmt.Bytes(vram));

            s.Add(prefix + "Illesztőprogram", o["DriverVersion"]);
            var dd = ToDate(o["DriverDate"]);
            if (dd.HasValue) s.Add(prefix + "Driver dátuma", dd.Value.ToString("yyyy.MM.dd"));

            var h = o["CurrentHorizontalResolution"];
            var v = o["CurrentVerticalResolution"];
            if (h is not null && v is not null && ToULong(h) > 0)
                s.Add(prefix + "Felbontás", $"{h} × {v} @ {o["CurrentRefreshRate"]} Hz");

            idx++;
        }
        return s;
    }

    private static ulong ReadVramFromRegistry(int index)
    {
        // A Win32_VideoController.AdapterRAM 4 GB-nal levag, ezert a registrybol olvassuk.
        try
        {
            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\" + index.ToString("D4"));
            var val = baseKey?.GetValue("HardwareInformation.qwMemorySize");
            if (val is long l && l > 0) return (ulong)l;
        }
        catch { }
        return 0;
    }

    // ---------------------------------------------------------------- lemezek

    private static HwSection Disks(WmiContext ctx)
    {
        var s = new HwSection { Title = "Tárolók (fizikai)" };

        // Reszletes adatok az uj Storage WMI namespace-bol (media tipus, allapot).
        var details = new Dictionary<string, (string Media, string Bus, string Health, ulong Size, uint Rpm)>();
        foreach (var o in ctx.Query("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            var name = o["FriendlyName"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            details[name] = (
                MediaTypeName(ToULong(o["MediaType"]), ToUInt(o["SpindleSpeed"])),
                BusTypeName(ToULong(o["BusType"])),
                HealthName(ToULong(o["HealthStatus"])),
                ToULong(o["Size"]),
                ToUInt(o["SpindleSpeed"]));
        }

        int i = 1;
        foreach (var o in ctx.Query("SELECT * FROM Win32_DiskDrive"))
        {
            var model = (o["Model"]?.ToString() ?? "ismeretlen").Trim();
            var size = ToULong(o["Size"]);

            details.TryGetValue(model, out var d);
            var media = string.IsNullOrEmpty(d.Media) ? GuessMedia(model) : d.Media;
            var parts = new List<string> { Fmt.Bytes(size) };
            if (!string.IsNullOrEmpty(media)) parts.Add(media);
            if (!string.IsNullOrEmpty(d.Bus)) parts.Add(d.Bus);
            if (d.Rpm > 0) parts.Add($"{d.Rpm} RPM");
            if (!string.IsNullOrEmpty(d.Health)) parts.Add("állapot: " + d.Health);

            s.Add($"{i}. {model}", string.Join(" · ", parts));
            s.Add("     partíciók", o["Partitions"]);
            i++;
        }

        return s;
    }

    private static string GuessMedia(string model)
    {
        var m = model.ToUpperInvariant();
        if (m.Contains("NVME") || m.Contains("SSD")) return "SSD";
        return "";
    }

    private static string MediaTypeName(ulong t, uint rpm) => t switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => rpm == 0 ? "SSD" : rpm > 0 ? "HDD" : ""
    };

    private static string BusTypeName(ulong t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA",
        6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
        11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", _ => ""
    };

    private static string HealthName(ulong h) => h switch
    {
        0 => "kifogástalan", 1 => "figyelmeztetés", 2 => "hibás", _ => ""
    };

    // ---------------------------------------------------------------- halozat

    private static HwSection Network(WmiContext ctx)
    {
        var s = new HwSection { Title = "Hálózat" };
        foreach (var o in ctx.Query(
            "SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True AND NetEnabled=True"))
        {
            var speed = ToULong(o["Speed"]);
            var sp = speed > 0 ? $" · {speed / 1_000_000} Mbit/s" : "";
            s.Add(o["NetConnectionID"]?.ToString() ?? "Adapter",
                $"{o["Name"]}{sp} · MAC: {o["MACAddress"]}");
        }
        return s;
    }

    // ---------------------------------------------------------------- WMI segedek

    internal static ulong ToULong(object? o)
    {
        try { return o is null ? 0 : Convert.ToUInt64(o); } catch { return 0; }
    }

    internal static uint ToUInt(object? o)
    {
        try { return o is null ? 0 : Convert.ToUInt32(o); } catch { return 0; }
    }

    internal static DateTime? ToDate(object? o)
    {
        try
        {
            var s = o?.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return ManagementDateTimeConverter.ToDateTime(s);
        }
        catch { return null; }
    }
}

// ===================================================================== szenzorok

/// <summary>
/// Elo szenzoradatok a LibreHardwareMonitor-bol.
///
/// FONTOS: a konyvtar kernel-illesztoje MEGOSZTOTT eroforras. Ha ket Computer peldany
/// van nyitva es az egyiket lezarjak, a masik is elveszti a hardvert. Ezert az egesz
/// programban EGYETLEN peldany letezik - a SMART panel is ezt hasznalja.
/// </summary>
public sealed class SensorReader : IDisposable
{
    private readonly Computer _computer;
    private readonly object _lock = new();
    private bool _opened;

    // A tarolo frissitese SMART-olvasast indit, ami lassu (0,1-0,5 mp lemezenkent),
    // es a lemezhofok ugysem valtozik masodpercenkent.
    private static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastStorage = DateTime.MinValue;

    public SensorReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true
        };
    }

    public string? OpenError { get; private set; }
    public string Diagnostics { get; private set; } = "";
    public bool DriverLoaded { get; private set; }
    public int HardwareCount { get; private set; }

    public void Open()
    {
        lock (_lock)
        {
            if (_opened) return;
            try { _computer.Open(); _opened = true; }
            catch (Exception ex) { OpenError = ex.Message; return; }
        }

        try
        {
            var readings = Read(forceStorage: true);
            var temps = readings.Count(r => r.Type == "Hőmérséklet");
            HardwareCount = _computer.Hardware.Count();
            DriverLoaded = temps > 0;

            Diagnostics = $"hardver: {HardwareCount} · érzékelő: {readings.Count} · " +
                          $"hőmérséklet: {temps}";
        }
        catch (Exception ex) { Diagnostics = "diagnosztika hiba: " + ex.Message; }
    }

    public sealed record Reading(string Hardware, string Sensor, string Type, float Value, string Unit);

    public List<Reading> Read() => Read(false);

    public List<Reading> Read(bool forceStorage)
    {
        var list = new List<Reading>();
        if (!_opened) return list;

        var doStorage = forceStorage || DateTime.UtcNow - _lastStorage > StorageInterval;

        lock (_lock)
        {
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    var isStorage = hw.HardwareType == HardwareType.Storage;
                    if (isStorage && !doStorage)
                    {
                        // Nem frissitunk, de a korabban beolvasott ertekeket kiadjuk.
                        CollectFrom(hw, list);
                        continue;
                    }

                    try
                    {
                        hw.Update();
                        foreach (var sub in hw.SubHardware) sub.Update();
                    }
                    catch { }

                    CollectFrom(hw, list);
                    foreach (var sub in hw.SubHardware) CollectFrom(sub, list);
                }

                if (doStorage) _lastStorage = DateTime.UtcNow;
            }
            catch { }
        }

        return list;
    }

    /// <summary>
    /// A tarolokhoz tartozo szenzorok, a SMART panel szamara. Ugyanazt a peldanyt
    /// hasznalja, ezert nem zavarja meg az elo mereseket.
    /// </summary>
    public List<(string Hardware, string Sensor, string Text)> ReadStorageSensors()
    {
        var list = new List<(string, string, string)>();
        if (!_opened) return list;

        lock (_lock)
        {
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.Storage) continue;
                    try { hw.Update(); } catch { }

                    foreach (var sensor in hw.Sensors)
                    {
                        if (sensor.Value is not float v || float.IsNaN(v)) continue;

                        var text = sensor.SensorType switch
                        {
                            SensorType.Temperature => $"{v:0.#} °C",
                            SensorType.Level => $"{v:0.#} %",
                            SensorType.Data => $"{v:0.##} GB",
                            SensorType.Factor => $"{v:0.##}",
                            _ => ""
                        };
                        if (text.Length == 0) continue;

                        list.Add((hw.Name, sensor.Name, text));
                    }
                }
                _lastStorage = DateTime.UtcNow;
            }
            catch { }
        }

        return list;
    }

    private static void CollectFrom(IHardware hw, List<Reading> list)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v || float.IsNaN(v)) continue;

            var (type, unit) = s.SensorType switch
            {
                SensorType.Temperature => ("Hőmérséklet", "°C"),
                SensorType.Load => ("Terhelés", "%"),
                SensorType.Fan => ("Ventilátor", "RPM"),
                SensorType.Clock => ("Órajel", "MHz"),
                SensorType.Power => ("Fogyasztás", "W"),
                SensorType.Voltage => ("Feszültség", "V"),
                SensorType.Data => ("Adat", "GB"),
                _ => ("", "")
            };

            if (type.Length == 0) continue;
            list.Add(new Reading(hw.Name, s.Name, type, v, unit));
        }
    }

    // ---------------------------------------------------------------- tartalék források

    private static List<Reading> _acpiCache = new();
    private static DateTime _acpiRead = DateTime.MinValue;

    /// <summary>
    /// Tartalek homerseklet-forras: ACPI termikus zonak WMI-bol. Nem igenyel kernel
    /// illesztot. A WMI lekerdezes lassu, ezert 10 masodpercenkent frissitjuk.
    /// </summary>
    public static List<Reading> ReadAcpiThermalZones()
    {
        if (DateTime.UtcNow - _acpiRead < TimeSpan.FromSeconds(10)) return _acpiCache;

        var list = new List<Reading>();
        try
        {
            var ctx = WmiContext.Local;
            int i = 1;
            foreach (var o in ctx.Query("SELECT * FROM MSAcpi_ThermalZoneTemperature", @"root\WMI"))
            {
                var raw = HardwareInfo.ToULong(o["CurrentTemperature"]);
                if (raw == 0) continue;

                // A WMI tized-Kelvinben adja meg.
                var celsius = (float)(raw / 10.0 - 273.15);
                if (celsius is < -20 or > 150) continue;

                var name = o["InstanceName"]?.ToString() ?? "";
                var shortName = name.Contains("THRM", StringComparison.OrdinalIgnoreCase)
                    ? "Termikus zóna " + i
                    : (name.Length is > 0 and <= 34 ? name : "Termikus zóna " + i);

                list.Add(new Reading("Alaplap (ACPI)", shortName, "Hőmérséklet", celsius, "°C"));
                i++;
            }
        }
        catch { }

        _acpiCache = list;
        _acpiRead = DateTime.UtcNow;
        return list;
    }

    /// <summary>
    /// A Memoriaintegritas (HVCI) blokkolja a szenzor-illesztoket, mert azok
    /// rajta vannak a Microsoft serulekeny-illeszto listajan. Ez a leggyakoribb
    /// oka annak, hogy uj gepen nincs CPU-homerseklet.
    /// </summary>
    public static bool IsMemoryIntegrityEnabled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return k?.GetValue("Enabled") is int i && i == 1;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { if (_opened) _computer.Close(); } catch { }
            _opened = false;
        }
    }
}

public static class Fmt
{
    public static string Bytes(ulong b)
    {
        if (b == 0) return "–";
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i <= 1 ? $"{v:0} {u[i]}" : $"{v:0.##} {u[i]}";
    }

    public static string Bytes(long b) => Bytes((ulong)Math.Max(0, b));
}
