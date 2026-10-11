# DevToolbox Fleet – biztonsági és optimalizálási audit (1.5.10)

Kódszintű biztonsági átvizsgálás (secure code review, pentesztelői szemlélettel) és
teljesítmény-áttekintés. **Élő** penetration test nem része (ahhoz futó Server/HUB kell a te
környezetedben); ez a forráskód elemzése a `greed998/Devtool` repón.

**Összkép: a kód biztonsági alapjai ERŐSEK.** Egy valódi (közepes súlyú) XSS-rés van, és néhány
alacsony/hardening tétel. Adatvesztést, auth-megkerülést, SQL- vagy parancs-injekciót nem találtam.

---

## Összegző táblázat

| # | Terület | Súlyosság | Állapot |
|---|---|---|---|
| F1 | Tárolt XSS: `esc()` nem escape-eli a `'`-t (onclick JS-sztring) | **Közepes** | ✅ **Javítva** (jesc) |
| F2 | winget argumentum: záró `\` kitörhet az idézőjelből | Alacsony | ✅ **Javítva** |
| F3 | Session-cookie `Secure` flag hiányzik | Alacsony | ✅ NPM-ben beállítva (Központ) |
| F4 | ~~`DeleteDayFile(day)` path-traversal~~ | — | Nem találat (már `TryDay`-védett) |
| O1 | Változás-detektálás nagy string + GetHashCode allokáció | Info | ✅ **Javítva (1.5.11)** |
| S1 | SNMP: TimeTicks-túlcsordulás, rate-limit megkerülés, cache-korlátok | Közepes | ✅ **Javítva (1.5.11)** |
| O2 | MetricStore DB-zár hosszú karbantartás alatt | Info | Elfogadható |

**Javítva:** F1 (jesc a JS-sztringes helyeken, mindkét felületen + `esc()` escapeli a `'`-t), F2 (a `WingetId.IsValid`
tiltja a záró backslash-t), O1 (allokáció-mentes hash), S1 (SNMP-megerősítés, lásd lent). Mind az 1.5.11-ben.

---

## Ami ERŐS (ezeket ellenőriztem, rendben vannak)

- **Hitelesítés.** Globális session-kapu: minden `/web/api/*` hívás előtt érvényes session kell
  (különben 401). A session **HMAC-SHA256-tal aláírt** token (user|szerep|lejárat), 8 órás
  lejárattal, **konstans idejű** ellenőrzéssel (`FixedTimeEquals`) – nem hamisítható, nincs timing-rés.
- **Jelszó.** PBKDF2-SHA256, egyedi sóval, **210 000 iteráció** (OWASP-ajánlás fölött), konstans idejű
  összehasonlítás. A jelszó sehol nem naplózódik (a „pass" mezőnevet az audit kihagyja).
- **Login-zárolás.** IP-nként 5 hibás próbálkozás → 15 perc zárolás; a zárolás alatti próbálkozás
  meghosszabbítja. Auditálva.
- **Cookie.** `HttpOnly` (JS nem olvassa → XSS nem lopja a sessiont) + `SameSite=Strict` (CSRF ellen).
- **Szerepkörök.** Minden érzékeny végpont `NeedKezelo()` / `NeedAdmin()` mögött; a veszélyes műveletek
  (terminál, szolgáltatás-vezérlés, azonnali reboot) **admin**-jogot kérnek.
- **SQL-injekció.** Nincs: minden lekérdezés paraméterezett (`AddWithValue`), nincs string-konkatenáció.
- **Parancs-injekció.** Minden `Process.Start` `UseShellExecute=false` (nincs shell-értelmezés). A winget
  azonosítót `WingetId.IsValid` szűri (nincs idézőjel/vezérlő/kezdő kötőjel). A webes terminál
  `-EncodedCommand` (base64) → nincs idézőjel-/escape-gond.
- **Agent↔Server és Server↔HUB.** Minden üzenet aláírt (ECDSA), nonce-szal replay-védett; az agent a
  választ CSAK aláírás-ellenőrzés után használja – közbeékelődő nem csempészhet be feladatot. Heti
  automatikus kulcscsere, láncolt kulcsátadás, ujjlenyomat-ellenőrzés.
- **Fájlfeltöltés (metrika).** Méretkorlát, SHA-256 egyezés, zip-bomba-védelem (kicsomagolt méretkorlát),
  SQLite-validálás („minden sor az adott napra"), telephely-azonosító `^[A-Za-z0-9_-]{1,80}$` (nincs
  path-traversal), a helyi fájl csak aláírt nyugta után törlődik.
- **SSRF.** A Janus-kapcsolat `AllowAutoRedirect=false` + csak TLS 1.2/1.3 – egy proxy/login-oldal nem
  nyeli el a riasztást, átirányítással nem lehet belső címre terelni.

---

## Találatok részletesen

### F1 — Tárolt XSS: `esc()` nem escape-eli az aposztrófot (KÖZEPES)

**Hol:** `agent/Server/WebUi.cs:801` és `agent/Hub/HubWebUi.cs:670`
```js
function esc(s){return String(s??'').replace(/[&<>"]/g, …)}   // a ' (aposztróf) kimarad
```
A felületen a user-adat gyakran **egyszeres idézőjeles JS-sztringbe** kerül egy `onclick`-ban, pl.:
```js
onclick="svcDo('${esc(s.name)}','servicestart')"
onclick="setRole('${esc(name)}', …)"
```
Mivel az `esc()` a `'`-t nem kódolja, egy `'` karakter a `s.name`/`name` értékben **kitöri a JS-sztringet**,
és tetszőleges JS fut az admin böngészőjében a gomb kattintásakor (a `&#39;` sem segít, mert a böngésző a
HTML-attribútumot előbb visszafejti, csak utána értelmezi a JS-t).

**Kihasználhatóság:** a mezőnek `'`-t kell tartalmaznia, és egy alacsonyabb jogú szereplőnek kell tudnia
beállítani. Reális vektorok: **Windows-szolgáltatásnév** (egy gép lokális adminja létrehozhat `a'onerror…`
nevű szolgáltatást), illetve bármely, kevésbé privilegizált forrásból jövő szöveg (pl. programkérés-indoklás,
ARP-programnév). A gépnév nem érintett (a Windows-gépnév nem tartalmazhat `'`-t). Az áldozat a felületet néző
**admin**, tehát ez gép-lokális adminból Fleet-adminná való eszkaláció lehet.

**Javaslat (két réteg, együtt):**
1. A user-adatot inline eseménykezelőbe tevő helyeken **ne** nyers nevet ágyazz be, hanem egy indexet, és a
   nevet JS-ben, a listából keresd ki (adat-attribútum + eseménykezelő, vagy `window._lista[idx].name`). Ez
   teljesen megszünteti a vektort.
2. Vagy vezess be egy `jesc()` segédet a JS-sztring kontextushoz: előbb `\`→`\\`, `'`→`\'`, utána a jelenlegi
   HTML-`esc()`. (Pusztán a `'`→`&#39;` az `esc()`-ben a többi, egyszeres idézőjeles **HTML-attribútumot**
   védi, de az onclick JS-sztringet nem – ott az 1. vagy 2. pont kell.)

**✅ JAVÍTVA (2. megközelítés):** a `WebUi.cs` és `HubWebUi.cs` kapott egy `jesc()` segédet, és az összes
`onclick="fn('${esc(…)}')` JS-sztringes hely `jesc`-re váltott (12 + 8 = 20 hely). Az `esc()` ráadásul most
az aposztrófot is escape-eli (`&#39;`), a többi egyszeres idézőjeles attribútum védelmére.

### F2 — winget argumentum: záró backslash (ALACSONY, hardening)

`WingetId.IsValid` tiltja az idézőjelet és a kezdő kötőjelet, de a **backslash**-t nem. A winget hívás egy
idézőjeles argumentum: `install --id "{Target}" …`. Egy `Valami\` végű azonosító a Windows
argumentum-feldolgozójában `\"`-ként eszkébelt idézőjelnek számíthat, és további winget-kapcsolók
„beinjektálhatók" (shell nincs, tehát tetszőleges parancs nem, csak winget-kapcsoló). Kihasználáshoz
hitelesített kezelő/admin kell (aki amúgy is privilegizált), ezért alacsony. **Javaslat:** a `WingetId.IsValid`
tiltsa a backslash-t is (érvényes winget-azonosító amúgy sem tartalmaz `\`-t).

**✅ JAVÍTVA:** a `WingetId.IsValid` (Protocol.cs, a szerver ÉS az agent is ezt hívja) mostantól tiltja a **záró**
backslash-t. (Fontos: a validátort az **ARP-azonosítók** is használják, amelyek jogosan tartalmaznak *belső*
backslash-t, pl. `ARP\Machine\X86\…` – ezért csak a záró `\` tiltott, ami a tényleges injekció-vektor. Unit teszt fedi.)

### F3 — Session-cookie `Secure` flag (ALACSONY, környezetfüggő)

A `SetCookie` `HttpOnly; SameSite=Strict`, de **nincs `Secure`**. A telephelyi szerver a belső hálón HTTP-n
fut (ott a `Secure` megakadályozná a cookie küldését), a Központ viszont HTTPS mögött (NPM). **Javaslat:** a
Központnál (HTTPS) érdemes `Secure`-t is tenni a cookie-ra (pl. ha a kérés `IsSecureConnection`), hogy a
session-token soha ne mehessen titkosítatlanul.

### F4 — `DeleteDayFile(day)` path-traversal (NEM TALÁLAT)

Ellenőrzés közben kiderült: a `MetricStore.DeleteDayFile` **már tartalmazza** a `if (!TryDay(day, out _)) return;`
védelmet az elején (MetricStore.cs:554). Tehát `metrics-{day}.db` útvonal csak érvényes `yyyy-MM-dd` napból épül
– nincs path-traversal. Ez nem is volt találat.

---

## Optimalizálási megfigyelések

### O1 — Változás-detektálás: nagy string + GetHashCode (INFO)
A `HubClient` a programlista/komponensek/szolgáltatások változását így nézi:
`string.Concat(lista.Select(... + ";")).GetHashCode()`. Ez minden betárcsázáskor, gépenként egy
potenciálisan **nagy ideiglenes stringet** allokál pusztán a hasheléshez. Sok gép × sok program esetén ez
felesleges GC-nyomás. **Javaslat:** inkrementális hash (`System.IO.Hashing` / `HashCode` streamelve), string
összefűzés nélkül. (Funkcionálisan jó, csak takarékosabb lehet.)

### O2 — MetricStore DB-zár (INFO, elfogadható)
A metrika-műveletek `lock(_lock)` alatt futnak (egy SQLite-kapcsolat miatt szükséges). A hosszabb
karbantartás (`CloseDays`) 7 napra korlátozott körönként, hogy a zár ne tartson sokáig – ez jó kompromisszum.
Nagyobb terhelésnél megfontolható egy külön, csak-olvasó kapcsolat a lekérdezésekhez (a napi fájlok már most
külön, csak-olvasó kapcsolaton mennek).

### Általános
- A webfelület minden frissüléskor teljes HTML-stringet épít – ez az architektúra sajátja, a 15 mp-es
  frissítés nem küldi a nagy listákat (már optimalizált, 1.3.1).
- A mérés-lekérdezések több forrásból (napi fájlok + metrics.db) időrendben, sorlimittel – rendben.

---

## S1 — SNMP-fogadó (1.5.11: átnézve, fuzzolva, javítva)
A BER-olvasó eleve szigorú volt (határozott hossz, max. 3 hosszbájt, csomaghatár-ellenőrzés, OID-túlcsordulás-védelem,
max. 200 varbind, minden kivétel elkapva). Az átnézés és **3,6 millió fuzzolt csomag** (csonkolás, bitfordítás,
hosszmező-rontás, beszúrás, véletlen bájtok; `tests/unit/SnmpHardening`, `SNMP_FUZZ=N`) alapján javítva:
- **TimeTicks-túlcsordulás** (a fuzzing találta, ~6% a mintán): a 32 bitesnél nagyobb érték `OverflowException`-t
  dobott, és az egész trap elveszett. Most a hibás érték nyersen látszik.
- **Terheléskorlát megkerülése** (közepes): a forrás-tábla 5000 forrás fölött teljesen törlődött; hamisított UDP-
  forrásokkal (a megengedett belső tartományok nagyok) a forrásonkénti korlát nullázható volt. Új globális korlát
  (2000 csomag / 10 mp), a tábla nem ürül ki.
- **Visszajátszás-tábla**: korlátos méret, legfeljebb 10 mp-enként takarít (eddig minden csomagnál O(n), korlátlan).
- **Kulcs-gyorsítótár**: csak sikeres HMAC után tárol (eddig hitelesítés nélkül kiüríthető volt).
- **Megjelenítés**: hexa csak az első 249 bájtból. **IPv4-tartalék** IPv6 nélküli gépen; socket-szivárgás javítva.
Eredmény: 0 generikus (ellenőrizetlen) parser-hiba, lineáris futásidő (~220 000 csomag/mp).

---

*Készült a 1.5.10 kódbázisra; a javítások az 1.5.11-ben jelentek meg.*
