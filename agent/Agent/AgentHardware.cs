using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Hardver-érzékelők a LibreHardwareMonitorral (a DevToolbox is ezt használja): CPU-, GPU-, alaplap-, lemez- és
/// memória-hőmérséklet, ventilátorok, fogyasztás, GPU-terhelés és -memória. Csak olvas.
///
/// A CPU- és alaplap-érzékelőkhöz a PawnIO meghajtó kell (a 0.9.6-os könyvtár már nem csomagol ki saját meghajtót):
/// enélkül csak a GPU (gyártói API) és a lemezek (SMART) értékei jönnek. A meghajtót az agent a szerver kérésére
/// wingettel telepíti (AgentCore.EnsureSensorDriverAsync), utána az érzékelőket újranyitja.
///
/// A könyvtár egyetlen példánya él (a kernel-oldali erőforrás megosztott); a mintavevő és a jelentkezés sorban olvas.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentHardware
{
    private static readonly object Lock = new();
    private static Computer? _computer;
    private static string _openError = "";
    // A lemezek SMART-olvasása lassú (lemezenként 0,1–0,5 mp), a hőmérsékletük pedig lassan változik.
    private static DateTime _lastStorage = DateTime.MinValue;
    private static readonly TimeSpan StorageInterval = TimeSpan.FromMinutes(5);
    // A valaha forgó ventilátorok: ha egy ilyen leáll (0 RPM), az hír - a sosem forgó (nem bekötött) csatlakozók nem.
    private static readonly HashSet<string> SeenFans = new();

    public static string LastDiagnostics { get; private set; } = "";

    /// <summary>A PawnIO meghajtó telepítve van-e (a CPU- és alaplap-érzékelőkhöz kell).</summary>
    public static bool DriverInstalled
    {
        get
        {
            try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch { return false; }
        }
    }

    public static string DriverVersion
    {
        get
        {
            try { return LibreHardwareMonitor.PawnIo.PawnIo.Version?.ToString() ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>Az érzékelők újranyitása (pl. a meghajtó telepítése után, hogy a CPU-érzékelők is megjelenjenek).</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            _openError = "";
            _lastStorage = DateTime.MinValue;
        }
    }

    private static Computer? Open()
    {
        if (_computer is not null) return _computer;
        if (_openError.Length > 0) return null;
        try
        {
            var c = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true,
                IsStorageEnabled = true, IsMemoryEnabled = true
            };
            c.Open();
            _computer = c;
            _lastStorage = DateTime.MinValue;
            return c;
        }
        catch (Exception ex) { _openError = ex.Message; return null; }
    }

    private static void Add(List<Metric> l, string key, string label, double val, string unit, string cat = "Hardver")
    {
        if (double.IsNaN(val) || double.IsInfinity(val)) return;
        l.Add(new Metric { Key = key, Label = label, Value = Math.Round(val, 2), Unit = unit, Category = cat });
    }

    /// <summary>Az érzékelők kiolvasása. perCore: magonkénti hőmérséklet is.</summary>
    public static void Collect(List<Metric> l, bool perCore)
    {
        var installed = DriverInstalled;
        Add(l, "hw.driver", "Érzékelő-meghajtó (PawnIO) telepítve", installed ? 1 : 0, "", "Hardver");
        lock (Lock)
        {
            var c = Open();
            if (c is null) { LastDiagnostics = "nem nyitható: " + _openError; throw new InvalidOperationException(_openError); }
            var doStorage = DateTime.UtcNow - _lastStorage > StorageInterval;
            int cpuIdx = 0, gpuIdx = 0, diskIdx = 0, ramIdx = 0, sensors = 0;
            foreach (var hw in c.Hardware)
            {
                var isStorage = hw.HardwareType == HardwareType.Storage;
                if (!isStorage || doStorage)
                {
                    try { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); } catch { }
                }
                var all = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).Where(s => s.Value is float f && !float.IsNaN(f)).ToList();
                sensors += all.Count;
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu: Cpu(l, hw.Name, cpuIdx++, all, perCore); break;
                    case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel: Gpu(l, hw.Name, gpuIdx++, all); break;
                    case HardwareType.Motherboard: Board(l, all); break;
                    case HardwareType.Storage: Storage(l, hw.Name, diskIdx++, all); break;
                    case HardwareType.Memory:
                        foreach (var s in all.Where(s => s.SensorType == SensorType.Temperature))
                            Add(l, $"hw.ram{ramIdx++}.temp", $"Memória ({s.Name}): hőmérséklet", V(s), "°C");
                        break;
                }
            }
            if (doStorage) _lastStorage = DateTime.UtcNow;
            LastDiagnostics = $"PawnIO: {(installed ? "telepítve " + DriverVersion : "NINCS (CPU- és alaplap-érzékelők nélkül)")} · " +
                              $"hardver: {c.Hardware.Count()} · érzékelő: {sensors}";
        }
    }

    private static double V(ISensor s) => s.Value ?? double.NaN;

    private static ISensor? Find(List<ISensor> all, SensorType t, params string[] names)
    {
        foreach (var n in names)
            if (all.FirstOrDefault(s => s.SensorType == t && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return null;
    }

    private static void Cpu(List<Metric> l, string name, int i, List<ISensor> all, bool perCore)
    {
        var p = i == 0 ? "hw.cpu" : $"hw.cpu{i}";
        var n = i == 0 ? "CPU" : $"CPU {i + 1}";
        if (Find(all, SensorType.Temperature, "CPU Package", "Tctl/Tdie", "Tdie", "Tctl", "Core Max", "Core Average", "CPU Cores") is { } t)
            Add(l, p + ".temp", $"{n} hőmérséklet ({name})", V(t), "°C");
        if (Find(all, SensorType.Temperature, "Core Max") is { } mx) Add(l, p + ".coremax", $"{n} legmelegebb mag", V(mx), "°C");
        // Meghajtó nélkül a fogyasztás 0 W-nak látszik - az nem adat, kihagyjuk.
        if (Find(all, SensorType.Power, "CPU Package", "Package") is { } pw && V(pw) > 0) Add(l, p + ".power", $"{n} fogyasztás", V(pw), "W");
        if (Find(all, SensorType.Voltage, "CPU Core", "Core (SVI2 TFN)", "Vcore") is { } vc) Add(l, p + ".vcore", $"{n} magfeszültség", V(vc), "V");
        if (!perCore) return;
        foreach (var s in all.Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("Core #") && !s.Name.Contains("Distance")))
        {
            var num = new string(s.Name.Skip(6).TakeWhile(char.IsDigit).ToArray());
            if (num.Length > 0) Add(l, $"{p}.core{num}.temp", $"{n} mag #{num} hőmérséklet", V(s), "°C", "CPU magok");
        }
    }

    private static void Gpu(List<Metric> l, string name, int i, List<ISensor> all)
    {
        var p = $"hw.gpu{i}";
        var n = $"GPU{(i > 0 ? " " + (i + 1) : "")}";
        if (Find(all, SensorType.Temperature, "GPU Core", "GPU Temperature") is { } t) Add(l, p + ".temp", $"{n} hőmérséklet ({name})", V(t), "°C", "GPU");
        if (Find(all, SensorType.Temperature, "GPU Hot Spot", "GPU Hotspot") is { } hs) Add(l, p + ".hotspot", $"{n} hotspot", V(hs), "°C", "GPU");
        if (Find(all, SensorType.Temperature, "GPU Memory Junction", "GPU Memory") is { } mt) Add(l, p + ".memtemp", $"{n} VRAM-hőmérséklet", V(mt), "°C", "GPU");
        if (Find(all, SensorType.Load, "GPU Core", "D3D 3D") is { } ld) Add(l, p + ".load", $"{n} terhelés", V(ld), "%", "GPU");
        var used = Find(all, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        var total = Find(all, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        if (used is not null) Add(l, p + ".memusedmb", $"{n} videómemória (foglalt)", V(used), "MB", "GPU");
        if (used is not null && total is not null && V(total) > 0) Add(l, p + ".mempct", $"{n} videómemória", V(used) * 100 / V(total), "%", "GPU");
        if (Find(all, SensorType.Clock, "GPU Core") is { } ck) Add(l, p + ".clock", $"{n} órajel", V(ck), "MHz", "GPU");
        if (Find(all, SensorType.Clock, "GPU Memory") is { } mc) Add(l, p + ".memclock", $"{n} memória-órajel", V(mc), "MHz", "GPU");
        if (Find(all, SensorType.Power, "GPU Package", "GPU Power", "GPU Core", "GPU Board Power") is { } pw && V(pw) > 0) Add(l, p + ".power", $"{n} fogyasztás", V(pw), "W", "GPU");
        if (all.FirstOrDefault(s => s.SensorType == SensorType.Fan) is { } f) Add(l, p + ".fan", $"{n} ventilátor", V(f), "RPM", "GPU");
    }

    private static void Board(List<Metric> l, List<ISensor> all)
    {
        foreach (var s in all.Where(s => s.SensorType == SensorType.Temperature))
        {
            var v = V(s);
            if (v is <= 0 or > 150) continue;   // nem bekötött csatlakozó (0 / -128 / 255)
            Add(l, $"hw.mb.{Safe(s.Name)}.temp", $"Alaplap: {s.Name}", v, "°C");
        }
        foreach (var s in all.Where(s => s.SensorType == SensorType.Fan))
        {
            var key = $"hw.fan.{Safe(s.Name)}.rpm";
            var v = V(s);
            if (v > 0) SeenFans.Add(key);
            if (!SeenFans.Contains(key)) continue;
            Add(l, key, $"Ventilátor: {s.Name}", v, "RPM");
        }
    }

    private static void Storage(List<Metric> l, string name, int i, List<ISensor> all)
    {
        var p = $"hw.disk{i}";
        if (all.FirstOrDefault(s => s.SensorType == SensorType.Temperature) is { } t && V(t) is > 0 and < 120)
            Add(l, p + ".temp", $"{name}: hőmérséklet", V(t), "°C", "Lemez-állapot");
        if (Find(all, SensorType.Level, "Available Spare") is { } sp) Add(l, p + ".spare", $"{name}: tartalék blokkok", V(sp), "%", "Lemez-állapot");
        if (Find(all, SensorType.Level, "Percentage Used", "Used Endurance") is { } pu) Add(l, p + ".wearpct", $"{name}: elhasználtság", V(pu), "%", "Lemez-állapot");
        if (Find(all, SensorType.Level, "Remaining Life") is { } rl) Add(l, p + ".life", $"{name}: hátralévő élettartam", V(rl), "%", "Lemez-állapot");
        if (Find(all, SensorType.Data, "Data Written", "Total Bytes Written") is { } dw) Add(l, p + ".writtengb", $"{name}: összes írás", V(dw), "GB", "Lemez-állapot");
        if (Find(all, SensorType.Data, "Data Read", "Total Bytes Read") is { } dr) Add(l, p + ".readgb", $"{name}: összes olvasás", V(dr), "GB", "Lemez-állapot");
    }

    private static string Safe(string s)
    {
        var x = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return x.Length > 30 ? x[..30] : x.Length == 0 ? "x" : x;
    }
}
