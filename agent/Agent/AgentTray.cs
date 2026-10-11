using System.Drawing;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// A tálca-ikon és a felhasználói felület (1.5.7, a felhasználó kérése és döntései). A bejelentkezett felhasználó
/// munkamenetében fut (a szolgáltatás indítja), rendszergazdai jog nélkül; mindent a szolgáltatás pipe-ján kér, a
/// szolgáltatás az agent aláírt csatornáján továbbítja a szervernek. Sima felhasználó: a jóváhagyott (repó) programok
/// telepítése, más program kérése indoklással, a saját kérések állapota. „Belépés adminként” (külön menüpont): a Fleet
/// kezelői / admin fiókjával – a jelszó nem megy át a hálón (kihívás–válasz, TrayLogin) –, utána bármely program
/// telepíthető és a gép kérései dönthetők el (15 percig).
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentTray
{
    public static void Run()
    {
        using var mutex = new Mutex(true, @"Local\DevToolboxAgentTray", out var created);
        if (!created) return;   // ebben a munkamenetben már fut
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
        using var ctx = new TrayContext();
        Application.Run(ctx);
    }
}

/// <summary>A szolgáltatás pipe-ja (egy kérés = egy kapcsolat, egy JSON-sor oda és vissza).</summary>
[SupportedOSPlatform("windows")]
internal static class TrayPipe
{
    public sealed record Reply(bool Ok, string Message, JsonElement Data);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerSessionId(IntPtr pipe, out uint sessionId);

    /// <summary>
    /// Egy kérés a szolgáltatásnak, a nyers válasz-sorral. Csak a 0. munkamenetben futó kiszolgálóval beszél (ott csak
    /// szolgáltatás futhat): egy felhasználó által előre lefoglalt, azonos nevű pipe („pipe squatting”) így nem kaphatja
    /// meg a kéréseket – a belépés válaszát sem. (Teszt módban – DEVTOOLBOX_AGENT_DIR – a saját munkamenet is jó.)
    /// </summary>
    public static async Task<string?> Raw(string json, int timeoutSec)
    {
        using var c = new NamedPipeClientStream(".", AgentTrayHost.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await c.ConnectAsync(3000);
        if (!GetNamedPipeServerSessionId(c.SafePipeHandle.DangerousGetHandle(), out var sess) ||
            (sess != 0 && Environment.GetEnvironmentVariable("DEVTOOLBOX_AGENT_DIR") is not { Length: > 0 }))
            throw new InvalidOperationException("a pipe nem az agent-szolgáltatásé – a kérés nem ment el");
        await c.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"));
        await c.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        return await AgentTrayHost.ReadLine(c, 8_000_000, cts.Token);
    }

    public static async Task<Reply> Call(string op, object? data = null, int timeoutSec = 60)
    {
        try
        {
            var line = await Raw(JsonSerializer.Serialize(new { op, data }, ProtocolJson.Options), timeoutSec);
            if (line is null) return new Reply(false, "Nincs válasz az agenttől.", default);
            var root = JsonDocument.Parse(line).RootElement;
            return new Reply(root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "",
                root.TryGetProperty("data", out var d) ? d.Clone() : default);
        }
        catch (TimeoutException) { return new Reply(false, "Az agent-szolgáltatás nem fut ezen a gépen.", default); }
        catch (Exception ex) { return new Reply(false, "Az agent nem érhető el: " + ex.Message, default); }
    }

    public static string S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public static bool B(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("items", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
}

/// <summary>A tálca-admin belépése (memóriában, 15 percig; a jegy csak erre a gépre érvényes).</summary>
internal sealed class TrayAdmin
{
    public string Token = "", User = "";
    public DateTime Until;
    public bool Active => Token.Length > 0 && DateTime.UtcNow < Until;
    public void Clear() { Token = ""; User = ""; }
}

[SupportedOSPlatform("windows")]
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private readonly TrayAdmin _admin = new();
    private readonly Dictionary<string, string> _seen = new();
    private TrayMainForm? _main;
    private bool _firstPoll = true;

    public TrayContext()
    {
        Icon icon;
        try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; } catch { icon = SystemIcons.Application; }
        _icon = new NotifyIcon { Icon = icon, Text = "DevToolbox – programok", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => ShowMain(0);
        TrayTheme.Style(_menu);   // a Fleet sötét stílusa
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
        _ = Poll();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem("Programok telepítése…", null, (_, _) => ShowMain(0)) { Font = new Font(_menu.Font, FontStyle.Bold) };
        _menu.Items.Add(open);
        _menu.Items.Add("Program kérése…", null, (_, _) => ShowMain(1));
        _menu.Items.Add("Kéréseim", null, (_, _) => ShowMain(2));
        _menu.Items.Add("Állapot…", null, async (_, _) => await ShowStatus());
        _menu.Items.Add(new ToolStripSeparator());
        if (_admin.Active)
        {
            var adm = new ToolStripMenuItem($"Admin: {_admin.User}");
            adm.DropDownItems.Add("Bármely program telepítése…", null, (_, _) => ShowMain(1));
            adm.DropDownItems.Add("Döntésre váró kérések…", null, (_, _) => new TrayApprovalsForm(_admin).Show());
            adm.DropDownItems.Add("Jelentkezés most", null, async (_, _) => { var r = await TrayPipe.Call("checkin-now"); Balloon(r.Message, r.Ok); });
            adm.DropDownItems.Add(new ToolStripSeparator());
            adm.DropDownItems.Add("Kilépés az admin módból", null, (_, _) => { _admin.Clear(); _main?.AdminChanged(); });
            TrayTheme.Style(adm.DropDown);
            adm.ForeColor = TrayTheme.Warn;
            _menu.Items.Add(adm);
        }
        else _menu.Items.Add("Belépés adminként…", null, (_, _) => Login());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Elrejtés a következő bejelentkezésig", null, async (_, _) => { await TrayPipe.Call("hide"); ExitThread(); });
    }

    private void ShowMain(int tab)
    {
        if (_main is null || _main.IsDisposed) _main = new TrayMainForm(_admin);
        _main.ShowTab(tab);
    }

    private void Login()
    {
        using var f = new TrayLoginForm();
        if (f.ShowDialog() != DialogResult.OK) return;
        _admin.Token = f.Token; _admin.User = f.User; _admin.Until = DateTime.UtcNow.AddMinutes(Math.Max(1, f.Minutes) - 0.5);
        Balloon($"Beléptél adminként ({f.User}) – {f.Minutes} percig.", true);
        _main?.AdminChanged();
    }

    private static async Task ShowStatus() => TrayTheme.Ask(null, StatusText(await StatusRoot()), "Fleet – állapot", false);

    // A "status" válasza a gyökérben van (nem a data-ban) – külön olvassuk.
    private static async Task<JsonElement> StatusRoot()
    {
        try
        {
            var line = await TrayPipe.Raw("{\"op\":\"status\"}", 10);
            return line is null ? default : JsonDocument.Parse(line).RootElement.Clone();
        }
        catch { return default; }
    }

    private static string StatusText(JsonElement s) => s.ValueKind != JsonValueKind.Object ? "Az agent nem érhető el." :
        $"Gép: {TrayPipe.S(s, "machine")}\nFelhasználó: {TrayPipe.S(s, "user")}\nAgent: {TrayPipe.S(s, "version")}\n" +
        $"Szerver: {TrayPipe.S(s, "server")}\nUtolsó jelentkezés: {(TrayPipe.S(s, "lastCheckin") is { Length: > 0 } l ? l : "még nem")}" +
        (TrayPipe.B(s, "enabled") ? "" : "\n\nA tálca-felület a szerveren ki van kapcsolva.");

    /// <summary>Percenként: kikapcsolták-e a tálcát, és változott-e a kéréseim állapota (értesítés).</summary>
    private async Task Poll()
    {
        var st = await StatusRoot();
        if (st.ValueKind == JsonValueKind.Object && !TrayPipe.B(st, "enabled") && TrayPipe.S(st, "lastCheckin").Length > 0) { ExitThread(); return; }
        var r = await TrayPipe.Call("requests");
        if (!r.Ok) return;
        foreach (var i in TrayPipe.Items(r.Data))
        {
            var id = TrayPipe.S(i, "id"); var state = TrayPipe.S(i, "state"); var name = TrayPipe.S(i, "name");
            if (_seen.TryGetValue(id, out var old) && old != state && !_firstPoll)
            {
                var msg = state switch
                {
                    "installed" => $"Települt: {name}", "failed" => $"Nem sikerült telepíteni: {name}",
                    "denied" => $"A kérést elutasították: {name}" + (TrayPipe.S(i, "note") is { Length: > 0 } n ? $" ({n})" : ""),
                    "installing" => $"Jóváhagyva, települ: {name}", _ => ""
                };
                if (msg.Length > 0) Balloon(msg, state is not ("failed" or "denied"));
            }
            _seen[id] = state;
        }
        _firstPoll = false;
    }

    private void Balloon(string text, bool ok)
    {
        if (text.Length == 0) return;
        _icon.BalloonTipTitle = "DevToolbox";
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = ok ? ToolTipIcon.Info : ToolTipIcon.Warning;
        _icon.ShowBalloonTip(5000);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        if (_main is not null) { _main.ForceClose = true; _main.Close(); }
        base.ExitThreadCore();
    }
}

