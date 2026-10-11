using System.Net;
using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// A Központ WireGuard-oldala (1.5.9, a felhasználó kérése és döntései): egy wireguard-nt adapter (alapból 10.77.0.1/24,
/// UDP 51820), társai a telephelyek (kulcs + kiosztott /32 cím). A telephely a betárcsázás aláírt összesítőjében közli a
/// kulcsát; az első alagúton jött betárcsázás után CSAK azon fogadjuk (szigorú mód). Az alagútból az admin felület nem
/// érhető el (csak a /hub/ útvonalak). Minden beállítás a Beállítások → WireGuard részben szerkeszthető.
/// </summary>
public static class HubWg
{
    public const string FirewallRule = "DevToolbox Fleet Hub - WireGuard";
    private static readonly Guid AdapterGuid = new("6b1f3e2a-6c55-4f0a-9d7e-1f6a0c4e9a01");
    private static WgTunnel? _t;
    private static string _applied = "";
    private static readonly object L = new();
    public static string LastError { get; private set; } = "";
    public static bool Running => _t is not null;

    public static WgNet? Net(ServerConfig c) => c.WgEnabled ? WgNet.Parse(c.WgSubnet) : null;
    public static string? HubAddress(ServerConfig c) => Net(c)?.Host(1);

    public static string Endpoint(ServerConfig c) =>
        c.WgEndpoint.Trim().Length > 0 ? c.WgEndpoint.Trim() : (c.HubDomain.Length > 0 ? c.HubDomain : Environment.MachineName) + ":" + c.WgListenPort;

    /// <summary>A Központ kulcspárja (DPAPI-val védve); első használatkor készül.</summary>
    public static (byte[] Private, string Public) Keys(ServerConfig c)
    {
        var priv = c.WgPrivateKeyProtected.Length > 0 ? NativeDpapi.Unprotect(c.WgPrivateKeyProtected) : null;
        if (priv is null || !WgKeys.IsKey(priv))
        {
            var k = WgKeys.NewPrivate();
            priv = Convert.ToBase64String(k);
            c.WgPrivateKeyProtected = NativeDpapi.Protect(priv);
            c.WgPublicKey = WgKeys.PublicOf(priv);
            c.Save(HubPaths.DataDir);
        }
        return (Convert.FromBase64String(priv), c.WgPublicKey.Length > 0 ? c.WgPublicKey : WgKeys.PublicOf(priv));
    }

    /// <summary>A Központ adapterének (újra)beállítása a mostani társakkal; a tűzfal is (UDP-port bárhonnan – a WireGuard maga szűr).</summary>
    public static void Apply(ServerConfig c, HubStore store)
    {
        lock (L)
        {
            try
            {
                if (Net(c) is not { } net) { Stop(); return; }
                var (priv, _) = Keys(c);
                var addr = net.Host(1)!;
                var peers = store.WgPeers().Select(p => new WgPeer(p.PublicKey, p.Address, 32)).ToList();
                var key = $"{addr}/{net.Prefix}|{c.WgListenPort}|{string.Join(";", peers)}|{c.WgPrivateKeyProtected.GetHashCode()}";
                if (key == _applied && _t is not null) return;
                if (_t is null || _t.Address != addr) { _t?.Dispose(); _t = null; _t = WgTunnel.Open("Fleet HUB WireGuard", AdapterGuid, addr, net.Prefix); }
                _t.Configure(WgConfig.Build(priv, c.WgListenPort, peers));
                try { NativeFirewall.Replace(FirewallRule, new[] { c.WgListenPort }, "*", "Fleet HUB – WireGuard-alagút a telephelyeknek (UDP)", udp: true); }
                catch (Exception ex) { HubLog.Write("WireGuard tűzfalszabály: " + ex.Message); }
                _applied = key; LastError = "";
                HubLog.Write($"WireGuard: {addr}/{net.Prefix}, UDP {c.WgListenPort}, {peers.Count} telephely");
            }
            catch (Exception ex) { LastError = ex.Message; HubLog.Write("WireGuard HIBA: " + ex.Message); }
        }
    }

    public static void Stop()
    {
        lock (L) { _t?.Dispose(); _t = null; _applied = ""; }
        try { NativeFirewall.Delete(FirewallRule); } catch { }
    }

    public static List<WgPeerStat> Stats() { lock (L) { try { return _t?.Stats() ?? new(); } catch { return new(); } } }

    /// <summary>
    /// Az alagútból jött-e a kérés: a küldő az alagút tartományában, és a Központ alagút-címére érkezett (a Windows erős
    /// gazdamodellje miatt ez csak a WireGuard-adapteren át lehet, a WireGuard pedig a társ kulcsához köti a forráscímet).
    /// Visszaadja a küldő alagút-címét, vagy null-t.
    /// </summary>
    public static string? TunnelIp(HttpListenerContext ctx, ServerConfig c)
    {
        if (Net(c) is not { } net || HubAddress(c) is not { } hub) return null;
        var remote = ctx.Request.RemoteEndPoint?.Address;
        var local = ctx.Request.LocalEndPoint?.Address;
        if (remote is null || local is null) return null;
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return net.Contains(remote) && local.ToString() == hub ? remote.ToString() : null;
    }

    /// <summary>Az alagút tartományából jött-e (akár más címre): ilyenkor az admin felület nem szolgálható ki.</summary>
    public static bool FromTunnelRange(HttpListenerContext ctx, ServerConfig c) =>
        Net(c) is { } net && ctx.Request.RemoteEndPoint?.Address is { } a && net.Contains(a);

    public static HubWgOffer? OfferFor(ServerConfig c, HubSite s)
    {
        if (Net(c) is not { } net || !WgKeys.IsKey(s.WgPublicKey) || s.WgAddress.Length == 0) return null;
        var (_, pub) = Keys(c);
        return new HubWgOffer
        {
            HubPublicKey = pub, Endpoint = Endpoint(c), HubAddress = net.Host(1)!, SiteAddress = s.WgAddress, Prefix = net.Prefix,
            HubUrl = $"http://{net.Host(1)}:{c.WebPort}", Keepalive = Math.Clamp(c.WgKeepalive, 0, 600), Required = Required(c, s)
        };
    }

    public static bool Required(ServerConfig c, HubSite s) => Net(c) is not null && (s.WgActive || c.WgStrictAll);

    /// <summary>
    /// POST /hub/wg-key: a telephely kulcsának frissítése alagúton kívül (a telephely kulcsával aláírva, friss nonce-szal); a
    /// válasz a Központ kulcsával aláírt ajánlat. Csak nyilvános kulcs és cím megy át, semmi titkos.
    /// </summary>
    public static SignedEnvelope KeyRequest(string body, HubStore store, ServerConfig c)
    {
        var env = ProtocolJson.Deserialize<SignedEnvelope>(body);
        var req = env?.Open<HubWgKeyRequest>();
        SignedEnvelope Reply(bool ok, string msg, HubWgOffer? o = null) =>
            store.Sign(new HubWgKeyReply { Ok = ok, Message = msg, RequestNonce = req?.Nonce ?? "", Offer = o });
        if (env is null || req is null) return Reply(false, "Hibás kérés.");
        var site = store.VerifySite(env, req.Timestamp, req.Nonce);
        if (site is null) return Reply(false, "Érvénytelen aláírás, ismeretlen telephely vagy ismételt kérés.");
        if (Net(c) is null) return Reply(false, "A Központban nincs bekapcsolva a WireGuard.");
        if (!WgKeys.IsKey(req.WgPublicKey)) return Reply(false, "Hibás WireGuard-kulcs.");
        if (store.SetWgKey(site, req.WgPublicKey)) HubLog.Write($"WireGuard: {site.Name} új kulcsot küldött ({site.WgAddress})");
        else if (site.WgPublicKey != req.WgPublicKey) return Reply(false, "Ez a kulcs már egy másik telephelyé.");
        return Reply(true, "", OfferFor(c, site));
    }
}
