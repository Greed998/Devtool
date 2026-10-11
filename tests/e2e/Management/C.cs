using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
const string AG="http://localhost:19743", WEB="http://localhost:19380";
HttpClient Client(out CookieContainer j){j=new CookieContainer();return new HttpClient(new HttpClientHandler{CookieContainer=j}){BaseAddress=new Uri(WEB)};}
var web=Client(out _);
async Task<JsonElement> W(HttpClient c,string p,object b){var r=await c.PostAsync("/web/api/"+p,new StringContent(JsonSerializer.Serialize(b),Encoding.UTF8,"application/json"));var t=await r.Content.ReadAsStringAsync();return JsonDocument.Parse(t.Length>0?t:"{}").RootElement;}
Task<JsonElement> Web(string p,object b)=>W(web,p,b);
await web.PostAsync("/web/setup",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="SzuperEros2024xy",["password2"]="SzuperEros2024xy",["webPort"]="19380",["agentPort"]="19743"}));
var api=new HttpClient{BaseAddress=new Uri(AG)};
var srvKey=""; 
async Task<(string id,Crypto.KeyPair k)> Enroll(string name){var t=await Web("token",new{label=name});var k=Crypto.NewKeyPair();
  var e=(await (await api.PostAsJsonAsync("/api/enroll",new EnrollRequest{Token=t.GetProperty("token").GetString()!,PublicKeyPem=k.PublicKeyPem,MachineName=name},ProtocolJson.Options)).Content.ReadFromJsonAsync<EnrollResponse>(ProtocolJson.Options))!;
  srvKey=e.ServerPublicKeyPem; return (e.AgentId,k);}
var (a1,k1)=await Enroll("KONYV-1"); var (a2,k2)=await Enroll("SZERVER-1");
var g1=Guid.NewGuid().ToString(); var g2=Guid.NewGuid().ToString();
AgentStatus St(){var s=new AgentStatus{AgentVersion="1.1.2",UpdatesAvailable=2,PendingReboot=true,UpdateSource="WSUS: http://wsus:8530",WuScannedUtc=DateTime.UtcNow};
  s.Updates.Add(new AppUpdate{Id="7zip.7zip",Name="7-Zip",Installed="23.01",Available="24.08"});
  s.Installed.Add(new InstalledApp{Id="7zip.7zip",Name="7-Zip",Version="23.01",Source="winget"});
  s.Installed.Add(new InstalledApp{Id=@"ARP\Machine\X86\HPSA",Name="HP Support Assistant",Version="9.1"});
  s.WindowsUpdates.Add(new WinUpdate{UpdateId=g1,Kb="KB5044284",Title="2026-09 Összesítő frissítés",Severity="Critical",RebootRequired=true,SizeMb=512});
  s.WindowsUpdates.Add(new WinUpdate{UpdateId=g2,Kb="KB890830",Title="Kártevőirtó eszköz",SizeMb=60});return s;}
async Task<List<AgentTask>> Checkin(string id,Crypto.KeyPair k){
  var req=new CheckinRequest{AgentId=id,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Status=St()};
  req.Signature=Crypto.Sign(k.PrivateKeyPem,req.CanonicalPayload());
  var r=(await (await api.PostAsJsonAsync("/api/checkin",req,ProtocolJson.Options)).Content.ReadFromJsonAsync<CheckinResponse>(ProtocolJson.Options))!;
  if(!Crypto.Verify(srvKey,r.SignedPayload!,r.Signature!)) throw new Exception("aláírás!");
  return ProtocolJson.Deserialize<CheckinReplyBody>(r.SignedPayload!)!.Tasks;}
await Checkin(a1,k1); await Checkin(a2,k2);

Console.WriteLine("--- Állapot a felületen ---");
var ag=(await Web("agents",new{})).GetProperty("agents").EnumerateArray().First(x=>x.GetProperty("agentId").GetString()==a1);
Chk("Telepített programok: darabszám a listában, a lista külön kérésre",
  ag.GetProperty("installedCount").GetInt32()==2 && (await Web("agent-installed",new{agentId=a1})).GetProperty("installed").GetArrayLength()==2);
Chk("Windows-frissítések, forrás, függő újraindítás látszik", ag.GetProperty("windowsUpdates").GetArrayLength()==2 && ag.GetProperty("pendingReboot").GetBoolean() && ag.GetProperty("updateSource").GetString()!.StartsWith("WSUS"));

Console.WriteLine("--- Katalógus és csoportok ---");
var cs=await Web("catalog-save",new{items=new object[]{new{id="7zip.7zip",name="7-Zip",@base=true},new{id="Mozilla.Firefox",name="Firefox",@base=true},new{id="rossz\"id",name="x",@base=false}}});
Chk("Katalógus mentve, a hibás azonosító kiszűrve", cs.GetProperty("count").GetInt32()==2);
await Web("agent-groups",new{agentId=a1,groups=new[]{"Könyvelés"}});
await Web("agent-groups",new{agentId=a2,groups=new[]{"Könyvelés","Szerverek"}});

Console.WriteLine("--- Feladatok egy gépre ---");
var r1=await Web("task",new{agentIds=new[]{a1},kind="install",target="VideoLAN.VLC"});
var t1=await Checkin(a1,k1);
Chk("Telepítés leér az agenthez", r1.GetProperty("ok").GetBoolean() && t1.Any(t=>t.Kind==AgentTaskKind.InstallApp && t.Target=="VideoLAN.VLC"));
Chk("Injektálási kísérlet elutasítva", !(await Web("task",new{agentIds=new[]{a1},kind="install",target="x\" --source evil"})).GetProperty("ok").GetBoolean());
await Web("task",new{agentIds=new[]{a1},kind="uninstall",target=@"ARP\Machine\X86\HPSA"});
await Web("task",new{agentIds=new[]{a1},kind="wuinstall",target=g1+","+g2});
Chk("Hibás frissítés-azonosító elutasítva", !(await Web("task",new{agentIds=new[]{a1},kind="wuinstall",target="nem-guid"})).GetProperty("ok").GetBoolean());
var t2=await Checkin(a1,k1);
Chk("Eltávolítás (ARP) leért", t2.Any(t=>t.Kind==AgentTaskKind.UninstallApp && t.Target.StartsWith("ARP")));
Chk("Kijelölt Windows-frissítések leértek", t2.Any(t=>t.Kind==AgentTaskKind.WindowsUpdateInstall && t.Target.Contains(g1)));

Console.WriteLine("--- Csoportos műveletek ---");
var rb=await Web("task",new{group="könyvelés",kind="installbase"});
Chk("Alapcsomag a csoportra: 2 gép, 2 program", rb.GetProperty("machines").GetInt32()==2 && rb.GetProperty("tasks").GetInt32()==2, rb.ToString());
await Web("task",new{group="Szerverek",kind="reboot",target="99999"});
var ta1=await Checkin(a1,k1); var ta2=await Checkin(a2,k2);
Chk("Mindkét gép megkapta az alapcsomagot", ta1.Count(t=>t.Kind==AgentTaskKind.InstallApp)==2 && ta2.Count(t=>t.Kind==AgentTaskKind.InstallApp)==2);
Chk("Újraindítás csak a Szerverek csoportnak, 1 órára korlátozva", !ta1.Any(t=>t.Kind==AgentTaskKind.Reboot) && ta2.Any(t=>t.Kind==AgentTaskKind.Reboot && t.Target=="3600"));

Console.WriteLine("--- Szerepkör ---");
await Web("user-create",new{username="nezo1",password="NezoJelszo2024!",role=0});
var viewer=Client(out _);
await viewer.PostAsync("/web/login",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="nezo1",["password"]="NezoJelszo2024!"}));
var vr=await W(viewer,"task",new{agentIds=new[]{a1},kind="reboot",target="60"});
Chk("Néző nem adhat ki feladatot", !(vr.TryGetProperty("ok",out var ok)&&ok.GetBoolean()), vr.ToString());
Chk("Néző nem írhat szabályt", !(await W(viewer,"policy-save",new{name="x",apps=true,days=new[]{1},startHour=1,endHour=2})).TryGetProperty("ok",out var o2)||!o2.GetBoolean());

Console.WriteLine("--- Programtár: döntések (saját repó, elutasított) ---");
var inv=(await Web("inventory",new{})).GetProperty("apps").EnumerateArray().First(x=>x.GetProperty("id").GetString()=="7zip.7zip");
Chk("Gépeken talált: a repóban levő program jelölve", inv.GetProperty("state").GetString()=="repo", inv.ToString());
var dc=await Web("catalog-decide",new{decision="reject",ids=new[]{"Mozilla.Firefox"}});
Chk("Elutasítás: kikerül a repóból", dc.GetProperty("changed").GetInt32()==1 && (await Web("catalog",new{})).GetProperty("rejectedCount").GetInt32()==1, dc.ToString());
var rj=await Web("winget-catalog",new{filter="rejected"});
Chk("Az Elutasított listában ott van", rj.GetProperty("total").GetInt32()==1 && rj.GetProperty("items")[0].GetProperty("state").GetString()=="rejected", rj.ToString());
await Web("catalog-decide",new{decision="accept",ids=new[]{"VideoLAN.VLC"},names=new Dictionary<string,string>{["VideoLAN.VLC"]="VLC media player"}});
var repo=await Web("winget-catalog",new{filter="repo",q="vlc"});
Chk("Felvétel a megadott névvel (gépeken talált program)", repo.GetProperty("total").GetInt32()==1 && repo.GetProperty("items")[0].GetProperty("name").GetString()=="VLC media player", repo.ToString());
Chk("Hibás azonosító nem kerül a repóba", (await Web("catalog-decide",new{decision="accept",ids=new[]{"x\" --source evil"}})).GetProperty("changed").GetInt32()==0);
var vd=await W(viewer,"catalog-decide",new{decision="accept",ids=new[]{"Git.Git"}});
Chk("Néző nem dönthet a programtárról", !(vd.TryGetProperty("ok",out var o3)&&o3.GetBoolean()), vd.ToString());
Chk("Néző böngészheti a winget-tárat", (await W(viewer,"winget-catalog",new{filter="all"})).TryGetProperty("total",out _));

Console.WriteLine("--- Automatikus szabály ---");
Chk("Művelet nélküli szabály elutasítva", !(await Web("policy-save",new{name="üres",days=new[]{1},startHour=22,endHour=4})).GetProperty("ok").GetBoolean());
var h=DateTime.Now.Hour;
var ps=await Web("policy-save",new{name="Most",group="Könyvelés",apps=true,windows=true,reboot=true,days=new[]{0,1,2,3,4,5,6},startHour=h,endHour=(h+1)%24,enabled=true});
Chk("Szabály mentve", ps.GetProperty("ok").GetBoolean());
var ap=await Checkin(a1,k1);
Chk("Ablakban: program + Windows + újraindítás automatikusan", ap.Any(t=>t.Kind==AgentTaskKind.UpgradeAll) && ap.Any(t=>t.Kind==AgentTaskKind.WindowsUpdateInstall && t.Target=="all") && ap.Any(t=>t.Kind==AgentTaskKind.Reboot), string.Join(",",ap.Select(t=>t.Kind)));
var ap2=await Checkin(a1,k1);
Chk("Ugyanabban az ablakban nem ismétli", !ap2.Any());
var pol=(await Web("policies",new{})).GetProperty("policies")[0];
await Web("policy-delete",new{id=pol.GetProperty("id").GetString()});
Chk("Szabály törölve", (await Web("policies",new{})).GetProperty("policies").GetArrayLength()==0);

var audit=(await Web("audit",new{})).GetProperty("log").GetString()!;
Chk("Napló: feladat, csoport, katalógus, szabály, automatikus kiadás", audit.Contains("feladat kiadva") && audit.Contains("gépcsoport") && audit.Contains("programkatalógus") && audit.Contains("automatikus szabály"));
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
