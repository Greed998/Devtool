using System.Net;
using System.Net.Sockets;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"notify-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
Environment.SetEnvironmentVariable("FLEET_HUB_DATA",dir);   // a HubMailer műveleti naplója ide írjon, ne a ProgramData-ba
var lim=TimeSpan.FromMinutes(15);
OfflineCheck.Item It(string k,string n,double minAgo,bool freeze=false)=>new(k,n,DateTime.UtcNow.AddMinutes(-minAgo),lim,freeze);

Console.WriteLine("--- Kiesés-riasztás ---");
OfflineCheck.StartedUtc=DateTime.UtcNow.AddHours(-2);
var off=new AlertStore(Path.Combine(dir,"off"));
var ev=OfflineCheck.Run(off,new[]{It("g1","PC1",5),It("g2","PC2",20)});
Chk("5 perce jelentkezett: nem riaszt; 20 perce nem: riaszt",ev.Count==1&&ev[0].Raised&&ev[0].MachineName=="PC2",string.Join(",",ev.Select(e=>e.MachineName)));
Chk("A riasztás szövege érthető",ev[0].Detail.Contains("20 perce nem jelentkezett")&&ev[0].Detail.Contains("15 perc"),ev[0].Detail);
Chk("Az aktív listában ott a kiesés",off.ActiveStates().Any(s=>s.RuleId==AlertStore.OfflineRuleId&&s.AgentId=="g2"&&s.Name=="PC2"));
Chk("Ismételt ellenőrzés: nem riaszt újra",OfflineCheck.Run(off,new[]{It("g1","PC1",6),It("g2","PC2",21)}).Count==0);

var off2=new AlertStore(Path.Combine(dir,"off"));
Chk("A jelzett kiesés túléli az újraindítást",off2.IsOffline("g2"));
OfflineCheck.StartedUtc=DateTime.UtcNow;   // most „indult” a szerver
ev=OfflineCheck.Run(off2,new[]{It("g2","PC2",60),It("g3","PC3",60)});
Chk("Indulás után: a már jelzett kiesés marad, új nem keletkezik (türelmi idő)",ev.Count==0&&off2.IsOffline("g2")&&!off2.IsOffline("g3"));
ev=OfflineCheck.Run(off2,new[]{It("g2","PC2",0),It("g3","PC3",60)});
Chk("Újra jelentkezik: a kiesés megszűnik",ev.Count==1&&!ev[0].Raised&&ev[0].MachineName=="PC2"&&!off2.IsOffline("g2"));

OfflineCheck.StartedUtc=DateTime.UtcNow.AddHours(-2);
OfflineCheck.Run(off2,new[]{It("g4","Telephely / PC4",30)});
ev=OfflineCheck.Run(off2,new[]{It("g4","Telephely / PC4",0,freeze:true)});
Chk("Kiesett telephely gépe (befagyasztva): marad, ami volt",ev.Count==0&&off2.IsOffline("g4"));
ev=OfflineCheck.Run(off2,Array.Empty<OfflineCheck.Item>());
Chk("Törölt / nem figyelt gép kiesése csendben megszűnik",ev.Count==0&&!off2.IsOffline("g4"));
Chk("Csoportszűrés: üres lista = minden gép",OfflineCheck.Watched(new List<string>(),new[]{"Iroda"}));
Chk("Csoportszűrés: kis-nagybetű nem számít",OfflineCheck.Watched(new[]{"Szerverek"},new[]{"szerverek"}));
Chk("Csoportszűrés: más csoport kimarad",!OfflineCheck.Watched(new[]{"Szerverek"},new[]{"Iroda"})&&!OfflineCheck.Watched(new[]{"Szerverek"},Array.Empty<string>()));
Chk("Küszöb: 0 = ki, legalább 10 perc",ServerConfig.ClampOfflineMinutes(0)==0&&ServerConfig.ClampOfflineMinutes(3)==10&&ServerConfig.ClampOfflineMinutes(15)==15);
Chk("Alapból bekapcsolt, 15 perc (a felhasználó döntése)",new ServerConfig().OfflineAlertMinutes==15&&new ServerConfig().OfflineAlertMachines);

Console.WriteLine("--- Riasztásnapló és értesítés ---");
AlertLog.Dir=Path.Combine(dir,"log");
var notified=new List<AlertEvent>(); AlertLog.Notify=notified.Add;
AlertLog.Write(new AlertEvent("CPU magas","a1","Ügyfél Kft. / PC-01",95.5,90,">",true,DateTime.UtcNow));
AlertLog.Write(new AlertEvent(AlertStore.OfflineRuleName,"a2","Ügyfél Kft. / Fleet szerver",0,15,"≥",false,DateTime.UtcNow,"újra jelentkezik"));
Chk("A naplóírás az értesítőnek is szól (Központ e-mail)",notified.Count==2);
var ent=AlertLog.Entries();
Chk("A napló sorai visszaolvashatók (CSV-hez)",ent.Count==2&&ent[0].Raised&&ent[0].Machine=="Ügyfél Kft. / PC-01"&&ent[0].Rule=="CPU magas"&&(ent[0].Detail=="95,5 > 90"||ent[0].Detail=="95.5 > 90"),
    string.Join(" | ",ent.Select(e=>$"{e.Machine}|{e.Rule}|{e.Detail}")));
Chk("Megszűnés + szöveges részlet",!ent[1].Raised&&ent[1].Rule==AlertStore.OfflineRuleName&&ent[1].Detail=="újra jelentkezik");
AlertLog.Notify=null;

Console.WriteLine("--- CSV-jelentések ---");
string Csv(byte[] b)=>Encoding.UTF8.GetString(b,3,b.Length-3);
var m=new CsvReport.Machine("Ügyfél Kft.","a1","PC-01","Windows 11 Pro","1.4.2",true,false,DateTime.UtcNow,new[]{"Iroda","Könyvelés"},
    new[]{new AppUpdate{Id="7zip.7zip",Name="7-Zip",Installed="23.01",Available="24.08"}},
    new[]{new WinUpdate{Kb="KB5031356",Title="2023-10 frissítés; kumulatív",Severity="Critical",RebootRequired=true,SizeMb=512.5}},
    new[]{new InstalledApp{Id="Evil.App",Name="=HYPERLINK(\"http://x\")",Version="3.12",Source="winget"},new InstalledApp{Id="ARP\\X",Name="-2+3",Version="1.0 beta"}},true);
var gep=CsvReport.Machines(new[]{m},true);
Chk("UTF-8 BOM (Excel ékezetek)",gep[0]==0xEF&&gep[1]==0xBB&&gep[2]==0xBF);
var g=Csv(gep);
Chk("Pontosvesszős fejléc, Telephely oszloppal",g.StartsWith("Telephely;Gép;Állapot;"),g.Split('\n')[0]);
Chk("Csoportok, igen/nem, darabszámok",g.Contains("Iroda, Könyvelés")&&g.Contains(";igen;")&&g.Contains(";1;1;"),g.Split('\n')[1]);
Chk("Agent-verzió szövegként (az Excel nem alakítja dátummá)",g.Contains("=\"1.4.2\""));
var prog=Csv(CsvReport.Programs(new[]{m},false));
Chk("Szerveren nincs Telephely oszlop",prog.StartsWith("Gép;Program;"),prog.Split('\n')[0]);
Chk("Képlet-befecskendezés ellen aposztróf",prog.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"")&&prog.Contains("'-2+3"),prog);
Chk("Szám-szerű verzió szövegként, a többi sima szöveg",prog.Contains("=\"3.12\"")&&prog.Contains(";1.0 beta;"));
var upd=Csv(CsvReport.Updates(new[]{m},true));
Chk("Frissítések: program és Windows sor, idézett pontosvessző, tizedesvessző",upd.Contains(";program;7-Zip;")&&upd.Contains("\"2023-10 frissítés; kumulatív\"")&&upd.Contains("512,5"),upd);
var alr=Csv(CsvReport.Alerts(ent,true));
Chk("Riasztásnapló: telephely és gép külön oszlopban",alr.Contains("Ügyfél Kft.;PC-01;")&&alr.Contains("Ügyfél Kft.;Fleet szerver;"),alr);

var ms=new MetricStore(Path.Combine(dir,"m"));
var t0=DateTime.UtcNow.Date.AddHours(10);
ms.InsertMany("a1",new[]{10.0,20,60}.Select((v,i)=>(t0.AddMinutes(i),(IReadOnlyList<Metric>)new List<Metric>{new(){Key="cpu.load",Label="CPU",Value=v,Unit="%",Category="CPU"}})));
ms.InsertMany("idegen",new[]{(t0,(IReadOnlyList<Metric>)new List<Metric>{new(){Key="cpu.load",Value=1}})});
var daily=ms.Daily(7);
Chk("Napi összesítő: átlag / min / max",daily.Count(r=>r.AgentId=="a1")==1&&daily.First(r=>r.AgentId=="a1") is {Avg:30,Min:10,Max:60,Count:3},
    string.Join(";",daily.Select(r=>$"{r.AgentId} {r.Day} {r.Avg} {r.Min} {r.Max}")));
var met=Csv(CsvReport.Metrics(daily,new Dictionary<string,(string,string)>{["a1"]=("Ügyfél Kft.","PC-01")},true));
Chk("Metrika-CSV: csak a látható gépek",met.Contains("Ügyfél Kft.;PC-01;")&&met.Contains(";CPU;CPU;cpu.load;30;10;60;%;3")&&!met.Contains("idegen"),met);
ms.Dispose();

Console.WriteLine("--- E-mail beállítás (DPAPI, ellenőrzés) ---");
var enc=NativeDpapi.Protect("Titkos-Jelszó1");
Chk("DPAPI: a titkosított alak nem a jelszó, visszafejthető",!enc.Contains("Titkos")&&NativeDpapi.Unprotect(enc)=="Titkos-Jelszó1");
Chk("DPAPI: sérült adatból nincs jelszó",NativeDpapi.Unprotect("AAAA")==null);
var c=new ServerConfig();
Chk("Érvénytelen cím elutasítva",HubMailer.Apply(c,true,"smtp.x.hu",587,"starttls","u","p",false,"nem-cim","a@b.hu") is not null);
Chk("Sortörés (fejléc-befecskendezés) elutasítva",HubMailer.Apply(c,true,"smtp.x.hu",587,"starttls","u","p",false,"a@b.hu","c@d.hu\r\nBcc: x@y.hu") is not null);
Chk("Jelszó titkosítatlan kapcsolaton elutasítva",HubMailer.Apply(c,true,"smtp.x.hu",25,"none","u","p",false,"a@b.hu","c@d.hu") is not null);
Chk("Bekapcsolás címzett nélkül elutasítva",HubMailer.Apply(c,true,"smtp.x.hu",587,"starttls","","",false,"a@b.hu","") is not null);
var okA=HubMailer.Apply(c,true,"smtp.x.hu",465,"ssl","fleet@x.hu","Titkos-Jelszó1",false,"fleet@x.hu","a@b.hu; c@d.hu");
Chk("Helyes beállítás elfogadva, címzettek szétbontva",okA is null&&c.MailTo.Count==2&&c.SmtpSecurity=="ssl",okA??"");
Chk("A jelszó csak titkosítva kerül a konfigurációba",c.SmtpPasswordProtected.Length>0&&!c.SmtpPasswordProtected.Contains("Titkos")&&NativeDpapi.Unprotect(c.SmtpPasswordProtected)=="Titkos-Jelszó1");
var keep=c.SmtpPasswordProtected; HubMailer.Apply(c,true,"smtp.x.hu",465,"ssl","fleet@x.hu","",false,"fleet@x.hu","a@b.hu");
Chk("Üres jelszómező: a tárolt marad",c.SmtpPasswordProtected==keep);
HubMailer.Apply(c,true,"smtp.x.hu",465,"ssl","fleet@x.hu","",true,"fleet@x.hu","a@b.hu");
Chk("Jelszó törlése",c.SmtpPasswordProtected=="");

Console.WriteLine("--- SMTP-küldés (helyi kamu szerver) ---");
// Egyetlen kapcsolatot kiszolgáló SMTP-szerver; a parancsokat és a levelet rögzíti. rejectRcpt / rejectAuth: hibaág.
async Task<(List<string> Cmds,string Data)> FakeSmtp(TcpListener l,bool rejectAuth=false,string rejectRcpt="")
{
    using var cl=await l.AcceptTcpClientAsync(); var s=cl.GetStream();
    var r=new StreamReader(s,new UTF8Encoding(false)); var w=new StreamWriter(s,new UTF8Encoding(false)){NewLine="\r\n",AutoFlush=true};
    var cmds=new List<string>(); var data=new StringBuilder();
    await w.WriteLineAsync("220 kamu ESMTP");
    while(await r.ReadLineAsync() is {} line){
        cmds.Add(line); var u=line.ToUpperInvariant();
        if(u.StartsWith("EHLO")){await w.WriteAsync("250-kamu\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n");}
        else if(u.StartsWith("AUTH")){await w.WriteLineAsync(rejectAuth?"535 5.7.8 Authentication failed":"235 2.7.0 OK"); if(rejectAuth)break;}
        else if(u.StartsWith("MAIL")){await w.WriteLineAsync("250 OK");}
        else if(u.StartsWith("RCPT")){if(rejectRcpt.Length>0&&line.Contains(rejectRcpt)){await w.WriteLineAsync("550 5.1.1 no such user");break;}await w.WriteLineAsync("250 OK");}
        else if(u=="DATA"){await w.WriteLineAsync("354 go");while(await r.ReadLineAsync() is {} d&&d!=".")data.AppendLine(d);await w.WriteLineAsync("250 queued");}
        else if(u=="QUIT"){await w.WriteLineAsync("221 bye");break;}
        else await w.WriteLineAsync("500 ?");
    }
    return (cmds,data.ToString());
}
var lis=new TcpListener(IPAddress.Loopback,0); lis.Start(); var port=((IPEndPoint)lis.LocalEndpoint).Port;
SmtpMail.Settings S(string user="",string pw="",params string[] to)=>new("127.0.0.1",port,"none",user,pw,"fleet@x.hu",to.Length>0?to.ToList():new(){"a@b.hu","c@d.hu"},"fleet.poltoli.hu");

var srv=FakeSmtp(lis);
await SmtpMail.SendAsync(S("fleet@x.hu","Titkos-Jelszó1"),"[DevToolbox] RIASZTÁS: Ügyfél / PC – Nem jelentkezik","Első sor ékezettel: árvíztűrő\r\n.\r\nsor ponttal");
var (cmds,data)=await srv;
var authLine=cmds.FirstOrDefault(x=>x.StartsWith("AUTH PLAIN "))??"";
var authDec=authLine.Length>11?Encoding.UTF8.GetString(Convert.FromBase64String(authLine[11..])):"";
Chk("EHLO a Központ domainjével",cmds.Contains("EHLO fleet.poltoli.hu"),string.Join(" | ",cmds));
Chk("AUTH PLAIN a felhasználóval és jelszóval",authDec=="\0fleet@x.hu\0Titkos-Jelszó1");
Chk("Feladó és mindkét címzett",cmds.Contains("MAIL FROM:<fleet@x.hu>")&&cmds.Contains("RCPT TO:<a@b.hu>")&&cmds.Contains("RCPT TO:<c@d.hu>"));
var subj=data.Split("\r\n").FirstOrDefault(x=>x.StartsWith("Subject: "))??"";
var subjDec=subj.StartsWith("Subject: =?UTF-8?B?")?Encoding.UTF8.GetString(Convert.FromBase64String(subj[19..^2])):"";
Chk("Ékezetes tárgy kódolva",subjDec=="[DevToolbox] RIASZTÁS: Ügyfél / PC – Nem jelentkezik",subj);
var bodyB64=string.Concat(data.Split("\r\n\r\n",2)[1].Split("\r\n",StringSplitOptions.RemoveEmptyEntries));
Chk("Törzs base64-ben, ékezettel és ponttal a sor elején is épen",Encoding.UTF8.GetString(Convert.FromBase64String(bodyB64))=="Első sor ékezettel: árvíztűrő\r\n.\r\nsor ponttal");

srv=FakeSmtp(lis,rejectAuth:true);
string err="";try{await SmtpMail.SendAsync(S("fleet@x.hu","Titkos-Jelszó1"),"x","y");}catch(Exception ex){err=ex.Message;} await srv;
Chk("Hibás jelszó: érthető hiba, a jelszó nincs benne",err.Contains("535")&&!err.Contains("Titkos")&&!err.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Titkos-Jelszó1"))),err);
srv=FakeSmtp(lis,rejectRcpt:"rossz@b.hu");
err="";try{await SmtpMail.SendAsync(S("","","rossz@b.hu"),"x","y");}catch(Exception ex){err=ex.Message;} await srv;
Chk("Elutasított címzett: a szerver válasza a hibában",err.Contains("550")&&err.Contains("RCPT"),err);
lis.Stop();
err="";try{await SmtpMail.SendAsync(S(),"x","y");}catch(Exception ex){err=ex.Message;}
Chk("Nem elérhető szerver: érthető hiba",err.Contains("nem érhető el"),err);

var tc=new ServerConfig(); HubMailer.Apply(tc,true,"127.0.0.1",port,"none","","",false,"fleet@x.hu","a@b.hu");
var (tok,tmsg)=await HubMailer.SendTestAsync(tc,"admin");
Chk("Teszt e-mail hibája a felületnek visszaadva",!tok&&tmsg.Contains("nem érhető el"),tmsg);

try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
