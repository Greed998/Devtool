using System.Net;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

// 1.5.11 – az SNMP-fogadó megerősítése: kulcs-gyorsítótár, visszajátszás-tábla, globális terheléskorlát, értékvágás,
// és fuzzing (sok ezer csonkolt / elrontott csomag). Linuxon is fut (DPAPI nélkül). A küldő független: SharpSnmpLib.
int fail = 0;
void Chk(string n, bool ok, string i = "") { Console.WriteLine((ok ? "  OK   " : "  HIBA ") + n + (i.Length > 0 ? "  -> " + i : "")); if (!ok) fail++; }

var vars = new List<Variable> { new(new ObjectIdentifier("1.3.6.1.2.1.2.2.1.2.3"), new OctetString("GigabitEthernet0/3")) };
IAuthenticationProvider Sha1(string pw) => new SHA1AuthenticationProvider(new OctetString(pw));
var myEngine = SnmpEngine.NewEngineId();
var users = new[]
{
    SnmpUserKeys.Create("u-auth", SnmpAuthProto.Sha1, "hitelesit123", SnmpPrivProto.None, ""),
    SnmpUserKeys.Create("u-priv", SnmpAuthProto.Sha1, "hitelesit123", SnmpPrivProto.Aes128, "titkosit456"),
};
SnmpEngine NewEngine() => new(myEngine, 3, users, new[] { "public" });
OctetString RandEngine() => new(Encoding.ASCII.GetBytes("80001F88" + Guid.NewGuid().ToString("N")[..16]));
byte[] V3Trap(int id, string user, IPrivacyProvider p, OctetString engine) =>
    new TrapV2Message(VersionCode.V3, id, id, new OctetString(user), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 0, vars, p, 0x10000, engine, 1, 0).ToBytes();

Console.WriteLine("--- Kulcs-gyorsítótár: csak sikeres hitelesítés után ---");
var eng = NewEngine();
for (int i = 0; i < 300; i++)
    eng.Process(V3Trap(10 + i, "u-auth", new DefaultPrivacyProvider(Sha1("rosszjelszo1")), RandEngine()), "10.0.0.1");
Chk("Rossz kulccsal, sok véletlen engineID-vel: semmi nem kerül a gyorsítótárba", eng.CachedKeyCount == 0 && eng.WrongDigest == 300, eng.CachedKeyCount.ToString());
var dev = RandEngine();
var ok1 = eng.Process(V3Trap(1, "u-auth", new DefaultPrivacyProvider(Sha1("hitelesit123")), dev), "10.0.0.1");
Chk("Helyes kulccsal: fogadva, a kulcs gyorsítótárazva", ok1.Trap is not null && eng.CachedKeyCount == 1, ok1.Error ?? "");
var ok2 = eng.Process(V3Trap(2, "u-priv", new AESPrivacyProvider(new OctetString("titkosit456"), Sha1("hitelesit123")), dev), "10.0.0.1");
Chk("Titkosított v3 trap: fogadva", ok2.Trap is not null, ok2.Error ?? "");

Console.WriteLine("--- Visszajátszás-tábla: korlátos méret, az ismétlés továbbra is elutasítva ---");
var rep = NewEngine();
var prov = new DefaultPrivacyProvider(Sha1("hitelesit123"));
var repEngine = RandEngine();
byte[] last = Array.Empty<byte>();
var sw = System.Diagnostics.Stopwatch.StartNew();
int n = SnmpEngine.ReplayMax + 5000;
for (int i = 0; i < n; i++) { last = V3Trap(100000 + i, "u-auth", prov, repEngine); rep.Process(last, "10.0.0.2"); }
Chk($"{n} hiteles üzenet után a tábla mérete korlátos (≤ {SnmpEngine.ReplayMax})", rep.ReplayCount <= SnmpEngine.ReplayMax, rep.ReplayCount.ToString());
Chk("A legutóbbi üzenet ismétlése továbbra is elutasítva", rep.Process(last, "10.0.0.2").Error?.Contains("ismételt") == true);
Console.WriteLine($"        ({n} üzenet {sw.Elapsed.TotalSeconds:F1} mp alatt)");

Console.WriteLine("--- Értékek: hosszú bináris OctetString / nagy TimeTicks ---");
var big = new byte[5000]; new Random(1).NextBytes(big); big[0] = 0;   // nem szöveg
var bigVars = new List<Variable> { new(new ObjectIdentifier("1.3.6.1.4.1.1.1"), new OctetString(big)) };
var bv = NewEngine().Process(new TrapV2Message(5, VersionCode.V2, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.4"), 0, bigVars).ToBytes(), "x");
var shown = bv.Trap?.VarBinds.FirstOrDefault()?.Value ?? "";
Chk("5000 bájtos bináris érték: legfeljebb ~500 karakteres hexa, „…”-tal", bv.Trap is not null && shown.Length <= 502 && shown.EndsWith("…"), $"{shown.Length} karakter");
// TimeTicks (0x43) 8 bájton: régen OverflowException miatt az egész trap elveszett.
byte[] HugeTicksTrap()
{
    var vb = Ber.Seq(Ber.Seq(Ber.Oid("1.3.6.1.2.1.1.3.0"), Ber.Tlv(0x43, new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })),
                     Ber.Seq(Ber.Oid("1.3.6.1.6.3.1.1.4.1.0"), Ber.Oid("1.3.6.1.6.3.1.1.5.4")));
    return Ber.Seq(Ber.Int(1), Ber.Oct(Encoding.ASCII.GetBytes("public")), Ber.Tlv(0xA7, Ber.Concat(Ber.Int(7), Ber.Int(0), Ber.Int(0), vb)));
}
var ht = NewEngine().Process(HugeTicksTrap(), "x");
Chk("Túl nagy TimeTicks: a trap megérkezik (nem dob kivételt)", ht.Trap is { TrapName: "linkUp" }, ht.Error ?? "");

Console.WriteLine("--- Fuzzing: csonkolt / elrontott / véletlen csomagok ---");
var seeds = new List<byte[]>
{
    new TrapV1Message(VersionCode.V1, IPAddress.Parse("10.1.2.3"), new OctetString("public"), new ObjectIdentifier("1.3.6.1.4.1.9"), GenericCode.LinkDown, 0, 12345, vars).ToBytes(),
    new TrapV2Message(4711, VersionCode.V2, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.4"), 500, vars).ToBytes(),
    new InformRequestMessage(99, VersionCode.V2, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.1"), 1, vars).ToBytes(),
    V3Trap(7, "u-auth", prov, dev),
    V3Trap(8, "u-priv", new AESPrivacyProvider(new OctetString("titkosit456"), Sha1("hitelesit123")), dev),
    HugeTicksTrap(),
};
// Alapból seedenként 8000 mutáció (gyors); mélyebb körhöz: SNMP_FUZZ=200000 dotnet run -c Release
var perSeed = int.TryParse(Environment.GetEnvironmentVariable("SNMP_FUZZ"), out var pf) && pf > 0 ? pf : 8000;
var rnd = new Random(int.TryParse(Environment.GetEnvironmentVariable("SNMP_FUZZ_SEED"), out var ps) ? ps : 20261011);
var fz = NewEngine();
var generic = new Dictionary<string, int>();
int runs = 0, accepted = 0;
var fsw = System.Diagnostics.Stopwatch.StartNew();
byte[] Mutate(byte[] s)
{
    var b = s.ToArray();
    switch (rnd.Next(6))
    {
        case 0: return b[..rnd.Next(b.Length)];                                        // csonkolás
        case 1: for (int k = rnd.Next(1, 4); k > 0; k--) b[rnd.Next(b.Length)] ^= (byte)(1 << rnd.Next(8)); return b;   // bitfordítás
        case 2: b[rnd.Next(b.Length)] = (byte)rnd.Next(256); return b;                  // véletlen bájt
        case 3: { var i = rnd.Next(b.Length); b[i] = (byte)(0x80 | rnd.Next(1, 8)); return b; }   // hosszmező-rontás
        case 4: { var l = new List<byte>(b); l.Insert(rnd.Next(l.Count), (byte)rnd.Next(256)); return l.ToArray(); }   // beszúrás
        default: { var r = new byte[rnd.Next(1, 600)]; rnd.NextBytes(r); r[0] = 0x30; return r; }   // véletlen
    }
}
foreach (var seed in seeds)
    for (int i = 0; i < perSeed; i++)
    {
        var pkt = Mutate(seed);
        SnmpEngine.Result res;
        try { res = fz.Process(pkt, "10.9.9.9"); }
        catch (Exception ex) { generic["KIDOBOTT " + ex.GetType().Name] = generic.GetValueOrDefault("KIDOBOTT " + ex.GetType().Name) + 1; continue; }
        runs++;
        if (res.Trap is not null) accepted++;
        // A szigorú olvasó minden hibát SnmpFormatException-nel jelez; a „hibás csomag: <Típusnév>” üzenet generikus
        // kivételt jelent (pl. IndexOutOfRange / Overflow) - azaz egy helyet, ahol nincs explicit ellenőrzés.
        if (res.Error is { } e && e.StartsWith("hibás csomag: ") && e.EndsWith("Exception"))
            generic[e[14..]] = generic.GetValueOrDefault(e[14..]) + 1;
    }
Console.WriteLine($"        ({runs} csomag {fsw.Elapsed.TotalSeconds:F1} mp alatt, ebből {accepted} értelmes trap)");
Chk("Egyetlen csomag sem dob kivételt kifelé, és nincs generikus (ellenőrizetlen) parser-hiba",
    generic.Count == 0, string.Join(", ", generic.Select(kv => $"{kv.Key}×{kv.Value}")));
Chk("A fuzzing nem lassú (nincs elakadás / kvadratikus viselkedés)", fsw.Elapsed < TimeSpan.FromSeconds(60) * Math.Max(1, perSeed / 8000), fsw.Elapsed.ToString());

Console.WriteLine("--- Fogadó: globális terheléskorlát hamisított forrásokkal ---");
var dir = Path.Combine(Path.GetTempPath(), "snmph-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
SnmpReceiver.ManageFirewall = false;
SnmpReceiver.Init(dir, _ => { });
var cfg = new ServerConfig { SnmpEnabled = true, SnmpPort = 19171, SnmpCommunities = new() { "public" } };   // v3-felhasználó nélkül: nincs DPAPI
var err = SnmpReceiver.Apply(cfg);
Chk("A fogadó elindul", err is null, err ?? "");
var trap = new TrapV2Message(1, VersionCode.V2, new OctetString("public"), new ObjectIdentifier("1.3.6.1.6.3.1.1.5.4"), 0, vars).ToBytes();
long rl0 = SnmpReceiver.RateLimited, acc0 = SnmpReceiver.Accepted;
int flood = 2 * SnmpReceiver.GlobalPer10s + 500;
for (int i = 0; i < flood; i++)
    await SnmpReceiver.Handle(null, trap, new IPEndPoint(new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }), 162));
var acc = SnmpReceiver.Accepted - acc0; var rl = SnmpReceiver.RateLimited - rl0;
// a teszt legfeljebb két 10 mp-es ablakba esik: legfeljebb 2 × GlobalPer10s fogadható, a többi eldobódik
Chk($"{flood} csomag {flood} különböző (hamis) forrásból: legfeljebb 2×{SnmpReceiver.GlobalPer10s} fogadva, a többi korlátozva",
    acc <= 2 * SnmpReceiver.GlobalPer10s && rl >= flood - 2 * SnmpReceiver.GlobalPer10s, $"fogadva {acc}, korlátozva {rl}");
cfg.SnmpEnabled = false; SnmpReceiver.Apply(cfg);
try { Directory.Delete(dir, true); } catch { }

Console.WriteLine(fail == 0 ? "\nMINDEN TESZT ATMENT" : $"\n{fail} BUKOTT");
return fail == 0 ? 0 : 1;
