using System.Management;
using System.Runtime.InteropServices;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Másolás külső eszközre (1.5.6, a felhasználó kérése): a cserélhető és az USB-s meghajtókra írt / átnevezett / törölt
/// fájlok, valamint a meghajtó csatlakozása és leválasztása. Csak ha a szerver kéri (MetricConfig.UsbAudit) - alapból KI,
/// mert munkavállalói megfigyelésnek számít (GDPR: a dolgozókat tájékoztatni kell).
///
/// Módszer: FileSystemWatcher a meghajtó gyökerén (alkönyvtárakkal) - folyamatindítás és naplózási házirend nélkül.
/// A „ki” a konzolon bejelentkezett felhasználó (a fájlt író folyamatot a Windows ezen az úton nem adja meg).
/// Egy másolás sok Changed-eseményt ad: fájlonként 30 mp-en belül egy bejegyzés (a végső mérettel).
/// </summary>
internal sealed class AgentUsb : IDisposable
{
    private sealed class Drive
    {
        public required FileSystemWatcher Watcher { get; init; }
        public string Label { get; init; } = "";
        public string Serial { get; init; } = "";
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Drive> _drives = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UsbEvent> _pending = new();
    private readonly Dictionary<string, UsbEvent> _recent = new(StringComparer.OrdinalIgnoreCase);
    private int _dropped;
    private HashSet<string> _usbLetters = new(StringComparer.OrdinalIgnoreCase);
    private string _lastLetters = "";
    private string _user = "";
    private DateTime _userAt = DateTime.MinValue;

    public const int MaxPending = 5000;

    /// <summary>5 mp-enként (az agent figyelő köre): új / eltávolított meghajtók. enabled = false → minden figyelés le.</summary>
    public void Tick(bool enabled)
    {
        if (!enabled) { lock (_lock) { foreach (var d in _drives.Values) d.Watcher.Dispose(); _drives.Clear(); _recent.Clear(); } return; }
        var ready = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).ToList();
        var letters = string.Join(",", ready.Select(d => d.Name).OrderBy(x => x));
        // Az USB-s (nem „cserélhető”, hanem fix) külső lemezek felismerése csak a meghajtók változásakor (WMI drága).
        if (letters != _lastLetters) { _usbLetters = UsbFixedLetters(); _lastLetters = letters; }
        var external = ready.Where(d => d.DriveType == DriveType.Removable || (d.DriveType == DriveType.Fixed && _usbLetters.Contains(d.Name[..1])))
            .Where(d => !string.Equals(d.Name[..1], Environment.SystemDirectory[..1], StringComparison.OrdinalIgnoreCase)).ToList();
        lock (_lock)
        {
            foreach (var gone in _drives.Keys.Where(k => !external.Any(d => d.Name.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                var d = _drives[gone];
                d.Watcher.Dispose();
                _drives.Remove(gone);
                Add(new UsbEvent { Kind = "disconnect", Drive = gone.TrimEnd('\\'), Label = d.Label, Serial = d.Serial });
            }
            foreach (var d in external.Where(d => !_drives.ContainsKey(d.Name)))
            {
                string label = "", serial = "";
                try { label = d.VolumeLabel; } catch { }
                serial = VolumeSerial(d.Name);
                var w = new FileSystemWatcher(d.RootDirectory.FullName)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                var root = d.Name;
                w.Created += (_, e) => File(root, "write", e.FullPath, null);
                w.Changed += (_, e) => File(root, "write", e.FullPath, null);
                w.Renamed += (_, e) => File(root, "rename", e.FullPath, e.OldFullPath);
                w.Deleted += (_, e) => File(root, "delete", e.FullPath, null);
                w.Error += (_, _) => { lock (_lock) _dropped++; };   // túl sok esemény egyszerre: a puffer betelt
                try { w.EnableRaisingEvents = true; } catch { w.Dispose(); continue; }
                _drives[d.Name] = new Drive { Watcher = w, Label = label, Serial = serial };
                Add(new UsbEvent { Kind = "connect", Drive = d.Name.TrimEnd('\\'), Label = label, Serial = serial,
                    Size = SafeSize(d) });
            }
        }
    }

    private static long SafeSize(DriveInfo d) { try { return d.TotalSize; } catch { return 0; } }

    private void File(string root, string kind, string full, string? old)
    {
        try
        {
            if (kind != "delete" && Directory.Exists(full)) return;   // mappa: a benne levő fájlok jelzik a másolást
            var rel = full.Length > root.Length ? full[root.Length..] : full;
            long size = 0;
            if (kind != "delete") try { size = new FileInfo(full).Length; } catch { }
            lock (_lock)
            {
                if (!_drives.TryGetValue(root, out var drv)) return;
                var now = DateTime.UtcNow;
                // Egy fájl írása sok eseményt ad: 30 mp-en belül ugyanazt a bejegyzést frissítjük (a végső mérettel).
                if (kind == "write" && _recent.TryGetValue(rel, out var prev) && now - prev.AtUtc < TimeSpan.FromSeconds(30) && prev.Kind == "write")
                { prev.Size = Math.Max(prev.Size, size); return; }
                var ev = new UsbEvent { Kind = kind, Drive = root.TrimEnd('\\'), Label = drv.Label, Serial = drv.Serial, Path = Cut(rel),
                    OldPath = old is null ? "" : Cut(old.Length > root.Length ? old[root.Length..] : old), Size = size };
                Add(ev);
                if (kind == "write") _recent[rel] = ev;
                if (_recent.Count > 2000) foreach (var k in _recent.Where(x => now - x.Value.AtUtc > TimeSpan.FromSeconds(30)).Select(x => x.Key).ToList()) _recent.Remove(k);
            }
        }
        catch { }
    }

    private static string Cut(string s) => s.Length <= 400 ? s : s[..400];

    private void Add(UsbEvent e)
    {
        e.AtUtc = DateTime.UtcNow;
        e.User = ConsoleUser();
        _pending.Add(e);
        if (_pending.Count > MaxPending) { _dropped += _pending.Count - MaxPending; _pending.RemoveRange(0, _pending.Count - MaxPending); }
    }

    /// <summary>A következő jelentésbe: legfeljebb max esemény (a legrégebbiek). Ha közben elveszett esemény, jelzi.</summary>
    public List<UsbEvent> Take(int max)
    {
        lock (_lock)
        {
            if (_dropped > 0)
            {
                _pending.Insert(0, new UsbEvent { AtUtc = DateTime.UtcNow, Kind = "overflow", Path = $"{_dropped} esemény elveszett (túl sok egyszerre)", User = ConsoleUser() });
                _dropped = 0;
            }
            var n = Math.Min(max, _pending.Count);
            var list = _pending.GetRange(0, n);
            _pending.RemoveRange(0, n);
            return list;
        }
    }

    /// <summary>Sikertelen jelentkezés után vissza a sor elejére.</summary>
    public void Requeue(List<UsbEvent> list) { lock (_lock) _pending.InsertRange(0, list); }

    /// <summary>Az USB-buszon levő (fix meghajtóként látszó) külső lemezek betűjelei (MSFT_Disk BusType 7 = USB).</summary>
    private static HashSet<string> UsbFixedLetters()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string ns = @"root\Microsoft\Windows\Storage";
            var usbDisks = new HashSet<uint>();
            using (var s = new ManagementObjectSearcher(ns, "SELECT Number, BusType FROM MSFT_Disk"))
                foreach (var o in s.Get())
                    if (Convert.ToInt32(o["BusType"]) == 7) usbDisks.Add(Convert.ToUInt32(o["Number"]));
            if (usbDisks.Count == 0) return set;
            using (var s = new ManagementObjectSearcher(ns, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
                foreach (var o in s.Get())
                    if (usbDisks.Contains(Convert.ToUInt32(o["DiskNumber"])) && o["DriveLetter"] is char c && c != '\0') set.Add(c.ToString());
        }
        catch { }
        return set;
    }

    private static string VolumeSerial(string root)
    {
        try { return GetVolumeInformation(root, null, 0, out var sn, out _, out _, null, 0) ? sn.ToString("X8") : ""; }
        catch { return ""; }
    }

    /// <summary>A konzolon bejelentkezett felhasználó (TARTOMÁNY\név) - 30 mp-ig gyorsítótárazva.</summary>
    private string ConsoleUser()
    {
        if (DateTime.UtcNow - _userAt < TimeSpan.FromSeconds(30)) return _user;
        _userAt = DateTime.UtcNow;
        try
        {
            var sid = WTSGetActiveConsoleSessionId();
            if (sid == 0xFFFFFFFF) return _user = "";
            string Q(int cls)
            {
                if (!WTSQuerySessionInformation(IntPtr.Zero, sid, cls, out var buf, out _)) return "";
                try { return Marshal.PtrToStringUni(buf) ?? ""; } finally { WTSFreeMemory(buf); }
            }
            var name = Q(5); var dom = Q(7);   // WTSUserName, WTSDomainName
            return _user = name.Length == 0 ? "" : dom.Length > 0 ? dom + "\\" + name : name;
        }
        catch { return _user = ""; }
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, System.Text.StringBuilder? name, int nameSize, out uint serial,
        out uint maxComponent, out uint flags, System.Text.StringBuilder? fs, int fsSize);

    public void Dispose() { lock (_lock) { foreach (var d in _drives.Values) d.Watcher.Dispose(); _drives.Clear(); } }
}
