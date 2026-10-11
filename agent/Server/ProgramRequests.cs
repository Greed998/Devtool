using System.Text.Json;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// Programkérések a gépek tálca-ikonjáról (1.5.7, a felhasználó kérése): a sima felhasználó a repó programjait azonnal
/// telepítheti, más programot kérhet – az a jóváhagyási sorba kerül (a szerver felületén és a Központban is dönthető), és
/// jóváhagyás után magától települ (InstallApp feladat; az eredménye zárja le a kérést). A szerver az egyetlen író
/// (program-kerelmek.json); a Központ döntése is itt fut le (HubSiteOp).
/// </summary>
public static class ProgramRequests
{
    private static readonly object Lock = new();
    private static List<ProgramRequestInfo> _items = new();
    private static string _file = "";

    /// <summary>Gépenként legfeljebb ennyi függő kérés, és naponta ennyi új (a tálcáról ne lehessen elárasztani a sort).</summary>
    public const int MaxPendingPerAgent = 10, MaxPerAgentPerDay = 30;

    public static void Init(string dataDir)
    {
        _file = Path.Combine(dataDir, "program-kerelmek.json");
        try
        {
            if (File.Exists(_file))
                lock (Lock) _items = JsonSerializer.Deserialize<List<ProgramRequestInfo>>(File.ReadAllText(_file), ProtocolJson.Options) ?? new();
        }
        catch { }
    }

    private static void Save()
    {
        if (_file.Length == 0) return;
        try
        {
            // A lezártakból a legutóbbi 500 marad (a függők mind).
            var closed = _items.Where(i => i.State is not ("pending" or "installing")).OrderByDescending(i => i.DecidedUtc ?? i.CreatedUtc).Skip(500).ToHashSet();
            if (closed.Count > 0) _items.RemoveAll(closed.Contains);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, ProtocolJson.Options));
            File.Move(tmp, _file, true);
        }
        catch { }
    }

    public static List<ProgramRequestInfo> List(string? agentId = null)
    {
        lock (Lock) return _items.Where(i => agentId is null || i.AgentId == agentId).OrderByDescending(i => i.CreatedUtc).ToList();
    }

    public static int PendingCount { get { lock (Lock) return _items.Count(i => i.State == "pending"); } }

    /// <summary>A Központnak: a függők és a legutóbbi 50 lezárt.</summary>
    public static List<ProgramRequestInfo> Summary()
    {
        lock (Lock)
            return _items.Where(i => i.State is "pending" or "installing")
                .Concat(_items.Where(i => i.State is not ("pending" or "installing")).OrderByDescending(i => i.DecidedUtc ?? i.CreatedUtc).Take(50))
                .ToList();
    }

    /// <summary>
    /// Új kérés a tálcáról. autoApprove: a repóban levő program – azonnal települ (a kérés csak napló). A már függő
    /// ugyanarra a programra nem duplázódik. Visszaadja a kérést, vagy a hibát.
    /// </summary>
    public static (ProgramRequestInfo? Item, string? Error) Add(FleetStore store, string agentId, string user, string packageId, string name, string reason,
        bool autoApprove, string approvedBy = "repó (automatikus)")
    {
        if (!WingetId.IsValid(packageId)) return (null, "Érvénytelen programazonosító.");
        var a = store.GetAgent(agentId);
        if (a is null || a.Revoked) return (null, "Ismeretlen gép.");
        ProgramRequestInfo item;
        lock (Lock)
        {
            var mine = _items.Where(i => i.AgentId == agentId).ToList();
            if (mine.FirstOrDefault(i => i.State is "pending" or "installing" && i.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) is { } dup)
                return (dup, null);
            if (!autoApprove && mine.Count(i => i.State == "pending") >= MaxPendingPerAgent)
                return (null, $"Erről a gépről már {MaxPendingPerAgent} kérés vár döntésre – előbb azokat kell elbírálni.");
            if (mine.Count(i => DateTime.UtcNow - i.CreatedUtc < TimeSpan.FromDays(1)) >= MaxPerAgentPerDay)
                return (null, "Mára elérted a kérések számának korlátját erről a gépről.");
            item = new ProgramRequestInfo
            {
                Id = Guid.NewGuid().ToString("N"), AgentId = agentId, Machine = a.MachineName, User = Trim(user, 120),
                PackageId = packageId, Name = Trim(name.Length > 0 ? name : packageId, 200), Reason = Trim(reason, 500),
                CreatedUtc = DateTime.UtcNow, State = "pending"
            };
            _items.Add(item);
            if (autoApprove) Start(store, item, approvedBy, "");
            Save();
        }
        AuditLog.Write(ServerPaths.DataDir, item.User.Length > 0 ? item.User : "tálca",
            autoApprove ? $"program telepítése a tálcáról ({approvedBy})" : "programkérés a tálcáról", $"{item.Machine}: {item.Name} ({item.PackageId})");
        return (item, null);
    }

    private static void Start(FleetStore store, ProgramRequestInfo i, string by, string note)
    {
        var task = new AgentTask { Kind = AgentTaskKind.InstallApp, Target = i.PackageId };
        store.QueueTask(i.AgentId, task);
        i.TaskId = task.TaskId; i.State = "installing"; i.DecidedBy = by; i.DecidedUtc = DateTime.UtcNow; i.Note = Trim(note, 500);
    }

    /// <summary>Döntés (a szerver felületéről vagy a Központból). Hibánál a hiba szövege, különben null.</summary>
    public static string? Decide(FleetStore store, string id, bool approve, string by, string note)
    {
        ProgramRequestInfo? i;
        lock (Lock)
        {
            i = _items.FirstOrDefault(x => x.Id == id);
            if (i is null) return "Nincs ilyen kérés.";
            if (i.State != "pending") return "Erről a kérésről már döntöttek.";
            if (approve)
            {
                if (store.GetAgent(i.AgentId) is not { Revoked: false }) return "A gép már nincs a flottában.";
                Start(store, i, by, note);
            }
            else { i.State = "denied"; i.DecidedBy = by; i.DecidedUtc = DateTime.UtcNow; i.Note = Trim(note, 500); }
            Save();
        }
        AuditLog.Write(ServerPaths.DataDir, by, approve ? "programkérés jóváhagyva" : "programkérés elutasítva", $"{i.Machine}: {i.Name} ({i.PackageId}) – kérte: {i.User}");
        return null;
    }

    /// <summary>A telepítési feladat eredménye zárja le a kérést (a tálca ebből látja: települt / nem sikerült).</summary>
    public static void OnResult(TaskResult r)
    {
        lock (Lock)
        {
            var i = _items.FirstOrDefault(x => x.TaskId == r.TaskId && x.AgentId == r.AgentId && x.State == "installing");
            if (i is null) return;
            i.State = r.Success ? "installed" : "failed";
            if (!r.Success) i.Note = Trim((i.Note.Length > 0 ? i.Note + " · " : "") + "hiba: " + LastLine(r.Output), 500);
            Save();
        }
    }

    private static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string Trim(string? s, int max)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }
}
