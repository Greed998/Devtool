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

try{Directory.Delete(AgentConfig.Dir,true);}catch{}
var tok=await Web("token",new{label="A"}); var fp1=tok.GetProperty("fingerprint").GetString()!;
new AgentConfig{ServerUrl=AG,EnrollmentToken=tok.GetProperty("token").GetString()!,ServerFingerprint=fp1}.Save();
var log=new List<string>(); AgentCore NewCore(){var c=new AgentCore(AgentConfig.Load());c.Log+=log.Add;return c;}

Console.WriteLine("--- A1: aláírt válasz ---");
await NewCore().CheckinAsync(CancellationToken.None);
Chk("Regisztráció + aláírt jelentkezés elfogadva", log.Any(l=>l.Contains("Jelentkezés kész")), string.Join(" | ",log.TakeLast(3)));
var aid=AgentConfig.Load().AgentId;
await Web("queue",new{agentId=aid,ids=new[]{"DevToolbox.Teszt.NemLetezo"}});
log.Clear(); await NewCore().CheckinAsync(CancellationToken.None);
Chk("Valódi szerver aláírt feladata végrehajtva", log.Any(l=>l.Contains("Feladat: UpgradeApp DevToolbox.Teszt.NemLetezo")));

Console.WriteLine("--- A1: támadó szerver ---");
var evil=new HttpListener(); evil.Prefixes.Add("http://localhost:19999/"); evil.Start();
var evilKey=Crypto.NewKeyPair(); int mode=0;
_=Task.Run(async()=>{while(true){var c=await evil.GetContextAsync();
  var req=await JsonSerializer.DeserializeAsync<CheckinRequest>(c.Request.InputStream,ProtocolJson.Options);
  var resp=new CheckinResponse{Ok=true,Tasks=new(){new AgentTask{Kind=AgentTaskKind.UpgradeAll}}};
  if(mode==1){var body=new CheckinReplyBody{AgentId=req!.AgentId,RequestNonce=req.Nonce,Ok=true,Tasks=resp.Tasks};
    resp.SignedPayload=ProtocolJson.Serialize(body);resp.Signature=Crypto.Sign(evilKey.PrivateKeyPem,resp.SignedPayload);resp.SignerPublicKeyPem=evilKey.PublicKeyPem;}
  var b=Encoding.UTF8.GetBytes(ProtocolJson.Serialize(resp));c.Response.ContentType="application/json";await c.Response.OutputStream.WriteAsync(b);c.Response.Close();}});
var cfg=AgentConfig.Load(); cfg.ServerUrl="http://localhost:19999"; cfg.Save();
log.Clear(); await NewCore().CheckinAsync(CancellationToken.None);
Chk("Aláírás nélküli válasz feladata NEM fut le", !log.Any(l=>l.StartsWith("Feladat")&&l.Contains("Feladat:")) && log.Any(l=>l.Contains("nincs aláírva")), string.Join(" | ",log));
mode=1; log.Clear(); await NewCore().CheckinAsync(CancellationToken.None);
Chk("Idegen kulccsal aláírt feladat NEM fut le", !log.Any(l=>l.Contains("Feladat:")) && log.Any(l=>l.Contains("BIZTONSÁGI HIBA")), string.Join(" | ",log));
Chk("A tárolt szerverkulcs NEM cserélődött a támadóéra", AgentConfig.Load().ServerFingerprint==fp1);
evil.Stop(); cfg=AgentConfig.Load(); cfg.ServerUrl=AG; cfg.Save();

Console.WriteLine("--- A3: kulcscsere újraregisztrálás nélkül ---");
var rot=await Web("rotate-keys",new{}); var fp2=rot.GetProperty("fingerprint").GetString()!;
Chk("A szerver kulcsa cserélődött", rot.GetProperty("ok").GetBoolean() && fp2!=fp1);
log.Clear(); await NewCore().CheckinAsync(CancellationToken.None);
Chk("Az agent ellenőrizte az átadást és átállt", log.Any(l=>l.Contains("átállt")), string.Join(" | ",log));
Chk("Az agent az új ujjlenyomatot tárolja", AgentConfig.Load().ServerFingerprint==fp2);
log.Clear(); await NewCore().CheckinAsync(CancellationToken.None);
Chk("Utána is gond nélkül jelentkezik", log.Any(l=>l.Contains("Jelentkezés kész")));
try{Directory.Delete(AgentConfig.Dir,true);}catch{}
var tok2=await Web("token",new{label="regi-csomag"});
new AgentConfig{ServerUrl=AG,EnrollmentToken=tok2.GetProperty("token").GetString()!,ServerFingerprint=fp1}.Save();
log.Clear(); await NewCore().CheckinAsync(CancellationToken.None);
Chk("Régi telepítőcsomag (régi ujjlenyomat) is működik", log.Any(l=>l.Contains("Sikeres regisztráció")), string.Join(" | ",log.TakeLast(2)));
try{Directory.Delete(AgentConfig.Dir,true);}catch{}

Console.WriteLine("--- A2: belépés-korlátozás ---");
var anon=new HttpClient{BaseAddress=new Uri(WEB)}; string last="";
for(int i=0;i<6;i++) last=await (await anon.PostAsync("/web/login",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="rossz"+i}))).Content.ReadAsStringAsync();
Chk("6. hibás próbálkozás után zárolva", last.Contains("Túl sok sikertelen"));
var good=await (await anon.PostAsync("/web/login",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="SzuperEros2024xy"}))).Content.ReadAsStringAsync();
Chk("Zárolás alatt helyes jelszóval sem enged", good.Contains("Túl sok sikertelen"));

Console.WriteLine("--- A4: műveleti napló ---");
var audit=(await Web("audit",new{})).GetProperty("log").GetString()!;
Chk("Kulcscsere naplózva", audit.Contains("KULCSCSERE"));
Chk("Frissítés-kiadás naplózva", audit.Contains("frissítés kiadva"));
Chk("Sikertelen belépések naplózva", audit.Contains("sikertelen belépés") && audit.Contains("belépés tiltva"));
Chk("Jelszó nincs a naplóban", !audit.Contains("SzuperEros") && !audit.Contains("rossz0"));
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
