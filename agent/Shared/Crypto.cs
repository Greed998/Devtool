using System.Security.Cryptography;
using System.Text;

namespace DevToolbox.Fleet;

/// <summary>
/// A hitelesítés kriptográfiai magja.
///
/// A modell (a komoly RMM-ek és az Azure Arc mintájára):
///   1. A szerver egy beléptető-tokent (enrollment key) ad ki – 256 bites, valódi véletlen.
///   2. Az agent telepítéskor SAJÁT kulcspárt generál. A privát kulcs SOHA nem hagyja el a gépet.
///   3. Az agent a tokennel + a publikus kulcsával regisztrál a szervernél.
///   4. A szerver kiállít az agentnek egy azonosítót és eltárolja a publikus kulcsát.
///   5. Onnantól minden üzenetet az agent a privát kulcsával ÍR ALÁ; a szerver a tárolt
///      publikus kulccsal ellenőriz. Így a token kiszivárgása sem enged üzenetet hamisítani.
///
/// Miért nem „közös jelszó”: egy megosztott titok mindenhol ugyanaz – egy gép feltörése az
/// egész flottát elviszi, és a titkot minden üzenethez ismerni kell. A kulcspáros aláírásnál
/// a szerver soha nem lát privát kulcsot, és minden agent kompromittálása elszigetelt.
/// </summary>
public static class Crypto
{
    // Ed25519 volna ideális, de a .NET-ben hordozhatóan az ECDSA a biztos választás: gyors, széles körben
    // auditált, és a Windows CNG natívan támogatja. 1.4.5-től a heti kulcscsere P-384-es kulcsokat ad.
    private const int TokenBytes = 32;   // 256 bit

    // ---------------------------------------------------------------- beléptető token

    /// <summary>
    /// Új beléptető-token. 256 bit kriptográfiai véletlen, base64url kódolással.
    /// Ezt adja a felhasználó az agent telepítőjének. Kétféleképpen olvasható:
    /// emberi (csoportokba tördelve) és gépi (folytonos) formában.
    /// </summary>
    public static string NewEnrollmentToken()
    {
        var raw = RandomNumberGenerator.GetBytes(TokenBytes);
        return Base64Url(raw);
    }

    /// <summary>A token ellenőrzése: helyes hosszú, dekódolható base64url.</summary>
    public static bool IsWellFormedToken(string token)
    {
        try
        {
            var bytes = FromBase64Url(token.Trim());
            return bytes.Length == TokenBytes;
        }
        catch { return false; }
    }

    /// <summary>
    /// A token sosem kerül lemezre nyersen: a szerver csak a SHA-256 lenyomatát tárolja.
    /// Így a szerver adatbázisának kiszivárgása nem adja ki a tokent.
    /// </summary>
    public static string TokenFingerprint(string token)
    {
        var bytes = FromBase64Url(token.Trim());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    // ---------------------------------------------------------------- kulcspár

    public sealed record KeyPair(string PublicKeyPem, string PrivateKeyPem, string Fingerprint);

    /// <summary>
    /// Új kulcspár. Alapból ECDSA P-256 (minden régi programverzió érti); strong = ECDSA P-384 (192 bites
    /// biztonsági szint, SHA-384-gyel). Erős kulcsot csak olyan félnek adunk, amelyik tudja ellenőrizni
    /// (1.4.5-től) - lásd a heti kulcscserét. A privát rész PEM-ként, a hívó felelőssége védeni.
    /// </summary>
    public static KeyPair NewKeyPair(bool strong = false)
    {
        using var ec = ECDsa.Create(strong ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256);
        var pub = ec.ExportSubjectPublicKeyInfoPem();
        var priv = ec.ExportPkcs8PrivateKeyPem();
        return new KeyPair(pub, priv, PublicKeyFingerprint(pub));
    }

    /// <summary>
    /// A hash a kulcs méretéhez igazodik (a görbe biztonsági szintjéhez): P-256 → SHA-256, P-384 → SHA-384,
    /// P-521 → SHA-512. A P-256 kulcs aláírása így bitre ugyanaz, mint eddig (régi kliensekkel kompatibilis).
    /// </summary>
    private static HashAlgorithmName HashFor(ECDsa ec) =>
        ec.KeySize > 384 ? HashAlgorithmName.SHA512 : ec.KeySize > 256 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256;

    /// <summary>A kulcs mérete bitben (256 / 384 / 521); 0, ha nem olvasható.</summary>
    public static int KeyBits(string publicKeyPem)
    {
        try { using var ec = ECDsa.Create(); ec.ImportFromPem(publicKeyPem); return ec.KeySize; }
        catch { return 0; }
    }

    /// <summary>Emberi név a felülethez és a naplóhoz.</summary>
    public static string Algorithm(string publicKeyPem) => KeyBits(publicKeyPem) switch
    {
        256 => "ECDSA P-256 / SHA-256", 384 => "ECDSA P-384 / SHA-384", 521 => "ECDSA P-521 / SHA-512", 0 => "?", var b => $"ECDSA {b} bit"
    };

    /// <summary>A publikus kulcs rövid ujjlenyomata – az agent azonosításához, naplóhoz.</summary>
    public static string PublicKeyFingerprint(string publicKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(publicKeyPem);
        var spki = ec.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki))[..32];
    }

    // ---------------------------------------------------------------- aláírás

    /// <summary>Adat aláírása a privát kulccsal (ECDSA, a kulcsmérethez illő hash-sel).</summary>
    public static string Sign(string privateKeyPem, byte[] data)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        var sig = ec.SignData(data, HashFor(ec));
        return Convert.ToBase64String(sig);
    }

    public static string Sign(string privateKeyPem, string data) =>
        Sign(privateKeyPem, Encoding.UTF8.GetBytes(data));

    /// <summary>Aláírás ellenőrzése a publikus kulccsal.</summary>
    public static bool Verify(string publicKeyPem, byte[] data, string signatureBase64)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicKeyPem);
            var sig = Convert.FromBase64String(signatureBase64);
            return ec.VerifyData(data, sig, HashFor(ec));
        }
        catch { return false; }
    }

    public static bool Verify(string publicKeyPem, string data, string signatureBase64) =>
        Verify(publicKeyPem, Encoding.UTF8.GetBytes(data), signatureBase64);

    // ---------------------------------------------------------------- ismétlés-védelem

    /// <summary>
    /// Minden kérés kap egy időbélyeget és egy egyszer használatos véletlent (nonce).
    /// A szerver elutasítja a túl régi vagy már látott kéréseket, így egy lehallgatott
    /// üzenet nem játszható vissza.
    /// </summary>
    public static string NewNonce() => Base64Url(RandomNumberGenerator.GetBytes(16));

    // ---------------------------------------------------------------- token védett tárolása

    /// <summary>
    /// A szerver→agent hitelesítéshez az agent a szerver publikus kulcsát is ellenőrzi
    /// (kölcsönös hitelesítés). Ehhez a szerver ujjlenyomatát az agent telepítéskor kapja meg,
    /// és minden válaszban ellenőrzi – így hamis szerver nem tud parancsot adni.
    /// </summary>
    public static bool FingerprintMatches(string expected, string publicKeyPem) =>
        string.Equals(expected, PublicKeyFingerprint(publicKeyPem),
            StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- base64url

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
