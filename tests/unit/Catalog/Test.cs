using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
using Microsoft.Data.Sqlite;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"catalog-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
WingetCatalog.LocalIndexOverride=()=>null;   // a gép valódi winget-forrása ne számítson

// A winget 2.x indexének „packages” táblája, ahogy a source2.msix Public/index.db-jében van.
string MakeIndex(string name,IEnumerable<(string Id,string Name,string Ver)> pkgs){
  var p=Path.Combine(dir,name);
  using(var db=new SqliteConnection($"Data Source={p};Pooling=False")){db.Open();var c=db.CreateCommand();
    c.CommandText="CREATE TABLE packages(rowid INTEGER PRIMARY KEY,id TEXT NOT NULL,name TEXT NOT NULL,moniker TEXT,latest_version TEXT NOT NULL,arp_min_version TEXT,arp_max_version TEXT,hash BLOB)";c.ExecuteNonQuery();
    foreach(var (id,n,v) in pkgs){var i=db.CreateCommand();i.CommandText="INSERT INTO packages(id,name,latest_version) VALUES($i,$n,$v)";
      i.Parameters.AddWithValue("$i",id);i.Parameters.AddWithValue("$n",n);i.Parameters.AddWithValue("$v",v);i.ExecuteNonQuery();}}
  return p;}
byte[] MakeMsix(string index){var ms=new MemoryStream();using(var z=new ZipArchive(ms,ZipArchiveMode.Create,true)){z.CreateEntryFromFile(index,"Public/index.db");z.CreateEntry("AppxManifest.xml");}return ms.ToArray();}
var basePkgs=Enumerable.Range(0,150).Select(i=>($"Vendor.App{i}",$"Program {i}",$"1.{i}")).ToList();

Console.WriteLine("--- Index olvasása ---");
var bad=MakeIndex("bad.db",new[]{("7zip.7zip","7-Zip","24.08"),("-rossz","Kapcsoló",""),("x\"y","Idézőjel","1"),("Git.Git","Git","2.46")});
var read=WingetCatalog.ReadIndex(bad);
Chk("Csak az érvényes azonosítók (kapcsoló és idézőjel kimarad)",read.Count==2&&read.Any(p=>p.Id=="7zip.7zip"&&p.Version=="24.08"),string.Join(",",read.Select(p=>p.Id)));

Console.WriteLine("--- Letöltés egy helyi kamu CDN-ről ---");
byte[] serve=Array.Empty<byte>(); int status=200;
var lis=new TcpListener(IPAddress.Loopback,0);lis.Start();var port=((IPEndPoint)lis.LocalEndpoint).Port;
_=Task.Run(async()=>{while(true){TcpClient cl;try{cl=await lis.AcceptTcpClientAsync();}catch{return;}
  using(cl){var s=cl.GetStream();var r=new StreamReader(s);while(!string.IsNullOrEmpty(await r.ReadLineAsync())){}
    var head=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: {(status==200?serve.Length:0)}\r\nConnection: close\r\n\r\n");
    await s.WriteAsync(head);if(status==200)await s.WriteAsync(serve);await s.FlushAsync();}}});
WingetCatalog.CdnUrl=$"http://127.0.0.1:{port}/cache/source2.msix";

var wc=new WingetCatalog(dir);
serve=MakeMsix(MakeIndex("v1.db",basePkgs));
var first=await wc.RefreshAsync();
Chk("Első letöltés: 150 program, az alap – nincs döntésre váró",first is {Count:0}&&wc.Count==150&&wc.Source=="Microsoft CDN",wc.LastError);
Chk("A gyorsítótár mentve, az ideiglenes fájlok törölve",File.Exists(wc.CachePath)&&!Directory.Exists(Path.Combine(dir,"winget-cache")));

var cfg=new ServerConfig();
cfg.SoftwareCatalog.Add(new CatalogItem{Id="New.Repo",Name="Már a repóban"});
cfg.CatalogRejected.Add(new CatalogItem{Id="New.Rejected",Name="Már elutasítva"});
var v2=basePkgs.Skip(1).Concat(new[]{("New.One","Új egy","1.0"),("New.Two","Új kettő","2.0"),("New.Repo","Már a repóban","3.0"),("New.Rejected","Már elutasítva","4.0")}).ToList();
serve=MakeMsix(MakeIndex("v2.db",v2));
var logs=new List<string>(); var saved=0;
await wc.RefreshAndRecord(cfg,_=>saved++,logs.Add);
Chk("Második letöltés: csak az ÚJ programok várnak döntésre (repóban / elutasítva levő nem)",cfg.CatalogPending.Select(p=>p.Id).OrderBy(x=>x).SequenceEqual(new[]{"New.One","New.Two"})&&saved==1,
    string.Join(",",cfg.CatalogPending.Select(p=>p.Id))+" | "+string.Join(" ",logs));
Chk("A döntésre váró a nevével és verziójával",cfg.CatalogPending.First(p=>p.Id=="New.Two") is {Name:"Új kettő",Version:"2.0"});
Chk("Újra letöltve ugyanaz: nem kérdez újra",(await wc.RefreshAsync()) is {Count:0}&&cfg.CatalogPending.Count==2);

status=500;
var failed=await wc.RefreshAsync();
Chk("Letöltési hiba (és nincs helyi winget): a régi lista marad, érthető hiba",failed is null&&wc.Count==153&&wc.LastError.Contains("nem tölthető le"),wc.LastError);
status=200; serve=MakeMsix(MakeIndex("tiny.db",basePkgs.Take(5)));
Chk("Gyanúsan kicsi lista nem cseréli le a meglévőt",(await wc.RefreshAsync()) is null&&wc.Count==153,wc.LastError);
var wc2=new WingetCatalog(dir);
Chk("Újraindítás után a lista a gyorsítótárból jön",wc2.Count==153&&wc2.Find("new.one")?.Name=="Új egy");

Console.WriteLine("--- Döntések ---");
cfg.AppBundles.Add(new AppBundle{Id="b",Name="Irodai",Apps=new(){"New.Repo","Vendor.App5"}});
Chk("Állapotok",cfg.CatalogState("new.repo")=="repo"&&cfg.CatalogState("New.Rejected")=="rejected"&&cfg.CatalogState("New.One")=="pending"&&cfg.CatalogState("Vendor.App7")=="");
var n=wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="accept",Ids=new(){"New.One","Vendor.App5"}});
Chk("Felvétel: a repóba kerül a winget-tár nevével, kikerül a döntésre várók közül",n==2&&cfg.CatalogState("New.One")=="repo"&&cfg.SoftwareCatalog.Any(c=>c.Id=="Vendor.App5"&&c.Name=="Program 5")&&cfg.CatalogPending.Count==1);
n=wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="reject",Ids=new(){"New.Repo"}});
Chk("Elutasítás: ki a repóból ÉS a csomagokból",n==1&&cfg.CatalogState("New.Repo")=="rejected"&&!cfg.AppBundles[0].Apps.Contains("New.Repo")&&cfg.AppBundles[0].Apps.Contains("Vendor.App5"));
n=wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="unreject",Ids=new(){"New.Repo"}});
Chk("Elutasítás visszavonása: döntés nélküli lesz (nem kérdezi újra)",n==1&&cfg.CatalogState("New.Repo")=="");
n=wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="remove",Ids=new(){"Vendor.App5"}});
Chk("Ki a repóból: a csomagból is kikerül",n==1&&cfg.CatalogState("Vendor.App5")==""&&cfg.AppBundles[0].Apps.Count==0);
n=wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="reject",AllPending=true});
Chk("Mindet elutasítom: az összes döntésre váró elutasítva",n==1&&cfg.CatalogPending.Count==0&&cfg.CatalogState("New.Two")=="rejected");
Chk("Hibás azonosító és ismeretlen döntés nem változtat",wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="accept",Ids=new(){"-x","a\"b"}})==0
    &&wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="töröl",Ids=new(){"Vendor.App9"}})==0);
n=wc.Decide(cfg,new WingetCatalog.DecideReq{Decision="accept",Ids=new(){"Contoso.Tool"},Names=new(){["contoso.tool"]="Contoso eszköz"}});
Chk("Nem winget-tárbeli program a megadott névvel",n==1&&cfg.SoftwareCatalog.Any(c=>c.Id=="Contoso.Tool"&&c.Name=="Contoso eszköz"));
Chk("A döntések mentés után megmaradnak",ServerConfigRoundTrip(cfg) is var c2&&c2.CatalogRejected.Count==2&&c2.SoftwareCatalog.Count==2,"rejected="+cfg.CatalogRejected.Count+" repo="+cfg.SoftwareCatalog.Count);

Console.WriteLine("--- Lista a felületnek (lapozás, keresés) ---");
JsonElement Page(string f,string q="",int p=0,bool dec=true)=>JsonDocument.Parse(JsonSerializer.Serialize(wc.Page(cfg,new WingetCatalog.ListReq{Filter=f,Q=q,Page=p,ShowDecided=dec}),ProtocolJson.Options)).RootElement;
var all=Page("all");
Chk("Teljes tár: 153 program, 100-as lapok",all.GetProperty("total").GetInt32()==153&&all.GetProperty("pages").GetInt32()==2&&all.GetProperty("items").GetArrayLength()==100);
Chk("Második lap a maradék",Page("all","",1).GetProperty("items").GetArrayLength()==53);
var s=Page("all","új");
Chk("Keresés névben (kis-nagybetű nélkül)",s.GetProperty("total").GetInt32()==2,s.ToString());
var st=Page("all","New.One").GetProperty("items")[0];
Chk("A sorban az állapot is",st.GetProperty("state").GetString()=="repo"&&st.GetProperty("inWinget").GetBoolean());
Chk("Elutasított szűrő",Page("rejected").GetProperty("total").GetInt32()==2);
Chk("Túl nagy lapszám a végére igazodik",Page("all","",99).GetProperty("page").GetInt32()==1);
Chk("Alapból az eldöntöttek (repó, elutasított) rejtve a teljes tárban",Page("all","",0,false).GetProperty("total").GetInt32()==150&&Page("all","új",0,false).GetProperty("total").GetInt32()==0&&wc.Undecided(cfg)==150,Page("all","",0,false).GetProperty("total").ToString());
Chk("A döntés nélküli programok látszanak",Page("all","Vendor.App3",0,false).GetProperty("total").GetInt32()>0);

Console.WriteLine("--- Döntésre várók felső korlátja ---");
var big=new ServerConfig();
big.AddPending(Enumerable.Range(0,5100).Select(i=>($"Bulk.App{i}",$"B{i}","1")));
Chk("Legfeljebb 5000 döntésre váró (a legrégebbiek esnek ki)",big.CatalogPending.Count==5000&&big.CatalogPending[0].Id=="Bulk.App100");

Console.WriteLine("--- 1.5.0: a Központ programtára a telephelyen (a Központ a mester) ---");
var site=new ServerConfig();
site.SoftwareCatalog.Add(new CatalogItem{Id="Local.App",Name="Helyi"});
site.AddPending(new[]{("Hub.App","Központi","1"),("Hub.Rej","Elutasított","1"),("Other.App","Más","1")});
var hc=new HubCatalog{Items=new(){new(){Id="Hub.App",Name="Központi"},new(){Id="-rossz",Name="x"}},
  Bundles=new(){new(){Id="hb",Name="Központi csomag",Apps=new(){"Hub.App","Nincs.Ilyen"}}},Rejected=new(){new(){Id="Hub.Rej",Name="Elutasított"}}};
hc.Hash=ServerConfig.CatalogHash(hc.Items,hc.Bundles,hc.Rejected);
Chk("Átvétel: változott",site.ApplyHubCatalog(hc));
Chk("Ugyanaz a lenyomat másodszor nem változtat",!site.ApplyHubCatalog(hc));
Chk("Hibás azonosító kimarad, a csomagban csak a repó programja",site.HubCatalogItems.Count==1&&site.HubBundles[0].Apps.SequenceEqual(new[]{"Hub.App"}));
Chk("Állapotok: hubrepo / hubrejected, a helyi repo marad",site.CatalogState("hub.app")=="hubrepo"&&site.CatalogState("Hub.Rej")=="hubrejected"&&site.CatalogState("Local.App")=="repo");
Chk("A Központ által eldöntöttek kikerülnek a döntésre várók közül",site.CatalogPending.Count==1&&site.CatalogPending[0].Id=="Other.App");
Chk("A Központ döntése itt nem módosítható",site.CatalogDecide("reject",new[]{"Hub.App"},_=>null)==0&&site.CatalogDecide("unreject",new[]{"Hub.Rej"},_=>null)==0&&site.CatalogState("Hub.App")=="hubrepo");
Chk("A Központ csomagja telepíthető (BundleApps)",site.BundleApps("hb").SequenceEqual(new[]{"Hub.App"}));
site.SetCatalog(new(){new(){Id="Local.App",Name="Helyi"}},new(){new(){Id="lb",Name="Vegyes",Apps=new(){"Local.App","Hub.App","Ismeretlen.App"}}});
Chk("Helyi csomagba a Központ repójának programja is kerülhet",site.AppBundles[0].Apps.Count==2&&site.AppBundles[0].Apps.Contains("Hub.App"));
var wcs=new WingetCatalog(dir);
JsonElement PageS(string f)=>JsonDocument.Parse(JsonSerializer.Serialize(wcs.Page(site,new WingetCatalog.ListReq{Filter=f}),ProtocolJson.Options)).RootElement;
var repo=PageS("repo");
Chk("A repó lista: helyi + Központ, a Központé jelölve",repo.GetProperty("total").GetInt32()==2&&repo.GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("state").GetString()=="hubrepo"));
Chk("Az elutasított lista a Központéval",PageS("rejected").GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("state").GetString()=="hubrejected"));
var empty=new HubCatalog();empty.Hash=ServerConfig.CatalogHash(empty.Items,empty.Bundles,empty.Rejected);
Chk("A Központ üres programtára törli a korábbit",site.ApplyHubCatalog(empty)&&site.HubCatalogItems.Count==0&&site.CatalogState("Hub.App")=="");

Console.WriteLine("--- 1.5.0: mérés-beállítás összefésülése (helyi + Központ) ---");
var mc=new ServerConfig{MonHardware=false,MonSecurity=false,MonServices=new(){"Spooler"},MonSampleSeconds=120};
var eff=mc.EffectiveMetricConfig();
Chk("Központ nélkül a helyi beállítás",!eff.Hardware&&!eff.Security&&eff.SampleSeconds==120&&eff.StatusVersion==4&&eff.AcceptsSamples);
mc.HubMonitor=new MetricConfig{Hardware=true,Security=false,Services=new(){"W3SVC","spooler"},SampleSeconds=60,SensorDriver=true};
eff=mc.EffectiveMetricConfig();
Chk("A Központ bővít: amit bármelyik kér",eff.Hardware&&eff.SensorDriver&&!eff.Security&&eff.Services.Count==2&&eff.SampleSeconds==60);
mc.HubMonitor=ServerConfig.NoMonitor();
eff=mc.EffectiveMetricConfig();
Chk("A Központ „semmi” kérése nem kapcsol ki helyit",eff.Cpu&&!eff.Hardware&&eff.SampleSeconds==120);
var am=new ServerConfig();am.ApplyMonitor(new MetricConfig{PingTargets=new(){"8.8.8.8","jó.host","rossz host!","x"},Services=new(){" Spooler ","spooler"}});
Chk("Ping-cél és szolgáltatás ellenőrzése",am.MonPingTargets.Count==3&&!am.MonPingTargets.Contains("rossz host!")&&am.MonServices.Count==1,string.Join(",",am.MonPingTargets));

Console.WriteLine("--- 1.5.4: fejlesztő és kategória ---");
Chk("Fejlesztő: az azonosító első tagja",WingetCatalog.PublisherOf("VideoLAN.VLC")=="VideoLAN"&&WingetCatalog.PublisherOf("Microsoft.VCRedist.2015+.x64")=="Microsoft"&&WingetCatalog.PublisherOf("x")=="x");
Chk("Kategória címkéből (az első illeszkedő szabály nyer)",WingetCatalog.Categorize("Steam",new[]{"game","store"})=="Játék"
  &&WingetCatalog.Categorize("TeamViewer",new[]{"meeting","remote-desktop"})=="Távelérés"&&WingetCatalog.Categorize("Java",new[]{"java","jre","runtime"})=="Futtatókörnyezet"
  &&WingetCatalog.Categorize("LibreOffice",new[]{"office","editor"})=="Irodai"&&WingetCatalog.Categorize("GnuCash",new[]{"finance","accounting"})=="Pénzügy, könyvelés");
Chk("Címke nélkül a névből, különben Egyéb",WingetCatalog.Categorize("VLC media player",null)=="Multimédia"&&WingetCatalog.Categorize("Valami Eszköz Pro",null)=="Egyéb");
// Index címketáblákkal (tags2 + tags2_map), ahogy a valódi source2.msix-ben van.
var tagged=MakeIndex("tags.db",new[]{("Valve.Steam","Steam","3"),("Ghisler.TotalCommander","Total Commander","11"),("Foo.Bar","Bar","1")});
using(var db=new SqliteConnection($"Data Source={tagged};Pooling=False")){db.Open();var c=db.CreateCommand();
  c.CommandText="CREATE TABLE tags2(rowid INTEGER PRIMARY KEY,tag TEXT NOT NULL);CREATE TABLE tags2_map(tag INT64 NOT NULL,package INT64 NOT NULL,PRIMARY KEY(tag,package)) WITHOUT ROWID;"+
    "INSERT INTO tags2(rowid,tag) VALUES(1,'game'),(2,'file-manager');INSERT INTO tags2_map(tag,package) VALUES(1,1),(2,2);";c.ExecuteNonQuery();}
var tr=WingetCatalog.ReadIndex(tagged);
Chk("Az index címkéiből: Játék, Rendszereszköz, címke nélkül Egyéb",tr.First(p=>p.Id=="Valve.Steam").Category=="Játék"&&tr.First(p=>p.Id=="Ghisler.TotalCommander").Category=="Rendszereszköz"
  &&tr.First(p=>p.Id=="Foo.Bar").Category=="Egyéb",string.Join(",",tr.Select(p=>p.Id+"="+p.Category)));
var pg=JsonDocument.Parse(JsonSerializer.Serialize(wc.Page(cfg,new WingetCatalog.ListReq{Filter="all",ShowDecided=true,Pub="Vendor"}),ProtocolJson.Options)).RootElement;
Chk("Fejlesztő-szűrés és kategória-darabszámok",pg.GetProperty("total").GetInt32()==149&&pg.GetProperty("cats").GetArrayLength()>=1&&pg.GetProperty("items")[0].GetProperty("publisher").GetString()=="Vendor",pg.GetProperty("total").ToString());
var pgc=JsonDocument.Parse(JsonSerializer.Serialize(wc.Page(cfg,new WingetCatalog.ListReq{Filter="all",ShowDecided=true,Cat="Játék"}),ProtocolJson.Options)).RootElement;
Chk("Kategória-szűrés (nincs ilyen program: 0)",pgc.GetProperty("total").GetInt32()==0);

Console.WriteLine("--- 1.5.4: döntésre csak a valóban új programok ---");
var fl=new ServerConfig();
var cnt0=wc.Count;
serve=MakeMsix(MakeIndex("flood.db",v2.Concat(Enumerable.Range(0,1200).Select(i=>($"Flood.App{i}",$"F{i}","1")))));
await wc.RefreshAndRecord(fl,_=>{},_=>{});
Chk("Gyanúsan sok „új” (pl. régi helyi index → CDN): új alap, semmi nem kerül döntésre",fl.CatalogPending.Count==0&&wc.Count==cnt0+1200&&wc.LastNote.Contains("új alap"),wc.LastNote);
status=500;var local=MakeIndex("local.db",v2.Concat(new[]{("Local.Only","Helyi","1")}));WingetCatalog.LocalIndexOverride=()=>local;
await wc.RefreshAndRecord(fl,_=>{},_=>{});
Chk("Forrásváltás (CDN → helyi winget): új alap",fl.CatalogPending.Count==0&&wc.Source.Contains("gépen")&&wc.LastNote.Contains("új alap"),wc.Source+" | "+wc.LastNote);
WingetCatalog.LocalIndexOverride=()=>null;status=200;
var pr=new ServerConfig();var now=DateTime.UtcNow;
pr.CatalogPending.Add(new PendingApp{Id="Old.App",Name="régi",SeenUtc=now.AddDays(-40)});
pr.CatalogPending.Add(new PendingApp{Id="Fresh.App",Name="friss",SeenUtc=now.AddDays(-2)});
pr.CatalogPending.Add(new PendingApp{Id="Gone.App",Name="eltűnt",SeenUtc=now.AddDays(-1)});
for(int i=0;i<1500;i++)pr.CatalogPending.Add(new PendingApp{Id=$"Batch.App{i}",Name="b",SeenUtc=now.AddDays(-3).AddMilliseconds(i)});
var removed=pr.PrunePending(id=>id!="Gone.App",now,1000);
Chk("Takarítás: 30 napnál régebbi, a tárból eltűnt és a tömeges (forrásváltásos) köteg kikerül, a valódi új marad",
  removed==1502&&pr.CatalogPending.Count==1&&pr.CatalogPending[0].Id=="Fresh.App",removed+" / "+string.Join(",",pr.CatalogPending.Take(3).Select(p=>p.Id)));

lis.Stop();
try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;

ServerConfig ServerConfigRoundTrip(ServerConfig c){var d=Path.Combine(dir,"cfg");c.Save(d);return ServerConfig.Load(d);}
