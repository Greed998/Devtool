namespace DevToolbox;

public sealed record ScriptSample(
    string Name,
    ScriptKind Kind,
    string Category,
    string Description,
    string Content,
    bool RunAsAdmin = false);

/// <summary>
/// A programmal szallitott szkriptek. Mind modern parancsokkal dolgozik:
/// a "wmic" eszkozt a Microsoft eltavolitotta a Windows 11 ujabb kiadasaibol.
/// A romboló muveletek megerositest kernek, es RunAsAdmin jelolest kapnak.
/// </summary>
public static class ScriptSamples
{
    public const int Version = 5;

    /// <summary>A mintak kategoriai - a frissiteskor ezekben keresunk.</summary>
    public static readonly string[] Categories =
        { "Minta", "Diagnosztika", "Karbantartás", "Leltár", "Hálózat", "Biztonság",
          "Kiszolgáló", "Linux", "Telepítés" };

    public static readonly ScriptSample[] All =
    {
        // ================================================================ DIAGNOSZTIKA

        new("Rendszerinformáció", ScriptKind.Batch, "Diagnosztika",
            "Gépnév, Windows verzió, jogosultság, IP-címek – csak beépített parancsokkal.",
            """
            @echo off
            chcp 65001>nul
            echo === Rendszer ===
            echo   Gépnév:        %COMPUTERNAME%
            echo   Felhasználó:   %USERDOMAIN%\%USERNAME%
            echo   Architektúra:  %PROCESSOR_ARCHITECTURE%
            echo   Logikai magok: %NUMBER_OF_PROCESSORS%
            ver
            echo.
            echo === Rendszergazdai jog ===
            net session >nul 2>&1 && (echo   igen) || (echo   nem)
            echo.
            echo === IP-címek ===
            ipconfig | findstr /C:"IPv4"
            echo.
            echo === Üzemidő ===
            powershell -NoProfile -Command "$b=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime; $u=(Get-Date)-$b; 'Utolsó indítás: {0:yyyy.MM.dd HH:mm}  ({1} nap {2} óra)' -f $b,$u.Days,$u.Hours"
            """),

        new("Lemezek szabad helye", ScriptKind.PowerShell, "Diagnosztika",
            "Meghajtónként méret, szabad hely és kihasználtság, CIM lekérdezéssel.",
            """
            # Meghajtók kihasználtsága – CIM lekérdezés, modern Windowson is működik
            Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
              Select-Object DeviceID, VolumeName,
                @{n='Méret GB';  e={[math]::Round($_.Size/1GB,1)}},
                @{n='Szabad GB'; e={[math]::Round($_.FreeSpace/1GB,1)}},
                @{n='Foglalt %'; e={if($_.Size){[math]::Round(($_.Size-$_.FreeSpace)/$_.Size*100,1)}}} |
              Format-Table -AutoSize
            """),

        new("Hibás eszközök és driverek", ScriptKind.PowerShell, "Diagnosztika",
            "Az Eszközkezelőben sárga felkiáltójellel jelölt eszközök kilistázása.",
            """
            Write-Host '=== Hibás vagy hiányzó illesztőprogramú eszközök ===' -ForegroundColor Cyan
            $bad = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
                   Where-Object { $_.Status -ne 'OK' }

            if ($bad) {
                $bad | Sort-Object Class |
                  Select-Object Status, Class, FriendlyName, InstanceId |
                  Format-Table -AutoSize -Wrap
                Write-Host ("Összesen: {0} db" -f @($bad).Count) -ForegroundColor Yellow
            } else {
                Write-Host 'Minden eszköz rendben van.' -ForegroundColor Green
            }
            """),

        new("Indításkor futó programok", ScriptKind.PowerShell, "Diagnosztika",
            "Automatikusan induló programok a registryből és az Indítópult mappákból.",
            """
            Write-Host '=== Registry és Indítópult bejegyzések ===' -ForegroundColor Cyan
            Get-CimInstance Win32_StartupCommand |
              Select-Object Name, Location, User, Command |
              Sort-Object Location, Name | Format-Table -AutoSize -Wrap

            Write-Host '=== Ütemezett feladatok bejelentkezéskor / indításkor ===' -ForegroundColor Cyan
            Get-ScheduledTask -ErrorAction SilentlyContinue |
              Where-Object { $_.State -ne 'Disabled' -and
                             $_.Triggers.CimClass.CimClassName -match 'Logon|Boot' } |
              Select-Object TaskPath, TaskName, State |
              Sort-Object TaskPath | Format-Table -AutoSize
            """),

        new("Kék halál előzmények", ScriptKind.PowerShell, "Diagnosztika",
            "BugCheck események és a memóriakép fájlok listája – összeomlások vizsgálatához.",
            """
            Write-Host '=== BugCheck (kék halál) események ===' -ForegroundColor Cyan
            $ev = Get-WinEvent -FilterHashtable @{
                      LogName='System'
                      ProviderName='Microsoft-Windows-WER-SystemErrorReporting'
                      Id=1001
                  } -MaxEvents 20 -ErrorAction SilentlyContinue

            if ($ev) { $ev | Select-Object TimeCreated, Message | Format-List }
            else      { Write-Host 'Nincs ilyen esemény a naplóban.' -ForegroundColor Green }

            Write-Host '=== Váratlan leállások (Kernel-Power 41) ===' -ForegroundColor Cyan
            Get-WinEvent -FilterHashtable @{LogName='System'; Id=41} -MaxEvents 10 -ErrorAction SilentlyContinue |
              Select-Object TimeCreated, Id, LevelDisplayName | Format-Table -AutoSize

            Write-Host '=== Memóriakép fájlok ===' -ForegroundColor Cyan
            Get-ChildItem "$env:SystemRoot\Minidump" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 10 |
              Select-Object LastWriteTime, Name, @{n='KB';e={[math]::Round($_.Length/1KB)}} |
              Format-Table -AutoSize
            """),

        new("Rendszerfájl-ellenőrzés (SFC + DISM)", ScriptKind.Batch, "Diagnosztika",
            "A sérült rendszerfájlok javításának hivatalos módja. Hosszú, akár 20–30 perc.",
            """
            @echo off
            chcp 65001>nul
            echo ============================================================
            echo   Rendszerfájl-ellenőrzés és javítás
            echo   Ez akár 20-30 percig is eltarthat. Ne szakítsd meg.
            echo ============================================================
            echo.
            echo [1/3] DISM: a rendszerkép állapotának ellenőrzése...
            DISM /Online /Cleanup-Image /ScanHealth
            echo.
            echo [2/3] DISM: javítás a Windows Update forrásból...
            DISM /Online /Cleanup-Image /RestoreHealth
            echo.
            echo [3/3] SFC: védett rendszerfájlok ellenőrzése...
            sfc /scannow
            echo.
            echo Kész. Ha a SFC javított valamit, indítsd újra a gépet.
            echo Részletes napló: %SystemRoot%\Logs\CBS\CBS.log
            """, RunAsAdmin: true),

        // ================================================================ KARBANTARTÁS

        new("Ideiglenes fájlok törlése", ScriptKind.PowerShell, "Karbantartás",
            "A TEMP mappák ürítése, felszabadított hellyel. Megerősítést kér.",
            """
            # Csak a két TEMP mappát érinti. A használatban lévő fájlokat átugorja.
            $targets = @($env:TEMP, "$env:SystemRoot\Temp")

            function Get-FolderSize($p) {
                if (-not (Test-Path $p)) { return 0 }
                (Get-ChildItem $p -Recurse -File -Force -ErrorAction SilentlyContinue |
                 Measure-Object Length -Sum).Sum
            }

            Write-Host '=== Jelenlegi méret ===' -ForegroundColor Cyan
            $total = 0
            foreach ($t in $targets) {
                $s = Get-FolderSize $t
                $total += $s
                '{0,-40} {1,10:N1} MB' -f $t, ($s/1MB)
            }
            Write-Host ('Összesen: {0:N1} MB' -f ($total/1MB)) -ForegroundColor Yellow

            $ok = Read-Host 'Törölhetem? (i/n)'
            if ($ok -notmatch '^(i|I|y|Y)') { Write-Host 'Megszakítva.'; return }

            foreach ($t in $targets) {
                if (-not (Test-Path $t)) { continue }
                Get-ChildItem $t -Force -ErrorAction SilentlyContinue |
                  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            }

            $after = 0
            foreach ($t in $targets) { $after += Get-FolderSize $t }
            Write-Host ('Felszabadítva: {0:N1} MB' -f (($total-$after)/1MB)) -ForegroundColor Green
            Write-Host 'A használatban lévő fájlok megmaradtak – ez normális.' -ForegroundColor DarkGray
            """),

        new("Windows Update gyorsítótár visszaállítása", ScriptKind.Batch, "Karbantartás",
            "Beragadt vagy hibára futó frissítések klasszikus javítása.",
            """
            @echo off
            chcp 65001>nul
            echo === Windows Update gyorsítótár visszaállítása ===
            echo.
            echo Szolgáltatások leállítása...
            net stop wuauserv
            net stop bits
            net stop cryptsvc
            echo.
            echo Mappák félretétele...
            if exist "%SystemRoot%\SoftwareDistribution.old" rd /s /q "%SystemRoot%\SoftwareDistribution.old"
            if exist "%SystemRoot%\System32\catroot2.old" rd /s /q "%SystemRoot%\System32\catroot2.old"
            ren "%SystemRoot%\SoftwareDistribution" SoftwareDistribution.old
            ren "%SystemRoot%\System32\catroot2" catroot2.old
            echo.
            echo Szolgáltatások indítása...
            net start cryptsvc
            net start bits
            net start wuauserv
            echo.
            echo Kész. A Windows Update újra letölti a szükséges adatokat.
            echo A régi mappák .old néven megmaradtak, később törölhetők.
            """, RunAsAdmin: true),

        new("Hálózati verem visszaállítása", ScriptKind.Batch, "Karbantartás",
            "Winsock és TCP/IP alaphelyzetbe. FIGYELEM: megszakítja a hálózatot, újraindítást igényel.",
            """
            @echo off
            chcp 65001>nul
            echo ============================================================
            echo   Hálózati verem visszaállítása
            echo.
            echo   FIGYELEM: a hálózati kapcsolat megszakad, és a végén
            echo   ÚJRAINDÍTÁS szükséges. Távoli munkameneten NE futtasd!
            echo ============================================================
            echo.
            choice /C IN /N /M "Folytatod? (I/N): "
            if errorlevel 2 goto :vege

            echo.
            echo DNS gyorsítótár ürítése...
            ipconfig /flushdns
            echo ARP tábla törlése...
            arp -d *
            echo NetBIOS név-gyorsítótár...
            nbtstat -R
            echo Winsock visszaállítása...
            netsh winsock reset
            echo TCP/IP visszaállítása...
            netsh int ip reset
            echo IP-cím megújítása...
            ipconfig /release
            ipconfig /renew
            echo.
            echo Kész. INDÍTSD ÚJRA A GÉPET.
            goto :eof

            :vege
            echo Megszakítva.
            """, RunAsAdmin: true),

        new("Nyomtatási sor törlése", ScriptKind.Batch, "Karbantartás",
            "Beragadt nyomtatási feladatok eltávolítása – a leggyakoribb helpdesk-hívás.",
            """
            @echo off
            chcp 65001>nul
            echo === Nyomtatási sor törlése ===
            echo.
            echo Nyomtatásisor-kezelő leállítása...
            net stop spooler
            echo Várakozó feladatok törlése...
            del /Q /F /S "%SystemRoot%\System32\spool\PRINTERS\*.*" >nul 2>&1
            echo Nyomtatásisor-kezelő indítása...
            net start spooler
            echo.
            echo Kész. A várakozó nyomtatási feladatok törlődtek.
            """, RunAsAdmin: true),

        new("Lemezellenőrzés ütemezése", ScriptKind.Batch, "Karbantartás",
            "chkdsk ütemezése a rendszermeghajtóra a következő újraindításra.",
            """
            @echo off
            chcp 65001>nul
            echo === Lemezellenőrzés (chkdsk) ===
            echo.
            echo Elobb a gyors, csak olvasó ellenőrzés fut le:
            chkdsk C:
            echo.
            echo ============================================================
            echo   A javító vizsgálat (/f /r) a KÖVETKEZŐ ÚJRAINDÍTÁSKOR fut,
            echo   és nagy lemeznél több órát is igénybe vehet.
            echo ============================================================
            choice /C IN /N /M "Ütemezzem a javító vizsgálatot? (I/N): "
            if errorlevel 2 goto :vege

            echo Y| chkdsk C: /f /r
            echo.
            echo Ütemezve. A vizsgálat a következő indításkor lefut.
            goto :eof

            :vege
            echo Nem lett ütemezve.
            """, RunAsAdmin: true),

        // ================================================================ LELTÁR

        new("Gépleltár CSV-be", ScriptKind.PowerShell, "Leltár",
            "Egysoros gépleltár (CPU, RAM, alaplap, BIOS, sorozatszám) a Dokumentumok mappába.",
            """
            $os   = Get-CimInstance Win32_OperatingSystem
            $cs   = Get-CimInstance Win32_ComputerSystem
            $bios = Get-CimInstance Win32_BIOS
            $cpu  = Get-CimInstance Win32_Processor | Select-Object -First 1

            $row = [PSCustomObject]@{
                Gépnév          = $env:COMPUTERNAME
                Gyártó          = $cs.Manufacturer
                Modell          = $cs.Model
                Sorozatszám     = $bios.SerialNumber
                Processzor      = $cpu.Name
                Magok           = $cpu.NumberOfCores
                Szálak          = $cpu.NumberOfLogicalProcessors
                'RAM GB'        = [math]::Round($cs.TotalPhysicalMemory/1GB,1)
                Windows         = $os.Caption
                Build           = $os.BuildNumber
                Architektúra    = $os.OSArchitecture
                BIOS            = "$($bios.Manufacturer) $($bios.SMBIOSBIOSVersion)"
                Tartomány       = $cs.Domain
                Rögzítve        = (Get-Date -Format 'yyyy.MM.dd HH:mm')
            }

            $row | Format-List

            $out = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "gepleltar_$env:COMPUTERNAME.csv"
            $row | Export-Csv $out -NoTypeInformation -Encoding UTF8 -Delimiter ';'
            Write-Host "Mentve: $out" -ForegroundColor Green
            Write-Host 'A pontosvesszős elválasztás miatt az Excel magyar területi beállítással is helyesen nyitja meg.' -ForegroundColor DarkGray
            """),

        new("Telepített szoftverek CSV-be", ScriptKind.PowerShell, "Leltár",
            "A registry uninstall bejegyzéseiből – gyorsabb és megbízhatóbb, mint a Win32_Product.",
            """
            # A Win32_Product lekérdezés lassú, és MSI-újrakonfigurálást is kiválthat,
            # ezért közvetlenül a registryből olvasunk.
            $paths = @(
                'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
            )

            $apps = Get-ItemProperty $paths -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -and -not $_.SystemComponent } |
                Select-Object @{n='Szoftver';e={$_.DisplayName}},
                              @{n='Verzió';  e={$_.DisplayVersion}},
                              @{n='Kiadó';   e={$_.Publisher}},
                              @{n='Telepítve';e={$_.InstallDate}} |
                Sort-Object Szoftver -Unique

            $apps | Format-Table -AutoSize
            Write-Host ("Összesen: {0} db" -f @($apps).Count) -ForegroundColor Yellow

            $out = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "szoftverek_$env:COMPUTERNAME.csv"
            $apps | Export-Csv $out -NoTypeInformation -Encoding UTF8 -Delimiter ';'
            Write-Host "Mentve: $out" -ForegroundColor Green
            """),

        new("Legnagyobb fájlok", ScriptKind.PowerShell, "Leltár",
            "A 20 legnagyobb fájl a felhasználói profilban – helyhiány gyors okának keresésére.",
            """
            $root = $env:USERPROFILE
            Write-Host "Vizsgált mappa: $root" -ForegroundColor Cyan
            Write-Host 'Ez eltarthat egy percig...' -ForegroundColor DarkGray

            Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
              Sort-Object Length -Descending | Select-Object -First 20 |
              Select-Object @{n='MB';e={[math]::Round($_.Length/1MB,1)}},
                            @{n='Módosítva';e={$_.LastWriteTime.ToString('yyyy.MM.dd')}},
                            FullName |
              Format-Table -AutoSize
            """),

        new("BitLocker állapot és helyreállítási kulcs", ScriptKind.PowerShell, "Leltár",
            "Titkosítás állapota kötetenként, a helyreállítási kulcsokkal. Mentsd ki újratelepítés előtt!",
            """
            Write-Host '=== BitLocker kötetek ===' -ForegroundColor Cyan
            $vols = Get-BitLockerVolume -ErrorAction SilentlyContinue
            if (-not $vols) {
                Write-Host 'Nincs BitLocker, vagy nincs jogosultság a lekérdezéshez.' -ForegroundColor Yellow
                return
            }

            $vols | Select-Object MountPoint, VolumeStatus, ProtectionStatus,
                                  EncryptionPercentage, EncryptionMethod |
              Format-Table -AutoSize

            Write-Host '=== Helyreállítási kulcsok ===' -ForegroundColor Cyan
            foreach ($v in $vols) {
                $keys = $v.KeyProtector | Where-Object KeyProtectorType -eq 'RecoveryPassword'
                foreach ($k in $keys) {
                    Write-Host ("  Kötet: {0}" -f $v.MountPoint)
                    Write-Host ("  Azonosító: {0}" -f $k.KeyProtectorId)
                    Write-Host ("  Kulcs:     {0}" -f $k.RecoveryPassword) -ForegroundColor Yellow
                    Write-Host ''
                }
            }
            Write-Host 'Ezt a kulcsot alaplapcsere vagy BIOS-frissítés előtt mindenképp mentsd ki.' -ForegroundColor DarkGray
            """, RunAsAdmin: true),

        // ================================================================ HÁLÓZAT

        new("Hálózati gyorsteszt", ScriptKind.PowerShell, "Hálózat",
            "Aktív adapterek, IP-beállítás és lépcsőzetes kapcsolat-ellenőrzés.",
            """
            Write-Host '=== Aktív adapterek ===' -ForegroundColor Cyan
            Get-NetAdapter | Where-Object Status -eq 'Up' |
              Select-Object Name, InterfaceDescription, LinkSpeed, MacAddress |
              Format-Table -AutoSize

            Write-Host '=== IP-beállítás ===' -ForegroundColor Cyan
            $cfg = Get-NetIPConfiguration | Where-Object { $_.IPv4Address }
            $cfg | Select-Object InterfaceAlias,
                     @{n='IPv4';   e={$_.IPv4Address.IPAddress}},
                     @{n='Átjáró'; e={$_.IPv4DefaultGateway.NextHop}},
                     @{n='DNS';    e={($_.DNSServer | Where-Object AddressFamily -eq 2).ServerAddresses -join ', '}} |
              Format-Table -AutoSize

            Write-Host '=== Kapcsolat lépésről lépésre ===' -ForegroundColor Cyan
            $gw = ($cfg | Select-Object -First 1).IPv4DefaultGateway.NextHop
            if ($gw) {
                $r = Test-Connection $gw -Count 2 -Quiet -ErrorAction SilentlyContinue
                Write-Host ("  Átjáró ({0}):    {1}" -f $gw, $(if($r){'OK'}else{'NEM válaszol'})) -ForegroundColor $(if($r){'Green'}else{'Red'})
            }
            $r = Test-Connection 1.1.1.1 -Count 2 -Quiet -ErrorAction SilentlyContinue
            Write-Host ("  Internet (IP):   {0}" -f $(if($r){'OK'}else{'NEM elérhető'})) -ForegroundColor $(if($r){'Green'}else{'Red'})
            $r = $null; try { $r = Resolve-DnsName microsoft.com -ErrorAction Stop } catch {}
            Write-Host ("  Névfeloldás:     {0}" -f $(if($r){'OK'}else{'HIBA'})) -ForegroundColor $(if($r){'Green'}else{'Red'})
            """),

        new("Megosztások és jogosultságok", ScriptKind.PowerShell, "Hálózat",
            "SMB megosztások listája a hozzáférési jogokkal – auditáláshoz vagy költözéshez.",
            """
            Write-Host '=== Megosztások ===' -ForegroundColor Cyan
            Get-SmbShare | Select-Object Name, Path, Description | Format-Table -AutoSize

            Write-Host '=== Jogosultságok (rejtett megosztások nélkül) ===' -ForegroundColor Cyan
            foreach ($s in Get-SmbShare | Where-Object Name -notlike '*$') {
                Write-Host ("--- {0}  ({1})" -f $s.Name, $s.Path) -ForegroundColor Yellow
                Get-SmbShareAccess -Name $s.Name |
                  Select-Object AccountName, AccessControlType, AccessRight |
                  Format-Table -AutoSize
            }

            Write-Host '=== Aktív SMB kapcsolatok ===' -ForegroundColor Cyan
            Get-SmbSession -ErrorAction SilentlyContinue |
              Select-Object ClientComputerName, ClientUserName, NumOpens |
              Format-Table -AutoSize
            """),

        new("Nyitott portok folyamatnévvel", ScriptKind.PowerShell, "Hálózat",
            "Melyik program figyel melyik porton – a netstat -anb olvasható változata.",
            """
            Write-Host '=== Figyelt TCP portok ===' -ForegroundColor Cyan
            Get-NetTCPConnection -State Listen |
              Select-Object LocalAddress, LocalPort,
                @{n='Folyamat'; e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}},
                @{n='PID';      e={$_.OwningProcess}} |
              Sort-Object LocalPort | Format-Table -AutoSize

            Write-Host '=== Kiépült kapcsolatok ===' -ForegroundColor Cyan
            Get-NetTCPConnection -State Established |
              Select-Object RemoteAddress, RemotePort,
                @{n='Folyamat'; e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} |
              Sort-Object Folyamat | Format-Table -AutoSize
            """),

        new("Mentett Wi-Fi hálózatok és kulcsok", ScriptKind.PowerShell, "Hálózat",
            "A gépen tárolt Wi-Fi jelszavak – újratelepítés előtti dokumentáláshoz.",
            """
            # A netsh XML exportja területi beállítástól függetlenül működik,
            # szemben a kimenet szövegének elemzésével.
            $dir = Join-Path $env:TEMP ('wlan_' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force -Path $dir | Out-Null

            try {
                netsh wlan export profile key=clear folder="$dir" | Out-Null

                $list = Get-ChildItem $dir -Filter *.xml -ErrorAction SilentlyContinue | ForEach-Object {
                    $x = [xml](Get-Content $_.FullName)
                    [PSCustomObject]@{
                        Hálózat     = $x.WLANProfile.name
                        Kulcs       = $x.WLANProfile.MSM.security.sharedKey.keyMaterial
                        Hitelesítés = $x.WLANProfile.MSM.security.authEncryption.authentication
                    }
                }

                if ($list) { $list | Sort-Object Hálózat | Format-Table -AutoSize }
                else { Write-Host 'Nincs mentett Wi-Fi profil, vagy nincs jogosultság.' -ForegroundColor Yellow }
            }
            finally {
                # A kiexportált fájlok jelszót tartalmaznak - azonnal töröljük.
                Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
            }
            """, RunAsAdmin: true),

        // ================================================================ BIZTONSÁG

        new("Defender állapot", ScriptKind.PowerShell, "Biztonság",
            "Valós idejű védelem, definíciók kora, utolsó vizsgálat, észlelt fenyegetések.",
            """
            $s = Get-MpComputerStatus -ErrorAction SilentlyContinue
            if (-not $s) { Write-Host 'A Defender nem érhető el ezen a gépen.' -ForegroundColor Yellow; return }

            Write-Host '=== Védelem állapota ===' -ForegroundColor Cyan
            [PSCustomObject]@{
                'Valós idejű védelem'     = $s.RealTimeProtectionEnabled
                'Felhőalapú védelem'      = $s.MAPSReporting
                'Manipulációvédelem'      = $s.IsTamperProtected
                'Definíció verzió'        = $s.AntivirusSignatureVersion
                'Definíció kora (nap)'    = $s.AntivirusSignatureAge
                'Utolsó gyorsvizsgálat'   = $s.QuickScanEndTime
                'Utolsó teljes vizsgálat' = $s.FullScanEndTime
                'Motor verzió'            = $s.AMEngineVersion
            } | Format-List

            if ($s.AntivirusSignatureAge -gt 3) {
                Write-Host 'A vírusdefiníciók elavultak. Frissítés: Update-MpSignature' -ForegroundColor Red
            }

            Write-Host '=== Utolsó észlelések ===' -ForegroundColor Cyan
            $t = Get-MpThreatDetection -ErrorAction SilentlyContinue |
                 Sort-Object InitialDetectionTime -Descending | Select-Object -First 10
            if ($t) {
                $t | Select-Object InitialDetectionTime, ThreatID, Resources | Format-Table -AutoSize -Wrap
            } else {
                Write-Host 'Nincs észlelt fenyegetés.' -ForegroundColor Green
            }
            """),

        new("Helyi rendszergazdák és fiókok", ScriptKind.PowerShell, "Biztonság",
            "Ki tagja a rendszergazda csoportnak, és mely fiókok aktívak.",
            """
            # SID alapján keresünk, mert a csoport neve nyelvfüggő (Administrators / Rendszergazdák).
            $grp = Get-LocalGroup | Where-Object { $_.SID.Value -eq 'S-1-5-32-544' }

            Write-Host "=== A(z) '$($grp.Name)' csoport tagjai ===" -ForegroundColor Cyan
            Get-LocalGroupMember -Group $grp.Name -ErrorAction SilentlyContinue |
              Select-Object Name, ObjectClass, PrincipalSource | Format-Table -AutoSize

            Write-Host '=== Helyi fiókok ===' -ForegroundColor Cyan
            Get-LocalUser | Select-Object Name, Enabled, LastLogon,
                                          PasswordLastSet, PasswordExpires |
              Sort-Object Name | Format-Table -AutoSize

            Write-Host '=== Sikertelen bejelentkezések (utolsó 10) ===' -ForegroundColor Cyan
            Get-WinEvent -FilterHashtable @{LogName='Security'; Id=4625} -MaxEvents 10 -ErrorAction SilentlyContinue |
              Select-Object TimeCreated, @{n='Fiók';e={$_.Properties[5].Value}},
                            @{n='Forrás';e={$_.Properties[19].Value}} |
              Format-Table -AutoSize
            """, RunAsAdmin: true),
        // ================================================================ KISZOLGÁLÓ

        new("AD – állapot és replikáció", ScriptKind.PowerShell, "Kiszolgáló",
            "Tartomány, erdő, tartományvezérlők, replikáció és jelszóházirend egy futtatással.",
            """
            Import-Module ActiveDirectory -ErrorAction Stop

            Write-Host '=== Tartomány ===' -ForegroundColor Cyan
            Get-ADDomain | Select-Object DNSRoot, NetBIOSName, DomainMode,
                @{n='PDC';e={$_.PDCEmulator}}, @{n='RID mester';e={$_.RIDMaster}} | Format-List

            Write-Host '=== Erdő ===' -ForegroundColor Cyan
            Get-ADForest | Select-Object Name, ForestMode, SchemaMaster, DomainNamingMaster,
                @{n='Tartományok';e={$_.Domains -join ', '}} | Format-List

            Write-Host '=== Tartományvezérlők ===' -ForegroundColor Cyan
            Get-ADDomainController -Filter * |
              Select-Object Name, Site, IPv4Address, OperatingSystem, IsGlobalCatalog, IsReadOnly |
              Format-Table -AutoSize

            Write-Host '=== Replikáció ===' -ForegroundColor Cyan
            repadmin /replsummary

            Write-Host '=== Jelszóházirend ===' -ForegroundColor Cyan
            Get-ADDefaultDomainPasswordPolicy |
              Select-Object MinPasswordLength, PasswordHistoryCount, MaxPasswordAge,
                            LockoutThreshold, ComplexityEnabled | Format-List

            Write-Host '=== Gyors DC-diagnosztika ===' -ForegroundColor Cyan
            dcdiag /q
            """, RunAsAdmin: true),

        new("AD – fiókok auditálása", ScriptKind.PowerShell, "Kiszolgáló",
            "Inaktív, letiltott és soha le nem járó jelszavú fiókok – a tipikus AD-takarítás alapja.",
            """
            Import-Module ActiveDirectory -ErrorAction Stop
            $days = 90
            $limit = (Get-Date).AddDays(-$days)

            $all = Get-ADUser -Filter * -Properties LastLogonDate, PasswordLastSet,
                                                   PasswordNeverExpires, Enabled, Description

            Write-Host "=== Inaktív fiókok ($days napja nem léptek be) ===" -ForegroundColor Cyan
            $all | Where-Object { $_.Enabled -and ($_.LastLogonDate -lt $limit -or -not $_.LastLogonDate) } |
              Select-Object Name, SamAccountName, LastLogonDate, Description |
              Sort-Object LastLogonDate | Format-Table -AutoSize

            Write-Host '=== Soha le nem járó jelszó ===' -ForegroundColor Cyan
            $all | Where-Object { $_.Enabled -and $_.PasswordNeverExpires } |
              Select-Object Name, SamAccountName, PasswordLastSet | Format-Table -AutoSize

            Write-Host '=== Letiltott fiókok ===' -ForegroundColor Cyan
            $all | Where-Object { -not $_.Enabled } |
              Select-Object Name, SamAccountName, LastLogonDate | Format-Table -AutoSize

            Write-Host '=== Tartományi rendszergazdák ===' -ForegroundColor Cyan
            Get-ADGroupMember 'Domain Admins' -Recursive |
              Select-Object Name, SamAccountName, objectClass | Format-Table -AutoSize

            $out = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "ad_fiokok_$env:COMPUTERNAME.csv"
            $all | Select-Object Name, SamAccountName, Enabled, LastLogonDate,
                                 PasswordLastSet, PasswordNeverExpires |
              Export-Csv $out -NoTypeInformation -Encoding UTF8 -Delimiter ';'
            Write-Host "Teljes lista mentve: $out" -ForegroundColor Green
            """, RunAsAdmin: true),

        new("DHCP – hatókörök és bérletek", ScriptKind.PowerShell, "Kiszolgáló",
            "Hatókörök kihasználtsága, kiosztott bérletek és fenntartások.",
            """
            Import-Module DhcpServer -ErrorAction Stop

            Write-Host '=== Hatókörök ===' -ForegroundColor Cyan
            $scopes = Get-DhcpServerv4Scope
            $scopes | Select-Object ScopeId, Name, StartRange, EndRange, State, LeaseDuration |
              Format-Table -AutoSize

            Write-Host '=== Kihasználtság ===' -ForegroundColor Cyan
            foreach ($s in $scopes) {
                $st = Get-DhcpServerv4ScopeStatistics -ScopeId $s.ScopeId
                $color = if ($st.PercentageInUse -ge 90) { 'Red' }
                         elseif ($st.PercentageInUse -ge 75) { 'Yellow' } else { 'Green' }
                Write-Host ("  {0,-16} {1,5:N1}%  ({2} / {3} cím)" -f
                    $s.Name, $st.PercentageInUse, $st.AddressesInUse, $st.AddressesFree) -ForegroundColor $color
            }

            Write-Host ''
            Write-Host '=== Fenntartások ===' -ForegroundColor Cyan
            foreach ($s in $scopes) {
                Get-DhcpServerv4Reservation -ScopeId $s.ScopeId -ErrorAction SilentlyContinue |
                  Select-Object IPAddress, ClientId, Name, Description | Format-Table -AutoSize
            }

            Write-Host '=== Aktív bérletek ===' -ForegroundColor Cyan
            foreach ($s in $scopes) {
                Get-DhcpServerv4Lease -ScopeId $s.ScopeId -ErrorAction SilentlyContinue |
                  Select-Object IPAddress, HostName, ClientId, AddressState, LeaseExpiryTime |
                  Sort-Object IPAddress | Format-Table -AutoSize
            }
            """, RunAsAdmin: true),

        new("Hyper-V – virtuális gépek", ScriptKind.PowerShell, "Kiszolgáló",
            "VM-ek állapota, erőforrásai, lemezei és az elfelejtett ellenőrzési pontok.",
            """
            Import-Module Hyper-V -ErrorAction Stop

            Write-Host '=== Virtuális gépek ===' -ForegroundColor Cyan
            Get-VM | Select-Object Name, State, CPUUsage,
                @{n='Memória GB';e={[math]::Round($_.MemoryAssigned/1GB,1)}},
                Uptime, Status, Version | Format-Table -AutoSize

            Write-Host '=== Virtuális lemezek ===' -ForegroundColor Cyan
            foreach ($vm in Get-VM) {
                foreach ($d in Get-VMHardDiskDrive -VMName $vm.Name) {
                    $v = Get-VHD -Path $d.Path -ErrorAction SilentlyContinue
                    if ($v) {
                        Write-Host ("  {0,-20} {1,8:N1} / {2,8:N1} GB  {3}  {4}" -f
                            $vm.Name, ($v.FileSize/1GB), ($v.Size/1GB), $v.VhdType, (Split-Path $d.Path -Leaf))
                    }
                }
            }

            Write-Host ''
            Write-Host '=== Ellenőrzési pontok ===' -ForegroundColor Cyan
            $snaps = Get-VMSnapshot -VMName * -ErrorAction SilentlyContinue
            if ($snaps) {
                $snaps | Select-Object VMName, Name, CreationTime,
                    @{n='Napja';e={[int]((Get-Date)-$_.CreationTime).TotalDays}} |
                  Sort-Object Napja -Descending | Format-Table -AutoSize
                $old = $snaps | Where-Object { ((Get-Date)-$_.CreationTime).TotalDays -gt 14 }
                if ($old) {
                    Write-Host 'FIGYELEM: 14 napnál régebbi ellenőrzési pont. Ezek folyamatosan' -ForegroundColor Red
                    Write-Host 'növelik a lemezhasználatot és lassítják a VM-et.' -ForegroundColor Red
                }
            } else {
                Write-Host '  Nincs ellenőrzési pont.' -ForegroundColor Green
            }

            Write-Host '=== Virtuális kapcsolók ===' -ForegroundColor Cyan
            Get-VMSwitch | Select-Object Name, SwitchType, NetAdapterInterfaceDescription |
              Format-Table -AutoSize
            """, RunAsAdmin: true),

        new("Tanúsítványok lejárata", ScriptKind.PowerShell, "Kiszolgáló",
            "A gépen lévő tanúsítványok, kiemelve a 60 napon belül lejárókat.",
            """
            $days = 60
            $limit = (Get-Date).AddDays($days)

            $certs = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue

            Write-Host "=== $days napon belül lejáró tanúsítványok ===" -ForegroundColor Cyan
            $soon = $certs | Where-Object { $_.NotAfter -lt $limit }
            if ($soon) {
                $soon | Select-Object Subject, NotAfter,
                    @{n='Napja hátra';e={[int]($_.NotAfter - (Get-Date)).TotalDays}}, Thumbprint |
                  Sort-Object 'Napja hátra' | Format-Table -AutoSize -Wrap
            } else {
                Write-Host '  Nincs ilyen.' -ForegroundColor Green
            }

            Write-Host '=== Összes gépi tanúsítvány ===' -ForegroundColor Cyan
            $certs | Select-Object Subject, Issuer, NotAfter, HasPrivateKey |
              Sort-Object NotAfter | Format-Table -AutoSize -Wrap

            Write-Host '=== IIS kötések (ha van IIS) ===' -ForegroundColor Cyan
            try {
                Import-Module WebAdministration -ErrorAction Stop
                Get-ChildItem IIS:\SslBindings | Format-Table -AutoSize
            } catch { Write-Host '  Nincs IIS ezen a gépen.' -ForegroundColor DarkGray }
            """, RunAsAdmin: true),

        new("Szerepkörök és szolgáltatások", ScriptKind.PowerShell, "Kiszolgáló",
            "Telepített kiszolgálói szerepkörök és a nem futó automatikus szolgáltatások.",
            """
            Write-Host '=== Telepített szerepkörök ===' -ForegroundColor Cyan
            Get-WindowsFeature | Where-Object Installed |
              Select-Object Name, DisplayName, FeatureType |
              Sort-Object FeatureType, DisplayName | Format-Table -AutoSize

            Write-Host '=== Automatikus indítású, de NEM futó szolgáltatások ===' -ForegroundColor Cyan
            $bad = Get-CimInstance Win32_Service |
                   Where-Object { $_.StartMode -eq 'Auto' -and $_.State -ne 'Running' }
            if ($bad) {
                $bad | Select-Object DisplayName, Name, State, StartMode | Format-Table -AutoSize
                Write-Host 'Ezeknek futniuk kellene. Nézd meg az eseménynaplót.' -ForegroundColor Yellow
            } else {
                Write-Host '  Minden automatikus szolgáltatás fut.' -ForegroundColor Green
            }

            Write-Host '=== Függőben lévő újraindítás ===' -ForegroundColor Cyan
            $pending = @(
                (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'),
                (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
            ) -contains $true
            Write-Host ("  " + $(if ($pending) { 'IGEN – a gép újraindításra vár' } else { 'nincs' })) `
                -ForegroundColor $(if ($pending) { 'Yellow' } else { 'Green' })
            """, RunAsAdmin: true),

        // ================================================================ LINUX

        new("Linux – szerver egészségjelentés", ScriptKind.Bash, "Linux",
            "Üzemidő, terhelés, memória, lemez, hibás szolgáltatások és belépések egy futtatással.",
            """
            #!/usr/bin/env bash
            # Gyors szerver-áttekintés. Futtatás: bash egeszseg.sh
            set -uo pipefail

            line() { printf '\n=== %s ===\n' "$1"; }

            line "Rendszer"
            hostnamectl 2>/dev/null | sed 's/^/  /' || uname -a
            printf '  Üzemidő: %s\n' "$(uptime -p 2>/dev/null || uptime)"

            line "Terhelés és memória"
            printf '  Load: %s\n' "$(cut -d' ' -f1-3 /proc/loadavg)"
            printf '  Magok: %s\n' "$(nproc)"
            free -h | sed 's/^/  /'

            line "Lemezek"
            df -hT -x tmpfs -x devtmpfs | sed 's/^/  /'

            line "Inode kihasználtság"
            df -i -x tmpfs -x devtmpfs | awk 'NR==1 || $5+0 > 70' | sed 's/^/  /'

            line "Legnagyobb 10 mappa a /var alatt"
            du -h --max-depth=2 /var 2>/dev/null | sort -rh | head -10 | sed 's/^/  /'

            line "Hibás systemd egységek"
            if systemctl --failed --no-legend 2>/dev/null | grep -q .; then
                systemctl --failed --no-legend | sed 's/^/  /'
            else
                echo "  nincs"
            fi

            line "Legtöbb memóriát használó folyamatok"
            ps -eo pid,comm,%cpu,%mem --sort=-%mem | head -8 | sed 's/^/  /'

            line "Utolsó belépések"
            last -n 8 2>/dev/null | sed 's/^/  /' || echo "  (nem elérhető)"

            line "Sikertelen belépési kísérletek"
            journalctl -u ssh -u sshd --since "24 hours ago" 2>/dev/null |
                grep -ci "failed password" | sed 's/^/  /' || echo "  (nem elérhető)"
            """),

        new("Linux – frissítések ellenőrzése", ScriptKind.Bash, "Linux",
            "Elérhető csomagfrissítések Debian/Ubuntu és RHEL/Alma/Rocky rendszereken egyaránt.",
            """
            #!/usr/bin/env bash
            # Csak ELLENŐRIZ, nem telepít. Disztribúciót automatikusan felismeri.
            set -uo pipefail

            if command -v apt-get >/dev/null 2>&1; then
                echo "=== Debian / Ubuntu ==="
                sudo apt-get update -qq
                echo
                echo "Frissíthető csomagok:"
                apt list --upgradable 2>/dev/null | grep -v '^Listing' | sed 's/^/  /'
                echo
                echo "Biztonsági frissítések száma:"
                apt-get -s upgrade 2>/dev/null | grep -ci '^Inst.*security' | sed 's/^/  /'
                echo
                if [ -f /var/run/reboot-required ]; then
                    echo "ÚJRAINDÍTÁS SZÜKSÉGES:"
                    cat /var/run/reboot-required.pkgs 2>/dev/null | sed 's/^/  /'
                else
                    echo "Újraindítás nem szükséges."
                fi

            elif command -v dnf >/dev/null 2>&1; then
                echo "=== RHEL / AlmaLinux / Rocky ==="
                sudo dnf check-update || true
                echo
                echo "Biztonsági frissítések:"
                sudo dnf updateinfo list security 2>/dev/null | sed 's/^/  /'
                echo
                if command -v needs-restarting >/dev/null 2>&1; then
                    needs-restarting -r || echo "  újraindítás szükséges"
                fi

            else
                echo "Ismeretlen csomagkezelő – nézd meg kézzel." >&2
                exit 1
            fi
            """),

        new("Linux – rsync mentés sablon", ScriptKind.Bash, "Linux",
            "Napi mentés rsync-kel, hardlinkes verziózással és naplózással. Testreszabandó.",
            """
            #!/usr/bin/env bash
            #
            # Napi mentés rsync-kel. A --link-dest miatt a változatlan fájlok NEM
            # foglalnak új helyet: hardlinkkel mutatnak az előző mentésre.
            #
            # Élesítés előtt írd át a három változót, és próbáld ki --dry-run-nal!
            #
            set -euo pipefail

            SOURCE="/srv/adatok/"          # a záró / fontos: a mappa TARTALMA másolódik
            TARGET="/mnt/mentes"
            KEEP_DAYS=14
            LOG="/var/log/mentes.log"

            STAMP="$(date +%Y-%m-%d_%H%M)"
            DEST="$TARGET/$STAMP"
            LATEST="$TARGET/latest"

            exec >> "$LOG" 2>&1
            echo "=== $(date '+%F %T') mentés indul ==="

            mkdir -p "$TARGET"

            LINK_OPT=()
            [ -d "$LATEST" ] && LINK_OPT=(--link-dest="$LATEST")

            rsync -aAX --delete --numeric-ids \
                  --exclude='*.tmp' --exclude='cache/' \
                  "${LINK_OPT[@]}" \
                  "$SOURCE" "$DEST/"

            rm -f "$LATEST"
            ln -s "$DEST" "$LATEST"

            # Régi mentések takarítása
            find "$TARGET" -maxdepth 1 -type d -name '20*' -mtime "+$KEEP_DAYS" \
                 -exec rm -rf {} + 2>/dev/null || true

            echo "Kész: $DEST"
            du -sh "$DEST"
            echo

            # Ellenőrzés éles indítás előtt:
            #   rsync -aAXn --delete "$SOURCE" "$TARGET/proba/"
            """),

        new("Linux – Docker konténerek", ScriptKind.Bash, "Linux",
            "Futó konténerek, erőforrás-használat, lemezfoglalás és a nem futók.",
            """
            #!/usr/bin/env bash
            set -uo pipefail

            if ! command -v docker >/dev/null 2>&1; then
                echo "A docker parancs nem elérhető ezen a gépen." >&2
                exit 1
            fi

            echo "=== Futó konténerek ==="
            docker ps --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}\t{{.Ports}}'

            echo
            echo "=== Leállt konténerek ==="
            docker ps -a --filter "status=exited" --filter "status=dead" \
                --format 'table {{.Names}}\t{{.Status}}\t{{.Image}}' | sed '1d' || true

            echo
            echo "=== Erőforrás-használat ==="
            docker stats --no-stream --format 'table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.NetIO}}'

            echo
            echo "=== Lemezfoglalás ==="
            docker system df

            echo
            echo "=== Nem használt elemek ==="
            echo "  Takarítás (ÓVATOSAN, töröl):  docker system prune -a --volumes"
            """),

        // ================================================================ TELEPÍTÉS

        new("Winget – App Installer helyreállítás", ScriptKind.PowerShell, "Telepítés",
            "Ellenőrzi és helyreállítja a winget elérhetőségét (App Installer újraregisztrálása).",
            WingetRepairScript(), RunAsAdmin: true),

        new("Alap szoftvertelepítés (winget)", ScriptKind.PowerShell, "Telepítés",
            "Céges alapcsomag telepítése wingettel: böngészők, tömörítők, futtatókörnyezetek, " +
            "hibakód-kezeléssel és összesítéssel.",
            BuildBaseInstallScript(), RunAsAdmin: true),

        new("Opcionális szolgáltatások letiltása", ScriptKind.PowerShell, "Telepítés",
            "Fogyasztói/felesleges Windows-szolgáltatások felmérése és letiltása AD-s " +
            "munkaállomáson. Alapból CSAK FELMÉR; backupot készít a visszaállításhoz.",
            BuildServiceHardeningScript(), RunAsAdmin: true),

    };

    // ============================================================================
    //  Nagy telepítő szkriptek külön metódusból – a fenti tömb így áttekinthető marad.
    //  Ezek a felhasználó által beküldött szkriptek JAVÍTOTT változatai.
    // ============================================================================

    private static string WingetRepairScript() =>
        "#Requires -RunAsAdministrator\n" +
        "$w = Get-Command winget.exe -ErrorAction SilentlyContinue\n" +
        "if ($w) { Write-Host \"A winget elérhető: $($w.Source)\" -ForegroundColor Green; winget --version; return }\n" +
        "Write-Host 'A winget nem található. App Installer újraregisztrálása...' -ForegroundColor Yellow\n" +
        "try { Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -ErrorAction Stop; Start-Sleep 8 }\n" +
        "catch { Write-Host \"Sikertelen: $($_.Exception.Message)\" -ForegroundColor Red }\n" +
        "$w = Get-Command winget.exe -ErrorAction SilentlyContinue\n" +
        "if ($w) { Write-Host 'Sikerült, a winget elérhető.' -ForegroundColor Green }\n" +
        "else { Write-Host 'Töltsd le: https://aka.ms/getwinget' -ForegroundColor Red }\n";

    private static string BuildBaseInstallScript()
    {
        var apps = new (string Id, string Name)[]
        {
            ("Adobe.Acrobat.Reader.64-bit", "Adobe Acrobat Reader 64-bit"),
            ("Google.Chrome", "Google Chrome"),
            ("Opera.Opera", "Opera"),
            ("EclipseAdoptium.Temurin.8.JRE", "Java 8 - Eclipse Temurin JRE"),
            ("IrfanSkiljan.IrfanView", "IrfanView"),
            ("Ghisler.TotalCommander", "Total Commander"),
            ("RARLab.WinRAR", "WinRAR"),
            ("7zip.7zip", "7-Zip"),
            ("VideoLAN.VLC", "VLC Media Player"),
            ("Microsoft.VCRedist.2015+.x64", "Visual C++ Redistributable x64"),
            ("Microsoft.VCRedist.2015+.x86", "Visual C++ Redistributable x86"),
            ("Microsoft.DotNet.DesktopRuntime.8", ".NET Desktop Runtime 8"),
            ("Microsoft.DotNet.DesktopRuntime.10", ".NET Desktop Runtime 10"),
            ("GlavSoft.TightVNC", "TightVNC"),
        };

        var sb = new System.Text.StringBuilder();
        sb.Append(
            "#Requires -RunAsAdministrator\n#Requires -Version 5.1\n" +
            "$ErrorActionPreference = 'Continue'\n" +
            "$LogDir = 'C:\\ProgramData\\DevToolbox-Telepites'\n" +
            "New-Item -Path $LogDir -ItemType Directory -Force | Out-Null\n" +
            "Start-Transcript -Path (Join-Path $LogDir 'alaptelepites.log') -Append | Out-Null\n\n" +
            "$w = Get-Command winget.exe -ErrorAction SilentlyContinue\n" +
            "if (-not $w) {\n" +
            "    Write-Host 'A winget nem elérhető. App Installer regisztrálása...' -ForegroundColor Yellow\n" +
            "    try { Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -ErrorAction Stop; Start-Sleep 8 } catch {}\n" +
            "    $w = Get-Command winget.exe -ErrorAction SilentlyContinue\n}\n" +
            "if (-not $w) { Write-Host 'HIBA: winget nem elérhető.' -ForegroundColor Red; Stop-Transcript | Out-Null; return }\n\n" +
            "& winget.exe source update --accept-source-agreements --disable-interactivity | Out-Null\n\n" +
            "$results = [System.Collections.Generic.List[object]]::new()\n\n" +
            "function Install-Pkg([string]$Id,[string]$Name) {\n" +
            "    Write-Host \"`nTelepítés: $Name\" -ForegroundColor Cyan\n" +
            "    & winget.exe install --id $Id --exact --silent --source winget --scope machine ``\n" +
            "        --accept-package-agreements --accept-source-agreements --disable-interactivity\n" +
            "    $code = $LASTEXITCODE\n" +
            "    $status = if ($code -eq 0) {'Sikeres'} elseif ($code -eq -1978335189) {'Már telepítve'} else {\"Sikertelen ($code)\"}\n" +
            "    $col = if ($status -eq 'Sikeres'){'Green'} elseif ($status -eq 'Már telepítve'){'Yellow'} else {'Red'}\n" +
            "    Write-Host \"  $status\" -ForegroundColor $col\n" +
            "    $results.Add([PSCustomObject]@{Alkalmazas=$Name;Azonosito=$Id;Eredmeny=$status;Hibakod=$code})\n}\n\n");

        foreach (var (id, name) in apps)
            sb.Append($"Install-Pkg '{id}' '{name}'\n");

        sb.Append(
            "\nWrite-Host \"`nVálassz gyártót:\" -ForegroundColor Green\n" +
            "Write-Host '1 - Dell Command Update'; Write-Host '2 - MSI Center'; Write-Host '3 - HP Image Assistant'; Write-Host '0 - Egyik sem'\n" +
            "switch (Read-Host 'Választás') {\n" +
            "    '1' { Install-Pkg 'Dell.CommandUpdate' 'Dell Command Update' }\n" +
            "    '2' { Install-Pkg 'MSI.MSICenter' 'MSI Center' }\n" +
            "    '3' { Install-Pkg 'HP.ImageAssistant' 'HP Image Assistant' }\n" +
            "    default { Write-Host 'Gyártóspecifikus alkalmazás kihagyva.' -ForegroundColor Yellow }\n}\n\n" +
            "Write-Host \"`nTelepítési összesítés\" -ForegroundColor Green\n" +
            "$results | Format-Table Alkalmazas, Eredmeny, Hibakod -AutoSize\n" +
            "$results | Export-Csv -Path (Join-Path $LogDir 'eredmeny.csv') -NoTypeInformation -Encoding UTF8\n" +
            "Stop-Transcript | Out-Null\n");

        return sb.ToString();
    }

    private static string BuildServiceHardeningScript() =>
        "#Requires -Version 5.1\n" +
        "# Opcionális Windows-szolgáltatások letiltása. Alapból CSAK FELMÉR. Tiltáshoz: -Apply\n" +
        "[CmdletBinding(SupportsShouldProcess=$true)]\n" +
        "param([switch]$Apply,[switch]$IncludeConditional,[switch]$IncludePerUser,[switch]$ForceConditional)\n" +
        "$ErrorActionPreference='Continue'\n" +
        "$WorkDir='C:\\ProgramData\\AD-Service-Hardening'; New-Item $WorkDir -ItemType Directory -Force|Out-Null\n" +
        "$BackupFile=Join-Path $WorkDir (\"ServiceBackup-\"+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.csv')\n" +
        "function Test-Admin { (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }\n" +
        "if ($Apply -and -not (Test-Admin)) { throw 'Az -Apply-hoz rendszergazdai PowerShell kell.' }\n" +
        "function Test-Pnp([string[]]$C,[string[]]$P=@()){ try { foreach($d in Get-CimInstance Win32_PnPEntity -EA Stop|?{$_.Status -eq 'OK'}){ if($C -contains [string]$d.PNPClass){return $true}; foreach($x in $P){if([string]$d.Name -like $x){return $true}} } } catch {}; return $false }\n" +
        "$HasBt=Test-Pnp @('Bluetooth') @('*Bluetooth*'); $HasBio=Test-Pnp @('Biometric') @('*Fingerprint*','*IR Camera*')\n" +
        "$HasSc=Test-Pnp @('SmartCardReader') @('*Smart Card Reader*'); $HasImg=Test-Pnp @('Image','Camera') @('*Scanner*','*Camera*')\n" +
        "$HasTouch=Test-Pnp @('HIDClass') @('*Touch Screen*','*Pen*','*Digitizer*')\n" +
        "Write-Host \"Bluetooth=$HasBt Biometria=$HasBio SmartCard=$HasSc Kamera=$HasImg Érintés=$HasTouch\" -ForegroundColor Cyan\n" +
        "Write-Host $(if($Apply){'MÓDOSÍTÁSI MÓD'}else{'FELMÉRÉSI MÓD - nem módosít'}) -ForegroundColor Cyan\n" +
        "$Base=@('WalletService','MixedRealityOpenXRSvc','AJRouter','Fax','MapsBroker','RetailDemo','WpcMonSvc','wisvc','XboxGipSvc','XblAuthManager','XblGameSave','XboxNetApiSvc','icssvc','SEMgrSvc','lfsvc')\n" +
        "$Cond=@('SharedAccess','WebClient','WMPNetworkSvc')\n" +
        "$HwGroups=@([pscustomobject]@{N='Bluetooth';P=$HasBt;S=@('bthserv')},[pscustomobject]@{N='SmartCard';P=$HasSc;S=@('SCardSvr','ScDeviceEnum','SCPolicySvc')},[pscustomobject]@{N='Biometria';P=$HasBio;S=@('WbioSrvc')},[pscustomobject]@{N='Kamera';P=$HasImg;S=@('stisvc')},[pscustomobject]@{N='Érintés';P=$HasTouch;S=@('TabletInputService')})\n" +
        "$Backup=New-Object System.Collections.Generic.List[object]\n" +
        "function Disable-Svc([string]$Pattern,[string]$Reason){\n" +
        "    # FONTOS: NEM $matches - az automatikus változó, amit a -match operátor felülír!\n" +
        "    $svcMatches=@(Get-Service -Name $Pattern -ErrorAction SilentlyContinue)\n" +
        "    if($svcMatches.Count -eq 0){ Write-Host \"[SKIP] $Pattern nincs telepítve.\" -ForegroundColor Yellow; return }\n" +
        "    foreach($svc in $svcMatches){\n" +
        "        $cim=Get-CimInstance Win32_Service -Filter \"Name='$($svc.Name)'\" -EA SilentlyContinue\n" +
        "        $Backup.Add([pscustomobject]@{Name=$svc.Name;StartMode=[string]$cim.StartMode;Status=[string]$svc.Status})\n" +
        "        Write-Host \"[INFO] $($svc.Name): $Reason (állapot=$($svc.Status), indítás=$($cim.StartMode))\" -ForegroundColor Cyan\n" +
        "        if($Apply -and $PSCmdlet.ShouldProcess($svc.Name,'Disabled')){\n" +
        "            if($svc.Status -ne 'Stopped'){Stop-Service $svc.Name -Force -EA Continue}\n" +
        "            Set-Service $svc.Name -StartupType Disabled -EA Continue\n" +
        "            Write-Host \"[ OK ] $($svc.Name) letiltva.\" -ForegroundColor Green } } }\n" +
        "foreach($n in $Base){ Disable-Svc $n 'opcionális Windows-funkció' }\n" +
        "if($IncludeConditional){\n" +
        "    foreach($n in $Cond){ Disable-Svc $n 'feltételes szolgáltatás' }\n" +
        "    foreach($g in $HwGroups){ if(-not $g.P -or $ForceConditional){ foreach($n in $g.S){ Disable-Svc $n \"$($g.N) hardver nem észlelhető\" } } else { Write-Host \"[SKIP] $($g.N) hardver észlelve; szolgáltatásai megmaradnak.\" -ForegroundColor Yellow } }\n" +
        "} else { Write-Host '[SKIP] Feltételes szolgáltatások kimaradtak (-IncludeConditional).' -ForegroundColor Yellow }\n" +
        "$Backup | Export-Csv -Path $BackupFile -NoTypeInformation -Encoding UTF8\n" +
        "Write-Host \"`nBiztonsági mentés (visszaállításhoz): $BackupFile\" -ForegroundColor Green\n" +
        "if(-not $Apply){ Write-Host 'Nem történt módosítás. Ellenőrzés után futtasd -Apply kapcsolóval.' -ForegroundColor Yellow }\n";
}
