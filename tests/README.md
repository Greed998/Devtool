# DevToolbox – tesztek

Minden teszt a **valódi termékkódot** fordítja (`..\agent\…`), másolat nélkül. Kiadás előtt mindet le kell futtatni.

## Futtatás

| Hol | Parancs |
|---|---|
| Linux / WSL / macOS | `./run-tests.sh` (vagy `unit`, `e2e`, `ui`) |
| Windows | **rendszergazdai** PowerShell: `.\run-tests.ps1` (vagy `unit`, `e2e`, `ui`) |

Kell: **.NET 10 SDK**, Linuxon `curl`. A felületi részhez: `node` (Windowson a Laragon mellé telepítettet is megtalálja), és
`pip install playwright` + `python -m playwright install chromium`. Ha ezek hiányoznak, az a rész kimarad (jelzi).

A `run-tests.sh` ellenőrzött; a `run-tests.ps1` ugyanazt a menetet követi, de Windowson még nem futott.

Szabad portok kellenek: 19743, 19380 (teszt-FleetServer), 18090 (teszt-Központ), 18099, 19998, 19999 (álszerverek).

## Biztonság

- A tesztek ideiglenes mappában dolgoznak. Az agent mappáját a `DEVTOOLBOX_AGENT_DIR` irányítja át, így egy
  valódi agent beállításait **nem** érintik.
- Az élő tesztek a valódi agentkódot is futtatják: Windowson ez olvas (winget-lista, WMI, Windows Update keresés),
  de nem telepít semmit – a tesztfeladatok nem létező csomagra szólnak.

## Mit ellenőriz

### Logikai tesztek (`unit\`)
| Teszt | Tartalom |
|---|---|
| Security | kulcstár-átalakítás a kulcs megtartásával, kulcscsere-lánc, hamis lánc elutasítása, régi ujjlenyomat, mentés verzióváltáskor, belépés-korlátozás, napló jelszó nélkül |
| Policies | karbantartási ablak (éjfélen átnyúló is), egyszeri kiadás, csoport-hatókör, újraindítás csak függő újraindításnál, azonosító-ellenőrzés (injektálás), `winget list` angol/magyar kimenet |
| Users | szerepkörök, telephely-hatókör, fő admin védelme, letiltás, megmaradás |
| MetricStore | idősor, legfrissebb érték, gépek elkülönülése, órás átlagolás, megőrzés, törlés |
| Alerts | küszöbök: egymást követő mérések, megszűnés, gép-specifikus szabály, operátorok, kikapcsolás; 1.5.0: csoport- és telephely-hatókör, „bármelyik” minta (disk.*.usedpct), a Központ szabályai a telephelyen, módosításkor / törléskor az állapot is törlődik |
| Groups | csoport-műveletek (létrehozás, tagság, átnevezés a hivatkozásokkal, törlés: a szűkített szabályok kikapcsolnak), a Központ csoport-sora (régi telephelynek nem megy le), programtár csak változáskor, metrika-katalógus mintái |
| HubStore | Központ: regisztráció, egyszer használatos token, aláírt jelentkezés mindkét irányban, visszajátszás/elavulás, gyakoriság, feladat lefelé, visszavonás |
| HubAccess | ki éri el a Központ adminját (proxy mögötti valódi kliens-IP, hamisított fejlécek) |
| MetricModel | mérési modell és mérés-beállítás átvitele |
| ServerCore | beléptető token lejárata és gépszám-korlátja |
| Catalog | teljes winget-tár: index olvasása (hibás azonosító kimarad), letöltés egy helyi kamu CDN-ről, első letöltés = alap, új programok döntésre (a repóban / elutasítottak nem), hibás / túl kicsi letöltés nem írja felül, döntések (felvétel, elutasítás a csomagokból is, visszavétel, mindet elutasítom), lapozás, keresés |
| KeyRotation | erősebb kulcs (P-384) és a régi kliensek kompatibilitása, szerverkulcs-lánc (60 szem, előzmény), agent-kulcscsere: kettős aláírás, visszajátszás, birtoklás-bizonyítás, megerősítésig érvényes régi kulcs, elveszett visszaigazolás, másik gép kulcsa, visszavont gép; erős szerverkulcs csak ha minden agent / telephely 1.4.5+; token letiltása |
| Janus | Janus-küldő (Központ): helyi kamu Janus, a Janus PHP-ellenőrzésével azonos HMAC (kulcs, időbélyeg, nonce), géplista, riasztás, megszűnés, telephely-kiesés, ismeretlen gép, hibaüzenet, újrapróbálás, kikapcsolás, cím-ellenőrzés |
| Notify | kiesés-riasztás (türelmi idő, újraindítás, befagyasztás, csoportszűrő), riasztásnapló visszaolvasása, CSV (BOM, pontosvessző, képlet-befecskendezés, verzió szövegként, napi metrika), e-mail: DPAPI, beállítás-ellenőrzés, SMTP egy helyi kamu szerverrel (AUTH, tárgy- és törzskódolás, hibaágak) |

### Élő tesztek (`e2e\`) – valódi FleetServer / Központ, valódi HTTP
| Teszt | Tartalom |
|---|---|
| Monitoring | setup, token, regisztráció, aláírt jelentkezés metrikákkal, grafikon-adat, küszöb és riasztás, mérés-beállítás az agentig, kiesés-riasztás beállítása, CSV-jelentések letöltése, a valódi agentkód végigfut és P-384-es kulcsra cserél (a kulcslistában megjelenik) |
| Security | aláírt válasz; **támadó szerver** aláírás nélkül és saját kulccsal – nem fut le feladat; kulcscsere újraregisztrálás nélkül, régi telepítőcsomag; belépés-korlátozás; napló |
| AgentUpdate | önfrissítési ajánlat az aláírt részben, letöltés és lenyomat-ellenőrzés, hamis lenyomat és túlméretes fájl elutasítása, kikapcsolás |
| Setup | a telepítővarázsló kapcsolat-tesztje minden hibaesetre, a webes ZIP előtöltése |
| Management | katalógus, programtár-döntések (elutasított, felvétel névvel, néző nem dönthet), csoportok, telepítés/eltávolítás/Windows Update/újraindítás gépre és csoportra, injektálás, néző szerepkör tiltása, automatikus szabály |
| HubAccess | Központ: kívülről az admin nem ad oldalt és belső IP-t, a `/hub/` nyitva, proxy-hiba érthető üzenete |
| HubDial | telephely betárcsázása, Központ-kulcscsere követése, kiesés-riasztás, e-mail beállítás (a jelszó nem jön vissza, titkosítva tárolódik, naplóba nem kerül), CSV-jelentések, belépés-korlátozás, napló |

### Felület (`ui\`)
| Teszt | Tartalom |
|---|---|
| szkriptek | a kész oldalak szkriptje hibátlan (node, `ui\check_scripts.js`), minden gomb létező függvényt hív, a szkriptek együtt sem ütköznek (globális nevek), a szerepkör bekerül a szkriptbe |
| monitorozás + oldalsáv | böngészőben: grafikonok, küszöb, minden menüpont, keskeny nézet, JavaScript-hiba nincs |
| gépkezelés | böngészőben: Windows Update, telepítés, eltávolítás, újraindítás, csoportok, katalógus, szabály |
| Központ | böngészőben: oldalsáv, Biztonság, Beállítások, Új telephely |

## Új teszt

`unit\<Név>\<Név>.csproj` – a termékfájlokat `$(A)/…` útvonallal veszi fel (lásd a meglévőket).
A program a végén `MINDEN TESZT ATMENT`-et ír, ha minden ellenőrzés sikeres – a futtató ezt figyeli.
