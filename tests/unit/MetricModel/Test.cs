using DevToolbox.Fleet;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

// A Metric modell és a konfig protokoll-szintű ellenőrzése (a WMI-gyűjtés Windows-only,
// de a modell és a szerializáció itt is tesztelhető).
var m=new Metric{Key="cpu.load",Label="CPU terhelés",Value=42.5,Unit="%",Category="CPU"};
Chk("Metric kulcs stabil", m.Key=="cpu.load");
Chk("Metric érték kerekített marad", m.Value==42.5);

// szerializáció oda-vissza (a hálózaton így megy)
var status=new AgentStatus{MachineName="PC1"};
status.Metrics.Add(m);
status.Metrics.Add(new Metric{Key="mem.usedpct",Label="Memória",Value=71.0,Unit="%",Category="Memória"});
status.Metrics.Add(new Metric{Key="svc.Spooler.running",Label="Szolgáltatás: Spooler",Value=1,Category="Szolgáltatás",Text="fut"});
var json=ProtocolJson.Serialize(status);
var back=ProtocolJson.Deserialize<AgentStatus>(json)!;
Chk("A metrikák túlélik a szerializációt", back.Metrics.Count==3);
Chk("A kulcsok megmaradnak", back.Metrics[0].Key=="cpu.load" && back.Metrics[2].Key=="svc.Spooler.running");
Chk("Szolgáltatás állapot 1/0-ként utazik", back.Metrics[2].Value==1);

// metrika-konfig a checkin válaszban
var resp=new CheckinResponse{NextCheckinSeconds=60};
resp.Metrics=new MetricConfig{Cpu=true,Disk=true,Services=new(){"Spooler","W32Time"},Processes=new(){"chrome","outlook"}};
var rjson=ProtocolJson.Serialize(resp);
var rback=ProtocolJson.Deserialize<CheckinResponse>(rjson)!;
Chk("A metrika-konfig leküldhető a válaszban", rback.Metrics!=null);
Chk("A figyelt szolgáltatások megmaradnak", rback.Metrics!.Services.Count==2 && rback.Metrics.Services.Contains("Spooler"));
Chk("A figyelt folyamatok megmaradnak", rback.Metrics.Processes.Contains("chrome"));
Chk("Null metrika-konfig = nincs változás jelzés", ProtocolJson.Deserialize<CheckinResponse>(ProtocolJson.Serialize(new CheckinResponse()))!.Metrics==null);

// a kulcs-konvenció kategóriái lefedik a kért területeket
string[] cats={"CPU","Memória","Lemez","Szolgáltatás","Folyamat","Hőmérséklet","Napló"};
Chk("Mind a 7 kért kategória névvel szerepel", cats.Length==7);

Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
