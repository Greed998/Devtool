using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
const string AG="http://localhost:19743", WEB="http://localhost:19380";
var jar=new CookieContainer(); var web=new HttpClient(new HttpClientHandler{CookieContainer=jar}){BaseAddress=new Uri(WEB)};
async Task<JsonElement> Web(string p,object b){var r=await web.PostAsync("/web/api/"+p,new StringContent(JsonSerializer.Serialize(b),Encoding.UTF8,"application/json"));return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;}
await web.PostAsync("/web/setup",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="SzuperEros2024xy",["password2"]="SzuperEros2024xy",["webPort"]="19380",["agentPort"]="19743"}));
var tok=await Web("token",new{label="b6"}); var fp=tok.GetProperty("fingerprint").GetString()!;

Console.WriteLine("--- Kapcsolat tesztelése (varázsló) ---");
var r=await SetupCheck.TestServerAsync(AG,fp);            Chk("Jó cím + jó ujjlenyomat", r.Ok, r.Message);
r=await SetupCheck.TestServerAsync(AG+"/",fp);           Chk("Záró perjellel is", r.Ok, r.Message);
r=await SetupCheck.TestServerAsync(AG,Crypto.NewKeyPair().Fingerprint); Chk("Rossz ujjlenyomat: BIZTONSÁGI HIBA", !r.Ok && r.Message.Contains("BIZTONSÁGI"), r.Message);
r=await SetupCheck.TestServerAsync(AG,"");               Chk("Hiányzó ujjlenyomat: nem enged", !r.Ok, r.Message);
r=await SetupCheck.TestServerAsync(WEB,fp);              Chk("Rossz port (a web-port): más szolgáltatás", !r.Ok && r.Message.Contains("más szolgáltatás"), r.Message);
r=await SetupCheck.TestServerAsync("http://localhost:19998",fp); Chk("Zárt port: nem érhető el", !r.Ok && r.Message.Contains("nem érhető el"), r.Message);
r=await SetupCheck.TestServerAsync("https://localhost:19743",fp); Chk("https a http-s szerverre: érthető TLS-hiba", !r.Ok && r.Message.Contains("TLS"), r.Message);
r=await SetupCheck.TestServerAsync("szerver:8443",fp);   Chk("Érvénytelen cím", !r.Ok && r.Message.Contains("nem érvényes"), r.Message);
await Web("rotate-keys",new{});
r=await SetupCheck.TestServerAsync(AG,fp);               Chk("Kulcscsere után a régi csomag ujjlenyomata is elfogadva", r.Ok, r.Message);

Console.WriteLine("--- Webes ZIP ---");
var tok2=await Web("token",new{label="zip"});
var zb=await web.GetByteArrayAsync(tok2.GetProperty("zipUrl").GetString()!);
using var z=new ZipArchive(new MemoryStream(zb));
Chk("A ZIP-ben ott az előtöltő fájl", z.GetEntry("DevToolboxAgent.telepites.json") is not null, string.Join(",",z.Entries.Select(e=>e.Name)));
var pre=JsonDocument.Parse(new StreamReader(z.GetEntry("DevToolboxAgent.telepites.json")!.Open()).ReadToEnd()).RootElement;
Chk("Előtöltés: agent-cím (http, agent-port)", pre.GetProperty("server").GetString()=="http://localhost:19743", pre.GetProperty("server").GetString()!);
Chk("Előtöltés: token", pre.GetProperty("token").GetString()==tok2.GetProperty("token").GetString());
Chk("Előtöltés: a JELENLEGI ujjlenyomat", pre.GetProperty("fingerprint").GetString()==tok2.GetProperty("fingerprint").GetString() && pre.GetProperty("fingerprint").GetString()!=fp);
var r2=await SetupCheck.TestServerAsync(pre.GetProperty("server").GetString()!,pre.GetProperty("fingerprint").GetString()!);
Chk("Az előtöltött adatokkal a teszt sikeres", r2.Ok, r2.Message);
Chk("OLVASSEL a varázslót írja le", new StreamReader(z.GetEntry("OLVASSEL.txt")!.Open()).ReadToEnd().Contains("Dupla kattintas"));
Chk("A TELEPIT.cmd tartalék megmaradt", z.GetEntry("TELEPIT.cmd") is not null);
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
