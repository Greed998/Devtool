using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"db-"+Guid.NewGuid().ToString("N"));
var ms=new MetricStore(dir);

Console.WriteLine("--- Beszúrás + legfrissebb ---");
var now=DateTime.UtcNow;
ms.Insert("agent1", new List<Metric>{
  new(){Key="cpu.load",Label="CPU",Value=30,Unit="%",Category="CPU"},
  new(){Key="mem.usedpct",Label="Memória",Value=60,Unit="%",Category="Memória"}
}, now.AddMinutes(-2));
ms.Insert("agent1", new List<Metric>{
  new(){Key="cpu.load",Label="CPU",Value=45,Unit="%",Category="CPU"},
  new(){Key="mem.usedpct",Label="Memória",Value=65,Unit="%",Category="Memória"}
}, now);
var latest=ms.Latest("agent1");
Chk("A legfrissebb CPU érték a legutóbbi", latest["cpu.load"].Value==45);
Chk("A címke és egység megmarad", latest["cpu.load"].Label=="CPU" && latest["cpu.load"].Unit=="%");

Console.WriteLine("--- Idősor ---");
var series=ms.Series("agent1","cpu.load",24);
Chk("Két adatpont az idősorban", series.Count==2, series.Count.ToString());
Chk("Időrendben növekvő", series[0].Ts<=series[1].Ts && series[0].Value==30 && series[1].Value==45);

Console.WriteLine("--- Kulcsok listája ---");
var keys=ms.Keys("agent1");
Chk("Két metrika-kulcs elérhető", keys.Count==2);
Chk("A kategória is jön", keys.Any(k=>k.Category=="CPU") && keys.Any(k=>k.Category=="Memória"));

Console.WriteLine("--- Több agent elkülönül ---");
ms.Insert("agent2", new List<Metric>{new(){Key="cpu.load",Value=99,Category="CPU"}}, now);
Chk("agent2 külön adata", ms.Latest("agent2")["cpu.load"].Value==99);
Chk("agent1 nem keveredik", ms.Latest("agent1")["cpu.load"].Value==45);

Console.WriteLine("--- Megőrzés: régi részletes -> órás átlag ---");
var old=new MetricStore(dir);
// 10 napnál régebbi részletes minták egy órán belül -> egy órás átlag legyen
var t=new DateTime(DateTime.UtcNow.AddDays(-10).Year,DateTime.UtcNow.AddDays(-10).Month,DateTime.UtcNow.AddDays(-10).Day,10,0,0,DateTimeKind.Utc);
for(int i=0;i<6;i++) old.Insert("agent3",new List<Metric>{new(){Key="cpu.load",Value=i*10,Category="CPU"}}, t.AddMinutes(i*5));
// maxPoints: nyers minták (a grafikon-sűrítés nélkül), hogy a megőrzés hatása látsszon
var before=old.Series("agent3","cpu.load",24*365,maxPoints:int.MaxValue).Count;
old.ApplyRetention(detailDays:7, keepDays:400);
var after=old.Series("agent3","cpu.load",24*365,maxPoints:int.MaxValue).Count;
Chk("6 részletes mintából EGY órás átlag lett", before==6 && after==1, $"{before} -> {after}");
Chk("Az átlag helyes (0,10,20,30,40,50 átlaga=25)", Math.Abs(old.Series("agent3","cpu.load",24*365)[0].Value-25)<0.01);

Console.WriteLine("--- Megőrzés: nagyon régi -> törlés ---");
var old2=new MetricStore(dir);
old2.Insert("agent4",new List<Metric>{new(){Key="x",Value=1,Category="C"}}, DateTime.UtcNow.AddDays(-500));
old2.ApplyRetention(7, 90);
Chk("500 napos adat törölve", old2.Series("agent4","x",24*365*2).Count==0);

Console.WriteLine("--- Agent törlés ---");
ms.PurgeAgent("agent2");
Chk("agent2 adatai törölve", ms.Latest("agent2").Count==0);
Chk("agent1 megmaradt", ms.Latest("agent1").Count>0);

Console.WriteLine("--- Mintavétel: sok időpont egy tranzakcióban ---");
var t0=DateTime.UtcNow.AddMinutes(-30);
ms.InsertMany("agent5", Enumerable.Range(0,30).Select(i=>(t0.AddMinutes(i),(IReadOnlyList<Metric>)new List<Metric>{new(){Key="cpu.load",Value=i,Category="CPU"}})));
Chk("30 percenkénti minta, mind a saját idejével", ms.Series("agent5","cpu.load",1,maxPoints:int.MaxValue).Count==30);
Chk("Grafikon: legfeljebb kb. a kért pontszám", ms.Series("agent5","cpu.load",1,maxPoints:10).Count<=11);

Console.WriteLine("--- Központ: lezárt 5 perces sávok ---");
var from=new DateTimeOffset(t0).ToUnixTimeSeconds();
var (rows,next)=ms.Buckets(from,300,10000);
var r5=rows.Where(r=>r.AgentId=="agent5").ToList();
Chk("Csak lezárt sávok (a folyó 5 perc nem)", r5.All(r=>r.Ts+300<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
Chk("Sávonként egy átlag", r5.Count>=5 && r5.Count==r5.Select(r=>r.Ts).Distinct().Count(), r5.Count.ToString());
Chk("Folytatás a sávok végétől", r5.Count>0 && next>=r5.Max(r=>r.Ts)+300);
var (rows2,_)=ms.Buckets(next,300,10000);
Chk("Újrahíváskor nincs ismétlés", !rows2.Any(r=>r.AgentId=="agent5" && r.Ts<next));
var (lim,nextLim)=ms.Buckets(from,300,3);
Chk("Sorlimitnél a félbevágott sáv a következő körre marad", lim.Count>0 && lim.All(r=>r.Ts<nextLim) && lim.Count<3);

ms.Dispose(); old.Dispose(); old2.Dispose();
try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
