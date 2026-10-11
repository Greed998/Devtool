using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using DevToolbox.Fleet.Server;

namespace DevToolbox.Fleet.Hub;

/// <summary>
/// E-mail értesítés riasztáskor - CSAK a Központ küld (a felhasználó döntése: a telephelyeken nincs SMTP-beállítás).
/// A riasztásnapló eseményei sorba kerülnek, és percenként EGY levélben mennek ki: egy telephely kiesésekor
/// így nem jön húsz levél. Sikertelen küldésnél a következő percben újra próbálja; tartós hibánál
/// (rossz beállítás) 10 kör után eldobja a sort, hogy ne nőjön korlátlanul. A jelszó DPAPI-val titkosítva
/// tárolódik, naplóba és a felületre nem kerül.
/// </summary>
public static class HubMailer
{
    private static readonly object Gate = new();
    private static readonly List<AlertEvent> Pending = new();
    private static Func<ServerConfig>? _cfg;
    private static int _failures;

    public static DateTime? LastSentUtc { get; private set; }
    public static string LastError { get; private set; } = "";
    public static int Queued { get { lock (Gate) return Pending.Count; } }

    public static void Start(Func<ServerConfig> cfg)
    {
        _cfg = cfg;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                try { await FlushAsync(); } catch (Exception ex) { HubLog.Write("E-mail: " + ex.Message); }
            }
        });
    }

    public static void Enqueue(AlertEvent ev)
    {
        if (_cfg?.Invoke() is not { MailEnabled: true }) return;
        lock (Gate) if (Pending.Count < 1000) Pending.Add(ev);
    }

    private static async Task FlushAsync()
    {
        if (_cfg?.Invoke() is not { } c) return;
        List<AlertEvent> batch;
        lock (Gate)
        {
            if (Pending.Count == 0) return;
            if (!c.MailEnabled) { Pending.Clear(); return; }
            batch = Pending.ToList();
        }

        var raised = batch.Count(e => e.Raised);
        var subject = batch.Count == 1
            ? $"[DevToolbox] {(batch[0].Raised ? "RIASZTÁS" : "Megszűnt")}: {batch[0].MachineName} – {batch[0].RuleName}"
            : $"[DevToolbox] {raised} új riasztás, {batch.Count - raised} megszűnt";
        var sb = new StringBuilder();
        sb.Append("A Fleet HUB riasztásai").Append(c.HubDomain.Length > 0 ? $" ({c.HubDomain})" : "").Append(":\r\n\r\n");
        foreach (var ev in batch.Take(200)) sb.Append(AlertLog.Line(ev)).Append("\r\n");
        if (batch.Count > 200) sb.Append($"… és még {batch.Count - 200} esemény (a Központ riasztásnaplójában).\r\n");
        sb.Append(Footer(c));

        try
        {
            await SmtpMail.SendAsync(Settings(c), subject, sb.ToString());
            lock (Gate) Pending.RemoveRange(0, Math.Min(batch.Count, Pending.Count));
            LastSentUtc = DateTime.UtcNow; LastError = ""; _failures = 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            HubLog.Write($"E-mail küldése sikertelen ({++_failures}. próba): {ex.Message}");
            if (_failures >= 10) { lock (Gate) Pending.Clear(); _failures = 0; HubLog.Write("E-mail: 10 sikertelen próba után a sor eldobva."); }
        }
    }

    private static string Footer(ServerConfig c) =>
        "\r\n--\r\nEzt a levelet a Fleet HUB küldte automatikusan." +
        (c.HubDomain.Length > 0 ? $"\r\nKözpont: https://{c.HubDomain}/web (az admin felület belső hálóról érhető el)" : "") +
        "\r\nBeállítás: Központ → Beállítások → E-mail értesítés.\r\n";

    public static async Task<(bool Ok, string Message)> SendTestAsync(ServerConfig c, string user)
    {
        if (c.SmtpHost.Length == 0 || c.MailFrom.Length == 0 || c.MailTo.Count == 0)
            return (false, "Add meg az SMTP-szervert, a feladót és legalább egy címzettet.");
        try
        {
            await SmtpMail.SendAsync(Settings(c), "[DevToolbox] Teszt e-mail",
                $"Teszt e-mail a Fleet HUB-ból.\r\nKérte: {user}, {DateTime.Now:yyyy.MM.dd HH:mm}.\r\n\r\n" +
                (c.MailEnabled ? "Az értesítés be van kapcsolva: riasztáskor és kiesésnél ide jön levél."
                               : "Az értesítés még KI van kapcsolva - a Beállításokban kapcsold be.") + "\r\n" + Footer(c));
            LastSentUtc = DateTime.UtcNow; LastError = "";
            AuditLog.Write(HubPaths.DataDir, user, "teszt e-mail elküldve", string.Join(", ", c.MailTo));
            return (true, $"Elküldve: {string.Join(", ", c.MailTo)}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return (false, ex.Message);
        }
    }

    private static SmtpMail.Settings Settings(ServerConfig c)
    {
        var pw = c.SmtpPasswordProtected.Length == 0 ? "" : NativeDpapi.Unprotect(c.SmtpPasswordProtected)
            ?? throw new InvalidOperationException("A tárolt SMTP-jelszó nem fejthető vissza (másik gépről vagy fiókból került ide?) – add meg újra.");
        return new SmtpMail.Settings(c.SmtpHost, c.SmtpPort, c.SmtpSecurity, c.SmtpUser, pw, c.MailFrom, c.MailTo,
            c.HubDomain.Length > 0 ? c.HubDomain : Environment.MachineName);
    }

    /// <summary>
    /// A webről kapott beállítás ellenőrzése és átvétele. Hibaüzenetet ad vissza (null = rendben).
    /// A jelszó: üres = marad a régi, clearPassword = törlés; titkosítva tárolódik.
    /// </summary>
    public static string? Apply(ServerConfig c, bool enabled, string host, int port, string security, string user,
        string password, bool clearPassword, string from, string to)
    {
        host = (host ?? "").Trim(); user = (user ?? "").Trim(); from = (from ?? "").Trim();
        security = (security ?? "").Trim().ToLowerInvariant();
        // Fejléc-befecskendezés ellen: sortörés sehol nem lehet (a címek a levél fejlécébe kerülnek).
        if (new[] { host, user, from, to ?? "", password ?? "" }.Any(s => s.Contains('\r') || s.Contains('\n')))
            return "Érvénytelen karakter (sortörés) a beállításban.";
        if (security is not ("starttls" or "ssl" or "none")) return "Ismeretlen titkosítási mód.";
        if (port is < 1 or > 65535) return "Érvénytelen port.";
        if (host.Any(char.IsWhiteSpace)) return "Az SMTP-szerver neve nem tartalmazhat szóközt.";
        var list = (to ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var a in list.Append(from).Where(a => a.Length > 0))
            if (!ValidAddress(a)) return $"Érvénytelen e-mail cím: {a}";
        if (list.Count > 50) return "Legfeljebb 50 címzett adható meg.";
        if (enabled && (host.Length == 0 || from.Length == 0 || list.Count == 0))
            return "A bekapcsoláshoz add meg az SMTP-szervert, a feladót és legalább egy címzettet.";
        var hasPw = clearPassword ? false : (password ?? "").Length > 0 || c.SmtpPasswordProtected.Length > 0;
        if (security == "none" && user.Length > 0 && hasPw)
            return "Jelszavas belépés csak titkosított kapcsolaton (STARTTLS vagy SSL) – a jelszó különben olvashatóan menne át.";

        c.MailEnabled = enabled; c.SmtpHost = host; c.SmtpPort = port; c.SmtpSecurity = security;
        c.SmtpUser = user; c.MailFrom = from; c.MailTo = list;
        if (clearPassword) c.SmtpPasswordProtected = "";
        else if ((password ?? "").Length > 0) c.SmtpPasswordProtected = NativeDpapi.Protect(password!);
        return null;
    }

    private static bool ValidAddress(string a)
    {
        if (a.Length > 254 || a.Any(ch => ch is '<' or '>' or '"' || char.IsWhiteSpace(ch) || char.IsControl(ch))) return false;
        try { return new System.Net.Mail.MailAddress(a).Address == a; } catch { return false; }
    }
}

/// <summary>
/// Kis SMTP-kliens: STARTTLS (587), azonnali TLS (465) és titkosítatlan (25, belső relay); AUTH PLAIN / LOGIN.
/// Miért nem a System.Net.Mail.SmtpClient: az nem tud azonnali TLS-t (465), amit sok hazai szolgáltató kér,
/// a MailKit pedig külső függőség lenne. A tanúsítványt a Windows szokásos módon ellenőrzi (nincs kivétel).
/// </summary>
public static class SmtpMail
{
    public sealed record Settings(string Host, int Port, string Security, string User, string Password,
        string From, List<string> To, string HeloName);

    public static async Task SendAsync(Settings s, string subject, string body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;
        using var tcp = new TcpClient();
        try { await tcp.ConnectAsync(s.Host, s.Port, ct); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        { throw new InvalidOperationException($"Az SMTP-szerver nem érhető el ({s.Host}:{s.Port}): {ex.Message}"); }

        Stream stream = tcp.GetStream();
        if (s.Security == "ssl") stream = await TlsAsync(stream, s.Host, ct);
        var io = new Io(stream);
        await io.ExpectAsync(ct, 220);
        var ext = await io.CmdAsync("EHLO " + s.HeloName, ct, false, 250);

        if (s.Security == "starttls")
        {
            if (!ext.Any(l => l.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A szerver ezen a porton nem kínál STARTTLS-t – próbáld az SSL/TLS (465) módot.");
            await io.CmdAsync("STARTTLS", ct, false, 220);
            stream = await TlsAsync(stream, s.Host, ct);
            io = new Io(stream);
            ext = await io.CmdAsync("EHLO " + s.HeloName, ct, false, 250);
        }

        if (s.User.Length > 0)
        {
            var auth = ext.FirstOrDefault(l => l.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)) ?? "";
            if (auth.Contains("PLAIN", StringComparison.OrdinalIgnoreCase))
                await io.CmdAsync("AUTH PLAIN " + B64("\0" + s.User + "\0" + s.Password), ct, true, 235);
            else if (auth.Contains("LOGIN", StringComparison.OrdinalIgnoreCase))
            {
                await io.CmdAsync("AUTH LOGIN", ct, false, 334);
                await io.CmdAsync(B64(s.User), ct, true, 334);
                await io.CmdAsync(B64(s.Password), ct, true, 235);
            }
            else throw new InvalidOperationException("A szerver nem kínál PLAIN vagy LOGIN hitelesítést (titkosított kapcsolatot választottál?).");
        }

        await io.CmdAsync($"MAIL FROM:<{s.From}>", ct, false, 250);
        foreach (var to in s.To) await io.CmdAsync($"RCPT TO:<{to}>", ct, false, 250, 251);
        await io.CmdAsync("DATA", ct, false, 354);
        await io.WriteAsync(Message(s, subject, body) + "\r\n.\r\n", ct);
        await io.ExpectAsync(ct, 250);
        try { await io.CmdAsync("QUIT", ct, false, 221); } catch { }
    }

    private static async Task<Stream> TlsAsync(Stream inner, string host, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try { await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct); }
        catch (Exception ex) { throw new InvalidOperationException($"TLS-hiba az SMTP-szerverrel ({host}): {ex.Message}"); }
        return ssl;
    }

    /// <summary>A levél: UTF-8, base64 törzs (így ékezet és pont a sor elején sem gond), kódolt tárgy és feladónév.</summary>
    private static string Message(Settings s, string subject, string body)
    {
        var now = DateTimeOffset.Now;
        var date = now.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + now.ToString("zzz").Replace(":", "");
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        var lines = Enumerable.Range(0, (b.Length + 75) / 76).Select(i => b.Substring(i * 76, Math.Min(76, b.Length - i * 76)));
        return $"From: {Enc("Fleet HUB")} <{s.From}>\r\n" +
               $"To: {string.Join(", ", s.To)}\r\n" +
               $"Subject: {Enc(subject)}\r\n" +
               $"Date: {date}\r\n" +
               $"Message-ID: <{Guid.NewGuid():N}@{s.HeloName}>\r\n" +
               "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
               string.Join("\r\n", lines);
    }

    private static string Enc(string s) => "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>Soronkénti olvasás bájtonként: STARTTLS-nél a nyers adatfolyamot cseréljük, nem maradhat pufferelt adat.</summary>
    private sealed class Io(Stream s)
    {
        public async Task WriteAsync(string text, CancellationToken ct)
        {
            await s.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await s.FlushAsync(ct);
        }

        /// <param name="secret">a parancs titkot tartalmaz: hibaüzenetbe nem kerülhet</param>
        public async Task<List<string>> CmdAsync(string cmd, CancellationToken ct, bool secret, params int[] ok)
        {
            await WriteAsync(cmd + "\r\n", ct);
            try { return await ExpectAsync(ct, ok); }
            catch (InvalidOperationException ex) when (!secret)
            { throw new InvalidOperationException($"{ex.Message} (parancs: {cmd.Split(' ')[0]})"); }
        }

        public async Task<List<string>> ExpectAsync(CancellationToken ct, params int[] ok)
        {
            var lines = new List<string>();
            int code;
            while (true)
            {
                var l = await ReadLineAsync(ct);
                if (l.Length < 3 || !int.TryParse(l[..3], out code)) throw new InvalidOperationException("Érthetetlen SMTP-válasz: " + l);
                lines.Add(l.Length > 4 ? l[4..] : "");
                if (l.Length == 3 || l[3] == ' ') break;
            }
            if (!ok.Contains(code))
                throw new InvalidOperationException($"Az SMTP-szerver elutasította: {code} {string.Join(" ", lines)}".Trim());
            return lines;
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var buf = new List<byte>();
            var one = new byte[1];
            while (buf.Count < 4096)
            {
                if (await s.ReadAsync(one, ct) == 0) throw new InvalidOperationException("Az SMTP-szerver bontotta a kapcsolatot.");
                if (one[0] == '\n') break;
                if (one[0] != '\r') buf.Add(one[0]);
            }
            return Encoding.UTF8.GetString(buf.ToArray());
        }
    }
}
