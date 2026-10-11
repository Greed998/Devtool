using System.Diagnostics;
using System.Text;

namespace DevToolbox;

public sealed class ScriptRunner
{
    public event Action<string, bool>? OutputReceived;   // (sor, hibae)
    public event Action<int, TimeSpan>? Finished;        // (kilepesi kod, futasido)

    private Process? _process;
    public bool IsRunning => _process is { HasExited: false };

    public async Task RunAsync(ScriptItem item, CancellationToken token = default)
    {
        if (IsRunning) throw new InvalidOperationException("Már fut egy szkript.");
        if (!File.Exists(item.FullPath)) throw new FileNotFoundException("A szkript fájl nem található.", item.FullPath);

        var sw = Stopwatch.StartNew();

        if (item.RunAsAdmin)
        {
            // Emelt jogon nem lehet a kimenetet atiranyitani (UAC korlat).
            RunElevated(item);
            Finished?.Invoke(0, sw.Elapsed);
            return;
        }

        var psi = BuildStartInfo(item);
        var enc = ResolveEncoding(item.OutputEncoding);
        psi.StandardOutputEncoding = enc;
        psi.StandardErrorEncoding = enc;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data, false); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data, true); };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        try
        {
            await _process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            Kill();
            OutputReceived?.Invoke("[megszakítva]", true);
        }

        sw.Stop();
        var code = 0;
        try { code = _process.ExitCode; } catch { }

        _process?.Dispose();
        _process = null;

        Finished?.Invoke(code, sw.Elapsed);
    }

    public void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { }
    }

    // ================================================================ futtathatoság

    private static readonly Lazy<string?> WslPath = new(() => Find("wsl.exe"));
    private static readonly Lazy<string?> PwshPath = new(() => Find("pwsh.exe"));
    private static readonly Lazy<string?> PythonPath = new(() => Find("python.exe"));

    private static string? Find(string exe)
    {
        try
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (var dir in paths)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }

            // A wsl.exe a System32-ben van, de 32 bites folyamatbol atiranyitodik.
            var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
            if (File.Exists(sys)) return sys;
        }
        catch { }
        return null;
    }

    public static bool WslAvailable => WslPath.Value is not null;
    public static bool PwshAvailable => PwshPath.Value is not null;

    /// <summary>Futtathato-e ezen a gepen, es ha nem, miert nem.</summary>
    public static (bool CanRun, string Reason) CanRun(ScriptKind kind) => kind switch
    {
        ScriptKind.Batch or ScriptKind.PowerShell or ScriptKind.VBScript => (true, ""),

        ScriptKind.PowerShellCore => PwshAvailable
            ? (true, "")
            : (true, "A PowerShell 7 (pwsh.exe) nincs telepítve – a beépített Windows " +
                     "PowerShell fut helyette, ami egyes újabb nyelvi elemeket nem ismer."),

        ScriptKind.Python => PythonPath.Value is not null
            ? (true, "")
            : (false, "Nincs python.exe a PATH-ban. Telepítsd a Pythont, vagy másold ki a " +
                      "szkriptet a célgépre."),

        ScriptKind.Bash => WslAvailable
            ? (true, "A szkript a WSL-en keresztül fut (Linux alrendszer).")
            : (false, "Bash szkript futtatásához WSL kell (Linux alrendszer Windowson). " +
                      "Telepítés: wsl --install. Enélkül a szkript tárolható és kimásolható, " +
                      "de itt nem futtatható."),

        ScriptKind.Sql => (false,
            "SQL szkript nem futtatható közvetlenül – nyisd meg az adatbázis-kezelőben, " +
            "vagy másold ki."),

        _ => (false, "Ehhez a típushoz nincs értelmező beállítva.")
    };

    /// <summary>Windows utvonal WSL-es alakra: C:\\x\\y  ->  /mnt/c/x/y</summary>
    internal static string ToWslPath(string windowsPath)
    {
        var p = windowsPath.Replace('\\', '/');
        if (p.Length > 1 && p[1] == ':')
            p = "/mnt/" + char.ToLowerInvariant(p[0]) + p[2..];
        return p;
    }

    private static ProcessStartInfo BuildStartInfo(ScriptItem item)
    {
        var path = item.FullPath;
        var args = item.Arguments ?? "";

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppPaths.ScriptsDir
        };

        switch (item.Kind)
        {
            case ScriptKind.Batch:
                psi.FileName = "cmd.exe";
                // /s: a kulso idezojelek levagasa; chcp 65001: UTF-8 kimenet (ekezetek)
                psi.Arguments = item.OutputEncoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
                    ? $"/s /c \"chcp 65001>nul & call \"{path}\" {args}\""
                    : $"/s /c \"call \"{path}\" {args}\"";
                break;

            case ScriptKind.PowerShell:
                psi.FileName = "powershell.exe";
                var quoted = path.Replace("'", "''");
                psi.Arguments =
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                    $"\"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & '{quoted}' {args}\"";
                break;

            case ScriptKind.VBScript:
                psi.FileName = "cscript.exe";
                psi.Arguments = $"//nologo \"{path}\" {args}";
                break;

            case ScriptKind.PowerShellCore:
                psi.FileName = PwshPath.Value ?? "powershell.exe";
                var q2 = path.Replace("'", "''");
                psi.Arguments =
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                    $"\"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & '{q2}' {args}\"";
                break;

            case ScriptKind.Python:
                psi.FileName = PythonPath.Value ?? "python.exe";
                psi.Arguments = $"\"{path}\" {args}";
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                break;

            case ScriptKind.Bash:
                // A WSL a Windows utvonalat /mnt/c/... alakban latja.
                psi.FileName = WslPath.Value ?? "wsl.exe";
                psi.Arguments = $"bash \"{ToWslPath(path)}\" {args}";
                break;

            default:
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/s /c \"\"{path}\" {args}\"";
                break;
        }

        return psi;
    }

    private static void RunElevated(ScriptItem item)
    {
        var path = item.FullPath;
        var args = item.Arguments ?? "";

        var psi = new ProcessStartInfo
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppPaths.ScriptsDir
        };

        switch (item.Kind)
        {
            case ScriptKind.PowerShell:
                psi.FileName = "powershell.exe";
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{path}\" {args}";
                break;
            case ScriptKind.VBScript:
                psi.FileName = "cscript.exe";
                psi.Arguments = $"//nologo \"{path}\" {args}";
                break;
            case ScriptKind.Python:
                psi.FileName = "python.exe";
                psi.Arguments = $"\"{path}\" {args}";
                break;
            default:
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/k \"\"{path}\" {args}\"";
                break;
        }

        Process.Start(psi);
    }

    private static Encoding ResolveEncoding(string name)
    {
        try
        {
            return name.ToLowerInvariant() switch
            {
                "cp852" => Encoding.GetEncoding(852),
                "cp1250" => Encoding.GetEncoding(1250),
                "cp437" => Encoding.GetEncoding(437),
                _ => new UTF8Encoding(false)
            };
        }
        catch { return new UTF8Encoding(false); }
    }

    public static readonly string[] SupportedEncodings = { "utf-8", "cp852", "cp1250", "cp437" };
}
