using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Agent;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
const string AG="http://localhost:19743", WEB="http://localhost:19380";
var jar=new CookieContainer(); var web=new HttpClient(new HttpClientHandler{CookieContainer=jar}){BaseAddress=new Uri(WEB)};
async Task<JsonElement> Web(string p,object b){var r=await web.PostAsync("/web/api/"+p,new StringContent(JsonSerializer.Serialize(b),Encoding.UTF8,"application/json"));return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;}
await web.PostAsync("/web/setup",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="SzuperEros2024xy",["password2"]="SzuperEros2024xy",["webPort"]="19380",["agentPort"]="19743"}));
var api=new HttpClient{BaseAddress=new Uri(AG)};
var tok=await Web("token",new{label="b5"}); var keys=Crypto.NewKeyPair();
var en=(await (await api.PostAsJsonAsync("/api/enroll",new EnrollRequest{Token=tok.GetProperty("token").GetString()!,PublicKeyPem=keys.PublicKeyPem,MachineName="UPD-PC"},ProtocolJson.Options)).Content.ReadFromJsonAsync<EnrollResponse>(ProtocolJson.Options))!;
var pkgSha=Environment.GetEnvironmentVariable("PKG_SHA")!;
async Task<CheckinReplyBody> Checkin(string ver,string sha){
  var req=new CheckinRequest{AgentId=en.AgentId,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Status=new AgentStatus{AgentVersion=ver,AgentSha256=sha}};
  req.Signature=Crypto.Sign(keys.PrivateKeyPem,req.CanonicalPayload());
  var r=(await (await api.PostAsJsonAsync("/api/checkin",req,ProtocolJson.Options)).Content.ReadFromJsonAsync<CheckinResponse>(ProtocolJson.Options))!;
  Chk("  (a válasz aláírása érvényes)", Crypto.Verify(en.ServerPublicKeyPem,r.SignedPayload!,r.Signature!));
  return ProtocolJson.Deserialize<CheckinReplyBody>(r.SignedPayload!)!; }

Console.WriteLine("--- Ajánlat ---");
var b1=await Checkin("1.1.0","AAAA"+new string('0',60));
Chk("Régi agent: frissítés felajánlva az aláírt részben", b1.AgentUpdate is not null && b1.AgentUpdate.Sha256==pkgSha, b1.AgentUpdate?.Sha256[..12]??"nincs");
var b2=await Checkin("1.1.1",pkgSha);
Chk("Már az új verzió fut: nincs ajánlat", b2.AgentUpdate is null);

Console.WriteLine("--- Letöltés és ellenőrzés ---");
var http=new HttpClient();
var (path,err)=await AgentUpdater.DownloadAndVerify(http,AG+"/api/agent-update",b1.AgentUpdate!,CancellationToken.None);
Chk("Letöltve, a lenyomat egyezik", path is not null && File.Exists(path), err);
Chk("A letöltött fájl bájtra egyezik a szerverével", path is not null && AgentUpdater.ComputeSha(path)==pkgSha);
var fake=new AgentUpdateOffer{Version="9.9.9",Sha256=new string('A',64),Size=b1.AgentUpdate!.Size};
var (p2,e2)=await AgentUpdater.DownloadAndVerify(http,AG+"/api/agent-update",fake,CancellationToken.None);
Chk("Hamis lenyomat: a fájl elvetve", p2 is null && e2.Contains("NEM egyezik"), e2);
var small=new AgentUpdateOffer{Version="1.1.1",Sha256=pkgSha,Size=1000};
var (p3,e3)=await AgentUpdater.DownloadAndVerify(http,AG+"/api/agent-update",small,CancellationToken.None);
Chk("Az ajánlottnál nagyobb letöltés elvetve", p3 is null && e3.Contains("nagyobb"), e3);
Chk("Érvénytelen ajánlat elutasítva", (await AgentUpdater.DownloadAndVerify(http,AG+"/api/agent-update",new AgentUpdateOffer{Sha256="x",Size=5},CancellationToken.None)).Path is null);

Console.WriteLine("--- Kikapcsolható ---");
await Web("settings",new{tokenLifetimeMinutes=60,tokenMaxUses=1,agentAutoUpdate=false});
Chk("Kikapcsolva: nincs ajánlat", (await Checkin("1.1.0","BBBB"+new string('0',60))).AgentUpdate is null);
await Web("settings",new{tokenLifetimeMinutes=60,tokenMaxUses=1});
Chk("Token-mentés nem kapcsolja vissza (változatlan marad)", (await Checkin("1.1.0","CCCC"+new string('0',60))).AgentUpdate is null);
var info=await Web("server-info",new{});
Chk("A beállítás a felületen is kikapcsoltként látszik", info.GetProperty("agentAutoUpdate").GetBoolean()==false);
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
