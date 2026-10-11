using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet;

namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Az agent lelke. Szolgaltataskent fut a felugyelt gepen, LocalSystem-kent.
///
/// Miert nem kell a felhasznalonak adminnak lennie: az agent maga rendszer-jogu,
/// ezert a winget-frissitest O vegzi el, nem a bejelentkezett felhasznalo. A felhasznalo
/// akar kijelentkezhet - az agent akkor is jelentkezik es frissit.
///
/// A privat kulcs a ProgramData vedett mappajaban van; a szolgaltatas LocalSystem-kent
/// fér hozza, normal felhasznalo nem.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentCore
{
    // A FleetServertol kapott metrika-konfig (a checkin valaszaban jon le).
    private volatile MetricConfig? _metricConfig;

    /// <summary>Csak szolgaltataskent frissiti magat (interaktiv once/run modban nem).</summary>
    public static bool IsService;

    // Telepitett programok (orankent) es Windows Update (6 orankent, hatterben) gyorsitotara.
    private List<InstalledApp> _installed = new();
    private DateTime _installedAt = DateTime.MinValue;
    private List<AppUpdate> _updates = new();
    private DateTime _updatesAt = DateTime.MinValue;
    private List<WinUpdate> _wu = new();
    private DateTime? _wuAt;
    private string _wuError = "";
    private int _wuScanning;
    private string _lastOfferSha = "";
    private int _svcSentHash, _svcPendingHash;
    private bool _terminalEnabled;   // 1.5.9: a szerver aláírt válaszában engedélyezte-e a webes admin terminált erre a gépre
    private DateTime _svcSentAt = DateTime.MinValue;
    // Driverek és firmware (1.5.6): óránként olvasva, változáskor (és naponta) küldve.
    private List<ComponentInfo> _comp = new();
    private DateTime _compAt = DateTime.MinValue, _compSentAt = DateTime.MinValue;
    private int _compSentHash, _compPendingHash;
    private DateTime _lastOfferTry = DateTime.MinValue;

    public const string AgentVersion = "1.5.12";

    private readonly AgentConfig _config;
    private readonly HttpClient _http;

    public AgentCore(AgentConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public event Action<string>? Log;
    private void L(string msg) => Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {msg}");
    /// <summary>Naplózás kívülről (a tálca-kiszolgáló is az agent naplójába ír).</summary>
    public void LogLine(string msg) => L(msg);

    // ================================================================ ébresztés, gyors szolgáltatás-figyelés, USB-napló (1.5.6)

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _lastWake = DateTime.MinValue;

    /// <summary>Azonnali jelentkezés kérése (pl. leállt egy figyelt szolgáltatás) - legfeljebb 5 mp-enként.</summary>
    public void Wake()
    {
        if (DateTime.UtcNow - _lastWake < TimeSpan.FromSeconds(5)) return;
        _lastWake = DateTime.UtcNow;
        _wake.TrySetResult();
    }

    /// <summary>Várakozás a következő jelentkezésig; ébresztésre (Wake) előbb visszatér.</summary>
    public async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        var w = _wake.Task;
        await Task.WhenAny(Task.Delay(delay, token), w);
        if (w.IsCompleted) _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        token.ThrowIfCancellationRequested();
    }

    private readonly object _watchLock = new();
    private readonly Dictionary<string, ServiceState> _watch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.ServiceProcess.ServiceController> _watchSc = new(StringComparer.OrdinalIgnoreCase);
    private readonly AgentUsb _usb = new();

    /// <summary>
    /// A figyelt szolgáltatások (MetricConfig.Services) gyors ellenőrzése WatchSeconds-onként (alap 5 mp, a felhasználó
    /// kérése). Állapotváltáskor azonnali jelentkezés - a szerver ebből riaszt („Szolgáltatás leállt”). Ugyanez a kör nézi a
    /// külső meghajtókat (USB-napló), ha a szerver kéri.
    /// </summary>
    public void StartWatchers(CancellationToken token) => _ = Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            var mc = _metricConfig;
            var secs = mc is { StatusVersion: >= 4 } ? mc.WatchSeconds : 0;
            try { _usb.Tick(mc is { StatusVersion: >= 4, UsbAudit: true }); } catch (Exception ex) { L("USB-figyelés: " + ex.Message); }
            if (secs <= 0 || mc!.Services.Count == 0)
            {
                lock (_watchLock) { _watch.Clear(); foreach (var sc in _watchSc.Values) sc.Dispose(); _watchSc.Clear(); }
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch { return; }
                continue;
            }
            var changed = false;
            lock (_watchLock)
            {
                var names = mc.Services.Where(n => n.Length is > 0 and <= 100).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
                foreach (var gone in _watch.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
                { _watch.Remove(gone); if (_watchSc.Remove(gone, out var old)) old.Dispose(); }
                foreach (var n in names)
                {
                    bool running = false, missing = false; string status = "", display = n;
                    try
                    {
                        if (!_watchSc.TryGetValue(n, out var sc)) _watchSc[n] = sc = new System.ServiceProcess.ServiceController(n);
                        sc.Refresh();
                        var st = sc.Status;
                        running = st == System.ServiceProcess.ServiceControllerStatus.Running;
                        status = st.ToString(); display = sc.DisplayName;
                    }
                    catch (InvalidOperationException) { missing = true; status = "nincs ilyen szolgáltatás"; if (_watchSc.Remove(n, out var bad)) bad.Dispose(); }
                    catch { continue; }
                    if (!_watch.TryGetValue(n, out var prev))
                        _watch[n] = new ServiceState { Name = n, DisplayName = display, Running = running, Missing = missing, Status = status, SinceUtc = DateTime.UtcNow };
                    else if (prev.Running != running || prev.Missing != missing)
                    {
                        prev.Running = running; prev.Missing = missing; prev.Status = status; prev.DisplayName = display; prev.SinceUtc = DateTime.UtcNow;
                        changed = true;
                        L($"Szolgáltatás: {display} ({n}) → {(missing ? "nincs" : running ? "fut" : status)}");
                    }
                }
            }
            if (changed) Wake();
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(secs, 2, 300)), token); } catch { return; }
        }
    });

    // ================================================================ regisztráció

    /// <summary>
    /// Elso inditaskor: sajat kulcspar generalasa (ha meg nincs) es regisztracio a szervernel.
    /// Visszaadja, sikerult-e. Idempotens: ugyanazzal a kulccsal ujra hivhato.
    /// </summary>
    public async Task<bool> EnsureEnrolledAsync(CancellationToken token)
    {
        if (_config.IsEnrolled)
        {
            L($"Már regisztrálva (agent: {_config.AgentId[..8]}).");
            return true;
        }

        // Kulcspar - a privat kulcs SOHA nem megy a halozatra.
        if (string.IsNullOrEmpty(_config.PrivateKeyPem))
        {
            L("Kulcspár generálása…");
            // Regisztrációhoz P-256 (a régi szerver is ellenőrzi); az 1.4.5+ szerver az első jelentkezés után erősebbre cserélteti.
            var keys = Crypto.NewKeyPair();
            _config.PrivateKeyPem = keys.PrivateKeyPem;
            _config.PublicKeyPem = keys.PublicKeyPem;
            _config.Fingerprint = keys.Fingerprint;
            _config.KeyCreatedUtc = DateTime.UtcNow;
            _config.Save();
        }

        if (string.IsNullOrWhiteSpace(_config.EnrollmentToken))
        {
            L("HIBA: nincs beléptető-token. A telepítéskor kell megadni.");
            return false;
        }

        var req = new EnrollRequest
        {
            Token = _config.EnrollmentToken,
            PublicKeyPem = _config.PublicKeyPem,
            MachineName = Environment.MachineName,
            OperatingSystem = GetOsCaption(),
            AgentVersion = AgentVersion,
            Fingerprint = _config.Fingerprint
        };

        try
        {
            L($"Regisztráció: {_config.ServerUrl} …");
            var resp = await _http.PostAsJsonAsync(
                Url("/api/enroll"), req, ProtocolJson.Options, token);

            var result = await resp.Content.ReadFromJsonAsync<EnrollResponse>(
                ProtocolJson.Options, token);

            if (result is null || !result.Success)
            {
                L("A regisztráció elutasítva: " + (result?.Message ?? "ismeretlen hiba"));
                return false;
            }

            // A szerver publikus kulcsanak ellenorzese a telepiteskor kapott ujjlenyommal.
            // Igy hamis szerver nem tud ervenyes agent-azonositot adni.
            if (_config.ServerFingerprint.Length > 0 &&
                !KeyStore.FingerprintReaches(_config.ServerFingerprint, result.KeyChain, result.ServerPublicKeyPem))
            {
                L("BIZTONSÁGI HIBA: a szerver kulcs-ujjlenyomata nem egyezik a várttal. " +
                  "Lehetséges közbeékelődés – a regisztráció megszakítva.");
                return false;
            }

            _config.AgentId = result.AgentId;
            _config.ServerPublicKeyPem = result.ServerPublicKeyPem;
            // A tokent a sikeres regisztracio utan toroljuk - tobbe nincs ra szukseg.
            _config.EnrollmentToken = "";
            _config.Save();

            L($"Sikeres regisztráció. Agent: {result.AgentId[..8]}");
            return true;
        }
        catch (Exception ex)
        {
            L("A regisztráció nem sikerült: " + ex.Message);
            return false;
        }
    }

    // ================================================================ jelentkezés

    /// <summary>Egy jelentkezési kör: állapot küldése + feladatok lekérése és végrehajtása.</summary>
    public async Task<int> CheckinAsync(CancellationToken token)
    {
        if (!_config.IsEnrolled)
        {
            if (!await EnsureEnrolledAsync(token)) return 300;
        }

        AgentStatus status;
        try { status = await CollectStatusAsync(token); }
        catch (Exception ex) { L("Állapotgyűjtés hiba: " + ex.Message); status = new AgentStatus(); }

        // A jelentkezesek kozott vett mintak - csak ha a szerver fogadja (kulonben az alairas nem egyezne).
        int sentSamples = 0;
        if (_metricConfig?.AcceptsSamples == true)
            lock (_sampleLock)
                if (_samples.Count > 0) { status.MetricSamples = _samples.ToList(); sentSamples = _samples.Count; }
        // 1.5.6: a gyorsan figyelt szolgáltatások állapota és az USB-napló - csak ha a szerver ismeri (StatusVersion ≥ 4).
        List<UsbEvent>? sentUsb = null;
        if (_metricConfig is { StatusVersion: >= 4 } mc4)
        {
            if (mc4.WatchSeconds > 0) lock (_watchLock) if (_watch.Count > 0) status.ServiceStates = _watch.Values.Select(s => new ServiceState
                { Name = s.Name, DisplayName = s.DisplayName, Missing = s.Missing, Running = s.Running, Status = s.Status, SinceUtc = s.SinceUtc }).ToList();
            if (mc4.UsbAudit && _usb.Take(500) is { Count: > 0 } ue) status.UsbEvents = sentUsb = ue;
        }

        var req = new CheckinRequest
        {
            AgentId = _config.AgentId,
            Timestamp = DateTime.UtcNow.ToString("o"),
            Nonce = Crypto.NewNonce(),
            Status = status
        };
        req.Signature = Crypto.Sign(_config.PrivateKeyPem, req.CanonicalPayload());

        try
        {
            var resp = await _http.PostAsJsonAsync(Url("/api/checkin"), req, ProtocolJson.Options, token);
            var raw = await resp.Content.ReadFromJsonAsync<CheckinResponse>(ProtocolJson.Options, token);

            // A szerver valaszat CSAK alairt tartalombol hasznaljuk. Alairas nelkul vagy hamis
            // alairassal egyetlen feladatot sem hajtunk vegre - kozbeekelodo nem csempeszhet be semmit.
            var result = VerifyReply(raw, req.Nonce);

            if (result is null || !result.Ok)
            {
                if (sentUsb is not null) _usb.Requeue(sentUsb);   // az USB-napló nem vész el
                L("Jelentkezés elutasítva: " + (result?.Message ?? "ismeretlen"));
                // Ha az agentet visszavontak vagy ismeretlen, ujraregisztralunk.
                if (result?.Message?.Contains("regisztráció") == true)
                {
                    _config.AgentId = "";
                    _config.Save();
                }
                return 300;
            }

            // A FleetServertol kapott metrika-beallitas eltarolasa a kovetkezo gyujteshez.
            if (result.Metrics is not null) _metricConfig = result.Metrics;
            _terminalEnabled = result.Terminal;   // 1.5.9: a webes admin terminál engedélye (aláírt válaszból, gépenként)
            LastCheckinUtc = DateTime.UtcNow;
            // Az elküldött szolgáltatáslista a szervernél van - legközelebb csak változáskor küldjük.
            if (status.ServiceList is not null) { _svcSentHash = _svcPendingHash; _svcSentAt = DateTime.UtcNow; }
            if (status.Components is not null) { _compSentHash = _compPendingHash; _compSentAt = DateTime.UtcNow; }
            // Az elkuldott mintak a szervernel vannak - kivesszuk a pufferbol (a kozben vettek maradnak).
            if (sentSamples > 0) lock (_sampleLock) _samples.RemoveRange(0, Math.Min(sentSamples, _samples.Count));

            L($"Jelentkezés kész. {status.UpdatesAvailable} frissíthető alkalmazás. " +
              (result.Tasks.Count > 0 ? $"{result.Tasks.Count} feladat érkezett." : "Nincs feladat."));

            foreach (var task in result.Tasks)
            {
                token.ThrowIfCancellationRequested();
                await ExecuteTaskAsync(task, token);
            }

            if (result.AgentUpdate is { } offer) await TryUpdateAsync(offer, token);

            // Portváltás: a szerver új agent-portot jelzett (a régi egy ideig még figyel).
            if (result.AgentPort is int ap) await TrySwitchPortAsync(ap, token);

            // Kulcscsere a szerver szabálya szerint (alapból hetente), illetve azonnal, ha a szerver erősebbet fogad.
            if (result.KeyPolicy is { } kp && KeyRotationDue(kp)) await RotateKeyAsync(kp.Strong, token);

            return result.NextCheckinSeconds;
        }
        catch (Exception ex)
        {
            if (sentUsb is not null) _usb.Requeue(sentUsb);
            L("Jelentkezés hiba: " + ex.Message);
            return 300;
        }
    }

    // ================================================================ mintavétel

    // A jelentkezestol fuggetlen, SampleSeconds-onkenti meresek puffere. Ha a szerver sokaig nem
    // erheto el, a legregebbiek esnek ki - igy a memoria nem no korlatlanul (~1 nap percenkent).
    private readonly List<MetricSample> _samples = new();
    private readonly object _sampleLock = new();
    private const int MaxBufferedSamples = 1440;

    /// <summary>Hatterben, a szerver altal megadott suruseggel mer (alapbol percenkent).</summary>
    public void StartSampler(CancellationToken token) => _ = Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            var mc = _metricConfig;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(mc?.SampleSeconds ?? 60, 15, 3600)), token); }
            catch (OperationCanceledException) { break; }
            if (mc is null || !mc.AcceptsSamples) continue;   // regi szerver: csak a jelentkezeskori meres
            try
            {
                var ms = AgentMetrics.Collect(AgentMetrics.Config.From(mc, ServerHost), includeSlow: false);
                lock (_sampleLock)
                {
                    _samples.Add(new MetricSample { TimestampUtc = DateTime.UtcNow, Metrics = ms });
                    if (_samples.Count > MaxBufferedSamples) _samples.RemoveRange(0, _samples.Count - MaxBufferedSamples);
                }
            }
            catch (Exception ex) { L("Mintavétel hiba: " + ex.Message); }
        }
    }, token);

    private void StartWuScan()
    {
        if (Interlocked.Exchange(ref _wuScanning, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                var (list, err) = WinUpdates.Scan();
                _wu = list; _wuError = err; _wuAt = DateTime.UtcNow;
                L(err.Length > 0 ? "Windows Update keresés: " + err : $"Windows Update keresés: {list.Count} elérhető frissítés.");
            }
            catch (Exception ex) { _wuError = ex.Message; _wuAt = DateTime.UtcNow; }
            finally { Interlocked.Exchange(ref _wuScanning, 0); }
        });
    }

    /// <summary>Alairt frissitesi ajanlat kezelese. Ugyanazt a verziot legfeljebb orankent probalja.</summary>
    private async Task TryUpdateAsync(AgentUpdateOffer offer, CancellationToken token)
    {
        if (!IsService) { L($"Elérhető agent-frissítés: {offer.Version} (szolgáltatásként települ automatikusan)."); return; }
        if (offer.Sha256 == _lastOfferSha && DateTime.UtcNow - _lastOfferTry < TimeSpan.FromHours(1)) return;
        _lastOfferSha = offer.Sha256; _lastOfferTry = DateTime.UtcNow;

        L($"Agent-frissítés érhető el: {AgentVersion} -> {offer.Version}. Letöltés…");
        var (path, error) = await AgentUpdater.DownloadAndVerify(_http, Url("/api/agent-update"), offer, token);
        if (path is null) { L("Agent-frissítés elvetve: " + error); return; }

        L("A letöltött fájl lenyomata egyezik az aláírttal – csere indul.");
        try { AgentUpdater.Launch(path); }
        catch (Exception ex) { L("Az önfrissítő nem indult: " + ex.Message); }
    }

    /// <summary>
    /// A szerver valaszanak ellenorzese: alairas a tarolt szerverkulccsal (vagy kulcscsere
    /// eseten a lancon at ellenorzott uj kulccsal), a mi nonce-unk es azonositonk.
    /// Sikertelen ellenorzeskor feladat nelkuli valaszt adunk vissza.
    /// </summary>
    private CheckinResponse? VerifyReply(CheckinResponse? raw, string nonce)
    {
        if (raw is null) return null;

        if (string.IsNullOrEmpty(raw.SignedPayload) || string.IsNullOrEmpty(raw.Signature))
        {
            L("Figyelem: a szerver válasza nincs aláírva (régi FleetServer?) – feladatok kihagyva. Frissítsd a szervert.");
            return new CheckinResponse { Ok = raw.Ok, Message = raw.Message, NextCheckinSeconds = raw.NextCheckinSeconds };
        }

        if (!VerifyServerSignature(raw.SignedPayload, raw.Signature, raw.SignerPublicKeyPem, raw.KeyChain))
            return new CheckinResponse { Ok = false, Message = "aláírás-hiba", NextCheckinSeconds = 300 };

        var body = ProtocolJson.Deserialize<CheckinReplyBody>(raw.SignedPayload);
        if (body is null || body.RequestNonce != nonce || body.AgentId != _config.AgentId)
        {
            L("BIZTONSÁGI HIBA: a válasz nem ehhez a kéréshez tartozik (visszajátszás?) – elutasítva.");
            return new CheckinResponse { Ok = false, Message = "aláírás-hiba", NextCheckinSeconds = 300 };
        }

        return new CheckinResponse
        {
            Ok = body.Ok, Message = body.Message, Tasks = body.Tasks,
            NextCheckinSeconds = body.NextCheckinSeconds, Metrics = body.Metrics,
            AgentUpdate = body.AgentUpdate, KeyPolicy = body.KeyPolicy, AgentPort = body.AgentPort
        };
    }

    // ================================================================ portváltás

    private DateTime _lastPortTry = DateTime.MinValue;

    /// <summary>
    /// A szerver (aláírt válaszban) új agent-portot jelzett. Átállás előtt az új porton lekérjük a szerver kulcsát, és csak
    /// akkor váltunk, ha az a nálunk levő (vagy láncon át elérhető) szerverkulcs - így egy ott figyelő idegen szolgáltatásra
    /// nem állhatunk át. Sikertelen próbát 30 percig nem ismétlünk; addig a régi porton maradunk (az még figyel).
    /// </summary>
    private async Task TrySwitchPortAsync(int port, CancellationToken token)
    {
        if (port is < 1 or > 65535 || !Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out var cur) || cur.Port == port) return;
        if (DateTime.UtcNow - _lastPortTry < TimeSpan.FromMinutes(30)) return;
        _lastPortTry = DateTime.UtcNow;
        var next = new UriBuilder(cur) { Port = port }.Uri.ToString().TrimEnd('/');
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var key = await _http.GetFromJsonAsync<ServerKeyInfo>(next + "/api/server-key", ProtocolJson.Options, cts.Token);
            var known = _config.ServerPublicKeyPem;
            if (key is null || key.PublicKeyPem.Length == 0 || known.Length == 0 ||
                !(key.PublicKeyPem == known || KeyStore.ChainReaches(known, key.KeyChain, key.PublicKeyPem)))
            { L($"Portváltás ({cur.Port} → {port}): az új porton nem a mi szerverünk válaszolt – maradunk a régin."); return; }
            _config.ServerUrl = next;
            _config.Save();
            L($"Portváltás: a szerver új címe {next} (a kulcs ellenőrizve).");
        }
        catch (Exception ex) { L($"Portváltás ({cur.Port} → {port}) most nem sikerült, később újra: {ex.Message}"); }
    }

    private sealed class ServerKeyInfo
    {
        public string PublicKeyPem { get; set; } = "";
        public List<KeyLink>? KeyChain { get; set; }
    }

    // ================================================================ érzékelő-meghajtó (PawnIO)

    private DateTime _lastDriverTry = DateTime.MinValue;
    public const string SensorDriverId = "namazso.PawnIO";

    /// <summary>
    /// A CPU- és alaplap-érzékelőkhöz a PawnIO meghajtó kell. Ha a szerver kéri (hardver-érzékelők + meghajtó telepítése),
    /// és nincs fent, wingettel telepítjük (aláírt, a winget hivatalos forrásából) - naponta legfeljebb egyszer próbáljuk.
    /// Utána az érzékelőket újranyitjuk, hogy a CPU-értékek is megjelenjenek.
    /// </summary>
    private async Task EnsureSensorDriverAsync(MetricConfig mc, CancellationToken token)
    {
        if (!IsService || !mc.Hardware || !mc.SensorDriver || AgentHardware.DriverInstalled) return;
        if (DateTime.UtcNow - _lastDriverTry < TimeSpan.FromHours(24)) return;
        _lastDriverTry = DateTime.UtcNow;
        L("Érzékelő-meghajtó (PawnIO) telepítése wingettel…");
        var sb = new StringBuilder();
        var (ok, exit) = await RunWinget($"install --id {SensorDriverId} --exact --silent --scope machine --accept-package-agreements " +
                                         "--accept-source-agreements --disable-interactivity", line => sb.AppendLine(line), token);
        if (!ok)
            (ok, exit) = await RunWinget($"install --id {SensorDriverId} --exact --silent --accept-package-agreements " +
                                         "--accept-source-agreements --disable-interactivity", line => sb.AppendLine(line), token);
        if (ok || AgentHardware.DriverInstalled)
        {
            L("Az érzékelő-meghajtó telepítve – az érzékelők újranyitva.");
            AgentHardware.Reset();
            _installedAt = DateTime.MinValue;
        }
        else L($"Az érzékelő-meghajtó telepítése nem sikerült (kód: {exit}): {Trim(sb.ToString().Trim(), 400)}");
    }

    /// <summary>
    /// Szerver-aláírás ellenőrzése a tárolt szerverkulccsal; kulcscserénél az új kulcsot csak akkor fogadjuk el, ha a
    /// nálunk levőtől ellenőrizhető lánc vezet hozzá - ekkor az új kulcs lesz a tárolt. Hibánál naplóz és hamisat ad.
    /// </summary>
    private bool VerifyServerSignature(string payload, string signature, string? signerPem, List<KeyLink>? chain)
    {
        var known = _config.ServerPublicKeyPem;
        var signer = signerPem ?? known;
        if (known.Length > 0 && !KeyStore.ChainReaches(known, chain, signer))
        {
            L("BIZTONSÁGI HIBA: a válasz ismeretlen kulccsal érkezett, és nincs érvényes átadás – elutasítva.");
            return false;
        }
        if (!Crypto.Verify(signer, payload, signature))
        {
            L("BIZTONSÁGI HIBA: a szerver válaszának aláírása érvénytelen – elutasítva.");
            return false;
        }
        if (known.Length > 0 && signer != known)
        {
            _config.ServerPublicKeyPem = signer;
            _config.ServerFingerprint = Crypto.PublicKeyFingerprint(signer);
            _config.Save();
            L($"A szerver kulcsa lecserélődött ({Crypto.Algorithm(signer)}) – az átadás ellenőrizve, az agent átállt.");
        }
        return true;
    }

    // ================================================================ tálca-felület (1.5.7)

    /// <summary>A szerver engedi-e a tálca-ikont (MetricConfig.Tray; régi szerver nem küldi → nincs tálca).</summary>
    public bool TrayEnabled => _metricConfig?.Tray == true;
    public DateTime? LastCheckinUtc { get; private set; }
    public string ServerUrl => _config.ServerUrl;

    /// <summary>
    /// A tálca kérése a szervernek (a pipe-on kapott felhasználó nevében): az agent kulcsával aláírva, a válasz a szerver
    /// kulcsával aláírt és ehhez a kéréshez kötött – hamis szerver a tálcának sem mondhat semmit.
    /// </summary>
    public async Task<AgentUserReplyBody> UserRequestAsync(string kind, string user, string data, CancellationToken token)
    {
        if (_config.AgentId.Length == 0) return new AgentUserReplyBody { Message = "Az agent még nincs regisztrálva." };
        var req = new AgentUserRequest
        {
            AgentId = _config.AgentId, Timestamp = DateTime.UtcNow.ToString("o"), Nonce = Crypto.NewNonce(), Kind = kind, User = user, Data = data
        };
        req.Signature = Crypto.Sign(_config.PrivateKeyPem, req.CanonicalPayload());
        try
        {
            var resp = await _http.PostAsJsonAsync(Url("/api/user"), req, ProtocolJson.Options, token);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new AgentUserReplyBody { Message = "A szerver még nem ismeri a tálca-felületet (1.5.7-től) – frissíteni kell." };
            var raw = await resp.Content.ReadFromJsonAsync<AgentUserResponse>(ProtocolJson.Options, token);
            if (raw is null || raw.SignedPayload.Length == 0 || !VerifyServerSignature(raw.SignedPayload, raw.Signature, raw.SignerPublicKeyPem, raw.KeyChain))
                return new AgentUserReplyBody { Message = "A szerver válasza nem ellenőrizhető." };
            var body = ProtocolJson.Deserialize<AgentUserReplyBody>(raw.SignedPayload);
            if (body is null || body.RequestNonce != req.Nonce || body.AgentId != _config.AgentId)
                return new AgentUserReplyBody { Message = "A szerver válasza nem ehhez a kéréshez tartozik." };
            return body;
        }
        catch (Exception ex) { return new AgentUserReplyBody { Message = "A szerver nem érhető el: " + ex.Message }; }
    }

    // ================================================================ saját kulcs cseréje

    private DateTime _lastRotateTry = DateTime.MinValue;

    /// <summary>Esedékes-e a csere: lejárt a szerver szerinti idő, vagy a szerver erősebb kulcsot fogad, mint a mostani.</summary>
    private bool KeyRotationDue(KeyPolicy kp)
    {
        if (DateTime.UtcNow - _lastRotateTry < TimeSpan.FromMinutes(30)) return false;   // sikertelen próbák ritkítva
        if (kp.Strong && Crypto.KeyBits(_config.PublicKeyPem) < 384) return true;
        return kp.RotateDays > 0 && (_config.KeyCreatedUtc is not { } c || DateTime.UtcNow - c >= TimeSpan.FromDays(kp.RotateDays));
    }

    /// <summary>
    /// Új kulcspár; a kérést a jelenlegi kulcs és az új is aláírja. Csak a szerver ALÁÍRT, ehhez a kéréshez kötött
    /// visszaigazolására váltunk - addig a régi kulcs marad. Ha a visszaigazolás elveszik, a szerver a régi kulcsot
    /// a megerősítésig elfogadja, és a következő próba új kulcsot kér: a gép nem zárható ki.
    /// </summary>
    private async Task RotateKeyAsync(bool strong, CancellationToken token)
    {
        _lastRotateTry = DateTime.UtcNow;
        var k = Crypto.NewKeyPair(strong);
        var req = new KeyRotateRequest
        {
            AgentId = _config.AgentId, Timestamp = DateTime.UtcNow.ToString("o"), Nonce = Crypto.NewNonce(), NewPublicKeyPem = k.PublicKeyPem
        };
        var payload = req.CanonicalPayload();
        req.Signature = Crypto.Sign(_config.PrivateKeyPem, payload);
        req.NewKeySignature = Crypto.Sign(k.PrivateKeyPem, payload);
        try
        {
            var resp = await _http.PostAsJsonAsync(Url("/api/rotate-key"), req, ProtocolJson.Options, token);
            var raw = await resp.Content.ReadFromJsonAsync<KeyRotateResponse>(ProtocolJson.Options, token);
            if (raw is null || raw.SignedPayload.Length == 0 || !VerifyServerSignature(raw.SignedPayload, raw.Signature, raw.SignerPublicKeyPem, raw.KeyChain))
            { L("Kulcscsere: a szerver válasza nem ellenőrizhető – a régi kulcs marad."); return; }
            var body = ProtocolJson.Deserialize<KeyRotateReplyBody>(raw.SignedPayload);
            if (body is null || body.RequestNonce != req.Nonce || body.AgentId != _config.AgentId)
            { L("BIZTONSÁGI HIBA: a kulcscsere válasza nem ehhez a kéréshez tartozik – a régi kulcs marad."); return; }
            if (!body.Ok || body.Fingerprint != k.Fingerprint)
            { L("Kulcscsere elutasítva: " + body.Message); return; }

            _config.PrivateKeyPem = k.PrivateKeyPem;
            _config.PublicKeyPem = k.PublicKeyPem;
            _config.Fingerprint = k.Fingerprint;
            _config.KeyCreatedUtc = DateTime.UtcNow;
            _config.Save();
            L($"Kulcscsere kész: {Crypto.Algorithm(k.PublicKeyPem)}, új ujjlenyomat {k.Fingerprint[..16]}…");
        }
        catch (Exception ex) { L("Kulcscsere nem sikerült (a régi kulcs marad): " + ex.Message); }
    }

    // ================================================================ feladatvégrehajtás

    private async Task ExecuteTaskAsync(AgentTask task, CancellationToken token)
    {
        L($"Feladat: {task.Kind} {task.Target}");
        var sb = new StringBuilder();
        int exit = 0;
        bool ok = false;

        try
        {
            switch (task.Kind)
            {
                case AgentTaskKind.UpgradeApp when !WingetId.IsValid(task.Target):
                case AgentTaskKind.InstallApp when !WingetId.IsValid(task.Target):
                case AgentTaskKind.UninstallApp when !WingetId.IsValid(task.Target):
                    sb.AppendLine("Érvénytelen programazonosító – elutasítva.");
                    ok = false; exit = -1;
                    break;

                case AgentTaskKind.InstallApp:
                    // Gepszintu telepites; ha a csomagnak nincs gepszintu telepitoje, a winget alapertelmezeset probaljuk.
                    (ok, exit) = await RunWinget(
                        $"install --id \"{task.Target}\" --exact --scope machine --silent --accept-package-agreements " +
                        "--accept-source-agreements --disable-interactivity", line => sb.AppendLine(line), token);
                    if (!ok)
                    {
                        sb.AppendLine("-- újrapróbálás hatókör megadása nélkül --");
                        (ok, exit) = await RunWinget(
                            $"install --id \"{task.Target}\" --exact --silent --accept-package-agreements " +
                            "--accept-source-agreements --disable-interactivity", line => sb.AppendLine(line), token);
                    }
                    _installedAt = DateTime.MinValue;   // a lista a kovetkezo jelentkezeskor frissul
                    break;

                case AgentTaskKind.UninstallApp:
                    (ok, exit) = await RunWinget(
                        $"uninstall --id \"{task.Target}\" --exact --silent --accept-source-agreements --disable-interactivity",
                        line => sb.AppendLine(line), token);
                    // Registrybol jott program, amit a winget (pl. rendszerfiokban) nem tudott eltavolitani:
                    // a Windows sajat csendes eltavolitoja (MSI: msiexec /x, kulonben a gyartoe). cmd nelkul.
                    if (!ok && task.Target.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
                    {
                        if (AgentInventory.UninstallCommand(task.Target) is { } u)
                        {
                            sb.AppendLine($"Eltávolítás a Windows saját eltávolítójával: {Path.GetFileName(u.Exe)} {u.Args}");
                            var (_, code, _) = await RunCapture(u.Exe, u.Args, token, line => sb.AppendLine(line));
                            ok = code is 0 or 1641 or 3010;   // 1641/3010: siker, ujrainditas kell
                            exit = code;
                        }
                        else sb.AppendLine("Ehhez a programhoz nincs csendes (felhasználói beavatkozás nélküli) eltávolító.");
                    }
                    _installedAt = DateTime.MinValue;
                    break;

                case AgentTaskKind.WindowsUpdateScan:
                {
                    var (list, err) = await Task.Run(WinUpdates.Scan, token);
                    _wu = list; _wuError = err; _wuAt = DateTime.UtcNow;
                    ok = err.Length == 0;
                    sb.AppendLine(ok ? $"{list.Count} elérhető frissítés." : err);
                    break;
                }

                case AgentTaskKind.WindowsUpdateInstall:
                {
                    // "all" = minden elerheto; kulonben vesszovel elvalasztott UpdateID-k (GUID-ok).
                    HashSet<string>? ids = null;
                    if (!task.Target.Equals("all", StringComparison.OrdinalIgnoreCase))
                        ids = task.Target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .Where(x => Guid.TryParse(x, out _)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (ids is { Count: 0 }) { sb.AppendLine("Nincs érvényes frissítés-azonosító."); break; }
                    var (wok, wout, reboot) = await Task.Run(() => WinUpdates.Install(ids, line => sb.AppendLine(line)), token);
                    ok = wok; sb.AppendLine(wout);
                    var (list, err) = await Task.Run(WinUpdates.Scan, token);   // friss lista a telepites utan
                    _wu = list; _wuError = err; _wuAt = DateTime.UtcNow;
                    break;
                }

                case AgentTaskKind.Reboot:
                {
                    // Csak kifejezett rendszergazdai feladatra. A felhasznalo figyelmeztetest kap.
                    var delay = int.TryParse(task.Target, out var d) ? Math.Clamp(d, 0, 3600) : 300;
                    try
                    {
                        NativeShutdown.Restart(delay,
                            $"DevToolbox: a rendszergazda újraindítást ütemezett ({delay / 60} perc múlva). Mentsd a munkádat!");
                        ok = true; exit = 0;
                    }
                    catch (Exception ex) { ok = false; exit = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode ?? -1; sb.AppendLine(ex.Message); }
                    sb.AppendLine(ok ? $"Újraindítás ütemezve {delay} mp múlva." : "Az újraindítás ütemezése nem sikerült.");
                    break;
                }

                case AgentTaskKind.RebootNow:
                {
                    // 1.5.9: azonnali újraindítás (a rendszergazda kifejezett kérésére; a futó programokat a rendszer zárja be).
                    try { NativeShutdown.Restart(0, "DevToolbox: a rendszergazda azonnali újraindítást kért."); ok = true; exit = 0; }
                    catch (Exception ex) { ok = false; exit = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode ?? -1; sb.AppendLine(ex.Message); }
                    sb.AppendLine(ok ? "Azonnali újraindítás." : "Az újraindítás nem sikerült.");
                    break;
                }

                case AgentTaskKind.ServiceStart:
                case AgentTaskKind.ServiceStop:
                case AgentTaskKind.ServiceRestart:
                {
                    // 1.5.10: Windows-szolgáltatás kezelése (admin; a szerver aláírt feladatként adja ki). A nevet a
                    // ServiceController API kapja (NEM parancssor). A szerver is ellenőrzi, itt védelemből újra.
                    var svc = task.Target.Trim();
                    if (!ServiceName.IsValid(svc)) { ok = false; exit = -1; sb.AppendLine("Érvénytelen szolgáltatásnév."); break; }
                    if (!NativeService.Exists(svc)) { ok = false; exit = -1; sb.AppendLine($"Nincs ilyen szolgáltatás: {svc}"); break; }
                    var wait = TimeSpan.FromSeconds(30);
                    if (task.Kind is AgentTaskKind.ServiceStop or AgentTaskKind.ServiceRestart)
                    {
                        if (!NativeService.Stop(svc, wait)) { ok = false; exit = -1; sb.AppendLine($"A(z) {svc} leállítása nem sikerült (futó függő szolgáltatás vagy időtúllépés). Állapot: {NativeService.StatusText(svc)}"); break; }
                        sb.AppendLine($"{svc}: leállítva.");
                    }
                    if (task.Kind is AgentTaskKind.ServiceStart or AgentTaskKind.ServiceRestart)
                    {
                        if (!NativeService.Start(svc, wait)) { ok = false; exit = -1; sb.AppendLine($"A(z) {svc} indítása nem sikerült (időtúllépés vagy hiba). Állapot: {NativeService.StatusText(svc)}"); break; }
                        sb.AppendLine($"{svc}: elindítva.");
                    }
                    ok = true; exit = 0;
                    sb.AppendLine($"Állapot: {NativeService.StatusText(svc)}");
                    break;
                }

                case AgentTaskKind.RunCommand:
                {
                    // 1.5.9: webes admin terminál. Csak ha a szerver aláírt válaszában engedélyezte erre a gépre; a parancsot
                    // a kezelő (admin) adta ki az aláírt csatornán. PowerShell, LocalSystemként; a kimenet visszamegy.
                    if (!_terminalEnabled) { ok = false; exit = -1; sb.AppendLine("A terminál ezen a gépen nincs engedélyezve."); break; }
                    (ok, exit, var outp) = await RunTerminal(task.Target, token);
                    sb.Append(outp);
                    break;
                }

                case AgentTaskKind.UpgradeApp:
                    (ok, exit) = await RunWinget(
                        $"upgrade --id \"{task.Target}\" --exact --silent --accept-package-agreements " +
                        "--accept-source-agreements --disable-interactivity",
                        line => sb.AppendLine(line), token);
                    break;

                case AgentTaskKind.UpgradeAll:
                    (ok, exit) = await RunWinget(
                        "upgrade --all --silent --accept-package-agreements " +
                        "--accept-source-agreements --disable-interactivity",
                        line => sb.AppendLine(line), token);
                    break;

                case AgentTaskKind.RunInventory:
                    ok = true;   // a kovetkezo jelentkezes ugyis friss leltárt kuld
                    sb.AppendLine("Leltár frissítése a következő jelentkezéskor.");
                    break;
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("Hiba: " + ex.Message);
            ok = false;
        }

        var result = new TaskResult
        {
            AgentId = _config.AgentId,
            TaskId = task.TaskId,
            Success = ok,
            ExitCode = exit,
            Output = Trim(sb.ToString(), 8000),
            Timestamp = DateTime.UtcNow.ToString("o"),
            Nonce = Crypto.NewNonce()
        };
        result.Signature = Crypto.Sign(_config.PrivateKeyPem, result.CanonicalPayload());

        // Programfeladat utan a kovetkezo jelentkezes friss listat kuldjon (ne a 30 perces gyorsitotarat).
        if (task.Kind is AgentTaskKind.UpgradeApp or AgentTaskKind.UpgradeAll or AgentTaskKind.InstallApp or AgentTaskKind.UninstallApp)
        { _updatesAt = DateTime.MinValue; _installedAt = DateTime.MinValue; }

        try
        {
            await _http.PostAsJsonAsync(Url("/api/result"), result, ProtocolJson.Options, token);
            if (ok) L($"Feladat kész ({task.Kind}, siker), eredmény elküldve.");
            // Hiba esetén a feladat típusa, a kilépési kód (pl. Win32-hibakód) és a kimenet eleje is a naplóba kerül,
            // hogy a C:\ProgramData\DevToolbox-Agent\agent.log önmagában megmondja az okát (pl. reboot: kód 87).
            else    L($"Feladat HIBA ({task.Kind}, kód: {exit}): {Trim(sb.ToString().Trim(), 400)}");
        }
        catch (Exception ex) { L("Az eredmény küldése nem sikerült: " + ex.Message); }
    }

    // ================================================================ állapotgyűjtés

    private async Task<AgentStatus> CollectStatusAsync(CancellationToken token)
    {
        var status = new AgentStatus
        {
            MachineName = Environment.MachineName,
            OperatingSystem = GetOsCaption(),
            AgentVersion = AgentVersion,
            AgentSha256 = AgentUpdater.SelfSha,
            CollectedUtc = DateTime.UtcNow
        };

        // Frissitheto csomagok wingettel - 30 percenkent (a winget futasa drága: forras-frissites, telepitett
        // programok bejarasa); programfeladat utan azonnal ujra (lasd ExecuteTaskAsync).
        // A keresés időköze a szerver beállítása szerint (1.5.6; alap 30 perc programokra, 6 óra a Windows Update-re).
        var updEvery = TimeSpan.FromMinutes(_metricConfig is { AppUpdateMinutes: > 0 } c1 ? Math.Clamp(c1.AppUpdateMinutes, 15, 1440) : 30);
        var wuEvery = TimeSpan.FromHours(_metricConfig is { WuScanHours: > 0 } c2 ? Math.Clamp(c2.WuScanHours, 1, 48) : 6);
        if (DateTime.UtcNow - _updatesAt > updEvery)
        {
            var (ok, _, output) = await RunWingetCapture(
                "upgrade --include-unknown --accept-source-agreements --disable-interactivity",
                token);
            if (ok || output.Length > 0)
            {
                _updates = AgentWinget.Parse(output).Select(pkg => new AppUpdate
                {
                    Id = pkg.Id, Name = pkg.Name,
                    Installed = pkg.Installed, Available = pkg.Available
                }).ToList();
                _updatesAt = DateTime.UtcNow;
            }
        }
        status.Updates = _updates;
        status.UpdatesAvailable = _updates.Count;

        // Telepitett programok (a felulet eltavolitas-listajahoz), orankent frissitve.
        if (DateTime.UtcNow - _installedAt > TimeSpan.FromHours(1))
        {
            var (lok, _, lout) = await RunWingetCapture("list --accept-source-agreements --disable-interactivity", token);
            if (lok || lout.Length > 0) _installed = AgentWinget.ParseList(lout);
            _installedAt = DateTime.UtcNow;   // sikertelen winget mellett se probalkozzunk minden jelentkezeskor
        }
        // A program-leltar alapbol a registrybol jon (rendszerfiokban is teljes, folyamat nelkul, minden
        // jelentkezeskor friss); a winget-azonositokat a fenti, oranként frissulo listabol parositjuk hozza.
        try { status.Installed = AgentInventory.Read(_installed); }
        catch (Exception ex) { L("Programleltár hiba: " + ex.Message); status.Installed = _installed; }

        // Windows Update: a kereses percekig tarthat, ezert hatterben fut, a jelentes a gyorsitotarbol megy.
        if (_wuAt is null || DateTime.UtcNow - _wuAt > wuEvery) StartWuScan();
        status.WindowsUpdates = _wu;
        status.WuScannedUtc = _wuAt;
        status.WuError = _wuError;
        try { status.PendingReboot = WinUpdates.PendingReboot(); status.UpdateSource = WinUpdates.Source(); } catch { }

        // Monitorozasi metrikak a FleetServer beallitasa szerint.
        try
        {
            var mc = _metricConfig ?? new MetricConfig();
            await EnsureSensorDriverAsync(mc, token);
            status.Metrics = AgentMetrics.Collect(AgentMetrics.Config.From(mc, ServerHost));
            // Rendszerállapot számként is, hogy küszöb állítható legyen rá (újraindításra vár, függő Windows-frissítések).
            status.Metrics.Add(new Metric { Key = "sys.pendingreboot", Label = "Újraindításra vár", Value = status.PendingReboot ? 1 : 0, Category = "Rendszer" });
            if (_wuAt is not null)
            {
                status.Metrics.Add(new Metric { Key = "wu.pending", Label = "Függő Windows-frissítések", Value = _wu.Count, Unit = "db", Category = "Rendszer" });
                status.Metrics.Add(new Metric { Key = "wu.important", Label = "Függő fontos / kritikus frissítések", Unit = "db", Category = "Rendszer",
                    Value = _wu.Count(w => w.Severity is "Critical" or "Important" or "Kritikus" or "Fontos") });
            }
            status.Metrics.Add(new Metric { Key = "app.updates", Label = "Frissíthető programok", Value = status.UpdatesAvailable, Unit = "db", Category = "Rendszer" });
            L($"Metrikagyűjtés: {status.Metrics.Count} érték");
            // A legtöbb erőforrást használó folyamatok - csak ha a szerver ismeri ezt a mezőt (különben az aláírás nem egyezne).
            if (mc.StatusVersion >= 2) status.TopProcesses = AgentMetrics.TopProcesses();
            // A gép szolgáltatásai (a „Mit mérjen?” listájához): csak ha változott, vagy 6 óránként (a szerver megtartja az előzőt).
            if (mc.StatusVersion >= 3)
            {
                var svc = AgentMetrics.ServiceList();
                var h = string.Concat(svc.Select(s => s.Name + "|" + s.DisplayName + "|" + s.Running + s.Auto + ";")).GetHashCode();
                if (svc.Count > 0 && (h != _svcSentHash || DateTime.UtcNow - _svcSentAt > TimeSpan.FromHours(6)))
                { status.ServiceList = svc; _svcPendingHash = h; }
            }
            // Driverek és firmware a sérülékenység-kereséshez (a Központ párosítja az NVD-vel).
            if (mc.StatusVersion >= 4)
            {
                if (DateTime.UtcNow - _compAt > TimeSpan.FromHours(1)) { _comp = AgentMetrics.Components(); _compAt = DateTime.UtcNow; }
                var ch = string.Concat(_comp.Select(c => c.Kind + "|" + c.Vendor + "|" + c.Name + "|" + c.Version + ";")).GetHashCode();
                if (_comp.Count > 0 && (ch != _compSentHash || DateTime.UtcNow - _compSentAt > TimeSpan.FromHours(24)))
                { status.Components = _comp; _compPendingHash = ch; }
            }
        }
        catch (Exception ex) { L("Metrikagyűjtés hiba: " + ex.Message); }

        return status;
    }

    /// <summary>A szerver gépneve / címe (a ping alapcélja).</summary>
    private string ServerHost => Uri.TryCreate(_config.ServerUrl, UriKind.Absolute, out var u) ? u.Host : "";

    // ================================================================ winget futtatás

    private static async Task<(bool Ok, int Exit)> RunWinget(
        string args, Action<string>? onLine, CancellationToken token)
    {
        var (ok, exit, _) = await RunWingetCapture(args, token, onLine);
        return (ok, exit);
    }

    private static async Task<(bool Ok, int Exit, string Capture)> RunWingetCapture(
        string args, CancellationToken token, Action<string>? onLine = null)
    {
        var exe = AgentWinget.FindWinget();
        if (exe is null) return (false, -1, "");
        var (_, code, capture) = await RunCapture(exe, args, token, onLine);
        // -1978335189 = winget: "nincs mit frissiteni" - az is siker
        return (code == 0 || code == -1978335189, code, capture);
    }

    private static async Task<(bool Ok, int Exit, string Capture)> RunCapture(
        string exe, string args, CancellationToken token, Action<string>? onLine = null)
    {
        var sb = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { sb.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try { await proc.WaitForExitAsync(token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }

        int code; try { code = proc.ExitCode; } catch { code = -1; }
        return (code == 0, code, sb.ToString());
    }

    // ================================================================ webes admin terminál (1.5.9)

    /// <summary>
    /// Egy PowerShell-parancs futtatása a webes admin terminálhoz (a szerver gépenként engedélyezi, a parancsot admin adta ki
    /// az aláírt csatornán). LocalSystemként, 120 mp időkorláttal; a kimenet (stdout+stderr) legfeljebb ~200 KB.
    /// </summary>
    private async Task<(bool Ok, int Exit, string Output)> RunTerminal(string command, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command)) return (false, -1, "(üres parancs)");
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe)) exe = "powershell.exe";
        // A parancsot base64-ben adjuk át (-EncodedCommand): idézőjel-, sortörés- és escape-gond nélkül, parancssori injekció nélkül.
        var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        L($"Terminál: parancs ({command.Length} karakter)");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var (_, code, cap) = await RunCapture(exe, $"-NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass -EncodedCommand {enc}", timeout.Token);
            if (cap.Length > 200_000) cap = cap[..200_000] + "\n…(a kimenet levágva)";
            return (code == 0, code, cap.Length > 0 ? cap : "(nincs kimenet)");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return (false, -1, "(a parancs 120 mp alatt nem fejeződött be – megszakítva)"); }
    }

    // ================================================================ segédek

    private string Url(string path) => _config.ServerUrl.TrimEnd('/') + path;

    private static string GetOsCaption()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Caption FROM Win32_OperatingSystem");
            foreach (var o in searcher.Get())
                return o["Caption"]?.ToString()?.Trim() ?? Environment.OSVersion.VersionString;
        }
        catch { }
        return Environment.OSVersion.VersionString;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "\n… (levágva)";
}
