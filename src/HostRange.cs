using System.Net;
using System.Net.NetworkInformation;

namespace DevToolbox;

/// <summary>
/// Gepnev- es IP-lista feldolgozasa. Tamogatott formak soronkent:
///   IRODA-PC1                     gepnev
///   192.168.1.50                  egy cim
///   192.168.1.10-40               rovid tartomany (utolso oktett)
///   192.168.1.10-192.168.1.40     teljes tartomany
///   192.168.1.0/24                halozat maszkkal
/// </summary>
public static class HostRange
{
    /// <summary>Biztonsagi felso hatar, hogy egy elgepelt /8 ne indítson vegtelen szkennelest.</summary>
    public const int MaxHosts = 1024;

    public sealed record ExpandResult(List<string> Hosts, List<string> Warnings);

    public static ExpandResult Expand(string input)
    {
        var hosts = new List<string>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lines = input.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0);

        foreach (var line in lines)
        {
            List<string> expanded;
            try { expanded = ExpandLine(line, warnings); }
            catch (Exception ex)
            {
                warnings.Add($"„{line}” nem értelmezhető: {ex.Message}");
                continue;
            }

            foreach (var h in expanded)
            {
                if (hosts.Count >= MaxHosts)
                {
                    warnings.Add($"A lista {MaxHosts} gépnél elvágva – szűkítsd a tartományt.");
                    return new ExpandResult(hosts, warnings);
                }
                if (seen.Add(h)) hosts.Add(h);
            }
        }

        return new ExpandResult(hosts, warnings);
    }

    private static List<string> ExpandLine(string line, List<string> warnings)
    {
        // --- CIDR:  192.168.1.0/24
        var slash = line.IndexOf('/');
        if (slash > 0)
        {
            var baseIp = line[..slash].Trim();
            var prefixText = line[(slash + 1)..].Trim();

            if (!TryParseIpv4(baseIp, out var ip))
                throw new FormatException("csak IPv4 hálózat adható meg");
            if (!int.TryParse(prefixText, out var prefix) || prefix is < 8 or > 32)
                throw new FormatException("a maszk 8 és 32 között lehet");

            var addr = ToUInt(ip);
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var network = addr & mask;
            var broadcast = network | ~mask;

            var first = prefix >= 31 ? network : network + 1;          // hálózati cím kihagyva
            var last = prefix >= 31 ? broadcast : broadcast - 1;       // broadcast kihagyva

            var count = last - first + 1;
            if (count > MaxHosts)
                warnings.Add($"A(z) {line} tartomány {count} címet tartalmaz – az első {MaxHosts} kerül a listába.");

            return Range(first, last);
        }

        // --- tartomany:  192.168.1.10-40  vagy  192.168.1.10-192.168.1.40
        var dash = line.IndexOf('-');
        if (dash > 0)
        {
            var leftText = line[..dash].Trim();
            var rightText = line[(dash + 1)..].Trim();

            if (!TryParseIpv4(leftText, out var left))
            {
                // Nem IP-vel kezdodik: valoszinuleg kotojeles gepnev (pl. IRODA-PC1).
                return new List<string> { line };
            }

            uint start = ToUInt(left);
            uint end;

            // FONTOS: eloszor a rovid format nezzuk. Az IPAddress.TryParse("14") ugyanis
            // SIKERES, es 0.0.0.14-et ad vissza - ettol elveszne a halozati resz.
            if (!rightText.Contains('.') &&
                int.TryParse(rightText, out var lastOctet) && lastOctet is >= 0 and <= 255)
            {
                end = (start & 0xFFFFFF00u) | (uint)lastOctet;
            }
            else if (TryParseIpv4(rightText, out var right))
            {
                end = ToUInt(right);
            }
            else
            {
                throw new FormatException("a tartomány vége nem értelmezhető");
            }

            if (end < start) (start, end) = (end, start);

            var count = end - start + 1;
            if (count > MaxHosts)
                warnings.Add($"A(z) {line} tartomány {count} címet tartalmaz – az első {MaxHosts} kerül a listába.");

            return Range(start, end);
        }

        // --- egyszerű gépnév vagy IP
        return new List<string> { line };
    }

    private static List<string> Range(uint first, uint last)
    {
        var list = new List<string>();
        for (uint a = first; a <= last && list.Count < MaxHosts; a++)
        {
            list.Add(ToIp(a));
            if (a == uint.MaxValue) break;
        }
        return list;
    }

    /// <summary>
    /// Szigoru IPv4 ellenorzes. A keretrendszer TryParse-a elfogadja a roviditeseket is
    /// ("14" -> 0.0.0.14, "10.1" -> 10.0.0.1), ami tartomanyoknal felreertelmezeshez vezet.
    /// </summary>
    private static bool TryParseIpv4(string s, out IPAddress ip)
    {
        ip = IPAddress.None;
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        if (parts.Any(p => p.Length == 0 || p.Length > 3 || !p.All(char.IsDigit))) return false;

        return IPAddress.TryParse(s, out ip!) &&
               ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static string ToIp(uint v) =>
        $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    /// <summary>
    /// Gyors elerhetoseg-szures ping-gel, parhuzamosan. Nagy tartomanynal ez sporolja
    /// meg a legtobb idot: a WMI idotullepese gepenkent 20-30 masodperc is lehet.
    /// </summary>
    public static async Task<List<string>> FilterAliveAsync(
        IEnumerable<string> hosts, int timeoutMs, IProgress<string>? progress, CancellationToken token)
    {
        var all = hosts.ToList();
        var alive = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;

        using var throttle = new SemaphoreSlim(64);

        var tasks = all.Select(async host =>
        {
            await throttle.WaitAsync(token);
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, timeoutMs);
                if (reply.Status == IPStatus.Success) alive.Add(host);
            }
            catch { /* nem elerheto */ }
            finally
            {
                throttle.Release();
                var n = Interlocked.Increment(ref done);
                if (n % 16 == 0 || n == all.Count)
                    progress?.Report($"Elérhetőség vizsgálata… {n}/{all.Count}");
            }
        });

        await Task.WhenAll(tasks);

        // Az eredeti sorrendet tartjuk meg, hogy a jelentes kiszamithato legyen.
        return all.Where(h => alive.Contains(h)).ToList();
    }
}
