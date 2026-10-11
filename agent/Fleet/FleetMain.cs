using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;

// ============================================================================
//  DevToolbox Fleet – EGY exe, két szerep:
//    - telephelyi szerver (FleetServer): az ügyfél belső hálóján, az agentek ide jelentkeznek;
//    - Központ (FleetHub): a telephelyi szerverek ide tárcsáznak be.
//
//  A szerep eldöntése, ebben a sorrendben:
//    1. első paraméter: "server" / "szerver" vagy "hub" / "kozpont"   (pl. Fleet.exe hub install)
//    2. az exe neve: a telepítő FleetServer.exe ill. FleetHub.exe néven másolja be magát, így a
//       szolgáltatás, a parancssor és a webes frissítés (*.new.exe) a régi telepítésekkel is működik;
//    3. ismeretlen helyről, paraméter nélkül indítva a varázsló kérdezi meg
//       (telephelyi szerver / Központ / mindkettő).
// ============================================================================

var role = FleetRole.Take(ref args) ?? FleetRole.FromExe(Environment.ProcessPath ?? "");
switch (role)
{
    case FleetRole.Server: ServerEntry.Run(args); return;
    case FleetRole.Hub: HubEntry.Run(args); return;
}

#if GUI_SETUP
if (Environment.UserInteractive && (args.Length == 0 || args[0].Equals("setup", StringComparison.OrdinalIgnoreCase)))
{
    WizardForm.FreeConsole();
    string? choice = null;
    ServiceWizard.OnSta(() => choice = RoleChooser.Ask());
    // „Mindkettő”: két szolgáltatás ugyanabból az exéből, a két telepítési mappában.
    if (choice is FleetRole.Server or FleetRole.Both) ServerEntry.Run(new[] { "setup" });
    if (choice is FleetRole.Hub or FleetRole.Both) HubEntry.Run(new[] { "setup" });
    return;
}
#endif

Console.WriteLine("Fleet Server és Fleet HUB – telephelyi szerver és Központ egy programban.");
Console.WriteLine();
Console.WriteLine("  Fleet.exe server <parancs>   telephelyi szerver (install, uninstall, reinstall, status, diag, token, fingerprint, run)");
Console.WriteLine("  Fleet.exe hub <parancs>      Központ (install, uninstall, reinstall, diag, run)");
Console.WriteLine("  Fleet.exe                    grafikus telepítő (szerep választása)");

static class FleetRole
{
    public const string Server = "server", Hub = "hub", Both = "both";

    /// <summary>Ha az első paraméter szerepet ad meg, azt kiveszi és visszaadja.</summary>
    public static string? Take(ref string[] args)
    {
        if (args.Length == 0) return null;
        var r = args[0].ToLowerInvariant() switch
        {
            "server" or "szerver" => Server,
            "hub" or "kozpont" or "központ" => Hub,
            _ => null
        };
        if (r is not null) args = args[1..];
        return r;
    }

    /// <summary>A szerep az exe nevéből vagy telepítési mappájából (a *.new.exe frissítés is ide tartozik).</summary>
    public static string? FromExe(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.StartsWith("FleetHub", StringComparison.OrdinalIgnoreCase)) return Hub;
        if (name.StartsWith("FleetServer", StringComparison.OrdinalIgnoreCase)) return Server;
        var dir = Path.GetDirectoryName(exe) ?? "";
        if (Same(dir, HubPaths.InstallDir)) return Hub;
        if (Same(dir, ServerPaths.InstallDir)) return Server;
        return null;
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

#if GUI_SETUP
/// <summary>Az első ablak, ha a szerep nem derül ki: mit telepítsünk erre a gépre?</summary>
static class RoleChooser
{
    public static string? Ask()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        string? result = null;
        using var f = new System.Windows.Forms.Form
        {
            Text = "Fleet Server / Fleet HUB – telepítés",
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, AutoSize = true,
            AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink,
            Padding = new System.Windows.Forms.Padding(16)
        };
        var t = new System.Windows.Forms.TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = System.Windows.Forms.DockStyle.Fill };
        t.Controls.Add(new System.Windows.Forms.Label
        {
            AutoSize = true, MaximumSize = new System.Drawing.Size(460, 0), Margin = new System.Windows.Forms.Padding(0, 0, 0, 12),
            Text = "Mit telepítsünk erre a gépre?\n\nA telephelyi szerver az ügyfél belső hálóján fut, az agentek ide jelentkeznek. " +
                   "A Központ a telephelyi szervereket fogja össze (pl. fleet.poltoli.hu mögött)."
        });
        void Btn(string text, string value)
        {
            var b = new System.Windows.Forms.Button { Text = text, Width = 460, Height = 40, Margin = new System.Windows.Forms.Padding(0, 0, 0, 8) };
            b.Click += (_, _) => { result = value; f.Close(); };
            t.Controls.Add(b);
        }
        Btn("Telephelyi szerver (FleetServer)", FleetRole.Server);
        Btn("Központ (FleetHub)", FleetRole.Hub);
        Btn("Mindkettő ezen a gépen", FleetRole.Both);
        f.Controls.Add(t);
        System.Windows.Forms.Application.Run(f);
        return result;
    }
}
#endif
