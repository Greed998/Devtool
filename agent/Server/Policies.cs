namespace DevToolbox.Fleet.Server;

// A CatalogItem es az AutoPolicy a kozos protokollban van (Shared/Protocol.cs), mert a Kozpont
// a sajat szabalyait lekuldi a telephelyekre.

public static class PolicyEngine
{
    /// <summary>A helyi ido az ablakban van-e. Ejfelen atnyulo ablaknal (22-04) a hajnali resz az ELOZO nap ablaka.</summary>
    public static bool InWindow(DateTime local, IReadOnlyCollection<int> days, int start, int end)
    {
        if (days.Count == 0 || start == end) return false;
        var d = (int)local.DayOfWeek; var h = local.Hour;
        if (start < end) return days.Contains(d) && h >= start && h < end;
        var prev = (d + 6) % 7;
        return (days.Contains(d) && h >= start) || (days.Contains(prev) && h < end);
    }

    /// <summary>
    /// A gepre vonatkozo, eppen aktiv szabalyok feladatai. Ugyanazt a feladatot egy szabaly
    /// 20 oran belul nem adja ki ujra (egy ablakban egyszer). Az ujrainditas csak akkor,
    /// ha a szabaly engedi ES a gep jelzi, hogy ujrainditasra var.
    /// </summary>
    /// <param name="groups">A gép tényleges csoportjai (kézi + automatikus, GroupOps.Of); null = csak a kézi csoportok.</param>
    public static List<AgentTask> Evaluate(IEnumerable<AutoPolicy> policies, EnrolledAgent agent, DateTime local, DateTime utcNow,
        IReadOnlyCollection<string>? groups = null)
    {
        var tasks = new List<AgentTask>();
        var st = agent.LastStatus;
        if (st is null || agent.Revoked) return tasks;
        var gs = groups ?? agent.Groups;

        foreach (var p in policies)
        {
            if (!p.Enabled) continue;
            if (p.Group.Length > 0 && !gs.Contains(p.Group, StringComparer.OrdinalIgnoreCase)) continue;
            if (!InWindow(local, p.Days, p.StartHour, p.EndHour)) continue;

            bool Due(string kind)
            {
                var key = p.Id + ":" + kind;
                if (agent.PolicyRuns.TryGetValue(key, out var last) && utcNow - last < TimeSpan.FromHours(20)) return false;
                agent.PolicyRuns[key] = utcNow;
                return true;
            }

            if (p.Apps && st.UpdatesAvailable > 0 && Due("apps"))
                tasks.Add(new AgentTask { Kind = AgentTaskKind.UpgradeAll });
            if (p.Windows && st.WindowsUpdates.Count > 0 && Due("windows"))
                tasks.Add(new AgentTask { Kind = AgentTaskKind.WindowsUpdateInstall, Target = "all" });
            if (p.Reboot && st.PendingReboot && Due("reboot"))
                tasks.Add(new AgentTask { Kind = AgentTaskKind.Reboot, Target = "300" });
        }
        return tasks;
    }
}
