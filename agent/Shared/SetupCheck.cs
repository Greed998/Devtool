using System.Net.Http.Json;

namespace DevToolbox.Fleet;

/// <summary>
/// A telepitovarazslo "Kapcsolat tesztelese" gombja mogotti logika - feluletfuggetlen,
/// hogy tesztelheto legyen. Belulrol kifele halad: cim -> eleres -> jo szolgaltatas -> kulcs.
/// </summary>
public static class SetupCheck
{
    public sealed class ServerKeyInfo
    {
        public string PublicKeyPem { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public List<KeyLink>? KeyChain { get; set; }
    }

    public static async Task<(bool Ok, string Message)> TestServerAsync(string serverUrl, string fingerprint, CancellationToken ct = default)
    {
        var baseUrl = (serverUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return (false, "A szerver címe nem érvényes. Példa: http://192.168.0.10:8443");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // 1. elérés + jó szolgáltatás
        try
        {
            var ping = await http.GetStringAsync(baseUrl + "/api/ping", ct);
            if (!ping.Contains("devtoolbox-fleet"))
                return (false, "Ezen a címen más szolgáltatás válaszol, nem a Fleet Server. Ellenőrizd a portot (alapból 8443).");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException
                                              || (ex.InnerException?.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return (false, "TLS-hiba: a szerver HTTP-t beszél, a cím https:// – írd át http://-re.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"A szerver nem érhető el ({uri.Host}:{uri.Port}). Fut a Fleet Server? Nyitva a port a tűzfalon? – {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, $"Időtúllépés – a(z) {uri.Host}:{uri.Port} nem válaszol. Tűzfal vagy rossz cím?");
        }

        // 2. kulcs + ujjlenyomat
        if (fingerprint.Trim().Length == 0)
            return (false, "Hiányzik a szerver ujjlenyomata – enélkül nem lehet biztonságosan kapcsolódni.");
        try
        {
            using var resp = await http.GetAsync(baseUrl + "/api/server-key", ct);
            if ((int)resp.StatusCode == 404)
                return (true, "A szerver elérhető. (Régebbi szerver: az ujjlenyomatot a regisztrációkor ellenőrzi.)");
            var key = await resp.Content.ReadFromJsonAsync<ServerKeyInfo>(ProtocolJson.Options, ct);
            if (key is null || key.PublicKeyPem.Length == 0) return (false, "A szerver nem adott kulcsot.");
            if (!KeyStore.FingerprintReaches(fingerprint.Trim(), key.KeyChain, key.PublicKeyPem))
                return (false, "BIZTONSÁGI HIBA: a szerver kulcsa nem egyezik a megadott ujjlenyomattal. Rossz szerver, vagy elírás?");
            return (true, "A szerver elérhető, és az ujjlenyomat egyezik.");
        }
        catch (Exception ex)
        {
            return (false, "A kulcs lekérése nem sikerült: " + ex.Message);
        }
    }
}
