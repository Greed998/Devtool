using System.Management;
using System.Runtime.Versioning;

namespace DevToolbox;

/// <summary>
/// Egy WMI kapcsolat leirasa. Ha a Host ures vagy "." / "localhost", helyi gepet jelent.
/// Ugyanaz a lekerdezo kod fut helyben es tavoli gepen is.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiContext
{
    public string Host { get; init; } = "";
    public string User { get; init; } = "";
    public string Password { get; init; } = "";

    public static WmiContext Local => new();

    public bool IsRemote =>
        !string.IsNullOrWhiteSpace(Host) &&
        !Host.Equals(".", StringComparison.Ordinal) &&
        !Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public string DisplayName => IsRemote ? Host : Environment.MachineName + " (helyi)";

    public ManagementScope BuildScope(string ns)
    {
        if (!IsRemote) return new ManagementScope(ns);

        var path = $@"\\{Host}\{ns.TrimStart('\\')}";
        var opts = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrWhiteSpace(User))
        {
            opts.Username = User;
            opts.Password = Password;
        }

        return new ManagementScope(path, opts);
    }

    /// <summary>Kapcsolat ellenorzese - beszedes hibauzenettel.</summary>
    public (bool Ok, string Message) TestConnection()
    {
        try
        {
            var scope = BuildScope(@"root\CIMV2");
            scope.Connect();
            return (true, "A kapcsolat létrejött.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Hozzáférés megtagadva – hibás felhasználónév vagy jelszó, " +
                           "illetve a fióknak rendszergazdának kell lennie a célgépen.");
        }
        catch (ManagementException ex)
        {
            return (false, "WMI hiba: " + ex.Message);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            var hint = (uint)ex.HResult switch
            {
                0x800706BA => " – a gép nem érhető el (tűzfal blokkolja a WMI/DCOM portot, " +
                              "vagy a gép ki van kapcsolva).",
                0x80070005 => " – hozzáférés megtagadva (DCOM jogosultság).",
                _ => ""
            };
            return (false, "Kapcsolódási hiba: " + ex.Message + hint);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>WQL lekerdezes futtatasa. Hiba eseten ures listat ad vissza.</summary>
    public List<ManagementObject> Query(string wql, string ns = @"root\CIMV2")
    {
        var results = new List<ManagementObject>();
        try
        {
            var searcher = new ManagementObjectSearcher(BuildScope(ns), new ObjectQuery(wql));
            foreach (ManagementObject o in searcher.Get()) results.Add(o);
        }
        catch { /* a hianyzo osztaly / jogosultsag nem vegzetes */ }
        return results;
    }

    /// <summary>Ugyanaz, de a hibat is visszaadja (ott, ahol ez szamit).</summary>
    public (List<ManagementObject> Items, string? Error) QueryChecked(string wql, string ns = @"root\CIMV2")
    {
        var results = new List<ManagementObject>();
        try
        {
            var searcher = new ManagementObjectSearcher(BuildScope(ns), new ObjectQuery(wql));
            foreach (ManagementObject o in searcher.Get()) results.Add(o);
            return (results, null);
        }
        catch (Exception ex)
        {
            return (results, ex.Message);
        }
    }
}
