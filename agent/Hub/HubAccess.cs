using System.Net;
using System.Net.Sockets;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// Ki eri el az admin feluletet. A telephely-API (/hub/...) mindig nyitva van (alairas vedi),
/// az admin (/web...) viszont csak belso halozatrol.
///
/// Miert itt es nem az NPM-ben: az NPM Advanced fulere irt "location /" blokk utkozik az
/// NPM altal generalttal ("duplicate location"), es a proxy host nem toltodik be. Igy az
/// NPM-ben eleg egy sima proxy host, a szures itt tortenik.
///
/// A valodi kliens-IP: ha a kozvetlen kapcsolat belso cimrol jon (az NPM), akkor a proxy
/// altal beallitott X-Real-IP-t (illetve az X-Forwarded-For UTOLSO elemet) hisszuk el.
/// Az NPM ezeket felulirja/hozzafuzi, igy kivulrol nem hamisithatok. Kulso kozvetlen
/// kapcsolat amugy sem jut el ide: a tuzfal csak belso tartomanyokat enged be.
/// </summary>
public static class HubAccess
{
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // CGNAT (Tailscale, ZeroTier)
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        return false;
    }

    /// <summary>A kerest indito kliens valodi cime (proxy mogott is).</summary>
    public static IPAddress? ClientIp(IPAddress? peer, string? xRealIp, string? xForwardedFor)
    {
        if (peer is null) return null;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();

        // Csak belso proxytol fogadjuk el a fejleceket.
        if (!IsPrivate(peer)) return peer;

        if (!string.IsNullOrWhiteSpace(xRealIp) && IPAddress.TryParse(xRealIp.Trim(), out var real))
            return real.IsIPv4MappedToIPv6 ? real.MapToIPv4() : real;

        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            var last = xForwardedFor.Split(',').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0);
            if (last is not null && IPAddress.TryParse(last, out var fwd))
                return fwd.IsIPv4MappedToIPv6 ? fwd.MapToIPv4() : fwd;
        }
        return peer;
    }

    public static bool AdminAllowed(IPAddress? peer, string? xRealIp, string? xForwardedFor) =>
        ClientIp(peer, xRealIp, xForwardedFor) is { } ip && IsPrivate(ip);
}

/// <summary>Tuzfal es URL-foglalas. Minden inditaskor fut, az aktualis porthoz igazitva.</summary>
public static class HubFirewall
{
    public const string RuleName = "DevToolbox Kozpont";

    /// <summary>Belso tartomanyok - a tuzfal csak innen enged be (az NPM is itt van).</summary>
    public const string PrivateRanges =
        "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,100.64.0.0/10,169.254.0.0/16,fc00::/7,fe80::/10";

    /// <summary>
    /// A korabbi szabaly csak Tartomanyi/Magan profilon nyitott - Nyilvanos profilu halozaton
    /// (Windows szervereken gyakori) a port zarva maradt. Most minden profilon nyit, de csak
    /// belso IP-tartomanyokbol: igy az NPM eleri, az internet kozvetlenul nem.
    /// </summary>
    public static void Ensure(int port)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Hiba eseten sem allunk meg: a szolgaltatas (SYSTEM) enelkul is figyelhet, a diag megmutatja.
        try { NativeFirewall.Replace(RuleName, new[] { port }, PrivateRanges, "DevToolbox Fleet Központ - admin és telephely-API"); }
        catch (Exception ex) { Console.WriteLine("Tűzfalszabály: " + ex.Message); }
        // URL-foglalas (SYSTEM-nek nem kotelezo, interaktiv futtatasnal segit). SDDL: nyelvfuggetlen.
        try { NativeUrlAcl.Ensure($"http://+:{port}/", "D:(A;;GX;;;SY)(A;;GX;;;BA)"); }
        catch (Exception ex) { Console.WriteLine(ex.Message); }
    }
}
