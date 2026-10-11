#if GUI_SETUP
using System.Drawing;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Forms;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Az agent grafikus telepitoje (dupla kattintas az exe-re). A kapcsolodasi adatokat
/// (szerver, token, ujjlenyomat) a webes csomag melletti DevToolboxAgent.telepites.json
/// elore kitolti; a "Kapcsolat tesztelese" telepites ELOTT ellenorzi a szervert es a kulcsot.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentSetupWizard
{
    public const string PrefillFile = "DevToolboxAgent.telepites.json";

    private sealed class Prefill
    {
        public string Server { get; set; } = "";
        public string Token { get; set; } = "";
        public string Fingerprint { get; set; } = "";
    }

    public static void Run()
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var existing = AgentConfig.Load();
        var upgrade = existing.AgentId.Length > 0;
        var pre = LoadPrefill();
        var fromPackage = pre is not null;

        // ---- 1. üdvözlés
        var welcome = new Panel();
        welcome.Controls.Add(WizardForm.Para(
            "A DevToolbox Agent a gépet a Fleet Serverhez kapcsolja: jelenti az állapotát, a frissíthető " +
            "programokat és a mérési adatokat, és végrehajtja a szerverről kiadott – aláírt – feladatokat.\n\n" +
            "Windows-szolgáltatásként fut, rendszerjogon, a háttérben. A kapcsolatot mindig az agent nyitja " +
            "kifelé, a gépen nem kell portot nyitni.\n\n" +
            (upgrade
                ? "Ezen a gépen már van DevToolbox Agent. A telepítő FRISSÍTI: a regisztráció, a kulcsok és a beállítások megmaradnak."
                : "A következő lépésben add meg a szerver adatait.")));

        // ---- 2. kapcsolódás
        var conn = new Panel();
        int y = 0;
        var tbServer = WizardForm.Field(conn, "Fleet Server címe (agent-port)", pre?.Server ?? existing.ServerUrl, ref y);
        var tbToken = WizardForm.Field(conn, upgrade ? "Beléptető token (frissítésnél nem kötelező)" : "Beléptető token", pre?.Token ?? "", ref y);
        var tbFp = WizardForm.Field(conn, "Szerver ujjlenyomat", pre?.Fingerprint ?? existing.ServerFingerprint, ref y);
        var btnTest = new Button { Text = "Kapcsolat tesztelése", Location = new Point(0, y + 2), Width = 170, Height = 30 };
        var lblTest = new Label { Location = new Point(0, y + 40), Size = new Size(470, 60), ForeColor = Color.DimGray,
            Text = fromPackage ? "Az adatok a letöltött telepítőcsomagból töltődtek be." : "" };
        conn.Controls.Add(btnTest); conn.Controls.Add(lblTest);

        var tested = false;
        async Task<bool> Test()
        {
            lblTest.ForeColor = Color.DimGray; lblTest.Text = "Ellenőrzés…";
            var (ok, msg) = await SetupCheck.TestServerAsync(tbServer.Text, tbFp.Text);
            if (ok && !upgrade && tbToken.Text.Trim().Length == 0) { ok = false; msg = "Add meg a beléptető tokent (a Fleet Server webfelületén: Új agent)."; }
            if (ok && tbToken.Text.Trim().Length > 0 && !Crypto.IsWellFormedToken(tbToken.Text.Trim())) { ok = false; msg = "A token formátuma hibás."; }
            lblTest.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            lblTest.Text = (ok ? "✔ " : "✖ ") + msg;
            return tested = ok;
        }
        btnTest.Click += async (_, _) => { btnTest.Enabled = false; try { await Test(); } finally { btnTest.Enabled = true; } };
        foreach (var tb in new[] { tbServer, tbToken, tbFp }) tb.TextChanged += (_, _) => tested = false;

        // ---- 3. összegzés
        var summaryLbl = WizardForm.Para("");
        var summary = new Panel(); summary.Controls.Add(summaryLbl);

        // ---- 4. telepítés
        var log = WizardForm.LogBox();
        var installPanel = new Panel(); installPanel.Controls.Add(log);
        var success = false;

        // ---- 5. kész
        var doneLbl = WizardForm.Para("");
        var done = new Panel(); done.Controls.Add(doneLbl);

        var pages = new List<WizardPage>
        {
            new() { Title = upgrade ? "DevToolbox Agent frissítése" : "Üdvözöl a DevToolbox Agent telepítője",
                    Subtitle = "A telepítéshez rendszergazdai jog szükséges – ez már megvan.", Body = welcome },
            new() { Title = "Kapcsolódás a Fleet Serverhez",
                    Subtitle = "Ezekkel az adatokkal kommunikál az agent a szerverrel. Telepítés előtt ellenőrizzük őket.",
                    Body = conn,
                    OnNext = async () => tested || await Test() },
            new() { Title = "Összegzés", Subtitle = "Ellenőrizd, majd kattints a Telepítés gombra.", Body = summary,
                    NextText = "Telepítés" },
            new() { Title = "Telepítés folyamatban…", Subtitle = "A szolgáltatás létrehozása, indítása és ellenőrzése.",
                    Body = installPanel, CanGoBack = false,
                    OnEnter = async () =>
                    {
                        var args = new List<string> { "install", "--server", tbServer.Text.Trim() };
                        if (tbToken.Text.Trim().Length > 0) { args.Add("--token"); args.Add(tbToken.Text.Trim()); }
                        if (tbFp.Text.Trim().Length > 0) { args.Add("--fingerprint"); args.Add(tbFp.Text.Trim()); }
                        await WizardForm.RunWithLog(log, () => AgentInstaller.Install(args.ToArray()));
                        success = IsRunning();
                        doneLbl.Text = success
                            ? "A DevToolbox Agent telepítve, és szolgáltatásként fut.\n\n" +
                              "Néhány percen belül megjelenik a Fleet Server webfelületén. Ez az ablak bezárható, " +
                              "a letöltött telepítőcsomag törölhető.\n\nEllenőrzés bármikor (rendszergazdai parancssor):\n" +
                              $"  \"{AgentInstaller.TargetExe}\" diag"
                            : "A telepítés nem fejeződött be sikeresen – a részletek az előző lépés naplójában voltak.\n\n" +
                              $"Ellenőrzés:  \"{AgentInstaller.TargetExe}\" diag\nNapló:  {AgentLog.FilePath}";
                        if (fromPackage && success) TryDeletePrefill();   // a token ne maradjon a gépen
                    } },
            new() { Title = "Kész", Subtitle = "", Body = done, IsLast = true, CanGoBack = false },
        };

        var form = new WizardForm("DevToolbox Agent", AgentCore.AgentVersion, pages);
        // az összegzés szövege belépéskor frissül
        form.Shown += (_, _) => { };
        pages[2].Body.VisibleChanged += (_, _) =>
            summaryLbl.Text =
                $"Telepítési hely:   {AgentInstaller.InstallDir}\n" +
                $"Szolgáltatás:       DevToolbox Agent (automatikus indítás, LocalSystem)\n" +
                $"Beállítások:         {AgentConfig.Dir}\n\n" +
                $"Fleet Server:       {tbServer.Text.Trim()}\n" +
                $"Ujjlenyomat:        {(tbFp.Text.Trim().Length > 0 ? tbFp.Text.Trim()[..Math.Min(24, tbFp.Text.Trim().Length)] + "…" : "(a meglévő)")}\n" +
                $"Token:                 {(tbToken.Text.Trim().Length > 0 ? "megadva" : "nincs – a meglévő regisztráció marad")}\n\n" +
                (upgrade ? "A meglévő agent leáll, lecserélődik és újraindul." : "Az agent a telepítés után azonnal regisztrál a szerveren.");
        Application.Run(form);
    }

    private static Prefill? LoadPrefill()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, PrefillFile);
            if (!File.Exists(path)) path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", PrefillFile);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Prefill>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    private static void TryDeletePrefill()
    {
        try { File.Delete(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", PrefillFile)); } catch { }
    }

    private static bool IsRunning()
    {
        try { using var sc = new ServiceController(AgentInstaller.ServiceName); return sc.Status == ServiceControllerStatus.Running; }
        catch { return false; }
    }
}
#endif
