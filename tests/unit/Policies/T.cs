using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
using DevToolbox.Fleet.Agent;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
DateTime T(int dow,int h){var d=new DateTime(2026,9,27,h,15,0); /* vasárnap */ return d.AddDays(dow);}  // 0=V,1=H...

Console.WriteLine("--- Karbantartási ablak ---");
var wk=new List<int>{1,2,3,4,5};
Chk("Hétfő 23:15 benne (22–04)", PolicyEngine.InWindow(T(1,23),wk,22,4));
Chk("Kedd 03:15 benne (hétfő éjszakai ablak folytatása)", PolicyEngine.InWindow(T(2,3),wk,22,4));
Chk("Hétfő 03:15 NINCS benne (vasárnap nincs ablak)", !PolicyEngine.InWindow(T(1,3),wk,22,4));
Chk("Szombat 02:15 benne (péntek éjszaka folytatása)", PolicyEngine.InWindow(T(6,2),wk,22,4));
Chk("Szombat 23:15 NINCS benne", !PolicyEngine.InWindow(T(6,23),wk,22,4));
Chk("Nappali ablak 12–14: 13:15 benne, 14:15 nem", PolicyEngine.InWindow(T(3,13),wk,12,14) && !PolicyEngine.InWindow(T(3,14),wk,12,14));
Chk("Nap nélkül soha", !PolicyEngine.InWindow(T(3,23),new List<int>(),22,4));

Console.WriteLine("--- Szabálykiértékelés ---");
var ag=new EnrolledAgent{AgentId="a",MachineName="PC",Groups=new(){"Könyvelés"},LastStatus=new AgentStatus{UpdatesAvailable=3,PendingReboot=true}};
ag.LastStatus.WindowsUpdates.Add(new WinUpdate{UpdateId=Guid.NewGuid().ToString()});
var pol=new AutoPolicy{Name="Éjjel",Group="könyvelés",Apps=true,Windows=true,Reboot=true,Days=wk,StartHour=22,EndHour=4};
var now=DateTime.UtcNow;
var t1=PolicyEngine.Evaluate(new[]{pol},ag,T(1,23),now);
Chk("Ablakban: program + Windows + újraindítás, ebben a sorrendben", t1.Select(t=>t.Kind).SequenceEqual(new[]{AgentTaskKind.UpgradeAll,AgentTaskKind.WindowsUpdateInstall,AgentTaskKind.Reboot}), string.Join(",",t1.Select(t=>t.Kind)));
Chk("Csoportnév kis/nagybetű független", t1.Count==3);
Chk("Ugyanabban az ablakban NEM adja ki újra", PolicyEngine.Evaluate(new[]{pol},ag,T(2,1),now.AddHours(2)).Count==0);
Chk("Másnap (20 óra után) újra kiadja", PolicyEngine.Evaluate(new[]{pol},ag,T(2,23),now.AddHours(24)).Count==3);
Chk("Ablakon kívül semmi", PolicyEngine.Evaluate(new[]{pol},new EnrolledAgent{Groups=new(){"Könyvelés"},LastStatus=ag.LastStatus},T(3,12),now).Count==0);
Chk("Más csoport gépére nem vonatkozik", PolicyEngine.Evaluate(new[]{pol},new EnrolledAgent{Groups=new(){"Raktár"},LastStatus=ag.LastStatus},T(1,23),now).Count==0);
var noReboot=new EnrolledAgent{Groups=new(){"Könyvelés"},LastStatus=new AgentStatus{UpdatesAvailable=0,PendingReboot=false}};
Chk("Nincs frissítés és nincs függő újraindítás: semmi (újraindítás sem)", PolicyEngine.Evaluate(new[]{pol},noReboot,T(1,23),now).Count==0);
var p2=new AutoPolicy{Apps=true,Days=wk,StartHour=22,EndHour=4,Enabled=false};
Chk("Kikapcsolt szabály nem ad ki semmit", PolicyEngine.Evaluate(new[]{p2},new EnrolledAgent{LastStatus=ag.LastStatus},T(1,23),now).Count==0);
var p3=new AutoPolicy{Apps=true,Days=wk,StartHour=22,EndHour=4};
Chk("Csoport nélküli szabály minden gépre", PolicyEngine.Evaluate(new[]{p3},new EnrolledAgent{LastStatus=ag.LastStatus},T(1,23),now).Count==1);
Chk("Visszavont gépre semmi", PolicyEngine.Evaluate(new[]{p3},new EnrolledAgent{Revoked=true,LastStatus=ag.LastStatus},T(1,23),now).Count==0);

Console.WriteLine("--- Azonosító-ellenőrzés ---");
Chk("7zip.7zip jó", WingetId.IsValid("7zip.7zip"));
Chk("ARP-azonosító szóközzel jó", WingetId.IsValid(@"ARP\Machine\X64\Adobe Acrobat Reader DC"));
Chk("Idézőjeles (parancs-injektálás) elutasítva", !WingetId.IsValid("x\" --source evil"));
Chk("Kapcsolóval kezdődő elutasítva", !WingetId.IsValid("--override"));
Chk("Sortörés elutasítva", !WingetId.IsValid("a\nb"));
Chk("Üres elutasítva", !WingetId.IsValid(" "));

Console.WriteLine("--- winget list feldolgozás ---");
string Row(params (string v,int w)[] c)=>string.Concat(c.Select(x=>x.v.PadRight(x.w)));
var W=new[]{21,23,10,10,6};
var en=Row(("Name",W[0]),("Id",W[1]),("Version",W[2]),("Available",W[3]),("Source",W[4]))+"\n"+new string('-',70)+"\n"+
  Row(("7-Zip 23.01 (x64)",W[0]),("7zip.7zip",W[1]),("23.01",W[2]),("24.08",W[3]),("winget",W[4]))+"\n"+
  Row(("Microsoft Edge",W[0]),("Microsoft.Edge",W[1]),("129.0",W[2]),("",W[3]),("winget",W[4]))+"\n"+
  Row(("HP Support Assistant",W[0]),(@"ARP\Machine\X86\HPSA",W[1]),("9.1",W[2]),("",W[3]),("",W[4]))+"\n";
Chk("  (tesztadat: Available és Source között 1 szóköz)", en.Contains("Available Source"));
var l1=AgentWinget.ParseList(en);
Chk("Angol kimenet: 3 program", l1.Count==3, l1.Count.ToString());
Chk("Azonosító, verzió, forrás helyes", l1[0].Id=="7zip.7zip" && l1[0].Version=="23.01" && l1[0].Source=="winget", $"{l1[0].Id}|{l1[0].Version}|{l1[0].Source}");
Chk("ARP-bejegyzés forrás nélkül", l1[2].Id==@"ARP\Machine\X86\HPSA" && l1[2].Source=="", l1[2].Id+"|"+l1[2].Source);
var up=AgentWinget.Parse(en);
Chk("A frissítés-lista (upgrade) is jó: elérhető = 24.08, forrás nélkül", up.Count>=1 && up[0].Available=="24.08", up.Count>0?up[0].Available:"-");
var hu="Név                  Azonosító             Verzió    Forrás\n----------------------------------------------------------------\nMozilla Firefox (x64) Mozilla.Firefox      131.0     winget\nNotepad++ (64-bit)   Notepad++.Notepad++   8.6.9     winget\n";
var l2=AgentWinget.ParseList(hu);
Chk("Magyar kimenet (Elérhető oszlop nélkül): 2 program", l2.Count==2 && l2[1].Id=="Notepad++.Notepad++" && l2[1].Source=="winget", string.Join(";",l2.Select(x=>x.Id+"/"+x.Source)));
Chk("Üres kimenet: üres lista", AgentWinget.ParseList("").Count==0);
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
