using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Windows Update a Windows sajat frissito feluleten at (Windows Update Agent API, COM).
/// Ugyanazt a forrast hasznalja, mint a Windows: ha a gepet WSUS vagy Intune szabalyozza,
/// annak a jovahagyott frissiteseit latja es telepiti - nem kerulj meg. Ujrainditast SOHA
/// nem vegez magatol; azt kulon, a rendszergazda dontesere kiadott feladat vegzi.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinUpdates
{
    private const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

    public static (List<WinUpdate> List, string Error) Scan()
    {
        var list = new List<WinUpdate>();
        try
        {
            dynamic session = NewSession();
            dynamic result = session.CreateUpdateSearcher().Search(Criteria);
            int n = result.Updates.Count;
            for (int i = 0; i < n; i++) list.Add(Describe(result.Updates.Item(i)));
            return (list, "");
        }
        catch (COMException ex) { return (list, Explain(ex)); }
        catch (Exception ex) { return (list, ex.Message); }
    }

    /// <summary>Telepites: ids == null -> minden elerheto frissites.</summary>
    public static (bool Ok, string Output, bool RebootRequired) Install(ICollection<string>? ids, Action<string> log)
    {
        try
        {
            dynamic session = NewSession();
            dynamic result = session.CreateUpdateSearcher().Search(Criteria);
            dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            int n = result.Updates.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic u = result.Updates.Item(i);
                string id = u.Identity.UpdateID;
                if (ids is not null && !ids.Contains(id)) continue;
                if (!(bool)u.EulaAccepted) u.AcceptEula();
                coll.Add(u);
                log("  + " + (string)u.Title);
            }
            if ((int)coll.Count == 0) return (true, "Nincs telepítendő frissítés.", false);

            log($"Letöltés ({coll.Count} db)…");
            dynamic dl = session.CreateUpdateDownloader(); dl.Updates = coll; dl.Download();

            log("Telepítés…");
            dynamic inst = session.CreateUpdateInstaller(); inst.Updates = coll;
            dynamic res = inst.Install();
            int code = res.ResultCode;   // 2=sikeres, 3=reszben, 4=hibas, 5=megszakitva
            bool reboot = res.RebootRequired;
            int okCount = 0;
            for (int i = 0; i < (int)coll.Count; i++)
            {
                int rc = res.GetUpdateResult(i).ResultCode;
                if (rc == 2 || rc == 3) okCount++;
                else log("  HIBA: " + (string)coll.Item(i).Title + $" (kód {rc})");
            }
            var summary = $"{okCount}/{coll.Count} frissítés telepítve." + (reboot ? " ÚJRAINDÍTÁS SZÜKSÉGES (a rendszergazda döntésére)." : "");
            return (code == 2 || code == 3, summary, reboot);
        }
        catch (COMException ex) { return (false, Explain(ex), false); }
        catch (Exception ex) { return (false, ex.Message, false); }
    }

    /// <summary>Var-e a gep ujrainditasra (frissites vagy telepito utan).</summary>
    public static bool PendingReboot()
    {
        try
        {
            using var lm = Registry.LocalMachine;
            if (lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null) return true;
            if (lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null) return true;
            using var sm = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch { return false; }
    }

    /// <summary>Honnan frissit a gep (WSUS / Intune / Microsoft Update).</summary>
    public static string Source()
    {
        try
        {
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            using var au = wu?.OpenSubKey("AU");
            if (au?.GetValue("UseWUServer") is int use && use == 1 && wu?.GetValue("WUServer") is string srv && srv.Length > 0)
                return "WSUS: " + srv;
            using var mdm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update");
            if (mdm is not null && mdm.ValueCount > 0) return "Intune (MDM) házirend";
        }
        catch { }
        return "Microsoft Update";
    }

    private static dynamic NewSession()
    {
        dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        s.ClientApplicationID = "DevToolbox Agent";
        return s;
    }

    private static WinUpdate Describe(dynamic u)
    {
        string kb = "";
        try { if ((int)u.KBArticleIDs.Count > 0) kb = "KB" + (string)u.KBArticleIDs.Item(0); } catch { }
        string sev = "";
        try { sev = (string?)u.MsrcSeverity ?? ""; } catch { }
        bool reboot = false;
        try { reboot = (int)u.InstallationBehavior.RebootBehavior != 0; } catch { }
        double mb = 0;
        try { mb = Math.Round(Convert.ToDouble(u.MaxDownloadSize) / 1048576.0, 1); } catch { }
        return new WinUpdate { UpdateId = (string)u.Identity.UpdateID, Kb = kb, Title = (string)u.Title, Severity = sev, RebootRequired = reboot, SizeMb = mb };
    }

    private static string Explain(COMException ex) => (uint)ex.HResult switch
    {
        0x8024402C or 0x80072EE7 or 0x80072EFD => "A frissítési szerver nem érhető el (hálózat/proxy/WSUS).",
        0x8024002E => "A Windows Update házirend tiltja a hozzáférést.",
        0x80240024 => "Nincs telepítendő frissítés.",
        0x8024001E => "A művelet megszakadt (a szolgáltatás leállt).",
        _ => $"Windows Update hiba 0x{(uint)ex.HResult:X8}: {ex.Message}"
    };
}
