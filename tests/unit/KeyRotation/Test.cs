using System.Security.Cryptography;
using System.Text;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
using DevToolbox.Fleet.Server;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}
var dir=Path.Combine(Path.GetTempPath(),"keys-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);

Console.WriteLine("--- Erősebb kulcs, kompatibilitás ---");
var p256=Crypto.NewKeyPair(); var p384=Crypto.NewKeyPair(strong:true);
Chk("Alap: P-256, erős: P-384",Crypto.KeyBits(p256.PublicKeyPem)==256&&Crypto.KeyBits(p384.PublicKeyPem)==384&&Crypto.Algorithm(p384.PublicKeyPem)=="ECDSA P-384 / SHA-384");
var s384=Crypto.Sign(p384.PrivateKeyPem,"adat");
Chk("P-384 aláírás ellenőrizhető, más adatra nem",Crypto.Verify(p384.PublicKeyPem,"adat",s384)&&!Crypto.Verify(p384.PublicKeyPem,"adaT",s384));
bool OldVerify(string pem,string data,string sig){using var ec=ECDsa.Create();ec.ImportFromPem(pem);return ec.VerifyData(Encoding.UTF8.GetBytes(data),Convert.FromBase64String(sig),HashAlgorithmName.SHA256);}
Chk("P-256 aláírás bitre a régi (SHA-256) – a régi agent / telephely ellenőrzi",OldVerify(p256.PublicKeyPem,"adat",Crypto.Sign(p256.PrivateKeyPem,"adat")));
Chk("P-384 aláírást a régi kód NEM tudja ellenőrizni – ezért kell a feltétel a szerverkulcsnál",!OldVerify(p384.PublicKeyPem,"adat",s384));

Console.WriteLine("--- Szerverkulcs: lánc és előzmény ---");
var ks=KeyStore.LoadOrCreate(Path.Combine(dir,"ks"),"server");
var first=ks.Current.PublicKeyPem;
Chk("Új kulcstár: egy kulcs az előzményben",ks.History.Count==1&&ks.History[0].RetiredUtc is null);
ks.Rotate(Path.Combine(dir,"ks"),"server",strong:true,reason:"automatikus (7 naponta)");
Chk("Csere erős kulcsra: P-384, a régi lezárva az előzményben",Crypto.KeyBits(ks.Current.PublicKeyPem)==384&&ks.History.Count==2&&ks.History[0].RetiredUtc is not null&&ks.History[1].Reason.StartsWith("automatikus"));
Chk("A P-256 → P-384 átadás ellenőrizhető a régi kulcstól",KeyStore.ChainReaches(first,ks.Links,ks.Current.PublicKeyPem));
for(int i=0;i<70;i++)ks.Rotate(Path.Combine(dir,"ks"),"server",strong:true);
Chk("A lánc legfeljebb 60 szem",ks.Links.Count==60,ks.Links.Count.ToString());
Chk("59 cserével korábbi kulcstól még eljut",KeyStore.ChainReaches(ks.Links[0].PublicKeyPem,ks.Links,ks.Current.PublicKeyPem));
Chk("A legelső (láncból kiesett) kulcstól már nem",!KeyStore.ChainReaches(first,ks.Links,ks.Current.PublicKeyPem));
Chk("Az előzmény a láncból kiesett kulcsokat is őrzi",ks.History.Count==72);
var ks2=KeyStore.LoadOrCreate(Path.Combine(dir,"ks"),"server");
Chk("Az előzmény újraindítás után is megvan",ks2.History.Count==72&&ks2.Current.PublicKeyPem==ks.Current.PublicKeyPem);

Console.WriteLine("--- Agent kulcscseréje ---");
var store=new FleetStore(Path.Combine(dir,"srv"));
(string Id,Crypto.KeyPair K) Enroll(string name,string ver){var k=Crypto.NewKeyPair();var r=store.Enroll(new EnrollRequest{Token=store.CreateEnrollmentToken("t",60,1),PublicKeyPem=k.PublicKeyPem,MachineName=name,AgentVersion=ver});return (r.AgentId,k);}
var (aid,k1)=Enroll("PC-1","1.4.4");
var agent=store.GetAgent(aid)!;
Chk("Regisztráció: az első kulcs az előzményben",agent.KeyHistory.Count==1&&agent.KeyHistory[0].Reason=="regisztráció");
KeyRotateRequest Rot(Crypto.KeyPair oldK,Crypto.KeyPair newK,Crypto.KeyPair? popK=null){var r=new KeyRotateRequest{AgentId=aid,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),NewPublicKeyPem=newK.PublicKeyPem};
  var p=r.CanonicalPayload();r.Signature=Crypto.Sign(oldK.PrivateKeyPem,p);r.NewKeySignature=Crypto.Sign((popK??newK).PrivateKeyPem,p);return r;}
CheckinResponse Checkin(Crypto.KeyPair k,string ver="1.4.5"){var r=new CheckinRequest{AgentId=aid,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Status=new AgentStatus{AgentVersion=ver}};
  r.Signature=Crypto.Sign(k.PrivateKeyPem,r.CanonicalPayload());return store.Checkin(r);}

var k2=Crypto.NewKeyPair(strong:true);
var req2=Rot(k1,k2);
var (ok,msg,fp)=store.RotateAgentKey(req2);
Chk("Csere elfogadva (régi kulccsal aláírva + birtoklás-bizonyítás)",ok&&fp==k2.Fingerprint&&agent.PublicKeyPem==k2.PublicKeyPem&&agent.PreviousPublicKeyPem==k1.PublicKeyPem,msg);
Chk("Visszajátszott csere-kérés elutasítva",!store.RotateAgentKey(req2).Ok);
Chk("Megerősítésig a régi kulcs is érvényes (elveszett visszaigazolás)",Checkin(k1).Ok&&agent.PreviousPublicKeyPem.Length>0);
Chk("Az új kulccsal jelentkezik: megerősítve, a régi kulcs törölve",Checkin(k2).Ok&&agent.PreviousPublicKeyPem.Length==0&&agent.KeyHistory[^1].Reason.Contains("megerősítve"));
Chk("A régi kulcs többé nem jó",!Checkin(k1).Ok);
var kx=Crypto.NewKeyPair(true);
Chk("Birtoklás-bizonyítás nélkül (más kulcs írta alá) elutasítva",!store.RotateAgentKey(Rot(k2,kx,popK:Crypto.NewKeyPair(true))).Ok);
Chk("Idegen kulccsal kért csere elutasítva",!store.RotateAgentKey(Rot(Crypto.NewKeyPair(),kx)).Ok);

var k3=Crypto.NewKeyPair(true);
Chk("Újabb csere (k2 → k3)",store.RotateAgentKey(Rot(k2,k3)).Ok);
var k4=Crypto.NewKeyPair(true);
var r4=store.RotateAgentKey(Rot(k2,k4));   // a k3-as visszaigazolás elveszett: az agent még k2-vel kér
Chk("Elveszett visszaigazolás után újrapróba a régi kulccsal: elfogadva, a soha nem használt k3 elvetve",
    r4.Ok&&agent.PublicKeyPem==k4.PublicKeyPem&&agent.PreviousPublicKeyPem==k2.PublicKeyPem&&agent.KeyHistory.Any(h=>h.Fingerprint==k3.Fingerprint&&h.Reason.Contains("elvetve")),r4.Message);
Chk("Az elvetett k3 nem használható",!Checkin(k3).Ok);
Chk("k4-gyel megerősítve",Checkin(k4).Ok&&agent.PreviousPublicKeyPem.Length==0);
Chk("Előzmény: regisztráció + 3 csere",agent.KeyHistory.Count==4,string.Join(" | ",agent.KeyHistory.Select(h=>h.Reason)));

var (aid2,kOther)=Enroll("PC-2","1.4.4");
Chk("Másik gép kulcsára cserélni nem lehet",!store.RotateAgentKey(Rot(k4,kOther)).Ok);
var pc2=store.GetAgent(aid2)!;
store.RevokeAgent(aid2);
var rr=new KeyRotateRequest{AgentId=aid2,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),NewPublicKeyPem=kx.PublicKeyPem};
rr.Signature=Crypto.Sign(kOther.PrivateKeyPem,rr.CanonicalPayload());rr.NewKeySignature=Crypto.Sign(kx.PrivateKeyPem,rr.CanonicalPayload());
Chk("Visszavont gép nem cserélhet kulcsot",!store.RotateAgentKey(rr).Ok);
var store2=new FleetStore(Path.Combine(dir,"srv"));
Chk("A kulcscsere és az előzmény újraindítás után is megvan",store2.GetAgent(aid)!.PublicKeyPem==k4.PublicKeyPem&&store2.GetAgent(aid)!.KeyHistory.Count==4);

Console.WriteLine("--- Erős szerverkulcs csak ha minden agent tudja ellenőrizni ---");
var (aid3,k5)=Enroll("RÉGI-PC","1.4.4");
Chk("Régi (1.4.4) agent visszatartja",store.StrongKeyBlockers().Count==1&&store.StrongKeyBlockers()[0].StartsWith("RÉGI-PC"),string.Join(",",store.StrongKeyBlockers()));
store.RotateKeys("kézi");
Chk("…ezért a szerver új kulcsa még P-256",Crypto.KeyBits(store.ServerKeys.PublicKeyPem)==256);
var c5=new CheckinRequest{AgentId=aid3,Timestamp=DateTime.UtcNow.ToString("o"),Nonce=Crypto.NewNonce(),Status=new AgentStatus{AgentVersion="1.4.5"}};
c5.Signature=Crypto.Sign(k5.PrivateKeyPem,c5.CanonicalPayload());store.Checkin(c5);
Chk("Frissült az agent: nincs visszatartó",store.StrongKeyBlockers().Count==0);
store.RotateKeys("automatikus (7 naponta)");
Chk("…és a szerver kulcsa már P-384",Crypto.KeyBits(store.ServerKeys.PublicKeyPem)==384&&store.ServerKeyHistory.Count==3&&store.ServerKeyHistory[^1].Reason.StartsWith("automatikus"));

Console.WriteLine("--- Központ: erős kulcs csak ha minden telephely tudja; tokenek ---");
var hub=new HubStore(Path.Combine(dir,"hub"));
var tok=hub.CreateToken("Ügyfél",60,1);
var siteKey=Crypto.NewKeyPair();
hub.Enroll(new HubEnrollRequest{Token=tok,PublicKeyPem=siteKey.PublicKeyPem,SiteName="Ügyfél",ServerVersion="1.4.4"});
Chk("Régi telephely visszatartja",hub.StrongKeyBlockers().Count==1);
hub.RotateKeys("kézi");
Chk("…ezért a Központ új kulcsa P-256",Crypto.KeyBits(hub.Keys.PublicKeyPem)==256);
Chk("A token elhasználva látszik a listában",hub.Tokens.Single().Uses==1);
var tok2=hub.CreateToken("Másik",60,1);
var tfp=Crypto.TokenFingerprint(tok2);
Chk("Token letiltása",hub.DisableToken(tfp)&&!hub.Tokens.First(t=>t.Fingerprint==tfp).IsUsable&&hub.Enroll(new HubEnrollRequest{Token=tok2,PublicKeyPem=Crypto.NewKeyPair().PublicKeyPem}).Success==false);

try{Directory.Delete(dir,true);}catch{}
Console.WriteLine("\n"+(fail==0?"MINDEN TESZT ATMENT":$"{fail} BUKOTT"));return fail;
