namespace DevToolbox.Fleet.Server;

/// <summary>Riasztasnaplo: allapotvaltasok idorendben. A felulet ezt mutatja, es
/// innen lathato, mi tortent, amig nem neztel oda. Forgatott, max ~1 MB.</summary>
public static class AlertLog
{
    /// <summary>Az adatmappa; ures = a szerver adatmappaja. A Kozpont-szerep a sajatjat allitja be.
    /// A naplo maga a naplomappaban van (LogFolder, 1.5.4-tol: alapbol az adatmappa logs\ almappaja).</summary>
    public static string Dir { get; set; } = "";
    private static string BaseDir => Dir.Length > 0 ? Dir : ServerPaths.DataDir;
    public static string Path => LogFolder.PathOf(BaseDir, "alerts.log");

    /// <summary>Minden beirt esemeny ide is megy (a Kozpont e-mail ertesitoje); a szerveren nincs beallitva.</summary>
    public static Action<AlertEvent>? Notify { get; set; }

    private const string Raised = "RIASZTÁS", Cleared = "megszűnt ";

    public static void Write(AlertEvent ev)
    {
        LogFolder.Append(BaseDir, "alerts.log", "alerts.log.1", Line(ev) + "\n");
        try { Notify?.Invoke(ev); } catch { }
    }

    /// <summary>Egy esemeny a naplo formajaban (az e-mail is ezt hasznalja).</summary>
    public static string Line(AlertEvent ev) =>
        $"{ev.WhenUtc.ToLocalTime():yyyy.MM.dd HH:mm:ss}  {(ev.Raised ? Raised : Cleared)}  {ev.MachineName}  {ev.RuleName}  ({ev.Detail})";

    public static string Tail(int lines)
    {
        try
        {
            if (!File.Exists(Path)) return "";
            var all = File.ReadAllLines(Path);
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch { return ""; }
    }

    public sealed record Entry(string When, bool Raised, string Machine, string Rule, string Detail, string Raw);

    /// <summary>
    /// A naplo (a forgatott .1-gyel egyutt) sorokra bontva - a CSV-jelenteshez. A gep es a szabaly
    /// kozott ket szokoz van; ha egy sor nem ilyen (regi formatum), a teljes sor a reszletbe kerul.
    /// </summary>
    public static List<Entry> Entries()
    {
        var list = new List<Entry>();
        foreach (var f in new[] { Path + ".1", Path })
        {
            string[] lines;
            try { if (!File.Exists(f)) continue; lines = File.ReadAllLines(f); } catch { continue; }
            foreach (var l in lines.Where(x => x.Length > 0))
            {
                var m = System.Text.RegularExpressions.Regex.Match(l,
                    @"^(\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d)  (RIASZTÁS|megszűnt )  (.*?)  (.*)  \((.*)\)$");
                list.Add(m.Success
                    ? new Entry(m.Groups[1].Value, m.Groups[2].Value == Raised, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value, l)
                    : new Entry("", false, "", "", l, l));
            }
        }
        return list;
    }
}
