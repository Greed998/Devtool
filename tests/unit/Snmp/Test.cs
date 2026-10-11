using System.Net;
using System.Security.Cryptography;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

int fail = 0;
void Chk(string n, bool ok, string i = "") { Console.WriteLine((ok ? "  OK   " : "  HIBA ") + n + (i.Length > 0 ? "  -> " + i : "")); if (!ok) fail++; }
string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

Console.WriteLine("--- Kriptográfia (RFC 3414 A.3 tesztvektorok, SHA-224) ---");
foreach (var n in new[] { 0, 3, 55, 56, 63, 64, 65, 1000 })
{
    var d = RandomNumberGenerator.GetBytes(n);
    if (!Sha224.Sha256ForTest(d).SequenceEqual(SHA256.HashData(d))) { Chk($"SHA-256 mag {n} bájtra", false); }
}
Chk("A saját SHA-256 mag egyezik a beépítettel", true);
Chk("SHA-224(\"abc\")", Hex(Sha224.Hash(Encoding.ASCII.GetBytes("abc"))) == "23097d223405d8228642a477bda255b32aadbce4bda0b3f7e36c9da7");
var rfcEngine = Convert.FromHexString("000000000000000000000002");
var kuMd5 = Usm.PasswordToKey(SnmpAuthProto.Md5, Encoding.ASCII.GetBytes("maplesyrup"));
var kuSha = Usm.PasswordToKey(SnmpAuthProto.Sha1, Encoding.ASCII.GetBytes("maplesyrup"));
Chk("MD5 Ku", Hex(kuMd5) == "9faf3283884e92834ebc9847d8edd963", Hex(kuMd5));
Chk("MD5 Kul", Hex(Usm.Localize(SnmpAuthProto.Md5, kuMd5, rfcEngine)) == "526f5eed9fcce26f8964c2930787d82b");
Chk("SHA-1 Ku", Hex(kuSha) == "9fb5cc0381497b3793528939ff788d5d79145211", Hex(kuSha));
Chk("SHA-1 Kul", Hex(Usm.Localize(SnmpAuthProto.Sha1, kuSha, rfcEngine)) == "6695febc9288e36282235fc7151f128497b38f3f");
Chk("BER egész oda-vissza", new[] { 0L, 127, 128, -1, -129, 65535, int.MaxValue, long.MinValue }.All(v =>
    { var b = Ber.Int(v); var r = new BerReader(b); return r.Int("x") == v; }));
Chk("OID oda-vissza", Ber.Oid(Ber.Oid("1.3.6.1.4.1.311.21.13"), new BerReader(Ber.Oid("1.3.6.1.4.1.311.21.13")).Next()) == "1.3.6.1.4.1.311.21.13");
Chk("Csonka / túl hosszú csomag: elutasítás, kivétel nélkül",
    new SnmpEngine(SnmpEngine.NewEngineId(), 1, Array.Empty<SnmpUserKeys>(), new[] { "public" }).Process(new byte[] { 0x30, 0x84, 1, 2, 3, 4 }, "x").Error is not null);

Console.WriteLine("--- v1 / v2c (független küldővel: SharpSnmpLib) ---");
var users = new List<(string Name, string Auth, string Priv, IPrivacyProvider Provider)>();
IAuthenticationProvider A(string a, string pw) => a switch
{
    "md5" => new MD5AuthenticationProvider(new OctetString(pw)), "sha1" => new SHA1AuthenticationProvider(new OctetString(pw)),
    "sha256" => new SHA256AuthenticationProvider(new OctetString(pw)), "sha384" => new SHA384AuthenticationProvider(new OctetString(pw)),
    "sha512" => new SHA512AuthenticationProvider(new OctetString(pw)), _ => DefaultAuthenticationProvider.Instance
};
IPrivacyProvider P(string p, IAuthenticationProvider auth, string pw) => p switch
{
    "des" => new DESPrivacyProvider(new OctetString(pw), auth), "3des" => new TripleDESPrivacyProvider(new OctetString(pw), auth),
    "aes128" => new AESPrivacyProvider(new OctetString(pw), auth), "aes192" => new AES192PrivacyProvider(new OctetString(pw), auth),
    "aes256" => new AES256PrivacyProvider(new OctetString(pw), auth), _ => new DefaultPrivacyProvider(auth)
};
foreach (var (a, p) in new[] { ("none", "none"), ("md5", "none"), ("sha1", "none"), ("sha256", "none"), ("sha384", "none"), ("sha512", "none"),
                               ("md5", "des"), ("sha1", "des"), ("sha1", "aes128"), ("sha256", "aes128"), ("sha512", "aes128"),
                               ("sha1", "aes192"), ("sha1", "aes256"), ("sha256", "aes256"), ("md5", "aes192"), ("sha1", "3des") })
    users.Add(($"u-{a}-{p}", a, p, P(p, A(a, "hitelesit123"), "titkosit456")));

var myEngine = SnmpEngine.NewEngineId();
// A SharpSnmpLib AES192 / AES256-ja a Cisco-féle (Reeder) kulcsbővítés: nálunk aes192c / aes256c (a sima aes192 / aes256
// a Net-SNMP-féle Blumenthal). MD5-tel a SharpSnmpLib AES256-kulcsa hibás (az utolsó 4 bájt ismétlődik) - azt nem vetjük össze.
string Ours(string p) => p is "aes192" or "aes256" ? p + "c" : p;
var keys = users.Select(u => SnmpUserKeys.Create(u.Name, Usm.Auth(u.Auth), "hitelesit123", Usm.Priv(Ours(u.Priv)), "titkosit456")).ToList();
var eng = new SnmpEngine(myEngine, 7, keys, new[] { "public", "masik" });
var vars = new List<Variable> { new(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.2.3"), new OctetString("GigabitEthernet0/3")) };

var v1 = new TrapV1Message(VersionCode.V1, IPAddress.Parse("10.1.2.3"), new OctetString("public"), new ObjectIdentifier("1.3.6.1.4.1.9"),
    GenericCode.LinkDown, 0, 12345, vars).ToBytes();
var r1 = eng.Process(v1, "10.1.2.3");
Chk("v1 linkDown: trap-OID (RFC 3584), ügynök-cím, változó", r1.Trap is { Version: "v1", TrapOid: "1.3.6.1.6.3.1.1.5.3", TrapName: "linkDown", AgentAddr: "10.1.2.3" } t1
    && t1.VarBinds.Any(v => v.Value == "GigabitEthernet0/3") && r1.Reply is null, r1.Error ?? r1.Trap?.TrapOid ?? "");
var v1e = eng.Process(new TrapV1Message(VersionCode.V1, IPAddress.Parse("10.1.2.3"), new OctetString("masik"), new ObjectIdentifier("1.3.6.1.4.1.9999"),
    GenericCode.EnterpriseSpecific, 17, 1, new List<Variable>()).ToBytes(), "x");
Chk("v1 vállalati trap: enterprise.0.specific", v1e.Trap?.TrapOid == "1.3.6.1.4.1.9999.0.17", v1e.Trap?.TrapOid ?? v1e.Error ?? "");
var bad = eng.Process(new TrapV1Message(VersionCode.V1, IPAddress.Loopback, new OctetString("rossz"), new ObjectIdentifier("1.3.6.1.4.1.9"),
    GenericCode.ColdStart, 0, 1, new List<Variable>()).ToBytes(), "x");
Chk("Ismeretlen közösségnév: elutasítva, számlálva", bad.Trap is null && eng.UnknownCommunity == 1);

var v2 = eng.Process(new TrapV2Message(4711, VersionCode.V2, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.4"), 500, vars).ToBytes(), "10.0.0.9");
Chk("v2c linkUp: trap-OID, futásidő, változó", v2.Trap is { Version: "v2c", TrapName: "linkUp" } t2 && t2.Uptime.Length > 0 && t2.VarBinds.Count == 1 && v2.Reply is null);
var inf = eng.Process(new InformRequestMessage(99, VersionCode.V2, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 1, vars).ToBytes(), "x");
var infResp = inf.Reply is null ? null : MessageFactory.ParseMessages(inf.Reply, new UserRegistry())[0];
Chk("v2c inform: nyugta (Response) ugyanazzal a request-id-vel", inf.Trap?.Inform == true && infResp is ResponseMessage rm && rm.RequestId() == 99, infResp?.GetType().Name ?? inf.Error ?? "");

Console.WriteLine("--- v3 trap: minden hitelesítés / titkosítás (a küldő a hiteles fél) ---");
var devEngine = new OctetString(Convert.FromHexString("80001F8880E9630000D61FF449"));
int mid = 1000;
foreach (var u in users)
{
    var msg = new TrapV2Message(VersionCode.V3, ++mid, mid, new OctetString(u.Name), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 0, vars,
        u.Provider, 0x10000, devEngine, 3, 0).ToBytes();
    var r = eng.Process(msg, "10.0.0.5");
    var ok = r.Trap is { Version: "v3", TrapName: "coldStart" } t && t.VarBinds.Any(v => v.Value == "GigabitEthernet0/3") && t.Security.StartsWith(u.Name);
    var replay = u.Auth == "none" || eng.Process(msg, "10.0.0.5").Error?.Contains("ismételt") == true;
    Chk($"v3 {u.Auth}/{u.Priv}: fogadva, visszafejtve{(u.Auth == "none" ? "" : ", az ismétlés elutasítva")}", ok && replay, r.Error ?? "");
}
var wrongUser = new TrapV2Message(VersionCode.V3, 5000, 5000, new OctetString("u-sha256-aes128"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 0, vars,
    P("aes128", A("sha256", "rosszjelszo1"), "titkosit456"), 0x10000, devEngine, 3, 0).ToBytes();
var wr = eng.Process(wrongUser, "x");
Chk("Rossz hitelesítési jelszó: elutasítva (hibás hitelesítés)", wr.Trap is null && eng.WrongDigest == 1, wr.Error ?? "");
var wrongPriv = new TrapV2Message(VersionCode.V3, 5001, 5001, new OctetString("u-sha256-aes128"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 0, vars,
    P("aes128", A("sha256", "hitelesit123"), "rosszprivjelszo"), 0x10000, devEngine, 3, 0).ToBytes();
var wp = eng.Process(wrongPriv, "x");
Chk("Rossz titkosítási jelszó: elutasítva", wp.Trap is null && eng.DecryptErrors >= 1, wp.Error ?? "");
var unknown = eng.Process(new TrapV2Message(VersionCode.V3, 5002, 5002, new OctetString("nincsilyen"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 0, vars,
    new DefaultPrivacyProvider(DefaultAuthenticationProvider.Instance), 0x10000, devEngine, 3, 0).ToBytes(), "x");
Chk("Ismeretlen v3-felhasználó: elutasítva", unknown.Trap is null && eng.UnknownUser == 1);
var lower = eng.Process(new TrapV2Message(VersionCode.V3, 5003, 5003, new OctetString("u-sha256-aes128"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 0, vars,
    new DefaultPrivacyProvider(A("sha256", "hitelesit123")), 0x10000, devEngine, 3, 0).ToBytes(), "x");
Chk("Alacsonyabb biztonsági szint (titkosítás nélkül) a titkosítós felhasználóval: elutasítva", lower.Trap is null && eng.UnsupportedLevel == 1);

Console.WriteLine("--- UDP-n: v2c inform, v3 inform felderítéssel (a fogadó a hiteles fél), címszűrés ---");
var dir = Path.Combine(Path.GetTempPath(), "snmp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
var raised = new List<AlertEvent>();
SnmpReceiver.ManageFirewall = false;
SnmpReceiver.Init(dir, e => { lock (raised) raised.Add(e); });
var port = 19162;
var cfg = new ServerConfig { SnmpEnabled = true, SnmpPort = port, SnmpCommunities = new() { "public" } };
foreach (var u in users.Where(x => x.Name is "u-sha256-aes128" or "u-sha1-aes256" or "u-md5-des" or "u-none-none"))
    cfg.SnmpUsers.Add(new SnmpUserConfig { Name = u.Name, Auth = u.Auth, Priv = Ours(u.Priv),
        AuthKeyProtected = u.Auth == "none" ? "" : NativeDpapi.Protect("hitelesit123"), PrivKeyProtected = u.Priv == "none" ? "" : NativeDpapi.Protect("titkosit456") });
var applyErr = SnmpReceiver.Apply(cfg);
Chk("A fogadó elindul (loopback)", applyErr is null && SnmpReceiver.Status.StartsWith("figyel") && cfg.SnmpEngineId.Length > 0 && cfg.SnmpEngineBoots == 1, applyErr ?? SnmpReceiver.Status);
var ep = new IPEndPoint(IPAddress.Loopback, port);
try
{
    Messenger.SendInform(1, VersionCode.V2, ep, new OctetString("public"), OctetString.Empty, new ObjectIdentifier("1.3.6.1.6.3.1.1.5.3"), 0, vars, 3000, null, null);
    Chk("v2c inform UDP-n: nyugtázva", true);
}
catch (Exception ex) { Chk("v2c inform UDP-n: nyugtázva", false, ex.Message); }
foreach (var name in new[] { "u-sha256-aes128", "u-sha1-aes256", "u-md5-des", "u-none-none" })
{
    var u = users.First(x => x.Name == name);
    try
    {
        var report = Messenger.GetNextDiscovery(SnmpType.InformRequestPdu).GetResponse(3000, ep);
        Messenger.SendInform(0, VersionCode.V3, ep, new OctetString(name), OctetString.Empty, new ObjectIdentifier("1.3.6.1.6.3.1.1.5.3"), 0, vars, 3000, u.Provider, report);
        Chk($"v3 inform {u.Auth}/{u.Priv}: felderítés + nyugta", true);
    }
    catch (Exception ex) { Chk($"v3 inform {u.Auth}/{u.Priv}: felderítés + nyugta", false, ex.GetType().Name + ": " + ex.Message); }
}
Thread.Sleep(200);
var (total, items) = SnmpReceiver.List("", 0);
Chk("A beérkezett informok a listában, legújabb elöl", total >= 5 && items[0].Inform, total.ToString());
Chk("A linkDown riasztást adott (alapból riasztó trap), a Központnak is", raised.Count >= 5 && raised.All(e => e.RuleName == "SNMP: linkDown") && SnmpReceiver.AlertEvents(30).Count >= 5);
var log = LogFolder.PathOf(dir, "snmp.log");
Chk("A trapek a naplómappa snmp.log fájljába kerülnek", File.Exists(log) && File.ReadAllLines(log).Length >= 5);
Chk("Próba-trap a saját portra megérkezik", await SnmpReceiver.SendTest(cfg) is null && await WaitFor(() => SnmpReceiver.List("netSnmpExample", 0).Total == 1));
cfg.SnmpAllowFrom = new() { "10.0.0.0/8" };
SnmpReceiver.Apply(cfg);
var na = SnmpReceiver.NotAllowed;
Messenger.SendTrapV2(1, VersionCode.V2, ep, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 0, vars);
Chk("Nem megengedett forrás (127.0.0.1, ha csak 10.0.0.0/8): eldobva", await WaitFor(() => SnmpReceiver.NotAllowed > na));
Chk("CIDR-ellenőrzés", SnmpReceiver.TryNet("192.168.1.0/24", out var n24) && SnmpReceiver.InNet(IPAddress.Parse("192.168.1.77"), n24) && !SnmpReceiver.InNet(IPAddress.Parse("192.168.2.1"), n24)
    && !SnmpReceiver.TryNet("192.168.1.0/33", out _) && SnmpReceiver.InNet(IPAddress.Parse("::ffff:192.168.1.5"), n24));
cfg.SnmpEnabled = false; SnmpReceiver.Apply(cfg);
Chk("Kikapcsolva a port bezárul", SnmpReceiver.Status == "kikapcsolva");

Console.WriteLine("--- Internetfigyelés (a ping kiváltva) ---");
var up = new HashSet<string>();
NetWatch.PingOverride = (h, _) => Task.FromResult(up.Contains(h) ? (long?)12 : null);
NetWatch.ConfirmDelay = TimeSpan.Zero;
NetWatch.Load(dir);
var alerts = new AlertStore(dir);
Chk("Célok ellenőrzése", NetWatch.Validate(new[] { "1.1.1.1", " 8.8.8.8 ", "dns.google" }, out var tg) is null && tg.Count == 3
    && NetWatch.Validate(new[] { "rossz cím" }, out _) is not null && NetWatch.Validate(Enumerable.Range(1, 11).Select(i => $"10.0.0.{i}"), out _) is not null);
up.Add("8.8.8.8");
var pr = await NetWatch.Probe(new[] { "1.1.1.1", "8.8.8.8", "9.9.9.9" });
Chk("Sorban: az első nem válaszol, a második igen – a harmadikat már nem kérdezi", pr.Ok && pr.Target == "8.8.8.8" && pr.NoReply.SequenceEqual(new[] { "1.1.1.1" }));
var t0 = DateTime.UtcNow;
Chk("Első ellenőrzés: van internet, nincs riasztás", NetWatch.Apply(pr, alerts, "srv", t0) is null && NetWatch.Online == true);
up.Clear();
var down = await NetWatch.Check(new[] { "1.1.1.1", "8.8.8.8" });
var evDown = NetWatch.Apply(down, alerts, "srv", t0.AddMinutes(5));
Chk("Egyik sem válaszol: kiesés, riasztás", !down.Ok && evDown is { Raised: true } && NetWatch.Online == false && alerts.ActiveStates().Any(s => s.RuleId == AlertStore.InternetRuleId));
Chk("Tartó kiesés: nincs újabb riasztás", NetWatch.Apply(down, alerts, "srv", t0.AddMinutes(6)) is null);
up.Add("1.1.1.1");
var evUp = NetWatch.Apply(await NetWatch.Check(new[] { "1.1.1.1" }), alerts, "srv", t0.AddMinutes(28));
Chk("Visszatér: megszűnés, a kiesés lezárva (23 perc)", evUp is { Raised: false } && evUp.Detail.Contains("23 perc") && NetWatch.Outages[^1].EndUtc is not null
    && !alerts.ActiveStates().Any(s => s.RuleId == AlertStore.InternetRuleId), evUp?.Detail ?? "");
var sum = NetWatch.Summary(new ServerConfig { NetWatchEnabled = true, NetWatchMinutes = 10 });
Chk("A Központnak: állapot és kiesések", sum is { Online: true, IntervalMinutes: 10 } && sum.Outages.Count == 1 && NetWatch.Summary(new ServerConfig()) is null);
NetWatch.Load(dir);
Chk("Újraindítás után az állapot megmarad", NetWatch.Online == true && NetWatch.Outages.Count == 1);
var alerts2 = new AlertStore(dir);
Chk("Riasztás-állapot újraindítás után (nincs aktív)", !alerts2.ActiveStates().Any());

Console.WriteLine("--- Központ: a telephely eseményeinek átvétele (egyszer) ---");
var site = new HubSite { SiteId = "s1", Name = "Ügyfél" };
var now = DateTime.UtcNow;
var ss = new SiteSummary
{
    Internet = new SiteInternet { Outages = new() { new() { StartUtc = now.AddDays(-2), EndUtc = now.AddDays(-2).AddMinutes(5) }, new() { StartUtc = now.AddMinutes(-40), EndUtc = now.AddMinutes(-10) }, new() { StartUtc = now.AddMinutes(-3) } } },
    SnmpEvents = new() { new() { Id = 10, AtUtc = now.AddHours(-3), Name = "régi" }, new() { Id = 11, AtUtc = now.AddMinutes(-2), Name = "linkDown" } }
};
var (o1, s1, _) = HubStore.NewSiteEvents(site, ss, now);
Chk("Első alkalommal csak a friss, lezárult kiesés és a friss trap", o1.Count == 1 && o1[0].StartUtc == now.AddMinutes(-40) && s1.Count == 1 && s1[0].Name == "linkDown");
var (o2, s2, _) = HubStore.NewSiteEvents(site, ss, now);
Chk("Másodszor semmi (nem jelez kétszer)", o2.Count == 0 && s2.Count == 0);
ss.Internet.Outages[2].EndUtc = now; ss.SnmpEvents.Add(new() { Id = 12, AtUtc = now, Name = "coldStart" });
var (o3, s3, _) = HubStore.NewSiteEvents(site, ss, now.AddMinutes(1));
Chk("Az újonnan lezárult kiesés és az új trap", o3.Count == 1 && s3.Count == 1 && s3[0].Id == 12);

try { Directory.Delete(dir, true); } catch { }
Console.WriteLine("\n" + (fail == 0 ? "MINDEN TESZT ATMENT" : $"{fail} BUKOTT"));
return fail;

static async Task<bool> WaitFor(Func<bool> cond)
{
    for (int i = 0; i < 40; i++) { if (cond()) return true; await Task.Delay(50); }
    return cond();
}
