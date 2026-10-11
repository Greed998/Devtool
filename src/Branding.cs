using System.Reflection;
using System.Runtime.Versioning;

namespace DevToolbox;

/// <summary>
/// A kiado arculati elemei. A logo beagyazott eroforraskent utazik az exe-ben,
/// igy nincs kulso fajlfugges.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Branding
{
    private static Image? _logo;

    /// <summary>A pajzs embléma, átlátszó háttérrel. Null, ha nem tölthető be.</summary>
    public static Image? Logo
    {
        get
        {
            if (_logo is not null) return _logo;
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("DevToolbox.logo.png");
                if (stream is null) return null;
                _logo = Image.FromStream(stream);
            }
            catch { return null; }
            return _logo;
        }
    }

    /// <summary>Arányos méretre igazított másolat, jó minőségű átméretezéssel.</summary>
    public static Image? LogoAt(int size)
    {
        var src = Logo;
        if (src is null) return null;

        try
        {
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawImage(src, 0, 0, size, size);
            return bmp;
        }
        catch { return null; }
    }
}
