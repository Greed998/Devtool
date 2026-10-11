@echo off
rem DevToolbox - onallo 64 bites exe forditasa (.NET 10 SDK szukseges)
setlocal
cd /d "%~dp0src"

echo === Onallo, egyfajlos exe (nem kell .NET a celgepen) ===
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "%~dp0out\standalone"

if errorlevel 1 goto :err
echo.
echo Kesz: %~dp0out\standalone\DevToolbox.exe
pause
exit /b 0

:err
echo.
echo A forditas hibaval leallt.
pause
exit /b 1
