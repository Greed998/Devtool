using System.Net;
using System.Text;
using System.Text.Json;
using DevToolbox.Fleet.Server;
// Elofeltetel: frissen indult Kozpont a localhost:18090-en (a run-tests ezt intezi).
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
const string HUB="http://localhost:18090";
var jar=new CookieContainer(); var web=new HttpClient(new HttpClientHandler{CookieContainer=jar}){BaseAddress=new Uri(HUB)};
async Task<(int Code,string Body,bool Dropped)> Get(string path,string? realIp){
  var r=new HttpRequestMessage(HttpMethod.Get,path); if(realIp!=null) r.Headers.Add("X-Real-IP",realIp);
  try{var resp=await web.SendAsync(r);return ((int)resp.StatusCode,await resp.Content.ReadAsStringAsync(),false);}
  catch(HttpRequestException){return (0,"",true);} }

Console.WriteLine("--- Központ: hozzáférés élőben (NPM mögötti helyzet szimulálva) ---");
var setup=await web.PostAsync("/web/setup",new FormUrlEncodedContent(new Dictionary<string,string>{["user"]="admin",["password"]="SzuperEros2024xy",["password2"]="SzuperEros2024xy",["domain"]="fleet.poltoli.hu",["webPort"]="18090"}));
Chk("Setup belső hálóról", setup.IsSuccessStatusCode);
foreach (var path in new[]{"/web","/","/web/api/sites","/web/login"}){
  var (code,body,dropped)=await Get(path,"8.8.8.8");
  Chk($"Kívülről {path}: nincs oldal (bontás vagy üres 404)", dropped || (code==404 && body.Length==0), dropped?"bontva":$"{code}, {body.Length} bájt");
  Chk($"Kívülről {path}: nem szivárog belső cím", !body.Contains("192.168") && !body.Contains("18090"));
}
var ping=await Get("/hub/ping","8.8.8.8");
Chk("A telephely-API kívülről is nyitva", ping.Code==200 && ping.Body.Contains("devtoolbox-hub"));
Chk("Admin belső IP-ről: 200", (await Get("/web","192.168.0.50")).Code==200);

Console.WriteLine("--- Telephely: érthető hiba, ha az NPM rosszul van beállítva ---");
var fake=new HttpListener(); fake.Prefixes.Add("http://localhost:18099/"); fake.Start();
_=Task.Run(async()=>{while(fake.IsListening){try{var c=await fake.GetContextAsync();c.Response.StatusCode=502;var b=Encoding.UTF8.GetBytes("<html>502 Bad Gateway</html>");
  c.Response.ContentType="text/html";await c.Response.OutputStream.WriteAsync(b);c.Response.Close();}catch{break;}}});
var dir=Path.Combine(Path.GetTempPath(),"site-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
var bad=new ServerConfig{HubUrl="http://localhost:18099",HubToken=new string('A',43),HubFingerprint=new string('B',64)};
try{ await HubClient.Instance.CycleAsync(new FleetStore(dir),bad,dir,CancellationToken.None); Chk("502 hibát jelez",false); }
catch(Exception ex){ Chk("NPM 502: érthető ok", ex.Message.Contains("502") && ex.Message.Contains("NPM nem éri el"), ex.Message); }
fake.Stop(); try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT")); return fail;
