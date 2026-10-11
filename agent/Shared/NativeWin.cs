using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;

namespace DevToolbox.Fleet;

// =====================================================================================
//  NATÍV WINDOWS-HÍVÁSOK – külső parancssor (sc, icacls, netsh, shutdown, netstat,
//  PowerShell, cmd) nélkül. Korábban ezek háttérben induló folyamatok voltak: lassabbak,
//  nyelvfüggő kimenetet kellett elemezni, és a vírusirtók is gyanúsnak találták.
// =====================================================================================

/// <summary>Windows-szolgáltatás létrehozása, módosítása, törlése a szolgáltatáskezelőn át (advapi32).</summary>
[SupportedOSPlatform("windows")]
public static class NativeService
{
    public static bool Exists(string name)
    {
        try { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>Állapot szövegesen; null, ha nincs ilyen szolgáltatás.</summary>
    public static ServiceControllerStatus? Status(string name)
    {
        if (!Exists(name)) return null;
        try { using var sc = new ServiceController(name); return sc.Status; }
        catch { return null; }
    }

    public static string StatusText(string name) => Status(name) switch
    {
        null => "NINCS TELEPITVE",
        ServiceControllerStatus.Running => "RUNNING (fut)",
        ServiceControllerStatus.Stopped => "STOPPED (leallt)",
        ServiceControllerStatus.StartPending => "START_PENDING (indul)",
        ServiceControllerStatus.StopPending => "STOP_PENDING (leall)",
        var s => s.ToString()!.ToUpperInvariant()
    };

    /// <summary>
    /// Létrehozza (vagy ha már van, átállítja) az automatikusan induló, LocalSystem alatt futó
    /// szolgáltatást, leírással és újraindítási szabállyal: hiba után 1, 1, majd 2 perc múlva újraindul.
    /// </summary>
    public static void CreateOrUpdate(string name, string displayName, string description, string exePath)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "szolgáltatáskezelő");
        try
        {
            var bin = "\"" + exePath + "\"";
            var svc = OpenServiceW(scm, name, SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                // obj = null -> LocalSystem
                svc = CreateServiceW(scm, name, displayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, bin, null, IntPtr.Zero, null, null, null);
                if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "szolgáltatás létrehozása");
            }
            else if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, SERVICE_AUTO_START, SERVICE_NO_CHANGE,
                         bin, null, IntPtr.Zero, null, null, null, displayName))
            {
                var err = Marshal.GetLastWin32Error();
                CloseServiceHandle(svc);
                throw new Win32Exception(err, "szolgáltatás módosítása");
            }

            try
            {
                var d = new SERVICE_DESCRIPTION { lpDescription = description };
                ChangeServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, ref d);
                SetRestartOnFailure(svc);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Törlésre jelölés. A tényleges eltűnés a folyamat kilépése után következik be.</summary>
    public static void Delete(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return;
        try
        {
            var svc = OpenServiceW(scm, name, DELETE);
            if (svc == IntPtr.Zero) return;
            try
            {
                if (!DeleteService(svc))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != ERROR_SERVICE_MARKED_FOR_DELETE) throw new Win32Exception(err, "szolgáltatás törlése");
                }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static bool Start(string name, TimeSpan wait)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, wait);
            return true;
        }
        catch { return false; }
    }

    public static bool Stop(string name, TimeSpan wait)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status == ServiceControllerStatus.Stopped) return true;
            if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, wait);
            return true;
        }
        catch { return false; }
    }

    private static void SetRestartOnFailure(IntPtr svc)
    {
        // Ugyanaz, mint korábban: sc failure reset= 86400 actions= restart/60000/restart/60000/restart/120000
        var actions = new[] { new SC_ACTION(SC_ACTION_RESTART, 60000), new SC_ACTION(SC_ACTION_RESTART, 60000), new SC_ACTION(SC_ACTION_RESTART, 120000) };
        var size = Marshal.SizeOf<SC_ACTION>();
        var mem = Marshal.AllocHGlobal(size * actions.Length);
        try
        {
            for (int i = 0; i < actions.Length; i++) Marshal.StructureToPtr(actions[i], mem + i * size, false);
            var fa = new SERVICE_FAILURE_ACTIONS { dwResetPeriod = 86400, cActions = (uint)actions.Length, lpsaActions = mem };
            ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, ref fa);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F, SERVICE_ALL_ACCESS = 0xF01FF, DELETE = 0x10000;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1, SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const int SC_ACTION_RESTART = 1, ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTION { public string lpDescription; }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SC_ACTION { public readonly int Type; public readonly uint Delay; public SC_ACTION(int t, uint d) { Type = t; Delay = d; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_FAILURE_ACTIONS { public uint dwResetPeriod; public string? lpRebootMsg; public string? lpCommand; public uint cActions; public IntPtr lpsaActions; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(IntPtr scm, string name, string display, uint access, uint type, uint start,
        uint error, string bin, string? group, IntPtr tag, string? deps, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint error, string? bin, string? group,
        IntPtr tag, string? deps, string? account, string? password, string? display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr svc, uint level, ref SERVICE_DESCRIPTION info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr svc, uint level, ref SERVICE_FAILURE_ACTIONS info);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr svc);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr h);
}

/// <summary>Mappajogok: csak a SYSTEM és a rendszergazdák (SID-del – nyelvfüggetlen). Az icacls helyett.</summary>
[SupportedOSPlatform("windows")]
public static class NativeAcl
{
    public static void RestrictToSystemAndAdmins(string dir)
    {
        Directory.CreateDirectory(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);   // öröklés tiltása, az örökölt jogok nélkül
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(sec);   // a gyermekelemekre a Windows maga örökíti
    }
}

/// <summary>Windows tűzfal a HNetCfg.FwPolicy2 COM-objektumon át – a netsh advfirewall helyett.</summary>
[SupportedOSPlatform("windows")]
public static class NativeFirewall
{
    private const int TcpProtocol = 6, UdpProtocol = 17, DirectionIn = 1, ActionAllow = 1, AllProfiles = 0x7FFFFFFF;

    /// <summary>Az azonos nevű szabályok törlése, majd egy bejövő TCP- (udp: UDP-) szabály minden profilon, a megadott címekről.</summary>
    public static void Replace(string name, IEnumerable<int> ports, string remoteAddresses, string description, bool udp = false)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        RemoveAll(policy, name);
        dynamic rule = Create("HNetCfg.FWRule");
        rule.Name = name;
        rule.Description = description;
        rule.Protocol = udp ? UdpProtocol : TcpProtocol;   // a port csak a protokoll után adható meg
        rule.LocalPorts = string.Join(",", ports);
        rule.RemoteAddresses = remoteAddresses;
        rule.Direction = DirectionIn;
        rule.Action = ActionAllow;
        rule.Profiles = AllProfiles;
        rule.Enabled = true;
        policy.Rules.Add(rule);
    }

    public static void Delete(string name)
    {
        try { RemoveAll(Create("HNetCfg.FwPolicy2"), name); } catch { }
    }

    /// <summary>A szabály portjai és profiljai (diagnosztikához); null, ha nincs ilyen.</summary>
    public static (string LocalPorts, bool AllProfiles, bool Enabled)? Find(string name)
    {
        try
        {
            dynamic policy = Create("HNetCfg.FwPolicy2");
            dynamic r = policy.Rules.Item(name);
            return ((string)r.LocalPorts, ((int)r.Profiles & 0x7) == 0x7, (bool)r.Enabled);
        }
        catch { return null; }
    }

    private static void RemoveAll(dynamic policy, string name)
    {
        // A Remove egyszerre egyet töröl; a netsh "delete rule name=" az összes azonos nevűt.
        for (int i = 0; i < 50; i++)
        {
            try { _ = policy.Rules.Item(name); } catch { return; }
            policy.Rules.Remove(name);
        }
    }

    private static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;
}

/// <summary>HTTP URL-foglalás (http.sys) – a "netsh http add urlacl" helyett.</summary>
[SupportedOSPlatform("windows")]
public static class NativeUrlAcl
{
    /// <summary>Foglalás a megadott SDDL-lel. Ha már létezik, lecseréli (így a jogosultság is naprakész).</summary>
    public static void Ensure(string urlPrefix, string sddl)
    {
        var ver = new HTTPAPI_VERSION { Major = 1, Minor = 0 };
        var rc = HttpInitialize(ver, HTTP_INITIALIZE_CONFIG, IntPtr.Zero);
        if (rc != 0) throw new Win32Exception((int)rc, "http.sys");
        try
        {
            var set = new HTTP_SERVICE_CONFIG_URLACL_SET { UrlPrefix = urlPrefix, Sddl = sddl };
            var size = Marshal.SizeOf<HTTP_SERVICE_CONFIG_URLACL_SET>();
            rc = HttpSetServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclInfo, ref set, size, IntPtr.Zero);
            if (rc == ERROR_ALREADY_EXISTS)
            {
                HttpDeleteServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclInfo, ref set, size, IntPtr.Zero);
                rc = HttpSetServiceConfiguration(IntPtr.Zero, HttpServiceConfigUrlAclInfo, ref set, size, IntPtr.Zero);
            }
            if (rc != 0) throw new Win32Exception((int)rc, "URL-foglalás: " + urlPrefix);
        }
        finally { HttpTerminate(HTTP_INITIALIZE_CONFIG, IntPtr.Zero); }
    }

    private const uint HTTP_INITIALIZE_CONFIG = 2, ERROR_ALREADY_EXISTS = 183;
    private const int HttpServiceConfigUrlAclInfo = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct HTTPAPI_VERSION { public ushort Major; public ushort Minor; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HTTP_SERVICE_CONFIG_URLACL_SET { public string UrlPrefix; public string Sddl; }

    [DllImport("httpapi.dll")] private static extern uint HttpInitialize(HTTPAPI_VERSION v, uint flags, IntPtr reserved);
    [DllImport("httpapi.dll")] private static extern uint HttpTerminate(uint flags, IntPtr reserved);
    [DllImport("httpapi.dll")]
    private static extern uint HttpSetServiceConfiguration(IntPtr h, int id, ref HTTP_SERVICE_CONFIG_URLACL_SET info, int len, IntPtr ov);
    [DllImport("httpapi.dll")]
    private static extern uint HttpDeleteServiceConfiguration(IntPtr h, int id, ref HTTP_SERVICE_CONFIG_URLACL_SET info, int len, IntPtr ov);
}

/// <summary>Újraindítás ütemezése (InitiateShutdown) – a shutdown.exe helyett.</summary>
[SupportedOSPlatform("windows")]
public static class NativeShutdown
{
    /// <summary>
    /// Újraindítás <paramref name="delaySeconds"/> múlva, üzenettel. A shutdown.exe /r /t N /d p:4:1
    /// megfelelője: késleltetéssel a futó programokat a rendszer zárja be, az ok „tervezett karbantartás”.
    /// </summary>
    public static void Restart(int delaySeconds, string message)
    {
        EnablePrivilege("SeShutdownPrivilege");
        // SHUTDOWN_FORCE_SELF KÖTELEZŐ: ha a grace period (delaySeconds) > 0, és a flag nem tartalmazza,
        // az InitiateShutdownW ERROR_INVALID_PARAMETER (87)-tel elszáll, és a gép sosem indul újra.
        // (A forrás SYSTEM-ként, a 0-s munkamenetben fut, ezért a "saját munkamenet" kijelentkeztetése
        //  itt veszteség nélküli.) FORCE_OTHERS: a bejelentkezett felhasználókat is kijelentkezteti.
        const uint SHUTDOWN_FORCE_OTHERS = 0x1, SHUTDOWN_FORCE_SELF = 0x2, SHUTDOWN_RESTART = 0x4;
        const uint FLAGS = SHUTDOWN_RESTART | SHUTDOWN_FORCE_OTHERS | SHUTDOWN_FORCE_SELF;
        const uint REASON = 0x80000000 /*tervezett*/ | 0x00040000 /*alkalmazás*/ | 0x1 /*karbantartás*/;
        var rc = InitiateShutdownW(null, message, (uint)delaySeconds, FLAGS, REASON);
        if (rc != 0) throw new Win32Exception((int)rc, "újraindítás ütemezése");
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var tok))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "folyamat-token");
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error(), name);
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), name);
        }
        finally { CloseHandle(tok); }
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint InitiateShutdownW(string? machine, string? message, uint grace, uint flags, uint reason);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

/// <summary>Hálózati információk a netstat és a PowerShell (Get-NetConnectionProfile) helyett.</summary>
[SupportedOSPlatform("windows")]
public static class NativeNet
{
    /// <summary>A TCP-porton figyelő folyamat azonosítója (IPv4 és IPv6), vagy null.</summary>
    public static int? ListeningPid(int port)
    {
        foreach (var af in new[] { AF_INET, AF_INET6 })
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) continue;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) continue;
                var count = Marshal.ReadInt32(buf);
                // MIB_TCPROW_OWNER_PID: 6 x uint (24 bájt); MIB_TCP6ROW_OWNER_PID: 16+4+4 + 16+4+4 + 4 + 4 (56 bájt)
                int rowSize = af == AF_INET ? 24 : 56, portOff = af == AF_INET ? 8 : 20, pidOff = af == AF_INET ? 20 : 52;
                for (int i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    var raw = (uint)Marshal.ReadInt32(row + portOff);
                    var p = ((int)(raw & 0xFF) << 8) | (int)((raw >> 8) & 0xFF);   // hálózati bájtsorrend
                    if (p == port) return Marshal.ReadInt32(row + pidOff);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    /// <summary>A csatlakoztatott hálózatok neve és profilja (Nyilvános / Magán / Tartományi).</summary>
    public static List<string> ConnectionProfiles()
    {
        var list = new List<string>();
        try
        {
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!)!;
            foreach (dynamic n in nlm.GetNetworks(1))   // 1 = csak a csatlakoztatottak
            {
                int cat = n.GetCategory();
                list.Add($"{(string)n.GetName()} = {cat switch { 0 => "Nyilvános", 1 => "Magán", 2 => "Tartományi", _ => cat.ToString() }}");
            }
        }
        catch (Exception ex) { list.Add("nem olvasható: " + ex.Message); }
        return list;
    }

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
}

/// <summary>
/// Titok tárolása a Windows DPAPI-val (crypt32), a futó fiók kulcsával: szolgáltatásként a SYSTEM-é, így
/// a konfigurációs fájlból (vagy annak mentéséből) más fiók, más gép nem fejti vissza. Külön NuGet-csomag nélkül.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeDpapi
{
    private static readonly byte[] Entropy = "DevToolbox.Fleet.Secret.v1"u8.ToArray();

    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        return Convert.ToBase64String(Run(System.Text.Encoding.UTF8.GetBytes(plain), protect: true));
    }

    /// <summary>Visszafejtés; null, ha nem sikerül (más fiók / más gép titkosította, vagy sérült).</summary>
    public static string? Unprotect(string protectedBase64)
    {
        if (protectedBase64.Length == 0) return "";
        try { return System.Text.Encoding.UTF8.GetString(Run(Convert.FromBase64String(protectedBase64), protect: false)); }
        catch { return null; }
    }

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = new DATA_BLOB(); var ent = new DATA_BLOB(); var output = new DATA_BLOB();
        try
        {
            input = Blob(data); ent = Blob(Entropy);
            var ok = protect
                ? CryptProtectData(ref input, null, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
            if (ent.pbData != IntPtr.Zero) Marshal.FreeHGlobal(ent.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static DATA_BLOB Blob(byte[] b)
    {
        var p = Marshal.AllocHGlobal(Math.Max(1, b.Length));
        Marshal.Copy(b, 0, p, b.Length);
        return new DATA_BLOB { cbData = b.Length, pbData = p };
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}

/// <summary>Rendszergazdai jog ellenőrzése (a telepítők közös segédje).</summary>
[SupportedOSPlatform("windows")]
public static class NativeAdmin
{
    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
