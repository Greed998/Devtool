using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
using Microsoft.Data.Sqlite;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var root=Path.Combine(Path.GetTempPath(),"mdays-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
long Count(string path){SqliteConnection.ClearAllPools();using var c=new SqliteConnection($"Data Source={path};Pooling=False");c.Open();using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM samples";return Convert.ToInt64(q.ExecuteScalar());}
var now=DateTime.UtcNow;var today=now.Date;
var st=new MetricStore(root);
// 5 és 4 nappal ezelőtti nap (percenként 2 óra), és a mai nap
void Fill(DateTime day,string agent,double v){var l=new List<(DateTime,IReadOnlyList<Metric>)>();
  for(var m=0;m<120;m++)l.Add((day.AddHours(10).AddMinutes(m),new List<Metric>{new Metric{Key="cpu.load",Value=v,Category="CPU",Label="CPU",Unit="%"}}));st.InsertMany(agent,l);}
Fill(today.AddDays(-5),"a1",10);Fill(today.AddDays(-4),"a1",20);Fill(today.AddDays(-4),"a2",50);Fill(today,"a1",30);

Console.WriteLine("--- Lezárás: a 48 óránál régebbi napok külön fájlba költöznek ---");
var more=st.CloseDays();
var files=st.DayFiles();
Chk("Két lezárt napi fájl, a mai nap nem",files.Count==2&&files[0].Day==today.AddDays(-5).ToString("yyyy-MM-dd")&&files[1].Day==today.AddDays(-4).ToString("yyyy-MM-dd"),string.Join(",",files.Select(f=>f.Day)));
Chk("Nem maradt hátra lezárandó",!more);
Chk("A sorok átkerültek (nincs kettő): a metrics.db-ben csak a mai",Count(Path.Combine(root,"metrics.db"))==120&&Count(files[0].Path)==120&&Count(files[1].Path)==240);
st.CloseDays();
Chk("Ismételt lezárás nem duplikál",Count(files[1].Path)==240);

Console.WriteLine("--- Lekérdezés több fájlból ---");
var s=st.Series("a1","cpu.load",24*7);
Chk("Idősor a napi fájlokból és a metrics.db-ből",s.Count>0&&s.Any(p=>Math.Abs(p.Value-10)<0.01)&&s.Any(p=>Math.Abs(p.Value-20)<0.01)&&s.Any(p=>Math.Abs(p.Value-30)<0.01),s.Count.ToString());
var from=new DateTimeOffset(today.AddDays(-6)).ToUnixTimeSeconds();
var (rows,next)=st.Buckets(from,300,100000);
Chk("5 perces sávok időrendben, a fájlokból is",rows.Count>0&&rows.Select(r=>r.Ts).SequenceEqual(rows.Select(r=>r.Ts).OrderBy(t=>t))&&rows.Any(r=>r.Value==10)&&rows.Any(r=>r.AgentId=="a2"));
var (rows2,_)=st.Buckets(from,300,10);
Chk("A sorlimit több forráson át is",rows2.Count<=10&&rows2.Count>0);
var daily=st.Daily(10);
Chk("Napi összesítő a fájlokból is",daily.Any(d=>d.AgentId=="a2"&&Math.Abs(d.Avg-50)<0.01&&d.Count==120),daily.Count.ToString());

Console.WriteLine("--- Ritkítás, archívum, feltöltésre tartás ---");
MetricStore.MaintainDir(st.DaysDir,detailDays:3,keepDays:30,archiveDays:0,holdForUpload:false);
Chk("A részletes időn túl órás átlag (2 óra → 2 sor gépenként)",Count(st.DayFiles()[0].Path)==2&&Count(st.DayFiles()[1].Path)==4);
Chk("Az órás átlag értéke megmarad",st.Series("a2","cpu.load",24*7).All(p=>Math.Abs(p.Value-50)<0.01));
Chk("Feltöltendő (3 napnál régebbi): mindkettő; 4 napnál: csak a régebbi",st.DueFiles(3).Count==2&&st.DueFiles(4).Count==1);
MetricStore.MaintainDir(st.DaysDir,1,3,0,holdForUpload:true);
Chk("Feltöltésre tartás: a megőrzés után sem törlődik",st.DayFiles().Count==2);
st.PurgeRange(new HashSet<string>{"a2"},new DateTimeOffset(today.AddDays(-4)).ToUnixTimeSeconds(),new DateTimeOffset(today.AddDays(-3)).ToUnixTimeSeconds());
Chk("Gép adatainak törlése egy napra (a Központ: a feltöltött fájl kiváltja)",Count(st.DayFiles()[1].Path)==2);
st.DeleteDayFile(st.DayFiles()[1].Day);
Chk("A nyugtázott napi fájl törlése",st.DayFiles().Count==1);
MetricStore.MaintainDir(st.DaysDir,1,3,archiveDays:10,holdForUpload:false);
var arch=Path.Combine(st.DaysDir,"archiv");
Chk("A megőrzés után ZIP-archívum (egy példány)",st.DayFiles().Count==0&&Directory.GetFiles(arch,"*.zip").Length==1);
MetricStore.MaintainDir(st.DaysDir,1,2,archiveDays:4,holdForUpload:false);
Chk("Az archívum ideje után törlődik",Directory.GetFiles(arch,"*.zip").Length==0);
Chk("Hibás napnév nem törlődik / nem értelmeződik",!MetricStore.TryDay("../x",out _)&&!MetricStore.TryDay("2026-13-01",out _));

Console.WriteLine("--- Áthelyezés a napi fájlokkal együtt ---");
Fill(today.AddDays(-3),"a1",40);st.CloseDays();
var nd=Path.Combine(root,"uj");
Chk("Áthelyezés",st.Relocate(nd) is null&&File.Exists(Path.Combine(nd,"metrics.db"))&&st.DayFiles().Count==1&&st.DayFiles()[0].Path.StartsWith(nd),string.Join(",",st.DayFiles().Select(f=>f.Path)));

Console.WriteLine("--- A Központ fogadja a lezárt napi fájlt (aláírt fejléc, lenyomat, nyugta) ---");
var hubDir=Path.Combine(root,"hub");Directory.CreateDirectory(hubDir);
var hub=new DevToolbox.Fleet.Hub.HubStore(hubDir);
var siteKeys=Crypto.NewKeyPair();
var en=hub.Enroll(new HubEnrollRequest{Token=hub.CreateToken("t",60,1),PublicKeyPem=siteKeys.PublicKeyPem,SiteName="Iroda",ServerVersion="1.5.8"});
var own=new MetricStore(Path.Combine(hubDir,"own"));
// a telephely gépei (élesben a betárcsázás összesítőjéből) – csak ezek saját másolata törölhető
hub.Sites.First(x=>x.SiteId==en.SiteId).LastSummary=new SiteSummary{Agents=new(){new SiteAgent{AgentId="ag1"}}};
DevToolbox.Fleet.Hub.HubMetricArchive.Init(own.Dir);
// a feltöltendő fájl: a telephely egy lezárt napja
var siteSt=new MetricStore(Path.Combine(root,"site"));
var dayOld=today.AddDays(-35);
var l2=new List<(DateTime,IReadOnlyList<Metric>)>();for(var m=0;m<60;m++)l2.Add((dayOld.AddHours(8).AddMinutes(m),new List<Metric>{new Metric{Key="cpu.load",Value=42}}));
siteSt.InsertMany("ag1",l2);siteSt.CloseDays();
var df=siteSt.DayFiles().Single();
// a Központ saját 5 perces másolata ugyanarra a napra (ennek törlődnie kell, ha a fájl megérkezik)
own.InsertMany("ag1",new[]{(dayOld.AddHours(8),(IReadOnlyList<Metric>)new List<Metric>{new Metric{Key="cpu.load",Value=42}})});
SqliteConnection.ClearAllPools();
byte[] Gz(string p){using var ms=new MemoryStream();using(var z=new System.IO.Compression.GZipStream(ms,System.IO.Compression.CompressionLevel.Optimal,true))using(var f=File.OpenRead(p))f.CopyTo(z);return ms.ToArray();}
var gz=Gz(df.Path);
(string Env,MetricFileHeader H) Hdr(string day,byte[] body,string? sha=null,string? keyPem=null){var h=new MetricFileHeader{Day=day,Sha256=sha??Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)),Size=body.Length,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce()};
  return (Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ProtocolJson.Serialize(SignedEnvelope.Create(en.SiteId,h,keyPem??siteKeys.PrivateKeyPem)))),h);}
async Task<MetricFileReceipt> Send((string Env,MetricFileHeader H) x,byte[] body){var r=await DevToolbox.Fleet.Hub.HubMetricArchive.Receive(x.Env,new MemoryStream(body),hub,own);
  Chk("  (a nyugta a Központ kulcsával aláírt)",r.Verify(hub.Keys.PublicKeyPem));return r.Open<MetricFileReceipt>()!;}
var bad1=await Send(Hdr(df.Day,gz,keyPem:Crypto.NewKeyPair().PrivateKeyPem),gz);
Chk("Idegen kulccsal aláírt fejléc elutasítva",!bad1.Ok);
var bad2=await Send(Hdr(df.Day,gz,sha:new string('0',64)),gz);
Chk("Eltérő lenyomat elutasítva",!bad2.Ok&&bad2.Message.Contains("lenyomat"),bad2.Message);
var bad3=await Send(Hdr(today.AddDays(-1).ToString("yyyy-MM-dd"),gz),gz);
Chk("Még le nem zárt nap elutasítva",!bad3.Ok&&bad3.Message.Contains("lezárva"),bad3.Message);
var bad4=await Send(Hdr(dayOld.AddDays(-1).ToString("yyyy-MM-dd"),gz),gz);
Chk("Más napra eső mérések elutasítva",!bad4.Ok,bad4.Message);
var hdrOk=Hdr(df.Day,gz);
var ok=await Send(hdrOk,gz);
Chk("Érvényes feltöltés: nyugta, ehhez a kéréshez kötve",ok.Ok&&ok.RequestNonce==hdrOk.H.Nonce&&ok.Day==df.Day,ok.Message);
Chk("Ugyanaz a kérés másodszor nem (nonce)",!(await Send(hdrOk,gz)).Ok);
var hubFiles=DevToolbox.Fleet.Hub.HubMetricArchive.Files(en.SiteId);
Chk("A fájl a telephely mappájában, és a Központ saját másolata arra a napra törölve",hubFiles.Count==1&&own.Series("ag1","cpu.load",24*40).Count==0);
var hs=DevToolbox.Fleet.Hub.HubMetricArchive.Series(en.SiteId,"ag1","cpu.load",24*40);
Chk("A gép grafikonja a feltöltött fájlból",hs.Count>0&&hs.All(p=>Math.Abs(p.Value-42)<0.01),hs.Count.ToString());
siteSt.Dispose();own.Dispose();

st.Dispose();SqliteConnection.ClearAllPools();
try{Directory.Delete(root,true);}catch{}
Console.WriteLine(fail==0?"MINDEN TESZT ATMENT":$"{fail} HIBA");
return fail==0?0:1;
