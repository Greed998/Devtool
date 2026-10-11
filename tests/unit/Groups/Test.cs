using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"gr-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
var store=new FleetStore(dir);
string Enroll(string name,string os=""){var k=Crypto.NewKeyPair();var t=store.CreateEnrollmentToken("t",0,0);
  return store.Enroll(new EnrollRequest{Token=t,PublicKeyPem=k.PublicKeyPem,MachineName=name,OperatingSystem=os}).AgentId;}
var a1=Enroll("PC1");var a2=Enroll("PC2");var a3=Enroll("SRV1");
// Az automatikus csoportok (1.5.4) itt kikapcsolva: a kézi csoportok viselkedése változatlan; lent külön teszt.
var cfg=new ServerConfig{AutoGroupsOn=false};var alerts=new AlertStore(dir);
string? Op(string op,string g,string nn="",params string[] ids)=>GroupOps.Apply(store,cfg,alerts,op,g,nn,ids,out _);
List<string> G(string id)=>store.GetAgent(id)!.Groups;

Console.WriteLine("--- Létrehozás, tagság ---");
Chk("Üres csoport létrehozható és listázódik",Op("create","Könyvelés")==null&&GroupOps.All(store,cfg).Contains("Könyvelés"));
Chk("Hibás név: üres, túl hosszú, vessző",Op("create","")!=null&&Op("create",new string('x',41))!=null&&Op("create","a,b")!=null);
Chk("Ugyanaz a név más kis-nagybetűvel nem lesz új",Op("create","könyvelés")==null&&GroupOps.All(store,cfg).Count==1);
Chk("Tagság: pontosan a megadott gépek",Op("members","Könyvelés","",a1,a2)==null&&G(a1).Contains("Könyvelés")&&G(a2).Contains("Könyvelés")&&!G(a3).Any());
Chk("Tagság csökkentése: a kivett gépből kikerül",Op("members","Könyvelés","",a2)==null&&!G(a1).Any()&&G(a2).Contains("Könyvelés"));
Chk("A kiürített csoport megmarad",Op("members","Könyvelés","")==null&&GroupOps.All(store,cfg).Contains("Könyvelés"));
Op("members","Szerverek","",a3);
Chk("A tagság által létrejött csoport is listázódik",GroupOps.All(store,cfg).Contains("Szerverek")&&cfg.Groups.Contains("Szerverek"));

Console.WriteLine("--- Átnevezés: a hivatkozások is követik ---");
cfg.AutoPolicies.Add(new AutoPolicy{Name="éjszaka",Group="Szerverek",Apps=true,Days=new(){1},StartHour=22,EndHour=4});
cfg.OfflineAlertGroups.Add("Szerverek");
alerts.Add(new AlertRule{Id="r1",Name="srv cpu",MetricKey="cpu.load",Threshold=90,Group="Szerverek"});
Chk("Átnevezés",Op("rename","Szerverek","Kiszolgálók")==null&&G(a3).SequenceEqual(new[]{"Kiszolgálók"}));
Chk("A szabály, a kiesés-figyelés és a küszöb is az új nevet kapja",cfg.AutoPolicies[0].Group=="Kiszolgálók"&&cfg.OfflineAlertGroups.Contains("Kiszolgálók")&&alerts.Rules[0].Group=="Kiszolgálók");
Chk("Már létező névre nem nevezhető át",Op("rename","Kiszolgálók","könyvelés")!=null);

Console.WriteLine("--- Törlés: a rá szűkített szabályok kikapcsolnak (nem tágulnak „minden gépre”) ---");
Chk("Törlés",Op("delete","Kiszolgálók")==null&&!G(a3).Any()&&!GroupOps.All(store,cfg).Contains("Kiszolgálók"));
Chk("Az automatikus szabály kikapcsolva, a kiesés-csoport törölve, a küszöb kikapcsolva",!cfg.AutoPolicies[0].Enabled&&!cfg.OfflineAlertGroups.Any()&&!alerts.Rules[0].Enabled);
Chk("Ismeretlen művelet",Op("valami","x")!=null);

Console.WriteLine("--- Automatikus csoportok (operációs rendszer, virtuális-e) ---");
Chk("Windows 11 kliens, fizikai",AutoGroups.Classify("Microsoft Windows 11 Pro",false)==("Kliensek","Windows 11","Fizikai gépek"));
Chk("Windows Server 2022, virtuális",AutoGroups.Classify("Microsoft Windows Server 2022 Standard",true)==("Szerverek","Windows Server 2022","Virtuális gépek"));
Chk("® jelekkel és R2-vel",AutoGroups.Classify("Microsoft® Windows Server® 2008 R2 Enterprise",null)==("Szerverek","Windows Server 2008 R2",null));
Chk("Windows 10 LTSC; ismeretlen hardver: nincs hardver-csoport",AutoGroups.For("Microsoft Windows 10 Enterprise LTSC",null).SequenceEqual(new[]{"Kliensek","Windows 10"}));
Chk("Üres OS: nincs automatikus csoport",AutoGroups.For("",null).Count==0);
Chk("Automatikus név felismerése",AutoGroups.IsAutoName("windows 11")&&AutoGroups.IsAutoName("Szerverek")&&AutoGroups.IsAutoName("Windows Server 2019")&&!AutoGroups.IsAutoName("Windows gépek")&&!AutoGroups.IsAutoName("Könyvelés"));
var a4=Enroll("SRV2","Microsoft Windows Server 2022 Standard");var a5=Enroll("PC3","Microsoft Windows 11 Pro");
var acfg=new ServerConfig();   // alapból bekapcsolva
Chk("Alapból bekapcsolva",acfg.AutoGroupsOn);
var srv=store.GetAgent(a4)!;
Chk("A gép tényleges csoportjai: kézi + automatikus",GroupOps.Of(srv,acfg).Contains("Szerverek")&&GroupOps.Of(srv,acfg).Contains("Windows Server 2022"));
Chk("Az automatikus csoportok listája (amelyikben van gép)",GroupOps.Auto(store,acfg).SequenceEqual(new[]{"Kliensek","Szerverek","Windows 11","Windows Server 2022"}),string.Join(",",GroupOps.Auto(store,acfg)));
Chk("Kikapcsolva nincs automatikus csoport",!GroupOps.Of(srv,new ServerConfig{AutoGroupsOn=false}).Any()&&!GroupOps.Auto(store,new ServerConfig{AutoGroupsOn=false}).Any());
Chk("Automatikus csoport kézzel nem hozható létre / tölthető / törölhető",
  GroupOps.Apply(store,acfg,alerts,"create","Szerverek","",null,out _)!=null&&GroupOps.Apply(store,acfg,alerts,"members","Windows 11","",new[]{a1},out _)!=null
  &&GroupOps.Apply(store,acfg,alerts,"delete","Kliensek","",null,out _)!=null&&!G(a1).Any());
Chk("Kézi csoport nem nevezhető át automatikus névre",GroupOps.Apply(store,acfg,alerts,"create","Raktár","",null,out _)==null&&GroupOps.Apply(store,acfg,alerts,"rename","Raktár","Windows 10",null,out _)!=null);
var pol=new AutoPolicy{Name="srv",Group="Szerverek",Apps=true,Days=new(){0,1,2,3,4,5,6},StartHour=0,EndHour=23};
srv.LastStatus=new AgentStatus{UpdatesAvailable=1,OperatingSystem="Microsoft Windows Server 2022 Standard"};
Chk("Szabály automatikus csoportra: a szerver megkapja",PolicyEngine.Evaluate(new[]{pol},srv,new DateTime(2026,1,5,12,0,0),DateTime.UtcNow,GroupOps.Of(srv,acfg)).Count==1);
var pc=store.GetAgent(a5)!;pc.LastStatus=new AgentStatus{UpdatesAvailable=1,OperatingSystem="Microsoft Windows 11 Pro"};
Chk("…a kliens nem",PolicyEngine.Evaluate(new[]{pol},pc,new DateTime(2026,1,5,12,0,0),DateTime.UtcNow,GroupOps.Of(pc,acfg)).Count==0);

Console.WriteLine("--- Központ: csoport-művelet sorba, a felület azonnal az új állapotot mutatja ---");
var hdir=Path.Combine(dir,"hub");var hub=new HubStore(hdir);
var tok=hub.CreateToken("t",0,0);var sk=Crypto.NewKeyPair();
var site=hub.Enroll(new HubEnrollRequest{Token=tok,PublicKeyPem=sk.PublicKeyPem,SiteName="Ügyfél"}).SiteId;
SignedEnvelope Dial(SiteSummary sum)=>hub.Checkin(SignedEnvelope.Create(site,new HubCheckinBody{Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Summary=sum},sk.PrivateKeyPem));
SiteSummary Sum(bool supports)=>new(){SiteName="Ügyfél",SupportsGroupOps=supports,GroupNames=new(){"Iroda"},Agents=new(){new(){AgentId="x1",MachineName="P1",Groups=new(){"Iroda"}},new(){AgentId="x2",MachineName="P2"}}};
Dial(Sum(true));
hub.QueueGroupOp(site,new HubGroupOp{Op="members",Group="Iroda",AgentIds=new(){"x2"}});
var s=hub.Sites.First();
Chk("A Központ felületén azonnal az új tagság",s.LastSummary!.Agents.First(a=>a.AgentId=="x2").Groups.Contains("Iroda")&&!s.LastSummary.Agents.First(a=>a.AgentId=="x1").Groups.Any());
var rep=Dial(Sum(false)).Open<HubCheckinReply>()!;
Chk("1.5.0 előtti telephelynek nem megy le",rep.GroupOps is null&&hub.Sites.First().PendingGroupOps.Count==1);
rep=Dial(Sum(true)).Open<HubCheckinReply>()!;
Chk("Az 1.5.0+ telephelynek lemegy, és a sor kiürül",rep.GroupOps?.Count==1&&rep.GroupOps[0].AgentIds.SequenceEqual(new[]{"x2"})&&hub.Sites.First().PendingGroupOps.Count==0);
Chk("A régi összesítő után is az új állapot látszik",hub.Sites.First().LastSummary!.Agents.First(a=>a.AgentId=="x2").Groups.Contains("Iroda"));

Console.WriteLine("--- Központ: programtár csak változáskor, szabályok és mérés minden válaszban ---");
var cat=new HubCatalog{Items=new(){new(){Id="7zip.7zip",Name="7-Zip"}}};cat.Hash=ServerConfig.CatalogHash(cat.Items,cat.Bundles,cat.Rejected);
hub.CatalogFor=()=>cat;hub.AlertRulesFor=_=>new(){new SiteAlertRule{Id="k",MetricKey="cpu.load"}};hub.MonitorFor=_=>ServerConfig.NoMonitor();
var sum=Sum(true);rep=Dial(sum).Open<HubCheckinReply>()!;
Chk("Eltérő lenyomatnál lemegy a programtár",rep.Catalog?.Hash==cat.Hash&&rep.AlertRules?.Count==1&&rep.Monitor is {Cpu:false});
sum=Sum(true);sum.HubCatalogHash=cat.Hash;rep=Dial(sum).Open<HubCheckinReply>()!;
Chk("Azonos lenyomatnál nem küldi újra",rep.Catalog is null&&rep.AlertRules?.Count==1);

Console.WriteLine("--- Metrika-katalógus: „bármelyik” minták ---");
// A JSON a nem-ASCII betűket \u-val írja: összehasonlításhoz visszaalakítjuk.
var mcat=System.Text.RegularExpressions.Regex.Unescape(System.Text.Json.JsonSerializer.Serialize(MetricCatalog.Build(new[]{
  ("disk.C.usedpct","Lemez C: kihasználtság","Lemez","%",2),("disk.D.usedpct","Lemez D: kihasználtság","Lemez","%",1),
  ("hw.gpu0.temp","GPU hőmérséklet (NVIDIA)","GPU","°C",1),("cpu.core3.load","Mag #3 terhelés","CPU magok","%",1),
  ("net.tcp.established","TCP","Hálózat","db",1),("hw.cpu.temp","CPU hőmérséklet","Hardver","°C",1),("net.IntelR.rxkbps","Fogadás: Intel[R]","Hálózat","KB/s",1)}),ProtocolJson.Options));
Chk("disk.*.usedpct mintával, 2 kulccsal és „*” felirattal",mcat.Contains("\"key\":\"disk.*.usedpct\",\"label\":\"Bármelyik – Lemez *: kihasználtság\"")&&mcat.Contains("\"count\":2"),mcat);
Chk("Számozott példány: hw.gpu*.temp, cpu.core*.load (Mag * terhelés)",mcat.Contains("hw.gpu*.temp")&&mcat.Contains("cpu.core*.load")&&mcat.Contains("Mag * terhelés"));
Chk("Nem példány: net.tcp.*, hw.*.temp nincs",!mcat.Contains("net.*.established")&&!mcat.Contains("\"hw.*.temp\""));
Chk("Tisztított név: a kettőspont utáni rész lesz *",mcat.Contains("Bármelyik – Fogadás: *"));

try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
