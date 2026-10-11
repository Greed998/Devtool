using DevToolbox.Fleet.Server;
var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir,"fleet_admin.html"), WebUi.Dashboard("admin", Role.Admin));
File.WriteAllText(Path.Combine(outDir,"fleet_nezo.html"), WebUi.Dashboard("nezo", Role.Megfigyelo));
File.WriteAllText(Path.Combine(outDir,"hub.html"), DevToolbox.Fleet.Hub.HubWebUi.Dashboard("admin", Role.Admin));
