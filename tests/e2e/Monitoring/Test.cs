using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Agent;

int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
const string AG="http://localhost:19743", WEB="http://localhost:19380";
var jar=new CookieContainer();
var web=new HttpClient(new HttpClientHandler{CookieContainer=jar}){BaseAddress=new Uri(WEB)};
var api=new HttpClient{BaseAddress=new Uri(AG)};
async Task<JsonElement> Web(string path,object body){
  var r=await web.PostAsync("/web/api/"+path,new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json"));
  var t=await r.Content.ReadAsStringAsync(); return JsonDocument.Parse(t).RootElement; }

Console.WriteLine("--- 1. Webes setup + belépés ---");
var setup=await web.PostAsync("/web/setup",new FormUrlEncodedContent(new Dictionary<string,string>{
  ["user"]="admin",["password"]="SzuperEros2024xy",["password2"]="SzuperEros2024xy",["webPort"]="19380",["agentPort"]="19743"}));
Chk("Setup lefutott", setup.IsSuccessStatusCode, ((int)setup.StatusCode).ToString());
Chk("Munkamenet-süti megvan", jar.GetCookies(new Uri(WEB)).Count>0);

Console.WriteLine("--- 2. Token a webről, regisztráció aláírt kulccsal ---");
var tok=await Web("token",new{label="lanc"});
var token=tok.GetProperty("token").GetString()!; var srvFp=tok.GetProperty("fingerprint").GetString()!;
var keys=Crypto.NewKeyPair();
var en=await (await api.PostAsJsonAsync("/api/enroll",new EnrollRequest{Token=token,PublicKeyPem=keys.PublicKeyPem,MachineName="TESZT-PC"},ProtocolJson.Options))
  .Content.ReadFromJsonAsync<EnrollResponse>(ProtocolJson.Options);
Chk("Regisztráció sikeres", en!.Success, en.Message);
Chk("A szerver kulcsa egyezik a webes ujjlenyomattal", Crypto.FingerprintMatches(srvFp,en.ServerPublicKeyPem));

async Task<CheckinResponse> Checkin(double cpu,double disk){
  var st=new AgentStatus{MachineName="TESZT-PC",AgentVersion="1.1.0"};
  st.Metrics.Add(new Metric{Key="cpu.load",Label="CPU terhelés",Value=cpu,Unit="%",Category="CPU"});
  st.Metrics.Add(new Metric{Key="disk.C.usedpct",Label="Lemez C: kihasználtság",Value=disk,Unit="%",Category="Lemez"});
  st.Metrics.Add(new Metric{Key="svc.Spooler.running",Label="Szolgáltatás: Spooler",Value=1,Category="Szolgáltatás",Text="fut"});
  var req=new CheckinRequest{AgentId=en.AgentId,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Status=st};
  req.Signature=Crypto.Sign(keys.PrivateKeyPem,req.CanonicalPayload());
  var r=await api.PostAsJsonAsync("/api/checkin",req,ProtocolJson.Options);
  return (await r.Content.ReadFromJsonAsync<CheckinResponse>(ProtocolJson.Options))!; }

Console.WriteLine("--- 3. Aláírt jelentkezés metrikákkal ---");
var c1=await Checkin(35,72);
Chk("A jelentkezést elfogadta", c1.Ok, c1.Message);
Chk("A válaszban jön a mérési beállítás", c1.Metrics is not null);

Console.WriteLine("--- 4. A felület eléri a tárolt adatot ---");
await Checkin(48,73);
var mk=await Web("metric-keys",new{agentId=en.AgentId});
var mkeys=mk.GetProperty("keys").EnumerateArray().ToList();
Chk("3 metrika látszik a felületen", mkeys.Count==3, mkeys.Count.ToString());
var cpuLatest=mkeys.First(k=>k.GetProperty("key").GetString()=="cpu.load").GetProperty("latest").GetDouble();
Chk("A legfrissebb CPU érték 48", cpuLatest==48, cpuLatest.ToString());
var ser=(await Web("metric-series",new{agentId=en.AgentId,key="cpu.load",hours=24})).GetProperty("series").EnumerateArray().ToList();
Chk("A grafikon idősora 2 pont (35, 48)", ser.Count==2 && ser[0].GetProperty("v").GetDouble()==35, ser.Count.ToString());

Console.WriteLine("--- 5. Küszöb a felületről, riasztás kiold ---");
var sav=await Web("alert-save",new{name="CPU magas",metricKey="cpu.load",op=0,threshold=90,forSamples=2,enabled=true});
Chk("Küszöb mentve", sav.GetProperty("ok").GetBoolean());
await Checkin(95,74);
var act1=(await Web("alerts",new{})).GetProperty("active").GetArrayLength();
Chk("1 mérés 95%: még nem riaszt (2 kell)", act1==0, act1.ToString());
await Checkin(97,74);
var act2=(await Web("alerts",new{})).GetProperty("active").GetArrayLength();
Chk("2. mérés 97%: riaszt", act2==1, act2.ToString());
Chk("Riasztásnaplóban RIASZTÁS", (await Web("alert-log",new{})).GetProperty("log").GetString()!.Contains("RIASZTÁS"));
await Checkin(20,74);
Chk("Visszaesés után megszűnt", (await Web("alerts",new{})).GetProperty("active").GetArrayLength()==0
    && (await Web("alert-log",new{})).GetProperty("log").GetString()!.Contains("megszűnt"));

Console.WriteLine("--- 6. Mérési beállítás a felületről eljut az agenthez ---");
await Web("monitor-save",new{cpu=true,memory=true,disk=true,diskSmart=false,temperature=true,eventLog=true,
  services=new[]{"Spooler","W32Time"},processes=new[]{"explorer"},detailDays=7,keepDays=90});
var c2=await Checkin(30,74);
Chk("Az agent megkapja a figyelt szolgáltatásokat", c2.Metrics!.Services.Contains("W32Time"), string.Join(",",c2.Metrics.Services));
Chk("Az agent megkapja a kikapcsolt SMART-ot", c2.Metrics.DiskSmart==false);

Console.WriteLine("--- 6b. Kiesés-riasztás beállítása, CSV-jelentések ---");
var al=await Web("alerts",new{});
Chk("Kiesés-riasztás alapból bekapcsolva, 15 perc", al.GetProperty("offlineMinutes").GetInt32()==15, al.GetProperty("offlineMinutes").ToString());
Chk("A frissen jelentkezett gép nem esett ki", !al.GetProperty("active").EnumerateArray().Any(a=>a.GetProperty("offline").GetBoolean()));
var os=await Web("offline-save",new{minutes=3,groups=new[]{"Szerverek"}});
Chk("Túl rövid küszöb 10 percre emelve", os.GetProperty("ok").GetBoolean() && os.GetProperty("minutes").GetInt32()==10, os.ToString());
al=await Web("alerts",new{});
Chk("A csoportszűrés elmentve", al.GetProperty("offlineGroups").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"Szerverek"}));
await Web("offline-save",new{minutes=15,groups=Array.Empty<string>()});
async Task<(int Code,string Type,byte[] Body)> Get(string url){var r=await web.GetAsync(url);return ((int)r.StatusCode,r.Content.Headers.ContentType?.MediaType??"",await r.Content.ReadAsByteArrayAsync());}
string Txt(byte[] b)=>Encoding.UTF8.GetString(b);
var rep=await Get("/web/report?r=gepek");
Chk("Géplista CSV: letölthető, BOM, a gép benne", rep.Code==200 && rep.Type=="text/csv" && rep.Body[0]==0xEF && Txt(rep.Body).Contains("TESZT-PC"), $"{rep.Code} {rep.Type}");
rep=await Get("/web/report?r=metrikak&napok=7");
Chk("Metrika-összesítő CSV: a CPU napi értékei", rep.Code==200 && Txt(rep.Body).Contains("cpu.load"), Txt(rep.Body).Split('\n').Skip(1).FirstOrDefault()??"");
rep=await Get("/web/report?r=riasztasok");
Chk("Riasztásnapló CSV: a küszöb-riasztás és a megszűnése", rep.Code==200 && Txt(rep.Body).Contains("CPU magas;") && Txt(rep.Body).Contains("megszűnt"));
foreach(var k in new[]{"programok","frissitesek"}) { rep=await Get("/web/report?r="+k); Chk($"{k} CSV letölthető", rep.Code==200 && rep.Type=="text/csv"); }
Chk("Ismeretlen jelentés: 404", (await Get("/web/report?r=nincs")).Code==404);
var anonRep=await new HttpClient{BaseAddress=new Uri(WEB)}.GetAsync("/web/report?r=gepek");
Chk("Bejelentkezés nélkül nincs jelentés", (int)anonRep.StatusCode==401);

Console.WriteLine("--- 7. A valódi agentkód is végigfut ---");
try{Directory.Delete(AgentConfig.Dir,true);}catch{}
var t2=(await Web("token",new{label="valodi"})).GetProperty("token").GetString()!;
new AgentConfig{ServerUrl=AG,EnrollmentToken=t2,ServerFingerprint=srvFp}.Save();
var core=new AgentCore(AgentConfig.Load()); var log=new List<string>(); core.Log+=log.Add;
await core.CheckinAsync(CancellationToken.None);
Chk("A valódi agent regisztrált", log.Any(l=>l.Contains("Sikeres regisztráció")), string.Join(" | ",log));
Chk("A valódi agent lefuttatta a metrikagyűjtést", log.Any(l=>l.Contains("Metrikagyűjtés")));
Chk("A valódi agent jelentkezett", log.Any(l=>l.Contains("Jelentkezés kész")));
Chk("A valódi agent erősebb (P-384) kulcsra cserélt (a szerver aláírta a visszaigazolást)", log.Any(l=>l.Contains("Kulcscsere kész: ECDSA P-384")), string.Join(" | ",log.Where(l=>l.Contains("ulcs"))));
var cfgAfter=AgentConfig.Load();
Chk("Az agent az új kulcsot tárolja", Crypto.KeyBits(cfgAfter.PublicKeyPem)==384 && cfgAfter.KeyCreatedUtc is not null);
var core2=new AgentCore(AgentConfig.Load()); var log2=new List<string>(); core2.Log+=log2.Add; await core2.CheckinAsync(CancellationToken.None);
Chk("Az új kulccsal is jelentkezik, és nem cserél újra", log2.Any(l=>l.Contains("Jelentkezés kész")) && !log2.Any(l=>l.Contains("Kulcscsere")), string.Join(" | ",log2));
var keysApi=await Web("keys",new{});
var me=keysApi.GetProperty("peers").EnumerateArray().First(p=>p.GetProperty("fingerprint").GetString()==cfgAfter.Fingerprint);
Chk("A kulcslistában: P-384, megerősítve, a régi kulcs az előzményben", me.GetProperty("algorithm").GetString()!.Contains("P-384") && !me.GetProperty("pending").GetBoolean() && me.GetProperty("history").GetArrayLength()==2, me.ToString());
Chk("A kulcslistában a kiadott tokenek is", keysApi.GetProperty("tokens").GetArrayLength()>=3 && keysApi.GetProperty("owner").GetProperty("rotateDays").GetInt32()==7);
Console.WriteLine("  (Linuxon nincs WMI, ezért itt 0 érték – a kategóriák eredménye:)");
Console.Write(AgentMetrics.LastDiagnostics);
try{Directory.Delete(AgentConfig.Dir,true);}catch{}

Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
