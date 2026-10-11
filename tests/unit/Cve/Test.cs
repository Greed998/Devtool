using System.Text.Json;
using DevToolbox.Fleet;
using DevToolbox.Fleet.Hub;
int fail=0;void Chk(string n,bool ok,string i=""){Console.WriteLine((ok?"  OK   ":"  HIBA ")+n+(i.Length>0?"  -> "+i:""));if(!ok)fail++;}

var root=Path.Combine(Path.GetTempPath(),"cve-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("FLEET_HUB_DATA",root);

Console.WriteLine("--- Programnév és verzió egyszerűsítése ---");
Chk("Firefox: zárójel, nyelv nélkül",HubCve.NormalizeName("Mozilla Firefox (x64 hu)")=="mozilla firefox",HubCve.NormalizeName("Mozilla Firefox (x64 hu)"));
Chk("7-Zip: verziószám nélkül",HubCve.NormalizeName("7-Zip 23.01 (x64)")=="7-zip",HubCve.NormalizeName("7-Zip 23.01 (x64)"));
Chk(".NET: a „ - verzió (x64)” vége nélkül",HubCve.NormalizeName("Microsoft .NET Runtime - 8.0.5 (x64)")=="microsoft .net runtime",HubCve.NormalizeName("Microsoft .NET Runtime - 8.0.5 (x64)"));
Chk("Wireshark x64",HubCve.NormalizeName("Wireshark 4.2.5 x64")=="wireshark",HubCve.NormalizeName("Wireshark 4.2.5 x64"));
Chk("PowerShell 7-x64",HubCve.NormalizeName("PowerShell 7-x64")=="powershell 7",HubCve.NormalizeName("PowerShell 7-x64"));
Chk("Notepad++",HubCve.NormalizeName("Notepad++ (64-bit x64)")=="notepad++",HubCve.NormalizeName("Notepad++ (64-bit x64)"));
Chk("Lenovo BIOS-verzió a zárójelből",HubCve.NormalizeVersion("N32ET88W (1.64 )","bios")=="1.64");
Chk("HP BIOS-verzió a „Ver.” után",HubCve.NormalizeVersion("T76 Ver. 01.12.00","bios")=="01.12.00");
Chk("Dell BIOS-verzió",HubCve.NormalizeVersion("1.17.0","bios")=="1.17.0");
Chk("Szám nélküli verzió: üres",HubCve.NormalizeVersion("Unknown")=="");
Chk("NVIDIA: 31.0.15.3742 → 537.42",HubCve.NvidiaVersion("31.0.15.3742")=="537.42",HubCve.NvidiaVersion("31.0.15.3742"));
Chk("NVIDIA: 32.0.15.6094 → 560.94",HubCve.NvidiaVersion("32.0.15.6094")=="560.94",HubCve.NvidiaVersion("32.0.15.6094"));
Chk("Dell BIOS-termék",HubCve.BiosKeys("Dell Inc.","Latitude 7420").SequenceEqual(new[]{"o:dell:latitude_7420_firmware"}));
Chk("HP BIOS-termék (rövidített változattal)",HubCve.BiosKeys("HP","HP EliteBook 840 G8 Notebook PC").SequenceEqual(new[]{"o:hp:elitebook_840_g8_notebook_pc_firmware","o:hp:elitebook_840_g8_firmware"}),
  string.Join(",",HubCve.BiosKeys("HP","HP EliteBook 840 G8 Notebook PC")));
Chk("Ismeretlen gyártó: nincs BIOS-keresés",HubCve.BiosKeys("Microsoft Corporation","Virtual Machine").Length==0);

Console.WriteLine("--- Verzió-összevetés ---");
Chk("0.81 = 0.81.0.0",HubCve.CompareVersions("0.81","0.81.0.0")==0);
Chk("1.10 > 1.9",HubCve.CompareVersions("1.10","1.9")>0);
Chk("01.12.00 = 1.12",HubCve.CompareVersions("01.12.00","1.12")==0);
Chk("4.43 > 4.43 beta",HubCve.CompareVersions("4.43","4.43.beta")>0);
Chk("23.01 < 24.07",HubCve.CompareVersions("23.01","24.07")<0);
var r=new HubCve.NvdRange{V="*",Si="23.0",Ee="24.07"};
Chk("Tartomány: 23.01 érintett",HubCve.Affects(r,"23.01"));
Chk("Tartomány: 24.07 már javított",!HubCve.Affects(r,"24.07"));
Chk("Tartomány: 22.0 nem érintett (a kezdete előtt)",!HubCve.Affects(r,"22.0"));
Chk("Pontos verzió",HubCve.Affects(new HubCve.NvdRange{V="4.42"},"4.42.0")&&!HubCve.Affects(new HubCve.NvdRange{V="4.42"},"4.43"));

Console.WriteLine("--- NVD-bejegyzés feldolgozása ---");
var json="""
{"id":"CVE-2099-0001","published":"2099-01-02T10:00:00.000","vulnStatus":"Analyzed",
 "descriptions":[{"lang":"es","value":"x"},{"lang":"en","value":"Heap overflow in 7-Zip."}],
 "metrics":{"cvssMetricV31":[{"type":"Secondary","cvssData":{"baseScore":5.0,"baseSeverity":"MEDIUM"}},{"type":"Primary","cvssData":{"baseScore":9.8,"baseSeverity":"CRITICAL"}}]},
 "configurations":[{"nodes":[{"operator":"OR","negate":false,"cpeMatch":[
   {"vulnerable":true,"criteria":"cpe:2.3:a:7-zip:7-zip:*:*:*:*:*:*:*:*","versionEndExcluding":"24.07"},
   {"vulnerable":true,"criteria":"cpe:2.3:a:7-zip:7-zip:*:*:*:*:*:*:*:*"},
   {"vulnerable":true,"criteria":"cpe:2.3:a:7-zip:7-zip:4.43:beta:*:*:*:*:*:*"},
   {"vulnerable":true,"criteria":"cpe:2.3:a:7-zip:7-zip:*:*:*:*:*:linux:*:*","versionEndExcluding":"99.0"},
   {"vulnerable":false,"criteria":"cpe:2.3:o:microsoft:windows:-:*:*:*:*:*:*:*"},
   {"vulnerable":true,"criteria":"cpe:2.3:a:other:7-zip:*:*:*:*:*:*:*:*","versionEndExcluding":"99.0"}]}]}]}
""";
var c=HubCve.ParseCve(JsonDocument.Parse(json).RootElement,new[]{"a","7-zip","7-zip"});
Chk("Feldolgozva",c is not null);
Chk("A Primary (NVD) pontszám",c!.Score==9.8&&c.Sev=="CRITICAL",$"{c.Score} {c.Sev}");
Chk("Angol leírás, dátum",c.Sum=="Heap overflow in 7-Zip."&&c.Pub=="2099-01-02");
Chk("Csak a tartomány marad (a „minden verzió”, a béta, a Linux, a más gyártó kimarad)",c.R.Count==1&&c.R[0].Ee=="24.07",c.R.Count.ToString());
var rej=HubCve.ParseCve(JsonDocument.Parse("""{"id":"CVE-2099-0002","vulnStatus":"Rejected","configurations":[]}""").RootElement,new[]{"a","7-zip","7-zip"});
Chk("Visszavont bejegyzés kimarad",rej is null);

Console.WriteLine("--- Egyeztetés a gyorsítótárból (hálózat nélkül) ---");
HubCve.PutProduct(new HubCve.NvdProduct{Key="a:7-zip:7-zip",FetchedUtc=DateTime.UtcNow,Cves=new(){c}});
HubCve.PutProduct(new HubCve.NvdProduct{Key="a:nvidia:gpu_display_driver",FetchedUtc=DateTime.UtcNow,Cves=new(){new HubCve.NvdCve{Id="CVE-2099-0003",Score=7.8,Sev="HIGH",R=new(){new HubCve.NvdRange{Si="535.0",Ee="538.15"}}}}});
var site=new HubSite{SiteId="s1",Name="Iroda",LastSummary=new SiteSummary{Agents=new(){
  new SiteAgent{AgentId="a1",MachineName="PC1",Installed=new(){new InstalledApp{Name="7-Zip 23.01 (x64)",Version="23.01"},new InstalledApp{Name="Valami Program",Version="1.0"}},
    Components=new(){new ComponentInfo{Kind="driver",Vendor="NVIDIA",Name="NVIDIA RTX A2000",Version="31.0.15.3742",Detail="Display"}}},
  new SiteAgent{AgentId="a2",MachineName="PC2",Installed=new(){new InstalledApp{Name="7-Zip 24.08 (x64)",Version="24.08"}}}}}};
HubCve.Rematch(()=>new[]{site});
var a1=HubCve.ForAgent("s1","a1");var a2=HubCve.ForAgent("s1","a2");
Chk("PC1: a régi 7-Zip és az NVIDIA-driver érintett",a1 is not null&&a1.Findings.Count==2&&a1.Critical==1&&a1.High==1,a1 is null?"nincs":string.Join(",",a1.Findings.Select(f=>f.Id)));
Chk("A javított verzió kijelezve",a1!.Findings.First(f=>f.Id=="CVE-2099-0001").FixedIn=="24.07");
Chk("Az NVIDIA a gyártói verzióval",a1.Findings.First(f=>f.Id=="CVE-2099-0003").Version=="537.42");
Chk("PC2: a frissített 7-Zip nem érintett",a2 is not null&&a2.Findings.Count==0);
var d1=HubCve.ForSite("s1","");
Chk("A telephely adatai mennek le (lenyomattal)",d1 is not null&&d1.Hash.Length==32&&d1.Agents.Count==2);
Chk("Ugyanazzal a lenyomattal nem megy le újra",HubCve.ForSite("s1",d1!.Hash) is null);
HubCve.Rematch(()=>new[]{site});
Chk("Változatlan adatnál a lenyomat ugyanaz",HubCve.ForSite("s1",d1.Hash) is null);
Chk("Az eredmény a lemezen",File.Exists(Path.Combine(root,"cve","eredmeny.json")));

try{Directory.Delete(root,true);}catch{}
Console.WriteLine(fail==0?"MINDEN TESZT ATMENT":$"{fail} HIBA");
return fail==0?0:1;
