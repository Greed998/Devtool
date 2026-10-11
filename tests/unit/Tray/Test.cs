using System.Security.Cryptography;
using System.Text.Json;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var root=Path.Combine(Path.GetTempPath(),"tray-"+Guid.NewGuid().ToString("N"));
var srv=Path.Combine(root,"srv");var hubDir=Path.Combine(root,"hub");Directory.CreateDirectory(srv);Directory.CreateDirectory(hubDir);
Environment.SetEnvironmentVariable("FLEET_DATA",srv);Environment.SetEnvironmentVariable("FLEET_HUB_DATA",hubDir);

var st=new FleetStore(srv);
var kp=Crypto.NewKeyPair();
var aid=st.Enroll(new EnrollRequest{Token=st.CreateEnrollmentToken("t",60,5),PublicKeyPem=kp.PublicKeyPem,MachineName="PC1"}).AgentId;
var users=new UserStore(srv);
const string PW="Erős-Jelszó-2026!x";
Chk("Felhasználók létrehozva",users.Create("kezelo1",PW,Role.Kezelo).Ok&&users.Create("nezo1",PW,Role.Megfigyelo).Ok);
TrayApi.Users=users;TrayApi.SessionKey=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var cfg=new ServerConfig();
cfg.SoftwareCatalog.Add(new CatalogItem{Id="7zip.7zip",Name="7-Zip"});
cfg.HubCatalogItems.Add(new CatalogItem{Id="Mozilla.Firefox",Name="Firefox"});
cfg.CatalogRejected.Add(new CatalogItem{Id="Bad.App",Name="Rossz"});
ProgramRequests.Init(srv);

AgentUserRequest Req(string kind,object data,string user=@"CEG\anna"){var r=new AgentUserRequest{AgentId=aid,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Kind=kind,User=user,
  Data=JsonSerializer.Serialize(data,ProtocolJson.Options)};r.Signature=Crypto.Sign(kp.PrivateKeyPem,r.CanonicalPayload());return r;}
var sigOk=true;
AgentUserReplyBody Send(AgentUserRequest r){var resp=TrayApi.Handle(st,cfg,r);sigOk&=Crypto.Verify(st.ServerKeys.PublicKeyPem,resp.SignedPayload,resp.Signature);
  return JsonSerializer.Deserialize<AgentUserReplyBody>(resp.SignedPayload,ProtocolJson.Options)!;}
AgentUserReplyBody Call(string kind,object data)=>Send(Req(kind,data));
JsonElement D(AgentUserReplyBody b)=>JsonDocument.Parse(b.Data.Length>0?b.Data:"{}").RootElement;

Console.WriteLine("--- Aláírt csatorna ---");
var cat=Call("catalog",new{});
Chk("Katalógus: a helyi és a Központ repója, az elutasított nélkül",cat.Ok&&D(cat).GetProperty("items").EnumerateArray().Select(x=>x.GetProperty("id").GetString()).OrderBy(x=>x).SequenceEqual(new[]{"7zip.7zip","Mozilla.Firefox"}),cat.Data);
var once=Req("catalog",new{});Send(once);
Chk("Ugyanaz a kérés másodszor nem fogadható el (nonce)",!Send(once).Ok);
var forged=Req("catalog",new{});forged.Signature=Crypto.Sign(Crypto.NewKeyPair().PrivateKeyPem,forged.CanonicalPayload());
Chk("Idegen kulccsal aláírt kérés elutasítva",!Send(forged).Ok);
var tamper=Req("install",new{id="7zip.7zip"});tamper.User="CEG\\admin";
Chk("Utólag átírt felhasználó: az aláírás nem egyezik",!Send(tamper).Ok);
Chk("A válaszok a szerver kulcsával aláírtak, és a kérés nonce-ához kötöttek",sigOk&&cat.RequestNonce.Length>0&&cat.AgentId==aid);

Console.WriteLine("--- Sima felhasználó: repóból azonnal, más program kérése ---");
var ins=Call("install",new{id="7zip.7zip"});
Chk("Repó-program: azonnal települ",ins.Ok&&ProgramRequests.List(aid).Any(i=>i.PackageId=="7zip.7zip"&&i.State=="installing"&&i.TaskId.Length>0&&i.User==@"CEG\anna"),ins.Message);
Chk("Ugyanaz újra: nem duplázódik",Call("install",new{id="7zip.7zip"}).Ok&&ProgramRequests.List(aid).Count(i=>i.PackageId=="7zip.7zip")==1);
Chk("Repón kívüli program közvetlenül nem telepíthető",!Call("install",new{id="Other.App"}).Ok);
Chk("Elutasított (letiltott) program nem kérhető",!Call("request",new{id="Bad.App",name="Rossz"}).Ok);
Chk("Hibás azonosító (kapcsoló) nem kérhető",!Call("request",new{id="--force",name="x"}).Ok);
var rq=Call("request",new{id="Some.App",name="Valami",reason="kell a munkához"});
var rqId=ProgramRequests.List(aid).FirstOrDefault(i=>i.PackageId=="Some.App")?.Id??"";
Chk("Más program: döntésre vár, indoklással",rq.Ok&&ProgramRequests.List(aid).Any(i=>i.Id==rqId&&i.State=="pending"&&i.Reason=="kell a munkához"),rq.Message);
var mine=Call("requests",new{});
Chk("Kéréseim: a gép kérései",mine.Ok&&D(mine).GetProperty("items").GetArrayLength()==2);

Console.WriteLine("--- Döntés a Központból (HubSiteOp) és az eredmény ---");
Chk("Jóváhagyás a Központból",SiteOps.Apply(st,null,new HubSiteOp{Kind="request-approve",Id=rqId,By="admin"},srv) is null&&ProgramRequests.List(aid).First(i=>i.Id==rqId) is {State:"installing",DecidedBy:"Központ: admin"});
Chk("Ugyanarról másodszor nem dönthet",SiteOps.Apply(st,null,new HubSiteOp{Kind="request-deny",Id=rqId},srv) is not null);
var task=ProgramRequests.List(aid).First(i=>i.Id==rqId).TaskId;
ProgramRequests.OnResult(new TaskResult{AgentId=aid,TaskId=task,Success=false,Output="valami\nwinget: 0x8a150011 hiba"});
Chk("A telepítés eredménye lezárja (hibával, az utolsó sorral)",ProgramRequests.List(aid).First(i=>i.Id==rqId) is {State:"failed"} x&&x.Note.Contains("0x8a150011"));
ProgramRequests.OnResult(new TaskResult{AgentId="mas-gep",TaskId=ProgramRequests.List(aid).First(i=>i.PackageId=="7zip.7zip").TaskId,Success=true});
Chk("Más gép eredménye nem zár le kérést",ProgramRequests.List(aid).First(i=>i.PackageId=="7zip.7zip").State=="installing");
ProgramRequests.Init(srv);
Chk("A kérések lemezen is megmaradnak",ProgramRequests.List(aid).Count==2);

Console.WriteLine("--- Belépés adminként: kihívás–válasz, a jelszó nem megy át ---");
string Login(string user,string pw,out AgentUserReplyBody res){var s=Call("login-start",new{username=user});if(!s.Ok){res=s;return "";}var d=D(s);
  var ch=d.GetProperty("challenge").GetString()!;var key=TrayLogin.Key(pw,d.GetProperty("salt").GetString()!,d.GetProperty("iterations").GetInt32());
  res=Call("login",new{username=user,challenge=ch,proof=TrayLogin.Proof(key,aid,user,ch)});return ch;}
Login("kezelo1",PW,out var ok1);
var token=ok1.Ok?D(ok1).GetProperty("token").GetString()!:"";
Chk("Helyes jelszó: belépés, jeggyel",ok1.Ok&&token.Length>0,ok1.Message);
Chk("A kérésben nincs jelszó",!Req("login",new{username="kezelo1",challenge="x",proof="y"}).Data.Contains(PW));
var ch2=Login("kezelo1","rossz-jelszo",out var bad1);
Chk("Rossz jelszó: elutasítva",!bad1.Ok);
Chk("A kihívás egyszer használható",!Call("login",new{username="kezelo1",challenge=ch2,proof="x"}).Ok);
Login("nezo1",PW,out var nz);
Chk("Megfigyelő nem léphet be adminként",!nz.Ok&&nz.Message.Contains("kezelői"),nz.Message);
var fake=Call("login-start",new{username="nincsilyen"});
Chk("Nem létező felhasználónál is ad kihívást (nem kérdezhető ki)",fake.Ok&&D(fake).GetProperty("salt").GetString()!.Length>0);
var adm=Call("admin-install",new{token,id="Other.App",name="Más"});
Chk("Admin-jeggyel bármely program telepíthető",adm.Ok&&ProgramRequests.List(aid).Any(i=>i.PackageId=="Other.App"&&i.State=="installing"&&i.DecidedBy=="kezelo1 (tálca)"),adm.Message);
Chk("Hamis jeggyel nem",!Call("admin-install",new{token="aGFtaXM=",id="X.Y"}).Ok);
users.SetRole("kezelo1",Role.Megfigyelo);
Chk("A jog elvétele után a jegy sem jó",!Call("admin-requests",new{token}).Ok);
for(var k=0;k<5;k++)Login("kezelo1","rossz"+k,out _);
users.SetRole("kezelo1",Role.Kezelo);
Login("kezelo1",PW,out var locked);
Chk("5 hibás próbálkozás után zárolva (a jó jelszóval is)",!locked.Ok&&locked.Message.Contains("Túl sok"),locked.Message);
cfg.TrayEnabled=false;
Chk("Kikapcsolt tálca: minden kérés elutasítva",!Call("catalog",new{}).Ok);
cfg.TrayEnabled=true;

Console.WriteLine("--- A Központ szabály-műveletei a telephelyen ---");
var alerts=new AlertStore(srv);
Chk("Szabály mentése a Központból",SiteOps.Apply(st,alerts,new HubSiteOp{Kind="rule-save",By="admin",Rule=new SiteAlertRule{Id="abc123",Name="CPU",MetricKey="cpu.load",Threshold=90,ForSamples=3}},srv) is null
  &&alerts.Rules.SingleOrDefault() is {Id:"abc123",Threshold:90,ForSamples:3});
Chk("Módosítás ugyanazzal az azonosítóval",SiteOps.Apply(st,alerts,new HubSiteOp{Kind="rule-save",Rule=new SiteAlertRule{Id="abc123",Name="CPU",MetricKey="cpu.load",Threshold=95}},srv) is null&&alerts.Rules.Single().Threshold==95);
Chk("Hibás kulcs elutasítva",SiteOps.Apply(st,alerts,new HubSiteOp{Kind="rule-save",Rule=new SiteAlertRule{MetricKey="cpu load; x"}},srv) is not null);
Chk("Központ-előtagú azonosító elutasítva",SiteOps.Apply(st,alerts,new HubSiteOp{Kind="rule-save",Rule=new SiteAlertRule{Id="hub-x",MetricKey="cpu.load"}},srv) is not null);
Chk("Ismeretlen gépre szóló szabály elutasítva",SiteOps.Apply(st,alerts,new HubSiteOp{Kind="rule-save",Rule=new SiteAlertRule{MetricKey="cpu.load",AgentId="nincs"}},srv) is not null);
Chk("Törlés",SiteOps.Apply(st,alerts,new HubSiteOp{Kind="rule-delete",Id="abc123"},srv) is null&&alerts.Rules.Count==0);
Chk("A műveleti naplóban a Központ felhasználója",AuditLog.Tail(srv,50).Contains("Központ: admin"));
var sum=new SiteSummary{LocalRules=new(),ProgramRequests=new(){new ProgramRequestInfo{Id="r1",State="pending"}}};
HubStore.ApplySiteOpLocally(sum,new HubSiteOp{Kind="rule-save",Rule=new SiteAlertRule{Id="q",MetricKey="mem.usedpct"}});
HubStore.ApplySiteOpLocally(sum,new HubSiteOp{Kind="request-deny",Id="r1",By="b"});
Chk("A Központ felülete azonnal az új állapotot mutatja",sum.LocalRules.Count==1&&sum.ProgramRequests[0].State=="denied");

Console.WriteLine("--- Napló-felküldés a Központba (bájt-mutató, forgatás) ---");
LogShip.Init(srv);
var (c0,cm0)=LogShip.Take(srv);cm0?.Invoke();   // a teszt eddigi naplói
for(var k=1;k<=3;k++)AuditLog.Write(srv,"u","sor"+k);
var (c1,cm1)=LogShip.Take(srv);
Chk("Az új sorok mennek fel",c1.Count==1&&c1[0].Log=="audit"&&c1[0].Text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length==3,string.Join("|",c1.Select(x=>x.Log)));
var (c1b,_)=LogShip.Take(srv);
Chk("Sikeres válasz nélkül nem lép tovább (újra ugyanaz)",c1b.Count==1&&c1b[0].Text==c1[0].Text);
cm1!();
Chk("A válasz után nincs új",LogShip.Take(srv).Chunks.Count==0);
LogLimits.MaxBytes=300;
var expect=new List<string>();
var got=new List<string>();
for(var k=0;k<40;k++){AuditLog.Write(srv,"u","forgatott-"+k);expect.Add("forgatott-"+k);
  if(k%3==0){var (cc,cmc)=LogShip.Take(srv,200);foreach(var ch in cc)got.AddRange(ch.Text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>l.Split(" | ")[^2]));cmc?.Invoke();}}
for(var guard=0;guard<50;guard++){var (cc,cmc)=LogShip.Take(srv,200);if(cc.Count==0)break;foreach(var ch in cc)got.AddRange(ch.Text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>l.Split(" | ")[^2]));cmc?.Invoke();}
// (Két betárcsázás között legfeljebb egy forgatás – a valóságban 2 MB-os napló és 5 perces betárcsázás.)
Chk("Forgatás mellett minden sor egyszer és sorrendben (kis darabokban)",got.SequenceEqual(expect),$"{got.Count}/{expect.Count}");
var (cr,_)=LogShip.Take(srv);
Chk("Az USB-napló nem megy fel",!LogShip.Logs.Any(l=>l.File.StartsWith("usb")));
LogLimits.MaxBytes=2_000_000;

Console.WriteLine("--- Központi napló a HUB-on ---");
var site=new HubSite{SiteId="site1",Name="Iroda"};
HubSiteLogs.Append(site,new(){new SiteLogChunk{Log="audit",Text="a | x | egy\na | y | kettő\n"},new SiteLogChunk{Log="usb",Text="tilos\n"},new SiteLogChunk{Log="alerts",Text="riasztás"}});
var (t1,_,_)=HubSiteLogs.Tail("site1","audit","");
Chk("Legújabb elöl",t1.SequenceEqual(new[]{"a | y | kettő","a | x | egy"}),string.Join(",",t1));
Chk("Szűrés",HubSiteLogs.Tail("site1","audit","KETTŐ").Lines.Count==1);
Chk("Ismeretlen napló (pl. USB) nem tárolódik",!Directory.GetFiles(Path.Combine(HubSiteLogs.Root,"site1")).Any(f=>f.Contains("usb")));
Chk("Sorvég nélküli darab is egész sor lesz",HubSiteLogs.Tail("site1","alerts","").Lines.SequenceEqual(new[]{"riasztás"}));
HubSiteLogs.Append(new HubSite{SiteId="../kiszokes"},new(){new SiteLogChunk{Log="audit",Text="x\n"}});
Chk("Útvonal-kiszökés ellen: hibás azonosító nem ír",!File.Exists(Path.Combine(HubSiteLogs.Root,"..","kiszokes","audit.log")));

try{Directory.Delete(root,true);}catch{}
Console.WriteLine(fail==0?"MINDEN TESZT ATMENT":$"{fail} HIBA");
return fail==0?0:1;
