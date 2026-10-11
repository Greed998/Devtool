using System.Diagnostics;
using Microsoft.Win32;

namespace DevToolbox;

/// <summary>
/// A program sajat magat telepiti a felhasznaloi profilba (nem kell rendszergazda jog).
/// Egyetlen exe-rol van szo, ezert a "telepites" = masolas + parancsikonok + uninstall bejegyzes.
/// </summary>
public static class Installer
{
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppPaths.AppName;

    public static bool IsInstalled()
    {
        try { return File.Exists(AppPaths.InstalledExePath); }
        catch { return false; }
    }

    /// <summary>Telepites. Visszaadja a telepitett exe utvonalat.</summary>
    public static string Install(bool desktopShortcut, bool startMenuShortcut, bool runAsAdmin = true)
    {
        Directory.CreateDirectory(AppPaths.InstallDir);

        var target = AppPaths.InstalledExePath;

        // Ha eppen a cel exe-t futtatjuk, nincs mit masolni.
        if (!string.Equals(Path.GetFullPath(AppPaths.ExePath), Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(AppPaths.ExePath, target, overwrite: true);
        }

        // Az esetleg ottfelejtett portable marker torlese a telepitesi mappabol.
        var strayMarker = Path.Combine(AppPaths.InstallDir, AppPaths.PortableMarkerName);
        if (File.Exists(strayMarker)) TryDelete(strayMarker);

        if (startMenuShortcut)
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            CreateShortcut(Path.Combine(programs, AppPaths.DisplayName + ".lnk"), target, runAsAdmin);
        }

        if (desktopShortcut)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktop, AppPaths.DisplayName + ".lnk"), target, runAsAdmin);
        }

        WriteUninstallEntry(target);
        return target;
    }

    public static void Uninstall(bool removeData)
    {
        // Parancsikonok
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppPaths.DisplayName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppPaths.DisplayName + ".lnk"));

        // Registry
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); }
        catch { /* nem kritikus */ }

        // Adatok
        if (removeData)
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppName);
            try { if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true); }
            catch { /* nem kritikus */ }
        }

        // A futo exe nem torolheto, de athelyezheto - igy a telepitesi mappa azonnal torolheto.
        ScheduleSelfDelete(AppPaths.InstallDir);
    }

    /// <summary>
    /// Torles kulso parancssor (cmd) nelkul: a futo exe azonos koteten athelyezheto, ezert a Temp mappaba
    /// kerul (emelt joggal ujrainditaskor torlodik), a telepitesi mappa pedig azonnal torolheto.
    /// </summary>
    private static void ScheduleSelfDelete(string dir)
    {
        try
        {
            var self = Environment.ProcessPath ?? "";
            if (self.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(self))
            {
                var parked = Path.Combine(Path.GetTempPath(), $"DevToolbox-eltavolitva-{Guid.NewGuid():N}.exe");
                File.Move(self, parked);
                MoveFileExW(parked, null, MOVEFILE_DELAY_UNTIL_REBOOT);   // csak rendszergazdakent sikerul - nem baj
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* nem kritikus: ami maradt, kézzel törölhető */ }
    }

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string? newName, int flags);

    private static void WriteUninstallEntry(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            if (key is null) return;
            key.SetValue("DisplayName", AppPaths.DisplayName);
            key.SetValue("DisplayVersion", AppPaths.Version);
            key.SetValue("Publisher", AppPaths.Publisher);
            key.SetValue("DisplayIcon", exePath);
            key.SetValue("InstallLocation", AppPaths.InstallDir);
            key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exePath}\" --uninstall --quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("URLInfoAbout", AppPaths.Website);
            key.SetValue("HelpLink", "mailto:" + AppPaths.SupportMail);
            key.SetValue("EstimatedSize", GetSizeKb(exePath), RegistryValueKind.DWord);
        }
        catch { /* nem kritikus */ }
    }

    private static int GetSizeKb(string path)
    {
        try { return (int)(new FileInfo(path).Length / 1024); }
        catch { return 0; }
    }

    /// <summary>.lnk letrehozasa kesoi kotesu COM-mal (WScript.Shell), kulso fuggoseg nelkul.</summary>
    private static void CreateShortcut(string lnkPath, string targetPath, bool runAsAdmin)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return;
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = targetPath;
            sc.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
            sc.IconLocation = targetPath + ",0";
            sc.Description = AppPaths.DisplayName + " – " + AppPaths.Tagline;
            sc.Save();

            if (runAsAdmin) SetShortcutRunAsAdmin(lnkPath);
        }
        catch { /* nem kritikus */ }
    }

    /// <summary>
    /// A "Futtatas rendszergazdakent" jelolonegyzet bekapcsolasa a parancsikonban.
    /// A WScript.Shell ezt nem tudja, ezert a .lnk fejlecet modositjuk:
    /// a LinkFlags mezo (0x14 offset, 4 bajt) 13. bitje a RunAsUser jelzo.
    /// </summary>
    private static void SetShortcutRunAsAdmin(string lnkPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 0x16) return;
            bytes[0x15] |= 0x20;                 // LinkFlags bit 13
            File.WriteAllBytes(lnkPath, bytes);
        }
        catch { /* nem kritikus */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
