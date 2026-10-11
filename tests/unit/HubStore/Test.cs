using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
int fail=0; void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var dir=Path.Combine(Path.GetTempPath(),"hub-"+Guid.NewGuid().ToString("N"));
var hub=new HubStore(dir);
Chk("Központ kulcsot generált", hub.Keys.PublicKeyPem.Contains("PUBLIC KEY"));

// telephely kulcspár
var site=Crypto.NewKeyPair();

Console.WriteLine("--- Regisztráció token nélkül / rossz tokennel ---");
Chk("Rossz token elutasítva", !hub.Enroll(new HubEnrollRequest{Token="rossz",PublicKeyPem=site.PublicKeyPem}).Success);

var tok=hub.CreateToken("Ügyfél Kft.",60,1);
var en=hub.Enroll(new HubEnrollRequest{Token=tok,PublicKeyPem=site.PublicKeyPem,SiteName="Ugyfel",ServerVersion="1.2.0"});
Chk("Regisztráció sikeres", en.Success && en.SiteId.Length==32);
Chk("Visszakapja a Központ kulcsát (pinninghez)", en.HubPublicKeyPem==hub.Keys.PublicKeyPem);
Chk("Egyszer használatos token: második telephely elutasítva",
    !hub.Enroll(new HubEnrollRequest{Token=tok,PublicKeyPem=Crypto.NewKeyPair().PublicKeyPem}).Success);

Console.WriteLine("--- Aláírt jelentkezés ---");
HubCheckinReply Checkin(string nonce,string ts,string priv,string sid){
  var body=new HubCheckinBody{Timestamp=ts,Nonce=nonce,Summary=new SiteSummary{SiteName="Ugyfel",ServerVersion="1.2.0",
    Agents=new(){new SiteAgent{AgentId="a1",MachineName="PC1",UpdatesAvailable=3}}}};
  var env=SignedEnvelope.Create(sid,body,priv);
  var replyEnv=hub.Checkin(env);
  Chk("A válasz a Központ kulcsával aláírt", replyEnv.Verify(hub.Keys.PublicKeyPem));
  return replyEnv.Open<HubCheckinReply>()!;
}
var r1=Checkin(Crypto.NewNonce(),DateTime.UtcNow.ToString("o"),site.PrivateKeyPem,en.SiteId);
Chk("Helyes jelentkezés elfogadva", r1.Ok);
Chk("A válasz nonce-a a kéréshez kötött", r1.RequestNonce.Length>0);
Chk("A Központ visszaadja a betárcsázási időt", r1.NextCheckinSeconds==300);

// hamis aláírás
var badBody=new HubCheckinBody{Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Summary=new()};
var badEnv=SignedEnvelope.Create(en.SiteId,badBody,Crypto.NewKeyPair().PrivateKeyPem);
Chk("Hamis aláírású jelentkezés elutasítva", !hub.Checkin(badEnv).Open<HubCheckinReply>()!.Ok);

// replay
var fixNonce=Crypto.NewNonce(); var ts=DateTime.UtcNow.ToString("o");
Chk("Első nonce átmegy", Checkin(fixNonce,ts,site.PrivateKeyPem,en.SiteId).Ok);
var replayEnv=SignedEnvelope.Create(en.SiteId,new HubCheckinBody{Timestamp=ts,Nonce=fixNonce,Summary=new()},site.PrivateKeyPem);
Chk("Megismételt nonce elutasítva", !hub.Checkin(replayEnv).Open<HubCheckinReply>()!.Ok);

// elavult
var stale=SignedEnvelope.Create(en.SiteId,new HubCheckinBody{Timestamp=DateTime.UtcNow.AddHours(-1).ToString("o"),Nonce=Crypto.NewNonce(),Summary=new()},site.PrivateKeyPem);
Chk("Elavult időbélyeg elutasítva", !hub.Checkin(stale).Open<HubCheckinReply>()!.Ok);

Console.WriteLine("--- Gyakoriság-vezérlés a Központból ---");
hub.SetInterval(en.SiteId,60);
var r2=Checkin(Crypto.NewNonce(),DateTime.UtcNow.ToString("o"),site.PrivateKeyPem,en.SiteId);
Chk("A telephely 60 mp-et kap a Központtól", r2.NextCheckinSeconds==60);

Console.WriteLine("--- Feladat lefelé ---");
hub.QueueTask(en.SiteId,new HubTask{AgentId="a1",Kind=AgentTaskKind.UpgradeAll});
var r3=Checkin(Crypto.NewNonce(),DateTime.UtcNow.ToString("o"),site.PrivateKeyPem,en.SiteId);
Chk("A feladat leér a telephelyhez", r3.Tasks.Count==1 && r3.Tasks[0].AgentId=="a1");
var r4=Checkin(Crypto.NewNonce(),DateTime.UtcNow.ToString("o"),site.PrivateKeyPem,en.SiteId);
Chk("A feladat nem ismétlődik", r4.Tasks.Count==0);

Console.WriteLine("--- Visszavonás ---");
hub.Revoke(en.SiteId);
Chk("Visszavont telephely elutasítva", !Checkin(Crypto.NewNonce(),DateTime.UtcNow.ToString("o"),site.PrivateKeyPem,en.SiteId).Ok);

// perzisztencia
var hub2=new HubStore(dir);
Chk("A telephelyek túlélik az újraindítást", hub2.Sites.Count==1);
Chk("A Központ kulcsa megmarad", hub2.Keys.PublicKeyPem==hub.Keys.PublicKeyPem);

try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
