using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace DevToolbox;

[SupportedOSPlatform("windows")]
public static class NetworkInfo
{
    public static List<HwSection> Collect(WmiContext ctx) => Collect(ctx, false);

    public static List<HwSection> Collect(WmiContext ctx, bool includeVirtual) =>
        ctx.IsRemote ? CollectRemote(ctx) : CollectLocal(includeVirtual);

    // ---------------------------------------------------------------- szűrés

    /// <summary>
    /// A Windows minden NDIS szurodriver-peldanyt kulon halozati interfeszkent mutat
    /// (Npcap, QoS Packet Scheduler, WFP filter), es a WAN Miniportok is igy jelennek meg.
    /// Ezek nem valodi adapterek: egyetlen Ethernet karty igy 8-10 sorra szaporodik.
    /// </summary>
    private static readonly string[] PseudoMarkers =
    {
        "WAN Miniport", "Kernel Debug", "Teredo", "ISATAP", "6to4",
        "Packet Scheduler", "LightWeight Filter", "Npcap", "WFP Native MAC Layer",
        "Virtual Network Filter", "Native WiFi Filter", "NDIS Capture",
        "Packet Driver", "Microsoft IP-HTTPS"
    };

    public static bool IsRealAdapter(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return false;

        var desc = ni.Description ?? "";
        var name = ni.Name ?? "";

        foreach (var m in PseudoMarkers)
            if (desc.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(m, StringComparison.OrdinalIgnoreCase))
                return false;

        // A szurodriver-peldanyok leirasa "-0000" alaku sorszamra vegzodik.
        if (System.Text.RegularExpressions.Regex.IsMatch(desc, @"-\d{4}$")) return false;

        // Valodi adapternek van MAC cime.
        try { if (ni.GetPhysicalAddress().GetAddressBytes().Length == 0) return false; }
        catch { return false; }

        return true;
    }

    // ================================================================ helyi

    private static List<HwSection> CollectLocal(bool includeVirtual)
    {
        var list = new List<HwSection>();

        // ---- általános
        var g = new HwSection { Title = "Általános" };
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            g.Add("Gépnév", props.HostName);
            g.Add("Tartomány", string.IsNullOrWhiteSpace(props.DomainName) ? "(munkacsoport)" : props.DomainName);
            g.Add("Internetkapcsolat", NetworkInterface.GetIsNetworkAvailable() ? "elérhető" : "nincs");
        }
        catch { }
        list.Add(g);

        // ---- adapterek: elobb az aktivak, utana a tobbi
        var all = SafeInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        var adapters = all
            .Where(n => includeVirtual || IsRealAdapter(n))
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var activeCount = adapters.Count(n => n.OperationalStatus == OperationalStatus.Up);
        var hidden = all.Count - adapters.Count;

        g.Add("Adapterek", $"{activeCount} aktív, {adapters.Count - activeCount} nem aktív");
        if (hidden > 0)
            g.Add("Elrejtve", $"{hidden} virtuális / szűrődriver-példány " +
                              "(WAN Miniport, QoS, Npcap és társaik)");

        foreach (var ni in adapters)
        {
            var up = ni.OperationalStatus == OperationalStatus.Up;
            var prefix = up ? "● " : "○ ";

            // A nem aktiv adapterek alapbol csukva jelennek meg, hogy ne nyomjak el a lenyeget.
            var s = new HwSection
            {
                Title = $"{prefix}{ni.Name}  –  {StatusText(ni.OperationalStatus)}",
                Collapsed = !up
            };
            s.Add("Leírás", ni.Description);
            s.Add("Típus", TypeText(ni.NetworkInterfaceType));
            s.Add("MAC cím", FormatMac(ni.GetPhysicalAddress().ToString()));

            try
            {
                if (ni.Speed > 0)
                    s.Add("Kapcsolat sebessége", $"{ni.Speed / 1_000_000} Mbit/s");
            }
            catch { }

            try
            {
                var p = ni.GetIPProperties();

                foreach (var ua in p.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        s.Add("IPv4 cím", $"{ua.Address}  /  {ua.IPv4Mask}");
                        try
                        {
                            if (ua.PrefixOrigin == PrefixOrigin.Dhcp)
                                s.Add("Címkiosztás", "DHCP");
                            else if (ua.PrefixOrigin == PrefixOrigin.Manual)
                                s.Add("Címkiosztás", "kézi (fix IP)");
                        }
                        catch { }
                    }
                    else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                             !ua.Address.IsIPv6LinkLocal)
                    {
                        s.Add("IPv6 cím", ua.Address.ToString());
                    }
                }

                foreach (var gw in p.GatewayAddresses)
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        s.Add("Átjáró", gw.Address.ToString());

                var dns = p.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).ToList();
                if (dns.Count > 0) s.Add("DNS kiszolgáló", string.Join(", ", dns));

                foreach (var dhcp in p.DhcpServerAddresses)
                    if (dhcp.AddressFamily == AddressFamily.InterNetwork)
                        s.Add("DHCP kiszolgáló", dhcp.ToString());

                if (!string.IsNullOrWhiteSpace(p.DnsSuffix)) s.Add("DNS utótag", p.DnsSuffix);
            }
            catch { }

            try
            {
                var st = ni.GetIPv4Statistics();
                s.Add("Forgalom", $"le: {Fmt.Bytes(st.BytesReceived)}  ·  fel: {Fmt.Bytes(st.BytesSent)}");
                if (st.IncomingPacketsWithErrors > 0 || st.OutgoingPacketsWithErrors > 0)
                    s.Add("Hibás csomagok", $"be: {st.IncomingPacketsWithErrors}, ki: {st.OutgoingPacketsWithErrors}");
            }
            catch { }

            list.Add(s);
        }

        return list.Where(s => s.Rows.Count > 0).ToList();
    }

    private static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return Array.Empty<NetworkInterface>(); }
    }

    // ================================================================ távoli (WMI)

    private static List<HwSection> CollectRemote(WmiContext ctx)
    {
        var list = new List<HwSection>();

        var g = new HwSection { Title = "Általános" };
        foreach (var o in ctx.Query("SELECT * FROM Win32_ComputerSystem"))
        {
            g.Add("Gépnév", o["Name"]);
            g.Add("Tartomány", o["Domain"]);
            g.Add("Tartomány tagja", o["PartOfDomain"] is bool b && b ? "igen" : "nem");
            break;
        }
        list.Add(g);

        foreach (var o in ctx.Query(
                     "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=True"))
        {
            var desc = o["Description"]?.ToString() ?? "Adapter";
            var s = new HwSection { Title = desc };

            s.Add("MAC cím", FormatMac(o["MACAddress"]?.ToString() ?? ""));

            if (o["IPAddress"] is string[] ips && o["IPSubnet"] is string[] masks)
            {
                for (int i = 0; i < ips.Length; i++)
                {
                    var mask = i < masks.Length ? masks[i] : "";
                    s.Add(ips[i].Contains(':') ? "IPv6 cím" : "IPv4 cím",
                        mask.Length > 0 ? $"{ips[i]}  /  {mask}" : ips[i]);
                }
            }

            if (o["DefaultIPGateway"] is string[] gws && gws.Length > 0)
                s.Add("Átjáró", string.Join(", ", gws));

            if (o["DNSServerSearchOrder"] is string[] dns && dns.Length > 0)
                s.Add("DNS kiszolgáló", string.Join(", ", dns));

            s.Add("Címkiosztás", o["DHCPEnabled"] is bool dh && dh ? "DHCP" : "kézi (fix IP)");
            s.Add("DHCP kiszolgáló", o["DHCPServer"]);
            s.Add("DNS utótag", o["DNSDomain"]);

            list.Add(s);
        }

        return list.Where(s => s.Rows.Count > 0).ToList();
    }

    // ================================================================ kapcsolatok

    public sealed record Connection(string Protocol, string Local, string Remote, string State);

    /// <summary>Aktív TCP kapcsolatok és figyelt portok (csak helyi gépen).</summary>
    public static List<Connection> GetConnections(bool listenersOnly)
    {
        var list = new List<Connection>();
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();

            foreach (var l in props.GetActiveTcpListeners())
                list.Add(new Connection("TCP", Ep(l), "—", "Figyel"));

            foreach (var l in props.GetActiveUdpListeners())
                list.Add(new Connection("UDP", Ep(l), "—", "Figyel"));

            if (!listenersOnly)
            {
                foreach (var c in props.GetActiveTcpConnections())
                    list.Add(new Connection("TCP", Ep(c.LocalEndPoint), Ep(c.RemoteEndPoint),
                        StateText(c.State)));
            }
        }
        catch { }

        return list;
    }

    private static string Ep(IPEndPoint ep) =>
        ep.Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{ep.Address}]:{ep.Port}"
            : $"{ep.Address}:{ep.Port}";

    private static string StateText(TcpState s) => s switch
    {
        TcpState.Established => "Kapcsolódva",
        TcpState.Listen => "Figyel",
        TcpState.TimeWait => "Várakozik",
        TcpState.CloseWait => "Lezárás alatt",
        TcpState.SynSent => "Kapcsolódik",
        TcpState.Closed => "Lezárva",
        _ => s.ToString()
    };

    private static string StatusText(OperationalStatus s) => s switch
    {
        OperationalStatus.Up => "aktív",
        OperationalStatus.Down => "nem aktív",
        OperationalStatus.Dormant => "alvó",
        _ => s.ToString()
    };

    private static string TypeText(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "vezetékes",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ppp => "PPP / mobil",
        NetworkInterfaceType.Tunnel => "alagút (VPN)",
        _ => t.ToString()
    };

    private static string FormatMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "";
        mac = mac.Replace(":", "").Replace("-", "");
        if (mac.Length != 12) return mac;
        return string.Join("-", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
    }
}
