using System.Text;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Kesz agent-telepito szkript generalasa a webfeluletrol. A token, a szerver
/// ujjlenyomata es a cime mar be van irva - az uj gepen csak le kell futtatni.
///
/// Biztonsagi megjegyzes: a generalt szkript TARTALMAZZA a belepteto tokent, ezert
/// ugy kell kezelni, mint magat a tokent - biztonsagos csatornan juttasd a gepre, es
/// futtatas utan torold. A token ugyis egyszer hasznalatos jellegu: ha kiszivarog,
/// csak uj agent regisztralasara jo, es a szerveren letilthato.
/// </summary>
public static class InstallerGen
{
    /// <summary>
    /// PowerShell telepito. Ez a javasolt: ellenorzi az admin jogot, letolti az agent
    /// exét a szerverrol (ha kell), es lefuttatja a telepitest.
    /// </summary>
    public static string PowerShell(string serverBaseUrl, int agentPort, string token,
        string fingerprint, string label)
    {
        // A szerver cimebol a host resz, hogy az agent-portos URL-t osszerakjuk.
        var host = HostOf(serverBaseUrl);
        var agentUrl = $"http://{host}:{agentPort}";
        var downloadUrl = $"{serverBaseUrl.TrimEnd('/')}/web/agent-exe";

        var sb = new StringBuilder();
        sb.Append(
            "#Requires -RunAsAdministrator\n" +
            "<#\n" +
            "  DevToolbox Agent telepito – automatikusan generalt\n" +
            $"  Cimke: {Sanitize(label)}\n" +
            "\n" +
            "  Ez a szkript a belepteto tokent tartalmazza. Kezeld bizalmasan,\n" +
            "  es futtatas utan torold. Rendszergazda PowerShellbol futtasd:\n" +
            "      powershell -ExecutionPolicy Bypass -File .\\telepit-agent.ps1\n" +
            "#>\n\n" +
            "$ErrorActionPreference = 'Stop'\n\n" +
            $"$Server      = '{agentUrl}'\n" +
            $"$Token       = '{token}'\n" +
            $"$Fingerprint = '{fingerprint}'\n" +
            $"$DownloadUrl = '{downloadUrl}'\n" +
            "# Mindig friss letoltes ideiglenes helyre - a telepito maga masolja a vegleges helyre\n" +
            "# (Program Files\\DevToolbox Agent). Igy frissiteskor sem a regi exe fut.\n" +
            "$ExePath     = Join-Path $env:TEMP ('DevToolboxAgent-setup-' + [guid]::NewGuid().ToString('N') + '.exe')\n\n" +
            "Write-Host 'DevToolbox Agent telepitese...' -ForegroundColor Cyan\n\n" +
            "# --- 1. Az agent exe letoltese a szerverrol\n" +
            "try {\n" +
            "    Invoke-WebRequest -Uri $DownloadUrl -OutFile $ExePath -UseBasicParsing\n" +
            "} catch {\n" +
            "    Write-Host \"Nem sikerult letolteni: $($_.Exception.Message)\" -ForegroundColor Red\n" +
            "    Write-Host 'Hasznald inkabb a ZIP-es telepitot a webfeluletrol.' -ForegroundColor Yellow\n" +
            "    exit 1\n" +
            "}\n\n" +
            "# --- 2. Telepites (a token es az ujjlenyomat mar be van irva)\n" +
            "Write-Host 'Szolgaltatas letrehozasa es regisztracio...' -ForegroundColor Cyan\n" +
            "& $ExePath install --server $Server --token $Token --fingerprint $Fingerprint\n" +
            "Remove-Item $ExePath -Force -ErrorAction SilentlyContinue\n\n" +
            "if ($LASTEXITCODE -eq 0) {\n" +
            "    Write-Host ''\n" +
            "    Write-Host 'Kesz. Az agent szolgaltataskent fut es regisztral a szervernel.' -ForegroundColor Green\n" +
            "    Write-Host 'Biztonsag: torold ezt a szkriptet, mert tokent tartalmaz.' -ForegroundColor Yellow\n" +
            "} else {\n" +
            "    Write-Host 'A telepites hibaba utkozott. Nezd meg a fenti uzeneteket.' -ForegroundColor Red\n" +
            "}\n");

        return sb.ToString();
    }

    /// <summary>Az agent altal hasznalt cim (a webes kerés hosztja + agent-port, mindig http).</summary>
    public static string AgentUrl(string serverBaseUrl, int agentPort)
    {
        return $"http://{HostOf(serverBaseUrl)}:{agentPort}";
    }

    /// <summary>Egyszeru batch valtozat, ha az exe mar a gep mellett van.</summary>
    public static string Batch(string serverBaseUrl, int agentPort, string token, string fingerprint, string label) =>
        BatchFor(AgentUrl(serverBaseUrl, agentPort), token, fingerprint, label);

    /// <summary>Ugyanaz, kesz agent-cimmel (a Kozpont a telephely belso cimet kapja meg).</summary>
    public static string BatchFor(string agentUrl, string token, string fingerprint, string label)
    {
        return
            "@echo off\r\n" +
            "chcp 65001>nul\r\n" +
            "rem DevToolbox Agent telepito - automatikusan generalt\r\n" +
            $"rem Cimke: {Sanitize(label)}\r\n" +
            "rem A DevToolboxAgent.exe legyen ugyanebben a mappaban.\r\n" +
            "rem Rendszergazdakent futtasd.\r\n\r\n" +
            "net session >nul 2>&1 || (echo Rendszergazdai jog szukseges. & pause & exit /b 1)\r\n\r\n" +
            "if not exist \"%~dp0DevToolboxAgent.exe\" (\r\n" +
            "    echo Hianyzik a DevToolboxAgent.exe ebbol a mappabol.\r\n" +
            "    pause & exit /b 1\r\n" +
            ")\r\n\r\n" +
            $"\"%~dp0DevToolboxAgent.exe\" install --server {agentUrl} --token {token} --fingerprint {fingerprint}\r\n\r\n" +
            "echo.\r\n" +
            "echo Kesz. Biztonsag: torold ezt a fajlt, mert tokent tartalmaz.\r\n" +
            "pause\r\n";
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch
        {
            // Ha nem teljes URL, probaljuk kihamozni.
            var s = url.Replace("https://", "").Replace("http://", "");
            var slash = s.IndexOf('/');
            if (slash > 0) s = s[..slash];
            var colon = s.IndexOf(':');
            if (colon > 0) s = s[..colon];
            return s.Length > 0 ? s : "SZERVER";
        }
    }

    private static string Sanitize(string s) =>
        new string(s.Where(c => c != '\r' && c != '\n' && c != '#').ToArray());
}
