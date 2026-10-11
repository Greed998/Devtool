using DevToolbox.Fleet;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
string D(){var d=Path.Combine(Path.GetTempPath(),"s-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(d);return d;}

Console.WriteLine("--- Kulcstár: átalakítás a meglévő kulcs megtartásával ---");
var d1=D(); var old=Crypto.NewKeyPair();
File.WriteAllText(Path.Combine(d1,"server-public.pem"),old.PublicKeyPem); File.WriteAllText(Path.Combine(d1,"server-private.pem"),old.PrivateKeyPem);
var ks=KeyStore.LoadOrCreate(d1,"server");
Chk("A régi .pem kulcs átkerült, NEM változott", ks.Pair.Fingerprint==old.Fingerprint);
Chk("Verziózott kulcstár-fájl létrejött", File.Exists(KeyStore.PathFor(d1,"server")) && ks.Version==KeyStore.CurrentVersion);
Chk("Újratöltés után ugyanaz a kulcs", KeyStore.LoadOrCreate(d1,"server").Pair.Fingerprint==old.Fingerprint);

Console.WriteLine("--- Kulcscsere-lánc ---");
var k1=ks.Current.PublicKeyPem; ks.Rotate(d1,"server"); var k2=ks.Current.PublicKeyPem; ks.Rotate(d1,"server"); var k3=ks.Current.PublicKeyPem;
Chk("A kulcs valóban cserélődött", k1!=k2 && k2!=k3);
Chk("Egy lépés: K1 -> K2 ellenőrizhető", KeyStore.ChainReaches(k1,ks.Links.Take(2).ToList(),k2));
Chk("Több lépés: K1 -> K3 (offline agent is átáll)", KeyStore.ChainReaches(k1,ks.Links,k3));
Chk("A régi privát kulcs nem maradt meg", !File.ReadAllText(KeyStore.PathFor(d1,"server")).Contains(old.PrivateKeyPem.Split('\n')[1]));
var evil=Crypto.NewKeyPair();
var forged=ks.Links.ToList(); forged.Add(new KeyLink{PublicKeyPem=evil.PublicKeyPem,SignedByPrevious=Crypto.Sign(evil.PrivateKeyPem,evil.PublicKeyPem)});
Chk("Hamis lánc (támadó saját maga írta alá) elutasítva", !KeyStore.ChainReaches(k1,forged,evil.PublicKeyPem));
Chk("Idegen kiinduló kulcs elutasítva", !KeyStore.ChainReaches(Crypto.NewKeyPair().PublicKeyPem,ks.Links,k3));
Chk("Régi telepítőcsomag ujjlenyomata a láncon át elfogadva", KeyStore.FingerprintReaches(old.Fingerprint,ks.Links,k3));
Chk("Ismeretlen ujjlenyomat elutasítva", !KeyStore.FingerprintReaches(evil.Fingerprint,ks.Links,k3));
Chk("A .pem tükör a jelenlegi kulcsot mutatja", File.ReadAllText(Path.Combine(d1,"server-public.pem"))==k3);

Console.WriteLine("--- Mentés verzióváltáskor ---");
var d2=D(); File.WriteAllText(Path.Combine(d2,"fleet.json"),"{}"); KeyStore.LoadOrCreate(d2,"server");
DataBackup.OnStartup(d2,"1.2.0"); Chk("Első indulás: nincs mentés", !Directory.Exists(Path.Combine(d2,"backup")));
DataBackup.OnStartup(d2,"1.3.0");
var bk=Directory.GetDirectories(Path.Combine(d2,"backup"));
Chk("Verzióváltáskor mentés készült", bk.Length==1 && bk[0].Contains("1.2.0-1.3.0"), bk.FirstOrDefault()??"");
Chk("A mentésben ott a kulcstár és az adatok", File.Exists(Path.Combine(bk[0],"server-keystore.json")) && File.Exists(Path.Combine(bk[0],"fleet.json")));
DataBackup.OnStartup(d2,"1.3.0"); Chk("Azonos verziónál nincs újabb mentés", Directory.GetDirectories(Path.Combine(d2,"backup")).Length==1);
for(int i=0;i<13;i++){DataBackup.Create(d2,"x"+i);Thread.Sleep(1100/1000);} 
Chk("Legfeljebb 10 mentés marad", Directory.GetDirectories(Path.Combine(d2,"backup")).Length<=10);

Console.WriteLine("--- Belépés-korlátozás ---");
var ip="10.1.1."+Random.Shared.Next(2,250);
for(int i=0;i<4;i++) LoginThrottle.Fail(ip);
Chk("4 hibás próbálkozás után még enged", LoginThrottle.Blocked(ip)==null);
LoginThrottle.Fail(ip);
Chk("5. után zárolva (~15 perc)", LoginThrottle.Blocked(ip) is {} t && t.TotalMinutes>14);
Chk("Másik IP-t nem érinti", LoginThrottle.Blocked("10.9.9.9")==null);
var ip2="10.2.2.2"; LoginThrottle.Fail(ip2); LoginThrottle.Success(ip2);
Chk("Sikeres belépés nullázza a számlálót", LoginThrottle.Blocked(ip2)==null);

Console.WriteLine("--- Műveleti napló ---");
var d3=D();
AuditLog.Api(d3,"admin","/web/api/user-create","{\"username\":\"bela\",\"password\":\"TitkosJelszo123!\",\"role\":1}");
AuditLog.Api(d3,"admin","/web/api/metric-keys","{\"agentId\":\"x\"}");
var log=AuditLog.Tail(d3,50);
Chk("A felhasználó-létrehozás naplózva", log.Contains("felhasználó létrehozása") && log.Contains("bela"));
Chk("A jelszó NEM került a naplóba", !log.Contains("TitkosJelszo"));
Chk("Csak-olvasó lekérdezés nincs naplózva", !log.Contains("metric-keys") && log.Split('\n').Length==1);

Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
