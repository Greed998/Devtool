using System.Text.Json;

namespace DevToolbox.Fleet.Server;

public enum AlertOp { Nagyobb, Kisebb, Egyenlo }   // >  <  =

/// <summary>
/// Egy kuszob-szabaly: EGY metrika-kulcsra, EGY osszehasonlitassal. A felhasznalo veszi
/// fel, semmi nem riaszt alapbol. Opcionalisan egy gepre szukitheto (AgentId ures = mind).
///
/// A riasztas akkor lep eletbe, ha a feltetel ForSamples egymast koveto mereskor igaz -
/// igy egy pillanatnyi csucs nem riaszt (pl. CPU 1 masodpercre 100%).
/// </summary>
public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string MetricKey { get; set; } = "";     // pl. disk.C.usedpct
    public AlertOp Op { get; set; } = AlertOp.Nagyobb;
    public double Threshold { get; set; }
    public string AgentId { get; set; } = "";        // ures = minden gepre
    public int ForSamples { get; set; } = 1;         // ennyi egymast koveto meresnyi kell
    public bool Enabled { get; set; } = true;

    // ---- 1.5.0: hatókör és leküldés
    /// <summary>Csak ennek a gépcsoportnak a gépeire (üres = minden gép).</summary>
    public string Group { get; set; } = "";
    /// <summary>Csak a Központban: mely telephelyekre vonatkozik (üres = mindre).</summary>
    public List<string> Sites { get; set; } = new();
    /// <summary>Csak a Központban: a szabály le is megy a telephelyekre (ott percre pontosan fut, a helyiek mellett).</summary>
    public bool PushDown { get; set; } = true;
    /// <summary>1.5.6: a küszöb megjelenítési egysége (pl. Mbit/s, Gbit/s, GB/s) - a Threshold mindig a mérés saját egységében van.</summary>
    public string DisplayUnit { get; set; } = "";

    public static readonly string[] DisplayUnits = { "", "KB/s", "MB/s", "GB/s", "kbit/s", "Mbit/s", "Gbit/s", "MB", "GB", "TB" };
    public static string CleanUnit(string? u) => DisplayUnits.Contains(u ?? "") ? u ?? "" : "";

    /// <summary>Metrika-kulcs (vagy minta) ellenőrzése: betű, szám, pont, kötőjel, aláhúzás, csillag.</summary>
    public static bool ValidKey(string k) => k.Length is > 0 and <= 200 && k.All(c => char.IsLetterOrDigit(c) || c is '.' or '*' or '-' or '_');

    /// <summary>A szabály erre a gépre vonatkozik-e (gép, csoport, telephely szerint). siteId null: telephelyen futunk.</summary>
    public bool AppliesTo(string agentId, IEnumerable<string>? groups, string? siteId = null) =>
        Enabled && (AgentId.Length == 0 || AgentId == agentId) &&
        (Group.Length == 0 || (groups ?? Array.Empty<string>()).Contains(Group, StringComparer.OrdinalIgnoreCase)) &&
        (siteId is null || Sites.Count == 0 || Sites.Contains(siteId));

    /// <summary>
    /// Minta-kulcs: a „*” egy kulcsrész (pont nélküli) tetszőleges darabja, pl. disk.*.usedpct = bármelyik lemez,
    /// hw.gpu*.temp = bármelyik videókártya. Ilyenkor kulcsonként külön riasztás (melyik lemez telt meg).
    /// </summary>
    public bool IsPattern => MetricKey.Contains('*');

    private System.Text.RegularExpressions.Regex? _rx;
    private string _rxFor = "";
    public bool KeyMatches(string key)
    {
        if (!IsPattern) return key == MetricKey;
        if (_rx is null || _rxFor != MetricKey)
        {
            _rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(MetricKey).Replace("\\*", "[^.]*") + "$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            _rxFor = MetricKey;
        }
        return _rx.IsMatch(key);
    }

    public bool Matches(double value) => Op switch
    {
        AlertOp.Nagyobb => value > Threshold,
        AlertOp.Kisebb => value < Threshold,
        AlertOp.Egyenlo => Math.Abs(value - Threshold) < 0.0001,
        _ => false
    };

    public string OpText => Op switch { AlertOp.Nagyobb => ">", AlertOp.Kisebb => "<", _ => "=" };
}

/// <summary>Egy aktiv riasztas allapota (egy szabaly + egy gep).</summary>
public sealed class AlertState
{
    public string RuleId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public bool Active { get; set; }
    public int Streak { get; set; }                  // hany egymast koveto meres teljesitette
    public DateTime? SinceUtc { get; set; }
    public double LastValue { get; set; }
    /// <summary>Kiesesnel a gep / telephely neve (a kuszob-allapotoknal a felulet a gepet maga keresi ki).</summary>
    public string Name { get; set; } = "";
    /// <summary>Minta-szabálynál (disk.*.usedpct) a konkrét kulcs, amelyik riaszt.</summary>
    public string MetricKey { get; set; } = "";
}

/// <summary>Allapotvaltas. Text: kesz leiras (kiesesnel), kulonben az ertek es a kuszob irodik ki.</summary>
public sealed record AlertEvent(string RuleName, string AgentId, string MachineName,
    double Value, double Threshold, string OpText, bool Raised, DateTime WhenUtc, string? Text = null)
{
    public string Detail => Text ?? $"{Value:0.#} {OpText} {Threshold:0.#}";
}

/// <summary>
/// A kuszobok tarolasa es kiertekelese. A kiertekeles minden jelentkezeskor fut az adott
/// gep metrikaira. Allapotvaltasnal (nyugalom->riaszt vagy vissza) esemenyt ad vissza,
/// amit a hivo (FleetServer) ertesitesse alakit.
/// </summary>
public sealed class AlertStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<AlertRule> _rules = new();
    private readonly Dictionary<string, AlertState> _states = new();   // kulcs: ruleId|agentId

    public AlertStore(string dir)
    {
        _path = Path.Combine(dir, "alerts.json");
        _offlinePath = Path.Combine(dir, "alerts-offline.json");
        Load();
    }

    // ---- Kieses: a gep / telephely N perce nem jelentkezett. Az allapot lemezre is kerul, hogy egy
    // ujrainditas utan a mar jelzett kiesest ne jelezzuk ujra (e-mailben sem).
    public const string OfflineRuleId = "kieses";
    public const string OfflineRuleName = "Nem jelentkezik";
    private readonly string _offlinePath;

    /// <summary>Egy elem kiesesi allapota; valtozaskor esemenyt ad (uj kieses vagy ujra jelentkezik).</summary>
    public AlertEvent? SetOffline(string key, string name, bool offline, double minutes, double limitMinutes)
    {
        lock (_lock)
        {
            var sKey = OfflineRuleId + "|" + key;
            _states.TryGetValue(sKey, out var st);
            if (offline)
            {
                if (st is { Active: true }) { st.LastValue = Math.Round(minutes); st.Name = name; return null; }
                _states[sKey] = new AlertState { RuleId = OfflineRuleId, AgentId = key, Active = true, SinceUtc = DateTime.UtcNow, LastValue = Math.Round(minutes), Name = name };
                SaveOffline();
                return new AlertEvent(OfflineRuleName, key, name, minutes, limitMinutes, "≥", true, DateTime.UtcNow,
                    $"{MinText(minutes)} nem jelentkezett (küszöb: {limitMinutes:0} perc)");
            }
            if (st is not { Active: true }) return null;
            _states.Remove(sKey);
            SaveOffline();
            return new AlertEvent(OfflineRuleName, key, name, 0, limitMinutes, "≥", false, DateTime.UtcNow, "újra jelentkezik");
        }
    }

    /// <summary>A kiesesi allapotok kozul csak ezek maradnak (a torolt / mar nem figyelt elemeke csendben megszunik).</summary>
    public void KeepOfflineOnly(ICollection<string> keys)
    {
        lock (_lock)
        {
            var drop = _states.Values.Where(s => s.RuleId == OfflineRuleId && !keys.Contains(s.AgentId)).ToList();
            foreach (var s in drop) _states.Remove(OfflineRuleId + "|" + s.AgentId);
            if (drop.Count > 0) SaveOffline();
        }
    }

    public bool IsOffline(string key) { lock (_lock) return _states.ContainsKey(OfflineRuleId + "|" + key); }

    // ---- Állapot-riasztás gép nélkül (1.5.5): a telephely internetkapcsolata (NetWatch). Mint a kiesés: lemezre kerül,
    // így újraindítás után nem jelez újra, és a megszűnését is egyszer jelzi.
    public const string InternetRuleId = "internet";
    public const string InternetRuleName = "Internetkapcsolat";
    /// <summary>1.5.6: gyorsan figyelt szolgáltatás leállt (az agent 5 mp-enként nézi, és azonnal jelez). Kulcs: agentId|szolgáltatás.</summary>
    public const string ServiceRuleId = "szolgaltatas";
    public const string ServiceRuleName = "Szolgáltatás leállt";
    private static readonly HashSet<string> PersistedRules = new() { OfflineRuleId, InternetRuleId, ServiceRuleId };

    /// <summary>Egy állapot be/ki; változáskor eseményt ad (riasztás / megszűnés), különben null. detail: a felületen a kulcs helyén.</summary>
    public AlertEvent? SetCondition(string ruleId, string ruleName, string key, string name, bool active, string raisedText, string clearedText, string detail = "")
    {
        lock (_lock)
        {
            var sKey = ruleId + "|" + key;
            _states.TryGetValue(sKey, out var st);
            if (active)
            {
                if (st is { Active: true }) return null;
                _states[sKey] = new AlertState { RuleId = ruleId, AgentId = key, Active = true, SinceUtc = DateTime.UtcNow, Name = name, MetricKey = detail };
                SaveOffline();
                return new AlertEvent(ruleName, key, name, 0, 0, "", true, DateTime.UtcNow, raisedText);
            }
            if (st is not { Active: true }) return null;
            _states.Remove(sKey);
            SaveOffline();
            return new AlertEvent(ruleName, key, name, 0, 0, "", false, DateTime.UtcNow, clearedText);
        }
    }

    private static string MinText(double m) => m >= 120 ? $"{m / 60:0.#} órája" : $"{m:0} perce";

    private void SaveOffline()
    {
        try
        {
            var list = _states.Values.Where(s => PersistedRules.Contains(s.RuleId)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_offlinePath)!);
            var tmp = _offlinePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, ProtocolJson.Options));
            File.Move(tmp, _offlinePath, overwrite: true);
        }
        catch { }
    }

    public IReadOnlyList<AlertRule> Rules { get { lock (_lock) return _rules.ToList(); } }

    public void Add(AlertRule r) { lock (_lock) { _rules.Add(r); Save(); } }
    public void Remove(string id) { lock (_lock) { _rules.RemoveAll(x => x.Id == id); DropStates(id); Save(); } }
    public void Update(AlertRule r)
    {
        lock (_lock)
        {
            var i = _rules.FindIndex(x => x.Id == r.Id);
            if (i >= 0) _rules[i] = r;
            // A feltétel / hatókör változhatott: az eddigi állapot nem érvényes (különben egy már nem
            // vonatkozó gép riasztása örökre aktív maradna).
            DropStates(r.Id);
            Save();
        }
    }

    private void DropStates(string ruleId)
    {
        foreach (var k in _states.Where(kv => kv.Value.RuleId == ruleId).Select(kv => kv.Key).ToList()) _states.Remove(k);
    }

    // ---- A Központtól kapott szabályok (csak a telephelyen; csak olvashatók, a helyiek mellett futnak).
    // Az azonosítójuk „hub-” előtagot kap, hogy az állapotuk ne keveredjen a helyiekével.
    public const string HubPrefix = "hub-";
    private List<AlertRule> _hubRules = new();
    public IReadOnlyList<AlertRule> HubRules { get { lock (_lock) return _hubRules.ToList(); } }

    /// <summary>A Központ szabályainak cseréje; a megszűnt szabályok állapota törlődik.</summary>
    public void SetHubRules(IEnumerable<AlertRule> rules)
    {
        lock (_lock)
        {
            var list = rules.Select(r => { if (!r.Id.StartsWith(HubPrefix)) r.Id = HubPrefix + r.Id; return r; }).ToList();
            foreach (var old in _hubRules)
            {
                var now = list.FirstOrDefault(x => x.Id == old.Id);
                if (now is null || ProtocolJson.Serialize(now) != ProtocolJson.Serialize(old)) DropStates(old.Id);
            }
            _hubRules = list;
        }
    }

    /// <summary>Aktualisan aktiv (riaszto) allapotok - a felulet ezt mutatja.</summary>
    public List<AlertState> ActiveStates()
    {
        lock (_lock) return _states.Values.Where(s => s.Active).ToList();
    }

    /// <summary>Egy szabály neve azonosító szerint (helyi vagy a Központtól kapott).</summary>
    /// <summary>Állapot-riasztások csendes törlése (pl. a szolgáltatás már nincs a figyeltek között).</summary>
    public void ClearConditions(string ruleId, Func<string, bool> drop)
    {
        lock (_lock)
        {
            var keys = _states.Where(kv => kv.Value.RuleId == ruleId && drop(kv.Value.AgentId)).Select(kv => kv.Key).ToList();
            foreach (var k in keys) _states.Remove(k);
            if (keys.Count > 0) SaveOffline();
        }
    }

    public string RuleName(string ruleId)
    {
        if (ruleId == InternetRuleId) return InternetRuleName;
        if (ruleId == ServiceRuleId) return ServiceRuleName;
        lock (_lock) return _rules.Concat(_hubRules).FirstOrDefault(r => r.Id == ruleId)?.Name ?? "";
    }

    /// <summary>
    /// Egy gep friss metrikainak kiertekelese az osszes ra vonatkozo szabaly ellen (a helyiek és a Központtól kapottak).
    /// groups: a gép csoportjai (csoport-hatókörhöz); siteId: csak a Központban (telephely-hatókörhöz).
    /// Visszaadja az allapotvaltas-esemenyeket (uj riasztas vagy megszunes).
    /// </summary>
    public List<AlertEvent> Evaluate(string agentId, string machineName, IReadOnlyList<Metric> metrics,
        IEnumerable<string>? groups = null, string? siteId = null)
    {
        var events = new List<AlertEvent>();
        var byKey = new Dictionary<string, double>();
        foreach (var m in metrics) byKey.TryAdd(m.Key, m.Value);
        var groupList = groups?.ToList();

        lock (_lock)
        {
            foreach (var rule in _rules.Concat(_hubRules).Where(r => r.AppliesTo(agentId, groupList, siteId)))
            {
                if (rule.IsPattern)
                {
                    foreach (var (key, val) in byKey)
                        if (rule.KeyMatches(key)) Step(rule, key, val, rule.Id + "|" + agentId + "|" + key);
                }
                else if (byKey.TryGetValue(rule.MetricKey, out var v)) Step(rule, rule.MetricKey, v, rule.Id + "|" + agentId);
            }
        }
        return events;

        void Step(AlertRule rule, string key, double val, string sKey)
        {
            if (!_states.TryGetValue(sKey, out var st))
                st = _states[sKey] = new AlertState { RuleId = rule.Id, AgentId = agentId, MetricKey = rule.IsPattern ? key : "" };
            st.LastValue = val;
            var name = rule.IsPattern ? $"{rule.Name} ({key})" : rule.Name;
            if (rule.Matches(val))
            {
                st.Streak++;
                // Csak akkor riaszt, ha eleg egymast koveto meres teljesult, es meg nem aktiv.
                if (!st.Active && st.Streak >= Math.Max(1, rule.ForSamples))
                {
                    st.Active = true;
                    st.SinceUtc = DateTime.UtcNow;
                    events.Add(new AlertEvent(name, agentId, machineName, val, rule.Threshold, rule.OpText, true, DateTime.UtcNow));
                }
            }
            else
            {
                st.Streak = 0;
                if (st.Active)
                {
                    st.Active = false;
                    st.SinceUtc = null;
                    events.Add(new AlertEvent(name, agentId, machineName, val, rule.Threshold, rule.OpText, false, DateTime.UtcNow));
                }
            }
        }
    }

    /// <summary>Egy gep torlesekor a ra vonatkozo allapotok is toroljenek.</summary>
    public void PurgeAgent(string agentId)
    {
        lock (_lock)
        {
            foreach (var k in _states.Where(kv => kv.Value.AgentId == agentId && kv.Value.RuleId != OfflineRuleId).Select(kv => kv.Key).ToList())
                _states.Remove(k);
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _rules = JsonSerializer.Deserialize<List<AlertRule>>(File.ReadAllText(_path), ProtocolJson.Options) ?? new();
        }
        catch { _rules = new(); }
        try
        {
            if (File.Exists(_offlinePath))
                foreach (var s in JsonSerializer.Deserialize<List<AlertState>>(File.ReadAllText(_offlinePath), ProtocolJson.Options) ?? new())
                    if (s.Active && s.AgentId.Length > 0)
                    {
                        if (!PersistedRules.Contains(s.RuleId)) s.RuleId = OfflineRuleId;   // 1.5.5 előtt csak kiesés került ide
                        _states[s.RuleId + "|" + s.AgentId] = s;
                    }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_rules, ProtocolJson.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }
}

/// <summary>
/// Kiesés-figyelés: percenként fut (szerveren a gépekre, a Központban a telephelyekre és gépeikre).
/// A szerver / Központ saját leállása alatt senki nem jelentkezhetett, ezért az indulás utáni első
/// küszöbnyi időben senki nem „esik ki” - különben minden újraindítás tömeges riasztást adna.
/// </summary>
public static class OfflineCheck
{
    /// <summary>A folyamat indulása (a teszt visszaállíthatja).</summary>
    public static DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <param name="Key">gép- vagy telephely-azonosító</param>
    /// <param name="Freeze">az állapota most nem ítélhető meg (pl. a telephelye kiesett): marad, ami volt</param>
    public sealed record Item(string Key, string Name, DateTime? LastSeenUtc, TimeSpan Limit, bool Freeze = false);

    /// <summary>Az állapotváltások (új kiesés / újra jelentkezik). A listában nem szereplő elemek kiesése csendben megszűnik.</summary>
    public static List<AlertEvent> Run(AlertStore alerts, IEnumerable<Item> items)
    {
        var now = DateTime.UtcNow;
        var events = new List<AlertEvent>();
        var keys = new HashSet<string>();
        foreach (var i in items)
        {
            keys.Add(i.Key);
            if (i.Freeze) continue;
            // A már jelzett kiesés az indulás után is marad, amíg az elem újra nem jelentkezik.
            var real = i.LastSeenUtc ?? StartedUtc;
            var down = now - (real > StartedUtc || alerts.IsOffline(i.Key) ? real : StartedUtc);
            if (alerts.SetOffline(i.Key, i.Name, down >= i.Limit, down.TotalMinutes, i.Limit.TotalMinutes) is { } ev) events.Add(ev);
        }
        alerts.KeepOfflineOnly(keys);
        return events;
    }

    /// <summary>A gép figyelendő-e: ha meg van adva csoportlista, csak az ott szereplő csoportok gépei.</summary>
    public static bool Watched(IReadOnlyCollection<string> groups, IEnumerable<string> agentGroups) =>
        groups.Count == 0 || agentGroups.Any(g => groups.Contains(g, StringComparer.OrdinalIgnoreCase));
}
