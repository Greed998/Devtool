namespace DevToolbox.Fleet.Agent;

/// <summary>
/// Fajlnaplo az agentnek. Szolgaltataskent nincs konzol, ezert enelkul egy elbukott
/// regisztracio vagy kapcsolodas nyomtalanul tunne el.
///
/// C:\ProgramData\DevToolbox-Agent\agent.log - 1 MB felett atnevezodik agent.log.1-re,
/// igy legfeljebb ~2 MB-ot foglal.
/// </summary>
public static class AgentLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 1_048_576;   // 1 MB (1024 × 1024)

    public static string FilePath => Path.Combine(AgentConfig.Dir, "agent.log");

    public static void Write(string line)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(AgentConfig.Dir);

                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy.MM.dd} {line}{Environment.NewLine}");
            }
            catch { /* a naplozas hibaja nem allithatja le az agentet */ }
        }
    }

    public static string Tail(int lines)
    {
        try
        {
            if (!File.Exists(FilePath)) return "(még nincs napló)";
            var all = File.ReadAllLines(FilePath);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex) { return "(a napló nem olvasható: " + ex.Message + ")"; }
    }
}
