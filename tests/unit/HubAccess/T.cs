using System.Net;
using DevToolbox.Fleet.Hub;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
IPAddress P(string s)=>IPAddress.Parse(s);
Console.WriteLine("--- Központ: ki éri el az admin felületet ---");
Chk("LAN-ról közvetlenül: engedve", HubAccess.AdminAllowed(P("192.168.1.20"),null,null));
Chk("NPM mögül, külső kliens (X-Real-IP 8.8.8.8): tiltva", !HubAccess.AdminAllowed(P("192.168.1.5"),"8.8.8.8",null));
Chk("NPM mögül, belső kliens: engedve", HubAccess.AdminAllowed(P("192.168.1.5"),"192.168.1.40",null));
Chk("Hamisított XFF – az NPM a valódi IP-t utolsónak fűzi: tiltva", !HubAccess.AdminAllowed(P("192.168.1.5"),null,"192.168.1.9, 8.8.8.8"));
Chk("Publikus közvetlen kapcsolat fejléce nem számít", !HubAccess.AdminAllowed(P("8.8.8.8"),"192.168.1.9",null));
Chk("IPv4-leképzett IPv6 felismerve", HubAccess.AdminAllowed(P("::ffff:10.0.0.7"),null,null));
Chk("Tailscale/CGNAT (100.64/10) belsőnek számít", HubAccess.AdminAllowed(P("100.101.1.2"),null,null));
Chk("172.32.x NEM belső (csak 172.16–31)", !HubAccess.IsPrivate(P("172.32.0.1")) && HubAccess.IsPrivate(P("172.31.255.1")));
Chk("Loopback belső", HubAccess.IsPrivate(P("127.0.0.1")) && HubAccess.IsPrivate(IPAddress.IPv6Loopback));
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
