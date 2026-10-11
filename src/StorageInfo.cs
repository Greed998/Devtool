namespace DevToolbox;

/// <summary>Egy meghajto kiolvasott allapota. Nem tartalmaz elo rendszerhivast.</summary>
public sealed record DriveSnapshot(
    string Name, string Label, string Format, DriveType Type,
    long Total, long Free, string RootPath);

public static class StorageInfo
{
    /// <summary>
    /// A DriveInfo tulajdonsagai (IsReady, TotalSize, VolumeLabel...) blokkolo rendszerhivasok:
    /// lecsatolt halozati meghajtonal vagy ures optikai meghajtonal masodpercekre megallhatnak.
    /// Ezert EGYSZER olvassuk ki oket, hattérszalon, es mindenki a masolattal dolgozik.
    /// </summary>
    public static List<DriveSnapshot> ReadDrives()
    {
        var list = new List<DriveSnapshot>();

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return list; }

        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady) continue;
                list.Add(new DriveSnapshot(
                    d.Name,
                    SafeGet(() => d.VolumeLabel, ""),
                    SafeGet(() => d.DriveFormat, ""),
                    d.DriveType,
                    SafeGet(() => d.TotalSize, 0L),
                    SafeGet(() => d.TotalFreeSpace, 0L),
                    d.RootDirectory.FullName));
            }
            catch { /* nem elerheto meghajto - kihagyjuk */ }
        }

        return list;
    }

    private static T SafeGet<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
