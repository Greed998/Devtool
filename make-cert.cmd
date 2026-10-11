@echo off
rem Onalairt kodalairo tanusitvany keszitese.
rem Reszletek: make-cert.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-cert.ps1" %*
pause
