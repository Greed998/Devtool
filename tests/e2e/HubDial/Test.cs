using System.Net;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet.Server;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
const string HUB="http://localhost:18090";
var jar=new CookieContainer(); var web=new HttpClient(new HttpClientHandler{CookieContainer=jar}){BaseAddress=new Uri(HUB)};
async Task<JsonElement> Web(string p,string b="{}"){var r=await web.PostAsync("/web/api/"+p,new StringContent(b,Encoding.UTF8,"application/json"));return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;}
await web.PostAsync("/web/setup",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="SzuperEros2024xy",["password2"]="SzuperEros2024xy",["domain"]="fleet.poltoli.hu",["webPort"]="18090"}));
var tok=await Web("token","{\"label\":\"Ugyfel\"}");
var dir=Path.Combine(Path.GetTempPath(),"site-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
var store=new FleetStore(dir);
var cfg=new ServerConfig{HubUrl=HUB,HubToken=tok.GetProperty("token").GetString()!,HubFingerprint=tok.GetProperty("fingerprint").GetString()!};
var fp1=cfg.HubFingerprint;
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("Telephely betárcsázott", cfg.HubSiteId.Length==32 && HubClient.Instance.LastError=="", HubClient.Instance.LastError);
var rot=await Web("rotate-keys"); var fp2=rot.GetProperty("fingerprint").GetString()!;
Chk("A Központ kulcsa cserélődött", rot.GetProperty("ok").GetBoolean() && fp2!=fp1);
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("A telephely ellenőrizte az átadást, hiba nélkül jelentkezett", HubClient.Instance.LastError=="", HubClient.Instance.LastError);
Chk("A telephely az új Központ-ujjlenyomatot tárolja", cfg.HubFingerprint==fp2);
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("Utána is rendben", HubClient.Instance.LastError=="");

Console.WriteLine("--- Metrikák a Központba (5 perces átlagok) ---");
var kp=DevToolbox.Fleet.Crypto.NewKeyPair();
var en=store.Enroll(new DevToolbox.Fleet.EnrollRequest{Token=store.CreateEnrollmentToken("teszt",60,1),PublicKeyPem=kp.PublicKeyPem,MachineName="TESZT-PC",Fingerprint=kp.Fingerprint});
var aid=en.AgentId;
var mdb=new MetricStore(dir); HubClient.Metrics=mdb; cfg.HubMetricsFromTs=0;
var t0=DateTime.UtcNow.AddMinutes(-40);
mdb.InsertMany(aid, Enumerable.Range(0,40).Select(i=>(t0.AddMinutes(i),(IReadOnlyList<DevToolbox.Fleet.Metric>)new List<DevToolbox.Fleet.Metric>{new(){Key="cpu.load",Label="CPU",Value=i,Unit="%",Category="CPU"}})));
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);   // a gép bekerül az összesítőbe, a Központ jelzi: fogad metrikát
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);   // most mennek fel az átlagok
Chk("A felküldés kezdőpontja előrelépett", cfg.HubMetricsFromTs>new DateTimeOffset(t0).ToUnixTimeSeconds(), cfg.HubMetricsFromTs.ToString());
var keys=await Web("metric-keys",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"agentId\":\"{aid}\"}}");
Chk("A Központban látszik a gép metrikája", keys.GetProperty("keys").GetArrayLength()==1, keys.ToString());
var ser=await Web("metric-series",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"agentId\":\"{aid}\",\"key\":\"cpu.load\",\"hours\":6}}");
Chk("5 perces átlagok érkeztek (kb. 7-8 sáv)", ser.GetProperty("series").GetArrayLength() is >= 6 and <= 9, ser.GetProperty("series").GetArrayLength().ToString());
var bad=await Web("metric-keys",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"agentId\":\"idegen-gep\"}}");
Chk("Idegen gép metrikája nem kérhető le", !bad.TryGetProperty("keys",out _));

Console.WriteLine("--- Központi szabály a telephelyre ---");
var ps=await Web("policy-save","{\"name\":\"Éjszaka\",\"apps\":true,\"days\":[1,2,3],\"startHour\":22,\"endHour\":4,\"enabled\":true}");
Chk("Központi szabály mentve", ps.GetProperty("ok").GetBoolean());
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("A telephely átvette a Központ szabályát", cfg.HubPolicies.Count==1 && cfg.HubPolicies[0].Name=="Éjszaka");

Console.WriteLine("--- 1.5.0: programtár (a Központ a mester), küszöb-szabály, mérés-kérés, csoportok ---");
await Web("catalog-save","{\"items\":[{\"id\":\"7zip.7zip\",\"name\":\"7-Zip\"}],\"bundles\":[{\"id\":\"\",\"name\":\"Központi alap\",\"apps\":[\"7zip.7zip\"]}]}");
var ar=await Web("alert-save","{\"name\":\"Lemez tele\",\"metricKey\":\"disk.*.usedpct\",\"op\":0,\"threshold\":90,\"forSamples\":2,\"enabled\":true,\"pushDown\":true}");
var ar2=await Web("alert-save","{\"name\":\"Csak a Központban\",\"metricKey\":\"cpu.load\",\"op\":0,\"threshold\":95,\"forSamples\":1,\"enabled\":true,\"pushDown\":false}");
Chk("Központi küszöb-szabályok mentve", ar.GetProperty("ok").GetBoolean() && ar2.GetProperty("ok").GetBoolean(), ar.ToString());
Chk("Érvénytelen metrika-kulcs elutasítva", !(await Web("alert-save","{\"name\":\"x\",\"metricKey\":\"a b\",\"threshold\":1}")).GetProperty("ok").GetBoolean());
var mon=await Web("monitor-save","{\"enabled\":true,\"sites\":[],\"cpu\":true,\"hardware\":true,\"sensorDriver\":false,\"services\":[\"W3SVC\"],\"sampleSeconds\":60}");
Chk("A Központ mérés-kérése mentve", mon.GetProperty("ok").GetBoolean(), mon.ToString());
HubClient.Alerts=new AlertStore(dir);
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("A telephely átvette a Központ programtárát (csak olvasható)", cfg.HubCatalogItems.Any(i=>i.Id=="7zip.7zip") && cfg.HubBundles.Count==1 && cfg.CatalogState("7zip.7zip")=="hubrepo", HubClient.Instance.LastError);
Chk("Csak a leküldésre jelölt szabály jött le, a riasztási motorba is", cfg.HubAlertRules.Count==1 && cfg.HubAlertRules[0].MetricKey=="disk.*.usedpct" && HubClient.Alerts.HubRules.Count==1);
Chk("A mérés-kérés összefésülve megy az agentekhez", cfg.HubMonitor?.Services.Contains("W3SVC")==true && cfg.EffectiveMetricConfig().Services.Contains("W3SVC"));
var hash=cfg.HubCatalogHash;
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("Változatlan programtár: ugyanaz a lenyomat", cfg.HubCatalogHash==hash && HubClient.Instance.LastError=="");
Chk("A telephely jelzi: ismeri a csoport-műveleteket", (await Site0()).GetProperty("supports").GetBoolean());
var gop=await Web("group-op",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"op\":\"members\",\"group\":\"Könyvelés\",\"agentIds\":[\"{aid}\"]}}");
Chk("Csoport-művelet a Központból elfogadva", gop.GetProperty("ok").GetBoolean(), gop.ToString());
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("A telephely végrehajtotta (a gép a csoportban)", store.GetAgent(aid)!.Groups.Contains("Könyvelés"));
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("A Központ csoport-oldala a telephely állapotát mutatja", (await Site0()).GetProperty("groups").EnumerateArray().Any(g=>g.GetProperty("name").GetString()=="Könyvelés" && g.GetProperty("members").GetArrayLength()==1));
async Task<JsonElement> Site0()=>(await Web("groups")).GetProperty("sites").EnumerateArray().First(x=>x.GetProperty("siteId").GetString()==cfg.HubSiteId);

Console.WriteLine("--- Új agent a Központból ---");
var rq=await Web("agent-token-request",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"target\":\"Könyvelés-PC\"}}");
Chk("Kérés elfogadva", rq.GetProperty("ok").GetBoolean());
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);   // a kérés lemegy, a telephely elkészíti
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);   // a beléptető feljön
var jobs=(await Web("agent-tokens",$"{{\"siteId\":\"{cfg.HubSiteId}\"}}")).GetProperty("jobs");
Chk("A beléptető megérkezett a Központba", jobs.GetArrayLength()==1 && jobs[0].GetProperty("ready").GetBoolean(), jobs.ToString());

Console.WriteLine("--- Frissítés a Központból ---");
async Task<JsonElement> Site()=>(await Web("sites")).GetProperty("sites").EnumerateArray().First(x=>x.GetProperty("siteId").GetString()==cfg.HubSiteId);
Chk("A telephely jelzi, hogy ismeri a Központból indított frissítést", (await Site()).GetProperty("supportsHubUpdates").GetBoolean());
await Web("site-update",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"target\":\"on\"}}");
Chk("Szerverfrissítés kérve", (await Site()).GetProperty("updateRequested").GetBoolean());
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("Azonos verziónál nincs csere, a kérés törlődik", !(await Site()).GetProperty("updateRequested").GetBoolean() && HubClient.Instance.LastError=="", HubClient.Instance.LastError);
cfg.AgentAutoUpdate=true;
await Web("site-agent-autoupdate",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"target\":\"off\"}}");
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("Az automatikus agent-frissítés a Központból kikapcsolva", !cfg.AgentAutoUpdate);
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("…és a Központ már a telephely tényleges állapotát mutatja", !(await Site()).GetProperty("agentAutoUpdate").GetBoolean() && !(await Site()).GetProperty("agentAutoUpdatePending").GetBoolean());
var ua=await Web("task",$"{{\"siteId\":\"{cfg.HubSiteId}\",\"agentIds\":[\"{aid}\"],\"kind\":\"updateagent\"}}");
await HubClient.Instance.CycleAsync(store,cfg,dir,CancellationToken.None);
Chk("Gépenkénti agent-frissítés: a telephely megjelöli (nem az agentnek adja)", ua.GetProperty("ok").GetBoolean() && store.GetAgent(aid)!.ForceUpdate);
Console.WriteLine("--- Kiesés-riasztás, e-mail, jelentések a Központban ---");
var al=await Web("alerts");
Chk("Kiesés-riasztás alapból be (15 perc, gépekre is)", al.GetProperty("offlineMinutes").GetInt32()==15 && al.GetProperty("offlineMachines").GetBoolean());
Chk("Az élő telephely nem esett ki", !al.GetProperty("active").EnumerateArray().Any(a=>a.GetProperty("offline").GetBoolean()), al.GetProperty("active").ToString());
Chk("Kiesés beállítása (admin)", (await Web("offline-save","{\"minutes\":20,\"machines\":false}")).GetProperty("minutes").GetInt32()==20);
await Web("offline-save","{\"minutes\":15,\"machines\":true}");
var ms0=await Web("mail-settings");
Chk("E-mail alapból kikapcsolva, nincs jelszó", !ms0.GetProperty("enabled").GetBoolean() && !ms0.GetProperty("hasPassword").GetBoolean());
var msv=await Web("mail-save","{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":1,\"security\":\"starttls\",\"user\":\"fleet@x.hu\",\"smtpPassword\":\"NagyonTitkos-77\",\"from\":\"fleet@x.hu\",\"to\":\"a@b.hu, c@d.hu\"}");
Chk("E-mail beállítás mentve", msv.GetProperty("ok").GetBoolean(), msv.ToString());
var ms1=await Web("mail-settings");
Chk("A jelszó nem jön vissza a felületre, csak hogy van", ms1.GetProperty("hasPassword").GetBoolean() && !ms1.ToString().Contains("NagyonTitkos"), ms1.ToString());
Chk("Címzettek elmentve", ms1.GetProperty("to").GetString()=="a@b.hu, c@d.hu");
var mt=await Web("mail-test","{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":1,\"security\":\"starttls\",\"user\":\"fleet@x.hu\",\"smtpPassword\":\"\",\"from\":\"fleet@x.hu\",\"to\":\"a@b.hu, c@d.hu\"}");
Chk("Teszt e-mail elérhetetlen SMTP-re: érthető hiba", !mt.GetProperty("ok").GetBoolean() && mt.GetProperty("error").GetString()!.Contains("nem érhető el"), mt.ToString());
Chk("Hibás cím elutasítva", !(await Web("mail-save","{\"enabled\":true,\"host\":\"x\",\"port\":587,\"security\":\"starttls\",\"from\":\"rossz\",\"to\":\"a@b.hu\"}")).GetProperty("ok").GetBoolean());
if(Environment.GetEnvironmentVariable("FLEET_HUB_TEST_DIR") is {Length:>0} hubDir)   // a run-tests.ps1 adja meg
{
    var hubCfg=File.ReadAllText(Path.Combine(hubDir,"server-config.json"));
    Chk("A konfigurációban a jelszó titkosítva", !hubCfg.Contains("NagyonTitkos") && hubCfg.Contains("SmtpPasswordProtected"));
    var hubAudit=File.ReadAllText(Path.Combine(hubDir,"logs","audit.log"));   // 1.5.4-tól a naplómappában
    Chk("A műveleti naplóban az e-mail beállítás ott van, a jelszó nincs", hubAudit.Contains("e-mail értesítés beállítása") && !hubAudit.Contains("NagyonTitkos"));
}
await Web("mail-save","{\"enabled\":false,\"host\":\"\",\"port\":587,\"security\":\"starttls\",\"from\":\"\",\"to\":\"\",\"clearPassword\":true}");
var web2=web; async Task<(int Code,string Body)> Rep(string q){var r=await web2.GetAsync("/web/report?"+q);return ((int)r.StatusCode,await r.Content.ReadAsStringAsync());}
var rg=await Rep("r=gepek");
Chk("Központi géplista CSV: telephely + gép", rg.Code==200 && rg.Body.Contains("Telephely;Gép;") && rg.Body.Contains("TESZT-PC"), rg.Body.Split('\n').Skip(1).FirstOrDefault()??"");
var rm=await Rep("r=metrikak&napok=1");
Chk("Központi metrika-összesítő CSV", rm.Code==200 && rm.Body.Contains("cpu.load"));
var anon=new HttpClient{BaseAddress=new Uri(HUB)}; string last="";
for(int i=0;i<6;i++){var m=new HttpRequestMessage(HttpMethod.Post,"/web/login"){Content=new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="x"+i})};last=await (await anon.SendAsync(m)).Content.ReadAsStringAsync();}
Chk("Központ: 6 hibás belépés után zárolva", last.Contains("Túl sok sikertelen"));
var audit=(await Web("audit")).GetProperty("log").GetString()!;
Chk("Központ napló: kulcscsere + sikertelen belépés", audit.Contains("KULCSCSERE") && audit.Contains("sikertelen belépés"));
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
