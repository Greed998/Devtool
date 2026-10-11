# DevToolbox Fleet 1.5.10 – tesztelési terv és kód-audit

Ez a dokumentum két részből áll:
1. **Kód-audit** – mit néztem át a forrásban, mit találtam (és mit javítottam).
2. **Éles tesztforgatókönyv** – lépésről lépésre, a valódi Windows / Server↔HUB környezetben.

A felhő-környezetben a `.exe` **nem** készül el (Windows-only: WinForms, WMI, P/Invoke), és
az éles alagút/terminál/újraindítás sem próbálható. A build, a `tests/run-tests.ps1` és az
alábbi éles tesztek a **te Windows-gépeiden** futnak.

---

## 1. Kód-audit (1.5.10)

| Terület | Eredmény |
|---|---|
| **Újraindítás (reboot)** | **Hiba megtalálva és javítva.** Az `InitiateShutdownW` a `NativeShutdown.Restart`-ban `SHUTDOWN_FORCE_SELF` nélkül hívódott; késleltetéssel (delay>0) ez `ERROR_INVALID_PARAMETER (87)`-et ad, így az **ütemezett** újraindítás sosem történt meg (az azonnali, delay=0, ezért működött). Javítva: `SHUTDOWN_FORCE_SELF` hozzáadva. Plusz a feladat-hiba most a típust + kilépési kódot + a kimenet elejét is az `agent.log`-ba írja. |
| **Szolgáltatás-kezelő UI** | **Új funkció.** A terminál helyett önálló felület (telephely-szerver és Központ) a Windows-szolgáltatások indítására/leállítására/újraindítására. Admin-jog, aláírt feladat-csatorna, auditálva, `ServiceName` validátor, unit teszt. |
| **Metrika-átvitel (Server↔HUB)** | **Átnézve – robusztus, nincs hiba.** A feltöltés gzip + SHA-256 + a telephely kulcsával aláírt fejléc; a helyi fájl **csak** a Központ aláírt, ehhez a kéréshez kötött, egyező nyugtája után törlődik. A fogadás méret- és hash-ellenőrzött, zip-bomba-védett, SQLite-validált („minden sor az adott napra"), a végleges helyre tétel UTÁN törli a saját 5-perces másolatot. Az archiválás ZIP-then-delete (nincs adatvesztés). A `HubMetricsFromTs` mutató csak sikeres válasz után lép. |
| **Admin terminál** | **Átnézve – rendben.** PowerShell `-EncodedCommand` (base64 UTF-16) → nincs parancssori injekció; 120 mp timeout; 200 KB kimenet-korlát; aláírt csatorna + gépenkénti engedély (alapból KI) + admin-jog. |
| **WireGuard** | A protokoll-/kulcs-/peer-logika fordul és a `HubStore` unit teszt fedi; a tényleges alagút (natív `wireguard.dll`) **csak éles 2-végpontú környezetben** ellenőrizhető (lásd lenti forgatókönyv). |
| **Teszt-harness** | **Javítva.** Az 1.5.9 (WireGuard) óta a `HubStore.cs` a `WgNet` típust használja, de a `HubStore.cs`-t fordító unit tesztek projektfájljába nem került be a forrása → build-hiba. A `WireGuard.cs` hozzáadva mindegyikhez; 8 teszt újra fut. |

**Megjegyzés (nem hiba, nem változtattam):** a HUB `HubMetricArchive.Maintain` a `MaintainDir`-t
`detailDays = keepDays`-szel hívja, így egy ritka esetben (ha egy teljes felbontású nap a telephely
ritkítása előtt töltődne fel) a HUB nem ritkítja órásra – csak tárhely-kérdés.

---

## 2. Éles tesztforgatókönyv

### 2.0 Build és automata tesztek (fejlesztői gép)
- [ ] `.NET 10 SDK` telepítve.
- [ ] `build.cmd` lefut hibátlanul → `out\fleet\Fleet.exe`, `out\agent\DevToolboxAgent.exe`, `out\standalone\DevToolbox.exe`, `AIO\Dev-<dátum>.zip`.
- [ ] Az `AIO\Dev-<dátum>.zip` tartalma: a 3 `.exe` + `FRISSITES.txt` + `FEJLESZTESI-NAPLO.md` + `README.md` + `DevToolbox-forras.zip`.
- [ ] (Opcionális) `sign.cmd <exe>` mindhárom exére, majd újracsomagolás.
- [ ] **Rendszergazdai** PowerShellből: `tests\run-tests.ps1 unit` → „MINDEN TESZT ATMENT" (köztük az új `ServiceControl`). A böngészős részhez `run-tests.ps1 all`.

### 2.1 Telepítés / frissítés sorrend
A `FRISSITES.txt` szerint: **Fleet.exe a Központra → telephelyekre**; az agentek a szerverről maguktól frissülnek.
- [ ] Központ frissítése (Beállítások → Központ frissítése), majd a telephelyek (a Központból „Szerver frissítése", vagy a telephely webfelületén).
- [ ] A gépek a következő jelentkezéskor 1.5.10-es agentre frissülnek (ha az automata agent-frissítés be van kapcsolva).

### 2.2 ⭐ Újraindítás – a JAVÍTOTT hiba (ezt mindenképp)
Ez volt a fő hiba: az **ütemezett** újraindítás eddig nem történt meg.
- [ ] Telephely-szerver → egy gép → **Újraindítás…** → „5 perc múlva" → **a gép 5 perc múlva TÉNYLEGESEN újraindul**.
- [ ] A gép `C:\ProgramData\DevToolbox-Agent\agent.log`-jában: „Feladat kész (Reboot, siker)…", **nincs** „kód: 87".
- [ ] „azonnal (figyelmeztetés nélkül!)" is indít (ez eddig is ment).
- [ ] Ugyanez a **Központból** (gép → Újraindítás fül → 5 perc).
- [ ] Ha valamiért mégsem indul: az `agent.log` most már kiírja a feladat típusát, a kilépési kódot és a kimenet elejét – küldd el nekem azt a néhány sort.

### 2.3 Szolgáltatás-kezelő UI (új)
**Telephely-szerveren:**
- [ ] Gép adatlap → **Szolgáltatások…** gomb → megjelenik a gép szolgáltatás-listája (kereshető).
- [ ] **Admin**ként: egy ártalmatlan szolgáltatáson (pl. `Print Spooler` / `Spooler`) **Leállít** → a gép következő jelentkezésekor leáll; **Indít** → elindul; **Újraindít** → újraindul.
- [ ] Az állapot (fut/leállt) a gép következő jelentkezése után frissül (nyisd újra a panelt).
- [ ] **Kezelő** (nem admin) joggal: a lista látszik, de a vezérlő gombok nincsenek / „admin jog kell".
- [ ] A művelet megjelenik a Műveleti naplóban (Biztonság → napló): „szolgáltatás újraindítása – <név> – <gép>".

**Központban:**
- [ ] Gép kezelése → **Szolgáltatások** fül → a lista megjelenik (az 1.5.10-es telephely a betárcsázáskor felküldi; ha üres: „a telephely még nem küldte fel", várj egy betárcsázást).
- [ ] Adminként Indít/Leállít/Újraindít → a feladat a telephelyen át jut el az agenthez (telephely betárcsázása → gép jelentkezése).

### 2.4 WireGuard-alagút (éles, 2 végpont)
- [ ] Központ (Beállítások → WireGuard): UDP 51820 a routeren/NPM Streams-ben a Központ gépére továbbítva; címtartomány (10.77.0.0/24); a kívülről hívott végpont (pl. `fleet.poltoli.hu:51820`).
- [ ] Telephely (Beállítások → Központ kapcsolat): az alagút **felépül** (állapot: handshake időbélyeggel).
- [ ] Szigorú mód: a telephely, amely egyszer az alagúton tárcsázott, utána **csak** azon – ellenőrizd, hogy a Központ admin felülete az alagútról nem érhető el.
- [ ] Helyi javítás: végpont felülírása / kulcscsere / újrakapcsolás a telephely oldalán.

### 2.5 Admin terminál (éles, LocalSystem)
- [ ] A gép-panelen a **Terminál** alapból „(ki)"; adminként kapcsold **be** erre a gépre.
- [ ] Parancs (pl. `whoami` → `nt authority\system`, vagy `Get-Service | ? Status -eq Running`) → pár másodperc múlva jön a kimenet.
- [ ] Minden parancs a Műveleti naplóba kerül.
- [ ] Nem admin / ki nem kapcsolt gépen nem fut.

### 2.6 Metrika-átvitel és archiválás (éles / hosszabb táv)
Ez időben elhúzódó (napok), de ellenőrizhető:
- [ ] A telephely a megőrzése (alap 30 nap) után a lezárt napi fájlt a Központba tölti; a `fleet-server.log`-ban: „Mérési adatok (ÉÉÉÉ-HH-NN) a Központban – a helyi napi fájl törölve".
- [ ] A Központban a gép grafikonja a régi időszakra is rajzol (a feltöltött fájlból).
- [ ] Nincs dupla adat a feltöltött napra (a Központ saját 5-perces másolata törlődött).
- [ ] 90 nap után a Központban ZIP-archívum, 180 nap után törlés (alapértékek; gyorsteszthez a Beállítások → Adatmegőrzés értékei átmenetileg csökkenthetők).

### 2.6b Webfelület telefonon / tableten (1.5.12)
- [ ] Telefonon (pl. iPhone, Android) a szerver és a Központ webfelülete: felül logó + ☰; a ☰ lenyitja a menüt, menüpontra koppintva bezárul.
- [ ] Nincs vízszintes görgetés az oldalon (csak a széles táblázatok görgethetők a saját dobozukban).
- [ ] Gépre koppintva az adatlapjára görget; a gombok tördelődnek, mind elérhető (Szolgáltatások…, Terminál, Újraindítás…).
- [ ] Felugró ablakok (pl. Újraindítás, Szolgáltatások) kiférnek; a Terminál beviteli sora látszik; gépeléskor nem nagyít rá az iPhone.
- [ ] Tableten (álló és fekvő) a Programtár / gépkezelés kategória-menüje felül, vízszintes sávban.

### 2.7 Hol nézz naplót
- Gép (agent): `C:\ProgramData\DevToolbox-Agent\agent.log`
- Telephely-szerver: `C:\ProgramData\DevToolbox-Fleet\logs\fleet-server.log`, `audit.log`, `alerts.log`
- Központ: `C:\ProgramData\DevToolbox-Fleet-Hub\logs\fleet-hub.log`, `audit.log`

---

*Készült az 1.5.10 fejlesztéséhez. A ⭐-os pont (újraindítás) a fő, megerősített javítás – azt mindenképp érdemes élesben visszaigazolni.*
