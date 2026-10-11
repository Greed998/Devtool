@echo off
rem =====================================================================
rem  DevToolbox - kodalairas
rem
rem  Hasznalat:   sign.cmd  [exe utvonala]
rem               (parameter nelkul: out\standalone\DevToolbox.exe)
rem
rem  A privat kulcs SOHA nem kerul be ebbe a fajlba. A jelszot a szkript
rem  futaskor kerdezi be, es nem irja ki a kepernyore.
rem =====================================================================
setlocal EnableDelayedExpansion

rem ---------------------------------------------------------------------
rem  1. LEPES - valaszd ki a modot, es toltsd ki a hozza tartozo sorokat
rem ---------------------------------------------------------------------
rem   SELFSIGNED = a make-cert.cmd altal keszitett onalairt tanusitvany
rem                (kezelt gepekre; nyilvanos letolteshez NEM eleg)
rem   PFX        = .pfx fajl a lemezen
rem   STORE      = tanusitvanytarolo / USB token (SafeNet, YubiKey, HSM)
rem   AZURE      = Azure Artifact Signing (dotnet sign, nem kell token)

set "MODE=SELFSIGNED"

rem ---- PFX modhoz --------------------------------------------------
set "PFX_FILE=%~dp0cert\kodalairas.pfx"

rem ---- STORE modhoz ------------------------------------------------
rem  A tanusitvany SHA-1 ujjlenyomata szokozok nelkul. Kilistazas:
rem     certutil -user -store My
set "THUMBPRINT=0000000000000000000000000000000000000000"

rem ---- AZURE modhoz ------------------------------------------------
set "AZ_METADATA=%~dp0azure-signing.json"

rem ---- Idobelyegzo (a tanusitvany kiadojatol fugg) -----------------
rem   DigiCert  : http://timestamp.digicert.com
rem   Sectigo   : http://timestamp.sectigo.com
rem   GlobalSign: http://timestamp.globalsign.com/tsa/r6advanced1
set "TIMESTAMP=http://timestamp.digicert.com"

rem ---------------------------------------------------------------------
rem  2. LEPES - a fajl es a signtool megkeresese
rem ---------------------------------------------------------------------
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=%~dp0out\standalone\DevToolbox.exe"

if not exist "%TARGET%" (
    echo [HIBA] Nem talalhato a fajl: %TARGET%
    echo         Elobb futtasd a build.cmd-t.
    goto :fail
)

if /i "%MODE%"=="AZURE" goto :azure

set "SIGNTOOL="
for /f "delims=" %%i in ('dir /b /s "%ProgramFiles(x86)%\Windows Kits\10\bin\*\x64\signtool.exe" 2^>nul') do set "SIGNTOOL=%%i"
if not defined SIGNTOOL for /f "delims=" %%i in ('dir /b /s "%ProgramFiles%\Windows Kits\10\bin\*\x64\signtool.exe" 2^>nul') do set "SIGNTOOL=%%i"

if not defined SIGNTOOL (
    echo [HIBA] A signtool.exe nem talalhato.
    echo         Telepitsd a Windows SDK-t: https://aka.ms/windowssdk
    echo         Eleg a "Windows SDK Signing Tools for Desktop Apps" resz.
    goto :fail
)

echo signtool:  %SIGNTOOL%
echo cel:       %TARGET%
echo mod:       %MODE%
echo.

if /i "%MODE%"=="SELFSIGNED" goto :selfsigned
if /i "%MODE%"=="PFX"        goto :pfx
if /i "%MODE%"=="STORE"      goto :store
echo [HIBA] Ismeretlen MODE: %MODE%   (SELFSIGNED / PFX / STORE / AZURE)
goto :fail

rem ---------------------------------------------------------------------
:selfsigned
set "TP_FILE=%~dp0cert\thumbprint.txt"
if not exist "%TP_FILE%" (
    echo [HIBA] Nem talalhato: %TP_FILE%
    echo         Keszitsd el eloszor:  make-cert.cmd
    goto :fail
)
set /p SS_TP=<"%TP_FILE%"
echo tanusitvany: %SS_TP%  ^(onalairt^)
echo.
"%SIGNTOOL%" sign /fd SHA256 /td SHA256 /tr "%TIMESTAMP%" /sha1 "%SS_TP%" /v "%TARGET%"
set "RC=%ERRORLEVEL%"
set "SELF=1"
goto :verify

rem ---------------------------------------------------------------------
:pfx
if not exist "%PFX_FILE%" (
    echo [HIBA] Nem talalhato a tanusitvany: %PFX_FILE%
    goto :fail
)
echo Add meg a .pfx jelszavat ^(a beires nem latszik^):
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Read-Host -AsSecureString; [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($p))" > "%TEMP%\dtb_pw.tmp"
set /p PFXPW=<"%TEMP%\dtb_pw.tmp"
del /f /q "%TEMP%\dtb_pw.tmp" >nul 2>&1

"%SIGNTOOL%" sign /fd SHA256 /td SHA256 /tr "%TIMESTAMP%" /f "%PFX_FILE%" /p "%PFXPW%" /v "%TARGET%"
set "RC=%ERRORLEVEL%"
set "PFXPW="
goto :verify

rem ---------------------------------------------------------------------
:store
"%SIGNTOOL%" sign /fd SHA256 /td SHA256 /tr "%TIMESTAMP%" /sha1 "%THUMBPRINT%" /v "%TARGET%"
set "RC=%ERRORLEVEL%"
goto :verify

rem ---------------------------------------------------------------------
:azure
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [HIBA] A dotnet parancs nem talalhato.
    goto :fail
)

dotnet tool list --global | find /i "sign" >nul 2>&1
if errorlevel 1 (
    echo A sign eszkoz telepitese...
    dotnet tool install --global sign --prerelease
    if errorlevel 1 goto :fail
)

if not exist "%AZ_METADATA%" (
    echo [HIBA] Hianyzik a metaadat fajl: %AZ_METADATA%
    echo.
    echo  Hozd letre ezzel a tartalommal, a sajat adataiddal:
    echo    {
    echo      "Endpoint": "https://neu.codesigning.azure.net/",
    echo      "CodeSigningAccountName": "SAJAT-FIOK",
    echo      "CertificateProfileName": "SAJAT-PROFIL"
    echo    }
    echo.
    echo  Bejelentkezeshez allitsd be elotte a kornyezeti valtozokat:
    echo    AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET
    goto :fail
)

for %%F in ("%TARGET%") do dotnet sign code trusted-signing "%%~nxF" --base-directory "%%~dpF" --trusted-signing-metadata "%AZ_METADATA%"
set "RC=%ERRORLEVEL%"
goto :verify

rem ---------------------------------------------------------------------
:verify
if not "%RC%"=="0" (
    echo.
    echo [HIBA] Az alairas nem sikerult. Hibakod: %RC%
    goto :fail
)

echo.
echo === Ellenorzes ===
if defined SELF goto :verify_self
if defined SIGNTOOL goto :verify_signtool
goto :verify_ps

:verify_self
rem Onalairt tanusitvanynal a signtool /pa csak akkor fogadja el, ha a tanusitvany
rem telepitve van a Megbizhato gyokertanusitvanyok koze ezen a gepen.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = Get-AuthenticodeSignature '%TARGET%'; Write-Host ('  Allapot : ' + $s.Status); if ($s.SignerCertificate) { Write-Host ('  Alairo  : ' + $s.SignerCertificate.Subject) }; if ($s.Status -ne 'Valid') { Write-Host '  Ez normalis, ha a tanusitvany meg nincs telepitve ezen a gepen.'; Write-Host '  Futtasd rendszergazdakent: install-cert.cmd' }"
goto :hash

:verify_signtool
"%SIGNTOOL%" verify /pa /v "%TARGET%"
goto :hash

:verify_ps
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-AuthenticodeSignature '%TARGET%' | Format-List Status, StatusMessage"
goto :hash

:hash
echo.
echo === SHA-256 lenyomat ^(tedd ki a letoltesi oldalra^) ===
certutil -hashfile "%TARGET%" SHA256 | findstr /v ":"
echo.

if defined SELF goto :done_self
echo Kesz. Az alairas utan a SmartScreen a nevedet mutatja "Ismeretlen kiado"
echo helyett. A figyelmeztetes viszont addig megmarad, amig a letoltesszam
echo alapjan felepul a hirnev.
goto :done

:done_self
echo Kesz. Onalairt tanusitvannyal a program azokon a gepeken lesz megbizhato,
echo ahol telepitetted a .cer fajlt (install-cert.cmd). Ismeretlen gepen ez NEM
echo helyettesiti a CA-tol vasarolt tanusitvanyt.

:done
endlocal
exit /b 0

:fail
endlocal
exit /b 1
