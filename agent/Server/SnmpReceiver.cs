using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// SNMP-trap fogadás a telephelyi szerveren (a felhasználó kérése, 1.5.5). Alapból KI. Bekapcsolva UDP-n figyel
/// (alap: 162), és csak a megengedett címekről fogad (alapból a belső / magán címtartományok - a tűzfalszabály is csak
/// ezekről enged be). Forrásonként terheléskorlát, a csomag legfeljebb 8 KB. A feldolgozás a SnmpEngine-ben (Snmp.cs).
///
/// A trapek a naplómappa snmp.log fájljába kerülnek (JSON soronként, forgatva), az utolsó 1000 a memóriában is (felület).
/// A riasztást adó trapek (SnmpAlertOids) a riasztásnaplóba, és a Központba is felmennek (SiteSummary.SnmpEvents) -
/// onnan e-mail / Janus.
/// </summary>
public static class SnmpReceiver
{
    private static readonly object Lock = new();
    private static UdpClient? _udp;
    private static CancellationTokenSource? _cts;
    private static int _port;
    private static SnmpEngine? _engine;
    private static string _dataDir = "";
    private static Action<AlertEvent>? _raise;
    private static List<(IPAddress Net, int Prefix)> _allow = new();
    private static List<string> _alertOids = new();
    private static readonly LinkedList<SnmpTrap> Recent = new();
    private static long _lastId;
    private static readonly Dictionary<string, (long Window, int Count)> Rate = new();

    public static string Status { get; private set; } = "kikapcsolva";
    public static string Error { get; private set; } = "";
    public static long Received, Accepted, Rejected, NotAllowed, RateLimited, TooBig;
    public static DateTime? LastAtUtc { get; private set; }
    public static SnmpEngine? Engine => _engine;

    public const int MaxPacket = 8192, PerSourcePer10s = 200, RecentMax = 1000;
    /// <summary>
    /// 1.5.11: globális korlát 10 mp-enként (minden forrás együtt), és a forrás-tábla mérete. Az UDP-forráscím hamisítható,
    /// a megengedett belső tartományok pedig nagyok: korábban 5000 forrás fölött a tábla TELJESEN törlődött, így hamis
    /// forrásokkal a forrásonkénti korlát nullázható volt. Most a tábla nem ürül ki: tele állapotban az új forrás csomagja eldobódik.
    /// </summary>
    public const int GlobalPer10s = 2000, MaxRateSources = 5000;
    private static long _gWin = -1, _prunedWin = -1;
    private static int _gCnt;

    /// <summary>A Windows tűzfalszabály kezelése (a teszt kikapcsolja: emelt jogú futtatásnál se hozzon létre szabályt).</summary>
    public static bool ManageFirewall { get; set; } = true;
    /// <summary>A tűzfalszabály neve (az eltávolítás is ezt törli) és a belső tartományok (mint a többi szabálynál).</summary>
    public const string FwRule = "DevToolbox Fleet - SNMP";
    public const string FirewallRanges = "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,169.254.0.0/16,fc00::/7,fe80::/10";

    public static readonly string[] DefaultAllow =
        { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16", "127.0.0.0/8", "fc00::/7", "fe80::/10", "::1/128" };

    /// <summary>Egy cím / CIDR ellenőrzése (felületi bevitelhez).</summary>
    public static bool TryNet(string s, out (IPAddress Net, int Prefix) net)
    {
        net = default;
        var p = s.Trim().Split('/');
        if (p.Length is < 1 or > 2 || !IPAddress.TryParse(p[0], out var ip)) return false;
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (p.Length == 2 && (!int.TryParse(p[1], out prefix) || prefix < 0 || prefix > max)) return false;
        net = (ip, prefix);
        return true;
    }

    public static bool InNet(IPAddress ip, (IPAddress Net, int Prefix) n)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var a = ip.GetAddressBytes(); var b = n.Net.GetAddressBytes();
        if (a.Length != b.Length) return false;
        int full = n.Prefix / 8, rest = n.Prefix % 8;
        for (int i = 0; i < full; i++) if (a[i] != b[i]) return false;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[full] & mask) == (b[full] & mask);
    }

    public static bool Allowed(IPAddress ip) { lock (Lock) return _allow.Any(n => InNet(ip, n)); }

    /// <summary>Indításkor: az utolsó trapek a naplóból; a riasztás-kezelő (a szerver AlertLog-ja).</summary>
    public static void Init(string dataDir, Action<AlertEvent> raise)
    {
        _dataDir = dataDir; _raise = raise;
        var lines = new List<string>();
        foreach (var f in new[] { "snmp.log.1", "snmp.log" })
            try { var p = LogFolder.PathOf(dataDir, f); if (File.Exists(p)) lines.AddRange(File.ReadLines(p).TakeLast(RecentMax)); } catch { }
        lock (Lock)
            foreach (var l in lines.TakeLast(RecentMax))
                try
                {
                    if (JsonSerializer.Deserialize<SnmpTrap>(l, ProtocolJson.Options) is { } t)
                    { Recent.AddFirst(t); _lastId = Math.Max(_lastId, t.Id); }
                }
                catch { }
    }

    /// <summary>
    /// A beállítás alkalmazása (indításkor és mentéskor): az engine újraépítése (a jelszavakból kulcs), a port megnyitása
    /// vagy bezárása, tűzfalszabály. A config-ot módosítja (engineID, boots) - a hívó menti. Visszaadja a hibát.
    /// </summary>
    public static string? Apply(ServerConfig cfg)
    {
        lock (Lock)
        {
            if (!cfg.SnmpEnabled)
            {
                Stop();
                Status = "kikapcsolva"; Error = "";
                if (ManageFirewall && OperatingSystem.IsWindows()) NativeFirewall.Delete(FwRule);
                return null;
            }
            if (cfg.SnmpEngineId.Length == 0) cfg.SnmpEngineId = Convert.ToHexString(SnmpEngine.NewEngineId());
            // Új engine = új „indulás” (RFC 3414): a boots nő, a küldők a következő informnál újraszinkronizálnak.
            cfg.SnmpEngineBoots = cfg.SnmpEngineBoots is >= 0 and < int.MaxValue - 1 ? cfg.SnmpEngineBoots + 1 : 1;
            var users = new List<SnmpUserKeys>();
            foreach (var u in cfg.SnmpUsers)
                try
                {
                    users.Add(SnmpUserKeys.Create(u.Name, Usm.Auth(u.Auth), NativeDpapi.Unprotect(u.AuthKeyProtected) ?? "",
                        Usm.Priv(u.Priv), NativeDpapi.Unprotect(u.PrivKeyProtected) ?? ""));
                }
                catch { Error = $"A(z) {u.Name} v3-felhasználó kulcsa nem olvasható – add meg újra a jelszavát."; }
            _engine = new SnmpEngine(Convert.FromHexString(cfg.SnmpEngineId), cfg.SnmpEngineBoots, users, cfg.SnmpCommunities);
            _allow = (cfg.SnmpAllowFrom.Count > 0 ? cfg.SnmpAllowFrom : DefaultAllow.ToList())
                .Select(s => TryNet(s, out var n) ? n : ((IPAddress Net, int Prefix)?)null).Where(n => n is not null).Select(n => n!.Value).ToList();
            _alertOids = cfg.SnmpAlertOids.ToList();

            if (_udp is null || _port != cfg.SnmpPort)
            {
                Stop();
                try
                {
                    UdpClient udp;
                    UdpClient? v6 = null;
                    try
                    {
                        v6 = new UdpClient(AddressFamily.InterNetworkV6);
                        v6.Client.DualMode = true;   // IPv4 és IPv6 egy socketen
                        v6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, cfg.SnmpPort));
                        udp = v6;
                    }
                    catch (SocketException ex)
                    {
                        v6?.Dispose();   // a félig megnyitott socket ne maradjon nyitva (foglalt portnál sem)
                        // 1.5.11: IPv6 nélküli (letiltott) gépen csak IPv4-en figyelünk – korábban így a fogadó el sem indult.
                        if (ex.SocketErrorCode is not (SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)) throw;
                        udp = new UdpClient(new IPEndPoint(IPAddress.Any, cfg.SnmpPort));
                    }
                    _udp = udp; _port = cfg.SnmpPort;
                    _cts = new CancellationTokenSource();
                    var ct = _cts.Token;
                    _ = Task.Run(() => Loop(udp, ct));
                }
                catch (SocketException ex)
                {
                    Status = "HIBA";
                    Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                        ? $"A(z) {cfg.SnmpPort}-es UDP-portot más program foglalja (gyakran a Windows „SNMP Trap” szolgáltatása – állítsd le, vagy válassz másik portot)."
                        : $"A(z) {cfg.SnmpPort}-es UDP-port nem nyitható meg: {ex.Message}";
                    return Error;
                }
            }
            if (ManageFirewall && OperatingSystem.IsWindows())
                try
                {
                    NativeFirewall.Replace(FwRule, new[] { cfg.SnmpPort },
                        cfg.SnmpAllowFrom.Count > 0 ? string.Join(",", cfg.SnmpAllowFrom) : FirewallRanges,
                        "DevToolbox Fleet Server - SNMP-trap fogadás", udp: true);
                }
                catch (Exception ex) { Error = "Tűzfalszabály: " + ex.Message; }
            Status = $"figyel (UDP {cfg.SnmpPort})";
            return null;
        }
    }

    private static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null; _cts = null; _port = 0;
    }

    private static async Task Loop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }   // pl. ICMP „port unreachable” visszhang: a figyelés megy tovább
            try { await Handle(udp, r.Buffer, r.RemoteEndPoint); } catch { }
        }
    }

    /// <summary>Egy csomag feldolgozása (a teszt közvetlenül is hívhatja: udp null = nincs válasz-küldés).</summary>
    public static async Task<SnmpEngine.Result?> Handle(UdpClient? udp, byte[] data, IPEndPoint from)
    {
        Interlocked.Increment(ref Received);
        var ip = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
        if (!Allowed(ip)) { Interlocked.Increment(ref NotAllowed); return null; }
        if (data.Length > MaxPacket) { Interlocked.Increment(ref TooBig); return null; }
        var key = ip.ToString();
        lock (Lock)
        {
            var w = Environment.TickCount64 / 10000;
            // Globális korlát: a hamisított forrásokból jövő áradat se terhelje korlátlanul a CPU-t / naplót / a válaszokat.
            if (_gWin != w) { _gWin = w; _gCnt = 0; }
            if (++_gCnt > GlobalPer10s) { Interlocked.Increment(ref RateLimited); return null; }
            if (!Rate.ContainsKey(key) && Rate.Count >= MaxRateSources)
            {
                // Tele: ablakonként egyszer kiürítjük az elavult (nem az aktuális ablakbeli) forrásokat; ha így sincs hely,
                // az ÚJ forrás csomagja eldobódik – a meglévők számlálója nem nullázódik.
                if (_prunedWin != w)
                {
                    _prunedWin = w;
                    foreach (var k in Rate.Where(e => e.Value.Window != w).Select(e => e.Key).ToList()) Rate.Remove(k);
                }
                if (Rate.Count >= MaxRateSources) { Interlocked.Increment(ref RateLimited); return null; }
            }
            var (win, cnt) = Rate.TryGetValue(key, out var x) ? x : (w, 0);
            if (win != w) { win = w; cnt = 0; }
            if (++cnt > PerSourcePer10s) { Rate[key] = (win, cnt); Interlocked.Increment(ref RateLimited); return null; }
            Rate[key] = (win, cnt);
        }
        var engine = _engine;
        if (engine is null) return null;
        var res = engine.Process(data, key);
        if (res.Reply is { } reply && udp is not null)
            try { await udp.SendAsync(reply, reply.Length, from); } catch { }
        if (res.Trap is { } t) Store(t);
        else if (res.Error is not null) Interlocked.Increment(ref Rejected);
        return res;
    }

    private static bool IsAlert(string oid, List<string> list) =>
        list.Any(p => p == "*" || oid == p || oid.StartsWith(p + ".", StringComparison.Ordinal));

    private static void Store(SnmpTrap t)
    {
        Interlocked.Increment(ref Accepted);
        lock (Lock)
        {
            // Időrendben növő azonosító (a Központ ez alapján veszi át az újakat, a szerver újraindítása után is).
            var id = DateTime.UtcNow.Ticks;
            _lastId = id = Math.Max(id, _lastId + 1);
            t.Id = id;
            t.Alert = IsAlert(t.TrapOid, _alertOids);
            Recent.AddFirst(t);
            while (Recent.Count > RecentMax) Recent.RemoveLast();
            LastAtUtc = t.AtUtc;
        }
        LogFolder.Append(_dataDir.Length > 0 ? _dataDir : ServerPaths.DataDir, "snmp.log", "snmp.log.1", JsonSerializer.Serialize(t, ProtocolJson.Options) + "\n");
        if (t.Alert)
            try { _raise?.Invoke(new AlertEvent("SNMP: " + t.TrapName, "snmp:" + t.Source, t.Source, 0, 0, "", true, t.AtUtc, t.Text)); } catch { }
    }

    /// <summary>A legutóbbi trapek (legújabb elöl), szűrve: forrás, név, OID, szöveg.</summary>
    public static (int Total, List<SnmpTrap> Items) List(string q, int page, int size = 100)
    {
        lock (Lock)
        {
            var all = Recent.Where(t => q.Length == 0 || t.Source.Contains(q, StringComparison.OrdinalIgnoreCase) || t.TrapName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                        || t.TrapOid.Contains(q, StringComparison.Ordinal) || t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            return (all.Count, all.Skip(Math.Max(0, page) * size).Take(size).ToList());
        }
    }

    /// <summary>A Központnak: a legutóbbi riasztást adó trapek.</summary>
    public static List<SiteSnmpEvent> AlertEvents(int n)
    {
        lock (Lock)
            return Recent.Where(t => t.Alert).Take(n).Reverse()
                .Select(t => new SiteSnmpEvent { Id = t.Id, AtUtc = t.AtUtc, Source = t.Source, Name = t.TrapName, Text = t.Text }).ToList();
    }

    /// <summary>Próba-trap a saját portra (v2c, az első közösségnévvel): ellenőrzi, hogy a fogadás működik.</summary>
    public static async Task<string?> SendTest(ServerConfig cfg)
    {
        if (!cfg.SnmpEnabled || _udp is null) return "Az SNMP-fogadás nincs bekapcsolva / nem figyel.";
        if (cfg.SnmpCommunities.Count == 0) return "Nincs közösségnév (community) beállítva – a próba v2c-trapet küld.";
        var pdu = Ber.Tlv(SnmpPdu.TrapV2, Ber.Concat(Ber.Int(Random.Shared.Next(1, int.MaxValue)), Ber.Int(0), Ber.Int(0),
            Ber.Seq(Ber.Seq(Ber.Oid(SnmpEngine.SysUpTime), Ber.UInt(0x43, (ulong)Environment.TickCount64 / 10)),
                    Ber.Seq(Ber.Oid(SnmpEngine.SnmpTrapOid), Ber.Oid("1.3.6.1.4.1.8072.2.3.0.1")),
                    Ber.Seq(Ber.Oid("1.3.6.1.2.1.1.5.0"), Ber.Oct(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName))))));
        var msg = Ber.Seq(Ber.Int(1), Ber.Oct(System.Text.Encoding.UTF8.GetBytes(cfg.SnmpCommunities[0])), pdu);
        try
        {
            using var c = new UdpClient();
            await c.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Loopback, cfg.SnmpPort));
            return null;
        }
        catch (Exception ex) { return "A próba-trap nem küldhető: " + ex.Message; }
    }
}
