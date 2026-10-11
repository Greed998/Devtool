using System.Runtime.Versioning;
using System.Text;

namespace DevToolbox;

public sealed class ExportOptions
{
    public bool Hardware = true;
    public bool Smart = true;
    public bool Storage = true;
    public bool Network = true;
    public bool Events = true;
    public bool License = true;
    public bool Apps;
    public bool Scripts;

    public int EventHours = 168;
    public bool EventWarnings = true;
}

public sealed class ReportData
{
    public string MachineName = Environment.MachineName;
    public DateTime Generated = DateTime.Now;
    public string Mode = "";
    public bool Elevated;

    public List<HwSection>? Hardware;
    public List<SmartDrive>? Smart;
    public List<DriveSnapshot>? Storage;
    public List<HwSection>? Network;
    public List<NetworkInfo.Connection>? Connections;
    public ScanResult? Events;
    public List<LicenseEntry>? Licenses;
    public List<InstalledApp>? Apps;
    public List<ScriptItem>? Scripts;
}

[SupportedOSPlatform("windows")]
public static class ReportBuilder
{
    public static ReportData Collect(ExportOptions o, IProgress<string>? progress, CancellationToken token)
    {
        var d = new ReportData
        {
            Mode = AppPaths.ModeLabel,
            Elevated = HardwareInfo.IsElevated()
        };

        if (o.Hardware)
        {
            progress?.Report("Hardveradatok…");
            token.ThrowIfCancellationRequested();
            try { d.Hardware = HardwareInfo.Collect(); } catch { }
        }

        if (o.Smart)
        {
            progress?.Report("Lemezállapot (SMART)…");
            token.ThrowIfCancellationRequested();
            try { d.Smart = SmartInfo.Collect(WmiContext.Local, includeLocalSensors: false); } catch { }
        }

        if (o.Storage)
        {
            progress?.Report("Tárhely…");
            token.ThrowIfCancellationRequested();
            try { d.Storage = StorageInfo.ReadDrives(); } catch { }
        }

        if (o.Network)
        {
            progress?.Report("Hálózat…");
            token.ThrowIfCancellationRequested();
            try { d.Network = NetworkInfo.Collect(WmiContext.Local); } catch { }
            try { d.Connections = NetworkInfo.GetConnections(true); } catch { }
        }

        if (o.License)
        {
            progress?.Report("Licencek…");
            token.ThrowIfCancellationRequested();
            try { d.Licenses = LicenseInfo.Collect(WmiContext.Local); } catch { }
        }

        if (o.Apps)
        {
            progress?.Report("Telepített szoftverek…");
            token.ThrowIfCancellationRequested();
            try { d.Apps = LicenseInfo.GetInstalledApps(WmiContext.Local); } catch { }
        }

        if (o.Events)
        {
            progress?.Report("Eseménynapló elemzése…");
            token.ThrowIfCancellationRequested();
            try
            {
                d.Events = EventLogAnalyzer.Scan(
                    new[] { "System", "Application" }, o.EventHours, o.EventWarnings,
                    8000, null, token);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (o.Scripts)
        {
            progress?.Report("Szkriptek…");
            try
            {
                var lib = new ScriptLibrary();
                lib.Load();
                d.Scripts = lib.Items;
            }
            catch { }
        }

        progress?.Report("Jelentés összeállítása…");
        return d;
    }
}

// =====================================================================================

[SupportedOSPlatform("windows")]
public static class HtmlReport
{
    public static string Render(ReportData d)
    {
        var sb = new StringBuilder();
        sb.Append(Head(d));
        sb.Append(Summary(d));

        if (d.Hardware is { Count: > 0 }) sb.Append(SectionList("Rendszer és hardver", d.Hardware));
        if (d.Smart is { Count: > 0 }) sb.Append(SmartSection(d.Smart));
        if (d.Storage is { Count: > 0 }) sb.Append(StorageSection(d.Storage));
        if (d.Network is { Count: > 0 }) sb.Append(SectionList("Hálózat", d.Network));
        if (d.Connections is { Count: > 0 }) sb.Append(ConnectionSection(d.Connections));
        if (d.Events is not null) sb.Append(EventSection(d.Events));
        if (d.Licenses is { Count: > 0 }) sb.Append(LicenseSection(d.Licenses));
        if (d.Apps is { Count: > 0 }) sb.Append(AppSection(d.Apps));
        if (d.Scripts is { Count: > 0 }) sb.Append(ScriptSection(d.Scripts));

        sb.Append(Foot(d));
        return sb.ToString();
    }

    /// <summary>
    /// HTML escape. A WebUtility.HtmlEncode a 127 feletti karaktereket is szamkodos
    /// entitassa alakitja (Tarhely -> T&amp;#225;rhely), ami UTF-8 dokumentumban
    /// felesleges: olvashatatlanna teszi a forrast es noveli a fajlmeretet.
    /// Itt csak azt kodoljuk, aminek tenyleg jelentese van a HTML-ben.
    /// </summary>
    private static string E(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Osszecsukhato szekcio nyitasa. A &lt;details&gt; elem JavaScript nelkul mukodik,
    /// tehat offline es nyomtatasban is jo. A nagy tablazatok alapbol csukva vannak.
    /// </summary>
    private static string Open(string title, string? count = null, bool expanded = true)
    {
        var badge = count is null ? "" : $"<span class=\"count\">{E(count)}</span>";
        return $"<details class=\"sec\"{(expanded ? " open" : "")}><summary>{E(title)}{badge}</summary><div class=\"inner\">";
    }

    private const string Close = "</div></details>";

    private static string Head(ReportData d) => $$"""
<!DOCTYPE html>
<html lang="hu">
<head>
<meta charset="utf-8">
<title>{{E(d.MachineName)}} – gépjelentés</title>
<style>
  :root {
    --ink:#1c2028; --muted:#6b7280; --line:#e2e5ea; --bg:#f7f8fa;
    --crit:#be1e1e; --warn:#b96400; --ok:#0f7a3c; --accent:#0b63ce;
  }
  * { box-sizing:border-box; }
  body { margin:0; font-family:"Segoe UI",system-ui,-apple-system,sans-serif;
         color:var(--ink); background:var(--bg); font-size:14px; line-height:1.5; }
  .wrap { max-width:1080px; margin:0 auto; padding:0 20px 60px; }
  header { background:#202d42; color:#fff; padding:26px 0 22px; margin-bottom:22px; }
  header .wrap { padding-bottom:0; }
  header h1 { margin:0 0 4px; font-size:24px; font-weight:600; }
  header .sub { color:#aabed7; font-size:13px; }
  details.sec { background:#fff; border:1px solid var(--line); border-radius:6px;
            margin-bottom:18px; overflow:hidden; display:block; }
  details.sec > summary { margin:0; padding:12px 18px; font-size:15px; font-weight:600;
                 background:#f2f4f7; cursor:pointer; list-style:none;
                 display:flex; align-items:center; gap:10px; user-select:none; }
  details.sec > summary:hover { background:#e9edf3; }
  details.sec[open] > summary { border-bottom:1px solid var(--line); }
  details.sec > summary::-webkit-details-marker { display:none; }
  details.sec > summary::before { content:"\25B8"; color:var(--muted);
                 transition:transform .12s; font-size:13px; }
  details.sec[open] > summary::before { transform:rotate(90deg); }
  summary .count { margin-left:auto; font-weight:400; font-size:12px; color:var(--muted); }
  .toolbar { display:flex; gap:8px; margin:0 0 14px; }
  .toolbar button { font:inherit; font-size:12px; padding:5px 12px; cursor:pointer;
                 background:#fff; border:1px solid var(--line); border-radius:4px;
                 color:var(--ink); }
  .toolbar button:hover { background:#f2f4f7; }
  .inner { padding:6px 18px 16px; }
  h3 { font-size:13px; font-weight:600; color:#3b4250; margin:16px 0 6px;
       text-transform:uppercase; letter-spacing:.03em; }
  table { width:100%; border-collapse:collapse; margin:6px 0; table-layout:fixed; }
  td, th { overflow-wrap:anywhere; }
  .num { text-align:right; font-variant-numeric:tabular-nums; }
  th { text-align:left; font-size:12px; text-transform:uppercase; letter-spacing:.03em;
       color:var(--muted); font-weight:600; padding:6px 8px; border-bottom:1px solid var(--line); }
  td { padding:5px 8px; border-bottom:1px solid #f0f2f5; vertical-align:top; }
  td.k { color:var(--muted); width:250px; }
  tr:last-child td { border-bottom:none; }
  .crit { color:var(--crit); font-weight:600; }
  .warn { color:var(--warn); font-weight:600; }
  .ok   { color:var(--ok); }
  .dim  { color:var(--muted); }
  .mono { font-family:Consolas,"Courier New",monospace; }
  .key  { font-family:Consolas,monospace; font-weight:700; color:#0b3a80;
          background:#eef4fd; padding:2px 6px; border-radius:3px; }
  .cards { display:flex; flex-wrap:wrap; gap:12px; margin:10px 0; }
  .card { border:1px solid var(--line); border-radius:5px; padding:12px 14px; width:250px; }
  .card .name { font-weight:600; margin-bottom:2px; }
  .bar { height:12px; background:#edeff3; border-radius:2px; overflow:hidden; margin:8px 0 6px; }
  .bar span { display:block; height:100%; }
  .verdict { padding:10px 14px; border-radius:5px; margin:10px 0; font-weight:600; }
  .v-crit { background:#fdeaea; color:var(--crit); }
  .v-warn { background:#fdf4e3; color:var(--warn); }
  .v-ok   { background:#eaf6ee; color:var(--ok); }
  .note { color:var(--muted); font-size:13px; margin:4px 0 0; }
  footer { color:var(--muted); font-size:12px; text-align:center; padding:20px 0; }
  @media print {
    .toolbar { display:none; }
    details.sec > summary::before { content:""; }
    details:not([open]) > *:not(summary) { display:block !important; }
    body { background:#fff; font-size:11px; }
    details.sec { break-inside:avoid; border-color:#ccc; }
    header { background:#fff; color:#000; border-bottom:2px solid #000; }
    header .sub { color:#444; }
  }
</style>
</head>
<body>
<header><div class="wrap">
  <h1>{{E(d.MachineName)}} – gépjelentés</h1>
  <div class="sub">Készült: {{d.Generated:yyyy. MM. dd. HH:mm}} · DevToolbox {{AppPaths.Version}} – {{AppPaths.Tagline}}</div>
</div></header>
<div class="wrap">
<div class="toolbar">
  <button type="button" onclick="document.querySelectorAll('details.sec').forEach(function(s){s.open=true})">Összes kinyitása</button>
  <button type="button" onclick="document.querySelectorAll('details.sec').forEach(function(s){s.open=false})">Összes becsukása</button>
  <button type="button" onclick="window.print()">Nyomtatás / PDF</button>
</div>
""";

    private static string Summary(ReportData d)
    {
        var items = new List<string>();
        if (d.Smart is { Count: > 0 })
        {
            var bad = d.Smart.Count(x => x.Verdict == SmartVerdict.Kritikus);
            var warn = d.Smart.Count(x => x.Verdict == SmartVerdict.Figyelendo);
            items.Add(bad > 0 ? $"<span class='crit'>{bad} lemez hibás</span>"
                    : warn > 0 ? $"<span class='warn'>{warn} lemez figyelendő</span>"
                    : "<span class='ok'>lemezek rendben</span>");
        }
        if (d.Events is not null)
        {
            var crit = d.Events.Groups.Count(g => g.EffectiveSeverity == Severity.Kritikus);
            var ser = d.Events.Groups.Count(g => g.EffectiveSeverity == Severity.Komoly);
            items.Add(crit > 0 ? $"<span class='crit'>{crit} kritikus eseménynapló-probléma</span>"
                    : ser > 0 ? $"<span class='warn'>{ser} komoly hiba a naplóban</span>"
                    : "<span class='ok'>napló rendben</span>");
        }
        if (d.Storage is { Count: > 0 })
        {
            var full = d.Storage.Count(s => s.Total > 0 && (double)(s.Total - s.Free) / s.Total >= 0.92);
            if (full > 0) items.Add($"<span class='crit'>{full} meghajtó majdnem tele</span>");
        }

        var line = items.Count > 0 ? string.Join(" &nbsp;·&nbsp; ", items) : "—";

        return $"""
<details class="sec" open><summary>Összegzés</summary><div class="inner">
<table><colgroup><col style="width:150px"><col></colgroup>
<tr><td class="k">Állapot</td><td>{line}</td></tr>
<tr><td class="k">Mód</td><td>{E(d.Mode)}</td></tr>
<tr><td class="k">Jogosultság</td><td>{(d.Elevated ? "rendszergazda" : "<span class='warn'>normál felhasználó – egyes adatok hiányozhatnak</span>")}</td></tr>
</table>
</div></details>
""";
    }

    private static string SectionList(string title, List<HwSection> sections)
    {
        var sb = new StringBuilder();
        sb.Append(Open(title));
        foreach (var s in sections)
        {
            sb.Append($"<h3>{E(s.Title)}</h3><table>");
            foreach (var r in s.Rows)
                sb.Append($"<tr><td class=\"k\">{E(r.Key)}</td><td>{E(r.Value)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append(Close);
        return sb.ToString();
    }

    private static string SmartSection(List<SmartDrive> drives)
    {
        var sb = new StringBuilder();
        sb.Append(Open("Lemezállapot (SMART)", $"{drives.Count} meghajtó"));

        foreach (var d in drives)
        {
            var (cls, text) = d.Verdict switch
            {
                SmartVerdict.Kritikus => ("v-crit", "HIBÁS"),
                SmartVerdict.Figyelendo => ("v-warn", "FIGYELENDŐ"),
                SmartVerdict.Rendben => ("v-ok", "RENDBEN"),
                _ => ("", "NINCS ADAT")
            };

            sb.Append($"<h3>{E(d.Model)}");
            if (d.SizeBytes > 0) sb.Append($" <span class=\"dim\">({E(Fmt.Bytes(d.SizeBytes))})</span>");
            sb.Append("</h3>");
            sb.Append($"<div class=\"verdict {cls}\">{text}</div>");

            sb.Append("<ul class=\"note\">");
            foreach (var f in d.Findings) sb.Append($"<li>{E(f)}</li>");
            sb.Append("</ul>");

            if (d.ExtraSensors.Count > 0)
            {
                sb.Append("<table>");
                foreach (var (k, v) in d.ExtraSensors)
                    sb.Append($"<tr><td class=\"k\">{E(k)}</td><td>{E(v)}</td></tr>");
                sb.Append("</table>");
            }

            if (d.Attributes.Count > 0)
            {
                sb.Append("<table><tr><th>ID</th><th>Megnevezés</th><th>Nyers érték</th>" +
                          "<th>Aktuális</th><th>Küszöb</th></tr>");
                foreach (var a in d.Attributes.OrderBy(x => x.Id))
                {
                    var bad = a.BelowThreshold || (a.Critical && a.Raw > 0);
                    var c = bad ? " class=\"crit\"" : "";
                    sb.Append($"<tr{c}><td class=\"mono\">{a.Id}</td><td>{E(a.Name)}</td>" +
                              $"<td class=\"mono\">{E(a.RawDisplay)}</td>" +
                              $"<td class=\"mono\">{a.Current}</td><td class=\"mono\">{a.Threshold}</td></tr>");
                }
                sb.Append("</table>");
            }
        }

        sb.Append(Close);
        return sb.ToString();
    }

    private static string StorageSection(List<DriveSnapshot> drives)
    {
        var sb = new StringBuilder();
        sb.Append(Open("Tárhely", $"{drives.Count} meghajtó"));
        sb.Append("<div class=\"cards\">");

        foreach (var s in drives)
        {
            var used = s.Total - s.Free;
            var ratio = s.Total > 0 ? (double)used / s.Total : 0;
            var color = ratio >= 0.92 ? "#be1e1e" : ratio >= 0.80 ? "#b96400" : "#0f7a3c";

            sb.Append($"""
<div class="card">
  <div class="name">{E(s.Name.TrimEnd('\\'))} {E(s.Label)}</div>
  <div class="dim" style="font-size:12px">{E(s.Format)}</div>
  <div class="bar"><span style="width:{ratio * 100:0.#}%;background:{color}"></span></div>
  <div style="font-size:12px">{E(Fmt.Bytes(used))} / {E(Fmt.Bytes(s.Total))}
    &nbsp;·&nbsp; szabad: {E(Fmt.Bytes(s.Free))} &nbsp;·&nbsp;
    <b style="color:{color}">{ratio * 100:0}%</b></div>
</div>
""");
        }

        sb.Append("</div>" + Close);
        return sb.ToString();
    }

    private static string ConnectionSection(List<NetworkInfo.Connection> conns)
    {
        var sb = new StringBuilder();
        sb.Append(Open("Figyelt portok", $"{conns.Count} tétel", expanded: false));
        sb.Append("<table><colgroup><col style=\"width:100px\"><col><col style=\"width:120px\"></colgroup>" +
                  "<tr><th>Protokoll</th><th>Helyi cím</th><th>Állapot</th></tr>");
        foreach (var c in conns.OrderBy(x => x.Protocol).ThenBy(x => x.Local))
            sb.Append($"<tr><td>{E(c.Protocol)}</td><td class=\"mono\">{E(c.Local)}</td>" +
                      $"<td>{E(c.State)}</td></tr>");
        sb.Append("</table>" + Close);
        return sb.ToString();
    }

    private static string EventSection(ScanResult r)
    {
        var sb = new StringBuilder();
        sb.Append(Open("Eseménynapló elemzés", $"{r.Groups.Count} csoport"));

        var crit = r.Groups.Count(g => g.EffectiveSeverity == Severity.Kritikus);
        var ser = r.Groups.Count(g => g.EffectiveSeverity == Severity.Komoly);

        var (cls, txt) = crit > 0
            ? ("v-crit", $"{crit} kritikus probléma – azonnali figyelmet igényel")
            : ser > 0
                ? ("v-warn", $"{ser} komoly hiba érdemel átnézést")
                : ("v-ok", "Nem található érdemi hiba a vizsgált időszakban");
        sb.Append($"<div class=\"verdict {cls}\">{txt}</div>");

        sb.Append($"<p class=\"note\">{r.TotalEvents} esemény · {r.Critical} kritikus, " +
                  $"{r.Errors} hiba, {r.Warnings} figyelmeztetés · {r.Groups.Count} csoport</p>");

        foreach (var s in r.Skipped) sb.Append($"<p class=\"note warn\">{E(s)}</p>");

        sb.Append("<table><colgroup><col style=\"width:55px\"><col><col style=\"width:190px\">" +
                  "<col style=\"width:60px\"><col style=\"width:110px\"></colgroup>" +
                  "<tr><th class=\"num\">Db</th><th>Mi a probléma</th><th>Forrás</th>" +
                  "<th class=\"num\">ID</th><th>Utoljára</th></tr>");

        foreach (var g in r.Groups.Take(60))
        {
            var c = g.EffectiveSeverity switch
            {
                Severity.Kritikus => " class=\"crit\"",
                Severity.Komoly => " class=\"warn\"",
                Severity.Zaj => " class=\"dim\"",
                _ => ""
            };
            var head = g.Headline + (g.Subject.Length > 0 ? $" ({g.Subject})" : "");
            sb.Append($"<tr{c}><td class=\"mono num\">{g.Count}</td><td>{E(head)}");
            if (g.Diag is { } diag)
                sb.Append($"<div class=\"note\">{E(diag.Action)}</div>");
            sb.Append($"</td><td class=\"dim\">{E(g.Provider)}</td>" +
                      $"<td class=\"mono num\">{g.EventId}</td><td class=\"mono\">{g.Last:MM.dd HH:mm}</td></tr>");
        }

        sb.Append("</table>" + Close);
        return sb.ToString();
    }

    private static string LicenseSection(List<LicenseEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(Open("Licenckulcsok és aktiválás", $"{entries.Count} tétel"));
        sb.Append("<table><colgroup><col><col style=\"width:290px\"><col style=\"width:150px\">" +
                  "<col style=\"width:110px\"></colgroup>" +
                  "<tr><th>Termék</th><th>Kulcs</th><th>Állapot</th><th>Csatorna</th></tr>");

        foreach (var e in entries)
        {
            var keyHtml = e.Key is "—" || e.Key.StartsWith("XXXXX")
                ? $"<span class=\"dim mono\">{E(e.Key)}</span>"
                : $"<span class=\"key\">{E(e.Key)}</span>";
            sb.Append($"<tr><td>{E(e.Product)}<div class=\"note\">{E(e.Note)}</div></td>" +
                      $"<td>{keyHtml}</td><td>{E(e.Status)}</td><td class=\"dim\">{E(e.Channel)}</td></tr>");
        }

        sb.Append("</table>" + Close);
        return sb.ToString();
    }

    private static string AppSection(List<InstalledApp> apps)
    {
        var sb = new StringBuilder();
        sb.Append(Open("Telepített szoftverek", $"{apps.Count} db", expanded: false));
        sb.Append("<table><colgroup><col><col style=\"width:130px\"><col style=\"width:230px\">" +
                  "<col style=\"width:110px\"></colgroup>" +
                  "<tr><th>Szoftver</th><th>Verzió</th><th>Kiadó</th><th class=\"num\">Telepítve</th></tr>");
        foreach (var a in apps)
            sb.Append($"<tr><td>{E(a.Name)}</td><td class=\"mono\">{E(a.Version)}</td>" +
                      $"<td class=\"dim\">{E(a.Publisher)}</td>" +
                      $"<td class=\"mono num\">{E(a.InstallDate)}</td></tr>");
        sb.Append("</table>" + Close);
        return sb.ToString();
    }

    private static string ScriptSection(List<ScriptItem> items)
    {
        var sb = new StringBuilder();
        sb.Append(Open("Szkriptek", $"{items.Count} db", expanded: false));
        sb.Append("<table><colgroup><col><col style=\"width:110px\"><col style=\"width:150px\">" +
                  "<col style=\"width:80px\"><col style=\"width:140px\"></colgroup>" +
                  "<tr><th>Név</th><th>Típus</th><th>Kategória</th><th class=\"num\">Futások</th>" +
                  "<th>Utolsó</th></tr>");
        foreach (var i in items.OrderBy(x => x.Category).ThenBy(x => x.Name))
            sb.Append($"<tr><td>{E(i.Name)}<div class=\"note\">{E(i.Description)}</div></td>" +
                      $"<td>{E(i.KindLabel)}</td><td class=\"dim\">{E(i.Category)}</td>" +
                      $"<td class=\"mono num\">{i.RunCount}</td>" +
                      $"<td class=\"mono\">{(i.LastRun?.ToString("yyyy.MM.dd HH:mm") ?? "–")}</td></tr>");
        sb.Append("</table></div></section>");
        return sb.ToString();
    }

    private static string Foot(ReportData d) => $"""
</div>
<footer>DevToolbox {AppPaths.Version} – {AppPaths.Tagline} · {E(d.MachineName)} · {d.Generated:yyyy.MM.dd HH:mm}</footer>
</body></html>
""";
}

// =====================================================================================

[SupportedOSPlatform("windows")]
public static class TextReport
{
    public static string Render(ReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DevToolbox {AppPaths.Version} – {AppPaths.Tagline}");
        sb.AppendLine($"Gép: {d.MachineName}   Készült: {d.Generated:yyyy.MM.dd HH:mm}");
        sb.AppendLine($"Mód: {d.Mode}   Jogosultság: {(d.Elevated ? "rendszergazda" : "normál")}");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine();

        void Sections(string title, List<HwSection>? list)
        {
            if (list is null || list.Count == 0) return;
            sb.AppendLine($"### {title}");
            foreach (var s in list)
            {
                sb.AppendLine($"  [{s.Title}]");
                foreach (var r in s.Rows) sb.AppendLine($"    {r.Key,-28} {r.Value}");
            }
            sb.AppendLine();
        }

        Sections("RENDSZER ÉS HARDVER", d.Hardware);

        if (d.Smart is { Count: > 0 })
        {
            sb.AppendLine("### LEMEZÁLLAPOT (SMART)");
            foreach (var x in d.Smart)
            {
                sb.AppendLine($"  {x.Model} ({Fmt.Bytes(x.SizeBytes)}) – {x.Verdict}");
                foreach (var f in x.Findings) sb.AppendLine("    • " + f);
                foreach (var a in x.Attributes.OrderBy(a => a.Id))
                    sb.AppendLine($"      {a.Id,-4} {a.Name,-34} {a.RawDisplay,-22} akt:{a.Current} küszöb:{a.Threshold}");
                foreach (var (k, v) in x.ExtraSensors) sb.AppendLine($"      {k,-34} {v}");
            }
            sb.AppendLine();
        }

        if (d.Storage is { Count: > 0 })
        {
            sb.AppendLine("### TÁRHELY");
            foreach (var s in d.Storage)
            {
                var used = s.Total - s.Free;
                var pct = s.Total > 0 ? used * 100.0 / s.Total : 0;
                sb.AppendLine($"  {s.Name,-6} {s.Label,-18} {Fmt.Bytes(used)} / {Fmt.Bytes(s.Total)}  " +
                              $"({pct:0}%)  szabad: {Fmt.Bytes(s.Free)}");
            }
            sb.AppendLine();
        }

        Sections("HÁLÓZAT", d.Network);

        if (d.Events is not null)
        {
            sb.AppendLine("### ESEMÉNYNAPLÓ");
            sb.AppendLine($"  {d.Events.TotalEvents} esemény · {d.Events.Critical} kritikus, " +
                          $"{d.Events.Errors} hiba, {d.Events.Warnings} figyelmeztetés");
            foreach (var g in d.Events.Groups.Take(60))
            {
                sb.AppendLine($"  [{g.EffectiveSeverity}] {g.Count,4}x {g.Headline}" +
                              (g.Subject.Length > 0 ? $" ({g.Subject})" : ""));
                if (g.Diag is { } diag) sb.AppendLine($"        → {diag.Action}");
            }
            sb.AppendLine();
        }

        if (d.Licenses is { Count: > 0 })
        {
            sb.AppendLine("### LICENCKULCSOK");
            foreach (var e in d.Licenses)
            {
                sb.AppendLine($"  {e.Product}");
                sb.AppendLine($"    Kulcs:   {e.Key}");
                sb.AppendLine($"    Állapot: {e.Status}  {e.Channel}");
            }
            sb.AppendLine();
        }

        if (d.Apps is { Count: > 0 })
        {
            sb.AppendLine($"### TELEPÍTETT SZOFTVEREK ({d.Apps.Count} db)");
            foreach (var a in d.Apps) sb.AppendLine($"  {a.Name,-52} {a.Version,-16} {a.Publisher}");
            sb.AppendLine();
        }

        if (d.Scripts is { Count: > 0 })
        {
            sb.AppendLine($"### SZKRIPTEK ({d.Scripts.Count} db)");
            foreach (var i in d.Scripts.OrderBy(x => x.Category).ThenBy(x => x.Name))
                sb.AppendLine($"  {i.Name,-40} {i.KindLabel,-12} {i.Category,-16} {i.RunCount}x");
        }

        return sb.ToString();
    }
}
