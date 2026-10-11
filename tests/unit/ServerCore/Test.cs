using DevToolbox.Fleet;
using DevToolbox.Fleet.Server;
int fail=0;
void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  ->  "+i:""));if(!ok)fail++;}

// A netstat -ano kimenet parse-olasa (angol es nemet allapotszavakkal is mukodjon)
var sample = @"
Aktiv kapcsolatok

  Prot  Helyi cim              Tavoli cim             Allapot           PID
  TCP    0.0.0.0:8080           0.0.0.0:0              LISTENING       4321
  TCP    0.0.0.0:8443           0.0.0.0:0              LISTENING       4321
  TCP    192.168.1.5:52000      93.184.216.34:443     ESTABLISHED     9999
  TCP    0.0.0.0:445            0.0.0.0:0              ABHÖREN          4
";
Chk("8080 figyelo PID megtalalva", PortTools.ParseListeningPid(sample,8080)==4321,
    ""+PortTools.ParseListeningPid(sample,8080));
Chk("8443 figyelo PID megtalalva", PortTools.ParseListeningPid(sample,8443)==4321);
Chk("Nemet 'ABHÖREN' allapot is jo (nyelvfuggetlen a 0.0.0.0:0 alapjan)",
    PortTools.ParseListeningPid(sample,445)==4);
Chk("ESTABLISHED sor NEM szamit figyelonek", PortTools.ParseListeningPid(sample,52000) is null);
Chk("Nem letezo port null", PortTools.ParseListeningPid(sample,9000) is null);

// FindFree: a foglaltakat kihagyja
var used=new HashSet<int>{8080,8081};
var free=PortTools.FindFree(8080, used, p=>false);
Chk("FindFree kihagyja a foglaltakat", free==8082, ""+free);

// ServerPaths konzisztens
Chk("DataDir tartalmazza a DevToolbox-Fleet-et", ServerPaths.DataDir.Contains("DevToolbox-Fleet"));
Chk("InstallDir Program Files alatt", ServerPaths.InstallDir.Contains("DevToolbox Fleet"));


Console.WriteLine("--- Token lejárat és gépkorlát ---");
var st=new FleetStore(Path.Combine(Path.GetTempPath(),"tl-"+Guid.NewGuid().ToString("N")));
EnrollResponse En(string t)=>st.Enroll(new EnrollRequest{Token=t,PublicKeyPem=Crypto.NewKeyPair().PublicKeyPem,MachineName="M"});
var one=st.CreateEnrollmentToken("egy",60,1);
Chk("Egygépes token: első gép regisztrál", En(one).Success);
Chk("Egygépes token: második gép ELUTASÍTVA", !En(one).Success);
var two=st.CreateEnrollmentToken("ketto",60,2);
Chk("Kétgépes token: 2 gép regisztrál", En(two).Success && En(two).Success);
Chk("Kétgépes token: harmadik elutasítva", !En(two).Success);
var unl=st.CreateEnrollmentToken("korlatlan",0,0);
Chk("Korlátlan token: 3 gép is regisztrál", En(unl).Success && En(unl).Success && En(unl).Success);
var old=st.CreateEnrollmentToken("regi");
Chk("Régi (paraméter nélküli) hívás korlátlan marad", En(old).Success && En(old).Success);
var ti=new FleetStore.TokenInfo("x",DateTime.UtcNow,"l",true){ExpiresUtc=DateTime.UtcNow.AddMinutes(-1)};
Chk("Lejárt token nem használható", !ti.IsUsable && ti.IsExpired);
Chk("Élő token használható", new FleetStore.TokenInfo("y",DateTime.UtcNow,"l",true){ExpiresUtc=DateTime.UtcNow.AddMinutes(5)}.IsUsable);

Console.WriteLine("--- Programtár: winget-keresés kimenete, alkalmazáscsomagok ---");
string Row(string n,string i,string v,string m,string s)=>$"{n,-26}{i,-28}{v,-12}{m,-15}{s}\n";
var en = "   - \b \\ \b\n" + Row("Name","Id","Version","Match","Source") + new string('-',92) + "\n" +
         Row("Mozilla Firefox (hu)","Mozilla.Firefox.hu","131.0","Tag: firefox","winget") +
         Row("Mozilla Firefox ESR","Mozilla.Firefox.ESR","128.3.0","","winget") +
         Row("Hibás \"sor\"","-rossz","1.0","","winget");
var hits = WingetSearch.Parse(en);
Chk("Angol kimenet: 2 érvényes találat (a hibás azonosító kimarad)", hits.Count==2, hits.Count.ToString());
Chk("Név, azonosító, verzió oszloppozíció szerint", hits[0].Name=="Mozilla Firefox (hu)" && hits[0].Id=="Mozilla.Firefox.hu" && hits[0].Version=="131.0");
var hu = "Név             Azonosító      Verzió  Forrás\n------------------------------------------------\n7-Zip           7zip.7zip      24.08   winget\n";
Chk("Magyar fejléc is jó", WingetSearch.Parse(hu) is [{ Id: "7zip.7zip", Version: "24.08" }]);
Chk("Fejléc nélküli kimenet: üres", WingetSearch.Parse("Nem található a feltételeknek megfelelő csomag.").Count==0);
var sc=new ServerConfig{SoftwareCatalog={new(){Id="7zip.7zip",Name="7-Zip",Base=true},new(){Id="Mozilla.Firefox",Name="Firefox"}}};
Chk("Régi alapcsomag-jelölőből Alapcsomag nevű csomag lesz", sc.MigrateBaseToBundle() && sc.AppBundles.Single().Name=="Alapcsomag" && sc.AppBundles[0].Apps.SequenceEqual(new[]{"7zip.7zip"}));
Chk("A migráció csak egyszer fut", !sc.MigrateBaseToBundle());
sc.SetCatalog(new(){new(){Id="7zip.7zip",Name="7-Zip"},new(){Id="-rossz",Name="x"}}, new(){new(){Name="Iroda",Apps={"7zip.7zip","Nincs.Tarban"}}});
Chk("Mentéskor: érvénytelen azonosító kimarad, csomagban csak tárbeli program", sc.SoftwareCatalog.Count==1 && sc.AppBundles.Single().Apps.SequenceEqual(new[]{"7zip.7zip"}) && sc.AppBundles[0].Id.Length==32);
Chk("Csomag programjai id alapján", sc.BundleApps(sc.AppBundles[0].Id).Count==1 && sc.BundleApps("nincs").Count==0);

Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));
return fail;
