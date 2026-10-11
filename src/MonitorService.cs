using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace DevToolbox;

/// <summary>
/// A figyelest vegzo hatterszolgaltatas. Szandekosan NEM fugg a feluletettol:
/// az ablak bezarasa (talcara teves) utan is fut tovabb.
///
/// Eroforras-takarekossag:
///  - System.Threading.Timer, nem UI-idozito: nem tart eletben ablakot
///  - egyszerre legfeljebb 4 gep vizsgalata parhuzamosan
///  - a ciklusok nem futhatnak egymasra (_running kapcsolo)
///  - eredmenyek helyben tarolva, a felulet csak lekerdezi oket
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MonitorService : IDisposable
{
    public static MonitorService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, HostHealth> _results = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _running;

    private MonitorService() { }

    public bool IsRunning => _timer is not null;
    public bool IsChecking => Volatile.Read(ref _running) == 1;
    public DateTime? LastCycle { get; private set; }

    /// <summary>Egy gep allapota megvaltozott (regi, uj).</summary>
    public event Action<MonitoredHost, HealthState, HostHealth>? StatusChanged;

    /// <summary>Egy gep vizsgalata befejezodott.</summary>
    public event Action<MonitoredHost, HostHealth>? HostChecked;

    /// <summary>Egy teljes kor lefutott.</summary>
    public event Action? CycleCompleted;

    public HostHealth? Get(string hostId) =>
        _results.TryGetValue(hostId, out var h) ? h : null;

    public IReadOnlyDictionary<string, HostHealth> Results => _results;

    /// <summary>A legrosszabb allapot az osszes figyelt gepen.</summary>
    public HealthState Overall
    {
        get
        {
            var known = MonitorConfig.Current.Hosts
                .Where(h => h.Enabled)
                .Select(h => Get(h.Id))
                .Where(r => r is not null)
                .Select(r => r!.Overall)
                .ToList();

            return known.Count == 0 ? HealthState.Ismeretlen : known.Max();
        }
    }

    // ================================================================ vezérlés

    public void Start()
    {
        Stop();

        var minutes = Math.Clamp(MonitorConfig.Current.IntervalMinutes, 1, 1440);
        _cts = new CancellationTokenSource();

        _timer = new System.Threading.Timer(
            _ => _ = RunCycleAsync(),
            null,
            TimeSpan.FromSeconds(3),                 // az elso kor rovid keslekedessel
            TimeSpan.FromMinutes(minutes));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _timer?.Dispose();
        _timer = null;
        _cts?.Dispose();
        _cts = null;
    }

    public Task RunNowAsync() => RunCycleAsync();

    public void Forget(string hostId)
    {
        _results.TryRemove(hostId, out _);
        _lastNotified.TryRemove(hostId, out _);
    }

    // ================================================================ ciklus

    private async Task RunCycleAsync()
    {
        // Ha az elozo kor meg fut, ezt kihagyjuk - nem torlodnak egymasra.
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        var token = _cts?.Token ?? CancellationToken.None;

        try
        {
            var hosts = MonitorConfig.Current.Hosts.Where(h => h.Enabled).ToList();
            if (hosts.Count == 0) return;

            using var throttle = new SemaphoreSlim(4);

            var tasks = hosts.Select(async host =>
            {
                await throttle.WaitAsync(token);
                try
                {
                    var previous = Get(host.Id)?.Overall ?? HealthState.Ismeretlen;
                    var health = await HealthChecker.CheckAsync(host, MonitorConfig.Current, token);

                    _results[host.Id] = health;
                    HostChecked?.Invoke(host, health);

                    if (health.Overall != previous && ShouldNotify(host, previous, health.Overall))
                        StatusChanged?.Invoke(host, previous, health);
                }
                catch (OperationCanceledException) { }
                catch { /* egy gep hibaja nem viheti el a kort */ }
                finally { throttle.Release(); }
            });

            await Task.WhenAll(tasks);
            LastCycle = DateTime.Now;
            CycleCompleted?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { Volatile.Write(ref _running, 0); }
    }

    /// <summary>
    /// Csak allapotvaltasnal ertesitunk, es gepenkent legfeljebb 15 percenkent,
    /// hogy egy billego kapcsolat ne arassza el a felhasznalot.
    /// </summary>
    private bool ShouldNotify(MonitoredHost host, HealthState from, HealthState to)
    {
        var cfg = MonitorConfig.Current;

        var isProblem = to is HealthState.Hiba or HealthState.Elerhetetlen;
        var isRecovery = to == HealthState.Rendben && from is HealthState.Hiba or HealthState.Elerhetetlen;

        if (isProblem && !cfg.NotifyOnError) return false;
        if (isRecovery && !cfg.NotifyOnRecovery) return false;
        if (!isProblem && !isRecovery) return false;

        // Az elso mereskor ne zudítsunk ki ertesitest mindenrol.
        if (from == HealthState.Ismeretlen && !isProblem) return false;

        if (_lastNotified.TryGetValue(host.Id, out var last) &&
            DateTime.Now - last < TimeSpan.FromMinutes(15) && !isRecovery)
            return false;

        _lastNotified[host.Id] = DateTime.Now;
        return true;
    }

    public void Dispose() => Stop();
}
