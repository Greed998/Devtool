# DevToolbox – fejlesztési napló

Poltoli Informatika · állapot: 2026. október 11.

Ez a napló a fő programok alapjait, a fejlesztési terv állapotát és a verziótörténetet rögzíti.
Minden kiadásnál frissül. Verziózás: kisebb frissítésnél **csak az utolsó számjegy nő**.

---

## 1. Programok – legfrissebb verziók

| Program | Verzió | Szerep | Hol fut |
|---|---|---|---|
| **DevToolbox** (asztali) | 1.5.13 | Rendszergazdai eszköztár, Flotta fül | A rendszergazda gépén |
| **DevToolboxAgent** | 1.5.13 | Felügyeleti agent | Minden felügyelt gépen (szolgáltatás) |
| **Fleet** – telephelyi szerver szerep (FleetServer) | 1.5.13 | Telephelyi szerver + webfelület | Az ügyfél belső szerverén (szolgáltatás) |
| **Fleet** – Központ szerep (FleetHub) | 1.5.13 | Központi felügyelet, telephelyek betárcsázása | Poltoli oldalon, `fleet.poltoli.hu` mögött |

Mind önálló, .NET 10-es, 64 bites, egyfájlos exe – a gépre .NET nem kell. **1.3.0-tól a FleetServer és a FleetHub
egyetlen exe (`Fleet.exe`)**, benne a beépített agenttel; a szerepet telepítéskor választod (szerver / Központ /
mindkettő). A telepítő a régi néven (`FleetServer.exe` / `FleetHub.exe`) másolja be magát, így a meglévő
telepítések és a webes frissítés változatlanul működnek. Háttérben egyik program sem indít cmd-t vagy PowerShellt
(a winget kivétel): a szolgáltatás-, tűzfal-, URL-foglalás-, jog- és újraindítás-kezelés közvetlen Windows-hívás.

```
FleetHub (Központ) ── fleet.poltoli.hu (NPM + Let's Encrypt, Cloudflare DNS only)
   ▲  betárcsázás: kimenő HTTPS, mindkét irányban aláírt üzenetek
FleetServer (ügyfelenként egy) ── belső háló
   ▲  jelentkezés: kimenő HTTP, mindkét irányban aláírt üzenetek
DevToolboxAgent (minden gépen)
```

Az ügyfélnél nem kell fix IP, DDNS vagy nyitott bejövő port: a kapcsolatot mindig az alsó szint nyitja kifelé.

---

## 2. Programonkénti alapok

### 2.1 DevToolbox (asztali alkalmazás) – 1.5.9

WinForms alkalmazás a rendszergazda gépére. Oldalak: Áttekintés, Hardver, Tárolók (SMART), RAID-kalkulátor,
Hálózat, Eseménynapló-elemző, Szolgáltatások (49 minősített szolgáltatás), Távoli műveletek, Szkriptek
(szkriptkönyvtár + szkriptépítő, javított telepítő szkriptek), Alkalmazás-frissítő (winget), Monitorozás
(WinRM, tálcaértesítéssel), Flotta, Licenc, HTML-jelentés/export.

A **Flotta** fül a helyi FleetServer adatait olvassa, és a postaládán (`inbox\`) keresztül küld neki parancsot –
a `fleet.json`-t mindig csak a FleetServer szolgáltatás írja (egyetlen író).

Az **Alkalmazás-frissítő** helyben a wingetet futtatja; **távoli gépen (1.5.7-től) a gép agentje frissít**: a gép a
Flotta agentjei közül választható, a lista az agent legutóbbi jelentése, a frissítés feladatként a postaládán át megy
ki (UpgradeApp / UpgradeAll), és az agent a következő jelentkezéskor rendszergazdai jogon elvégzi. A korábbi
PowerShell Remoting-os (Invoke-Command) út megszűnt; agent nélküli gépre távolról nem frissít.

### 2.2 DevToolboxAgent – 1.5.8

| | |
|---|---|
| Szolgáltatás | `DevToolboxAgent`, LocalSystem, automatikus indítás, hibánál újraindul |
| Program | `C:\Program Files\DevToolbox Agent\DevToolboxAgent.exe` |
| Adatok | `C:\ProgramData\DevToolbox-Agent\` – `agent.json` (kulcs, azonosító, szerver), `agent.log`, `update\` |
| Kapcsolat | kimenő HTTP a FleetServer agent-portjára (alap: 8443) |

**Parancsok** (rendszergazdaként): dupla kattintás / `setup` – grafikus telepítő ·
`install --server … --token … --fingerprint …` · `uninstall` · `diag` · `once` · `run` · (`self-update` – belső).

**Tudja:**
- regisztráció egyszeri tokennel, saját ECDSA-kulcspárral; a szerver kulcsát ujjlenyomattal ellenőrzi;
- aláírt jelentkezés; a szerver válaszát is ellenőrzi – aláíratlan vagy idegen kulcsú válaszból **nem** hajt végre feladatot;
- winget: frissíthető programok, frissítés, telepítés, eltávolítás, telepített programok listája;
- Windows Update a Windows saját felületén (WSUS/Intune esetén azt a forrást használja): keresés, telepítés,
  függő újraindítás jelzése; újraindítás csak külön feladatra, késleltetéssel;
- metrikák – kategóriánként hibatűrő, a `diag` kategóriánként kiírja az eredményt:
  CPU (terhelés, fizikai magok, logikai szálak, foglalat, max. órajel, processzor-várakozási sor), rendszer (folyamatok, szálak, futásidő),
  memória (kihasználtság, szabad, összes, commit, lapozás), lemez (kihasználtság, szabad hely és méret meghajtónként),
  lemez-I/O (foglaltság, sor, olvasás, írás), hálózat (fogadás/küldés adapterenként és összesen), SMART,
  hőmérséklet (ACPI zónák), figyelt szolgáltatások (fut/áll) és folyamatok (példányszám), naplóhibák (1 óra);
- **mintavétel a jelentkezéstől függetlenül** (alap 60 mp, a szerver állítja): időbélyeges minták pufferben
  (max. 1440 ≈ 1 nap), jelentkezéskor egyben felküldve; a SMART és a naplóhibák csak jelentkezéskor;
- winget-lista 30 percenként (programfeladat után azonnal), nem minden jelentkezéskor;
- önfrissítés a szerverről: aláírt SHA-256 lenyomattal ellenőrzött letöltés, csere, hibánál visszaállítás;
- kulcscsere követése (a szerver kulcsláncát ellenőrzi, újraregisztrálás nélkül átáll);
- **saját kulcs cseréje (1.4.5):** a szerver szabálya szerint (alapból hetente) új ECDSA P-384 kulcspár; a kérést a régi
  és az új kulcs is aláírja, és csak a szerver aláírt, a kéréshez kötött visszaigazolására vált.
- **bővített mérés (1.5.0)** – minden csoport a „Mit mérjen?”-ben kapcsolható:
  CPU részletek (valós órajel, teljesítmény %, turbó, teljesítménykorlát / throttling, C1–C3 állapot, rendszermag-idő,
  DPC, megszakítás/s, környezetváltás/s, rendszerhívás/s) · magonkénti terhelés (alapból ki) · memória (foglalt, gyorsítótár,
  commit és korlát, laphiba/s, swap be/ki, lapozófájl %) · lemez (foglalt GB partíciónként; IOPS, kihasználtság, olvasási /
  írási válaszidő – nyers számlálókból) · lemez-állapot (`MSFT_StorageReliabilityCounter`: hőmérséklet, kopás, üzemóra,
  be-/kikapcsolás, javíthatatlan hibák, max. válaszidő; ATA SMART-attribútumok: áthelyezett / függő / offline javíthatatlan
  szektor, CRC, élettartam, összes írás) · hálózat (Mbps, kihasználtság a kártya sebességéhez, hibás / eldobott csomag,
  TCP ESTABLISHED / TIME_WAIT / összes / figyelő, UDP, újraküldés) · ping (szerver, átjáró, +további célok: válaszidő,
  jitter, csomagvesztés) · GPU a Windows számlálóiból (3D-terhelés, dedikált / megosztott videómemória) · figyelt folyamatok
  CPU %, memória, leíró, szál · eseménynapló XPath-szűrővel (kritikus / hiba / figyelmeztetés 1 óra, lemez- és fájlrendszer-
  hiba, szolgáltatás- és program-összeomlás 24 óra, váratlan leállás 7 nap, WHEA/ECC 24 óra) · biztonsági napló (sikeres
  helyi / távoli asztali belépés, sikertelen belépés, fiókzárolás, legtöbb próbálkozás egy forrásból) · Hyper-V (VM-ek
  állapota, VM CPU %, dinamikus memória, ellenőrzőpontok) · rendszer (újraindításra vár, függő Windows-frissítések,
  frissíthető programok – küszöb állítható rájuk) · a legtöbb memóriát / CPU-t használó folyamatok listája (adatlap);
- **hardver-érzékelők (1.5.0, a felhasználó döntése: alapból BE):** LibreHardwareMonitorLib 0.9.6 (`AgentHardware.cs`) –
  CPU-csomag és legmelegebb mag, CPU-fogyasztás és -feszültség, GPU hő / hotspot / VRAM-hő, terhelés, videómemória,
  órajelek, fogyasztás, ventilátor; alaplap hőmérsékletei (chipset, VRM…), ventilátorok (a valaha forgók), lemezek és
  memóriamodulok hőmérséklete, NVMe tartalék / elhasználtság. A 0.9.6 a **PawnIO** meghajtót használja (nem csomagol ki
  sajátot): enélkül csak a GPU és a lemezek értékei jönnek. Ha a szerver kéri (alapból igen), az agent wingettel telepíti
  (`namazso.PawnIO`, naponta legfeljebb egy próba), utána újranyitja az érzékelőket; `hw.driver` mutatja, fent van-e;
- **portváltás követése (1.5.0):** ha a szerver aláírt válaszában új agent-port jön, az új porton lekéri a szerver
  kulcsát, és csak akkor vált, ha az a nálunk levő (vagy láncon át elérhető) kulcs; sikertelenül 30 percig a régin marad.
- **1.5.6:** a figyelt szolgáltatások gyors ellenőrzése (alap 5 mp, `ServiceController`) – leálláskor azonnal jelentkezik
  (`AgentStatus.ServiceStates`); másolás külső (USB / cserélhető) meghajtóra naplózva (`AgentUsb.cs`, FileSystemWatcher,
  csatlakoztatás / leválasztás, írt / átnevezett / törölt fájl, a bejelentkezett felhasználó; **alapból KI**, GDPR);
  védelmi állapot (vírusvédelem, valós idejű védelem, definíciók kora, tűzfal-profilok, BitLocker – WMI); a külső gyártók
  driverei és a firmware (BIOS, lemezek) óránként olvasva, változáskor küldve (`AgentStatus.Components`, a CVE-kereséshez);
  a programfrissítések és a Windows Update keresési időköze a szerverről állítható. Az új mezők csak `StatusVersion ≥ 4`-es
  szervernek mennek (régi szervernél az aláírás változatlan).
- **1.5.7 – tálca-ikon** (`AgentTray.cs`, `AgentTrayUi.cs`, `AgentTrayHost.cs`; a Fleet webfelületének sötét stílusában):
  `DevToolboxAgent.exe tray` a felhasználó munkamenetében – a szolgáltatás indítja (WTSQueryUserToken + CreateProcessAsUser,
  konzolablak nélkül; bejelentkezéskor és percenként, ha a szerver engedi: `MetricConfig.Tray`). A szolgáltatás named pipe-ja
  (`DevToolboxAgent-Tray`): jog SID-del (hitelesített felhasználók; NETWORK tiltva), a kérő a pipe-kliens azonosításából,
  felhasználónként 40 kérés / perc; a tálca csak a 0. munkamenetben futó kiszolgálóval beszél (pipe-foglalás ellen). A szerver
  felé az agent aláírt csatornáján (`/api/user`, `AgentUserRequest`, a válasz aláírt és a nonce-hoz kötött). Jóváhagyott
  programok telepítése, program kérése indoklással, kéréseim (értesítés változáskor), „Belépés adminként” (kihívás–válasz:
  `TrayLogin`, a jelszó nem megy át; 15 perces, gépre kötött jegy) → bármely program telepítése, a gép kéréseinek döntése.
  Önfrissítés, telepítés és eltávolítás előtt a tálca-programok leállnak (`AgentTrayHost.StopAll`).

### 2.3 FleetServer (a Fleet telephelyi szerepe) – 1.5.8

| | |
|---|---|
| Szolgáltatás | `DevToolboxFleetServer`, LocalSystem |
| Program | `C:\Program Files\DevToolbox Fleet\FleetServer.exe` (a `DevToolboxAgent.exe` mellette – ebből készül a telepítőcsomag és az önfrissítés) |
| Portok | agent-port **8443**, web-port **8080** (HTTP, belső háló) |
| Adatok | `C:\ProgramData\DevToolbox-Fleet\` (csak SYSTEM + rendszergazdák) |

Adatmappa: `server-config.json`, `fleet.json` (gépek, tokenek), `users.json`, `server-keystore.json`
(+ `.pem` tükör), `metrics.db` (SQLite idősor), `alerts.json`, `alerts-offline.json`, `version.txt`,
`backup\` (utolsó 10 mentés), `inbox\`, `hub-client-*.pem` (Központ-kapcsolat). **1.5.4-től a naplók** (`fleet-server.log`,
`audit.log`, `alerts.log` a forgatott párjukkal) a `logs\` almappában; a naplók, a mentések és a `metrics.db` helye a
Beállítások → Tárolás részben áthelyezhető (a kulcsok és a beállítások az adatmappában maradnak).

**Parancsok:** dupla kattintás / `setup` – grafikus telepítő · `install` (frissítésre is) · `uninstall` ·
`reinstall` (setup nullázása, kulcsok maradnak) · `reinstall --clean` (TORLES beírása + mentés) ·
`run` · `status` · `token` · `fingerprint`.

**Webfelület** (oldalsáv, 1.4.6-tól összecsukható ikonsávvá): Gépek · Új agent · Csoportok · Programok · Szabályok ·
Monitorozás (1.5.0) · Riasztások · Jelentések · Beállítások · Felhasználók · Biztonság – 1.4.6-tól mind teljes oldalon nyílik (nem ablakban).

- Új agent: ZIP (agent + előtöltött varázsló-adat + `TELEPIT.cmd`), rövid életű, gépszám-korlátos token;
- gépenként: frissíthető programok, telepítés/eltávolítás, Windows Update, újraindítás, csoportok, Monitorozás;
- **Monitorozás (1.4.4): teljes oldalon**, bal oldalon kategóriamenü (Áttekintés, CPU, Memória, Lemez, Lemez-I/O,
  Hálózat, …); az Áttekintésben csempék (magok, szálak, memória, lemezméret, futásidő, fő értékek) és a fő grafikonok;
  nagy grafikonok min/átlag/max értékkel, egérrel leolvasható ponttal. A modális ablakok sosem nagyobbak a képernyőnél;
- **Programtár / saját repó (1.4.4): teljes oldalon** – Saját repó · Döntésre vár · Teljes winget-tár (a Microsoft
  hivatalos forrása, ~15 ezer program, naponta letöltve: `source2.msix` → `Public/index.db`, tartalék: a gép
  winget-forrása) · Elutasított · Gépeken talált · Alkalmazáscsomagok. A winget-tárba az előző letöltés óta
  újonnan került programok döntésre várnak (az első letöltés az alap); az elutasított nem kerül a repóba és nem
  kérdezi újra (visszavehető). Minden döntés azonnal mentődik; a gyorsítótár `winget-katalogus.cache`;
- gépcsoportok csoportos műveletekkel;
- automatikus szabályok karbantartási ablakkal (alapból nincs);
- riasztási küszöbök (alapból semmi nem riaszt);
- **kiesés-riasztás (1.4.3):** ha egy gép N perce nem jelentkezett – a felhasználó döntése szerint **alapból be,
  15 perc** (0 = ki, legalább 10); csoportszűrővel (pl. csak a „Szerverek”); percenként fut, a jelzett kiesés
  újraindítás után is megmarad (`alerts-offline.json`), induláskor a küszöbnyi ideig türelmi idő;
- **CSV-jelentések (1.4.3):** géplista, telepített programok, elérhető frissítések, riasztásnapló, napi
  metrika-összesítő – Excelre szabva (pontosvessző, UTF-8 BOM, tizedesvessző, képlet-befecskendezés ellen);
- szerepkörök: Megfigyelő / Kezelő / Admin; a fő admin védett;
- Beállítások: token-alapértékek, agentek automatikus frissítése, Központ-kapcsolat, FleetServer frissítés feltöltése;
- Biztonság: ujjlenyomat, kulcscsere, műveleti napló.

**1.5.8 újdonságai (szerver) – mérési adatok napi fájlokban:**
- `MetricStore`: a mérések napi fájlokba kerülnek (`metrics-napok\metrics-ÉÉÉÉ-HH-NN.db`, UTC-nap): a nap a vége után 48 órával
  LEZÁRUL, és a sorai a `metrics.db`-ből átköltöznek oda (egy példány). A lekérdezések (grafikon, 5 perces átlagok a
  Központba, napi CSV) a napi fájlokból és a `metrics.db`-ből együtt dolgoznak; a részletes idő után a napi fájl órás átlagra
  ritkul (`meta.hourly`). Első induláskor a régi `metrics.db` napjai körönként 7-esével költöznek (10 percenként).
- A megőrzést a Központ állíthatja (`HubCheckinReply.MetricKeepDays`, alap 30 nap): utána a lezárt napi fájl feltöltődik
  (`POST /hub/metric-file`: gzip, a telephely kulcsával aláírt `MetricFileHeader` – nap, SHA-256, méret, nonce), és csak a
  Központ aláírt, ehhez a kéréshez kötött nyugtája után törlődik; csak akkor, ha a nap 5 perces átlagai már fent vannak
  (`HubMetricsFromTs` túl van rajta). Ha a Központ nem érhető el, a fájl megmarad (legfeljebb a megőrzés + 90 napig).

**1.5.7 újdonságai (szerver):**
- **Programkérések** (új menüpont; `Server/ProgramRequests.cs`, `program-kerelmek.json`): a tálcáról kért programok
  jóváhagyási sora (kezelő dönt); jóváhagyáskor InstallApp feladat, a feladat eredménye zárja le a kérést. Gépenként
  legfeljebb 10 függő / 30 napi kérés; az elutasított (letiltott) program nem kérhető. `Server/TrayApi.cs`: a tálca kérései.
- **Tálca-ikon** kapcsoló a „Mit mérjen?”-ben (`TrayEnabled`, alapból BE; a Központ nem írja felül).
- **A Központ műveletei** (`Server/SiteOps.cs`, `HubCheckinReply.SiteOps`): a helyi küszöb-szabályok mentése / törlése és a
  programkérések döntése a Központból – ugyanaz az ellenőrzés, mint a webfelületen, a műveleti naplóba „Központ: <felhasználó>”.
- **Naplók a Központba** (`Server/LogShip.cs`): az audit-, riasztás-, SNMP- és szervernapló új sorai betárcsázáskor
  (naplónként ≤256 KB), bájt-mutatóval (`hub-naplo.json`; forgatáskor a forgatott párra vált – `LogFolder.OnRotate`), a
  mutató csak a Központ visszaigazolása után lép. Az `usb.log` nem megy fel.

**1.5.6 újdonságai (szerver; a felület a Központtal közös):**
- **Felület:** „Fleet Server” / „Fleet HUB” név; oldalon belüli megerősítő / szövegbekérő ablak a böngésző confirm() /
  prompt() helyett (`WebUi.DialogJs`); haladásjelző a feltöltésekhez (MB, MB/s, hátralévő idő) és a hosszabb műveletekhez.
- **Monitorozás:** % grafikon 0–100, GB 0-tól a lemez méretéig; Lemez-állapot (SMART) lemezenként táblázatban, kiemelt
  élettartammal, grafikon csak az élettartamhoz. Szabály-szerkesztő: „Gyakori szabályok” (teljes CPU %, bármelyik mag,
  memória, bármelyik lemez % / szabad GB – HDD és SSD, hő, SSD-élettartam, hálózat Mbit/s, kártya %) és mértékegység-választó
  (KB/s … Gbit/s, GB / TB) – a küszöb a mérés saját egységében tárolódik (`DisplayUnit` csak megjelenítés).
- **Szolgáltatás-figyelés 5 mp-enként:** leállás → azonnali riasztás (`AlertStore.ServiceRuleId`), újraindulás → megszűnés;
  a Központba is (`SiteSummary.ServiceEvents`). **USB-napló** (`logs\usb.log`, gépenként és összesen, kezelőnek).
- **Eszköz-összegzés** (`Server/DeviceScore.cs`): 0–100 pont gépenként – Biztonság (Windows- és programfrissítések,
  vírusvédelem, tűzfal, BitLocker, jelszó-próbálkozás, CVE, agent-verzió) és Állapot (jelentkezés, lemez-telítettség, SMART,
  SSD-élettartam, hibás szektor, hő, újraindítás, aktív riasztás, memória), tételes levonással; a gép-panelen és a listában.
- **Sérülékenységek (CVE):** a Központ párosítása gépenként (`Server/Cve.cs`, `cve.json`) – a gép-panel „Sérülékenységek”
  oldala (CVSS, KEV, EPSS, érintett program / driver / BIOS, javított verzió), a pontszámba is beszámít.
- **Programtár:** „Egyéb” helyett bővebb kategória-szabályok, és a repóban kézzel választható kategória (`CatalogItem.Category`).
- A frissítés-lista újraellenőrzése beállítható (programok: perc, Windows Update: óra) a „Mit mérjen?”-ben.

**1.5.5 újdonságai (szerver):**
- **Internetkapcsolat figyelése** (Riasztások oldal, kezelő): a megadott IP-címek sorban pingelve (`Server/NetWatch.cs`, a .NET
  Ping-je – nem ping.exe), az első válasz = van internet; mind néma → 30 mp múlva újra, utána kiesés-riasztás
  (`AlertStore.InternetRuleId`, perzisztens), kiesés alatt percenként; kiesések listája (`internet-figyeles.json`), főoldali csempe.
- **SNMP-trap fogadás** (új SNMP menüpont; beállítás: admin; alapból KI): v1, v2c (trap + inform nyugtával), v3 USM
  (hitelesítés MD5 / SHA-1 / SHA-224…512, titkosítás DES / 3DES / AES-128/192/256 + Cisco-féle „c” bővítés; v3 inform:
  felderítés, időszinkron-Report, Response), saját függőség nélküli kód (`Server/Snmp.cs`, `SnmpReceiver.cs`). UDP-port (162),
  csak a megengedett (alapból belső) címekről – a tűzfalszabály is (`DevToolbox Fleet - SNMP`); forrásonkénti terheléskorlát,
  ≤8 KB csomag, visszajátszás-szűrés; jelszavak DPAPI-val, a közösségnév csak adminnak látszik. A trapek a naplómappa
  `snmp.log`-jába; a riasztást adók (alap: coldStart, warmStart, linkDown) a riasztásnaplóba és a Központba.

**1.5.4 újdonságai (szerver és Központ):**
- **Programtár:** minden programnál fejlesztő (a winget-azonosító első tagja) és kategória (a winget-címkékből, saját
  szabálylistával: Játék, Böngésző, Futtatókörnyezet, Biztonság, Pénzügy és könyvelés, Távelérés, Irodai, MI, Kommunikáció,
  Grafika, Multimédia, Fejlesztői eszköz, Hálózat, Rendszereszköz, Egyéb); kategória-címkék darabszámmal, fejlesztő-szűrés.
  A csomag-szerkesztő és a telepítés-választó kategóriánként csoportosít.
- **Döntésre vár: csak a valóban új programok** – forrásváltásnál (helyi winget-index ↔ CDN) vagy gyanúsan nagy eltérésnél
  (`FloodLimit`: ezer, ill. a tár 5%-a fölött) új alap; 30 nap után és a tárból eltűnve kikerül; a korábbi tömeg kitakarítva.
- **Automatikus csoportok** (`AutoGroups`): Szerverek / Kliensek, Windows-verzió, Virtuális / Fizikai gépek – a gép
  adataiból, kézzel nem módosítható; minden hatókörben (szabály, küszöb, kiesés-figyelés, feladat). Kikapcsolható.
  A gépek listája csoportosítható (típus, Windows-verzió, hardver, csoport).
- **Tárolás** (Beállítások, admin): a naplók, a mentések és a mérési adatbázis helye (helyi, új vagy üres mappa; jogai
  SYSTEM + rendszergazdák), a naplóméret; mentéskor minden átköltözik (`Storage.cs`, `LogFolder`, `MetricStore.Relocate`).

**1.5.0 újdonságai (szerver):**
- **Monitorozás menüpont** (küszöb-szabályok külön oldalon): lista, új szabály metrika-választóval (a gépek mért
  kulcsai + „bármelyik” minták, pl. `disk.*.usedpct` = bármelyik lemez, `hw.gpu*.temp`; mintánál kulcsonként külön
  riasztás), hatókör: minden gép / gépcsoport / egy gép; be-/kikapcsolás; „A Központtól” lap (csak olvasható); „Mit mérjen?”
  lap az összes kapcsolóval, ping-célokkal, gyors szolgáltatás-gombokkal (MSSQL, IIS, DHCP, DNS, AD…). A Riasztások menüben az
  aktív lista, a kiesés-riasztás és a napló maradt. Szabály módosítása / törlése a régi állapotot is törli.
- **Csoportok oldal:** csoport létrehozása (üres is – `ServerConfig.Groups`), tagok kijelölése, átnevezés (az automatikus
  szabályok, a kiesés-figyelés és a küszöbök is követik), törlés (a rá szűkített szabályok kikapcsolnak – nem tágulnak
  minden gépre); csoportos műveletek. Közös kód: `Server/Groups.cs` (`GroupOps`), a Központ műveletei is ezt hívják.
- **Portok a webről** (Beállítások, admin): agent-port váltáskor a régi port tovább figyel (`OldAgentPort`, a tűzfal és az
  URL-foglalás mindkettőre), az agentek az aláírt válaszban kapják az újat; a régi port bezárul, ha minden aktív (3 napon
  belül látott) gép átállt, vagy letelt a türelmi idő (alap 30 nap), vagy kézzel; a web-port azonnal vált, a böngésző
  átirányul. Újraindítás nem kell (a futó figyelőhöz kerül / abból kerül ki az előtag).
- **A Központ a programtár mestere:** a Központ repója, csomagjai és elutasítottjai a helyiek mellett, csak olvashatóként
  (állapot `hubrepo` / `hubrejected`), telepíthetők; a Központ által eldöntöttek kikerülnek a „döntésre vár” listából.
- A Központ küszöb-szabályai (a leküldésre jelöltek) a helyiek mellett futnak, a Központ mérés-kérése a helyivel
  összefésülve (amit bármelyik kér, azt méri; a mintavétel a sűrűbb) megy az agentekhez.

**1.3.0 újdonságai (szerver):** beépített agent (nem kell mellé `DevToolboxAgent.exe`; mellé tett ÚJABB verzió
felülírja); `diag` parancs; frissítés cmd nélkül (`apply-update`, visszaállítással); percenkénti minták
időbélyeggel, egy tranzakcióban, küszöb mintánként; „Mit mérjen?”: hálózat, lemez-I/O, mintavételi sűrűség;
grafikon legfeljebb ~720 ponttal; a Központba 5 perces átlagok és beléptetők mennek fel, a Központ szabályai a
helyi szabályok mellett futnak (csak olvasható listában).

### 2.4 FleetHub / Központ (a Fleet Központ szerepe) – 1.5.8

| | |
|---|---|
| Szolgáltatás | `DevToolboxFleetHub`, LocalSystem |
| Program | `C:\Program Files\DevToolbox Fleet Hub\FleetHub.exe` |
| Port | **8090** – egyetlen port: `/hub/…` telephely-API, `/web` admin |
| Elérés | NGINX Proxy Manager (másik gépen) → `fleet.poltoli.hu`, Let's Encrypt; Cloudflare **DNS only** |
| Adatok | `C:\ProgramData\DevToolbox-Fleet-Hub\` – `hub.json`, `hub-keystore.json`, `users.json`, `backup\`, `caddy\NPM-beallitas.txt`; 1.5.4-től a naplók (`fleet-hub.log`, `audit.log`, `alerts.log`) a `logs\` almappában – a naplók, mentések és a `metrics.db` helye a Beállításokban állítható |

**Parancsok:** dupla kattintás / `setup` · `install` (frissítésre is) · `uninstall` · `reinstall` · `run` · `diag`.

- a `/hub/` mindig nyitva (aláírás védi); az admin csak belső IP-ről – kívülről válasz nélkül bont,
  belső IP-t nem árul el; opcionálisan az NPM is eldobhatja (`return 444` blokk, location nélkül);
- tűzfal minden induláskor: minden profilon, de csak belső IP-tartományokból;
- telephelyenként állítható betárcsázási gyakoriság; feladat leküldése a telephely gépeire;
- felhasználók szerepkörrel és telephely-hatókörrel; kulcscsere; műveleti napló; webes önfrissítés.

**1.3.0 – a Központ tudása a szerverével egy szinten, a telephelyeken át:**
- gépenként „Kezelés”: frissíthető programok, Windows Update (egyenként / összes / keresés), telepítés
  (katalógusból is), eltávolítás, újraindítás késleltetéssel; telephelyenként csoportos műveletek;
- monitorozás: a telephely 5 perces átlagai (6 óra – 90 nap grafikon), saját küszöbök és riasztásnapló,
  megőrzés állítható (alap: 5 perces átlag 7 napig, órás átlag 90 napig);
- programkatalógus és alapcsomag; automatikus szabályok telephely- és csoport-hatókörrel (a telephelyen futnak);
- új agent a Központból (a telephely készíti a beléptetőt, a ZIP a beépített agentből);
- `fleet-hub.log`, `alerts.log`, `metrics.db` az adatmappában.

**1.4.3 – értesítés és jelentések:**
- **kiesés-riasztás:** telephely nem tárcsázott be (küszöb legalább a betárcsázási időköz háromszorosa) és – ha be
  van kapcsolva – a telephely gépe nem jelentkezett; alapból be, 15 perc; csoportszűrő; a telephely órájának
  eltérését a Központ kiszámolja (nincs téves riasztás); kiesett telephely gépeinek állapota „befagy”;
- **e-mail értesítés – CSAK a Központ küld:** Beállítások → E-mail értesítés (admin): SMTP-szerver, port,
  STARTTLS / SSL (465) / titkosítás nélkül (csak jelszó nélküli belső relay-hez), felhasználó, jelszó, feladó,
  címzettek, teszt e-mail. Riasztás keletkezésekor és megszűnésekor, kiesésnél; a percen belüli események egy
  levélben. A jelszó DPAPI-val titkosítva (a SYSTEM kulcsával), a felületre és a naplóba nem kerül;
- **CSV-jelentések** a szerverével azonos formában, „Telephely” oszloppal, a felhasználó hatókörére szűrve.

**1.5.8 – mérési adatok megőrzése (a felhasználó kérése: ne legyen kettő ugyanabból):**
- Beállítások → Adatmegőrzés: a telephelyek szerverein (alap 30 nap), a Központban olvasható (alap 90), archívum összesen
  (alap 180), 5 perces átlag. A telephelyek a következő betárcsázáskor átveszik.
- `Hub/HubMetricArchive.cs`: a beérkező napi fájl ellenőrzése (aláírás, friss nonce, méret, SHA-256, csak lezárt nap,
  érvényes SQLite, minden sor arra a napra), tárolás telephelyenként (`metrics-telephelyek\<id>\`), és a Központ saját 5
  perces másolata arra a napra a telephely gépeire törlődik. A gép grafikonja a saját adatból és a feltöltött fájlokból
  együtt rajzol. A „olvasható” idő után ZIP-archívum, az „összesen” után törlés (a Központ saját napi fájljaira is).

**1.5.7 – a telephely szabályai, központi napló, programkérések:**
- **A telephely saját szabályai** (Monitorozás → „Telephelyek szabályai”, a telephely adatlapjáról is): lista, új, szerkesztés,
  ki-/bekapcsolás, törlés → `HubSite.PendingSiteOps` → a következő betárcsázáskor megy le (csak 1.5.7+ telephelynek:
  `SiteSummary.SupportsSiteOps`); a felület azonnal az új állapotot mutatja (`SiteSummary.LocalRules`).
- **Központi napló** (`Hub/HubSiteLogs.cs`): a telephelyek naplói a Központ naplómappájában (`logs\telephelyek\<id>\`),
  10 MB-onként forgatva, naplónként 5 fájl; a telephely adatlapján „Naplók” (admin): napló-választó, szűrés, legújabb elöl.
  A naplómappa áthelyezésekor a `telephelyek\` is költözik.
- **Programkérések** menüpont: a látható telephelyek kérései, jóváhagyás / elutasítás (megjegyzéssel) a telephelyre leküldve.

**1.5.6 – sérülékenység-keresés, eszköz-összegzés, szolgáltatás-leállás:**
- **Sérülékenységek menü** (`Hub/HubCve.cs`): naponta egyszer (alap 3 órától) a telephelyek gépeinek programjai, driverei
  (NVIDIA – a Windows-verzióból a gyártói számra váltva –, Intel grafika) és BIOS-a (Dell / HP / Lenovo típusnévből) az
  **NVD** API 2.0-val párosítva, kiegészítve a **CISA KEV**-listával és az **EPSS**-szel. Programnév → CPE: beépített
  táblázat, utána az NVD CPE-szótárában szigorú névegyezéssel (30 napig tárolva); csak az NVD verziótartományai és pontos
  verziói számítanak (a „minden verzió”, a béta és a más platformra szóló bejegyzés kimarad). Gyorsítótár: `cve\`
  (termékenként, utána csak a módosultak), a párosítás félóránként hálózat nélkül is frissül. Haladásjelző, „Ellenőrzés most”
  (kezelő), NVD API-kulcs (admin; DPAPI, a felület nem kapja vissza; kulcs nélkül 5 kérés / 30 mp). Az eredmény a betárcsázás
  aláírt válaszában megy le (`HubCheckinReply.Cve`, lenyomattal – csak változáskor). A Microsoft saját termékeit a
  Windows-frissítések listája fedi le.
- A gép oldalán az **eszköz-összegzés** (a telephely számolja: `SiteAgent.Score`) és a „Sérülékenységek” nézet; a gépek
  listájában pontszám-jelvény.
- A telephely gépeinek **szolgáltatás-leállása** a riasztásnaplóba (→ e-mail, Janus; újrainduláskor megszűnik).

**1.5.5 – Janus-kapcsolat megerősítve, a telephely internet- és SNMP-eseményei:**
- Janus: átirányítást nem követ, csak TLS 1.2 / 1.3, válasz ≤64 KB, siker csak a Janus `{"ok":true}` válaszára (proxy /
  captive portal nem nyeli el a riasztást), óraeltérés-jelzés a próbánál; a titok csak a Janus formátumában, egy kulcs csak
  egy telephelynél, a címben nincs felhasználónév / jelszó.
- A telephely lezárult internet-kiesései (`SiteSummary.Internet`) és riasztást adó SNMP-trapjei (`SiteSummary.SnmpEvents`)
  egyszer (`HubSite.LastOutageStartUtc` / `LastSnmpEventId`) a Központ riasztásnaplójába → e-mail, Janus (a kiesés „info”,
  jegy nélkül); a telephely adatlapján internet-állapot és SNMP-riasztások.

**1.5.0 – szabályok, mérés, csoportok, programtár a telephelyekre:**
- **Monitorozás menüpont** a szerverével azonos szabály-oldallal; hatókör: telephelyek (több is) + gépcsoport / gép;
  „Leküldés a telephelyekre is” (alapból be): a szabály a következő betárcsázáskor lemegy, és a telephelyen percenkénti
  méréseken fut (ott csak olvasható); a Központ maga az 5 perces átlagokon értékeli (e-mail innen). Telephely-hatókörű
  szabályt az is menthet, aki minden érintett telephelyet lát; minden telephelyre szólót csak admin.
- **Mit mérjen? a telephelyeknek** (admin): a Központ mérés-kérése a kijelölt (vagy minden) telephelyre; kikapcsolva
  „semmit nem kér” megy le, így a korábbi kérése megszűnik.
- **Csoportok** telephelyenként: a telephely összesítőjéből (az üres csoportok is felmennek); létrehozás / tagok /
  átnevezés / törlés → `HubSite.PendingGroupOps`, a következő betárcsázáskor megy le (csak 1.5.0+ telephelynek); a
  felület azonnal az új állapotot mutatja.
- **Programtár a telephelyekre:** a repó + csomagok + elutasítottak lenyomata (`HubCatalog.Hash`) csak változáskor
  megy le (a telephely a nála levő lenyomatot küldi fel).
- A telephely adatlapján az agent-port és a folyamatban levő portváltás.

**1.4.4:** a Monitorozás (a telephely 5 perces átlagai, 90 napig) és a Programtár (saját repó a teljes winget-tárból,
döntésre vár / elutasított) a szerverével azonos teljes oldalas nézetben; a programtár-döntés a Központban csak adminé.
Javítva: a Központ felületén a gép-kezelés és a küszöb gombjai a szerepkör hiánya miatt rejtve maradtak (1.4.3).

---

## 3. Biztonsági modell (közös)

- **Kulcsok:** ECDSA; 1.4.5-től az új kulcsok **P-384 / SHA-384** (a hash a kulcsmérethez igazodik, a P-256 marad
  SHA-256 – a régi kliensek kompatibilisek). Jelszavak: PBKDF2-SHA256, 210 000 iteráció.
- **Automatikus kulcscsere (1.4.5, alapból hetente, állítható, 0 = ki):**
  - *Agentek:* új kulcspár; a kérést a jelenlegi kulcs ÉS az új is aláírja (birtoklás-bizonyítás), friss/egyszeri
    (időbélyeg + nonce), az új kulcs más gépnél nem lehet használatban. Az agent csak a szerver aláírt, a kéréshez
    kötött visszaigazolására vált. A szerver a régi kulcsot a megerősítésig (az első új kulccsal aláírt kérésig)
    elfogadja – elveszett válasz nem zárhatja ki a gépet; egy soha nem használt új kulcsot a következő próba elvet.
  - *Szerver és Központ:* láncolt csere (a régi kulcs aláírja az újat), a lánc 60 szemet őriz (~14 hónap offline);
    erős (P-384) kulcs csak ha minden agent / telephely 1.4.5+, különben P-256 – a felület megmutatja, ki tartja vissza.
  - *Kulcslista:* Biztonság oldal – saját kulcs előzménnyel (a láncból kiesett is), gépek/telephelyek kulcsai
    előzménnyel és állapottal, a kiadott tokenek (lenyomat, állapot, letiltás), műveleti napló.
- **Mindkét irány aláírt:** agent → szerver, szerver → agent, telephely → Központ, Központ → telephely.
  A válasz a kérés nonce-ához kötött (nem játszható vissza), időbélyeg ±5 perc.
- **Kulcstár (v2):** verziózott, a régi `.pem` automatikusan átkerül. **Kulcscsere** újraregisztrálás nélkül:
  az új kulcsot a régi aláírja (lánc, max. 6 szem), régi privát kulcs nem marad; régi telepítőcsomag is működik.
- **Tokenek:** rövid életű, gépszám-korlátos; a szerver csak a lenyomatukat tárolja.
- **Belépés-korlátozás:** IP-nként 5 hiba / 15 perc → 15 perc zárolás. **Műveleti napló** jelszó nélkül.
- **Mentés:** verzióváltáskor induláskor, kulcscsere és `--clean` előtt (`backup\`, utolsó 10).
- **Adatmappák:** csak SYSTEM és rendszergazdák (SID alapján – nyelvfüggetlen).
- **Programazonosítók** (winget/ARP) ellenőrzése a szerveren és az agenten is (parancs-injektálás ellen).
- **Kulcsfájlok** (`*.pem`, kulcstár): csak SYSTEM, rendszergazdák és a létrehozó fiók (élesben maga a SYSTEM).
- **SMTP-jelszó** (Központ): DPAPI-val titkosítva, csak a Központ szolgáltatásfiókja fejti vissza; jelszó
  titkosítatlan SMTP-kapcsolaton nem adható meg; a címek sortörést nem tartalmazhatnak (fejléc-befecskendezés ellen).
- **CSV-jelentések:** a gépekről jövő szöveg (programnév stb.) `= + - @` kezdettel aposztrófot kap (Excel-képlet ellen).
- **Portváltás (1.5.0):** az új agent-port az aláírt válaszban megy le; az agent az új porton a szerver kulcsát
  ellenőrzi (ugyanaz vagy láncon át elérhető), csak utána vált – egy ott figyelő idegen szolgáltatásra nem állhat át.
- **Központ → telephely (1.5.0):** a programtár, a küszöb-szabályok, a mérés-kérés és a csoport-műveletek az aláírt
  válaszban jönnek; a telephelyen ellenőrizve (érvényes programazonosító, metrika-kulcs, csoportnév, saját gép).
- **Hardver-érzékelő meghajtó (1.5.0):** a PawnIO aláírt kernel-meghajtó, a winget hivatalos forrásából, csak ha a szerver
  kéri (a „Mit mérjen?”-ben kikapcsolható); az agent semmit nem tölt le saját címről.

---

## 4. Fejlesztési terv – állapot

### Kész (15 pontos terv)

| # | Pont | Állapot | Kiadás |
|---|---|---|---|
| 1 | Aláírt szerverválasz az agent felé | ✅ kész | FleetServer 1.2.1 · Agent 1.1.1 |
| 2 | Belépés-korlátozás (FleetServer, Központ) | ✅ kész | 1.2.1 · Központ 1.1.2 |
| 3 | Kulcsok: mentés, verziózott kulcstár, kulcscsere újraregisztrálás nélkül, `--clean` védelem | ✅ kész | 1.2.1 · 1.1.1 · 1.1.2 |
| 4 | Műveleti napló | ✅ kész | 1.2.1 · 1.1.2 |
| 5 | Agent önfrissítés (aláírt lenyomat, visszaállítás) | ✅ kész | 1.2.2 · Agent 1.1.2 |
| 6 | Grafikus telepítővarázsló (agent előtöltve, FleetServer, Központ) | ✅ kész | 1.2.2 · 1.1.2 · 1.1.3 |
| 7 | Oldalsávos menü (FleetServer, Központ) | ✅ kész | 1.2.2 · 1.1.3 |
| 8 | Programtelepítés / -eltávolítás, katalógus, alapcsomag | ✅ kész | 1.2.3 · Agent 1.1.3 |
| 9 | Windows Update figyelés és telepítés (újraindítás csak döntésre) | ✅ kész | 1.2.3 · 1.1.3 |
| 10 | Automatikus frissítési szabály karbantartási ablakkal | ✅ kész | 1.2.3 |
| 11 | Gépcsoportok csoportos műveletekkel | ✅ kész | 1.2.3 |

### Tervezett (D – monitorozás)

| # | Pont | Állapot |
|---|---|---|
| 12 | Kiesés-riasztás: jelez, ha egy gép adott ideje nem jelentkezett | ✅ kész – Fleet 1.4.3 (gép, telephely; alapból 15 perc) |
| 13 | Aktív értesítés (e-mail) riasztáskor | ✅ kész – Fleet 1.4.3 (csak a Központ küld) |
| 14 | Sűrűbb mintavétel (pl. percenként, a jelentkezéstől függetlenül) | ✅ kész – Agent 1.2.0 · Fleet 1.3.0 |
| 15 | Jelentések: gépek, frissítések, riasztások exportja (CSV) | ✅ kész – Fleet 1.4.3 (+ programok, metrika-összesítő) |

### A 15 pontos terv előtt megvalósult

- Központ (FleetHub) a telephelyek betárcsázásával, telephelyenként állítható gyakorisággal.
- NGINX Proxy Manager mód (egy port, `/hub/` útvonal), Cloudflare DNS only, admin csak belső hálóról.
- ZIP-es agent-telepítőcsomag, rövid életű token; FleetServer és Központ webes frissítő-feltöltése.
- Szerepkörös felhasználókezelés mindkét felületen, telephely-hatókör a Központon.
- Monitorozás: metrikagyűjtés, SQLite idősor megőrzéssel (7 nap részletes, 90 nap órás átlag), grafikonok, riasztási küszöbök.
- Diagnosztika: `DevToolboxAgent.exe diag`, `FleetHub.exe diag`; érthető hibaüzenetek proxy- és kapcsolati hibákra.

### Javasolt, még nem ütemezett

- ~~A Központ felé a metrikák/riasztások összesítése~~ – kész (Fleet 1.3.0).
- ~~A Központ felületén a C) gépkezelési funkciók telephelyeken át~~ – kész (Fleet 1.3.0).
- ~~Az agent saját kulcsának cseréje~~ – kész (1.4.5, hetente automatikusan). Még nyitott: a telephely Központ felé
  használt kulcsának (hub-client) cseréje.
- MSI-csomag (WiX-forrás) GPO/Intune-os tömeges telepítéshez.
- Opcionális TLS a belső FleetServer–agent kapcsolatra (az integritást már most az aláírás védi).

### Javasolt biztonsági fejlesztések – MEGBESZÉLENDŐ (a 2026-10-10-i átnézésből; a felhasználó kérése: előbb átbeszéljük)

| # | Gyenge pont | Javaslat | Érinti |
|---|---|---|---|
| B1 | Az NPM és a Fleet HUB (8090) között a belső hálón sima HTTP: az üzenetek aláírtak (nem hamisíthatók), de ezen a szakaszon olvashatók. | TLS ezen a szakaszon is (HUB-tanúsítvány az NPM felé), vagy az NPM és a HUB elkülönített VLAN-ban / ugyanazon a gépen. | Fleet HUB, NPM |
| B2 | A telephelyi Fleet Server webfelülete HTTP: a belépési jelszó a belső hálón titkosítatlanul megy. | Beépített HTTPS a webfelületre (önaláírt vagy saját tanúsítvány, http.sys tanúsítvány-kötés), a HTTP átirányít. | Fleet Server |
| B3 | Janus: a gép–ügyfél párosítás gépnév alapján megy – két ügyfél azonos nevű gépe („SERVER”) összekeveredhet (rossz ügyfélhez kerülő gép / jegy). | A párosítás csak akkor írja felül a Fleetserver ügyfelét, ha a párosított ügyfél ugyanaz, vagy a kulcs tartalmazza a Fleetservert is. | Janus (külön kiadás) |
| B4 | Janus: nincs forrás-IP korlát a Fleetserver-kulcson. | Kulcsonként engedélyezett forrás-IP (a HUB nyilvános címe), eltérő IP-ről elutasítás. | Janus |
| B5 | Janus: a sikertelen hitelesítés nincs korlátozva. | IP-nkénti korlát (pl. 10 hibás / 15 perc). | Janus |
| B6 | Janus: a válasz nincs aláírva (TLS mellett csak egy hamis tanúsítvánnyal kijátszható). | HMAC-os válasz-aláírás (a kérés nonce-ához kötve), a HUB ellenőrzi. | Janus + Fleet HUB |

### A felhasználó döntései (2026-10-10) – megvalósítva az 1.5.7-ben

- ✅ Agent tálca-ikon és felület (alapból be, kikapcsolható); „Belépés adminként” külön menüben, a jelszó nem megy át.
- ✅ A Server riasztási szabályai a HUB-ról szerkeszthetők (a Server marad az egyetlen író).
- ✅ Központi napló a HUB-on, USB-napló nélkül (a Server méretkorlátos, forgatott naplói a puffer).
- A Microsoft Store-forrás (Lenovo Vantage, MSI Center Pro…) a felhasználó döntése szerint NEM kerül a programtárba.
- Megjegyzés (biztonság): a tálcás belépés a tárolt PBKDF2-kulcsot használja – aki a `users.json`-t megszerzi, a tálcán
  beléphetne a jelszó nélkül; a fájl csak SYSTEM + rendszergazdák számára olvasható (a webes belépést ez nem érinti).

### Valódi Windowson még ellenőrizendő

1.3.0-tól rendszergazdaként ellenőrizendő még: szolgáltatás létrehozás/törlés, tűzfalszabály, URL-foglalás
(NativeWin), újraindítás ütemezése, `apply-update` csere és visszaállítás, a szerepválasztó ablak, az új
WMI-metrikák (hálózat, lemez-I/O) és a percenkénti mintavétel, a Központ új felülete élő telephellyel.

1.5.0-tól valódi gépen ellenőrizendő: a PawnIO telepítése wingettel SYSTEM-ként, és utána a CPU-hőmérséklet; a
`MSFT_StorageReliabilityCounter` és az ATA SMART-attribútumok SYSTEM-ként; a biztonsági napló számlálói; Hyper-V gazdán a
VM-mérések; a portváltás élőben (kettős figyelés, agent-átállás, a régi port lezárása); a web-port váltása és az átirányítás.

1.5.7-től valódi gépen ellenőrizendő: a tálca indulása a felhasználói munkamenetben (konzol-, RDP-, gyors felhasználóváltás),
a sötét címsor Windows 10 / 11-en, az önfrissítés futó tálcával (a tálca leáll, az új verzió újraindítja), a telepítés
jóváhagyás után, a központi napló élő telephellyel.

1.5.6-tól valódi gépen ellenőrizendő: a szolgáltatás-leállás jelzése 5 mp-en belül; az USB-napló (pendrive, USB-s külső
lemez, felhasználónév SYSTEM-ből); `Win32_PnPSignedDriver` / BIOS SYSTEM-ként; a védelmi mérések más vírusirtóval (ESET…);
a Központ első CVE-köre élő telephellyel (időtartam kulcs nélkül, a találatok helyessége néhány ismert programon).

A logika tesztelt, de ezek csak Windowson futnak: WMI-metrikák, a grafikus varázslók felülete, az önfrissítés
tényleges cseréje, winget telepítés/eltávolítás, Windows Update (COM) végrehajtás, a `Response.Abort` bontás
viselkedése http.sys alatt. Ellenőrzés: `DevToolboxAgent.exe diag`, `FleetHub.exe diag`, `agent.log`.

---

## 5. Verziótörténet

### Fleet 1.5.13 · Agent 1.5.13 · DevToolbox 1.5.13 – szabály felküldése a Központba, Tárolás-javítás (2026-10-11)
A felhasználó kérése: a telephelyen létrehozott szabály felküldhető legyen a Központba globális figyelésnek, és onnan
leküldhető a szerverekre; a Tárolás beállítás nem volt menthető.
- **Felküldés (telephely):** `AlertRule.PromoteToken` (új jel minden kérésnél, `AlertStore.Promote`, `/web/api/alert-promote`,
  kezelő), a `SiteAlertRule.PromoteToken` a meglévő `LocalRules` összesítőben megy fel (új mezők a VÉGÉN). A helyi
  szerkesztés megtartja a jelet (`SiteOps.SaveLocalRule`).
- **Átvétel (Központ):** `HubStore.OnPromoteRules` → `PromoteFromSite`: egy kérés (telephely|szabály|jel) csak egyszer
  (`ServerConfig.HubPromotedRules`, max. 2000), törlés után sem jön vissza; ugyanabból a helyi szabályból egy központi.
  Biztonsági alapállás: `Sites=[küldő]`, `PushDown=false` – egy telephely (akár feltört) más telephelyre nem hat; a
  kiterjesztés a Központ kezelőjének döntése (a meglévő Küszöb-szabályok szerkesztő). Idegen gépre szóló szabálynál a gép
  kimarad. A Központ felületéről is: `/web/api/site-rule-promote` (kezelő + a telephelyhez jog). `OriginSite/OriginRuleId`
  szerkesztéskor megmarad.
- **Dupla riasztás ellen:** a leküldött szabály `OriginRuleId`-t csak a küldő telephely kapja; ott az `AlertStore` a
  kiváltott helyi szabályt nem értékeli (`_superseded`, csak bekapcsolt központi szabálynál), az aktív állapota törlődik.
- **Tárolás:** a jelenlegi hely nem esik az „idegen, nem üres” vizsgálat alá; a `metrics-napok` / `metrics-telephelyek`
  almappa a miénk; a három hely nem lehet azonos mappa; a `DataBackup` takarítása csak az `ÉÉÉÉHHNN-ÓÓPPMM-` mappákat törli.
- **Napi mérési fájl feltöltése:** sikertelen (elutasított / megszakadt) feltöltés után az adott nap 1 óráig nem megy újra
  (`HubClient._fileRetryAt`) – eddig egy tartós elutasítás minden betárcsázáskor a teljes fájlt újraküldte.
- Teszt: `unit/Alerts` (felküldés, kiváltás, nincs dupla riasztás, kikapcsolás/törlés után a helyi fut), `unit/Storage`.

### Fleet 1.5.12 · Agent 1.5.12 · DevToolbox 1.5.12 – webfelület telefonon és tableten (2026-10-11)
A felhasználó kérése: a webfelület telefonon és tableten is jól jelenjen meg; minden verzió 1.5.12 (az agent és a DevToolbox
kódja nem változott). A közös `WebUi.Css` a telephely-szerveré és a Központé is, így mindkettő javult.
- **Mérés:** a tesztszerverekkel (`tests/hosts`) valódi böngészőben (Chromium, fej nélkül) 390 / 768 / 1024 / 1440 px
  széles nézetben, a szerver 11 és a Központ 10 nézetén (a Központ telephely- és gépkezelő oldalai álnév-adattal, mert az
  adatfeltöltő e2e Linuxon a DPAPI miatt nem fut). Előtte: telefonon és álló tableten 1603 px széles oldal (a menü egy
  gombsor volt), fekvő tableten és asztalin kilógó gép-adatlap-gombok; utána: 84 nézetből 0 kilógás, JS-hiba nélkül.
- **1000 px alatt:** tapadó felső sáv, ☰ lenyíló menü (`body.nav-open`; menüpontra vagy mellé kattintva bezárul – a
  `toggleNav()` keskeny képernyőn ezt kapcsolja, szélesen továbbra is az oldalsáv összecsukását); a `.page` a sáv alatt.
- **1200 px alatt:** a `.layout` egy oszlop (eddig `minmax(360px)+minmax(400px)` miatt kilógott), gépválasztáskor görgetés az
  adatlapra; a `.page-menu` felül, vízszintesen; a `.upd-table` a saját dobozában görget.
- **Mindenhol:** tördelődő `.panel-head` / `.detail-actions`; az ablak rácssávja `minmax(0,1fr)` (telefonon eddig 560 px
  maradt), `dvh` a mobil böngészősáv miatt; `overflow-wrap:break-word` a tartalmi részeken (a táblázatcellákra NEM
  `anywhere` – az betűnként tördelte az oszlopokat, ezt a mérés mutatta ki).
- **Telefon (760 px alatt):** kisebb margók, két összesítő csempe soronként, kétoszlopos űrlapok egymás alatt.
- **Érintőképernyő:** nagyobb gombok, 16 px-es mezők (az iOS Safari kisebbnél ráközelít).
- A terminál magassága a tapadó sávval és a mobil böngészősávval is számol (`100dvh - var(--term-off)`).
- Teszt-harness: a `FleetHub.Host`-ból is hiányzott a `WireGuard.cs` (1.5.9 óta nem fordult) – pótolva. A
  `check_scripts.js` egy 1.5.11-es kommentben lévő `onclick="fn(…)"` mintát hiányzó függvénynek vett – a komment átírva.
- Ismert, nem ehhez tartozó: az `e2e/Management` „Csoportos műveletek" része elavult (az „alapcsomag" jelölés helyett az
  1.4.0 óta „Alapcsomag" nevű csomag van) – külön javítandó.

### Fleet 1.5.11 · Agent 1.5.11 – biztonsági audit javításai, SNMP-megerősítés, optimalizálás (2026-10-11)
A felhasználó kérése: kódszintű biztonsági audit („penetration test" szemlélettel) és optimalizálás; a javítások. Részletek:
`BIZTONSAGI-AUDIT-1.5.10.md`.
- **XSS (közepes).** Az `esc()` nem kódolta az aposztrófot, és a felület az `onclick="fn('${esc(név)}')"` mintában
  egyszeres idézőjeles JS-sztringbe tette a neveket (szolgáltatásnév, felhasználónév) – egy `'` kitörhetett. Új `jesc()`
  (JS-escape + HTML-escape) a `WebUi.cs`-ben és a `HubWebUi.cs`-ben, mind a 20 ilyen hely átállítva; az `esc()` most az
  aposztrófot is kódolja.
- **winget-argumentum (alacsony).** A `WingetId.IsValid` tiltja a záró backslash-t (`"id"` → eszkébelt idézőjel →
  kapcsoló-injekció). A belső backslash marad: az ARP-azonosítók tartalmazzák (ezt egy meglévő teszt jelezte).
- **SNMP-megerősítés (fuzzing + átnézés).** TimeTicks-túlcsordulás miatt elveszett trapek (fuzzing találta);
  globális terheléskorlát és a forrás-tábla nem ürül ki (hamisított forrásokkal nullázható volt); korlátos,
  ritkán takarított visszajátszás-tábla; kulcs-gyorsítótár csak sikeres HMAC után; hexa-megjelenítés vágása;
  IPv4-tartalék IPv6 nélküli gépen, socket-szivárgás javítva. Új teszt: `tests/unit/SnmpHardening` (3,6 millió
  fuzzolt csomag: 0 generikus hiba).
- **Optimalizálás (O1).** A `HubClient` változás-detektálása `System.HashCode`-dal, ideiglenes nagy stringek nélkül.
- **Teszt-harness.** A `HubClient`-et fordító e2e tesztekből hiányzott a `WireGuard.cs` / `SiteWg.cs` – pótolva.
- A session-cookie `Secure` flagje a Központ előtt az NPM-ben van beállítva (a felhasználó jelezte) – a kódban nem kell.

### Fleet 1.5.10 · Agent 1.5.10 – szolgáltatás-kezelő felület, újraindítás-javítás (2026-10-11)
A felhasználó kérése és döntései.
- **Újraindítás-javítás (fő hiba).** Az ütemezett (késleltetett) újraindítás eddig nem történt meg: a `NativeShutdown.Restart`
  az `InitiateShutdownW`-t `SHUTDOWN_FORCE_SELF` nélkül hívta, és a Win32 API dokumentációja szerint, ha a grace period > 0 és
  nincs `SHUTDOWN_FORCE_SELF`, a hívás `ERROR_INVALID_PARAMETER (87)`-tel elszáll. Ezért működött csak az azonnali (delay=0)
  újraindítás. Javítás: `SHUTDOWN_FORCE_SELF` hozzáadva. A feladat-eredmény naplózása bővült: hibánál a feladat típusa, a
  kilépési kód és a kimenet eleje is az `agent.log`-ba kerül (eddig csak „siker/hiba").
- **Szolgáltatás-kezelő felület** (a terminál helyett, a korábban tervezett „szolgáltatás-restart UI"). Protokoll: három új
  feladat-típus (`ServiceStart/Stop/Restart`, a sor végén, hogy a régi agentek ordinálja ne csússzon el) és `ServiceName`
  validátor. Agent: a meglévő `NativeService.Start/Stop`-ra építve (ServiceController API, nem parancssor). Telephely-szerver:
  a gép-panelen „Szolgáltatások…" gomb, kereshető lista, adminként Indít/Leállít/Újraindít; `/web/api/machine-services` és a
  `/web/api/task` admin-kapuval, auditálva. Központ: a `SiteAgent` kiegészült a `ServiceList`-tel (a telephely a betárcsázáskor
  felküldi, változás-alapú kihagyással, a Components mintájára), a gép-oldalon „Szolgáltatások" fül. Unit teszt: `ServiceControl`.
- **build.cmd + AIO.** A `build.cmd` mostantól mindhárom exét lefordítja (Fleet, Agent, DevToolbox) és összerakja az
  `AIO\Dev-<dátum>.zip` csomagot (3 exe + dokumentumok + forras-zip); a verziót a `Fleet.csproj`-ból olvassa.
- **Teszt-harness javítás.** Az 1.5.9 (WireGuard) óta a `HubStore.cs` a `WgNet` típust használja, de a `HubStore.cs`-t fordító
  unit tesztek projektfájljába nem került be a `WireGuard.cs` – ezek build-hibával elszálltak. Javítva (8 teszt újra fut).
- **Audit:** a metrika-átvitel (Server↔HUB) és az admin terminál átnézve – robusztusak, nincs hiba (lásd `TESZTELESI-TERV-1.5.10.md`).

### Fleet 1.5.9 · Agent 1.5.9 – WireGuard a Server↔HUB között, webes admin terminál, azonnali újraindítás, MB-kijelzés (2026-10-11)
A felhasználó kérése és döntései.
- **WireGuard-alagút a telephelyi szerver és a Központ között** (a hivatalos, aláírt wireguard-nt beágyazva – lásd
  `ThirdParty/wireguard-nt`; UDP 51820, alapból be, magáncímes alagút, alap 10.77.0.0/24). A telephely a WireGuard nyilvános
  kulcsát az aláírt összesítőben küldi, a Központ címet rendel hozzá; **szigorú mód:** ami egyszer az alagúton tárcsázott be,
  csak azon jöhet. Ha az alagút nem áll fel, a Server webfelületén javítható (végpont, kulcscsere, újrakapcsolás – a
  `/hub/wg-key` aláírt útján). Az admin felület az alagút felől nem érhető el. Kulcsok DPAPI-val. Beállítás mindkét oldalon
  (HUB: port, címtartomány, végpont, keepalive, kötelezőség, telephely-lista; Server: állapot, végpont-felülírás, javítás).
- **Webes admin terminál** (a felhasználó kifejezett döntése, a „háttérben nincs cmd/PowerShell” szabály kivétele): csak
  admin, csak a kifejezetten engedélyezett gépen (alapból KI, gépenként), PowerShell LocalSystemként (`-EncodedCommand`,
  120 mp), a parancs és a kimenet az aláírt feladat-/eredmény-csatornán; nyitott terminálnál 2 mp-es betárcsázás. Minden
  parancs a műveleti naplóban. `AgentTaskKind.RunCommand` (a lista végén), `Server/Terminal.cs`, UI `showTerminal`.
- **Azonnali újraindítás** (`AgentTaskKind.RebootNow`): az Újraindítás ablakban az „azonnal” opció (külön megerősítéssel).
- **Méretkijelzés 1024-es alapon** (a felhasználó kérdése nyomán): a feltöltésjelző, a Tárolás, a naplóméret és a központi
  napló MB-ban ugyanannyit mutat, mint a Windows fájlkezelője (1 MB = 1024×1024 bájt).
- Tesztek: 19/19 unit (a WireGuard és a terminál élő/valódi gépes próbája a felhasználónál).

### Fleet 1.5.8 · Agent 1.5.8 – mérési adatok napi fájlokban, megőrzés a Központból, archívum (2026-10-10)
A felhasználó kérése és döntései: a Központban állítható a szerverek megőrzése is (alap: Központ 90 nap, szerver 30 nap); a
Központban a régebbi archívumba kerül, összesen 180 napig; a szerver a régebbit feltölti, utána csak a Központban látszik;
„csak lezárt fájl tölthető fel”; nem lehet kettő ugyanabból. A Központ élő 5 perces átlagai maradnak (a felhasználó döntése).
Az agent kódja nem változott (közös verzió); a DevToolbox 1.5.9 marad.
- Napi mérési fájlok a szerveren és a Központban; feltöltés aláírt fejléccel és lenyomattal, törlés csak a Központ aláírt
  nyugtája után; a Központ a beérkezett napra törli a saját másolatát; olvasható → ZIP-archívum → törlés.
- A méretek a felületen 1024-es alapon (MB, mint a fájlkezelőben – a felhasználó kérdése nyomán).
- Tesztek: új `unit/MetricDays` (lezárás, több fájlos lekérdezés, ritkítás, archívum, feltöltésre tartás, áthelyezés, a
  Központ fogadása: idegen kulcs, rossz lenyomat, le nem zárt nap, más napra eső sor, visszajátszás, másolat törlése); 19/19.

### Fleet 1.5.7 · Agent 1.5.7 – tálca-ikon, programkérések, a telephely szabályai a HUB-ról, központi napló (2026-10-10)
A felhasználó döntése: „rákövetkező verzió” (1.5.7). A DevToolbox nem változott – 1.5.9 marad (a közös `Security.cs`-ben
csak általa nem használt részek).
- **Agent tálca-ikon** a Fleet stílusában (a felhasználó kérése): jóváhagyott programok telepítése, program kérése, kéréseim,
  értesítések; „Belépés adminként” (kihívás–válasz, a jelszó nem megy át) → bármely program, a kérések döntése. Alapból BE,
  a „Mit mérjen?”-ben kikapcsolható. Named pipe SID-alapú joggal, a kérő Windows-felhasználó azonosítva, pipe-foglalás ellen
  védve; a szerver felé az agent aláírt csatornáján.
- **Programkérések** a szerveren és a Központban: jóváhagyás → magától települ, az eredmény lezárja a kérést.
- **A Server szabályai a HUB-ról szerkeszthetők** (HubSiteOp; a telephely ment, a műveleti naplóba a HUB felhasználójával).
- **Központi napló a HUB-on**: audit, riasztás, SNMP, szerver (az USB-napló nem) – bájt-mutatóval, forgatás mellett is
  veszteség nélkül; a telephely adatlapján nézhető, szűrhető.
- Tesztek: új `unit/Tray` (aláírt csatorna, visszajátszás, hamis kulcs, repó / kérés / döntés / eredmény, belépés és
  zárolás, szabály-műveletek, napló-felküldés forgatással, központi napló, útvonal-kiszökés); a pipe élőben kipróbálva
  (azonosítás, korlát, idegen pipe elutasítása); 18/18.

### Fleet 1.5.6 · Agent 1.5.6 – sérülékenységek (CVE), eszköz-összegzés, szolgáltatás-figyelés, USB-napló, felület (2026-10-10)
A felhasználó döntése: „rákövetkező verzió” (1.5.6). A DevToolbox nem változott – 1.5.9 marad.
- **Felület:** „Fleet Server” / „Fleet HUB”; oldalon belüli megerősítő ablak a böngésző confirm() / prompt() helyett;
  haladásjelző (feltöltés: MB, MB/s, hátralévő idő; tesztek, mentés, átköltöztetés). Grafikon: % 0–100, GB 0-tól.
  SMART lemezenként táblázatban, kiemelt élettartammal.
- **Monitorozás-szabályok:** gyakori szabályok egy kattintással (CPU %, bármelyik mag / lemez – HDD és SSD –, hálózat
  Mbit/s …), mértékegység-választó (Mbit/s ↔ Gbit/s, GB ↔ TB) – a mentett küszöb a mérés saját egységében.
- **Agent:** szolgáltatás-figyelés 5 mp-enként (leállás → azonnali riasztás, a Központba és a Janusba is); USB-másolás
  napló (alapból KI); védelmi állapot (vírusvédelem, tűzfal, BitLocker); driverek és firmware a CVE-kereséshez; a frissítés-
  lista újraellenőrzése beállítható időközönként. `MetricConfig.StatusVersion` = 4.
- **Eszköz-összegzés:** 0–100 pont (Biztonság + Állapot) tételes levonással – szerveren és a Központban.
- **Sérülékenységek (CVE):** a Központ naponta párosítja a gépek programjait, drivereit, BIOS-át az NVD-vel, a CISA KEV-vel
  és az EPSS-szel; az eredmény a telephelyre is lemegy (gép-panel, pontszám). Élőben kipróbálva (7-Zip 23.01: 2 KEV-tétel).
- **Programtár:** az „Egyéb” kategória szűkült (bővebb szabályok), a repóban kézzel is választható kategória.
- Ellenőrizve: a winget-tár teljes (a közösségi forrás); a Lenovo Vantage / MSI Center Pro csak a Microsoft Store-ban van –
  a felhasználó döntése: nem kell.
- Tesztek: új `unit/Cve` (névegyszerűsítés, verzió-összevetés, NVD-feldolgozás, egyeztetés); 17/17.

### Fleet 1.5.5 · Agent 1.5.5 – Janus-kapcsolat megerősítve, internetfigyelés, SNMP-trap fogadás (2026-10-10)
A felhasználó döntése: a következő javítóverzió (1.5.5). A DevToolbox nem változott – 1.5.9 marad; az agent kódja nem
változott (a közös `NativeWin.cs` tűzfal-hívása kapott egy UDP-kapcsolót – közös verzió).
- **Janus–Fleet átnézés:** a Fleet oldalon megerősítve (átirányítás, TLS, válaszméret, csak a Janus valódi válasza számít
  sikernek, óraeltérés, titok-formátum, kulcs-ütközés, cím-ellenőrzés, a HMAC-kulcs nullázása). A Janus oldala jó volt
  (HMAC + hash_equals, ±5 perc, egyszeri nonce, titkosított titok, ügyfelenként elkülönített gépek); javaslatok a Janushoz
  (nem itt javítva): a gépnév-alapú párosítás ügyfelek között ütközhet; kulcsonkénti forrás-IP; hibás hitelesítés korlátozása.
- **Internetkapcsolat figyelése** a telephelyi szerveren: a megadott IP-címek sorban, 1–60 percenként (alap 5); az első
  válasz = van internet, különben a következő cím; ha egyik sem, megerősítő kör után riasztás; a kiesések mettől meddig – a
  Központ e-mailben is jelzi.
- **SNMP-trap fogadás:** v1 / v2c / v3 (minden szabványos hitelesítés és titkosítás), inform nyugtával; beérkezett trapek
  részletekkel, riasztást adó trapek (OID-előtag), próba-trap, útmutató; a Központba is felmennek.
- Tesztek: új `unit/Snmp` (RFC 3414 tesztvektorok, SHA-224, és egy független megvalósítás – Lextm.SharpSnmpLib, CSAK a
  tesztben – küldi a v1 / v2c / v3 trapet és informot minden protokollal; internetfigyelés; a Központ esemény-átvétele);
  `unit/Janus` bővítve – 16/16.

### Fleet 1.5.4 · Agent 1.5.4 – programtár fejlesztővel és kategóriával, automatikus csoportok, naplók és tárolás helye (2026-10-10)
A felhasználó döntése: javításként (1.5.4). A DevToolbox nem változott (a közös `Security.cs`-ben csak általa nem
használt részek) – 1.5.9 marad. Az agent kódja nem változott (közös verzió).
- **Programtár áttekinthetőbben:** fejlesztő (az azonosító első tagja, kattintásra szűr) és kategória (a winget-index
  `tags2` címkéiből, címke nélkül a névből) a teljes winget-tárban, a repóban, a döntésre várók és a gépeken talált
  programok között; kategória-címkék darabszámmal. Az „Új alkalmazáscsomag” ablak széles, kategóriánként csoportosít,
  fejlesztővel, „csak a kijelöltek” szűrővel és számlálóval; a telepítés-választó is csoportosít. Sötét görgetősáv és
  natív elemek mindenhol (`color-scheme: dark`).
- **Döntésre vár javítva:** csak a winget-tárba valóban újonnan került programok. Forrásváltáskor (a gép helyi winget-
  indexe ↔ Microsoft CDN) vagy gyanúsan sok „új” programnál új alap; 30 napnál régebbi, a tárból eltűnt és a korábbi
  tömeges (egy frissítésben felvett) tételek induláskor és frissítéskor kikerülnek. A régi gyorsítótár hamarabb frissül.
- **Automatikus csoportok** a szerveren és a Központban: Szerverek / Kliensek, Windows-verzió (Windows 11, Windows Server
  2022 …), Virtuális / Fizikai gépek (`SiteAgent.IsVirtual` – új mező a végén). Hatókörként mindenhol használható; a
  tagság kézzel nem módosítható, a Csoportok oldalon kikapcsolható. A gépek listája (szerver főoldal, Központ: telephely
  gépei) csoportosítható, a csoportok becsukhatók. A Központ telephelyre leküldött, automatikus csoportra szóló szabálya
  az 1.5.4-es telephelyen érvényesül.
- **Naplók és tárolás:** a naplók 1.5.4-től az adatmappa `logs\` almappájában – frissítéskor a régiek átköltöznek (ha a
  cél létezik, időrendben összefűzve). Beállítások → Tárolás (admin): a naplók, a mentések és a mérési adatbázis helye
  (helyi meghajtó, új vagy üres mappa, rendszermappa nem; jogai SYSTEM + rendszergazdák), a naplóméret; mentéskor
  átköltöztetés (a `metrics.db` élőben, másolással – hibánál a régi helyén marad). A kulcsok és a beállítások helye nem
  változik; az agent.log a gépeken a helyén maradt.
- Tesztek: új `unit/Storage`; `unit/Catalog` (kategória, fejlesztő, új alap, takarítás) és `unit/Groups` (automatikus
  csoportok) bővítve – 15/15.

### Fleet 1.5.3 · Agent 1.5.3 – Janus-kapcsolat, kapcsolat-teszt és azonnali kapcsolódás, frissülések láthatóan (2026-10-04)
- **Janus-kapcsolat a Központból** (a felhasználó döntése): a Központ a telephelyek géplistáját (15 percenként vagy
  változáskor: gép, OS, agent, RAM, C: lemez) és riasztásait (küszöb, kiesés; a megszűnést is) a Janus `api/fleet.php`
  végpontjára küldi, a Janus HMAC-protokollja szerint (`Hub/HubJanus.cs`); a Janus a figyelmeztető / kritikus riasztásból
  hibajegyet nyit. Beállítás: Központ → Beállítások → Janus-kapcsolat (a Janus címe, be/ki; telephelyenként a Janusban
  ügyfelenként kapott kulcsazonosító és titok – DPAPI-val titkosítva, nem jön vissza a felületre, a naplóba nem kerül –,
  „Próba” gomb). Sikertelen küldésnél a riasztás a sorban marad (24 óráig).
- **Telephely: kapcsolat-teszt és azonnali kapcsolódás** (Beállítások → Központ kapcsolat): „Kapcsolat tesztelése” mentés
  nélkül (elérés, TLS / proxy, a Központ kulcsa az ujjlenyomattal – új nyilvános `/hub/key` végpont), „Mentés és kapcsolódás”,
  „Kapcsolódás most”.
- **Központ: gyors betárcsázás** – a Központ nem hívhatja fel a telephelyet, ezért minden ott kért művelet (feladat,
  frissítés, csoport, új agent) után a telephely 10 percig 30 mp-enként tárcsáz be; kézzel is bekapcsolható; a telephely
  adatlapján a következő betárcsázás várható ideje.
- **Frissülések láthatóan:** Központ – „A szerver frissült: … → … (időpont)”, a legutóbbi agent-frissülések, a kért, még
  várakozó agent-frissítések; a gép oldalán „Az agent frissült” / „Agent-frissítés kérve”. Telephely – a gép paneljén
  „Az agent frissült” (agent-frissitesek.json) és „Frissítés kérve”; a műveleti naplóba is.
- Tesztek: új `unit/Janus` (helyi kamu Janus: a Janus PHP-ellenőrzésével azonos HMAC, géplista, riasztás, megszűnés,
  kiesés, hibakezelés, újrapróbálás, kikapcsolás).

### Fleet 1.5.2 · Agent 1.5.2 – a Központban a gép kezelése teljes oldalon (2026-10-04)
- A felhasználó kérése: a gép „Kezelés” nem ablakban, hanem teljes oldalon nyílik, bal oldali menüvel (Áttekintés,
  Frissíthető programok, Windows Update, Program telepítése, Telepített programok – szűrővel –, Újraindítás), mint a
  Programtár. Az Áttekintésben figyelmeztetés, ha az agent régebbi a telephely beépített agentjénél (frissítés gombbal).
  Az agent kódja nem változott (közös verzió).

### Fleet 1.5.1 · Agent 1.5.1 – szolgáltatás-választó, figyelmeztetés a régi agentre (2026-10-04)
- „Mit mérjen?”: a figyelt szolgáltatások a gépeken ténylegesen meglevő szolgáltatások listájából választhatók (szűrés,
  „csak a futók”, hány gépen van / fut) – a szerveren és a Központban is. Az agent a szolgáltatáslistát csak változáskor
  (és 6 óránként) küldi (`AgentStatus.ServiceList`, `MetricConfig.StatusVersion` = 3); a szerver megtartja az előzőt, a
  telephely összesítve küldi fel (`SiteSummary.Services`).
- A gép Monitorozás oldala jelzi, ha az agent 1.5.0-nál régebbi: a bővített mérések csak a frissítése után jelennek meg
  (a felhasználó jelezte, hogy nem látja őket – a gépén még a régi agent futott).
### Fleet 1.5.0 · Agent 1.5.0 – monitorozási szabályok, mérésbővítés, hardver-érzékelők, csoportok, portok, programtár a Központból (2026-10-04)
A felhasználó engedélyével új középső verzió. A DevToolbox nem változott (a közös protokollban csak új, általa nem
használt mezők vannak) – 1.5.9 marad.
- **Monitorozás menüpont** (szerver és Központ): küszöb-szabályok külön oldalon, metrika-választóval, „bármelyik”
  mintákkal (`disk.*.usedpct`), gép / csoport / (Központban) telephely hatókörrel; „Mit mérjen?” lap.
- **A Központ szabályai és mérés-kérése lemegy a telephelyekre** (ott csak olvasható, a helyiek mellett fut / összefésül).
- **Csoportok oldal** (szerver és Központ): létrehozás, tagok, átnevezés, törlés; a Központból telephelyre leküldve.
- **Portok a webről:** agent-port átmeneti kettős figyeléssel (az agentek ellenőrzötten átállnak), web-port azonnal.
- **Programtár: a Központ a mester** – a Központ repója / csomagjai / elutasítottjai a telephelyen csak olvashatóként.
- **Bővített mérés** (CPU részletek, magok, memória, lemez-I/O, SMART/NVMe, hálózat, ping, GPU, napló, biztonság,
  Hyper-V, folyamatok) és **hardver-érzékelők** LibreHardwareMonitorral + a PawnIO meghajtó wingetes telepítése.
- Linuxos tételek (load average, syslog, nginx, …) és Proxmox / VMware: az agent csak Windowson fut – nem részei.
- Tesztek: új `unit/Groups`; `unit/Alerts` és `unit/Catalog` bővítve; `e2e/HubDial` bővítve (programtár, szabály,
  mérés-kérés, csoport-művelet a Központból).

### Fleet 1.4.7 · Agent 1.4.7 · DevToolbox 1.5.9 – agent-frissítés javítás, tokenek törlése (2026-10-04)
- Javítva: ha a beépített agent kicsomagolása egyszer nem sikerült (pl. a vírusirtó épp vizsgálta), a szerver az
  újraindításig nem ajánlott agent-frissítést. Most tartalom-lenyomatos fájlnévvel csomagol ki, hibánál 2 perc múlva
  újrapróbál, és naplózza, melyik agentet adja ki.
- Gép adatlapja: ha az agent régebbi a szerverbe építettnél, figyelmeztetés (és hogy ki van-e kapcsolva az automatikus
  frissítés) + „Agent frissítése most” gomb.
- Biztonság → tokenek: a kiadott tokenek törölhetők (szerveren és a Központban).
- DevToolbox 1.5.9 / Agent: újrafordítva a közös kóddal (felületük nem változott).
### Fleet 1.4.6 · Agent 1.4.6 – minden menüpont teljes oldalon, összecsukható menü (2026-10-04)
A felhasználó kérése: ahogy a Programok, úgy minden menüpont külön oldalon jelenjen meg; az oldalsó menü legyen
összecsukható; a teljes winget-tárban az eldöntött programok ne látszódjanak.
- Új agent, Csoportok, Szabályok, Riasztások, Jelentések, Beállítások, Felhasználók (a Központban: Riasztások,
  Jelentések, Új telephely, Beállítások, Felhasználók, Szabályok) teljes oldalon; a részletes szerkesztők (pl. egy
  felhasználó módosítása) az oldal fölött nyílnak. A menü első pontja (Gépek / Telephelyek) visszavisz az áttekintésre.
- Az oldalsó menü ikonsávvá csukható (☰), az állapotot a böngésző megjegyzi.
- Programtár → Teljes winget-tár: a repóban levő és az elutasított programok alapból rejtve („az eldöntöttek is”
  kapcsolóval megjeleníthetők); a menü darabszáma a még el nem döntötteké.
- Agent: kódja nem változott (a közös verzió miatt 1.4.6).
### Fleet 1.4.5 · Agent 1.4.5 · DevToolbox 1.5.8 – heti automatikus kulcscsere, erősebb kulcsok, kulcslista (2026-10-04)
A felhasználó kérése: kulcslista (mit adott ki, mi cserélődött), heti kulcscsere agent ↔ szerver, és a szerver is
automatikusan – kibervédelmi szempontból összegyúrva, erősebb kulcspárral. Verzió: a felhasználó döntése (javításként).
- **Agent-kulcscsere** (`/api/rotate-key`): hetente új ECDSA P-384 kulcspár, kettős aláírás (régi + új), a szerver
  aláírt visszaigazolása; a régi kulcs a megerősítésig érvényes. Az 1.4.4-es agentek a frissítés után azonnal P-384-re
  cserélnek.
- **Szerver- és Központ-kulcs automatikus cseréje** (alapból hetente); a lánc 60 szem; P-384 csak ha minden agent /
  telephely 1.4.5+. A kézi csere megmaradt.
- **Biztonság oldal (teljes oldalon):** saját kulcs + előzmény, automatikus csere beállítása, agentek / telephelyek
  kulcsai előzménnyel, kiadott tokenek állapottal és letiltással, műveleti napló.
- Javítva: a teljes oldalas nézetben a beviteli mezők stílusa.
- DevToolbox 1.5.8: újrafordítva a közös kóddal (kriptográfia, kulcstár, protokoll) – felülete nem változott.
- Tesztek: új `unit/KeyRotation` (P-384, kompatibilitás, 60-as lánc, agent-csere minden hibaágával, erős kulcs
  feltétele, Központ); e2e/Monitoring: a valódi agentkód P-384-re cserél, és a kulcslistában megjelenik.

### Fleet 1.4.4 · Agent 1.4.4 – monitorozó oldal, magok, teljes winget-tár a programtárban (2026-10-04)
A felhasználó kérése képernyőképekkel; a felhasználó döntése: javításként (1.4.x). DevToolbox változatlan (1.5.7).
- **Javítva:** a nagy ablakok (pl. Monitorozás) kilógtak a képernyőből – az ablak most legfeljebb képernyőnyi, görget.
- **Monitorozás teljes oldalon**, kategóriánkénti menüvel (szerveren és a Központban); csempék a nem változó
  értékeknek, nagyobb grafikonok min/átlag/max-szal és egérrel leolvasható ponttal; a Központban 90 napig.
- **Agent – új mérések:** fizikai magok, foglalat, max. órajel (egyszer olvasva), lemezméret meghajtónként.
- **Programtár = saját repó a teljes winget-tárból:** a Microsoft előre indexelt forrása naponta letöltve (tartalék:
  a gép winget-forrása); Döntésre vár (a winget-tárba újonnan került – a felhasználó döntése), Elutasított (nem
  kérdezi újra, visszavehető), Gépeken talált, Alkalmazáscsomagok; döntés egyenként, kijelöltekre vagy mindre.
- Tesztek: új `unit/Catalog` (index, letöltés kamu CDN-ről, új programok, döntések, lapozás); e2e/Management
  bővítve; a felület-ellenőrzés az oldal szkriptjeit együtt is értelmezi (ütköző globális nevek ellen).

### Fleet 1.4.3 · Agent 1.4.3 · DevToolbox 1.5.7 – kiesés-riasztás, e-mail, CSV-jelentések (2026-10-04)
A felhasználó döntése: új funkciók ellenére javításként (1.4.x / 1.5.x).
- **Kiesés-riasztás** (szerver: gép; Központ: telephely és gépei) – alapból be, 15 perc, csoportszűrő, a jelzett
  kiesés újraindítás után is megmarad, induláskor türelmi idő; az aktív listában és a riasztásnaplóban látszik.
- **E-mail értesítés csak a Központból:** saját SMTP-kliens (STARTTLS, SSL 465, titkosítás nélkül), percenkénti
  kötegelés, jelszó DPAPI-val, teszt e-mail; a jelszó naplóba és a felületre nem kerül.
- **CSV-jelentések** mindkét felületen (Jelentések menü): géplista, telepített programok, elérhető frissítések,
  riasztásnapló, napi metrika-összesítő (1/7/30/90 nap).
- **Javítva – Központ felület:** a szerepkör nem jutott el a szkriptig (`window.ROLE` üres maradt), ezért a kezelői
  gombok egy része (gép kezelése, küszöb, csoportos művelet, szabály) rejtve maradt. A felület-ellenőrzés ezt mostantól
  vizsgálja (node, Python nélkül).
- **SecureFile:** a kulcsfájlhoz a létrehozó fiók is jogot kap (élesben SYSTEM – nem változik), így a tesztek emelt jog
  nélkül is átmennek (unit/HubStore, unit/Security). Agent: ezen kívül működése változatlan.
- **DevToolbox 1.5.7:** az Alkalmazás-frissítő távoli módja a gép agentjével dolgozik (Flotta agentjei közül választva,
  feladat a postaládán át); a PowerShell Remoting-os út megszűnt.
- Tesztek: új `unit/Notify` (kiesés, napló, CSV, DPAPI, SMTP egy helyi kamu szerverrel); e2e/Monitoring és e2e/HubDial bővítve.

### Fleet 1.4.2 · Agent 1.4.2 · DevToolbox 1.5.6 – frissítés a Központból
- A telephely szervere a Központ verziójára frissíthető (letöltés rövid életű tokennel, méret + aláírt SHA-256,
  csere visszaállítással); agentek automatikus frissítése a Központból kapcsolható; gépenkénti „Agent frissítése”
  (`AgentTaskKind.UpdateAgent` a lista végén – a telephely jelöli meg, nem az agent kapja).
- Régi (1.4.1 előtti) telephelyet egyszer kézzel kell frissíteni.

### Fleet 1.4.1 · Agent 1.4.1 · DevToolbox 1.5.5 – közös verzió, agent-cím javítás
- A Fleet és az Agent verziója mostantól közös.
- Javítva: localhost-ról megnyitott webfelületnél a telepítőcsomagba `localhost` került (más gépen az agent önmagára
  csatlakozott) – most a szerver belső IP-címe kerül bele.
- Központ Beállítások: adatmegőrzés, naplóméret, a szabályok ide kerültek.

### Fleet 1.4.0 · Agent 1.2.3 · DevToolbox 1.5.4 – programtár és alkalmazáscsomagok
- Programtár (a gépeken talált programokból vagy winget-keresővel) és alkalmazáscsomagok; telepítés gépre/csoportra.
- Javítva: az agent a programlistát a registryből olvassa (rendszerfiókban a winget-lista üres volt); a telepítő
  gombjai nagy DPI-n kicsúsztak; a „Mit mérjen” ablak jelölőnégyzetei.

### Fleet 1.3.2 · Agent 1.2.2 – biztonságos frissítés
- A webes feltöltés (Fleet Server / Központ frissítése) után a csere AZONNAL indul (szolgáltatásként): az új exe
  2 mp múlva leállítja, kicseréli, újraindítja, és ha nem indul el, visszaállítja a régit.
- Javítva: sikertelen frissítés után a hibás új exe félreáll (*.hibas.exe), nem próbálkozik újra minden indításkor.
- Az önfrissítő kód külön fájlban (Shared/ServiceSelfUpdate.cs) - csak a Fleet használja (Agent: működése változatlan).
- Az 1.3.0 előtti (régi) szerverről/Központról az ELSŐ frissítést még a régi program végzi: feltöltés után a
  szolgáltatást (vagy a gépet) egyszer újra kell indítani; onnantól automatikus.

### Fleet 1.3.1 · Agent 1.2.1 · DevToolbox 1.5.3 – optimalizálás
- Agent: a WMI-kapcsolat névterenként megmarad (a percenkénti mintavétel nem kapcsolódik újra lekérdezésenként).
- Telephely → Központ: a gépek programlistája csak változáskor megy fel (InstalledOmitted; 6 óránként teljes),
  a Központ megtartja az előzőt.
- FleetServer felület: a 15 mp-es géplista a telepített programok helyett darabszámot ad; a lista az
  Eltávolítás ablakban jön (/web/api/agent-installed).
- DevToolbox: eltávolításkor cmd nélkül törli magát (a futó exe a Temp-be kerül, a mappa azonnal törölhető).

### Fleet 1.3.0 · Agent 1.2.0 · DevToolbox 1.5.2 – egy exe, Központ a szerver szintjén, részletes metrikák
- **FleetServer + FleetHub egy exe-ben (`Fleet.exe`)**, szerepválasztással; az agent beépítve.
- **Nincs háttérben cmd / PowerShell / sc / netsh / icacls / shutdown / netstat** – natív Windows-hívások
  (`agent/Shared/NativeWin.cs`); a frissítés az új exe-vel (`apply-update`), visszaállítással. A winget marad.
- **Részletes metrikák:** percenkénti mintavétel időbélyeggel; új mérések: processzor-sor, folyamatok, szálak,
  futásidő, commit-memória, lapozás, lemez-I/O, hálózat adapterenként.
- **Központ:** gépkezelés, csoportos műveletek, monitorozás és riasztások, katalógus és alapcsomag, szabályok
  (a telephelyen futnak), új agent – mind a telephelyeken át. Szerver: `diag`.
- **Optimalizálás:** a minták nem kerülnek a `fleet.json`-ba; késleltetett állapotmentés; beállítás-gyorsítótár;
  metrika „latest” tábla; winget-lista 30 percenként; a Központ listája csak darabszámokat ad.
- DevToolbox 1.5.2: újrafordítva a közös kóddal; a fájl tulajdonságaiban helyes verzió (eddig 1.5.0-t írt).

### FleetServer 1.2.3 · Agent 1.1.3 – gépkezelés
Programtelepítés/-eltávolítás, programkatalógus és alapcsomag, Windows Update, gépcsoportok, automatikus szabályok
karbantartási ablakkal, újraindítás késleltetéssel. **Javítva:** a winget-táblázat oszlopai egyetlen szóköznél
összeolvadtak (az „Elérhető” mezőben a forrás is megjelenhetett).

### DevToolbox 1.5.1
Újrafordítva a legfrissebb közös kóddal (protokoll, kulcstár).

### FleetServer 1.2.2 · Agent 1.1.2 · Központ 1.1.3 – üzemeltetés
Agent önfrissítés, grafikus telepítővarázsló mindhárom programhoz, oldalsávos menü.
Az agent-telepítő meglévő regisztrációnál token nélkül is frissít.

### FleetServer 1.2.1 · Agent 1.1.1 · Központ 1.1.2 – biztonsági alapok
Aláírt szerverválasz, belépés-korlátozás, verziózott kulcstár és kulcscsere, mentések, műveleti napló.
**Javítva:** a FleetServer webfelületéről kimaradt a Monitorozás és a Riasztások kódja – pótolva, böngészőben tesztelve;
grafikon-rajzolás egybeeső időbélyegeknél.

### Központ 1.1.0–1.1.1
Egy port, útvonal-alapú szétválasztás; tűzfal minden profilon belső tartományokra; admin csak belső hálóról
(kívülről válasz nélküli bontás, belső IP nem szivárog); `diag`; NPM-útmutató.

### Korábbi fontos javítások
- https/http eltérés a telepítőben → az agent mindig `http://` + agent-port.
- .NET-függés → önálló (self-contained) exe-k.
- Több író írta felül a `fleet.json`-t → egyetlen író + postaláda.
- Magyar csoportnév az `icacls`-ben → SID-alapú jogosultság.
- Tűzfal csak Tartományi/Magán profilon → minden profil, belső tartományokra szűrve.
- Az agent-szolgáltatás a kicsomagolt ZIP-re mutatott → fix hely (`Program Files`), frissítéskor csere.
- Újratelepítés 1072-es hiba (törlésre jelölt szolgáltatás) → megvárja a tényleges eltávolítást.
- Az NPM Advanced fülre javasolt `location /` ütközött → az admin-védelem a Központba került.
- Az eseménynapló olvashatatlansága „0 hibát” mutatott → most hibaként jelenik meg.

---

## 6. Fejlesztési jegyzetek

- Forrás: `src\` (asztali), `agent\Shared` (protokoll, kriptográfia, kulcstár, varázsló), `agent\Agent`,
  `agent\Server`, `agent\Hub`, **`tests\`** (minden teszt).
- Kiadás: `dotnet publish -c Release -r win-x64` (a projektfájlok önálló, egyfájlos kimenetre állítva).
- A grafikus varázsló a `GUI_SETUP` fordítási szimbólum mögött van (csak a Windows-os projektekben).
- Új feladattípust (`AgentTaskKind`) csak a lista **végére** szabad felvenni.

### Tesztek – kiadás előtt kötelező

`tests\run-tests.sh` (Linux/WSL) vagy `tests\run-tests.ps1` (Windows, rendszergazdaként). Részletek: `tests\README.md`.

| Csoport | Tesztcsomag | Mit fed le |
|---|---|---|
| Logikai | 12 | biztonság, kulcscsere (P-384, lánc, agent-csere), szabályok, felhasználók, mérések, riasztások, kiesés, CSV, e-mail (kamu SMTP), winget-tár és programtár-döntések, Központ, hozzáférés, tokenek |
| Élő (HTTP) | 7 | valódi szerver/Központ + agentkód, támadó szerverrel is; jelentések, kiesés- és e-mail-beállítás |
| Felület | 4 | szkript-szintaxis és szerepkör (node), böngészős bejárás mindkét webfelületen |

Utolsó teljes futás: 20/20 sikeres (2026. október 2.). 2026. október 4.: a 12 logikai teszt és a felület-ellenőrzés
emelt jog nélkül is sikeres; az élő teszteket rendszergazdaként kell futtatni.

### 2026. október 2. – tesztek a forrásba, két forráshiba javítva
- A tesztek eddig csak a fejlesztői munkakörnyezetben voltak; most a `tests\` mappában, relatív útvonalakkal,
  futtatóval és leírással. A régebbi egységtesztek egy része a közben bővült kóddal már nem fordult – javítva.
- A Központ gép-hozzáférés tesztje (korábban felülíródott) újra megírva: logikai és élő változat.
- **Forráshiba:** a Központ projektje a C) csoport óta nem fordult (hiányzó fájlhivatkozás) – javítva.
  A kiadott Központ 1.1.3 ettől nem érintett (előtte készült).
- Az agent a `DEVTOOLBOX_AGENT_DIR` környezeti változóval tesztmappába irányítható (csak tesztekhez;
  nélküle változatlan). A következő agent-kiadásba kerül.
