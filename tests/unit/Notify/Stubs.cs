// A HubMailer a Központ naplóját és adatmappáját használja; a HubSetup.cs a teljes Központot húzná be, ezért itt pótoljuk.
namespace DevToolbox.Fleet.Hub
{
    public static class HubPaths { public static string DataDir => Environment.GetEnvironmentVariable("FLEET_HUB_DATA") ?? Path.GetTempPath(); }
    public static class HubLog { public static void Write(string m) => Console.WriteLine("        (Központ napló) " + m); }
}
