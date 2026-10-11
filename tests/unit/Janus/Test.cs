using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"janus-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);

// ---- kamu Janus: rögzíti a kéréseket, a válasz kódja állítható
var got=new List<(Dictionary<string,string> H,string Body)>(); int status=200; string reply="{\"ok\":true}"; string extraHead="";
var lis=new TcpListener(IPAddress.Loopback,0);lis.Start();var port=((IPEndPoint)lis.LocalEndpoint).Port;
_=Task.Run(async()=>{while(true){TcpClient cl;try{cl=await lis.AcceptTcpClientAsync();}catch{return;}
  using(cl){var s=cl.GetStream();var buf=new List<byte>();var one=new byte[1];
    while(true){if(await s.ReadAsync(one)==0)break;buf.Add(one[0]);if(buf.Count>=4&&buf[^4]==13&&buf[^3]==10&&buf[^2]==13&&buf[^1]==10)break;}
    var head=Encoding.ASCII.GetString(buf.ToArray());var h=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    foreach(var l in head.Split("\r\n").Skip(1)){var i=l.IndexOf(':');if(i>0)h[l[..i].Trim()]=l[(i+1)..].Trim();}
    var len=int.Parse(h.GetValueOrDefault("Content-Length","0"));var body=new byte[len];var r=0;while(r<len)r+=await s.ReadAsync(body.AsMemory(r));
    lock(got)got.Add((h,Encoding.UTF8.GetString(body)));
    var rb=Encoding.UTF8.GetBytes(reply);
    await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\n{extraHead}Content-Length: {rb.Length}\r\nConnection: close\r\n\r\n"));await s.WriteAsync(rb);}}});
(Dictionary<string,string> H,string Body) Last(){lock(got)return got[^1];}
int Count(){lock(got)return got.Count;}

// ---- Központ egy telephellyel és két géppel
var hub=new HubStore(dir);var sk=Crypto.NewKeyPair();
var site=hub.Enroll(new HubEnrollRequest{Token=hub.CreateToken("t",0,0),PublicKeyPem=sk.PublicKeyPem,SiteName="Ügyfél Kft."}).SiteId;
hub.Checkin(SignedEnvelope.Create(site,new HubCheckinBody{Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Summary=new SiteSummary{SiteName="Ügyfél Kft.",
  Agents=new(){new(){AgentId="x1",MachineName="IRODA-PC01",OperatingSystem="Windows 11 Pro",AgentVersion="1.5.3"},new(){AgentId="x2",MachineName="RAKTAR-PC",OperatingSystem="Windows 10 Pro",AgentVersion="1.5.3"}}}},sk.PrivateKeyPem));
var ms=new MetricStore(dir);
ms.Insert("x1",new List<Metric>{new(){Key="mem.totalgb",Value=15.9},new(){Key="disk.C.totalgb",Value=476.94},new(){Key="disk.C.freegb",Value=120.44}},DateTime.UtcNow);
const string KEY="0123456789abcdef",SECRET="titok-Kovacs-Kft_2026";
var cfg=new ServerConfig{JanusUrl=$"http://127.0.0.1:{port}/api/fleet.php",JanusEnabled=true};
hub.SetJanus(site,KEY,NativeDpapi.Protect(SECRET));
HubJanus.Start(()=>cfg,hub,ms);
var s=hub.Sites.First();

// A Janus PHP-kódjával azonos ellenőrzés (includes/fleet.php: fleet_authenticate)
string Why((Dictionary<string,string> H,string Body) q){
  var k=q.H.GetValueOrDefault("X-Janus-Key","");var ts=q.H.GetValueOrDefault("X-Janus-Timestamp","");var n=q.H.GetValueOrDefault("X-Janus-Nonce","");var sig=q.H.GetValueOrDefault("X-Janus-Signature","");
  if(!Regex.IsMatch(k,"^[a-f0-9]{16}$")||!Regex.IsMatch(ts,"^[0-9]+$")||!Regex.IsMatch(n,"^[A-Za-z0-9_-]{16,48}$")||!Regex.IsMatch(sig,"^[a-f0-9]{64}$"))return "hibás fejlécek";
  if(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-long.Parse(ts))>300)return "időbélyeg";
  using var h=new HMACSHA256(Encoding.UTF8.GetBytes(SECRET));
  var calc=Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(ts+"\n"+n+"\n"+q.Body))).ToLowerInvariant();
  return calc==sig?"":"aláírás";}

Console.WriteLine("--- Kapcsolat-próba (életjel) ---");
var (ok,msg)=await HubJanus.Test(s);
Chk("A próba sikeres",ok,msg);
Chk("A Janus ellenőrzése átmegy (kulcs, időbélyeg, nonce, HMAC)",Why(Last())=="",Why(Last()));
Chk("Életjel a törzsben",Last().Body=="{\"type\":\"heartbeat\"}",Last().Body);
Chk("JSON tartalomtípus, POST a megadott útvonalra",Last().H.GetValueOrDefault("Content-Type","").StartsWith("application/json"));
var n1=Last().H["X-Janus-Nonce"];await HubJanus.Test(s);
Chk("Kérésenként új nonce (ismétlés-védelem)",Last().H["X-Janus-Nonce"]!=n1);

Console.WriteLine("--- Géplista ---");
await HubJanus.Inventory(true);
var inv=JsonDocument.Parse(Last().Body).RootElement;
Chk("inventory, mindkét gép",inv.GetProperty("type").GetString()=="inventory"&&inv.GetProperty("machines").GetArrayLength()==2,Last().Body);
var m1=inv.GetProperty("machines").EnumerateArray().First(m=>m.GetProperty("uid").GetString()=="x1");
Chk("A gép adatai (név, OS, agent, RAM, lemez)",m1.GetProperty("hostname").GetString()=="IRODA-PC01"&&m1.GetProperty("os").GetString()=="Windows 11 Pro"
  &&m1.GetProperty("agent_version").GetString()=="1.5.3"&&m1.GetProperty("ram_gb").GetDouble()==15.9&&m1.GetProperty("disk_total_gb").GetDouble()==476.9&&m1.GetProperty("disk_free_gb").GetDouble()==120.4,m1.ToString());
Chk("Aláírás a géplistán is",Why(Last())=="");
var c0=Count();await HubJanus.Inventory(false);
Chk("Változatlan géplista 15 percen belül nem megy újra",Count()==c0);

Console.WriteLine("--- Riasztás, megszűnés, kiesés ---");
HubJanus.Enqueue(new AlertEvent("Lemez tele (disk.C.usedpct)","x1","Ügyfél Kft. / IRODA-PC01",93.2,90,">",true,DateTime.UtcNow));
await HubJanus.Flush();
var al=JsonDocument.Parse(Last().Body).RootElement;
Chk("alert: gép, kulcs, súlyosság, üzenet",al.GetProperty("type").GetString()=="alert"&&al.GetProperty("uid").GetString()=="x1"&&al.GetProperty("hostname").GetString()=="IRODA-PC01"
  &&al.GetProperty("key").GetString()=="kuszob:Lemez tele (disk.C.usedpct)"&&al.GetProperty("severity").GetString()=="figyelmeztetes"&&al.GetProperty("message").GetString()!.Contains("> 90")&&al.GetProperty("message").GetString()!.Contains("93"),Last().Body);
HubJanus.Enqueue(new AlertEvent("Lemez tele (disk.C.usedpct)","x1","Ügyfél Kft. / IRODA-PC01",50,90,">",false,DateTime.UtcNow));
await HubJanus.Flush();
al=JsonDocument.Parse(Last().Body).RootElement;
Chk("Megszűnés: ugyanaz a kulcs, resolved, üzenet \"-\"",al.GetProperty("resolved").GetBoolean()&&al.GetProperty("key").GetString()=="kuszob:Lemez tele (disk.C.usedpct)"&&al.GetProperty("message").GetString()=="-");
HubJanus.Enqueue(new AlertEvent(AlertStore.OfflineRuleName,"site:"+site,"Ügyfél Kft. / Fleet szerver",20,15,"≥",true,DateTime.UtcNow,"20 perce nem jelentkezett"));
await HubJanus.Flush();
al=JsonDocument.Parse(Last().Body).RootElement;
Chk("Telephely kiesése: kritikus, gép nélkül",al.GetProperty("key").GetString()=="telephely-kieses"&&al.GetProperty("severity").GetString()=="kritikus"&&!al.TryGetProperty("uid",out _),Last().Body);
HubJanus.Enqueue(new AlertEvent("x","idegen-gep","?",1,1,">",true,DateTime.UtcNow));
c0=Count();await HubJanus.Flush();
Chk("Ismeretlen gép riasztása nem megy ki",Count()==c0);

Console.WriteLine("--- Hibakezelés ---");
status=401;reply="{\"ok\":false,\"error\":\"Érvénytelen aláírás.\"}";
(ok,msg)=await HubJanus.Test(s);
Chk("Elutasított próba: a Janus hibaüzenete látszik",!ok&&msg.Contains("401")&&msg.Contains("Érvénytelen aláírás"),msg);
Chk("Az állapotban ott a hiba",HubJanus.State[site].Error.Contains("Érvénytelen aláírás"));
status=500;reply="<html>hiba</html>";
HubJanus.Enqueue(new AlertEvent("CPU","x2","Ügyfél Kft. / RAKTAR-PC",99,90,">",true,DateTime.UtcNow));
c0=Count();await HubJanus.Flush();
Chk("Sikertelen küldés: a riasztás a sorban marad",Count()==c0+1);
status=200;reply="{\"ok\":true}";await HubJanus.Flush();
Chk("Újrapróbálva elmegy",Count()==c0+2&&JsonDocument.Parse(Last().Body).RootElement.GetProperty("uid").GetString()=="x2");

Console.WriteLine("--- Biztonsági átnézés (1.5.5): átirányítás, nem-Janus válasz, óraeltérés, internet-kiesés ---");
status=200;reply="<html>Bejelentkezés a vendég-wifire</html>";
(ok,msg)=await HubJanus.Test(s);
Chk("200-as, de nem a Janus válasza (proxy / captive portal): NEM siker",!ok&&msg.Contains("nem a Janus API"),msg);
status=307;reply="";extraHead=$"Location: http://127.0.0.1:{port}/masik\r\n";c0=Count();
(ok,msg)=await HubJanus.Test(s);
await Task.Delay(200);
Chk("Átirányítást nem követ (az aláírt kérés nem megy más címre)",!ok&&msg.Contains("átirányít")&&Count()==c0+1,msg+" | kérések: "+(Count()-c0));
status=200;extraHead="";reply="{\"ok\":true,\"server_time\":"+(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-600)+"}";
(ok,msg)=await HubJanus.Test(s);
Chk("Óraeltérés figyelmeztetés a próbánál",ok&&msg.Contains("eltér"),msg);
reply="{\"ok\":true}";c0=Count();
HubJanus.Enqueue(new AlertEvent(HubJanus.InternetOutageRule,"site:"+site,"Ügyfél Kft. / Fleet szerver",23,0,"",true,DateTime.UtcNow,"az internet nem volt elérhető: 10:05 – 10:28 (23 perc)"));
await HubJanus.Flush();
List<JsonElement> lastTwo;lock(got)lastTwo=got.Skip(got.Count-2).Select(g=>JsonDocument.Parse(g.Body).RootElement).ToList();
Chk("Lezárult internet-kiesés: tájékoztatás (info, nincs jegy) és rögtön megszűnés",Count()==c0+2&&lastTwo[0].GetProperty("severity").GetString()=="info"
  &&lastTwo[0].GetProperty("key").GetString()=="telephely-internet"&&lastTwo[1].GetProperty("resolved").GetBoolean());
Chk("Felhasználónév / jelszó a címben: elutasítva",HubJanus.ValidUrl("https://u:p@janus.poltoli.hu/api/fleet.php")!=null&&HubJanus.ValidUrl("https://janus.poltoli.hu/api/fleet.php#x")!=null);
cfg.JanusEnabled=false;c0=Count();
HubJanus.Enqueue(new AlertEvent("CPU","x2","Ügyfél Kft. / RAKTAR-PC",99,90,">",true,DateTime.UtcNow));await HubJanus.Flush();await HubJanus.Inventory(true);
Chk("Kikapcsolva semmi nem megy ki",Count()==c0);

Console.WriteLine("--- Cím-ellenőrzés ---");
Chk("https elfogadva, http csak localhost",HubJanus.ValidUrl("https://janus.poltoli.hu/api/fleet.php")==null&&HubJanus.ValidUrl("http://127.0.0.1/api/fleet.php")==null
  &&HubJanus.ValidUrl("http://janus.poltoli.hu/api/fleet.php")!=null&&HubJanus.ValidUrl("ftp://x")!=null);

lis.Stop();
try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
