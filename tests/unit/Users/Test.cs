using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"ut-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
var us=new UserStore(dir);

Console.WriteLine("--- Szuperadmin ---");
us.EnsureFirstAdmin("admin","ErosJelszo2024!");
Chk("A szuperadmin létrejött adminként", us.Find("admin")?.Role==Role.Admin);
Chk("Helyes jelszóval bejelentkezik", us.Authenticate("admin","ErosJelszo2024!")!=null);
Chk("Rossz jelszó nem", us.Authenticate("admin","x")==null);

Console.WriteLine("--- Szerepkörök ---");
var cn=us.Create("nezo","NezoJelszo2024!",Role.Megfigyelo);Console.WriteLine("  create nezo: "+cn.Ok+" "+cn.Error);
var co=us.Create("oper","OpJelszo2024!!",Role.Kezelo);Console.WriteLine("  create op: "+co.Ok+" "+co.Error);
var nezo=us.Find("nezo")!; var op=us.Find("oper")!; var admin=us.Find("admin")!;
Chk("Néző csak néz", nezo.Role==Role.Megfigyelo && nezo.Role<Role.Kezelo);
Chk("Operátor kezelő szint", op.Role==Role.Kezelo && op.Role<Role.Admin);
Chk("Admin a legmagasabb", admin.Role==Role.Admin);

Console.WriteLine("--- Telephely-hatókör ---");
us.Create("korlatos","KorlatJelszo24!",Role.Kezelo);
us.SetScope("korlatos", new(){"site-A","site-B"});
var k=us.Find("korlatos")!;
Chk("Korlátozott: nem lát mindent", !k.AllSites);
Chk("Látja a hatókörén belülit (A)", k.CanSeeSite("site-A"));
Chk("Nem látja a hatókörön kívülit (C)", !k.CanSeeSite("site-C"));
Chk("Üres hatókör = minden telephely", nezo.AllSites && nezo.CanSeeSite("barmi"));

Console.WriteLine("--- Védelmek ---");
Chk("Szuperadmint nem lehet lefokozni", !us.SetRole("admin",Role.Megfigyelo).Ok);
Chk("Szuperadmint nem lehet törölni", !us.Delete("admin").Ok);
Chk("Szuperadmint nem lehet letiltani", !us.SetDisabled("admin",true).Ok);
Chk("Sima felhasználó törölhető", us.Delete("nezo").Ok);
Chk("Duplikált név nem hozható létre", !us.Create("oper","x",Role.Kezelo).Ok);

Console.WriteLine("--- Letiltás ---");
us.SetDisabled("oper",true);
Chk("Letiltott nem tud belépni", us.Authenticate("oper","OpJelszo2024!!")==null);
us.SetDisabled("oper",false);
Chk("Visszaengedélyezés után igen", us.Authenticate("oper","OpJelszo2024!!")!=null);

Console.WriteLine("--- Perzisztencia ---");
var us2=new UserStore(dir);
Chk("A felhasználók túlélik újraindítást", us2.Find("korlatos")!=null);
Chk("A hatókör is megmarad", us2.Find("korlatos")!.CanSeeSite("site-A") && !us2.Find("korlatos")!.CanSeeSite("site-C"));

try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
