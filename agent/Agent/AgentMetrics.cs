using System.Diagnostics;
using System.Runtime.Versioning;
using System.Management;
using System.Text;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// A felugyelt gep metrikainak gyujtese. Minden kategoria KULON, valoban izolalva:
/// egy WMI-osztaly hianya vagy hibaja nem viszi el a tobbit. Csak olvas.
///
/// A korabbi valtozat hibai javitva:
///  - a WMI-eredmenyeket rogton egy listaba masoljuk es Dispose-oljuk (nincs szivargas,
///    es a kivetel a kategoria try/catch-eben keletkezik, nem kesobb, iteralaskor);
///  - a naplo-lekerdezes gyorsabb (kevesebb visszaadott sor);
///  - Diagnostics() megmondja, melyik kategoria hany metrikat adott - igy latszik,
///    ha valamelyik ures marad.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentMetrics
{
    public sealed class Config
    {
        public bool Cpu { get; set; } = true;
        public bool Memory { get; set; } = true;
        public bool Disk { get; set; } = true;
        public bool DiskSmart { get; set; } = true;
        public bool Temperature { get; set; } = true;
        public bool EventLog { get; set; } = true;
        public bool Network { get; set; } = true;
        public bool DiskIo { get; set; } = true;
        public List<string> Services { get; set; } = new();
        public List<string> Processes { get; set; } = new();
        // 1.5.0
        public bool CpuDetail { get; set; } = true;
        public bool PerCore { get; set; }
        public bool Hardware { get; set; } = true;
        public bool Gpu { get; set; } = true;
        public bool Security { get; set; } = true;
        public bool HyperV { get; set; } = true;
        public bool Ping { get; set; } = true;
        public List<string> PingTargets { get; set; } = new();
        /// <summary>A szerver gépneve / címe (a ServerUrl-ből) - a ping alapcélja.</summary>
        public string ServerHost { get; set; } = "";

        public static Config From(MetricConfig mc, string serverHost = "") => new()
        {
            Cpu = mc.Cpu, Memory = mc.Memory, Disk = mc.Disk, DiskSmart = mc.DiskSmart,
            Temperature = mc.Temperature, EventLog = mc.EventLog, Network = mc.Network, DiskIo = mc.DiskIo,
            Services = mc.Services, Processes = mc.Processes,
            CpuDetail = mc.CpuDetail, PerCore = mc.PerCore, Hardware = mc.Hardware, Gpu = mc.Gpu, Security = mc.Security,
            HyperV = mc.HyperV, Ping = mc.Ping, PingTargets = mc.PingTargets ?? new(), ServerHost = serverHost
        };
    }

    /// <summary>A legutobbi gyujtes kategoriankenti eredmenye - a "diag" parancs ezt irja ki.</summary>
    public static string LastDiagnostics { get; private set; } = "";

    /// <param name="includeSlow">
    /// A lassan valtozo es dragan merheto kategoriak (SMART, esemenynaplo) csak jelentkezeskor -
    /// a percenkenti mintavetel (includeSlow=false) nem terheli veluk a gepet.
    /// </param>
    public static List<Metric> Collect(Config cfg, bool includeSlow = true)
    {
        var list = new List<Metric>();
        var diag = new StringBuilder();

        Run(diag, "CPU", cfg.Cpu, list, Cpu);
        Run(diag, "CPU részletek", cfg.Cpu && cfg.CpuDetail, list, CpuDetail);
        Run(diag, "CPU magok", cfg.Cpu && cfg.PerCore, list, PerCore);
        Run(diag, "Memória", cfg.Memory, list, Memory);
        Run(diag, "Lemez", cfg.Disk, list, Disk);
        Run(diag, "Lemez-I/O", cfg.DiskIo, list, DiskIo);
        Run(diag, "Hálózat", cfg.Network, list, Network);
        Run(diag, "Ping", cfg.Ping, list, l => Ping(l, cfg));
        Run(diag, "SMART", cfg.DiskSmart && includeSlow, list, Smart);
        Run(diag, "Hőmérséklet", cfg.Temperature, list, Temperature);
        Run(diag, "Hardver", cfg.Hardware, list, l => AgentHardware.Collect(l, cfg.PerCore));
        Run(diag, "GPU", cfg.Gpu, list, Gpu);
        Run(diag, "Szolgáltatás", cfg.Services.Count > 0, list, l => Services(l, cfg.Services));
        Run(diag, "Folyamat", cfg.Processes.Count > 0, list, l => Processes(l, cfg.Processes));
        Run(diag, "Hyper-V", cfg.HyperV && HyperVPresent(), list, l => HyperV(l, includeSlow));
        Run(diag, "Napló", cfg.EventLog && includeSlow, list, EventLog);
        Run(diag, "Biztonság", cfg.Security && includeSlow, list, Security);
        // 1.5.6: védelmi állapot (vírusvédelem, tűzfal, BitLocker) - az eszköz-összegzés biztonsági része ebből számol.
        Run(diag, "Védelem", cfg.Security && includeSlow, list, Protection);
        if (cfg.Hardware && includeSlow) diag.AppendLine($"  {"",-14}  {AgentHardware.LastDiagnostics}");

        if (includeSlow) LastDiagnostics = diag.ToString();
        // Egy kulcs egyszer (pl. a lemez hőmérséklete a SMART-számlálóból és az ATA-attribútumból is jöhet).
        return list.DistinctBy(m => m.Key).ToList();
    }

    /// <summary>Egy kategoria futtatasa izolaltan; a diagnosztikaba beirja az eredmenyt.</summary>
    private static void Run(StringBuilder diag, string name, bool enabled, List<Metric> list, Action<List<Metric>> collect)
    {
        if (!enabled) { diag.AppendLine($"  {name,-14}: kikapcsolva"); return; }
        var before = list.Count;
        try
        {
            collect(list);
            diag.AppendLine($"  {name,-14}: {list.Count - before} metrika");
        }
        catch (Exception ex)
        {
            diag.AppendLine($"  {name,-14}: HIBA - {ex.Message}");
        }
    }

    private static void Add(List<Metric> l, string key, string label, double val, string unit, string cat, string text = "")
        => l.Add(new Metric { Key = key, Label = label, Value = Math.Round(val, 2), Unit = unit, Category = cat, Text = text });

    // ---------------------------------------------------------------- CPU
    private static (double Cores, double Threads, int Sockets, double MaxMhz)? _cpuInfo;

    private static void Cpu(List<Metric> l)
    {
        // Elsodleges: teljesitmenyszamlalo (azonnali, nincs ket mintavetel).
        var rows = Read("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
        if (rows.Count > 0)
            Add(l, "cpu.load", "CPU terhelés", ToD(rows[0].GetValueOrDefault("PercentProcessorTime")), "%", "CPU");
        else
        {
            // Tartalek: Win32_Processor.LoadPercentage (lassabb, de mindig letezik).
            var p = Read("SELECT LoadPercentage FROM Win32_Processor");
            if (p.Count > 0)
            {
                var avg = p.Average(r => ToD(r.GetValueOrDefault("LoadPercentage")));
                Add(l, "cpu.load", "CPU terhelés", avg, "%", "CPU");
            }
        }

        // A processzor felépítése (magok, szálak, foglalat, órajel) nem változik: egyszer olvassuk, utána a tárolt érték megy.
        if (_cpuInfo is null)
        {
            var procs = Read("SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            if (procs.Count > 0)
                _cpuInfo = (procs.Sum(r => ToD(r.GetValueOrDefault("NumberOfCores"))), procs.Sum(r => ToD(r.GetValueOrDefault("NumberOfLogicalProcessors"))),
                            procs.Count, procs.Max(r => ToD(r.GetValueOrDefault("MaxClockSpeed"))));
        }
        if (_cpuInfo is { } ci)
        {
            Add(l, "cpu.cores", "Fizikai magok", ci.Cores, "db", "CPU");
            Add(l, "cpu.threads", "Logikai szálak", ci.Threads, "db", "CPU");
            Add(l, "cpu.sockets", "Processzorok (foglalat)", ci.Sockets, "db", "CPU");
            if (ci.MaxMhz > 0) Add(l, "cpu.maxmhz", "Max. órajel", ci.MaxMhz, "MHz", "CPU");
        }
        else
        {
            var cs = Read("SELECT NumberOfLogicalProcessors FROM Win32_ComputerSystem");
            if (cs.Count > 0)
                Add(l, "cpu.threads", "Logikai szálak", ToD(cs[0].GetValueOrDefault("NumberOfLogicalProcessors")), "db", "CPU");
        }

        // Rendszer: a processzorra varo szalak (tartos >2/mag = szuk keresztmetszet), folyamatok, futasido.
        var sys = Read("SELECT ProcessorQueueLength, Processes, Threads FROM Win32_PerfFormattedData_PerfOS_System");
        if (sys.Count > 0)
        {
            Add(l, "cpu.queue", "Processzor-várakozási sor", ToD(sys[0].GetValueOrDefault("ProcessorQueueLength")), "db", "CPU");
            Add(l, "sys.processes", "Futó folyamatok", ToD(sys[0].GetValueOrDefault("Processes")), "db", "Rendszer");
            Add(l, "sys.threads", "Szálak", ToD(sys[0].GetValueOrDefault("Threads")), "db", "Rendszer");
        }
        Add(l, "sys.uptimeh", "Futásidő (újraindítás óta)", Environment.TickCount64 / 3_600_000.0, "óra", "Rendszer");
    }

    // ---------------------------------------------------------------- memória
    private static void Memory(List<Metric> l)
    {
        var os = Read("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        if (os.Count == 0) return;
        var totalKb = ToD(os[0].GetValueOrDefault("TotalVisibleMemorySize"));
        var freeKb = ToD(os[0].GetValueOrDefault("FreePhysicalMemory"));
        if (totalKb <= 0) return;
        Add(l, "mem.usedpct", "Memória kihasználtság", (totalKb - freeKb) * 100.0 / totalKb, "%", "Memória");
        Add(l, "mem.freegb", "Szabad memória", freeKb / 1024 / 1024, "GB", "Memória");
        Add(l, "mem.totalgb", "Összes memória", totalKb / 1024 / 1024, "GB", "Memória");

        // Lefoglalt (commit) memoria es lapozas: a fizikai RAM mellett ez mutatja, ha a gep a lapozofajlbol el.
        var pm = Read("SELECT PercentCommittedBytesInUse, PagesPersec, CacheBytes, StandbyCacheNormalPriorityBytes, StandbyCacheReserveBytes, " +
                      "StandbyCacheCoreBytes, ModifiedPageListBytes, CommittedBytes, CommitLimit, PageFaultsPersec, PagesInputPersec, PagesOutputPersec " +
                      "FROM Win32_PerfFormattedData_PerfOS_Memory");
        if (pm.Count > 0)
        {
            var m = pm[0];
            double G(string k) => ToD(m.GetValueOrDefault(k)) / 1024 / 1024 / 1024;
            Add(l, "mem.commitpct", "Lefoglalt memória (commit)", ToD(m.GetValueOrDefault("PercentCommittedBytesInUse")), "%", "Memória");
            Add(l, "mem.pagesps", "Lapozás", ToD(m.GetValueOrDefault("PagesPersec")), "lap/s", "Memória");
            // Gyorsítótár: a fájlrendszer-cache és a készenléti (standby) lista - ez a "Gyorsítótárazott" a Feladatkezelőben.
            Add(l, "mem.cachegb", "Gyorsítótár (cache)", G("CacheBytes") + G("StandbyCacheNormalPriorityBytes") + G("StandbyCacheReserveBytes") +
                G("StandbyCacheCoreBytes") + G("ModifiedPageListBytes"), "GB", "Memória");
            Add(l, "mem.usedgb", "Foglalt memória", (totalKb - freeKb) / 1024 / 1024, "GB", "Memória");
            Add(l, "mem.committedgb", "Lefoglalt (commit)", G("CommittedBytes"), "GB", "Memória");
            Add(l, "mem.commitlimitgb", "Lefoglalható (commit-korlát)", G("CommitLimit"), "GB", "Memória");
            Add(l, "mem.faultsps", "Laphibák", ToD(m.GetValueOrDefault("PageFaultsPersec")), "db/s", "Memória");
            Add(l, "mem.swapinps", "Lapozás be (swap in)", ToD(m.GetValueOrDefault("PagesInputPersec")), "lap/s", "Memória");
            Add(l, "mem.swapoutps", "Lapozás ki (swap out)", ToD(m.GetValueOrDefault("PagesOutputPersec")), "lap/s", "Memória");
        }
        var pf = Read("SELECT PercentUsage FROM Win32_PerfFormattedData_PerfOS_PagingFile WHERE Name='_Total'");
        if (pf.Count > 0) Add(l, "mem.swappct", "Lapozófájl-használat (swap)", ToD(pf[0].GetValueOrDefault("PercentUsage")), "%", "Memória");
    }

    // ---------------------------------------------------------------- CPU részletek
    private static void CpuDetail(List<Metric> l)
    {
        var rows = Read("SELECT PercentProcessorPerformance, ProcessorFrequency, PercentPerformanceLimit, PercentPrivilegedTime, InterruptsPersec, " +
                        "PercentC1Time, PercentC2Time, PercentC3Time, PercentDPCTime FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
        if (rows.Count > 0)
        {
            var r = rows[0];
            var perf = ToD(r.GetValueOrDefault("PercentProcessorPerformance"));
            var nominal = ToD(r.GetValueOrDefault("ProcessorFrequency"));
            var limit = ToD(r.GetValueOrDefault("PercentPerformanceLimit"));
            // A valós órajel: a névleges frekvencia × a teljesítmény-%; 100% fölött turbó.
            if (nominal > 0 && perf > 0) Add(l, "cpu.mhz", "Órajel (most)", nominal * perf / 100, "MHz", "CPU");
            Add(l, "cpu.perfpct", "Teljesítmény (a névlegeshez)", perf, "%", "CPU");
            Add(l, "cpu.turbo", "Turbó aktív", perf > 100 ? 1 : 0, "", "CPU");
            // 100 = nincs korlát; kevesebb: a processzort lassítják (hő, fogyasztási korlát, energiatakarékosság).
            if (limit > 0)
            {
                Add(l, "cpu.limitpct", "Teljesítménykorlát (100 = nincs)", limit, "%", "CPU");
                Add(l, "cpu.throttled", "Lassítás aktív (throttling)", limit < 100 ? 1 : 0, "", "CPU");
            }
            Add(l, "cpu.privpct", "Rendszermag-idő (privileged)", ToD(r.GetValueOrDefault("PercentPrivilegedTime")), "%", "CPU");
            Add(l, "cpu.dpcpct", "Késleltetett eljárások (DPC)", ToD(r.GetValueOrDefault("PercentDPCTime")), "%", "CPU");
            Add(l, "cpu.interruptsps", "Megszakítások", ToD(r.GetValueOrDefault("InterruptsPersec")), "db/s", "CPU");
            Add(l, "cpu.c1pct", "Üresjárat: C1 állapot", ToD(r.GetValueOrDefault("PercentC1Time")), "%", "CPU");
            Add(l, "cpu.c2pct", "Üresjárat: C2 állapot", ToD(r.GetValueOrDefault("PercentC2Time")), "%", "CPU");
            Add(l, "cpu.c3pct", "Üresjárat: C3 (mély) állapot", ToD(r.GetValueOrDefault("PercentC3Time")), "%", "CPU");
        }
        var sys = Read("SELECT ContextSwitchesPersec, SystemCallsPersec FROM Win32_PerfFormattedData_PerfOS_System");
        if (sys.Count > 0)
        {
            Add(l, "cpu.ctxswps", "Környezetváltás (context switch)", ToD(sys[0].GetValueOrDefault("ContextSwitchesPersec")), "db/s", "CPU");
            Add(l, "cpu.syscallsps", "Rendszerhívások", ToD(sys[0].GetValueOrDefault("SystemCallsPersec")), "db/s", "CPU");
        }
    }

    // ---------------------------------------------------------------- magonkénti terhelés
    private static void PerCore(List<Metric> l)
    {
        foreach (var o in Read("SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor"))
        {
            var name = o.GetValueOrDefault("Name")?.ToString() ?? "";
            if (!int.TryParse(name, out var n)) continue;   // a _Total kimarad
            Add(l, $"cpu.core{n}.load", $"Mag #{n} terhelés", ToD(o.GetValueOrDefault("PercentProcessorTime")), "%", "CPU magok");
        }
    }

    // ---------------------------------------------------------------- lemez-aktivitás
    private static void DiskIo(List<Metric> l)
    {
        foreach (var o in Read("SELECT PercentDiskTime, AvgDiskQueueLength, DiskReadBytesPersec, DiskWriteBytesPersec " +
                               "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'"))
        {
            Add(l, "diskio.busypct", "Lemez foglaltság", Math.Min(100, ToD(o.GetValueOrDefault("PercentDiskTime"))), "%", "Lemez-I/O");
            Add(l, "diskio.queue", "Lemez várakozási sor", ToD(o.GetValueOrDefault("AvgDiskQueueLength")), "db", "Lemez-I/O");
            Add(l, "diskio.readkbps", "Lemez olvasás", ToD(o.GetValueOrDefault("DiskReadBytesPersec")) / 1024, "KB/s", "Lemez-I/O");
            Add(l, "diskio.writekbps", "Lemez írás", ToD(o.GetValueOrDefault("DiskWriteBytesPersec")) / 1024, "KB/s", "Lemez-I/O");
        }
        foreach (var o in Read("SELECT DiskReadsPersec, DiskWritesPersec, PercentIdleTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'"))
        {
            Add(l, "diskio.readiops", "Olvasási műveletek (IOPS)", ToD(o.GetValueOrDefault("DiskReadsPersec")), "IOPS", "Lemez-I/O");
            Add(l, "diskio.writeiops", "Írási műveletek (IOPS)", ToD(o.GetValueOrDefault("DiskWritesPersec")), "IOPS", "Lemez-I/O");
            // A kihasználtság az üresjárat ellentéte: a % Disk Time több lemeznél / sorban álló kérésnél 100 fölé is mehet.
            Add(l, "diskio.utilpct", "Lemez kihasználtság", Math.Clamp(100 - ToD(o.GetValueOrDefault("PercentIdleTime")), 0, 100), "%", "Lemez-I/O");
        }
        DiskLatency(l);
    }

    // A válaszidő a formázott számlálóban egész másodperc (mindig 0) - a nyers számlálók két mintája közti különbségből számoljuk.
    private static (double ReadT, double ReadN, double WriteT, double WriteN, double Freq)? _prevLat;

    private static void DiskLatency(List<Metric> l)
    {
        var rows = Read("SELECT AvgDisksecPerRead, AvgDisksecPerRead_Base, AvgDisksecPerWrite, AvgDisksecPerWrite_Base, Frequency_PerfTime " +
                        "FROM Win32_PerfRawData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
        if (rows.Count == 0) return;
        var r = rows[0];
        var now = (ToD(r.GetValueOrDefault("AvgDisksecPerRead")), ToD(r.GetValueOrDefault("AvgDisksecPerRead_Base")),
                   ToD(r.GetValueOrDefault("AvgDisksecPerWrite")), ToD(r.GetValueOrDefault("AvgDisksecPerWrite_Base")), ToD(r.GetValueOrDefault("Frequency_PerfTime")));
        if (_prevLat is { } p && now.Item5 > 0)
        {
            double Ms(double t1, double t0, double n1, double n0) => n1 > n0 && t1 >= t0 ? (t1 - t0) / now.Item5 / (n1 - n0) * 1000 : 0;
            Add(l, "diskio.readms", "Olvasási válaszidő", Ms(now.Item1, p.ReadT, now.Item2, p.ReadN), "ms", "Lemez-I/O");
            Add(l, "diskio.writems", "Írási válaszidő", Ms(now.Item3, p.WriteT, now.Item4, p.WriteN), "ms", "Lemez-I/O");
        }
        _prevLat = now;
    }

    // ---------------------------------------------------------------- hálózat
    private static void Network(List<Metric> l)
    {
        double rx = 0, tx = 0, errs = 0, drops = 0, util = 0;
        foreach (var o in Read("SELECT Name, BytesReceivedPersec, BytesSentPersec, CurrentBandwidth, PacketsReceivedErrors, PacketsOutboundErrors, " +
                               "PacketsReceivedDiscarded, PacketsOutboundDiscarded FROM Win32_PerfFormattedData_Tcpip_NetworkInterface"))
        {
            var bw = ToD(o.GetValueOrDefault("CurrentBandwidth"));
            if (bw <= 0) continue;   // nem csatlakoztatott / virtualis csonk
            var name = o.GetValueOrDefault("Name")?.ToString() ?? "adapter";
            var key = Safe(name); if (key.Length > 40) key = key[..40];
            var r = ToD(o.GetValueOrDefault("BytesReceivedPersec")) / 1024;
            var s = ToD(o.GetValueOrDefault("BytesSentPersec")) / 1024;
            rx += r; tx += s;
            Add(l, $"net.{key}.rxkbps", $"Fogadás: {name}", r, "KB/s", "Hálózat");
            Add(l, $"net.{key}.txkbps", $"Küldés: {name}", s, "KB/s", "Hálózat");
            // Kihasználtság a kártya sebességéhez (a leterheltebb irány); hibás (pl. CRC) és eldobott csomagok.
            var u = Math.Max(r, s) * 1024 * 8 * 100 / bw;
            util = Math.Max(util, u);
            Add(l, $"net.{key}.utilpct", $"Kihasználtság: {name}", Math.Min(100, u), "%", "Hálózat");
            // A számlálók összesítettek (a Windows indulása óta), nem másodpercenkéntiek.
            errs += ToD(o.GetValueOrDefault("PacketsReceivedErrors")) + ToD(o.GetValueOrDefault("PacketsOutboundErrors"));
            drops += ToD(o.GetValueOrDefault("PacketsReceivedDiscarded")) + ToD(o.GetValueOrDefault("PacketsOutboundDiscarded"));
        }
        Add(l, "net.rxkbps", "Hálózat fogadás (összes)", rx, "KB/s", "Hálózat");
        Add(l, "net.txkbps", "Hálózat küldés (összes)", tx, "KB/s", "Hálózat");
        Add(l, "net.rxmbps", "Hálózat fogadás", rx * 1024 * 8 / 1e6, "Mbps", "Hálózat");
        Add(l, "net.txmbps", "Hálózat küldés", tx * 1024 * 8 / 1e6, "Mbps", "Hálózat");
        Add(l, "net.utilpct", "Hálózat kihasználtság (legterheltebb kártya)", Math.Min(100, util), "%", "Hálózat");
        Add(l, "net.errors", "Hibás csomagok (pl. CRC, indulás óta)", errs, "db", "Hálózat");
        Add(l, "net.drops", "Eldobott csomagok (indulás óta)", drops, "db", "Hálózat");

        // Kapcsolatok állapot szerint - közvetlenül a TCP-táblából (netstat nélkül).
        var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
        var conns = props.GetActiveTcpConnections();
        Add(l, "net.tcp.established", "TCP-kapcsolatok (ESTABLISHED)", conns.Count(c => c.State == System.Net.NetworkInformation.TcpState.Established), "db", "Hálózat");
        Add(l, "net.tcp.timewait", "TCP-kapcsolatok (TIME_WAIT)", conns.Count(c => c.State == System.Net.NetworkInformation.TcpState.TimeWait), "db", "Hálózat");
        Add(l, "net.tcp.total", "TCP-kapcsolatok (összes)", conns.Length, "db", "Hálózat");
        Add(l, "net.tcp.listen", "Figyelő TCP-portok", props.GetActiveTcpListeners().Length, "db", "Hálózat");
        Add(l, "net.udp.listen", "UDP-végpontok", props.GetActiveUdpListeners().Length, "db", "Hálózat");
        foreach (var o in Read("SELECT SegmentsRetransmittedPersec, ConnectionFailures, ConnectionsReset FROM Win32_PerfFormattedData_Tcpip_TCPv4"))
        {
            Add(l, "net.tcp.retransps", "TCP-újraküldés", ToD(o.GetValueOrDefault("SegmentsRetransmittedPersec")), "db/s", "Hálózat");
            Add(l, "net.tcp.resets", "TCP-kapcsolat bontva (reset, indulás óta)", ToD(o.GetValueOrDefault("ConnectionsReset")), "db", "Hálózat");
        }
    }

    // ---------------------------------------------------------------- ping (válaszidő, ingadozás, csomagvesztés)
    private static void Ping(List<Metric> l, Config cfg)
    {
        var targets = new List<(string Key, string Label, string Host)>();
        if (cfg.ServerHost.Length > 0) targets.Add(("server", "Fleet szerver", cfg.ServerHost));
        var gw = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any));
        if (gw is not null) targets.Add(("gw", "Átjáró", gw.ToString()));
        foreach (var t in cfg.PingTargets.Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 100 && Uri.CheckHostName(x) != UriHostNameType.Unknown).Take(8))
            targets.Add((Safe(t), t, t));

        using var ping = new System.Net.NetworkInformation.Ping();
        foreach (var (key, label, host) in targets)
        {
            const int N = 4;
            var times = new List<double>();
            for (int i = 0; i < N; i++)
            {
                try
                {
                    var r = ping.Send(host, 1000);
                    if (r.Status == System.Net.NetworkInformation.IPStatus.Success) times.Add(r.RoundtripTime);
                }
                catch { }
            }
            Add(l, $"ping.{key}.loss", $"Csomagvesztés: {label}", (N - times.Count) * 100.0 / N, "%", "Ping");
            if (times.Count == 0) continue;
            Add(l, $"ping.{key}.ms", $"Válaszidő: {label}", times.Average(), "ms", "Ping");
            // Ingadozás (jitter): az egymást követő válaszidők átlagos eltérése.
            Add(l, $"ping.{key}.jitter", $"Ingadozás (jitter): {label}", times.Count < 2 ? 0 : times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average(), "ms", "Ping");
        }
    }

    // ---------------------------------------------------------------- GPU (a Windows saját számlálóiból)
    private static void Gpu(List<Metric> l)
    {
        // A 3D-motor kihasználtsága adapterenként (a folyamatonkénti példányok összege) - meghajtó nélkül is működik.
        var load = new Dictionary<string, double>();
        foreach (var o in Read("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D'"))
        {
            var name = o.GetValueOrDefault("Name")?.ToString() ?? "";
            var i = name.IndexOf("luid_", StringComparison.Ordinal);
            var j = name.IndexOf("_phys", StringComparison.Ordinal);
            var luid = i >= 0 && j > i ? name[i..j] : "gpu";
            load[luid] = load.GetValueOrDefault(luid) + ToD(o.GetValueOrDefault("UtilizationPercentage"));
        }
        if (load.Count > 0) Add(l, "gpu.load", "GPU 3D-terhelés (legterheltebb kártya)", Math.Min(100, load.Values.Max()), "%", "GPU");
        double ded = 0, shared = 0;
        foreach (var o in Read("SELECT DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
        {
            ded += ToD(o.GetValueOrDefault("DedicatedUsage"));
            shared += ToD(o.GetValueOrDefault("SharedUsage"));
        }
        if (ded > 0) Add(l, "gpu.dedicatedmb", "Videómemória (dedikált, foglalt)", ded / 1024 / 1024, "MB", "GPU");
        if (shared > 0) Add(l, "gpu.sharedmb", "Videómemória (megosztott, foglalt)", shared / 1024 / 1024, "MB", "GPU");
    }

    // ---------------------------------------------------------------- lemez
    private static void Disk(List<Metric> l)
    {
        foreach (var o in Read("SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
        {
            var id = (o.GetValueOrDefault("DeviceID")?.ToString() ?? "?").TrimEnd(':');
            var size = ToD(o.GetValueOrDefault("Size"));
            var free = ToD(o.GetValueOrDefault("FreeSpace"));
            if (size <= 0) continue;
            Add(l, $"disk.{id}.usedpct", $"Lemez {id}: kihasználtság", (size - free) * 100.0 / size, "%", "Lemez");
            Add(l, $"disk.{id}.freegb", $"Lemez {id}: szabad hely", free / 1e9, "GB", "Lemez");
            Add(l, $"disk.{id}.usedgb", $"Lemez {id}: foglalt hely", (size - free) / 1e9, "GB", "Lemez");
            Add(l, $"disk.{id}.totalgb", $"Lemez {id}: méret", size / 1e9, "GB", "Lemez");
        }
    }

    // ---------------------------------------------------------------- SMART
    private static void Smart(List<Metric> l)
    {
        // Ez az egyetlen kategoria masik WMI-namespace-ben (Storage). Ha nincs (regi rendszer),
        // a try/catch elnyeli, es a diagnosztika "0 metrika"-t ir - nem hiba.
        const string ns = @"root\Microsoft\Windows\Storage";
        var names = new Dictionary<string, string>();
        foreach (var o in Read("SELECT DeviceId, FriendlyName, HealthStatus FROM MSFT_PhysicalDisk", ns))
        {
            var name = o.GetValueOrDefault("FriendlyName")?.ToString() ?? "lemez";
            names[o.GetValueOrDefault("DeviceId")?.ToString() ?? ""] = name;
            var health = ToD(o.GetValueOrDefault("HealthStatus"));   // 0=Healthy, 1=Warning, 2/3=Unhealthy
            Add(l, $"smart.{Safe(name)}.health", $"SMART: {name}", health, "", "Lemez",
                health == 0 ? "kifogástalan" : health == 1 ? "figyelmeztetés" : "hibás");
        }

        // Megbízhatósági számlálók (SATA és NVMe is, a Windows tárolókezelőjéből; rendszerjog kell) - lemezenként külön,
        // hogy egy hiányzó ne vigye el a többit.
        try
        {
            foreach (var o in Read("SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, StartStopCycleCount, ReadErrorsUncorrected, " +
                                   "WriteErrorsUncorrected, ReadErrorsTotal, WriteErrorsTotal, ReadLatencyMax, WriteLatencyMax FROM MSFT_StorageReliabilityCounter", ns))
            {
                var name = names.GetValueOrDefault(o.GetValueOrDefault("DeviceId")?.ToString() ?? "", "lemez");
                var k = "smart." + Safe(name);
                void A(string prop, string sk, string label, string unit)
                {
                    if (o.GetValueOrDefault(prop) is { } v) Add(l, $"{k}.{sk}", $"{name}: {label}", ToD(v), unit, "Lemez-állapot");
                }
                if (ToD(o.GetValueOrDefault("Temperature")) > 0) A("Temperature", "temp", "hőmérséklet", "°C");
                A("Wear", "wearpct", "elhasználtság (wear)", "%");
                A("PowerOnHours", "poh", "üzemórák", "óra");
                A("StartStopCycleCount", "cycles", "be-/kikapcsolások", "db");
                A("ReadErrorsUncorrected", "readerr", "javíthatatlan olvasási hibák", "db");
                A("WriteErrorsUncorrected", "writeerr", "javíthatatlan írási hibák", "db");
                A("ReadErrorsTotal", "readerrtotal", "olvasási hibák (összes)", "db");
                A("ReadLatencyMax", "readlatmax", "max. olvasási válaszidő", "ms");
                A("WriteLatencyMax", "writelatmax", "max. írási válaszidő", "ms");
            }
        }
        catch { }

        // Klasszikus (ATA) SMART-attribútumok: áthelyezett / függő / javíthatatlan szektorok stb. NVMe-n nincs ilyen osztály.
        try { AtaSmart(l); } catch { }
    }

    private static void AtaSmart(List<Metric> l)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in Read("SELECT PNPDeviceID, Model FROM Win32_DiskDrive"))
            if (o.GetValueOrDefault("PNPDeviceID")?.ToString() is { } pnp) models[pnp] = o.GetValueOrDefault("Model")?.ToString() ?? "lemez";
        // attribútum-azonosító -> (kulcs, felirat, csak az alsó 2 bájt értelmes-e)
        var attrs = new Dictionary<int, (string Key, string Label)>
        {
            [1] = ("rawreaderr", "nyers olvasási hibaarány"), [5] = ("realloc", "áthelyezett szektorok"), [7] = ("seekerr", "pozicionálási hibaarány"),
            [9] = ("poh", "üzemórák"), [12] = ("cycles", "be-/kikapcsolások"), [177] = ("wearlevel", "kopáskiegyenlítés"),
            [194] = ("temp", "hőmérséklet"), [197] = ("pending", "függő szektorok"), [198] = ("offunc", "offline javíthatatlan"),
            [199] = ("crc", "UDMA CRC-hibák"), [231] = ("life", "SSD hátralévő élettartam"), [241] = ("lbaw", "összes írás (LBA)"), [242] = ("lbar", "összes olvasás (LBA)")
        };
        foreach (var o in Read("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", @"root\WMI"))
        {
            if (o.GetValueOrDefault("VendorSpecific") is not byte[] data || data.Length < 362) continue;
            var inst = o.GetValueOrDefault("InstanceName")?.ToString() ?? "";
            var name = models.FirstOrDefault(m => inst.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase) ||
                                                  inst.Replace("_0", "").Equals(m.Key, StringComparison.OrdinalIgnoreCase)).Value ?? "lemez";
            // 30 darab 12 bájtos bejegyzés a 2. bájttól: azonosító, jelzők (2), aktuális, legrosszabb, nyers érték (6), fenntartott.
            for (int i = 0; i < 30; i++)
            {
                var off = 2 + i * 12;
                int id = data[off];
                if (id == 0 || !attrs.TryGetValue(id, out var a)) continue;
                long raw = 0;
                for (int b = 5; b >= 0; b--) raw = (raw << 8) | data[off + 5 + b];
                // A hőmérséklet és néhány más érték csak az alsó bájt(ok)ban van, a többiben min/max.
                double v = id == 194 ? data[off + 5] : id is 177 or 231 ? data[off + 3] : raw;
                Add(l, $"smart.{Safe(name)}.{a.Key}", $"{name}: {a.Label}", v, id == 194 ? "°C" : id is 177 or 231 ? "%" : id == 9 ? "óra" : "", "Lemez-állapot");
            }
        }
    }

    // ---------------------------------------------------------------- hőmérséklet
    private static void Temperature(List<Metric> l)
    {
        int i = 1;
        foreach (var o in Read("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\WMI"))
        {
            var raw = ToD(o.GetValueOrDefault("CurrentTemperature"));
            if (raw <= 0) continue;
            var c = raw / 10.0 - 273.15;   // tized-Kelvin -> Celsius
            if (c is < -20 or > 150) continue;
            Add(l, $"temp.zone{i}", $"Hőmérséklet (zóna {i})", c, "°C", "Hőmérséklet");
            i++;
        }
    }

    // ---------------------------------------------------------------- szolgáltatások
    private static void Services(List<Metric> l, List<string> names)
    {
        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return;

        // Egyetlen lekerdezes, majd szures - gyorsabb, mint szolgaltatasonkent kerdezni.
        foreach (var o in Read("SELECT Name, State FROM Win32_Service"))
        {
            var name = o.GetValueOrDefault("Name")?.ToString() ?? "";
            if (!wanted.Contains(name)) continue;
            var running = (o.GetValueOrDefault("State")?.ToString() ?? "") == "Running";
            Add(l, $"svc.{Safe(name)}.running", $"Szolgáltatás: {name}", running ? 1 : 0, "", "Szolgáltatás", running ? "fut" : "áll");
        }
    }

    /// <summary>A gép összes szolgáltatása (a szerver „Mit mérjen?” választólistájához).</summary>
    public static List<ServiceEntry> ServiceList()
    {
        try
        {
            return Read("SELECT Name, DisplayName, State, StartMode FROM Win32_Service")
                .Select(o => new ServiceEntry
                {
                    Name = o.GetValueOrDefault("Name")?.ToString() ?? "",
                    DisplayName = o.GetValueOrDefault("DisplayName")?.ToString() ?? "",
                    Running = (o.GetValueOrDefault("State")?.ToString() ?? "") == "Running",
                    Auto = (o.GetValueOrDefault("StartMode")?.ToString() ?? "") == "Auto"
                })
                .Where(s => s.Name.Length is > 0 and <= 256).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(2000).ToList();
        }
        catch { return new(); }
    }

    // ---------------------------------------------------------------- folyamatok
    private static void Processes(List<Metric> l, List<string> names)
    {
        var wanted = names.Select(n => n.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
                          .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (wanted.Contains(p.ProcessName)) counts[p.ProcessName] = counts.GetValueOrDefault(p.ProcessName) + 1; }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var name in wanted)
        {
            var c = counts.GetValueOrDefault(name);
            Add(l, $"proc.{Safe(name)}.count", $"Folyamat: {name}", c, "db", "Folyamat", c > 0 ? $"{c} példány fut" : "nem fut");
        }

        // Erőforrás-használat a futó példányok összegeként (a teljesítményszámlálóból: egy lekérdezés, nem kell két minta).
        var usage = ProcessUsage();
        foreach (var name in wanted)
        {
            if (!usage.TryGetValue(name, out var u)) continue;
            var k = $"proc.{Safe(name)}";
            Add(l, k + ".cpupct", $"{name}: CPU", u.CpuPct, "%", "Folyamat");
            Add(l, k + ".memmb", $"{name}: memória", u.MemMb, "MB", "Folyamat");
            Add(l, k + ".handles", $"{name}: leírók (handle)", u.Handles, "db", "Folyamat");
            Add(l, k + ".threads", $"{name}: szálak", u.Threads, "db", "Folyamat");
        }
    }

    /// <summary>
    /// Folyamatonkénti erőforrás-használat, a példányok (chrome, chrome#1, …) összevonásával. A CPU % a teljes gépre
    /// vetítve (a számláló egy magra számol, ezért osztjuk a logikai processzorok számával); memória = privát munkakészlet.
    /// </summary>
    private static Dictionary<string, ProcessInfo> ProcessUsage()
    {
        var d = new Dictionary<string, ProcessInfo>(StringComparer.OrdinalIgnoreCase);
        var cpus = Math.Max(1, Environment.ProcessorCount);
        foreach (var o in Read("SELECT Name, PercentProcessorTime, WorkingSetPrivate, HandleCount, ThreadCount FROM Win32_PerfFormattedData_PerfProc_Process"))
        {
            var raw = o.GetValueOrDefault("Name")?.ToString() ?? "";
            if (raw is "_Total" or "Idle" or "") continue;
            var hash = raw.IndexOf('#');
            var name = hash > 0 ? raw[..hash] : raw;
            if (!d.TryGetValue(name, out var p)) d[name] = p = new ProcessInfo { Name = name };
            p.Count++;
            p.CpuPct += ToD(o.GetValueOrDefault("PercentProcessorTime")) / cpus;
            p.MemMb += ToD(o.GetValueOrDefault("WorkingSetPrivate")) / 1024 / 1024;
            p.Handles += (int)ToD(o.GetValueOrDefault("HandleCount"));
            p.Threads += (int)ToD(o.GetValueOrDefault("ThreadCount"));
        }
        foreach (var p in d.Values) { p.CpuPct = Math.Round(p.CpuPct, 1); p.MemMb = Math.Round(p.MemMb, 1); }
        return d;
    }

    /// <summary>A legtöbb memóriát és a legtöbb CPU-t használó folyamatok (jelentkezéskor, a gép adatlapjához).</summary>
    public static List<ProcessInfo> TopProcesses(int n = 8)
    {
        try
        {
            var all = ProcessUsage().Values.ToList();
            return all.OrderByDescending(p => p.MemMb).Take(n)
                .Concat(all.OrderByDescending(p => p.CpuPct).Take(n).Where(p => p.CpuPct >= 0.5))
                .DistinctBy(p => p.Name).ToList();
        }
        catch { return new(); }
    }

    // ---------------------------------------------------------------- eseménynapló
    private static void EventLog(List<Metric> l)
    {
        // XPath-szűrős olvasás (EventLogReader): a napló csak a kért szintű / azonosítójú, adott időn belüli
        // bejegyzéseit adja vissza - nem kell a teljes naplót végigolvasni (a Win32_NTLogEvent percekig is tartana).
        const string H1 = "TimeCreated[timediff(@SystemTime) <= 3600000]", D1 = "TimeCreated[timediff(@SystemTime) <= 86400000]";
        int readable = 0;
        double Both(string filter)
        {
            double n = 0;
            foreach (var log in new[] { "System", "Application" })
                if (Count(log, $"*[System[{filter}]]") is { } c) { n += c; readable++; }
            return n;
        }
        var crit = Both($"(Level=1) and {H1}");
        var err = Both($"(Level=2) and {H1}");
        var warn = Both($"(Level=3) and {H1}");
        // Ha egyik naplo sem olvashato, ne kuldjunk "0 hiba" erteket - az hamis nyugalmat adna.
        if (readable == 0) throw new InvalidOperationException("az eseménynapló nem olvasható");
        Add(l, "evt.errors1h", "Naplóhibák (1 óra)", crit + err, "db", "Napló");
        Add(l, "evt.critical1h", "Kritikus események (1 óra)", crit, "db", "Napló");
        Add(l, "evt.warnings1h", "Figyelmeztetések (1 óra)", warn, "db", "Napló");

        void One(string key, string label, string log, string filter)
        {
            if (Count(log, $"*[System[{filter}]]") is { } c) Add(l, key, label, c, "db", "Napló");
        }
        // Lemezhiba: a tárolóvezérlők és a fájlrendszer hibái (pl. disk 7/11/51/153, Ntfs 55/98, stornvme).
        One("evt.diskerr24h", "Lemez- és fájlrendszer-hibák (24 óra)", "System",
            "Provider[@Name='disk' or @Name='Ntfs' or @Name='stornvme' or @Name='storahci' or @Name='iaStorA' or @Name='iaStorAVC' or @Name='volmgr' or @Name='partmgr' or @Name='Microsoft-Windows-Ntfs'] and (Level=1 or Level=2 or Level=3) and " + D1);
        // Szolgáltatás váratlanul leállt (7031: újraindítással, 7034: anélkül).
        One("evt.svccrash24h", "Szolgáltatás-összeomlások (24 óra)", "System", "Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034) and " + D1);
        One("evt.appcrash24h", "Program-összeomlások / lefagyások (24 óra)", "Application", "(EventID=1000 or EventID=1002) and " + D1);
        // Váratlan leállás (áramszünet, lefagyás): Kernel-Power 41, illetve 6008.
        One("evt.unexpshutdown7d", "Váratlan leállások (7 nap)", "System",
            "((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or EventID=6008) and TimeCreated[timediff(@SystemTime) <= 604800000]");
        // Hardverhibák (WHEA): processzor-, memória- (ECC), PCIe-hibák - a javított is jelzés.
        One("evt.whea24h", "Hardverhibák – WHEA, pl. ECC (24 óra)", "System", "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and " + D1);
    }

    /// <summary>Egy napló XPath-szűrőnek megfelelő bejegyzéseinek száma (legfeljebb max-ig); null, ha a napló nem olvasható.</summary>
    private static int? Count(string log, string xpath, int max = 5000)
    {
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                new System.Diagnostics.Eventing.Reader.EventLogQuery(log, System.Diagnostics.Eventing.Reader.PathType.LogName, xpath));
            int n = 0;
            while (n < max)
            {
                using var e = reader.ReadEvent();
                if (e is null) break;
                n++;
            }
            return n;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- biztonsági napló
    private static void Security(List<Metric> l)
    {
        const string H1 = "TimeCreated[timediff(@SystemTime) <= 3600000]";
        // Sikeres belépés: csak a valódi (helyi, távoli asztal, gyorsítótárazott) - a hálózati / szolgáltatás-belépések
        // tartományvezérlőn óránként tízezrek lennének.
        var ok = Count("Security", $"*[System[EventID=4624 and {H1}] and EventData[Data[@Name='LogonType']='2' or Data[@Name='LogonType']='10' or Data[@Name='LogonType']='11']]");
        if (ok is null) throw new InvalidOperationException("a biztonsági napló nem olvasható");
        Add(l, "sec.logonok1h", "Sikeres belépés – helyi / távoli asztal (1 óra)", ok.Value, "db", "Biztonság");
        Add(l, "sec.lockout1h", "Fiókzárolás (1 óra)", Count("Security", $"*[System[EventID=4740 and {H1}]]") ?? 0, "db", "Biztonság");

        // Sikertelen belépés: összesen, és a legtöbb próbálkozás egy forrásból (IP vagy felhasználó) - ez jelzi a jelszótörést.
        var bySource = new Dictionary<string, int>();
        int fail = 0;
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "Security", System.Diagnostics.Eventing.Reader.PathType.LogName, $"*[System[EventID=4625 and {H1}]]"));
            while (fail < 5000)
            {
                using var e = reader.ReadEvent();
                if (e is null) break;
                fail++;
                var p = e.Properties;
                var ip = p.Count > 19 ? p[19].Value?.ToString() ?? "" : "";
                var src = ip.Length > 1 && ip != "-" ? ip : "fiók: " + (p.Count > 5 ? p[5].Value?.ToString() ?? "?" : "?");
                bySource[src] = bySource.GetValueOrDefault(src) + 1;
            }
        }
        catch { }
        Add(l, "sec.logonfail1h", "Sikertelen belépés (1 óra)", fail, "db", "Biztonság");
        var top = bySource.OrderByDescending(kv => kv.Value).FirstOrDefault();
        l.Add(new Metric { Key = "sec.failmaxsrc1h", Label = "Próbálkozás egy forrásból – jelszótörés? (1 óra)", Value = top.Value, Unit = "db",
                           Category = "Biztonság", Text = top.Key ?? "" });
    }

    // ---------------------------------------------------------------- védelmi állapot (1.5.6)
    /// <summary>
    /// Vírusvédelem (Defender: MSFT_MpComputerStatus; más termék: SecurityCenter2 - csak kliens Windowson), a Windows tűzfal
    /// profiljai (MSFT_NetFirewallProfile) és a BitLocker (Win32_EncryptableVolume). Mind WMI, folyamatindítás nélkül.
    /// </summary>
    private static void Protection(List<Metric> l)
    {
        bool? av = null, rtp = null; double? sigAge = null;
        try
        {
            foreach (var o in Read("SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender"))
            {
                av = o.GetValueOrDefault("AntivirusEnabled") is true; rtp = o.GetValueOrDefault("RealTimeProtectionEnabled") is true;
                if (o.GetValueOrDefault("AntivirusSignatureAge") is { } a) sigAge = ToD(a);
            }
        }
        catch { }
        try
        {
            // Más vírusirtó (pl. ESET): a productState 2. bájtja 0x10 = bekapcsolva (a Defender ilyenkor passzív).
            foreach (var o in Read("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2"))
                if (o.GetValueOrDefault("productState") is { } ps && ((Convert.ToInt64(ps) >> 8) & 0x10) != 0) { av = true; rtp ??= true; }
        }
        catch { }
        if (av is not null) Add(l, "sec.av.enabled", "Vírusvédelem bekapcsolva", av.Value ? 1 : 0, "", "Biztonság", av.Value ? "igen" : "NEM");
        if (rtp is not null) Add(l, "sec.av.realtime", "Valós idejű védelem", rtp.Value ? 1 : 0, "", "Biztonság", rtp.Value ? "igen" : "NEM");
        if (sigAge is not null) Add(l, "sec.av.sigage", "Vírusdefiníciók kora", sigAge.Value, "nap", "Biztonság");
        try
        {
            int on = 0, all = 0;
            foreach (var o in Read("SELECT Name, Enabled FROM MSFT_NetFirewallProfile", @"root\StandardCimv2"))
            { all++; if (ToD(o.GetValueOrDefault("Enabled")) == 1) on++; }
            if (all > 0) Add(l, "sec.fw.offprofiles", "Kikapcsolt tűzfal-profilok", all - on, "db", "Biztonság");
        }
        catch { }
        try
        {
            var sys = Environment.SystemDirectory[..2];
            foreach (var o in Read("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", @"root\cimv2\security\microsoftvolumeencryption"))
                if (o.GetValueOrDefault("DriveLetter")?.ToString() is { Length: >= 2 } dl && dl[..2].Equals(sys, StringComparison.OrdinalIgnoreCase))
                    Add(l, "sec.bitlocker.sys", "BitLocker a rendszermeghajtón", ToD(o.GetValueOrDefault("ProtectionStatus")) == 1 ? 1 : 0, "", "Biztonság");
        }
        catch { }
    }

    // ---------------------------------------------------------------- driverek és firmware (1.5.6, CVE-kereséshez)
    /// <summary>
    /// A külső gyártók driverei (Win32_PnPSignedDriver; a Microsoft sajátjai a Windows Update-tel frissülnek, azokat a
    /// Windows-frissítések lefedik), a BIOS (gép gyártója + típusa + BIOS-verzió) és a lemezek firmware-e. Lassú lekérdezés
    /// (néhány másodperc): az agent csak óránként hívja, és csak változáskor küldi.
    /// </summary>
    public static List<ComponentInfo> Components()
    {
        var l = new List<ComponentInfo>();
        static string S(Dictionary<string, object?> o, string k) => (o.GetValueOrDefault(k)?.ToString() ?? "").Trim();
        static string D(object? dmtf)
        {
            var s = dmtf?.ToString() ?? "";
            return s.Length >= 8 && s[..8].All(char.IsDigit) ? $"{s[..4]}-{s[4..6]}-{s[6..8]}" : "";
        }
        try
        {
            foreach (var o in Read("SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver"))
            {
                var prov = S(o, "DriverProviderName"); var name = S(o, "DeviceName"); var ver = S(o, "DriverVersion");
                if (name.Length == 0 || ver.Length == 0 || prov.Length == 0 || prov.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                l.Add(new ComponentInfo { Kind = "driver", Vendor = prov, Name = name, Version = ver, Date = D(o.GetValueOrDefault("DriverDate")), Detail = S(o, "DeviceClass") });
            }
        }
        catch { }
        try
        {
            var cs = Read("SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem").FirstOrDefault();
            var pr = Read("SELECT Version FROM Win32_ComputerSystemProduct").FirstOrDefault();
            foreach (var b in Read("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            {
                var vendor = cs is null ? "" : S(cs, "Manufacturer");
                // A Lenovónál a Model a gép típuskódja (pl. 20XW…), a beszédes név a termékverzióban van (ThinkPad X1 Carbon Gen 9).
                var model = cs is null ? "" : S(cs, "Model");
                if (vendor.StartsWith("LENOVO", StringComparison.OrdinalIgnoreCase) && pr is not null && S(pr, "Version") is { Length: > 0 } lv) model = lv;
                l.Add(new ComponentInfo { Kind = "bios", Vendor = vendor, Name = model, Version = S(b, "SMBIOSBIOSVersion"), Date = D(b.GetValueOrDefault("ReleaseDate")), Detail = S(b, "Manufacturer") });
            }
        }
        catch { }
        try
        {
            foreach (var o in Read("SELECT Model, FirmwareRevision FROM Win32_DiskDrive"))
                if (S(o, "Model") is { Length: > 0 } m && S(o, "FirmwareRevision") is { Length: > 0 } fw)
                    l.Add(new ComponentInfo { Kind = "disk", Vendor = m.Split(' ', 2)[0], Name = m, Version = fw });
        }
        catch { }
        // Egy eszköztípusból több példány is lehet (pl. USB-portok): elég egyszer.
        return l.GroupBy(c => c.Kind + "|" + c.Vendor + "|" + c.Name + "|" + c.Version, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).OrderBy(c => c.Kind).ThenBy(c => c.Vendor).ThenBy(c => c.Name).Take(300).ToList();
    }

    // ---------------------------------------------------------------- Hyper-V
    private static bool? _hvPresent;
    private static DateTime _hvChecked = DateTime.MinValue;

    /// <summary>Van-e Hyper-V a gépen (óránként egyszer nézzük - a hiányzó névtér minden mintánál kivételt adna).</summary>
    private static bool HyperVPresent()
    {
        if (_hvPresent is { } p && DateTime.UtcNow - _hvChecked < TimeSpan.FromHours(1)) return p;
        _hvChecked = DateTime.UtcNow;
        try { _hvPresent = Read("SELECT Name FROM Msvm_VirtualSystemManagementService", @"root\virtualization\v2").Count > 0; }
        catch { _hvPresent = false; }
        return _hvPresent.Value;
    }

    private static void HyperV(List<Metric> l, bool includeSlow)
    {
        const string ns = @"root\virtualization\v2";
        var vms = new Dictionary<string, string>();   // VM-azonosító -> név
        int running = 0;
        foreach (var o in Read("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem", ns))
        {
            var id = o.GetValueOrDefault("Name")?.ToString() ?? "";
            if (!Guid.TryParse(id, out _)) continue;   // a gazdagép maga (a neve a gépnév) kimarad
            var name = o.GetValueOrDefault("ElementName")?.ToString() ?? id;
            vms[id] = name;
            var on = ToD(o.GetValueOrDefault("EnabledState")) == 2;   // 2 = fut, 3 = ki, 32768 = szünetel, 32769 = mentve
            if (on) running++;
            Add(l, $"hv.vm.{Safe(name)}.running", $"VM fut: {name}", on ? 1 : 0, "", "Hyper-V");
        }
        Add(l, "hv.vms.total", "Virtuális gépek", vms.Count, "db", "Hyper-V");
        Add(l, "hv.vms.running", "Futó virtuális gépek", running, "db", "Hyper-V");

        // Processzor-használat VM-enként (a virtuális processzorok átlaga) és a dinamikus memória.
        var cpu = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var o in Read("SELECT Name, PercentTotalRunTime FROM Win32_PerfFormattedData_HvStats_HyperVHypervisorVirtualProcessor"))
            {
                var n = o.GetValueOrDefault("Name")?.ToString() ?? "";
                var c = n.IndexOf(':');
                if (c <= 0) continue;
                var vm = n[..c];
                (cpu.TryGetValue(vm, out var list) ? list : cpu[vm] = new()).Add(ToD(o.GetValueOrDefault("PercentTotalRunTime")));
            }
            foreach (var (vm, list) in cpu) Add(l, $"hv.vm.{Safe(vm)}.cpupct", $"VM processzor: {vm}", list.Average(), "%", "Hyper-V");
        }
        catch { }
        try
        {
            foreach (var o in Read("SELECT Name, PhysicalMemory FROM Win32_PerfFormattedData_BalancerStats_HyperVDynamicMemoryVM"))
                if (o.GetValueOrDefault("Name")?.ToString() is { Length: > 0 } vm && vm != "_Total")
                    Add(l, $"hv.vm.{Safe(vm)}.memmb", $"VM memória: {vm}", ToD(o.GetValueOrDefault("PhysicalMemory")), "MB", "Hyper-V");
        }
        catch { }

        if (!includeSlow) return;
        // Ellenőrzőpontok (checkpoint): a régen ott felejtett ellenőrzőpont lassítja a VM-et és fogyasztja a lemezt.
        var cps = new Dictionary<string, int>();
        foreach (var o in Read("SELECT VirtualSystemIdentifier FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType='Microsoft:Hyper-V:Snapshot:Realized'", ns))
        {
            var id = o.GetValueOrDefault("VirtualSystemIdentifier")?.ToString() ?? "";
            cps[id] = cps.GetValueOrDefault(id) + 1;
        }
        Add(l, "hv.checkpoints", "Ellenőrzőpontok (összes)", cps.Values.Sum(), "db", "Hyper-V");
        foreach (var (id, name) in vms) Add(l, $"hv.vm.{Safe(name)}.checkpoints", $"VM ellenőrzőpontjai: {name}", cps.GetValueOrDefault(id), "db", "Hyper-V");
    }

    // ---------------------------------------------------------------- WMI segéd
    /// <summary>
    /// WMI-lekerdezes: az eredmenyt AZONNAL kimasolja egy listaba (kulcs->ertek szotarak),
    /// es minden ManagementObject-et Dispose-ol. Igy nincs szivargas, es a hiba itt, a
    /// hivo try/catch-eben keletkezik - nem kesobb, egy lusta iteralas kozben.
    /// </summary>
    // A WMI-kapcsolat nevterenkent megmarad: a percenkenti mintavetel kb. 10 lekerdezese ne kapcsolodjon
    // ujra minden alkalommal. A lekerdezesek sorban futnak (a mintavevo es a jelentkezes ne zavarja egymast).
    private static readonly object WmiLock = new();
    private static readonly Dictionary<string, ManagementScope> Scopes = new(StringComparer.OrdinalIgnoreCase);

    private static List<Dictionary<string, object?>> Read(string wql, string scope = @"root\cimv2")
    {
        var result = new List<Dictionary<string, object?>>();
        var opt = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(20) };
        lock (WmiLock)
        {
            if (!Scopes.TryGetValue(scope, out var ms) || !ms.IsConnected)
            {
                ms = new ManagementScope(scope);
                ms.Connect();
                Scopes[scope] = ms;
            }
            try
            {
                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql), opt);
                using var coll = searcher.Get();
                foreach (ManagementObject o in coll)
                {
                    using (o)
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (PropertyData pd in o.Properties) row[pd.Name] = pd.Value;
                        result.Add(row);
                    }
                }
            }
            catch { Scopes.Remove(scope); throw; }   // pl. a WMI-szolgaltatas ujraindult: legkozelebb ujrakapcsolodunk
        }
        return result;
    }

    private static double ToD(object? o) => o is null ? 0 : double.TryParse(o.ToString(), out var d) ? d : 0;
    private static string Safe(string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
}
