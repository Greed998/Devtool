#if GUI_SETUP
using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Windows.Forms;

namespace DevToolbox.Fleet;

/// <summary>
/// A FleetServer es a Kozpont telepitovarazsloja: portok -> osszegzes -> telepites -> kesz,
/// a vegen egy gombbal megnyilik a webes elso beallitas. (Az admin fiokot a web keri be.)
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceWizard
{
    public sealed record PortField(string Label, int Value, string Hint);

    /// <param name="save">a megadott portok elmentese a konfigba (telepites elott)</param>
    /// <param name="install">a program sajat telepitoje (Console-ra ir)</param>
    public static void Run(string product, string version, string serviceName, string intro,
        IReadOnlyList<PortField> ports, Action<int[]> save, Action install, Func<int[], string> webUrl, string extraDone = "")
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var upgrade = Exists(serviceName);

        var welcome = new Panel();
        welcome.Controls.Add(WizardForm.Para(intro + "\n\n" + (upgrade
            ? "Ezen a gépen már telepítve van. A telepítő FRISSÍTI: a beállítások, kulcsok és adatok megmaradnak (előtte automatikus mentés készül)."
            : "Windows-szolgáltatásként települ, automatikus indítással. A telepítés után az ablak bezárható.")));

        var portPanel = new Panel(); int y = 0;
        var boxes = ports.Select(p =>
        {
            var tb = WizardForm.Field(portPanel, p.Label, p.Value.ToString(), ref y);
            portPanel.Controls.Add(new Label { Text = p.Hint, Location = new Point(0, y - 8), AutoSize = true, ForeColor = Color.DimGray });
            y += 18;
            return tb;
        }).ToList();
        var portErr = new Label { Location = new Point(0, y + 6), Size = new Size(470, 40), ForeColor = Color.Firebrick };
        portPanel.Controls.Add(portErr);
        int[] values = ports.Select(p => p.Value).ToArray();

        var sumLbl = WizardForm.Para(""); var sum = new Panel(); sum.Controls.Add(sumLbl);
        var log = WizardForm.LogBox(); var inst = new Panel(); inst.Controls.Add(log);
        var doneLbl = WizardForm.Para(""); var done = new Panel(); done.Controls.Add(doneLbl);
        var openBtn = new Button { Text = "Webes beállítás megnyitása", Width = 220, Height = 32, Dock = DockStyle.Top, Visible = false };
        done.Controls.Add(openBtn); openBtn.BringToFront(); doneLbl.BringToFront();
        openBtn.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(webUrl(values)) { UseShellExecute = true }); } catch { } };

        var pages = new List<WizardPage>
        {
            new() { Title = (upgrade ? "Frissítés: " : "Üdvözöl a telepítő: ") + product, Subtitle = "Rendszergazdai jog megvan.", Body = welcome },
            new() { Title = "Hálózati beállítások", Subtitle = "Ezeken a portokon kommunikál a program. A tűzfalat a telepítő állítja be (csak belső hálózatra).",
                    Body = portPanel,
                    OnNext = () =>
                    {
                        var vals = new int[boxes.Count];
                        for (int i = 0; i < boxes.Count; i++)
                            if (!int.TryParse(boxes[i].Text.Trim(), out vals[i]) || vals[i] < 1 || vals[i] > 65535)
                            { portErr.Text = $"Érvénytelen port: {ports[i].Label}"; return Task.FromResult(false); }
                        if (vals.Distinct().Count() != vals.Length) { portErr.Text = "A portok nem lehetnek azonosak."; return Task.FromResult(false); }
                        portErr.Text = ""; values = vals;
                        sumLbl.Text = $"Program:        {product} {version}\nSzolgáltatás:  {serviceName} (automatikus, LocalSystem)\n\n" +
                                      string.Join("\n", ports.Select((p, i) => $"{p.Label}: {vals[i]}")) +
                                      "\n\n" + (upgrade ? "A futó szolgáltatás leáll, lecserélődik és újraindul." : "A telepítés után a webes felületen hozod létre az admin fiókot.");
                        return Task.FromResult(true);
                    } },
            new() { Title = "Összegzés", Subtitle = "Ellenőrizd, majd kattints a Telepítés gombra.", Body = sum, NextText = "Telepítés" },
            new() { Title = "Telepítés folyamatban…", Subtitle = "", Body = inst, CanGoBack = false,
                    OnEnter = async () =>
                    {
                        await WizardForm.RunWithLog(log, () => { save(values); install(); });
                        var ok = IsRunning(serviceName);
                        doneLbl.Text = ok
                            ? $"A(z) {product} telepítve, és szolgáltatásként fut.\n\nKövetkező lépés: nyisd meg a webes felületet ({webUrl(values)}), " +
                              (upgrade ? "és jelentkezz be." : "és hozd létre az admin fiókot.") + (extraDone.Length > 0 ? "\n\n" + extraDone : "")
                            : "A telepítés nem fejeződött be sikeresen – a részletek az előző lépés naplójában voltak.\n" +
                              "Próbáld meg rendszergazdai parancssorból:  <program>.exe diag";
                        openBtn.Visible = ok;
                    } },
            new() { Title = "Kész", Body = done, IsLast = true, CanGoBack = false },
        };
        Application.Run(new WizardForm(product, version, pages));
    }

    private static bool Exists(string name)
    {
        try { return ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private static bool IsRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            return true;
        }
        catch { return false; }
    }

    /// <summary>WinForms STA szalat igenyel; a top-level Main MTA, ezert kulon szalon fut.</summary>
    public static void OnSta(Action a)
    {
        var t = new Thread(() => a());
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
    }
}
#endif
