// A HubSetup.cs a teljes Központot húzná be: itt csak az adatmappa és a verzió kell (a teszt saját, üres mappája).
namespace DevToolbox.Fleet.Hub
{
    public static class HubPaths
    {
        public static string DataDir => Environment.GetEnvironmentVariable("FLEET_HUB_DATA") ?? Path.GetTempPath();
        public const string Version = "0.0.0-teszt";
    }
    public static class HubLog { public static void Write(string m) => Console.WriteLine("        (Központ napló) " + m); }
}
