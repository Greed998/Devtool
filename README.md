# DevToolbox 1.5.7 – IT Admin toolkit

Kiadó: **Poltoli Informatika – Pusztai Csaba e.v.** · [poltoli.hu](https://poltoli.hu) · info@poltoli.hu

## Mi változott az 1.5.7-ben

**Az Alkalmazás frissítő távoli módja mostantól a gép agentjével dolgozik.** A *Távoli gépen
(agenttel)* választásnál a gépet a Flotta agentjei közül választod ki (a helyi Fleet Server
adatmappájából – ugyanonnan, ahonnan a Flotta fül olvas). A lista az agent legutóbbi jelentése;
a *Kijelölt frissítése* / *Összes frissítése* feladatként a Fleet Server postaládáján át megy ki,
és a gép agentje a következő jelentkezéskor (legfeljebb 5 perc) rendszergazdai jogon elvégzi –
a felhasználónak semmit nem kell tennie, és offline gépnél a feladat megvárja a gépet.

A korábbi PowerShell Remoting-os (`Invoke-Command`) távoli frissítés **megszűnt**: agent nélküli
gépre távolról nem frissít a program (helyben továbbra is a winget fut). A távoli frissítés így
nem igényel WinRM-et, és nem ütközik abba, hogy a winget a távoli munkamenetből nem látszik.

A Fleet 1.4.3 újdonságai (kiesés-riasztás, e-mail értesítés a Központból, CSV-jelentések) a
webfelületeken vannak – lásd `FRISSITES.txt`.

## Mi változott az 1.5.0-ban

**Új – webes admin felület a Fleet Serverhez.** A szerver mostantól két külön porton figyel:
egy az **agent-kommunikációé** (alap 8443), egy a **böngészős admin felületé** (alap 8080).
Első induláskor a webfelület végigvezet a beállításon: létrehozod a **szuperadmin fiókot**, és
külön megadod a két portot. Modern, sötét, letisztult dizájn – teljesen beágyazva a szerverbe,
külső keretrendszer és internetkapcsolat nélkül.

A felületről látod az összes agentes gépet, ki online, mi elavult rajta, és **egy gombbal
frissítést adhatsz ki** (kijelölt alkalmazások vagy összes) – ez a nézet ÉS a vezérlés is.

**Új – agent-telepítő generálása a webről.** Az „Új token" gomb kész **PowerShell telepítőt**
ad: a token, a szerver címe és az ujjlenyomat már be van írva. Letöltöd, az új gépen egy
paranccsal futtatod, és az agent exét a szkript magától beszerzi a szerverről. Az új gép
felügyeletbe vétele így egyetlen fájl futtatása.

### A szuperadmin jelszó védelme

A jelszót soha nem tárolom visszafejthetően: **PBKDF2-SHA256**, egyedi sóval, 210 000
iterációval. A szerver csak ezt tárolja. A webes munkamenet **HttpOnly, SameSite=Strict**
sütivel megy, egy induláskor generált kulccsal aláírva – manipulálni nem lehet. Ezt a réteget
is teszteltem: a jelszó-visszafejtés, a süti-hamisítás és a felhasználónév-csere mind elbukik.

> A HttpListener HTTP-t nyit; a jelszó és a munkamenet miatt a webfelületet éles üzemben
> **TLS mögé kell tenni** (reverse proxy), vagy csak megbízható belső hálózaton/VPN-en
> elérhetővé. A setup és a login e nélkül is működik, de a szállítás titkosítása a TLS dolga.

### Telepítés

```
FleetServer.exe install       # szolgáltatásként telepíti, URL-foglalással
```
Majd nyisd meg: `http://localhost:8080` – ott az első beállítás (szuperadmin + portok).
A felügyelt gépekre a telepítőt már a webfelületről generálod.

## Szerepkörös felhasználókezelés (FleetServer és Központ)

Mindkét felületen (telephelyi FleetServer és Központ) több felhasználó hozható létre, három
szinttel:

| Szerepkör | Mit tehet |
|---|---|
| **Megfigyelő** | csak néz – nem indíthat frissítést, nem állíthat semmit |
| **Kezelő** | frissítést indíthat, gyakoriságot/tokent állíthat, feltölthet frissítést |
| **Adminisztrátor** | mindent, plusz **felhasználókat kezel** |

- Az első beállításnál létrejövő fiók automatikusan **Adminisztrátor**.
- A jelszavak felhasználónként PBKDF2-SHA256-tal, egyedi sóval tárolódnak (nem visszafejthetők).
- A munkamenet-süti a szerepkört is hordozza, **aláírva** – a kliensen nem hamisítható.
- Az **utolsó adminisztrátor** nem törölhető, nem tiltható le és nem fokozható le – így a
  rendszer nem marad kezelő nélkül.
- A felület a jog szerint rejti el a gombokat, és a szerver oldalán is ellenőriz (403, ha
  nincs jog) – nem csak a felületen.

24 automata teszt fedi: hitelesítés, szerepkör-szintek, utolsó-admin védelem, letiltás,
session-hamisítás elleni védelem, perzisztencia.

## Monitorozás – teljes (grafikonok + riasztás)

A FleetServer felületén, gépenként a **Monitorozás** gomb: kategóriánként (CPU, Memória, Lemez,
Szolgáltatás, Folyamat, Hőmérséklet, Napló) **idősoros grafikonok** (SVG, külső könyvtár nélkül),
6 óra / 24 óra / 7 nap / 30 nap váltóval. A „Mit mérjen?" panelen állítod, mely metrikákat
gyűjtse, mely szolgáltatásokat és folyamatokat figyelje, és a megőrzési időket.

**Riasztás – teljesen a te kezedben.** A topbar **Riasztások** gombján veszel fel küszöböket:
egy metrika, egy operátor (`>`, `<`, `=`), egy érték, és hány egymást követő mérésnél kell
teljesülnie (így egy pillanatnyi csúcs nem riaszt). A küszöb vonatkozhat egy gépre vagy az
összesre. **Alapból semmi nem riaszt** – csak amit te bekapcsolsz. Az aktív riasztások és a
riasztásnapló ugyanitt látszik. A grafikon melletti „+ küszöb" gombbal egy adott metrikára
egy kattintással veszel fel szabályt.

Az adat SQLite-ban, idősorként tárolódik, órás átlagra vont megőrzéssel – a fájl nem hízik
korlátlanul. Mindhárom szolgáltatás (agent, FleetServer, Központ) Windows-szolgáltatásként
települ (`install`), LocalSystem alatt; a telepítés után a parancssori ablak bezárható.

## Monitorozás – 1-2. lépés kész (idősoros metrikák)

Az agent mostantól **gépadatokat mér
** és küld fel a FleetServernek, ami **SQLite-ban idősorként**
tárolja. Amit gyűjt (a FleetServer beállítja, mit): CPU-terhelés, memória-kihasználtság, lemez%
és szabad hely, fizikai lemez SMART-állapot, hőmérséklet (ACPI), figyelt szolgáltatások
(fut/áll), figyelt folyamatok (hány példány), és a naplóhibák száma (1 óra).

- **Megőrzés:** részletes minta 7 napig, utána órás átlagra vonva 90 napig, majd törlés – a
  határok állíthatók, így az adatfájl nem hízik korlátlanul.
- **Semmi nem riaszt alapból** – a küszöböket te szabod meg (következő lépés).
- A metrikamodell **kulcs-alapú**, így új mérés a protokoll változtatása nélkül bevezethető.

Hátralévő lépések: grafikonok a FleetServer felületén, majd küszöbök + riasztás (teljesen a
te kezedben).

## Szerepkörös felhasználókezelés (4. lépés kész)

Mind a **FleetServer**, mind a **Központ** felületén saját felhasználók, három szerepkörrel:

- **Megfigyelő** – mindent lát, de nem indít semmit.
- **Kezelő** – frissítést indíthat, betárcsázási gyakoriságot állíthat, tokent/telephelyet kezelhet.
- **Adminisztrátor** – mindezt, plusz felhasználókezelés és beállítások.

A setupban készült **fő admin** védett: nem fokozható le, nem tiltható le és nem törölhető
(hogy ne lehessen véletlenül kizárni magad). A jelszavak PBKDF2-vel tárolódnak, a jogosultságot
minden API-végpont ellenőrzi (nem csak a felület rejti el a gombot).

**Telephely-hatókör a Központon.** Egy felhasználóhoz megadható, mely telephelyeket látja
(mind, vagy kiválasztottak). A korlátozott felhasználó a hatókörén kívüli telephelyet nem
látja a listában, és nem is adhat rá feladatot – ezt a backend is kikényszeríti, nem csak a UI.
Így több ügyfél gépeit egy Központban kezelheted anélkül, hogy mindenki mindent látna.

## Fleet – Központ NGINX Proxy Manager mögött (DNS only)

A Központ mostantól **egyetlen porton** figyel: az admin felület és a telephelyek
betárcsázása (`/hub/…`) is ugyanott megy, útvonal szerint szétválasztva – így az **NPM-nek
egyetlen proxy host** kell. A `FleetHub.exe install` a belső IP-n nyitja a portot és a tűzfalat,
a setup pedig **NPM-beállítási útmutatót** ír (Caddy helyett).

- **Cloudflare: DNS only** – a TLS végig az NPM-ig tart, a tartalom útközben nem látszik.
- **Admin csak belső hálóról**, a `/hub/` nyitva (aláírás védi). Az útmutató megadja az NPM
  Advanced blokkot ehhez.
- **Hub frissítés a webről**: Beállítások → Központ frissítése, az új FleetHub.exe feltöltése.

Szerepkörös felhasználókezelés és a könnyű, testreszabható monitor (Zabbix-szerű) külön
lépésben készül.

## Fleet 1.2 – Központ, ZIP-telepítő, frissítő felület

**Agent telepítőcsomag (ZIP).** A Fleet web „Új agent” gombja egy ZIP-et ad: benne a
DevToolboxAgent.exe + TELEPIT.cmd (a tokennel). Kicsomagolod, a TELEPIT.cmd-t rendszergazdaként
futtatod – kész. A token **lejár** és **géplimitje** van (alapból 60 perc / 1 gép), a
beállításokban szabod meg.

**Frissítő felület.** A Fleet web Beállítások ablakában feltöltheted az új FleetServer.exe-t;
a csere a szolgáltatás újraindításakor történik.

**Központ (Aggregátor) – új, önálló FleetHub.exe.** A telephelyi FleetServerek **betárcsáznak**
egy központba, publikus hálón át. Egy felületen látod az összes telephelyet és gépeiket, és a
**betárcsázás gyakoriságát telephelyenként a Központból állítod**.

- Kapcsolat: mindig a telephely nyit kifelé – az ügyfélnél nincs fix IP/DDNS/port.
- Biztonság: **TLS a Caddyval** (Let's Encrypt, a setup generálja a konfigot a domainhez) +
  mindkét irányban **aláírt üzenetek**, a Központ kulcsát a telephely ujjlenyomattal ellenőrzi
  (pinning). Token: rövid életű, egyszer használatos.

A Központ teljes láncát (regisztráció, aláírt jelentkezés, replay-védelem, gyakoriság-vezérlés,
feladat lefelé, visszavonás) valós HTTP-n futó teszt ellenőrzi.

## Fleet 1.1 – javítások

- **A telepítő https:// címet írt, a szerver viszont HTTP-t beszél** – az agent sosem tudott regisztrálni. Javítva.
- **Önálló agent és szerver** – nem kell .NET az ügyfélgépre (korábban enélkül a szolgáltatás el sem indult).
- **Egyetlen író**: a `fleet.json`-t csak a szerver írja; a DevToolbox Flotta fül és a parancssor postaládán át küld
  parancsot. Korábban az onnan létrehozott token vagy feladat nyomtalanul elveszett.
- **Magyar Windows**: a jogosultságok SID-del (`*S-1-5-32-544`), nem „Administrators” névvel.
- **Tűzfalszabály** a két portra (tartományi/magán profil), minden szerverinduláskor az aktuális portokhoz igazítva.
- **Agent napló** (`agent.log`) és **`diag` parancs** a hibakereséshez.
- **`FleetServer.exe reinstall`**: újratelepítés, a setup újra lefut; `--clean` teljes törléssel.

Valódi hálózati kapcsolaton, a tényleges agent- és szerverkódon futó teszt ellenőrzi a regisztrációt, jelentkezést,
feladatkiosztást és visszavonást.

---

## Mi változott az 1.4.0-ban

**Új – Flotta (agent-alapú felügyelet és frissítés).** Három részből áll:

- **Agent**: kis Windows-szolgáltatás a felügyelt gépeken, **LocalSystem-ként**. Ezért a
  bejelentkezett felhasználónak **nem kell adminnak lennie** – az agent maga rendszer-jogú,
  ő végzi a frissítéseket. A gépen **nem nyílik port**: az agent kizárólag kifelé,
  HTTPS-en jelentkezik, tehát tűzfal/NAT/VPN mögül is működik.
- **Fleet Server**: önálló, mindig futó szolgáltatás egy szerveren. Fogadja az agentek
  jelentkezéseit, tárolja az állapotukat, és kiosztja a feladatokat (frissítés).
- **Flotta fül** a DevToolboxban: egy felületen látod az összes agentes gépet, hogy melyiken
  mi elavult, és egy gombbal frissítést adhatsz ki – helyi jelenlét nélkül.

### A hitelesítésről

Nincs „feltörhetetlen kulcs", de a **felépítés** gyakorlatilag az. Nem közös jelszó, hanem
rendes kriptográfia:

- **Minden agent saját kulcspárt kap** telepítéskor. A privát kulcs **soha nem hagyja el a
  gépet**, és a szerver soha nem látja.
- **A belépést egyszeri token engedi** – ez a „komplex kulcs": **256 bites, kriptográfiailag
  véletlen**. A szerver ennek is csak a SHA-256 lenyomatát tárolja, visszafejteni nem lehet.
- Regisztráció után **minden üzenetet az agent a privát kulcsával ír alá**, a szerver a tárolt
  publikus kulccsal ellenőrzi. Így a token kiszivárgása sem enged üzenetet hamisítani – csak
  új agent regisztrálására jó, és a szerveren letiltható.
- **Kölcsönös hitelesítés**: az agent a szerver kulcs-ujjlenyomatát is ellenőrzi (közbeékelődés
  ellen), és minden kérés **időbélyeggel + egyszer használatos nonce-szal** védett a
  visszajátszás ellen.

Ezt a modellt (egyszeri token → gépenkénti kulcs → aláírt üzenetek) használják a komoly RMM-ek
és az Azure Arc is. A biztonsági rétegét **32 automata teszttel** ellenőriztem: a hamis
aláírás, a visszajátszott kérés, az elavult időbélyeg és a visszavont agent mind elbukik.

### Az agent-felügyelet a WinRM MELLÉ került, nem helyette

- **Ahol van agent** → jog nélküli, teljes körű kezelés, frissítés is.
- **Ahol nincs** → marad a csak-olvasás WinRM-en (Vezérlőpult), ahogy eddig.

Ez tudatos döntés: a winget-frissítés a célgépen elkerülhetetlenül rendszergazdai jogot
igényel (Windows-korlát). A WinRM jog nélkül ezt nem tudja megadni – az agent viszont igen,
mert ő maga rendszer-jogú.

### Telepítés

A szerveren (egyszer):
```
FleetServer.exe token "iroda"      # beléptető-token + szerver-ujjlenyomat kiírása
FleetServer.exe                     # a szerver indítása (alap port: 8443)
```

Minden felügyelt gépen (rendszergazda parancssor):
```
DevToolboxAgent.exe install --server https://SZERVER:8443 --token <TOKEN> --fingerprint <UJJLENYOMAT>
```

> A protokoll HTTPS-re készült. Éles üzemben a Fleet Servert TLS-terminátor (nginx, IIS, Caddy)
> mögé tedd, vagy kösd tanúsítványhoz – a belső hitelesítés (aláírt üzenetek) e nélkül is véd,
> de a TLS adja a titkosítást a szállításon.

---

## Mi változott az 1.3.0-ban

**Új – Alkalmazás frissítő** (Program menü). A winget köré épült felület: lekéri a gépen
telepített alkalmazásokat, kijelzi a frissíthetőket, és egy vagy több programot frissít –
**helyben vagy távoli gépen**. (A távoli frissítés 1.5.7-től a gép agentjével megy; az itt
eredetileg leírt PowerShell Remoting-os út megszűnt.)

**Új – 3 telepítő szkript** a beépített készletben, a te szkriptjeid javított változatai:
céges alap-szoftvertelepítés wingettel, opcionális szolgáltatások letiltása, és winget/App
Installer helyreállítás.

**Bővítve – +10 szolgáltatás** a Windows szolgáltatások katalógusában (most 49), köztük
a smart card szolgáltatások és a WebClient (WebDAV).

---

## Mi változott az 1.2.1-ben

**Új – Poltoli embléma az ikonban.** A programikon, a tálcaikon, a Névjegy és az indítóablak
is a cég pajzs-emblémáját használja.

A .ico nyolc méretet tartalmaz (16, 20, 24, 32, 48, 64, 128, 256). A kisebbeknél a vékony
gradiens kontúr külön erősítést kap, különben 16 pixelen szinte eltűnne – a nyers kicsinyítés
halvány, alig látható foltot adott. A teljes logó szöveggel együtt nem került az ikonba: kis
méretben a felirat olvashatatlan pép lenne, ezért csak a pajzs szerepel benne.

---

## Mi változott az 1.2.0-ban

**Új – Vezérlőpult, az első fül.** Tartományvezérlők és kiszolgálók folyamatos figyelése,
csoportokba rendezve. Első indításkor végigvezet a beállításon.

**Új – háttérfutás és tálcaikon.** Az ablak bezárása nem lép ki: a program a tálcán fut
tovább és figyel. Az ikon színes szövege mutatja az összesített állapotot, a jobb gombos menü
pedig azonnali ellenőrzést, szüneteltetést és valódi kilépést kínál.

**Új – azonnali értesítés.** Ha egy gép hibára vált, buborékértesítés jön a hiba tartalmával.

**Optimalizálás – lusta fülbetöltés.** A fülek csak az első megnyitásukkor épülnek fel. Eddig
induláskor felolvasódott a szkriptkönyvtár és elindult a szenzorolvasás akkor is, ha rá se
néztél; most a Vezérlőpulton kívül semmi nem fut feleslegesen.

**Optimalizálás – egypéldányos futás, memória visszaadás.** Két párhuzamos példány két
figyelőkört jelentene ugyanazokra a gépekre, ezért a program mostantól csak egyszer indul el.
Tálcára téve visszaadja a munkakészletet az operációs rendszernek, és nem-egyidejű
szemétgyűjtővel fordul, ami kisebb memórialábnyomot ad.

---

## Mi változott az 1.1.0-ban

**Új – többplatformos szkripttár.** A könyvtár már nem csak Windows-os szkripteket tárol:
bekerült a **Bash (.sh)**, a **PowerShell 7 (.ps1)** és az **SQL (.sql)**. A lista külön
oszlopban mutatja a platformot, és ami a gépen nem futtatható, azt halványan jelöli, a
részletek pedig megmondják, miért. Ilyenkor is tárolható, szerkeszthető, **vágólapra másolható
és fájlba menthető** – két új gombbal.

**Új – bash futtatás WSL-en.** Ha a gépen van Linux alrendszer, a bash szkriptek helyben is
futnak: a program lefordítja az útvonalat (`C:\Temp\a.sh` → `/mnt/c/Temp/a.sh`).

**Új – szkriptépítő.** A Szkriptek fül *Építő…* gombja hét sablonból generál kész szkriptet
az adatok bekérése után – köztük **Active Directory erdő** és **belső DHCP kiszolgáló**
felhúzása.

**Új – 10 kiszolgálói és Linux szkript** a beépített készletben (összesen 31).

---

## Mi változott az 1.0.9-ben

**Javítva – üres hőmérséklet-panel.** A hibát én vittem be: a SMART-olvasó **saját
`Computer` példányt** nyitott a szenzorkönyvtárból, majd `Close()`-zal lezárta. A kernel-
illesztő viszont **megosztott erőforrás** – a lezárás elrántotta a hardvert a hőmérséklet-panel
alól. Mivel az 1.0.7-ben a SMART-ot előre hoztam, azóta a panel teljesen üres maradt (ezért
tűnt el a CPU-terhelés is). Mostantól az egész program **egyetlen példányt** használ, és a
SMART is ebből olvas. Erre külön teszt figyel, hogy ne kerülhessen vissza.

**Javítva – lassú frissítés.** Három ok volt:

1. A lista minden ciklusban teljesen újraépült. Most csak az értékek íródnak át, ha az
   érzékelők halmaza nem változott – ettől a villogás is megszűnik.
2. A tárolók frissítése SMART-olvasást indít lemezenként, ami lassú. Ez most **30
   másodpercenként** fut, nem 3-anként – a lemezhőfok úgysem változik másodpercenként.
3. Az ACPI termikus zónák WMI-lekérdezése minden ciklusban lefutott. Most 10 másodpercre
   gyorsítótárazva van.

**Új – Memóriaintegritás felismerése.** Ha nincs CPU-hőmérséklet, a program megnézi, hogy be
van-e kapcsolva a **Memóriaintegritás (Core Isolation / HVCI)**, és ezt ki is írja. Ez blokkolja
a szenzor-illesztőket, mert azok szerepelnek a Microsoft sérülékeny-illesztő listáján – új
gépeken alapból be van kapcsolva, és ez a leggyakoribb oka a hiányzó hőfoknak.

---

## Mi változott az 1.0.8-ban

**Javítva – 63 „adapter" a Hálózat fülön.** A Windows minden NDIS szűrődriver-példányt külön
hálózati interfészként mutat (Npcap, QoS Packet Scheduler, WFP Native MAC Layer Filter), és a
WAN Miniportok is így jelennek meg – egyetlen Ethernet kártyából így lesz nyolc-tíz sor.
Ezek mostantól rejtve vannak, csak a **valódi adapterek** látszanak. Az „Általános" blokk
kiírja, mennyit rejtettünk el, és egy eszköztár-kapcsolóval (*Virtuális adapterek is*)
bármikor előhozhatók.

**Javítva – nem lehetett összecsukni a csoportokat.** A `ListView` csoportjai csak akkor
kapnak összecsukó nyilat, ha a `CollapsedState` explicit értéket kap – `Default` esetén a
Windows nem rajzol semmit. Most a Hálózat, a Hardver, az érzékelők és a Szkriptek fül
csoportjai is összecsukhatók, és a **nem aktív adapterek alapból csukva** nyílnak meg.

---

## Mi változott az 1.0.7-ben

**Javítva – nem működött a hőmérséklet-figyelő.** A LibreHardwareMonitor a kernel-illesztőjét
az `Assembly.Location` mellé csomagolja ki, ez viszont **egyfájlos publikálásnál üres** – így
az illesztő soha nem töltődött be, és nem volt CPU-hőfok (a terhelés igen, mert az máshonnan
jön). A publikálás mostantól kicsomagolja a teljes tartalmat, így lesz érvényes útvonal.
Mellé két tartalék is került: ha az illesztő mégsem tölt be, a program az **ACPI termikus
zónákat** és a **lemezek SMART-hőfokát** mutatja. Új *Diagnosztika* gomb megmondja, pontosan
hol akadt el.

**Új – IP-tartomány a távoli lekérdezésnél.** Megadható rövid formában (`192.168.1.10-40`),
teljes tartományként (`192.168.1.10-192.168.1.40`) vagy maszkkal (`192.168.1.0/24`).
A mező alatt látszik, hány gépet jelent. Alapból ping-előszűrés fut, mert a WMI időtúllépése
gépenként 20–30 másodperc is lehet.

**Új – „A bejelentkezett felhasználó adataival" jelölőnégyzet.** Bekapcsolva integrált
hitelesítéssel megy a lekérdezés, a felhasználónév és jelszó mező inaktív lesz.

**Javítva – a Hálózat fül szétválasztja az aktív és nem aktív adaptereket**, külön fejléc
alatt, jelöléssel (● / ○).

**Áthelyezve – a RAID kalkulátor a Program menübe került**, saját ablakba.

**Javítva – az eseménynapló azonnal újraelemez**, ha időszakot, szintet vagy naplót váltasz.

**Új – Windows szolgáltatások** a Program menüben.

**Bővítve – licenckinyerés:** Office kulcsok visszafejtése és általános registry-keresés.

---

## Mi változott az 1.0.6-ban

**Új – összecsukható szakaszok a HTML jelentésben.** A szakaszok `<details>` elemek, tehát
JavaScript nélkül is működnek, offline is. A nagy táblázatok (telepített szoftverek, figyelt
portok, szkriptek) alapból csukva nyílnak meg, a fejlécük mutatja a darabszámot. Felül gombok:
*Összes kinyitása / becsukása / Nyomtatás*. Nyomtatásnál minden automatikusan kinyílik.

**Javítva – szétcsúszó táblázatoszlopok.** A táblázatok `table-layout:fixed` elrendezést és
rögzített oszlopszélességeket kaptak, a szám- és dátumoszlopok jobbra igazítva. Korábban a
böngésző a tartalom alapján osztotta el a helyet, ezért nagy üres hézagok keletkeztek.

**Javítva – levágott export ablak.** Nagyobb és átméretezhető lett, a magyarázó szöveg követi
az ablakszélességet. A tartalom kijelöléséhez *mind / egyik sem* gomb került.

**Javítva – olvashatatlan HTML forrás.** A `WebUtility.HtmlEncode` a 127 feletti karaktereket
is számkódos entitássá alakította (`Tárhely` → `T&#225;rhely`). UTF-8 dokumentumban ez
felesleges: most csak az `& < > " '` karakterek kódolódnak, az ékezetek olvashatóak maradnak,
és a fájl is kisebb.

**Új – 21 üzemeltetői szkript** öt kategóriában (lásd lentebb).

---

## Mi változott az 1.0.5-ben

**Javítva – elcsúszott kártyák az indítóablakban.** A két választókártya külön-külön a saját
tartalmához méretezte magát, ezért a hosszabb szövegű alsó kártya szélesebb lett, és a jobb
szélük nem esett egybe. Most mindkettő kitölti az oszlopot, a két gomb pedig azonos szélességű.

**Új – önaláírt tanúsítvány készítése** (`make-cert.cmd`) és telepítése célgépekre
(`install-cert.cmd`). A `sign.cmd` kapott egy `SELFSIGNED` módot.

---

## Mi változott az 1.0.4-ben

**Kiadói metaadatok.** A fájl tulajdonságainál és a UAC ablakban mostantól a kiadó neve,
a leírás és a copyright is látszik. A Névjegy kiírja, hogy a futó példány **alá van-e írva**,
és ha igen, ki által.

**Aláíró szkript** (`sign.cmd`) három módban: PFX fájl, hardveres token / tanúsítványtároló,
illetve Azure Artifact Signing. A jelszót futáskor kéri be, nem tárolja.

> **Fontos:** a metaadat önmagában NEM tünteti el a SmartScreen figyelmeztetést. Az
> „Ismeretlen kiadó" felirat a digitális aláírásból jön, nem a fájl metaadataiból.
> Részletek a Kódaláírás fejezetben.

---

## Mi változott az 1.0.3-ban

**Javítva – a minta szkriptek nem futottak le.** A Microsoft eltávolította a `wmic` parancsot
a Windows 11 újabb kiadásaiból (24H2 / 25H2 és afölött), ezért a beépített minta hibára futott.
A minták most CIM/PowerShell parancsokat használnak, és jött két új is (lemezek szabad helye,
hálózati gyorsteszt). **A már telepített példányoknál is frissülnek**: a program felismeri az
elavult mintát, kicseréli, és a kimeneti ablakban jelzi. A saját szkriptjeidhez nem nyúl –
csak a „Minta" kategóriájú, névre egyező beépített darabokhoz.

**Javítva – elméretezett Beállítások ablak.** A „Tallózás…" és „Alap" gombok kilógtak, az
Aktuális helyek három sora pedig nem állt egy vonalban. Oka: a `FlowLayoutPanel` nem nyújtja
a gyermekeit a szülő szélességére, a `Label` `AutoSize` tulajdonsága pedig felülírta a fix
szélességet. Most táblázatos elrendezés van: a beviteli mező kitölti a helyet, a gombok mindig
látszanak, a címkék pedig fix szélességű oszlopban állnak. Az ablak nagyobb és átméretezhető,
a magyarázó szövegek követik a szélességet.

**Javítva – az indítóablakban törtek az útvonalak.** A hosszú elérési út szó közben tördelődött
(„…DevTool / box"). Most fix szélességű, automatikusan hárompontozó címke, a teljes út pedig
elérhető egérrel rámutatva.

**Új – RAID kalkulátor fül.**

---

## Mi változott az 1.0.2-ben

**Névjegy.** Üzenetdoboz helyett rendes ablak: mód, telepítési hely, adatmappa, szkriptkönyvtár
és exportmappa; a használt komponensek verzióval, licenccel és azzal, hogy **mihez** kellenek;
végül a teljes rendszerkövetelmény-lista. Vágólapra másolható – ügyfélnek küldhető.

**Indítás rendszergazdaként.** Telepítés után a program mindig emelt joggal indul: a Start menü
és az asztali parancsikon is „futtatás rendszergazdaként" jelzőt kap (a .lnk fejlécének
LinkFlags mezőjében a 13. bit), és a program indításkor magát is emeli, ha kell. Ha az UAC-ot
elutasítod, felajánlja a korlátozott futtatást ahelyett, hogy egyszerűen kilépne.

**Hordozható módban választható a futtatás módja.** Az indítóablakban a hordozható kártyán
két lehetőség: *Normál futtatás* vagy *Futtatás rendszergazdaként*. A választás a `Data`
mappába kerül, tehát pendrive-on másik gépre is átjön.

**Exportálás menü** (a Súgó előtt) – HTML vagy szöveges jelentés, választható tartalommal
és célmappával.

**Beállítások menü** – elérési utak, indítási jogosultság, szenzorok, eseménynapló,
távoli hozzáférés.

**Szkriptkönyvtár átállítható** – akár hálózati vagy felhős mappára, így több gép ugyanazt
a szkriptkészletet használhatja.

---

## Mi változott az 1.0.1-ben

**Javítva – elméretezett indítóablak.** A választókártyák fix magasságúak voltak, ezért más
betűméretnél és DPI-nél levágódott az alsó sor. Most az egész ablak automatikus elrendezésű
(TableLayout + AutoSize), tehát a tartalomhoz igazodik.

**Javítva – lefagyás a Tárhely fülön.** Két oka volt, mindkettő javítva:

1. A szenzor-időzítő az **UI szálon** olvasta a hardvert, és fülváltáskor sem állt le. A
   LibreHardwareMonitor frissítése SMART- és I/O-műveleteket indít, ami másodpercekre
   megakasztotta a felületet – ezért látszott „Nem válaszol" a Tárhely fülön is. Most
   háttérszálon fut, és ha a Hardver fül nem látszik, meg is áll.
2. A `DriveInfo` tulajdonságai (`IsReady`, `TotalSize`, `VolumeLabel`) blokkoló
   rendszerhívások – lecsatolt hálózati meghajtónál vagy üres optikai meghajtónál
   percekig várakozhatnak. Ráadásul **minden újrarajzoláskor** lefutottak. Most egyszer,
   háttérszálon olvassuk ki őket, a kártyák pedig csak a másolatból rajzolnak.

**Új – SMART.** A Hardver fül jobb alsó paneljén.
**Új – Hálózat fül.**
**Új – Licencek fül.**
**Új – Távoli gép fül.**


Szkriptkezelő, hardver-, hálózat-, licenc- és eseménynapló-elemző eszköz Windowsra.
Helyben és hálózaton keresztül is. 64 bites (x64), .NET 10.

`DevToolbox.exe` – önálló, egyetlen fájl. Nem kell hozzá .NET runtime, nem kell telepítő,
nem kell rendszergazdai jog az indításhoz.

---

## Első indítás: telepíthető vagy hordozható

Az exe legelső indításakor egy választóablak jelenik meg:

**Telepítés erre a gépre**
- Bemásolja magát ide: `%LocalAppData%\Programs\DevToolbox`
- Parancsikon a Start menübe és az asztalra (kipipálható)
- Megjelenik a Windows „Alkalmazások és szolgáltatások" listájában → onnan eltávolítható
- Adatok helye: `%AppData%\DevToolbox`
- **Nem kell rendszergazdai jog** (felhasználói profilba telepít)

**Hordozható futtatás**
- Semmi nem kerül a rendszerbe: se registry, se parancsikon
- Az exe mellé készül egy `Data` mappa, abban van minden szkript és beállítás
- Pendrive-ról is vihető, gépről gépre

A választás később módosítható: `Program → Telepítés erre a gépre…`, illetve
`Program → Eltávolítás…`. Hordozható módból úgy is lehet újra választani, hogy törlöd
az exe melletti `portable.dat` fájlt.

---

## Szkriptek fül

- **Betöltés fájlból** – meglévő `.bat` `.cmd` `.ps1` `.vbs` `.py` felvétele (több egyszerre is).
  A fájl bemásolódik a könyvtárba, tehát az eredeti mozgatása/törlése nem töri el.
- **Új szkript** – beépített szerkesztő, sötét témás kódmezővel.
- **Futtatás** (F5 vagy dupla kattintás) – a kimenet élőben folyik be a jobb oldali konzolba,
  a hibák pirossal. A **Leállítás** a teljes folyamatfát lelövi.
- **Futtatás rendszergazdaként** – szkriptenként beállítható; ilyenkor UAC kérdéssel,
  külön ablakban indul (emelt jogú folyamat kimenetét a Windows nem engedi átirányítani).
- **Kategóriák** – a lista kategóriánként csoportosítva, kereséssel szűrhető.
- **Paraméterek** – szkriptenként eltárolható parancssori argumentum.
- **Kódolás** – `utf-8` (alap), `cp852`, `cp1250`, `cp437`. Ha egy régi batch fájl ékezetei
  krix-kraxok, itt lehet átállítani. Batch esetén az UTF-8 mód automatikusan `chcp 65001`-et
  állít, a PowerShell pedig UTF-8 kimenetre vált.

Tárolás: a szkriptek fizikai fájlként az adatmappa `scripts` almappájában, a hozzájuk tartozó
adatok (név, leírás, kategória, futásszám) egy `library.json` fájlban. Mindkettő nyitott
formátum, kézzel is szerkeszthető, menthető, másik gépre átvihető.

---

## Windows szolgáltatások

A **Program → Windows szolgáltatások…** menüpont kilistázza a gép összes szolgáltatását, és
**49-et közülük minősít**: azokat, amiknél pontosan meg lehet mondani, mi történik a
letiltástól. Három szint:

| Jel | Jelentés |
|---|---|
| ✓ | céges munkaállomáson biztonsággal letiltható |
| ! | letiltható, de függ a használattól |
| !! | csak nagyon indokolt esetben |

Minden szolgáltatásnál két blokk olvasható: **mit csinál** és **mi romlik el, ha letiltod**.
Ez a lényeg – a lista nem „gyorsítsd fel a Windowst" javaslatgyűjtemény, hanem döntéshez adott
információ. Néhány példa arra, miért:

- **Xbox Live Auth Manager, Xbox Live Game Save, Gaming Services** – céges gépen semmit nem
  visznek magukkal, ezek a tipikus felesleges szolgáltatások.
- **Print Spooler** – kockázatosnak jelölve: letiltva **semmilyen** nyomtatás nem működik,
  még a PDF-nyomtató sem. Szerveren viszont épp biztonsági megfontolásból szokás.
- **Windows Search** – szintén kockázatos: az **Outlook keresése elromlik** tőle. Sokan
  letiltják és aztán keresik, miért nem talál semmit a levelezőjük.
- **SysMain (Superfetch)** – SSD-n kevés haszna van, HDD-n viszont érezhetően lassul tőle a
  gép, ezért nem lehet egységesen ajánlani.
- **DiagTrack (telemetria)** – gyakran tiltják, de ez táplálja a Windows Update for Business
  jelentéseket és az Intune eszközállapotot.

A szolgáltatások szűrhetők (kikapcsolhatók / csak a biztonsággal tilthatók / amit már
letiltottál / ami most fut / az összes) és kereshetők. Az indítási mód átállítható
(Automatikus / Kézi / Letiltva), a szolgáltatás leállítható, és egy gombbal **visszaállítható
a Windows alapértelmezésére** is – tehát nem egyirányú út.

A módosítás WMI-n keresztül történik, ezért rendszergazdai jog kell hozzá; enélkül a lista
megtekinthető, de nem módosítható. A program **soha nem tilt le semmit magától**, minden
változtatás külön megerősítést kér, és a kockázatosaknál a hatás is megjelenik a kérdésben.

A **kritikus rendszerszolgáltatások** (RPC, DCOM, Eseménynapló, Winlogon, tűzfal-motor)
szándékosan nincsenek a listán – ezeket nem lehet biztonságosan letiltani, tehát nincs miért
felkínálni.

---

## Licenckulcsok – bővített keresés

A Licencek fül immár négy forrásból dolgozik:

1. **Firmware (OEM) kulcs** – az alaplapba égetett Windows-kulcs.
2. **Windows telepítési kulcs** – a registry `DigitalProductId` bejegyzéséből.
3. **Office kulcsok** – az Office saját `Registration` ágából. Az Office 2010/2013 a régi
   base-24 algoritmust használja, a 2016+ az „N" beszúrásosat; a program mindkettőt ismeri.
4. **Általános registry-keresés** – végigmegy a szoftverek registry-ágán, és összeszedi a
   kulcsnak tűnő értékeket (`ProductKey`, `SerialNumber`, `LicenseKey`, `CDKey` és társai).

A negyedik pontnál fontos az őszinteség: a talált érték **lehet licenckulcs, de sorozatszám
vagy telepítési azonosító is** – a program ezt oda is írja, és megmutatja, melyik registry-ág
alól jött. A keresés kiszűri a verziószámokat, útvonalakat és GUID-okat, időkorlátos, és nem
próbál semmilyen védelmet megkerülni: csak azt olvassa el, amit a szoftverek maguk tárolnak
olvasható formában.

Amit **nem** lehet kinyerni, azt is kimondja: a Microsoft 365 előfizetéshez nem tartozik
kulcs (az aktiválás fiókhoz kötött), és a Windows a legtöbb kulcsot csak részlegesen
(utolsó 5 karakter) tárolja visszafejthető módon.

---

## Alkalmazás frissítő

A **Program → Alkalmazás frissítő…** a winget (Windows Package Manager) köré épül. Fent
választható, hol dolgozzon: **ezen a gépen** vagy **távoli gépen (agenttel)** – ez utóbbinál a
gépet a Flotta agentjei közül választod (1.5.7-től; lásd lent).

Az *Alkalmazások lekérése* gomb kilistázza a telepített programokat, alapból a frissíthetőket
kijelölve. Innen egy gombbal frissíthető a kijelölt néhány, vagy az összes. A napló élőben
mutatja a winget kimenetét, a művelet megszakítható, és a végén összesítést ad.

Néhány dolog, amit érdemes tudni:

**A winget kimenetét pozíció alapján dolgozom fel.** A winget táblázatos szöveget ad,
nyelvfüggő oszlopfejlécekkel – a fejléc alatti `----` elválasztó és az oszlopok kezdőpozíciója
adja a határokat. Így a magyar és angol nyelvű winget is helyesen olvasható, és a szóközös
alkalmazásnevek (pl. „Mozilla Firefox (x64 hu)") sem csúsznak el. Ezt szintetikus winget-
kimeneteken teszteltem.

**Távoli gépen a gép agentje frissít (1.5.7-től).** A lista az agent legutóbbi jelentése (a
Fleet Server adatmappájából); a frissítés feladatként a szerver postaládáján át megy ki, és az
agent a következő jelentkezéskor rendszergazdai jogon végzi el. Ehhez a DevToolboxnak el kell
érnie a Fleet Server adatmappáját (a szerveren futtatva, rendszergazdaként, vagy a Flotta fülön
megadott mappával). A korábbi PowerShell Remoting-os út megszűnt – agent nélküli gépre távolról
nem frissít; a gépek a Fleet Server és a Központ webfelületéről is frissíthetők.

---

## Telepítő szkriptek

Három szkript került a készletbe a **Telepítés** kategóriában. Ezek a beküldött szkriptjeid
javított változatai – amit javítottam:

**Alap szoftvertelepítés (winget)** – a v2 alapján, néhány kiigazítással:
- **.NET Desktop Runtime**, nem a sima Runtime. WinForms/WPF alkalmazásokhoz a Desktop Runtime
  kell; a sima Runtime csak konzolos appokat futtat.
- **MSI Center**: a `MSI.MSICenter` winget-azonosító a Store-os `9NVMNJCR03XV` helyett –
  a winget a saját ID-jével megbízhatóbban telepít.
- Winget-ellenőrzés a legelején, és ha hiányzik, App Installer regisztráció.
- Hibakód-kezelés: külön kezeli a „már telepítve" (-1978335189) esetet, és a végén CSV
  összesítést ír.

**Opcionális szolgáltatások letiltása** – jó szkript volt, egy **valódi hibával**: a
`$matches = @(Get-Service ...)` az automatikus `$matches` változót írta felül, amit a `-match`
operátor is használ – ez kiszámíthatatlan viselkedést okoz. Átneveztem `$svcMatches`-re. A
szkript egyébként helyesen alapból csak felmér, hardver hiányában tiltja a Bluetooth/smart
card/kamera szolgáltatásokat, és backupot készít a visszaállításhoz.

**Winget – App Installer helyreállítás** – ha a gépről hiányzik a winget, újraregisztrálja az
App Installert.

Mindhárom emelt jogosultsággal fut, és a beépített szolgáltatás-listával összhangban tiltja a
szolgáltatásokat.

---

## Vezérlőpult

Az első fül. Ha még nincs beállítva semmi, egy rövid bemutató fogad, és egyetlen gombbal
felveheted az első tartományvezérlőt.

### Mit ellenőriz

| Ellenőrzés | Mit néz |
|---|---|
| Elérhetőség | három ping átlaga és csomagvesztés; ha a tűzfal tiltja az ICMP-t, TCP-vel próbálkozik |
| Szolgáltatási portok | LDAP 389, Kerberos 88, DNS 53, SMB 445, RPC 135 |
| WMI kapcsolat | operációs rendszer, gyártó/modell, üzemidő, fizikai vagy virtuális |
| Óraeltérés | a helyi órához képest |
| Lemezkapacitás | meghajtónként a foglaltság, állítható küszöbökkel |
| Fizikai lemezek | `MSFT_PhysicalDisk` állapot – **virtuális gépen kihagyva** |
| Címtárszolgáltatások | NTDS, Netlogon, DNS, KDC, W32Time, DFSR |
| SYSVOL és NETLOGON | megvan-e a két megosztás |
| Eseménynapló | az elmúlt 24 óra komoly hibái, a meglévő tudásbázissal értékelve |

Néhány döntés, ami a gyakorlatban számít:

**Az óraeltérés nem kozmetika.** A Kerberos alapértelmezetten 5 percnél nagyobb csúszásnál
megtagadja a bejelentkezést. A program 2 perc fölött figyelmeztet, 280 másodperc fölött már
hibát jelez – tehát azelőtt szól, hogy a felhasználók nem tudnának belépni.

**Virtuális gépen a fizikai lemez kimarad.** A SMART adat a hypervisor mögül nem látszik, így
az ellenőrzés félrevezető lenne. A program felismeri a gyakori hypervisorokat (VMware, Hyper-V,
KVM/QEMU, VirtualBox, Xen, Parallels), és ilyenkor kiírja, hogy a tárolót a gazdagépen érdemes
figyelni. **A szabad hely mérése megmarad** – az ugyanúgy betelhet.

**A SYSVOL hiánya replikációs hibát jelent.** Ha a megosztás eltűnik, a csoportházirendek nem
érvényesülnek – ez a fajta hiba hetekig észrevétlen tud maradni.

### Csak olvas, és csak belső hálózaton

A modul **egyetlen műveletet sem hajt végre** a célgépen: nem indít és nem állít le semmit,
nem módosít beállítást. A lekérdezés a bejelentkezett felhasználó jogaival megy, **jelszót a
program nem tárol**.

**Publikus IP-címet nem fogad el.** Ez szándékos, nem korlátozás kedvéért: a modul
hitelesítve, rendszeresen, a háttérben kérdez le – ezt nem szabad az interneten át tenni, mert
egyrészt hitelesítő adatokat szivárogtatna ki, másrészt a saját gépedről induló, ismétlődő
vizsgálattá válna egy idegen rendszer ellen. Elfogadott tartományok: 10.0.0.0/8,
172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16, 127.0.0.0/8, 100.64.0.0/10 (CGNAT – Tailscale,
ZeroTier), IPv6 ULA és link-local.

**VPN-en át működik**, mert azon keresztül a távoli gép is belső címet kap. A program minden
körben újraellenőrzi a feloldott címet, tehát egy menet közben megváltoztatott DNS-bejegyzés
sem tudja kivinni a lekérdezést.

### Csoportok és értesítés

A gépek csoportokba rendezhetők (telephely, ügyfél, szerepkör), a lista eszerint csoportosít,
és a csoportok összecsukhatók.

Értesítés **csak állapotváltáskor** érkezik, és gépenként legfeljebb 15 percenként – így egy
billegő kapcsolat nem áraszt el. A helyreállásról is szól, ha kéred.

### Erőforrás-használat

- A figyelés `System.Threading.Timer`-rel fut, nem UI-időzítővel: tálcára téve is megy.
- Egyszerre legfeljebb **4 gép** vizsgálata párhuzamosan.
- A körök nem futhatnak egymásra: ha az előző még tart, a következő kimarad.
- A felület csak akkor frissül, ha látszik.
- A fülek csak az első megnyitásukkor épülnek fel.

---

## Szkriptépítő

A Szkriptek fül **Építő…** gombja parametrizált sablonokból állít össze kész, futtatható
szkriptet. Bal oldalt a sablon, középen az adatok, jobb oldalt élő előnézet. Az eredmény
felvehető a könyvtárba, kimenthető fájlba vagy vágólapra másolható.

| Sablon | Mit csinál |
|---|---|
| Active Directory – új erdő | első tartományvezérlő felhúzása, előfeltétel-ellenőrzéssel |
| Active Directory – további DC | meglévő tartományhoz újabb tartományvezérlő |
| DHCP kiszolgáló és hatókör | szerepkör, AD-engedélyezés, hatókör, opciók, kizárás |
| DNS – belső zóna | forward és reverse zóna, továbbítók |
| Fájlmegosztás jogosultságokkal | SMB megosztás + NTFS jogok, öröklés megszakításával |
| AD felhasználók CSV-ből | tömeges létrehozás, mintafájl generálásával |
| Linux szerver alapbeállítás | frissítés, felhasználó, SSH szigorítás, tűzfal (bash) |

Néhány döntés, ami a gyakorlatban számít:

**Jelszó soha nem kerül a generált fájlba.** A DSRM jelszót, a tartományi hitelesítést és az
AD-felhasználók kezdő jelszavát a szkript **futáskor kéri be** (`Read-Host -AsSecureString`).
Így a könyvtárban tárolt vagy e-mailben továbbküldött szkript kiszivárgása nem ad hozzáférést
semmihez.

**Az AD-sablon előbb ellenőriz, aztán telepít.** Megnézi, hogy a gép nem tartományvezérlő-e
már, hogy a hálózati kártya **fix IP-t** használ-e (DHCP-s DC-nél elromlik a DNS), hogy a
gépnév nem a gyári véletlen név-e (telepítés után már nem nevezhető át egyszerűen), és hogy
van-e elég szabad hely. Minden kérdésnél tovább lehet lépni, de tudni fogod, mit vállalsz.

**A DHCP-sablon az AD-engedélyezést sem hagyja ki.** Tartományban a DHCP kiszolgáló
engedélyezés (authorize) nélkül nem oszt címet – ez a leggyakoribb „miért nem működik"
kérdés. A sablon a biztonsági csoportokat is létrehozza, és eltünteti a Server Manager
„konfiguráció befejezése" figyelmeztetését.

**A Linux-sablon nem zár ki a szerverből.** Az SSH-szigorításnál a jelszavas belépés
szándékosan bekapcsolva marad, és a szkript a végén figyelmeztet: nyiss egy másik terminált
és próbáld ki az új portot, mielőtt kilépnél.

---

## Többplatformos szkripttár

| Típus | Kiterjesztés | Platform | Futtatás ezen a gépen |
|---|---|---|---|
| Batch | .bat / .cmd | Windows | igen |
| PowerShell | .ps1 | Windows | igen |
| PowerShell 7 | .ps1 | többplatformos | igen, ha van `pwsh.exe` |
| VBScript | .vbs | Windows | igen |
| Python | .py | többplatformos | igen, ha van `python.exe` |
| Bash | .sh | Linux / Unix | igen, ha van WSL |
| SQL | .sql | – | nem, csak tárolás |

Két részlet, ami nélkül ez nem működne megbízhatóan:

**Sorvégek típusonként.** A `cmd.exe` csak CRLF-fel dolgozza fel megbízhatóan a batch fájlokat,
a bash viszont **hibára fut a CR karaktertől** (`bad interpreter: /bin/bash^M`). A program
ezért típus szerint állítja a sorvégeket, akkor is, ha a szerkesztőben írod át.

**BOM nélküli UTF-8** a batch, bash és SQL fájloknál – a `cmd.exe` és a `bash` egyaránt
elszáll a bájtsorrend-jelzőtől.

Ha egy szkript itt nem futtatható, a program nem tűnteti el a Futtatás gombot szó nélkül:
megmondja, mi hiányzik (pl. „Bash szkript futtatásához WSL kell – telepítés: `wsl --install`"),
és a tárolás, szerkesztés, másolás, mentés továbbra is működik.

---

## Beépített szkriptkészlet

31 szkript hét kategóriában. Mind modern parancsokkal dolgozik – a `wmic` eszközt a Microsoft
eltávolította a Windows 11 újabb kiadásaiból. A rendszerszintű műveletek emelt jogosultsággal
futnak, a romboló hatásúak pedig megerősítést kérnek futás közben.

**Diagnosztika**

| Szkript | Mit csinál |
|---|---|
| Rendszerinformáció | gépnév, Windows verzió, jogosultság, IP, üzemidő |
| Lemezek szabad helye | meghajtónként méret, szabad hely, kihasználtság |
| Hibás eszközök és driverek | az Eszközkezelő sárga felkiáltójelei egy listában |
| Indításkor futó programok | registry, Indítópult és ütemezett feladatok |
| Kék halál előzmények | BugCheck és Kernel-Power 41 események, memóriaképek |
| Rendszerfájl-ellenőrzés | DISM + SFC a hivatalos sorrendben |

**Karbantartás**

| Szkript | Mit csinál |
|---|---|
| Ideiglenes fájlok törlése | TEMP mappák ürítése, felszabadított hellyel |
| Windows Update gyorsítótár visszaállítása | beragadt frissítések klasszikus javítása |
| Hálózati verem visszaállítása | Winsock és TCP/IP alaphelyzetbe |
| Nyomtatási sor törlése | beragadt nyomtatási feladatok |
| Lemezellenőrzés ütemezése | chkdsk a következő újraindításra |

**Leltár**

| Szkript | Mit csinál |
|---|---|
| Gépleltár CSV-be | CPU, RAM, alaplap, BIOS, sorozatszám egy sorban |
| Telepített szoftverek CSV-be | registryből, nem a lassú `Win32_Product`-tal |
| Legnagyobb fájlok | a 20 legnagyobb a felhasználói profilban |
| BitLocker állapot és kulcs | helyreállítási kulcsok kimentése |

**Hálózat**

| Szkript | Mit csinál |
|---|---|
| Hálózati gyorsteszt | adapterek, IP, majd átjáró → internet → DNS ellenőrzés |
| Megosztások és jogosultságok | SMB megosztások a hozzáférési jogokkal |
| Nyitott portok folyamatnévvel | a `netstat -anb` olvasható változata |
| Mentett Wi-Fi hálózatok és kulcsok | újratelepítés előtti dokumentáláshoz |

**Biztonság**

| Szkript | Mit csinál |
|---|---|
| Defender állapot | védelem, definíciók kora, észlelések |
| Helyi rendszergazdák és fiókok | ki a rendszergazda, sikertelen bejelentkezések |

Néhány részlet, ami a gyakorlatban számít:

- A **Wi-Fi kulcsok** kiolvasása a `netsh` XML exportján keresztül történik, nem a kimenet
  szövegének elemzésével – így területi beállítástól függetlenül működik. A jelszót tartalmazó
  ideiglenes fájlokat a szkript azonnal törli.
- A **helyi rendszergazdák** lekérdezése SID alapján keresi a csoportot (`S-1-5-32-544`), mert
  a neve nyelvfüggő (Administrators / Rendszergazdák).
- A **hálózati verem visszaállítása** figyelmeztet, hogy távoli munkameneten ne futtasd, mert
  megszakítja a kapcsolatot.
- A szkriptek **CRLF sorvégekkel** íródnak ki: a `cmd.exe` csak így dolgozza fel megbízhatóan
  a batch fájlokat.

Frissítéskor a program felismeri a saját, elavult szkriptjeit és lecseréli őket, az újakat
pedig felveszi. A te szkriptjeidhez nem nyúl – csak azokhoz, amiket ő hozott létre.

---

## Hardver fül

Bal oldalon a leltár (WMI-ből): operációs rendszer, üzemidő, gyártó/modell, alaplap, BIOS,
processzor (magok, szálak, órajel, gyorsítótár), memória (összes/használt + modulonként),
videokártya (a valódi VRAM méret a registryből, mert a WMI 4 GB fölött hazudik), fizikai
tárolók (méret, **SSD/HDD**, busz típus – SATA/NVMe/USB –, fordulatszám, egészségi állapot),
hálózati adapterek.

Jobb oldalon az élő szenzorok 2 másodpercenként frissülve: **hőmérséklet**, terhelés,
ventilátor-fordulatszám. A hőfok színkódolt – 70 °C fölött narancs, 85 °C fölött piros.

> A hőmérséklet-olvasás a LibreHardwareMonitor könyvtárral történik, ami kernel szintű
> hozzáférést igényel. **Normál jogosultsággal a szenzorok egy része nem látszik** – a
> program ilyenkor sárga sávban figyelmeztet. Teljes adatokért: `Program → Újraindítás
> rendszergazdaként`.

### Ha nincs CPU-hőmérséklet

Új gépeken gyakori, hogy csak az alaplapi (ACPI) és a lemez-hőfok látszik. Ennek szinte
mindig a **Memóriaintegritás (Core Isolation / HVCI)** az oka: ez blokkolja a szenzor-
illesztőket, mert azok szerepelnek a Microsoft sérülékeny-illesztő listáján. Windows 11-en
új gépen alapból be van kapcsolva.

A *Diagnosztika* gomb megmondja, mi az állapot, és két utat kínál:

1. **Marad bekapcsolva** – beéred az ACPI zónával és az NVMe hőfokkal. Laptopon az ACPI
   zóna gyakorlatilag a CPU hőfokát követi, tehát a lényeget látod.
2. **Kikapcsolod** – *Windows biztonság → Eszközbiztonság → Magelkülönítés → Memóriaintegritás*,
   majd újraindítás. Ez viszont **gyengíti a gép védelmét**, céges gépen pedig gyakran
   házirend írja elő a bekapcsolt állapotot – kérdezd meg a biztonsági felelőst.

A többi lehetséges ok (másik monitorozó program foglalja az illesztőt, vírusirtó blokkolja,
virtuális gép) szintén megjelenik a diagnosztikában.

A **Mentés szövegfájlba** gomb egy teljes gépleltárat ír ki – ügyfélnél hasznos.

### SMART – lemezállapot

A jobb alsó panel minden fizikai lemezt kiértékel, és **nem nyers számokat tol az arcodba**,
hanem kimondja, mi a baj. Négy állapot: Rendben / Figyelendő / Hibás / Nincs adat.

Két forrásból dolgozik. SATA lemezeknél a klasszikus SMART attribútumokat olvassa WMI-ből
(`MSStorageDriver_FailurePredictData`), a küszöbértékekkel együtt. NVMe meghajtóknál ez a WMI
osztály üres, ott a szenzorkönyvtár adja az élettartamot, hőfokot és az írt adatmennyiséget.

Amit külön figyel és magyarul megnevez:

| Attribútum | Mit jelent |
|---|---|
| 5 – Újraosztott szektorok | hibás szektorok, tartalékkal pótolva – ha nő, a lemez romlik |
| 197 – Függőben lévő szektorok | instabil, még nem olvasható szektorok → **adatvesztés kockázata** |
| 198 – Javíthatatlan szektorok | végleg olvashatatlan – az adat elveszett |
| 187 – Nem javítható hibák | a meghajtó feladta a javítást |
| 199 – UDMA CRC hibák | szinte mindig **kábelhiba**, nem a lemez hibás |
| 9 – Üzemidő | órában és napban is |
| 194 – Hőmérséklet | 60 °C fölött szól |
| SSD élettartam | 20% alatti maradék esetén figyelmeztet |

Ha egy attribútum a **gyártói küszöb alá esik**, az hivatalosan meghibásodásnak számít – ezt
külön kiemeli. Ugyanígy a meghajtó saját `PredictFailure` jelzését is.

> Ha egy lemeznél „Nincs adat" jelenik meg, a program megmondja a négy tipikus okot:
> nincs rendszergazdai jog; USB-házban lévő lemez (a híd nem továbbítja a SMART parancsokat);
> RAID vezérlő mögötti lemez; vagy virtuális gép.

---

## Tárhely fül

Minden csatolt meghajtó kártyaként, kihasználtsági sávval: használt/összes, szabad hely,
százalék. A sáv 80% fölött narancs, 92% fölött piros.

A **Mappák elemzése** a kijelölt meghajtó legnagyobb 40 mappáját számolja ki, mérettel és
százalékos aránnyal. Háttérszálon fut, bármikor megszakítható, a hozzáférés-megtagadott
mappákat átugorja, a junction/symlink pontokat pedig kihagyja (különben végtelen ciklus).
Dupla kattintás a soron megnyitja a mappát az Intézőben.

---

## Eseménynapló fül

Ez nem egyszerű naplólistázó. A nyers eseményeket **összevonja és rangsorolja**, hogy egy
pillantásra látszódjon, mire van a gépnek problémája.

**Felül egy mondatos ítélet** – pl. „⚠ 2 kritikus probléma – legfontosabb: Hibás szektor a
lemezen”, alatta a darabszámok. Ha nincs valódi baj, azt is kimondja.

**A lista csoportosít.** Az azonos forrásból, azonos azonosítóval érkező események egy sorba
kerülnek darabszámmal, így 400 ismétlődő bejegyzés nem nyomja el az egyetlen fontosat.
Alkalmazás- és szolgáltatáshibáknál az érintett program neve is kiderül, tehát a „chrome.exe
összeomlott” és a „sqlservr.exe összeomlott” külön sor marad.

**Rangsorolás súlyosság szerint, nem időrendben.** Egy darab lemezhiba előrébb kerül, mint
négyszáz DistributedCOM figyelmeztetés. A közismerten ártalmatlan eseményeket (DCOM 10016,
Schannel 36887, ESENT) a program külön „zaj” kategóriába teszi és leviszi a lista aljára –
ezek azok, amiket évek óta mindenki hiába kerget.

**Magyar magyarázat minden ismert eseményhez.** 41 beépített szabály fedi a gyakorlatban
számító eseményeket: lemez- és fájlrendszerhibák (Disk 7/11/51/153, Ntfs 55/98, storahci és
stornvme 129), hardverhibák (WHEA-Logger 1/17/18/19), váratlan leállások és kék halál
(Kernel-Power 41, BugCheck 1001, EventLog 6008), szolgáltatás-összeomlások (SCM
7000/7001/7009/7011/7023/7031/7034), alkalmazáshibák (Application Error 1000, Hang 1002,
.NET 1026), CPU-throttling, memóriakifogyás, hálózati és DHCP/DNS hibák, sikertelen
bejelentkezések. Mindegyiknél két blokk: **mit jelent** és **mit érdemes tenni**. Amihez nincs
szabály, ott az eredeti üzenet jelenik meg.

Beállítható az időszak (24 óra – 30 nap), a szint (csak hibák, vagy figyelmeztetésekkel), és
hogy mely naplókat nézze. A **Jelentés mentése** egy teljes, szöveges kiértékelést ír ki –
ügyfélnél átadható.

> A Rendszer és Alkalmazás napló normál joggal is olvasható. A **Biztonság naplóhoz
> rendszergazdai jog kell**; enélkül a program jelzi, hogy kihagyta. A Biztonság naplónál
> nem szint, hanem audit-failure szűrés fut, mert a sikertelen bejelentkezések technikailag
> „információ” szintű események.


---

## Hálózat fül

Alapból csak a **valódi hálózati adapterek** látszanak. A Windows ugyanis minden NDIS
szűrődriver-példányt (Npcap, QoS Packet Scheduler, WFP filter) és minden WAN Miniportot külön
interfészként mutat – ettől egy átlagos gépen 60 fölé megy az „adapterek" száma, és elveszik
benne a lényeg. A *Virtuális adapterek is* kapcsolóval ezek előhozhatók.

Az aktív adapterek elöl, a nem aktívak mögöttük és **alapból összecsukva** jelennek meg
(● / ○ jelöléssel). A csoportok fejlécére kattintva bármelyik ki- és becsukható.

Bal oldalon adapterenként: típus (vezetékes / Wi-Fi / VPN alagút), állapot, MAC cím,
kapcsolat sebessége, IPv4 és IPv6 címek maszkkal, átjáró, DNS-kiszolgálók, DHCP-kiszolgáló,
DNS utótag, valamint hogy a cím **DHCP-ből jött-e vagy fix**. Forgalmi számlálók és hibás
csomagok is látszanak – utóbbi kábel- vagy adapterhibára utal.

Jobb oldalon a figyelt portok, illetve átkapcsolva az összes aktív TCP kapcsolat távoli
címmel és állapottal.

---

## Licencek fül

**Windows termékkulcs kinyerése** két forrásból:

1. **Gyári (OEM) kulcs** – az alaplap firmware-ébe (ACPI/MSDM tábla) égetett kulcs, a
   `SoftwareLicensingService` osztályból. Gyári Windowsnál ez a gép „igazi" kulcsa, és
   újratelepítéskor a Windows automatikusan felismeri.
2. **Telepített kulcs** – a registry `DigitalProductId` bejegyzéséből visszafejtve
   (base-24 dekódolás). A program figyelmeztet is: **Windows 10/11 alatt ez gyakran nem a
   vásárolt kulcs**, hanem egy általános telepítési kulcs, mert az aktiválás digitális
   licenchez (Microsoft-fiók és hardverazonosító) kötött.

Emellett termékenként az **aktiválási állapot** (aktiválva / türelmi idő / nem aktivált),
a licenccsatorna (OEM, Retail, Volume) és a kulcs utolsó 5 karaktere – Office is idelátszik,
ha telepítve van. A Windows a teljes kulcsot csak a fenti két helyen tárolja
visszafejthetően, a többit szándékosan nem; a program ezt nem próbálja megkerülni.

Alul a **telepített szoftverek** listája (név, verzió, kiadó, telepítés dátuma) a registry
uninstall bejegyzéseiből – gyorsabb és megbízhatóbb, mint a `Win32_Product` lekérdezés,
ami mellesleg MSI-újrakonfigurálást is kiválthat.

---

## Távoli gép fül

Ugyanaz az adatgyűjtés hálózaton keresztül, **több gépre egyszerre**. A célgépeket soronként
kell felsorolni (gépnév vagy IP), a bejelentkezés `TARTOMÁNY\felhasználó` vagy
`.\Administrator` formában – üresen hagyva az aktuális fiókkal próbálkozik.

Kipipálható, mit gyűjtsön: rendszer és hardver, SMART lemezállapot, hálózati beállítások,
licenckulcsok, telepített szoftverek, eseménynapló-elemzés. Az eredmény egyetlen, mentheto
szöveges jelentés – ügyfélnél átadható vagy dokumentációba tehető.

A **Kapcsolat tesztelése** gomb gépenként külön visszajelez, és a tipikus hibákat magyarul
mondja meg (nem elérhető / hibás jelszó / DCOM jogosultság).

**Feltételek a célgépen:**

- a megadott fiók legyen **helyi rendszergazda** a célgépen
- tűzfalon engedélyezve: *Windows Management Instrumentation (WMI)* szabály
- az eseménynaplóhoz ezen felül: *Remote Event Log Management* szabály
- munkacsoportos (nem tartományi) gépnél a célgépen kell:
  `LocalAccountTokenFilterPolicy = 1` (DWORD) a
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` kulcs alatt

A hőmérséklet-szenzorok távolról **nem** olvashatók (kernel szintű hozzáférés kell hozzá,
ami nem megy WMI-n át) – a SMART attribútumok viszont igen.

---

## RAID kalkulátor fül

Nem csak kapacitást számol – azt is megmondja, mit kockáztatsz.

Megadod a RAID szintet (JBOD, 0, 1, 5, 6, 10, 50, 60), a lemezek számát és méretét, a hot
spare-ek számát, RAID 50/60-nál a csoportok számát. Kijön:

- **hasznos kapacitás decimálisan és binárisan is** – mert a gyártó 4 TB-ot ír, a Windows meg
  3,64 TiB-et mutat, és ezen évek óta mindenki meglepődik
- nyers kapacitás, redundanciára menő rész, hot spare tartalék, kihasználtság százalékban
- **hány lemez kiesését viseli el** – a RAID 10-nél külön kiírja, hogy garantáltan egyet, de
  szerencsés esetben többet is, mert ha egy tükörpáron belül esik ki kettő, a tömb elvész
- **írási büntetés** (RAID 5 = 4×, RAID 6 = 6×, RAID 10 = 2×) magyarázattal, hogy miért
- **újraépítés becsült ideje** a megadott sebességgel
- **olvasási hiba (URE) esélye újraépítés közben**

Ez utóbbi a leghasznosabb szám. A lemez adatlapján szereplő olvasásihiba-arányból (asztali
SATA-nál jellemzően 1 hiba / 10¹⁴ bit) kiszámolja, mekkora eséllyel akad el az újraépítés egy
javíthatatlan blokkon. Négy darab 4 TB-os asztali lemezből álló RAID 5-nél ez **~62%** – ezért
nem ajánlott ma már a RAID 5 nagy lemezekkel. RAID 6-nál ugyanez a hiba túlélhető, RAID 10-nél
pedig nagyságrenddel kisebb az esélye, mert csak a tükörpárt kell végigolvasni.

Alul egy **összehasonlító táblázat**: ugyanazzal a lemezkészlettel az összes RAID szint
kapacitása, hatékonysága, hibatűrése és írási büntetése egymás mellett – az érvénytelenek
(pl. RAID 6 három lemezzel) indoklással. Az aktuálisan választott szint ki van emelve.

Az eredmény vágólapra másolható, szöveges formában.

> A kalkulátor minden esetben kiírja, hogy **a RAID nem mentés**: nem véd véletlen törlés,
> zsarolóvírus, tűz vagy vezérlőhiba ellen.

---

## Exportálás

A menüsor **Exportálás** pontja (Ctrl+E) egy ablakot nyit, ahol kipipálható, mi kerüljön a
jelentésbe: rendszer és hardver, SMART lemezállapot, tárhely, hálózat, eseménynapló-elemzés,
licenckulcsok, telepített szoftverek, szkriptkönyvtár. Az eseménynapló időszaka külön
állítható.

A célmappa és a fájlnév szabadon megadható (Tallózás gombbal), a formátum **HTML** vagy
egyszerű szöveg. A program megjegyzi a legutóbbi célmappát.

A HTML jelentés **önálló fájl**: nincs benne külső kép, stíluslap vagy szkript-hivatkozás,
tehát e-mailben küldhető, és böngészőből nyomtatható (külön nyomtatási stílussal). Felül
összegzés áll – hibás lemez, kritikus naplóbejegyzés, majdnem tele meghajtó –, alatta a
részletek táblázatokban, a súlyosság szerint színezve. A beolvasott szövegek HTML-escape-elve
kerülnek bele, tehát egy furcsa karakter egy eszköznévben nem töri szét a jelentést.

---

## Beállítások

A menüsor **Beállítások** pontja, öt lapon:

**Elérési utak** – a szkriptek könyvtára és az exportálás alapmappája szabadon átállítható.
Ha a szkriptmappát megváltoztatod, a program felajánlja a meglévő fájlok átmásolását az új
helyre, hogy ne maradjanak árván. Hálózati vagy felhős (OneDrive, hálózati megosztás) mappa
is megadható – így több gép ugyanazt a szkriptkészletet használja.

**Indítás** – mindig rendszergazdaként induljon-e; megerősítés kérése szkript futtatása
előtt; új szkriptek alapértelmezett kódolása.

**Hardver** – élő szenzorok be/ki, frissítési időköz (1–60 mp), valamint a hőmérséklet
figyelmeztetési és kritikus küszöbe.

**Eseménynapló** – alapértelmezett időszak és szint, illetve az ismert ártalmatlan események
teljes elrejtése (alapesetben csak a lista aljára kerülnek).

**Távoli gépek** – alapértelmezett felhasználónév és a gyakran használt gépek listája, ami
automatikusan betöltődik a Távoli gép fülre.

> A **jelszót a program szándékosan nem tárolja**. Minden adatgyűjtéskor be kell írni – így
> a beállításfájl kiszivárgása nem ad hozzáférést az ügyfélgépekhez. Ezt automata teszt is
> ellenőrzi.

A beállítások a `settings.json` fájlba kerülnek az adatmappában, tehát hordozható módban is
együtt utaznak a programmal. A program a hibás vagy kézzel elrontott értékeket értelmes
határok közé vágja betöltéskor.

---

## Kódaláírás és a SmartScreen

Aláíratlan programnál a Windows ezt írja ki: *„A Windows megvédte a számítógépét –
Gyártó: Ismeretlen kiadó."* Ez a sor a digitális aláírásból jön, ezért a fájl metaadatainak
kitöltése önmagában nem szünteti meg.

### Mit érdemes tudni, mielőtt tanúsítványt veszel

| Megoldás | Ár | SmartScreen |
|---|---|---|
| Azure Artifact Signing | ~10 USD/hó | hírnév épül, az első letöltéseknél figyelmeztet |
| OV tanúsítvány (DigiCert, Sectigo) | 150–300 USD/év | ugyanaz |
| EV tanúsítvány | 400+ USD/év | **ugyanaz** – 2024 óta nincs azonnali átengedés |
| Önaláírt | ingyen | erős blokk, nyilvános terjesztésre alkalmatlan |

Két gyakori tévhitet érdemes eloszlatni:

**Az EV tanúsítvány már nem oldja meg.** Régen az EV azonnal átvitte az első letöltést is,
de ezt a viselkedést a Microsoft 2024-ben megszüntette. Az EV-vel aláírt fájl ma ugyanazon a
hírnévépítési folyamaton megy át, mint az OV-s – a felár pusztán a SmartScreen miatt nem
indokolt.

**Az önaláírt tanúsítvány semmit nem segít.** Sőt: erősebb blokkot vált ki, mint az aláírás
nélküli fájl. Csak ott van értelme, ahol a tanúsítványt csoportházirenddel vagy Intune-nal
megbízhatóként telepítik a gépekre – vállalati belső terjesztésnél tehát igen, nyilvánosan nem.

**Akkor mit ad az aláírás?** Két dolgot. Egyrészt az „Ismeretlen kiadó" helyére a **te neved**
kerül – ez ügyfélnél önmagában sokat számít. Másrészt a hírnév az aláíró identitáshoz tapad,
nem a fájl lenyomatához: aláíratlan fájlnál **minden egyes új build nulláról indul**, aláírva
viszont a következő verziók öröklik a felépült hírnevet.

Egyéni vállalkozóként egy dolgot érdemes előre tisztázni: az Azure Artifact Signingnál a
szervezetek közé az EU is beletartozik, magánszemélyként viszont csak USA és Kanada. Hogy egy
magyar e.v. melyik kategóriába esik, kérdezd meg tőlük, mielőtt fizetsz.


### Önaláírt tanúsítvány – kezelt gépekre

Ha a programot a saját és az ügyfeleid gépeire telepíted, az önaláírt tanúsítvány működő
megoldás: azokon a gépeken, ahol a tanúsítványodat megbízhatóként telepíted, **eltűnik a
figyelmeztetés és látszik a neved**. Nyilvános letöltéshez viszont nem alkalmas – ott a
Windows nem ismeri a kiállítót, és erősebb blokkot ad, mint aláírás nélkül.

**1. Tanúsítvány készítése** (egyszer, a saját gépeden):

```
make-cert.cmd
```

Létrehoz egy 5 évig érvényes, SHA-256 / RSA-3072 kódaláíró tanúsítványt, majd exportál két
fájlt a `cert` mappába:

| Fájl | Mi ez | Hova kerül |
|---|---|---|
| `kodalairas.pfx` | **privát kulcs** | csak a saját gépeden marad, jelszóval védve |
| `kodalairas.cer` | nyilvános rész | ez megy ki a célgépekre |

A `.pfx` fájlt ne töltsd fel sehova és ne tedd verziókövetésbe. A szkript az ujjlenyomatot
kiírja a `cert\thumbprint.txt` fájlba, innen a `sign.cmd` automatikusan felolvassa.

**2. Aláírás:**

```
sign.cmd
```

A `MODE=SELFSIGNED` az alapértelmezés, tehát nincs mit átállítani.

**3. Telepítés a célgépekre** – rendszergazdaként, a `.cer` fájllal együtt átmásolva:

```
install-cert.cmd
```

Ez a Megbízható legfelső szintű hitelesítésszolgáltatók és a Megbízható kiadók tárolóba
telepíti. Eltávolítás: `install-cert.cmd -Remove`.

Sok gépnél érdemesebb központilag: tartományban csoportházirenddel (*Számítógép konfigurációja
→ Házirendek → Windows-beállítások → Biztonsági beállítások → Nyilvános kulcsokra vonatkozó
házirendek*), Intune-nal pedig *Konfigurációs profilok → Megbízható tanúsítvány* profillal.

> Az ellenőrzés a saját gépeden „NotTrusted" állapotot mutathat, amíg nem telepítetted a
> tanúsítványt – ez ilyenkor normális, nem hiba.

### Az aláírás menete

1. Írd át a `src\DevToolbox.csproj` fájlban a `<Company>` sort, hogy **szó szerint** egyezzen
   a tanúsítvány subject nevével. (Ha eltér, a UAC és a fájl tulajdonságai ellentmondanak.)
2. Fordítsd újra: `build.cmd`
3. Nyisd meg a `sign.cmd`-t, állítsd be a `MODE` értékét (`PFX`, `STORE` vagy `AZURE`) és a
   hozzá tartozó néhány sort.
4. Futtasd: `sign.cmd`

A szkript SHA-256 lenyomattal és időbélyeggel ír alá – az időbélyeg azért kell, hogy az
aláírás a tanúsítvány lejárta után is érvényes maradjon. Végül ellenőrzi az aláírást és kiírja
a fájl SHA-256 lenyomatát, amit érdemes kitenni a letöltési oldalra.

A privát kulcs sehol nem kerül a szkriptbe: PFX módban futáskor kéri be a jelszót (a beírás
nem látszik), token és Azure módban pedig a kulcs el sem hagyja a hardvert.

### Amíg nincs tanúsítvány

Ha ügyfélgépre telepíted, a figyelmeztetés egy lépéssel megkerülhető: a letöltött fájlon
jobb gomb → Tulajdonságok → **Feloldás** (Unblock), vagy PowerShellből:

```powershell
Unblock-File .\DevToolbox.exe
```

Ez leveszi a fájlról a „másik gépről származik" jelölést (Mark of the Web), és a SmartScreen
nem kérdez rá. Hálózati megosztásról másolva eleve nem kerül rá ez a jelölés.

---

## Rendszerkövetelmény

- Windows 10 vagy 11, **64 bites**
- Nincs más függőség (a .NET runtime bele van fordítva az exe-be)
- PowerShell szkriptekhez a Windows beépített PowerShellje elég; a program `-ExecutionPolicy
  Bypass`-szal indítja, tehát a rendszer futtatási házirendje nem akadályozza
- Python szkriptekhez a `python.exe` kell a PATH-ban

---

## Újrafordítás

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o out
```

Vagy egyszerűen futtasd a `build.cmd`-t. Szükséges: .NET 10 SDK.

Forrásfájlok:

| Fájl | Mit csinál |
|---|---|
| `Program.cs` | belépési pont, mód eldöntése, `--uninstall` kapcsoló |
| `AppPaths.cs` | telepített/hordozható mód és adatútvonalak |
| `Installer.cs` | önmagát telepíti: másolás, parancsikon, registry uninstall bejegyzés |
| `ModeChooserForm.cs` | az indulási választóablak |
| `ScriptLibrary.cs` | szkript modell + JSON tároló |
| `ScriptRunner.cs` | futtatómotor, kimenet-átirányítás, kódolás |
| `ScriptsPage.cs` | Szkriptek fül |
| `ScriptEditForm.cs` | szkriptszerkesztő ablak |
| `HardwareInfo.cs` | WMI lekérdezések + LibreHardwareMonitor szenzorok |
| `HardwarePage.cs` | Hardver fül |
| `StoragePage.cs` | Tárhely fül + mappaelemző |
| `EventLogAnalyzer.cs` | eseménynapló olvasás, csoportosítás, magyar tudásbázis |
| `EventLogPage.cs` | Eseménynapló fül |
| `WmiContext.cs` | helyi/távoli WMI kapcsolat, hitelesítés |
| `SmartInfo.cs` | SMART attribútumok olvasása, dekódolás, értékelés |
| `NetworkInfo.cs` | hálózati adapterek, IP-beállítás, kapcsolatok |
| `NetworkPage.cs` | Hálózat fül |
| `LicenseInfo.cs` | termékkulcs-visszafejtés, aktiválás, szoftverleltár |
| `LicensePage.cs` | Licencek fül |
| `RemotePage.cs` | Távoli gép fül |
| `AppSettings.cs` | beállítások tárolása, érvényesítés |
| `SettingsForm.cs` | Beállítások ablak |
| `AboutForm.cs` | Névjegy ablak |
| `HtmlReport.cs` | adatgyűjtés + HTML és szöveges jelentés |
| `ExportForm.cs` | Exportálás ablak |
| `StorageInfo.cs` | meghajtók blokkolásmentes beolvasása |
| `RaidCalculator.cs` | RAID kapacitás, hibatűrés, újraépítés, URE kockázat |
| `RaidPage.cs` | RAID kalkulátor (Program menü) |
| `ScriptBuilder.cs` | szkriptsablonok és generálás |
| `ScriptBuilderForm.cs` | szkriptépítő ablak |
| `MonitorModel.cs` | figyelési adatmodell, belső hálózat ellenőrzés |
| `HealthChecker.cs` | gépenkénti állapotfelmérés |
| `MonitorService.cs` | háttérben futó figyelőszolgáltatás |
| `DashboardPage.cs` | Vezérlőpult |
| `HostEditForm.cs` | gép felvétele és szerkesztése |
| `MonitorSettingsForm.cs` | figyelés beállításai |
| `Branding.cs` | beágyazott embléma betöltése |
| `Winget.cs` | winget burkoló (lista, frissítés, helyi és távoli) |
| `AppUpdaterForm.cs` | Alkalmazás frissítő ablak |
| `FleetPage.cs` | Flotta fül – agentek és frissítéseik |
| `agent/Shared/Crypto.cs` | kriptográfiai mag (token, kulcspár, aláírás) |
| `agent/Shared/Protocol.cs` | agent–szerver protokoll |
| `agent/Server/` | Fleet Server (önálló szolgáltatás) |
| `agent/Agent/` | DevToolbox Agent (Windows-szolgáltatás) |
| `WindowsServices.cs` | szolgáltatás-katalógus és WMI módosítás |
| `ServicesForm.cs` | Windows szolgáltatások ablak |
| `HostRange.cs` | IP-tartomány feldolgozás, ping előszűrés |
| `MainForm.cs` | főablak, menü, állapotsor |

---

## Licenc

A szenzorolvasás a [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
könyvtárat használja, **MPL 2.0** licenc alatt. Ha a programot továbbadod vagy ügyfélnek
telepíted, ezt a licencet fel kell tüntetni (a Névjegy ablak megteszi).
