@echo off
rem =====================================================================
rem  DevToolbox Fleet - teljes fordites + AIO csomag
rem
rem  Eloallitja:
rem    out\fleet\Fleet.exe             (telephelyi szerver + Kozpont, benne az agent + wireguard.dll)
rem    out\agent\DevToolboxAgent.exe   (onallo agent - az AIO-hoz; a Fleet amugy sajat agentet agyaz be)
rem    out\standalone\DevToolbox.exe   (az onallo DevToolbox eszkoz)
rem    AIO\Dev-<datum>.zip             (mind a harom exe + a dokumentumok + a forras-zip)
rem
rem  Szukseges: .NET 10 SDK, Windows x64. A verziot a Fleet.csproj-bol olvassa.
rem =====================================================================
setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

rem --- Verzio a Fleet.csproj-bol (pl. 1.5.10) ---
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command ^
  "(Select-String -Path 'agent\Fleet\Fleet.csproj' -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value"`) do set "VER=%%v"
if "%VER%"=="" set "VER=0.0.0"

rem --- Datumbelyeg (yyyy-MM-dd), lokaliis beallitastol fuggetlenul ---
for /f "usebackq delims=" %%d in (`powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd"`) do set "STAMP=%%d"

echo === DevToolbox Fleet %VER% fordites (%STAMP%) ===
echo.

rem ---------------------------------------------------------------------
rem  1. Agent (onallo exe az AIO-hoz). A Fleet sajat agentet agyaz be a
rem     forditasakor, ezt a peldanyt csak a csomagba tesszuk.
rem ---------------------------------------------------------------------
echo --- [1/3] DevToolboxAgent.exe ---
dotnet publish agent\Agent\DevToolboxAgent.csproj -c Release -r win-x64 -o "out\agent" --nologo -v minimal
if errorlevel 1 goto :err

rem ---------------------------------------------------------------------
rem  2. Fleet (szerver + Kozpont; az EmbedAgent.targets a sajat agentet
rem     epiti es agyazza be, plusz a wireguard.dll).
rem ---------------------------------------------------------------------
echo --- [2/3] Fleet.exe ---
dotnet publish agent\Fleet\Fleet.csproj -c Release -r win-x64 -o "out\fleet" --nologo -v minimal
if errorlevel 1 goto :err

rem ---------------------------------------------------------------------
rem  3. DevToolbox (onallo eszkoz)
rem ---------------------------------------------------------------------
echo --- [3/3] DevToolbox.exe ---
dotnet publish src\DevToolbox.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o "out\standalone" --nologo -v minimal
if errorlevel 1 goto :err

rem ---------------------------------------------------------------------
rem  AIO csomag osszerakasa
rem ---------------------------------------------------------------------
echo.
echo --- AIO csomag ---
set "AIODIR=%~dp0AIO"
set "STAGE=%AIODIR%\staging"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%" 2>nul
mkdir "%AIODIR%" 2>nul

rem exe-k
copy /y "out\fleet\Fleet.exe"             "%STAGE%\Fleet.exe"             >nul || goto :err
copy /y "out\agent\DevToolboxAgent.exe"   "%STAGE%\DevToolboxAgent.exe"  >nul || goto :err
copy /y "out\standalone\DevToolbox.exe"   "%STAGE%\DevToolbox.exe"       >nul || goto :err

rem dokumentumok (ami megvan) - ugyanaz, mint a kezi AIO-ban. A README.md szandekosan
rem nincs benne; ha kell, vedd fel ide egy tovabbi copy sorral.
if exist "FRISSITES.txt"         copy /y "FRISSITES.txt"         "%STAGE%\" >nul
if exist "FEJLESZTESI-NAPLO.md"  copy /y "FEJLESZTESI-NAPLO.md"  "%STAGE%\" >nul

rem forras-zip: ha git-repo, a kovetett fajlokbol (tiszta, csak a verziozott forras);
rem kulonben a munkafabol, a build-szemetet kihagyva.
echo   DevToolbox-forras.zip ...
where git >nul 2>nul
if %errorlevel%==0 if exist ".git" (
  git archive --format=zip --prefix=DevToolbox-forras/ -o "%STAGE%\DevToolbox-forras.zip" HEAD
)
if not exist "%STAGE%\DevToolbox-forras.zip" (
  rem Tartalek: munkafa-masolat a build-kimenetek nelkul, majd tomorites.
  set "SRCTMP=%STAGE%\DevToolbox-forras"
  robocopy "." "!SRCTMP!" /e /xd .git out AIO bin obj .vs cert /xf *.pfx *.key >nul
  powershell -NoProfile -Command "Compress-Archive -Path '!SRCTMP!' -DestinationPath '%STAGE%\DevToolbox-forras.zip' -Force"
  rmdir /s /q "!SRCTMP!"
)

rem vegso AIO zip
set "AIOZIP=%AIODIR%\Dev-%STAMP%.zip"
if exist "%AIOZIP%" del /q "%AIOZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%AIOZIP%' -Force"
if errorlevel 1 goto :err
rmdir /s /q "%STAGE%"

echo.
echo === KESZ: %VER% ===
echo   Fleet.exe            : out\fleet\Fleet.exe
echo   DevToolboxAgent.exe  : out\agent\DevToolboxAgent.exe
echo   DevToolbox.exe       : out\standalone\DevToolbox.exe
echo   AIO                  : %AIOZIP%
echo.
echo A kodalairashoz: sign.cmd ^<exe^>   (mindharom exe-t kulon ajanlott alairni, majd ujracsomagolni)
pause
exit /b 0

:err
echo.
echo *** A fordites/csomagolas HIBAVAL leallt. ***
pause
exit /b 1
