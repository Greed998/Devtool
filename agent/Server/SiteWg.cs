using System.Net;
using System.Net.Sockets;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// A telephely WireGuard-oldala (1.5.9): egy wireguard-nt adapter, egyetlen társa a Központ (a Központ aláírt ajánlata
/// szerint: HubWgOffer). A saját kulcspár DPAPI-val védve a beállításban; a nyilvános kulcs az aláírt összesítőben megy fel.
/// A végpont (a Központ címe:port) helyben felülírható – ha az alagút nem áll fel, a webfelületen javítható (a felhasználó
/// döntése: szigorú mód, helyi javítással).
/// </summary>
public static class SiteWg
{
    private static readonly Guid AdapterGuid = new("6b1f3e2a-6c55-4f0a-9d7e-1f6a0c4e9a02");
    private static WgTunnel? _t;
    private static string _applied = "";
    private static readonly object L = new();
    private static DateTime _resolvedAt = DateTime.MinValue;
    private static IPEndPoint? _ep;
    public static string LastError { get; set; } = "";
    public static bool Running => _t is not null;

    public static (byte[] Private, string Public) Keys(ServerConfig c, string dir)
    {
        var priv = c.WgPrivateKeyProtected.Length > 0 ? NativeDpapi.Unprotect(c.WgPrivateKeyProtected) : null;
        if (priv is null || !WgKeys.IsKey(priv))
        {
            priv = Convert.ToBase64String(WgKeys.NewPrivate());
            c.WgPrivateKeyProtected = NativeDpapi.Protect(priv);
            c.WgPublicKey = WgKeys.PublicOf(priv);
            c.Save(dir);
        }
        if (c.WgPublicKey.Length == 0) c.WgPublicKey = WgKeys.PublicOf(priv);
        return (Convert.FromBase64String(priv), c.WgPublicKey);
    }

    /// <summary>Új saját kulcs (helyi javítás / kulcscsere): utána a /hub/wg-key-jel kell közölni a Központtal.</summary>
    public static void NewKey(ServerConfig c, string dir)
    {
        c.WgPrivateKeyProtected = ""; c.WgPublicKey = "";
        Keys(c, dir);
        lock (L) _applied = "";
    }

    public static string Endpoint(ServerConfig c) => c.WgEndpoint.Trim().Length > 0 ? c.WgEndpoint.Trim() : c.WgOffer?.Endpoint ?? "";

    /// <summary>„gépnév:port” → cím (IPv4 előnyben); 5 percig megjegyezve, utána újra (a Központ címe változhat).</summary>
    private static IPEndPoint? Resolve(string hostPort, bool force)
    {
        if (!force && _ep is not null && DateTime.UtcNow - _resolvedAt < TimeSpan.FromMinutes(5)) return _ep;
        var i = hostPort.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(hostPort[(i + 1)..], out var port) || port is < 1 or > 65535) throw new InvalidOperationException("hibás végpont: " + hostPort);
        var host = hostPort[..i].Trim('[', ']');
        var addrs = IPAddress.TryParse(host, out var ip) ? new[] { ip } : Dns.GetHostAddresses(host);
        var a = addrs.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault()
                ?? throw new InvalidOperationException("a végpont nem oldható fel: " + host);
        _ep = new IPEndPoint(a, port); _resolvedAt = DateTime.UtcNow;
        return _ep;
    }

    /// <summary>Az adapter beállítása a Központ ajánlata szerint (csak változáskor). Hibánál a hiba szövege, különben null.</summary>
    public static string? Apply(ServerConfig c, string dir, bool forceResolve = false)
    {
        lock (L)
        {
            try
            {
                if (c.WgOffer is not { } o || !WgKeys.IsKey(o.HubPublicKey) || !IPAddress.TryParse(o.SiteAddress, out _) || !IPAddress.TryParse(o.HubAddress, out _))
                { Stop(); return null; }
                var (priv, _) = Keys(c, dir);
                var ep = Resolve(Endpoint(c), forceResolve);
                var peers = new List<WgPeer> { new(o.HubPublicKey, o.HubAddress, 32, ep, Math.Clamp(o.Keepalive, 0, 600)) };
                var key = $"{o.SiteAddress}/{o.Prefix}|{string.Join(";", peers)}|{c.WgPrivateKeyProtected.GetHashCode()}";
                if (key == _applied && _t is not null) return null;
                if (_t is null || _t.Address != o.SiteAddress) { _t?.Dispose(); _t = null; _t = WgTunnel.Open("Fleet WireGuard", AdapterGuid, o.SiteAddress, o.Prefix); }
                _t.Configure(WgConfig.Build(priv, 0, peers));
                _applied = key; LastError = "";
                SrvLog.Info($"WireGuard: {o.SiteAddress}/{o.Prefix} → Központ {o.HubAddress} ({ep})");
                return null;
            }
            catch (Exception ex) { LastError = ex.Message; return ex.Message; }
        }
    }

    public static void Stop() { lock (L) { _t?.Dispose(); _t = null; _applied = ""; } }

    public static WgPeerStat? HubStat() { lock (L) { try { return _t?.Stats().FirstOrDefault(); } catch { return null; } } }

    /// <summary>Volt-e kézfogás az utolsó 3 percben (a WireGuard 2 percenként újít, ha van forgalom / keepalive).</summary>
    public static bool HandshakeRecent => HubStat()?.LastHandshakeUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromMinutes(3);
}
