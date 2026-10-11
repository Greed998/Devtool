using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// A tálca-felület kiszolgálása a szolgáltatásban (1.5.7, a felhasználó kérése: alapból BE, a Fleetben kikapcsolható):
///  - named pipe (\\.\pipe\DevToolboxAgent-Tray) a bejelentkezett felhasználók tálca-programjának. Jog SID-del: a helyi,
///    hitelesített felhasználók írhatják / olvashatják, a hálózatról (NETWORK) tiltva; a kérő Windows-felhasználót a pipe
///    kliensének azonosításából vesszük (a tálca nem mondhatja meg magáról). Felhasználónként percenként legfeljebb 40 kérés.
///  - a tálca-program indítása a felhasználói munkamenetekben (WTSQueryUserToken + CreateProcessAsUser, ablak nélkül) –
///    bejelentkezéskor és percenként: ahol fut felhasználó, de nincs tálca (és a felhasználó nem zárta be).
/// A szerverhez a kérés az agent aláírt csatornáján megy (AgentCore.UserRequestAsync).
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentTrayHost
{
    public const string PipeName = "DevToolboxAgent-Tray";
    private static readonly string[] ServerKinds =
        { "catalog", "search", "install", "request", "requests", "login-start", "login", "admin-install", "admin-requests", "admin-decide" };

    private static readonly ConcurrentDictionary<string, (int Count, DateTime Window)> Rate = new();
    /// <summary>Munkamenetek, ahol a felhasználó bezárta a tálcát – a következő bejelentkezésig nem indítjuk újra.</summary>
    private static readonly ConcurrentDictionary<int, bool> Hidden = new();
    private static DateTime _lastLaunch = DateTime.MinValue;

    // ================================================================ pipe-kiszolgáló

    public static void Start(AgentCore core, CancellationToken token)
    {
        for (var i = 0; i < 4; i++) _ = Task.Run(() => ServeLoop(core, token), token);
        _ = Task.Run(() => LaunchLoop(core, token), token);
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // A futtató fiók (a szolgáltatásban SYSTEM) további példányokat is nyithat.
        if (WindowsIdentity.GetCurrent().User is { } me) ps.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, ps);
    }

    private static async Task ServeLoop(AgentCore core, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(token);
                await Handle(core, pipe, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { core.LogLine("Tálca-pipe: " + ex.Message); try { await Task.Delay(2000, token); } catch { break; } }
            finally { try { pipe?.Dispose(); } catch { } }
        }
    }

    private sealed class PipeReq { public string Op { get; set; } = ""; public JsonElement? Data { get; set; } }

    private static async Task Handle(AgentCore core, NamedPipeServerStream pipe, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        var line = await ReadLine(pipe, 32_768, cts.Token);
        if (line is null) return;
        // A kérő a pipe kliense (Windows-azonosítás); a munkamenete a bezárás jelzéséhez kell.
        string user = "", sid = "";
        try { pipe.RunAsClient(() => { using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query); user = id.Name; sid = id.User?.Value ?? ""; }); }
        catch (Exception ex) { core.LogLine("Tálca-pipe: a kérő nem azonosítható: " + ex.Message); }
        uint session = 0;
        try { GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out session); } catch { }

        object reply;
        try
        {
            var req = JsonSerializer.Deserialize<PipeReq>(line, ProtocolJson.Options) ?? new();
            if (sid.Length == 0) reply = new { ok = false, message = "A kérő nem azonosítható." };
            else if (!Allow(sid)) reply = new { ok = false, message = "Túl sok kérés – várj egy percet." };
            else reply = await Dispatch(core, req, user, (int)session, cts.Token);
        }
        catch (Exception ex) { reply = new { ok = false, message = "Hibás kérés: " + ex.Message }; }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, ProtocolJson.Options) + "\n");
        await pipe.WriteAsync(bytes, cts.Token);
        await pipe.FlushAsync(cts.Token);
        try { pipe.WaitForPipeDrain(); } catch { }
    }

    private static async Task<object> Dispatch(AgentCore core, PipeReq req, string user, int session, CancellationToken token)
    {
        switch (req.Op)
        {
            case "status":
                return new
                {
                    ok = true, enabled = core.TrayEnabled, machine = Environment.MachineName, server = core.ServerUrl, version = AgentCore.AgentVersion,
                    lastCheckin = core.LastCheckinUtc?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss"), user
                };
            case "checkin-now":
                core.Wake();
                return new { ok = true, message = "A gép most jelentkezik a szervernél." };
            case "hide":
                if (session > 0) Hidden[session] = true;
                return new { ok = true };
            default:
                if (!ServerKinds.Contains(req.Op)) return new { ok = false, message = "Ismeretlen kérés." };
                if (!core.TrayEnabled) return new { ok = false, message = "A tálca-felület ezen a gépen ki van kapcsolva." };
                var data = req.Data is { ValueKind: JsonValueKind.Object } d ? d.GetRawText() : "{}";
                var r = await core.UserRequestAsync(req.Op, user, data, token);
                // Telepítés / jóváhagyás után azonnal jelentkezzen: a feladat így hamar átjön.
                if (r.Ok && req.Op is "install" or "request" or "admin-install" or "admin-decide") core.Wake();
                return new { ok = r.Ok, message = r.Message, data = r.Data.Length > 0 ? JsonDocument.Parse(r.Data).RootElement : (JsonElement?)null };
        }
    }

    private static bool Allow(string sid)
    {
        var now = DateTime.UtcNow;
        var v = Rate.AddOrUpdate(sid, _ => (1, now), (_, o) => now - o.Window > TimeSpan.FromMinutes(1) ? (1, now) : (o.Count + 1, o.Window));
        return v.Count <= 40;
    }

    public static async Task<string?> ReadLine(Stream s, int max, CancellationToken token)
    {
        var buf = new List<byte>(1024);
        var one = new byte[1];
        while (buf.Count < max)
        {
            var n = await s.ReadAsync(one, token);
            if (n == 0) return buf.Count > 0 ? Encoding.UTF8.GetString(buf.ToArray()) : null;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.Add(one[0]);
        }
        return null;   // túl hosszú kérés
    }

    // ================================================================ indítás a munkamenetekben

    /// <summary>Bejelentkezéskor / feloldáskor (a szolgáltatás munkamenet-eseménye): a bezárás-jelzés törlődik, és indul.</summary>
    public static void OnSessionChange(int sessionId, bool logon)
    {
        if (logon) Hidden.TryRemove(sessionId, out _);
        _lastLaunch = DateTime.MinValue;
    }

    private static async Task LaunchLoop(AgentCore core, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch { break; }
            if (!core.TrayEnabled || DateTime.UtcNow - _lastLaunch < TimeSpan.FromSeconds(60)) continue;
            _lastLaunch = DateTime.UtcNow;
            try { LaunchInSessions(core); } catch (Exception ex) { core.LogLine("Tálca indítása: " + ex.Message); }
        }
    }

    private static void LaunchInSessions(AgentCore core)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var running = Process.GetProcessesByName("DevToolboxAgent").Where(p => p.Id != Environment.ProcessId).Select(p => { try { return p.SessionId; } catch { return -1; } }).ToHashSet();
        foreach (var sid in ActiveSessions())
        {
            if (sid == 0 || running.Contains(sid) || Hidden.ContainsKey(sid)) continue;
            if (StartInSession(sid, exe, "tray")) core.LogLine($"Tálca elindítva a(z) {sid}. munkamenetben.");
        }
    }

    /// <summary>A tálca-programok leállítása (önfrissítés, eltávolítás előtt: a futó exe nem cserélhető).</summary>
    public static void StopAll(string exePath)
    {
        foreach (var p in Process.GetProcessesByName("DevToolboxAgent"))
            try
            {
                if (p.Id != Environment.ProcessId && p.SessionId != 0 && string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                    p.Kill(true);
            }
            catch { }
    }

    // ---- Win32
    private const int WTSActive = 0;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr hServer, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint sessionId);

    private static List<int> ActiveSessions()
    {
        var res = new List<int>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var p, out var n)) return res;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < n; i++)
            {
                var s = Marshal.PtrToStructure<WTS_SESSION_INFO>(p + i * size);
                if (s.State == WTSActive) res.Add(s.SessionId);
            }
        }
        finally { WTSFreeMemory(p); }
        return res;
    }

    /// <summary>A program indítása a munkamenet felhasználójaként, a felhasználó asztalán, konzolablak nélkül.</summary>
    private static bool StartInSession(int session, string exe, string args)
    {
        if (!WTSQueryUserToken(session, out var tok)) return false;   // nincs bejelentkezett felhasználó
        var env = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out env, tok, false);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
            var cmd = new StringBuilder($"\"{exe}\" {args}");
            if (!CreateProcessAsUserW(tok, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env,
                    Path.GetDirectoryName(exe), ref si, out var pi)) return false;
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(tok);
        }
    }
}
