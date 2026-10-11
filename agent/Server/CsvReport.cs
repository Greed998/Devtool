using System.Globalization;
using System.Text;

namespace DevToolbox.Fleet.Server;

/// <summary>
/// CSV-jelentések (géplista, telepített programok, elérhető frissítések, riasztásnapló, metrika-összesítő) -
/// közös a telephelyi szerver és a Központ felületén; a Központ egy „Telephely” oszlopot is ír.
///
/// Excelre szabva (magyar beállítás): pontosvessző az elválasztó, tizedesvessző, UTF-8 BOM-mal (különben az
/// ékezetek elromlanak). A gépekről jövő szövegek (programnév stb.) nem megbízhatók: az =, +, -, @ kezdetű
/// cella elé aposztróf kerül, hogy az Excel ne képletként futtassa (CSV-befecskendezés).
/// </summary>
public static class CsvReport
{
    /// <summary>Egy gép a jelentéshez - a szerver az EnrolledAgentből, a Központ a telephely összesítőjéből tölti.</summary>
    public sealed record Machine(string Site, string AgentId, string Name, string Os, string AgentVersion, bool Online, bool Revoked,
        DateTime? LastSeenUtc, IReadOnlyList<string> Groups, IReadOnlyList<AppUpdate> Updates, IReadOnlyList<WinUpdate> WindowsUpdates,
        IReadOnlyList<InstalledApp> Installed, bool PendingReboot, DateTime? WuScannedUtc = null, DateTime? EnrolledUtc = null);

    public static readonly (string Id, string Title)[] Kinds =
    {
        ("gepek", "Géplista"), ("programok", "Telepített programok"), ("frissitesek", "Elérhető frissítések"),
        ("riasztasok", "Riasztásnapló"), ("metrikak", "Metrika-összesítő (napi)")
    };

    public static byte[] Machines(IEnumerable<Machine> list, bool withSite)
    {
        var c = new Csv(withSite);
        c.Head("Gép", "Állapot", "Utoljára látva", "Operációs rendszer", "Agent-verzió", "Frissíthető programok",
               "Windows-frissítések", "Újraindításra vár", "Csoportok", "Telepített programok (db)", "Utolsó WU-keresés", "Regisztrálva");
        foreach (var m in list.OrderBy(m => m.Site).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            c.Row(m.Site, m.Name, m.Revoked ? "visszavonva" : m.Online ? "online" : "offline", m.LastSeenUtc, m.Os, new Csv.Ver(m.AgentVersion),
                  m.Updates.Count, m.WindowsUpdates.Count, m.PendingReboot, string.Join(", ", m.Groups), m.Installed.Count,
                  m.WuScannedUtc, m.EnrolledUtc);
        return c.Bytes();
    }

    public static byte[] Programs(IEnumerable<Machine> list, bool withSite)
    {
        var c = new Csv(withSite);
        c.Head("Gép", "Program", "Verzió", "Azonosító", "Forrás");
        foreach (var m in list.Where(m => !m.Revoked).OrderBy(m => m.Site).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            foreach (var p in m.Installed.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                c.Row(m.Site, m.Name, p.Name, new Csv.Ver(p.Version), p.Id, p.Source.Length > 0 ? p.Source : "rendszer");
        return c.Bytes();
    }

    public static byte[] Updates(IEnumerable<Machine> list, bool withSite)
    {
        var c = new Csv(withSite);
        c.Head("Gép", "Típus", "Név", "Azonosító / KB", "Telepített verzió", "Elérhető verzió", "Súlyosság", "Újraindítást igényel", "Méret (MB)");
        foreach (var m in list.Where(m => !m.Revoked).OrderBy(m => m.Site).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            foreach (var u in m.Updates)
                c.Row(m.Site, m.Name, "program", u.Name, u.Id, new Csv.Ver(u.Installed), new Csv.Ver(u.Available), null, null, null);
            foreach (var w in m.WindowsUpdates)
                c.Row(m.Site, m.Name, "Windows", w.Title, w.Kb, null, null, w.Severity, w.RebootRequired, w.SizeMb > 0 ? w.SizeMb : null);
        }
        return c.Bytes();
    }

    /// <summary>A Központban a „Telephely / Gép” nevet szétbontjuk; a kiesett telephely sorában a gép helyén „Fleet szerver” áll.</summary>
    public static byte[] Alerts(IEnumerable<AlertLog.Entry> entries, bool withSite)
    {
        var c = new Csv(withSite);
        c.Head("Gép", "Időpont", "Esemény", "Szabály", "Részletek");
        foreach (var e in entries)
        {
            var site = ""; var machine = e.Machine;
            if (withSite && machine.IndexOf(" / ", StringComparison.Ordinal) is var i and >= 0) { site = machine[..i]; machine = machine[(i + 3)..]; }
            c.Row(site, machine, e.When, e.When.Length == 0 ? "" : e.Raised ? "riasztás" : "megszűnt", e.Rule, e.Detail);
        }
        return c.Bytes();
    }

    /// <param name="names">agentId -> (telephely, gép); csak az itt szereplő gépek kerülnek a jelentésbe</param>
    public static byte[] Metrics(IEnumerable<MetricStore.DailyRow> rows, IReadOnlyDictionary<string, (string Site, string Machine)> names, bool withSite)
    {
        var c = new Csv(withSite);
        c.Head("Gép", "Nap", "Kategória", "Mérés", "Kulcs", "Átlag", "Minimum", "Maximum", "Mértékegység", "Minták");
        foreach (var r in rows)
            if (names.TryGetValue(r.AgentId, out var n))
                c.Row(n.Site, n.Machine, r.Day, r.Category, r.Label.Length > 0 ? r.Label : r.Key, r.Key,
                      Math.Round(r.Avg, 2), Math.Round(r.Min, 2), Math.Round(r.Max, 2), r.Unit, r.Count);
        return c.Bytes();
    }

    public static string FileName(string prefix, string kind) => $"{prefix}-{kind}-{DateTime.Now:yyyyMMdd-HHmm}.csv";

    /// <summary>Egyszerű CSV-író. withSite: az első oszlop a telephely (a Központban), különben kimarad.</summary>
    private sealed class Csv(bool withSite)
    {
        private static readonly CultureInfo Hu = CultureInfo.GetCultureInfo("hu-HU");
        private readonly StringBuilder _sb = new();

        /// <summary>Verziószám: az Excel a „3.12”-t dátummá alakítaná - ="3.12" alakban szöveg marad.</summary>
        public sealed record Ver(string Value);

        public void Head(params string[] cols) => Row(new object?[] { "Telephely" }.Concat(cols).ToArray());

        public void Row(params object?[] cells)
        {
            var c = withSite ? cells : cells.Skip(1);
            _sb.AppendJoin(';', c.Select(Cell)).Append("\r\n");
        }

        private static string Cell(object? v) => v switch
        {
            null => "",
            bool b => b ? "igen" : "nem",
            DateTime d => (d.Kind == DateTimeKind.Local ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime()).ToString("yyyy.MM.dd HH:mm", Hu),
            double x => x.ToString("0.##", Hu),
            int i => i.ToString(Hu),
            long l => l.ToString(Hu),
            Ver { Value.Length: > 0 } ver when ver.Value.All(ch => char.IsDigit(ch) || ch == '.') => "=\"" + ver.Value + "\"",
            Ver ver => Text(ver.Value),
            _ => Text(v.ToString() ?? "")
        };

        private static string Text(string s)
        {
            if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        public byte[] Bytes() => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(_sb.ToString())).ToArray();
    }
}
