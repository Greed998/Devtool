using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"al-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
var al=new AlertStore(dir);

List<Metric> M(double cpu)=>new(){new(){Key="cpu.load",Label="CPU",Value=cpu,Category="CPU"}};
int RaiseCount(List<AlertEvent> e)=>e.Count(x=>x.Raised);
int ClearCount(List<AlertEvent> e)=>e.Count(x=>!x.Raised);

Console.WriteLine("--- Alapból semmi nem riaszt ---");
Chk("Szabály nélkül nincs esemény", al.Evaluate("a1","PC1",M(99)).Count==0);
Chk("Nincs aktív riasztás", al.ActiveStates().Count==0);

Console.WriteLine("--- Küszöb: CPU > 90, 2 egymást követő mérés ---");
al.Add(new AlertRule{Name="CPU magas",MetricKey="cpu.load",Op=AlertOp.Nagyobb,Threshold=90,ForSamples=2});
Chk("1. mérés 95%: még NEM riaszt (kell 2)", RaiseCount(al.Evaluate("a1","PC1",M(95)))==0);
Chk("2. mérés 95%: MOST riaszt", RaiseCount(al.Evaluate("a1","PC1",M(95)))==1);
Chk("Aktív a riasztás", al.ActiveStates().Count==1);
Chk("3. mérés 95%: nem riaszt újra (már aktív)", al.Evaluate("a1","PC1",M(96)).Count==0);

Console.WriteLine("--- Megszűnés ---");
Chk("Érték 50% alá esik: riasztás megszűnik", ClearCount(al.Evaluate("a1","PC1",M(50)))==1);
Chk("Már nincs aktív", al.ActiveStates().Count==0);

Console.WriteLine("--- Streak megszakad ---");
al.Evaluate("a1","PC1",M(95)); // 1
al.Evaluate("a1","PC1",M(50)); // megszakad
Chk("Egy 95 után egy 50: nem riaszt (streak nullázódott)", al.ActiveStates().Count==0);
al.Evaluate("a1","PC1",M(95)); al.Evaluate("a1","PC1",M(95));
Chk("Két egymást követő 95 újra riaszt", al.ActiveStates().Count==1);

Console.WriteLine("--- Gép-specifikus szabály ---");
var al2=new AlertStore(Path.Combine(dir,"b"));
al2.Add(new AlertRule{Name="csak PC2",MetricKey="cpu.load",Op=AlertOp.Nagyobb,Threshold=80,AgentId="pc2",ForSamples=1});
Chk("PC1-re nem vonatkozik", al2.Evaluate("pc1","PC1",M(99)).Count==0);
Chk("PC2-re igen", RaiseCount(al2.Evaluate("pc2","PC2",M(99)))==1);

Console.WriteLine("--- Operátorok ---");
var al3=new AlertStore(Path.Combine(dir,"c"));
al3.Add(new AlertRule{Name="lemez fogy",MetricKey="disk.free",Op=AlertOp.Kisebb,Threshold=10,ForSamples=1});
Chk("< operátor: 5 < 10 riaszt", RaiseCount(al3.Evaluate("x","X",new List<Metric>{new(){Key="disk.free",Value=5}}))==1);
Chk("< operátor: 20 nem", al3.Evaluate("x","X",new List<Metric>{new(){Key="disk.free",Value=20}}).Any(e=>!e.Raised));

Console.WriteLine("--- Kikapcsolt szabály ---");
var al4=new AlertStore(Path.Combine(dir,"d"));
al4.Add(new AlertRule{Name="ki",MetricKey="cpu.load",Op=AlertOp.Nagyobb,Threshold=1,Enabled=false,ForSamples=1});
Chk("Kikapcsolt szabály nem riaszt", al4.Evaluate("x","X",M(99)).Count==0);

Console.WriteLine("--- Perzisztencia ---");
var al5=new AlertStore(dir);
Chk("A szabályok túlélik újraindítást", al5.Rules.Count==1 && al5.Rules[0].Name=="CPU magas");

Console.WriteLine("--- 1.5.0: csoport-hatókör ---");
var g=new AlertStore(Path.Combine(dir,"g"));
g.Add(new AlertRule{Name="könyvelés CPU",MetricKey="cpu.load",Op=AlertOp.Nagyobb,Threshold=80,Group="Könyvelés",ForSamples=1});
Chk("Más csoport gépére nem vonatkozik", g.Evaluate("x","X",M(99),new[]{"Raktár"}).Count==0);
Chk("Csoport nélküli gépre nem vonatkozik", g.Evaluate("y","Y",M(99)).Count==0);
Chk("A csoport gépére igen (kis-nagybetű mindegy)", RaiseCount(g.Evaluate("z","Z",M(99),new[]{"könyvelés"}))==1);

Console.WriteLine("--- 1.5.0: „bármelyik” minta (disk.*.usedpct) ---");
var p=new AlertStore(Path.Combine(dir,"p"));
p.Add(new AlertRule{Name="Lemez tele",MetricKey="disk.*.usedpct",Op=AlertOp.Nagyobb,Threshold=90,ForSamples=1});
List<Metric> D(double c,double d)=>new(){new(){Key="disk.C.usedpct",Value=c},new(){Key="disk.D.usedpct",Value=d},new(){Key="disk.C.freegb",Value=1}};
var ev=p.Evaluate("pc","PC",D(95,50));
Chk("Csak a teli lemez riaszt, a kulccsal a névben", RaiseCount(ev)==1 && ev[0].RuleName.Contains("disk.C.usedpct"), string.Join("; ",ev.Select(e=>e.RuleName)));
Chk("Mindkét teli lemez külön riaszt", RaiseCount(p.Evaluate("pc","PC",D(95,96)))==1 && p.ActiveStates().Count==2);
Chk("Az aktív állapotban ott a konkrét kulcs", p.ActiveStates().Any(s=>s.MetricKey=="disk.D.usedpct"));
Chk("A C megszűnik, a D marad", ClearCount(p.Evaluate("pc","PC",D(50,96)))==1 && p.ActiveStates().Count==1);
Chk("Minta: a * nem lép át pontot", !new AlertRule{MetricKey="disk.*.usedpct"}.KeyMatches("disk.C.x.usedpct"));
Chk("Minta: számozott példány (hw.gpu*.temp)", new AlertRule{MetricKey="hw.gpu*.temp"}.KeyMatches("hw.gpu0.temp") && !new AlertRule{MetricKey="hw.gpu*.temp"}.KeyMatches("hw.cpu.temp"));
Chk("Kulcs-ellenőrzés: injektálás tiltva", AlertRule.ValidKey("disk.*.usedpct") && !AlertRule.ValidKey("x'; drop") && !AlertRule.ValidKey(""));

Console.WriteLine("--- 1.5.0: szabály módosítása / törlése az állapotot is viszi ---");
var r1=p.Rules[0]; r1.Threshold=99; p.Update(r1);
Chk("Módosítás után nincs régi aktív állapot", p.ActiveStates().Count==0);
p.Evaluate("pc","PC",D(100,10)); p.Remove(r1.Id);
Chk("Törlés után nincs árva aktív állapot", p.ActiveStates().Count==0);

Console.WriteLine("--- 1.5.0: a Központ szabályai a telephelyen ---");
var h=new AlertStore(Path.Combine(dir,"h"));
h.Add(new AlertRule{Id="helyi",Name="helyi",MetricKey="cpu.load",Op=AlertOp.Nagyobb,Threshold=95,ForSamples=1});
h.SetHubRules(new[]{new AlertRule{Id="k1",Name="Központ CPU",MetricKey="cpu.load",Op=AlertOp.Nagyobb,Threshold=80,ForSamples=1}});
Chk("A Központ szabálya hub- előtagot kap, és nincs a helyiek között", h.HubRules.Single().Id=="hub-k1" && h.Rules.Count==1);
var hev=h.Evaluate("pc","PC",M(90));
Chk("A Központ szabálya a helyi mellett fut", RaiseCount(hev)==1 && hev[0].RuleName=="Központ CPU");
Chk("A riasztás neve visszakereshető", h.RuleName("hub-k1")=="Központ CPU");
h.SetHubRules(Array.Empty<AlertRule>());
Chk("A megszűnt Központ-szabály állapota törlődik", h.ActiveStates().Count==0 && h.HubRules.Count==0);
var h2=new AlertStore(Path.Combine(dir,"h"));
Chk("A Központ szabályai nem kerülnek a helyi alerts.json-ba", h2.Rules.Count==1 && h2.Rules[0].Id=="helyi");

Console.WriteLine("--- 1.5.0: telephely-hatókör (Központ) ---");
var s=new AlertStore(Path.Combine(dir,"s"));
s.Add(new AlertRule{Name="csak s1",MetricKey="cpu.load",Op=AlertOp.Nagyobb,Threshold=50,Sites=new(){"s1"},ForSamples=1});
Chk("Más telephely gépére nem", s.Evaluate("a","A",M(99),null,"s2").Count==0);
Chk("A kijelölt telephely gépére igen", RaiseCount(s.Evaluate("b","B",M(99),null,"s1"))==1);

try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
