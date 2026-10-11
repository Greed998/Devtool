using DevToolbox.Fleet;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

// 1.5.10 – Windows-szolgáltatás kezelése (indít / leállít / újraindít).
// A szolgáltatásnév ellenőrzése (a szerver ES az agent is ezt hívja).
Console.WriteLine("--- ServiceName.IsValid ---");
Chk("Egyszerű név jó", ServiceName.IsValid("Spooler"));
Chk("Pont, kötőjel, szóköz a névben jó", ServiceName.IsValid("MSSQL$SQLEXPRESS"));
Chk("Üres nem jó", !ServiceName.IsValid(""));
Chk("Csak szóköz nem jó", !ServiceName.IsValid("   "));
Chk("null nem jó", !ServiceName.IsValid(null));
Chk("Perjel nem jó", !ServiceName.IsValid("a/b"));
Chk("Visszaperjel nem jó", !ServiceName.IsValid(@"a\b"));
Chk("Idézőjel nem jó", !ServiceName.IsValid("a\"b"));
Chk("Vezérlőkarakter nem jó", !ServiceName.IsValid("a\nb"));
Chk("Körülvevő szóköz nem jó (trim-eltérés)", !ServiceName.IsValid(" Spooler "));
Chk("256-nál hosszabb nem jó", !ServiceName.IsValid(new string('x',257)));
Chk("Pontosan 256 még jó", ServiceName.IsValid(new string('x',256)));

// Az új feladat-típusok a sor VÉGÉN vannak (a régi agentek szám szerint kapják: a meglévők ordinálja nem csúszhat el).
Console.WriteLine("--- AgentTaskKind bővítés ---");
Chk("UpgradeApp = 0 (változatlan)", (int)AgentTaskKind.UpgradeApp==0);
Chk("RebootNow = 10 (változatlan)", (int)AgentTaskKind.RebootNow==10);
Chk("ServiceStart = 11", (int)AgentTaskKind.ServiceStart==11);
Chk("ServiceStop = 12", (int)AgentTaskKind.ServiceStop==12);
Chk("ServiceRestart = 13", (int)AgentTaskKind.ServiceRestart==13);

// A feladat szerializációja oda-vissza (a hálózaton így megy), aláírással együtt.
Console.WriteLine("--- AgentTask szerializáció ---");
var t=new AgentTask{Kind=AgentTaskKind.ServiceRestart,Target="Spooler"};
var back=ProtocolJson.Deserialize<AgentTask>(ProtocolJson.Serialize(t))!;
Chk("Kind túléli a szerializációt", back.Kind==AgentTaskKind.ServiceRestart);
Chk("Target túléli a szerializációt", back.Target=="Spooler");

Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
