using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace DevToolbox;

/// <summary>
/// Egy figyelt gep allapotanak felmerese. Minden ellenorzes kulon van becsomagolva,
/// igy egy hiba nem viszi el az egesz vizsgalatot, es mindegyiknek van idokorlatja.
///
/// A modul CSAK OLVAS: egyetlen muvelet sem modosit a celgepen.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HealthChecker
{
    private static readonly (int Port, string Name)[] DcPorts =
    {
        (389, "LDAP"), (88, "Kerberos"), (445, "SMB"), (53, "DNS"), (135, "RPC")
    };

    private static readonly (int Port, string Name)[] ServerPorts =
    {
        (445, "SMB"), (135, "RPC")
    };

    private static readonly (string Service, string Label)[] DcServices =
    {
        ("NTDS", "Címtárszolgáltatás"),
        ("Netlogon", "Netlogon"),
        ("DNS", "DNS kiszolgáló"),
        ("kdc", "Kerberos (KDC)"),
        ("W32Time", "Időszolgáltatás"),
        ("DFSR", "SYSVOL replikáció")
    };

    public static async Task<HostHealth> CheckAsync(
        MonitoredHost host, MonitorConfig cfg, CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var h = new HostHealth { HostId = host.Id };

        // --- 0. Belso halozat ellenorzese minden korben. A DNS kozben megvaltozhat.
        var priv = PrivateNetwork.Validate(host.Host);
        if (!priv.Allowed)
        {
            h.Checks.Add(new CheckResult
            {
                Name = "Hálózati korlátozás",
                State = HealthState.Hiba,
                Detail = "A cím nem belső hálózati cím – a lekérdezés nem indult el.",
                Advice = priv.Message
            });
            h.Overall = HealthState.Hiba;
            h.Duration = sw.Elapsed;
            return h;
        }

        // --- 1. Elerhetoseg
        var reach = await PingAsync(host.Host, cfg.PingTimeoutMs, token);
        h.Checks.Add(reach.Result);

        if (reach.Result.State == HealthState.Elerhetetlen)
        {
            // Nem minden tuzfal engedi az ICMP-t: probaljuk meg TCP-vel is.
            var tcp = await PortAsync(host.Host, 445, 1500, token);
            if (tcp)
            {
                h.Checks[^1] = new CheckResult
                {
                    Name = "Elérhetőség",
                    State = HealthState.Figyelmeztetes,
                    Detail = "Pingre nem válaszol, de a 445-ös port nyitva van.",
                    Advice = "A tűzfal valószínűleg tiltja az ICMP-t. Ez nem hiba, " +
                             "csak a válaszidőt nem tudjuk mérni."
                };
            }
            else
            {
                h.Overall = HealthState.Elerhetetlen;
                h.Duration = sw.Elapsed;
                return h;   // tovabbi ellenorzesnek nincs ertelme
            }
        }

        // --- 2. Portok
        var ports = host.IsDomainController ? DcPorts : ServerPorts;
        h.Checks.Add(await PortsAsync(host.Host, ports, token));

        // --- 3. WMI + rendszeradatok
        var ctx = new WmiContext { Host = host.Host };   // integralt hitelesites
        var sys = await Task.Run(() => SystemInfo(ctx, h, cfg), token);
        h.Checks.Add(sys);

        var wmiOk = sys.State != HealthState.Hiba;

        if (wmiOk)
        {
            // --- 4. Idoelteres: Kerberos +/- 5 percnel elszall
            h.Checks.Add(await Task.Run(() => TimeSkew(ctx, cfg), token));

            // --- 5. Lemezek
            h.Checks.Add(await Task.Run(() => DiskCapacity(ctx, cfg), token));

            var skipPhysical = h.IsVirtual && host.SkipPhysicalDiskIfVirtual;
            h.Checks.Add(skipPhysical
                ? new CheckResult
                {
                    Name = "Fizikai lemezek",
                    State = HealthState.Rendben,
                    Detail = "Virtuális gép – kihagyva.",
                    Advice = "Virtuális lemeznél a SMART adat a hypervisor mögül nem látszik, " +
                             "ezért az ellenőrzésnek itt nincs értelme. A tárolót a " +
                             "hypervisor gazdagépén érdemes figyelni."
                }
                : await Task.Run(() => PhysicalDisks(ctx), token));

            // --- 6. Tartomanyvezerlo-specifikus
            if (host.IsDomainController)
            {
                h.Checks.Add(await Task.Run(() => Services(ctx), token));
                h.Checks.Add(await Task.Run(() => SysvolShares(ctx), token));
            }

            // --- 7. Friss hibak a naploban
            h.Checks.Add(await Task.Run(() => RecentErrors(ctx, token), token));
        }

        h.Overall = h.Checks.Count == 0
            ? HealthState.Ismeretlen
            : h.Checks.Max(c => c.State);

        h.Duration = sw.Elapsed;
        return h;
    }

    // ================================================================ egyes ellenőrzések

    private static async Task<(CheckResult Result, long Ms)> PingAsync(
        string host, int timeout, CancellationToken token)
    {
        var times = new List<long>();
        int lost = 0;

        for (int i = 0; i < 3; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync(host, timeout);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
                else lost++;
            }
            catch { lost++; }
        }

        if (times.Count == 0)
            return (new CheckResult
            {
                Name = "Elérhetőség",
                State = HealthState.Elerhetetlen,
                Detail = "Nem válaszol.",
                Advice = "A gép kikapcsolt, hálózati hiba van, vagy a tűzfal tiltja az ICMP-t."
            }, 0);

        var avg = (long)times.Average();
        var state = lost > 0 ? HealthState.Figyelmeztetes
                  : avg > 200 ? HealthState.Figyelmeztetes
                  : HealthState.Rendben;

        return (new CheckResult
        {
            Name = "Elérhetőség",
            State = state,
            Detail = $"{avg} ms átlag" + (lost > 0 ? $", {lost}/3 csomagvesztés" : ""),
            Advice = lost > 0
                ? "A csomagvesztés hálózati problémára utal: kábel, switch-port vagy Wi-Fi."
                : avg > 200 ? "A válaszidő magas – terhelt hálózat vagy távoli telephely." : ""
        }, avg);
    }

    private static async Task<bool> PortAsync(string host, int port, int timeout, CancellationToken token)
    {
        try
        {
            using var c = new TcpClient();
            var task = c.ConnectAsync(host, port, token).AsTask();
            var done = await Task.WhenAny(task, Task.Delay(timeout, token));
            return done == task && c.Connected;
        }
        catch { return false; }
    }

    private static async Task<CheckResult> PortsAsync(
        string host, (int Port, string Name)[] ports, CancellationToken token)
    {
        var closed = new List<string>();

        foreach (var (port, name) in ports)
        {
            token.ThrowIfCancellationRequested();
            if (!await PortAsync(host, port, 1500, token)) closed.Add($"{name} ({port})");
        }

        if (closed.Count == 0)
            return new CheckResult
            {
                Name = "Szolgáltatási portok",
                State = HealthState.Rendben,
                Detail = $"{ports.Length}/{ports.Length} nyitva"
            };

        return new CheckResult
        {
            Name = "Szolgáltatási portok",
            State = closed.Count == ports.Length ? HealthState.Hiba : HealthState.Figyelmeztetes,
            Detail = "Nem érhető el: " + string.Join(", ", closed),
            Advice = "Ellenőrizd a tűzfalat és azt, hogy a hozzá tartozó szolgáltatás fut-e."
        };
    }

    private static CheckResult SystemInfo(WmiContext ctx, HostHealth h, MonitorConfig cfg)
    {
        var os = ctx.Query("SELECT Caption, Version, LastBootUpTime FROM Win32_OperatingSystem")
                    .FirstOrDefault();
        var cs = ctx.Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem")
                    .FirstOrDefault();

        if (os is null)
            return new CheckResult
            {
                Name = "WMI kapcsolat",
                State = HealthState.Hiba,
                Detail = "A WMI lekérdezés nem sikerült.",
                Advice = "A bejelentkezett felhasználónak helyi rendszergazdának kell lennie a " +
                         "célgépen, és a tűzfalon engedélyezni kell a WMI szabályt."
            };

        h.OperatingSystem = os["Caption"]?.ToString()?.Trim() ?? "";

        var boot = HardwareInfo.ToDate(os["LastBootUpTime"]);
        if (boot.HasValue)
        {
            var up = DateTime.Now - boot.Value;
            h.Uptime = $"{(int)up.TotalDays} nap {up.Hours} óra";
        }

        var manuf = cs?["Manufacturer"]?.ToString() ?? "";
        var model = cs?["Model"]?.ToString() ?? "";
        h.Platform = $"{manuf} {model}".Trim();
        h.IsVirtual = IsVirtualMachine(manuf, model);

        return new CheckResult
        {
            Name = "WMI kapcsolat",
            State = HealthState.Rendben,
            Detail = h.OperatingSystem +
                     (h.IsVirtual ? "  ·  virtuális gép" : "  ·  fizikai gép") +
                     (h.Uptime.Length > 0 ? $"  ·  üzemidő: {h.Uptime}" : "")
        };
    }

    /// <summary>A gyakori hypervisorok felismerese gyarto/modell alapjan.</summary>
    public static bool IsVirtualMachine(string manufacturer, string model)
    {
        var s = (manufacturer + " " + model).ToLowerInvariant();
        string[] markers =
        {
            "virtual", "vmware", "kvm", "qemu", "xen", "hyper-v",
            "innotek", "virtualbox", "parallels", "bochs", "bhyve", "openstack"
        };
        return markers.Any(m => s.Contains(m));
    }

    private static CheckResult TimeSkew(WmiContext ctx, MonitorConfig cfg)
    {
        var o = ctx.Query("SELECT LocalDateTime FROM Win32_OperatingSystem").FirstOrDefault();
        var remote = o is null ? null : HardwareInfo.ToDate(o["LocalDateTime"]);

        if (remote is null)
            return new CheckResult
            {
                Name = "Óraeltérés",
                State = HealthState.Ismeretlen,
                Detail = "Nem olvasható."
            };

        var diff = Math.Abs((remote.Value - DateTime.Now).TotalSeconds);

        // A Kerberos alapertelmezetten 5 perc elterest tur; folotte a bejelentkezes elszall.
        var state = diff > 280 ? HealthState.Hiba
                  : diff > cfg.TimeSkewWarnSeconds ? HealthState.Figyelmeztetes
                  : HealthState.Rendben;

        return new CheckResult
        {
            Name = "Óraeltérés",
            State = state,
            Detail = $"{diff:0} másodperc eltérés a helyi órához képest",
            Advice = state == HealthState.Rendben ? "" :
                "A Kerberos 5 percnél nagyobb eltérésnél megtagadja a bejelentkezést. " +
                "Ellenőrizd az időszinkronizálást: w32tm /query /status"
        };
    }

    private static CheckResult DiskCapacity(WmiContext ctx, MonitorConfig cfg)
    {
        var rows = ctx.Query("SELECT DeviceID, Size, FreeSpace, VolumeName FROM Win32_LogicalDisk WHERE DriveType=3");
        if (rows.Count == 0)
            return new CheckResult
            {
                Name = "Lemezkapacitás",
                State = HealthState.Ismeretlen,
                Detail = "Nem olvasható."
            };

        var parts = new List<string>();
        var worst = HealthState.Rendben;

        foreach (var d in rows)
        {
            var size = HardwareInfo.ToULong(d["Size"]);
            var free = HardwareInfo.ToULong(d["FreeSpace"]);
            if (size == 0) continue;

            var usedPct = (size - free) * 100.0 / size;
            var id = d["DeviceID"]?.ToString() ?? "?";

            var state = usedPct >= cfg.DiskCriticalPercent ? HealthState.Hiba
                      : usedPct >= cfg.DiskWarnPercent ? HealthState.Figyelmeztetes
                      : HealthState.Rendben;

            if (state > worst) worst = state;
            parts.Add($"{id} {usedPct:0}% ({Fmt.Bytes(free)} szabad)");
        }

        return new CheckResult
        {
            Name = "Lemezkapacitás",
            State = worst,
            Detail = string.Join("  ·  ", parts),
            Advice = worst == HealthState.Rendben ? "" :
                "Tartományvezérlőn a megtelt rendszerlemez leállítja a címtárszolgáltatást."
        };
    }

    private static CheckResult PhysicalDisks(WmiContext ctx)
    {
        var rows = ctx.Query("SELECT FriendlyName, HealthStatus, OperationalStatus, MediaType " +
                             "FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");

        if (rows.Count == 0)
            return new CheckResult
            {
                Name = "Fizikai lemezek",
                State = HealthState.Ismeretlen,
                Detail = "Nem olvasható (régi rendszer vagy RAID vezérlő mögötti lemez)."
            };

        var bad = new List<string>();
        foreach (var d in rows)
        {
            var health = HardwareInfo.ToULong(d["HealthStatus"]);
            if (health == 0) continue;   // 0 = Healthy

            var name = d["FriendlyName"]?.ToString() ?? "ismeretlen";
            bad.Add($"{name}: {(health == 1 ? "figyelmeztetés" : "hibás")}");
        }

        return bad.Count == 0
            ? new CheckResult
            {
                Name = "Fizikai lemezek",
                State = HealthState.Rendben,
                Detail = $"{rows.Count} lemez kifogástalan"
            }
            : new CheckResult
            {
                Name = "Fizikai lemezek",
                State = HealthState.Hiba,
                Detail = string.Join("; ", bad),
                Advice = "Azonnali mentés és lemezcsere-tervezés indokolt."
            };
    }

    private static CheckResult Services(WmiContext ctx)
    {
        var rows = ctx.Query("SELECT Name, State, StartMode FROM Win32_Service");
        if (rows.Count == 0)
            return new CheckResult
            {
                Name = "Címtárszolgáltatások",
                State = HealthState.Ismeretlen,
                Detail = "Nem olvasható."
            };

        var map = rows.ToDictionary(
            r => r["Name"]?.ToString() ?? "",
            r => (State: r["State"]?.ToString() ?? "", Start: r["StartMode"]?.ToString() ?? ""),
            StringComparer.OrdinalIgnoreCase);

        var stopped = new List<string>();
        var missing = new List<string>();

        foreach (var (svc, label) in DcServices)
        {
            if (!map.TryGetValue(svc, out var info)) { missing.Add(label); continue; }
            if (!info.State.Equals("Running", StringComparison.OrdinalIgnoreCase) &&
                !info.Start.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                stopped.Add(label);
        }

        if (stopped.Count == 0)
            return new CheckResult
            {
                Name = "Címtárszolgáltatások",
                State = HealthState.Rendben,
                Detail = missing.Count == 0
                    ? "Mind fut"
                    : $"Fut. (Nincs telepítve: {string.Join(", ", missing)})"
            };

        return new CheckResult
        {
            Name = "Címtárszolgáltatások",
            State = HealthState.Hiba,
            Detail = "Nem fut: " + string.Join(", ", stopped),
            Advice = "Tartományvezérlőn ezek nélkül nincs bejelentkezés. Nézd meg az " +
                     "eseménynaplót a szolgáltatás indulási hibájáért."
        };
    }

    private static CheckResult SysvolShares(WmiContext ctx)
    {
        var shares = ctx.Query("SELECT Name FROM Win32_Share")
                        .Select(s => s["Name"]?.ToString() ?? "")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (shares.Count == 0)
            return new CheckResult
            {
                Name = "SYSVOL és NETLOGON",
                State = HealthState.Ismeretlen,
                Detail = "Nem olvasható."
            };

        var missing = new[] { "SYSVOL", "NETLOGON" }.Where(s => !shares.Contains(s)).ToList();

        return missing.Count == 0
            ? new CheckResult
            {
                Name = "SYSVOL és NETLOGON",
                State = HealthState.Rendben,
                Detail = "Mindkettő megosztva"
            }
            : new CheckResult
            {
                Name = "SYSVOL és NETLOGON",
                State = HealthState.Hiba,
                Detail = "Hiányzik: " + string.Join(", ", missing),
                Advice = "Ez replikációs hibára utal. A csoportházirendek nem érvényesülnek. " +
                         "Ellenőrzés: dfsrdiag ReplicationState, illetve az eseménynapló " +
                         "DFS Replication naplója."
            };
    }

    private static CheckResult RecentErrors(WmiContext ctx, CancellationToken token)
    {
        try
        {
            var scan = EventLogAnalyzer.Scan(
                ctx, new[] { "System" }, 24, includeWarnings: false,
                maxRecordsPerLog: 500, progress: null, token: token);

            var serious = scan.Groups
                .Where(g => g.EffectiveSeverity >= Severity.Komoly)
                .OrderByDescending(g => g.Rank)
                .ToList();

            if (serious.Count == 0)
                return new CheckResult
                {
                    Name = "Eseménynapló (24 óra)",
                    State = HealthState.Rendben,
                    Detail = $"{scan.TotalEvents} hiba, érdemi probléma nincs"
                };

            var top = serious[0];
            return new CheckResult
            {
                Name = "Eseménynapló (24 óra)",
                State = serious.Any(g => g.EffectiveSeverity == Severity.Kritikus)
                    ? HealthState.Hiba : HealthState.Figyelmeztetes,
                Detail = $"{serious.Count} komoly probléma – legfontosabb: {top.Headline} ({top.Count}×)",
                Advice = top.Diag?.Action ?? ""
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new CheckResult
            {
                Name = "Eseménynapló (24 óra)",
                State = HealthState.Ismeretlen,
                Detail = "Nem olvasható: " + ex.Message,
                Advice = "A távoli naplóolvasáshoz a „Remote Event Log Management” " +
                         "tűzfalszabály kell a célgépen."
            };
        }
    }
}
