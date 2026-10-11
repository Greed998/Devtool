using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DevToolbox.Fleet.Server;

// ============================================================================================================
//  WireGuard a telephely szervere és a Központ között (1.5.9, a felhasználó kérése és döntései): a hivatalos, aláírt
//  wireguard-nt (wireguard.dll, VÁLTOZATLANUL, csak a wireguard.h felületén – lásd ThirdParty/wireguard-nt), UDP 51820,
//  SZIGORÚ mód: a Központ a telephely kéréseit csak az alagútból fogadja. Az alagúton belül a forgalom továbbra is
//  aláírt (ECDSA) – a WireGuard egy további réteg, nem helyettesíti az aláírást.
// ============================================================================================================

/// <summary>WireGuard-kulcsok: X25519 (RFC 7748), base64 alakban, mint a wg-eszközöknél.</summary>
public static class WgKeys
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger A24 = 121665;

    public static byte[] NewPrivate()
    {
        var k = RandomNumberGenerator.GetBytes(32);
        k[0] &= 248; k[31] &= 127; k[31] |= 64;
        return k;
    }

    public static byte[] PublicOf(byte[] priv) { var b = new byte[32]; b[0] = 9; return X25519(priv, b); }

    public static string PublicOf(string privB64) => Convert.ToBase64String(PublicOf(Convert.FromBase64String(privB64)));

    public static bool IsKey(string? b64)
    {
        if (string.IsNullOrEmpty(b64) || b64.Length != 44) return false;
        try { return Convert.FromBase64String(b64).Length == 32; } catch { return false; }
    }

    /// <summary>X25519 skalárszorzás (Montgomery-létra, RFC 7748 5. fejezet).</summary>
    public static byte[] X25519(byte[] scalar, byte[] u)
    {
        var k = (byte[])scalar.Clone(); k[0] &= 248; k[31] &= 127; k[31] |= 64;
        var uu = (byte[])u.Clone(); uu[31] &= 127;
        BigInteger x1 = FromLe(uu), x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        var swap = 0;
        for (var t = 254; t >= 0; t--)
        {
            var kt = (k[t >> 3] >> (t & 7)) & 1;
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;
            var a = M(x2 + z2); var aa = M(a * a); var b = M(x2 - z2); var bb = M(b * b); var e = M(aa - bb);
            var c = M(x3 + z3); var d = M(x3 - z3); var da = M(d * a); var cb = M(c * b);
            x3 = M((da + cb) * (da + cb)); z3 = M(x1 * M((da - cb) * (da - cb)));
            x2 = M(aa * bb); z2 = M(e * (aa + A24 * e));
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
        return ToLe(M(x2 * BigInteger.ModPow(z2, P - 2, P)));
    }

    private static BigInteger M(BigInteger v) { v %= P; return v.Sign < 0 ? v + P : v; }
    private static BigInteger FromLe(byte[] b) => new(b, isUnsigned: true, isBigEndian: false);
    private static byte[] ToLe(BigInteger v)
    {
        var r = new byte[32];
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(b, r, Math.Min(32, b.Length));
        return r;
    }
}

/// <summary>Az alagút címtartománya (IPv4 CIDR, pl. 10.77.0.0/24): a Központ az első cím, a telephelyek utána.</summary>
public sealed record WgNet(uint Network, int Prefix)
{
    public static WgNet? Parse(string? cidr)
    {
        var p = (cidr ?? "").Trim().Split('/');
        if (p.Length != 2 || !IPAddress.TryParse(p[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(p[1], out var pre) || pre is < 16 or > 29) return null;
        var n = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes()) & Mask(pre);
        // csak magáncím-tartomány (az alagút ne ütközzön nyilvános címekkel)
        var first = n >> 24; var second = (n >> 16) & 0xFF;
        if (!(first == 10 || (first == 172 && second is >= 16 and <= 31) || (first == 192 && second == 168) || (first == 100 && second is >= 64 and <= 127))) return null;
        return new WgNet(n, pre);
    }

    private static uint Mask(int pre) => pre == 0 ? 0 : uint.MaxValue << (32 - pre);
    public int Size => 1 << (32 - Prefix);
    public string Cidr => $"{Ip(Network)}/{Prefix}";
    public static string Ip(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return new IPAddress(b).ToString(); }
    /// <summary>Az n. gépcím (1 = a Központ); a hálózati és a szórási cím nem adható.</summary>
    public string? Host(int n) => n >= 1 && n < Size - 1 ? Ip(Network + (uint)n) : null;
    public bool Contains(IPAddress? a)
    {
        if (a is null) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        return (BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes()) & Mask(Prefix)) == Network;
    }
}

/// <summary>Egy WireGuard-társ a konfigurációban (a Központban: a telephelyek; a telephelyen: a Központ).</summary>
public sealed record WgPeer(string PublicKey, string AllowedIp, int AllowedCidr, IPEndPoint? Endpoint = null, int Keepalive = 0);

/// <summary>Egy társ állapota (a meghajtótól): utolsó kézfogás, forgalom.</summary>
public sealed record WgPeerStat(string PublicKey, DateTime? LastHandshakeUtc, ulong RxBytes, ulong TxBytes, string Endpoint);

/// <summary>
/// A wireguard-nt konfigurációs puffere (a wireguard.h szerkezetei, x64, 8 bájtra igazítva): WIREGUARD_INTERFACE (80 bájt),
/// utána társonként WIREGUARD_PEER (136 bájt) és az engedélyezett címei (WIREGUARD_ALLOWED_IP, 24 bájt). A Fleet csak ezt a
/// felületet használja (a licenc szerinti „Permitted API”).
/// </summary>
public static class WgConfig
{
    public const int IfaceSize = 80, PeerSize = 136, AllowedSize = 24;
    private const int IfHasPrivate = 1 << 1, IfHasListenPort = 1 << 2, IfReplacePeers = 1 << 3;
    private const int PeerHasPublic = 1 << 0, PeerHasKeepalive = 1 << 2, PeerHasEndpoint = 1 << 3, PeerReplaceAllowed = 1 << 5;

    public static byte[] Build(byte[] privateKey, int listenPort, IReadOnlyList<WgPeer> peers)
    {
        var buf = new byte[IfaceSize + peers.Count * (PeerSize + AllowedSize)];
        var flags = IfHasPrivate | IfReplacePeers | (listenPort > 0 ? IfHasListenPort : 0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), (ushort)Math.Max(0, listenPort));
        privateKey.CopyTo(buf, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(72), (uint)peers.Count);
        var o = IfaceSize;
        foreach (var p in peers)
        {
            var pf = PeerHasPublic | PeerReplaceAllowed | (p.Keepalive > 0 ? PeerHasKeepalive : 0) | (p.Endpoint is not null ? PeerHasEndpoint : 0);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), pf);
            Convert.FromBase64String(p.PublicKey).CopyTo(buf, o + 8);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 72), (ushort)Math.Clamp(p.Keepalive, 0, 65535));
            if (p.Endpoint is { } ep) WriteSockaddr(buf.AsSpan(o + 76, 28), ep);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 128), 1);
            o += PeerSize;
            IPAddress.Parse(p.AllowedIp).GetAddressBytes().CopyTo(buf, o);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 16), (ushort)AddressFamily.InterNetwork);
            buf[o + 18] = (byte)p.AllowedCidr;
            o += AllowedSize;
        }
        return buf;
    }

    /// <summary>SOCKADDR_INET (IPv4: család, port hálózati sorrendben, cím).</summary>
    public static void WriteSockaddr(Span<byte> s, IPEndPoint ep)
    {
        s.Clear();
        var a = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s, 2);   // AF_INET
            BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)ep.Port);
            a.GetAddressBytes().CopyTo(s[4..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s, 23);  // AF_INET6
            BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)ep.Port);
            a.GetAddressBytes().CopyTo(s[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)a.ScopeId);
        }
    }

    /// <summary>A meghajtótól visszaolvasott konfiguráció társai (kézfogás, forgalom).</summary>
    public static List<WgPeerStat> ParseStats(byte[] buf, int len)
    {
        var l = new List<WgPeerStat>();
        if (len < IfaceSize) return l;
        var n = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(72));
        var o = IfaceSize;
        for (var i = 0; i < n && o + PeerSize <= len; i++)
        {
            var pub = Convert.ToBase64String(buf, o + 8, 32);
            var hs = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(o + 120));
            var tx = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(o + 104));
            var rx = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(o + 112));
            var fam = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o + 76));
            var ep = fam == 2 ? $"{new IPAddress(buf.AsSpan(o + 80, 4))}:{BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(o + 78))}" : "";
            var cnt = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o + 128));
            l.Add(new WgPeerStat(pub, hs > 0 ? DateTime.FromFileTimeUtc(hs) : null, rx, tx, ep));
            o += PeerSize + (int)cnt * AllowedSize;
        }
        return l;
    }
}

/// <summary>
/// A WireGuard-adapter (wireguard-nt). A DLL-t a Fleet.exe beágyazva hozza: induláskor a lenyomat ellenőrzése után a saját
/// mappájába írja (wireguard.dll + wireguard-LICENSE.txt), és onnan tölti be. Az adapter a folyamat élettartamáig él (a
/// szolgáltatás birtokolja a socketeket); a cím az IP Helper API-val (CreateUnicastIpAddressEntry) kerül rá.
/// </summary>
public sealed class WgTunnel : IDisposable
{
    public const string DllSha256 = "B1B85E072C45D81358BE29D94C599DC76652F912BE8C0F0A41E2D5D89A6461D3";
    private static IntPtr _lib;
    private static readonly object LoadLock = new();
    private IntPtr _adapter;
    public string Address { get; private set; } = "";

    /// <summary>A DLL kiírása és betöltése (egyszer). Hibánál a hiba szövege, különben null.</summary>
    public static string? Load()
    {
        lock (LoadLock)
        {
            if (_lib != IntPtr.Zero) return null;
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                var path = Path.Combine(dir, "wireguard.dll");
                byte[] want;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("wireguard.dll"))
                {
                    if (s is null) return "a wireguard.dll nincs a Fleet.exe-ben";
                    using var ms = new MemoryStream(); s.CopyTo(ms); want = ms.ToArray();
                }
                if (!Convert.ToHexString(SHA256.HashData(want)).Equals(DllSha256, StringComparison.OrdinalIgnoreCase))
                    return "a beágyazott wireguard.dll lenyomata nem egyezik";
                if (!File.Exists(path) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(DllSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(path, want);
                    using var lic = Assembly.GetExecutingAssembly().GetManifestResourceStream("wireguard-LICENSE.txt");
                    if (lic is not null) { using var f = File.Create(Path.Combine(dir, "wireguard-LICENSE.txt")); lic.CopyTo(f); }
                }
                _lib = NativeLibrary.Load(path);
                NativeLibrary.SetDllImportResolver(typeof(WgTunnel).Assembly, (name, _, _) => name == "wireguard.dll" ? _lib : IntPtr.Zero);
                return null;
            }
            catch (Exception ex) { return "WireGuard betöltése: " + ex.Message; }
        }
    }

    /// <summary>Az adapter létrehozása (vagy a megmaradt megnyitása), a cím (CIDR) beállítása. Hibánál kivétel.</summary>
    public static WgTunnel Open(string name, Guid guid, string address, int prefix)
    {
        if (Load() is { } err) throw new InvalidOperationException(err);
        var a = WireGuardOpenAdapter(name);
        if (a == IntPtr.Zero) a = WireGuardCreateAdapter(name, "Fleet", ref guid);
        if (a == IntPtr.Zero) throw new InvalidOperationException($"a WireGuard-adapter nem hozható létre (hiba {Marshal.GetLastWin32Error()})");
        var t = new WgTunnel { _adapter = a, Address = address };
        WireGuardGetAdapterLUID(a, out var luid);
        var row = new byte[80];
        InitializeUnicastIpAddressEntry(row);
        WgConfig.WriteSockaddr(row.AsSpan(0, 28), new IPEndPoint(IPAddress.Parse(address), 0));
        BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(32), luid);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(44), 1);   // PrefixOrigin: kézi
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(48), 1);   // SuffixOrigin: kézi
        row[60] = (byte)prefix;
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(64), 4);   // DadState: Preferred
        var r = CreateUnicastIpAddressEntry(row);
        if (r != 0 && r != 5010) { t.Dispose(); throw new InvalidOperationException($"az alagút címe nem állítható be (hiba {r})"); }
        return t;
    }

    public void Configure(byte[] config)
    {
        if (!WireGuardSetConfiguration(_adapter, config, (uint)config.Length))
            throw new InvalidOperationException($"a WireGuard-konfiguráció nem állítható be (hiba {Marshal.GetLastWin32Error()})");
        if (!WireGuardSetAdapterState(_adapter, 1))
            throw new InvalidOperationException($"az alagút nem kapcsolható be (hiba {Marshal.GetLastWin32Error()})");
    }

    public List<WgPeerStat> Stats()
    {
        uint n = 4096;
        for (var i = 0; i < 4; i++)
        {
            var buf = new byte[n];
            if (WireGuardGetConfiguration(_adapter, buf, ref n)) return WgConfig.ParseStats(buf, (int)n);
            if (Marshal.GetLastWin32Error() != 234) break;   // ERROR_MORE_DATA: nagyobb puffer kell
        }
        return new();
    }

    public void Dispose()
    {
        if (_adapter == IntPtr.Zero) return;
        try { WireGuardSetAdapterState(_adapter, 0); } catch { }
        try { WireGuardCloseAdapter(_adapter); } catch { }
        _adapter = IntPtr.Zero;
    }

    [DllImport("wireguard.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr WireGuardCreateAdapter(string name, string tunnelType, ref Guid guid);
    [DllImport("wireguard.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr WireGuardOpenAdapter(string name);
    [DllImport("wireguard.dll")] private static extern void WireGuardCloseAdapter(IntPtr adapter);
    [DllImport("wireguard.dll")] private static extern void WireGuardGetAdapterLUID(IntPtr adapter, out ulong luid);
    [DllImport("wireguard.dll", SetLastError = true)] private static extern bool WireGuardSetConfiguration(IntPtr adapter, byte[] config, uint bytes);
    [DllImport("wireguard.dll", SetLastError = true)] private static extern bool WireGuardGetConfiguration(IntPtr adapter, [In, Out] byte[] config, ref uint bytes);
    [DllImport("wireguard.dll", SetLastError = true)] private static extern bool WireGuardSetAdapterState(IntPtr adapter, int state);
    [DllImport("iphlpapi.dll")] private static extern void InitializeUnicastIpAddressEntry([In, Out] byte[] row);
    [DllImport("iphlpapi.dll")] private static extern int CreateUnicastIpAddressEntry(byte[] row);
}
