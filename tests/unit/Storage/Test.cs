using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

// A mappajog-szűkítés itt nincs bekapcsolva (LogFolder.SecureDir = null): nem emelt joggal futó tesztben kizárna minket.
var root=Path.Combine(Path.GetTempPath(),"stor-"+Guid.NewGuid().ToString("N"));var data=Path.Combine(root,"data");Directory.CreateDirectory(data);

Console.WriteLine("--- Frissítés: a régi, gyökérbeli naplók a logs\\ mappába költöznek ---");
File.WriteAllText(Path.Combine(data,"fleet-server.log"),"régi sor\n");
File.WriteAllText(Path.Combine(data,"audit.log"),"régi audit\n");
File.WriteAllText(Path.Combine(data,"alerts.log.1"),"régi riasztás\n");
File.WriteAllText(Path.Combine(data,"server-config.json"),"{}");
var notes=Storage.ApplyAtStartup(data,new ServerConfig(),secure:false);
var logs=Path.Combine(data,"logs");
Chk("Nincs figyelmeztetés",notes.Count==0,string.Join(" | ",notes));
Chk("A naplók átköltöztek, a beállítás maradt",File.Exists(Path.Combine(logs,"fleet-server.log"))&&File.Exists(Path.Combine(logs,"audit.log"))&&File.Exists(Path.Combine(logs,"alerts.log.1"))
  &&!File.Exists(Path.Combine(data,"audit.log"))&&File.Exists(Path.Combine(data,"server-config.json")));
AuditLog.Write(data,"admin","próba");
Chk("Az új bejegyzés a régi után, ugyanabba a fájlba",File.ReadAllText(Path.Combine(logs,"audit.log")) is var a&&a.StartsWith("régi audit")&&a.Contains("| admin | próba"),File.ReadAllText(Path.Combine(logs,"audit.log")));
Chk("A műveleti napló olvasása az új helyről",AuditLog.Tail(data,10).Contains("próba"));

Console.WriteLine("--- Útvonal-ellenőrzés ---");
string? V(string key,string p)=>Storage.Validate(key,p,data,out _);
Chk("Üres: alapértelmezés",V(Storage.Logs,"")==null);
Chk("Relatív és hálózati út nem jó",V(Storage.Logs,"naplok")!=null&&V(Storage.Logs,@"\\srv\share\naplo")!=null);
Chk("Meghajtó-gyökér és rendszermappa nem jó",V(Storage.Logs,@"C:\")!=null&&V(Storage.Logs,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"x"))!=null
  &&V(Storage.Logs,Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))!=null);
var foreign=Path.Combine(root,"idegen");Directory.CreateDirectory(foreign);File.WriteAllText(Path.Combine(foreign,"fontos.docx"),"x");
Chk("Idegen, nem üres mappa nem jó (a jogait szűkítenénk)",V(Storage.Logs,foreign)!=null);
Chk("Az adatmappa gyökere a naplóknak nem jó, a mérési adatbázisnak az alapértelmezés",V(Storage.Logs,data)!=null&&Storage.Validate(Storage.MetricsKey,data,data,out var md)==null&&md=="");
Chk("Az alapértelmezés kézzel beírva: üresre normalizál",Storage.Validate(Storage.Logs,logs,data,out var ln)==null&&ln=="");

Console.WriteLine("--- Mentés a webről: naplók, mentések, mérési adatbázis áthelyezése ---");
var cfg=new ServerConfig();
DataBackup.Create(data,"proba");
var met=new MetricStore(data);
met.Insert("a1",new[]{new Metric{Key="cpu.load",Value=42,Category="CPU",Label="CPU",Unit="%"}},DateTime.UtcNow);
var newLogs=Path.Combine(root,"N naplo");var newBak=Path.Combine(root,"mentes");var newMet=Path.Combine(root,"meres");
var ch=new List<string>();
var err=Storage.Save(data,cfg,met,new Storage.Req{LogDir=newLogs,BackupDir=newBak,MetricsDir=newMet,LogMaxMb=5},ch);
Chk("Sikeres mentés, 4 változás",err==null&&ch.Count==4,err+" | "+string.Join("; ",ch));
Chk("A naplók az új helyen, a régi helyen nincsenek",File.Exists(Path.Combine(newLogs,"audit.log"))&&!File.Exists(Path.Combine(logs,"audit.log"))&&LogFolder.Dir(data)==newLogs);
Chk("A mentések átköltöztek",Directory.GetDirectories(newBak).Length==1&&DataBackup.Root(data)==newBak&&!Directory.Exists(Path.Combine(data,"backup")));
Chk("A mérési adatbázis az új helyen, az adatok megvannak, tovább írható",met.Dir==newMet&&File.Exists(Path.Combine(newMet,"metrics.db"))&&!File.Exists(Path.Combine(data,"metrics.db"))
  &&met.Latest("a1").ContainsKey("cpu.load"));
met.Insert("a1",new[]{new Metric{Key="cpu.load",Value=7,Category="CPU",Label="CPU",Unit="%"}},DateTime.UtcNow.AddSeconds(5));
Chk("…és az új érték is bekerül",met.Latest("a1")["cpu.load"].Value==7);
Chk("A beállításban az új helyek és a naplóméret",cfg.LogDir==newLogs&&cfg.BackupDir==newBak&&cfg.MetricsDir==newMet&&cfg.LogMaxMb==5);
AuditLog.Write(data,"admin","új helyen");
Chk("Az új napló az új helyre megy",File.ReadAllText(Path.Combine(newLogs,"audit.log")).Contains("új helyen"));

// 1.5.13 – a felhasználó esete: a mérési mappában már ott vannak a napi fájlok (metrics-napok), a helyet nem változtatja,
// csak a naplóméretet. Eddig: „Mérési adatbázis: A mappa nem üres" – és emiatt SEMMI nem mentődött.
Console.WriteLine("--- Változatlan hely, a mérési mappában napi fájlokkal ---");
Directory.CreateDirectory(Path.Combine(newMet,MetricStore.DaysFolder));
ch.Clear();
err=Storage.Save(data,cfg,met,new Storage.Req{LogDir=newLogs,BackupDir=newBak,MetricsDir=newMet,LogMaxMb=2},ch);
Chk("A változatlan (saját) mérési mappa nem „idegen”: a mentés sikerül, a naplóméret 2 MB",err==null&&cfg.LogMaxMb==2&&met.Dir==newMet,err??"");
var sn=new List<string>();
Chk("Induláskor is a beállított mérési mappa marad (nem áll vissza csendben az adatmappára)",Storage.MetricsDirAtStartup(data,cfg,sn)==newMet&&sn.Count==0,string.Join(" | ",sn));
ch.Clear();
err=Storage.Save(data,cfg,met,new Storage.Req{LogDir=newLogs,BackupDir=newLogs,MetricsDir=newMet},ch);
Chk("Naplók és mentések ugyanabba a mappába: érthető hiba, semmi nem változik",err!=null&&err.Contains("ugyanabban")&&ch.Count==0&&DataBackup.Root(data)==newBak,err??"");
// A mentések takarítása csak a mentés-almappákat törölheti (eddig a mappa minden almappáját a legújabb 10 után).
var keep=Path.Combine(newBak,"nem-mentes-mappa");Directory.CreateDirectory(keep);
for(int i=0;i<12;i++){var dd=Path.Combine(newBak,$"2020010{i/10}-0000{i%10:D2}-regi");Directory.CreateDirectory(dd);}
DataBackup.Create(data,"takaritas");
Chk("Takarítás: legfeljebb 10 mentés marad, a más almappa megmarad",Directory.GetDirectories(newBak).Count(d=>DataBackup.IsBackupDir(Path.GetFileName(d)))==10&&Directory.Exists(keep));
Directory.Delete(keep);

Console.WriteLine("--- Hibás útnál semmi nem változik ---");
ch.Clear();
err=Storage.Save(data,cfg,met,new Storage.Req{LogDir=foreign,BackupDir=newBak,MetricsDir=newMet},ch);
Chk("Idegen mappa: hiba, a naplók maradnak",err!=null&&ch.Count==0&&LogFolder.Dir(data)==newLogs,err??"");

Console.WriteLine("--- Vissza az alapértelmezésre ---");
ch.Clear();
err=Storage.Save(data,cfg,met,new Storage.Req{LogDir="",BackupDir="",MetricsDir=""},ch);
Chk("Minden visszakerül az adatmappába",err==null&&File.Exists(Path.Combine(logs,"audit.log"))&&File.Exists(Path.Combine(data,"metrics.db"))&&met.Dir==data
  &&Directory.GetDirectories(Path.Combine(data,"backup")).Length==1&&cfg.LogDir==""&&cfg.MetricsDir=="",err+" | "+string.Join("; ",ch));
met.Dispose();

Console.WriteLine("--- Induláskor a kézzel beírt, de használhatatlan hely: marad az alapértelmezett ---");
var n2=Storage.ApplyAtStartup(data,new ServerConfig{LogDir=@"\\halozat\naplo"},secure:false);
Chk("Figyelmeztetés, alapértelmezett naplómappa",n2.Count==1&&LogFolder.Dir(data)==logs,string.Join(" | ",n2));

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
try{Directory.Delete(root,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
