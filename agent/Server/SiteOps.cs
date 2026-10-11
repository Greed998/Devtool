namespace DevToolbox.Fleet.Server;

/// <summary>
/// A helyi küszöb-szabályok mentése és a Központ műveletei a telephelyen (1.5.7, a felhasználó döntése: a Server
/// szabályai a HUB-ról szerkeszthetők). A webfelület és a Központ ugyanazt az ellenőrzést kapja; a telephely marad az
/// egyetlen író (alerts.json), a Központ csak kérést küld a betárcsázás aláírt válaszában.
/// </summary>
public static class SiteOps
{
    /// <summary>A helyi szabály a Központ felé (a Központ ezt mutatja és szerkeszti).</summary>
    public static SiteAlertRule ToSite(AlertRule r) => new()
    {
        Id = r.Id, Name = r.Name, MetricKey = r.MetricKey, Op = (int)r.Op, Threshold = r.Threshold, Group = r.Group,
        AgentId = r.AgentId, ForSamples = r.ForSamples, Enabled = r.Enabled, DisplayUnit = r.DisplayUnit
    };

    /// <summary>Egy helyi szabály ellenőrzése és mentése (új vagy módosított). Hibánál a hiba szövege, különben null.</summary>
    public static string? SaveLocalRule(FleetStore store, AlertStore alerts, SiteAlertRule r, out AlertRule? saved)
    {
        saved = null;
        var key = (r.MetricKey ?? "").Trim();
        if (key.Length == 0) return "Hiányzó metrika.";
        if (!AlertRule.ValidKey(key)) return "Érvénytelen metrika-kulcs.";
        var id = (r.Id ?? "").Trim();
        if (id.StartsWith(AlertStore.HubPrefix)) return "A Központ saját szabálya itt nem módosítható.";
        if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return "Hibás szabály-azonosító.";
        var agentId = (r.AgentId ?? "").Trim();
        if (agentId.Length > 0 && store.GetAgent(agentId) is null) return "Nincs ilyen gép.";
        if (!double.IsFinite(r.Threshold)) return "Hibás küszöbérték.";
        var name = (r.Name ?? "").Trim();
        var rule = new AlertRule
        {
            Id = id.Length == 0 ? Guid.NewGuid().ToString("N") : id,
            Name = name.Length == 0 ? key : name.Length > 120 ? name[..120] : name,
            MetricKey = key, Op = (AlertOp)Math.Clamp(r.Op, 0, 2), Threshold = r.Threshold,
            AgentId = agentId, Group = agentId.Length > 0 ? "" : GroupOps.Clean(r.Group),
            ForSamples = Math.Clamp(r.ForSamples, 1, 60), Enabled = r.Enabled, DisplayUnit = AlertRule.CleanUnit(r.DisplayUnit)
        };
        if (alerts.Rules.Any(x => x.Id == rule.Id)) alerts.Update(rule); else alerts.Add(rule);
        saved = rule;
        return null;
    }

    /// <summary>A Központ egy művelete (a betárcsázás aláírt válaszából). Visszaadja a hibát, vagy null-t.</summary>
    public static string? Apply(FleetStore store, AlertStore? alerts, HubSiteOp op, string dataDir)
    {
        var by = "Központ" + (op.By.Length > 0 ? ": " + (op.By.Length > 60 ? op.By[..60] : op.By) : "");
        switch (op.Kind)
        {
            case "rule-save":
            {
                if (alerts is null || op.Rule is null) return "hiányzó szabály";
                var err = SaveLocalRule(store, alerts, op.Rule, out var saved);
                AuditLog.Write(dataDir, by, "küszöb mentése (a Központból)", err is null
                    ? $"{saved!.Name}: {saved.MetricKey} {saved.OpText} {saved.Threshold}" : $"{op.Rule.Name} – HIBA: {err}");
                return err;
            }
            case "rule-delete":
            {
                if (alerts is null) return "nincs riasztási tár";
                var r = alerts.Rules.FirstOrDefault(x => x.Id == op.Id);
                if (r is null) return "nincs ilyen szabály";
                alerts.Remove(r.Id);
                AuditLog.Write(dataDir, by, "küszöb törlése (a Központból)", r.Name);
                return null;
            }
            case "request-approve":
            case "request-deny":
                return ProgramRequests.Decide(store, op.Id, op.Kind == "request-approve", by, op.Note);
            default:
                return "ismeretlen művelet: " + op.Kind;
        }
    }
}
