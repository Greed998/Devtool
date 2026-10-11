@echo off
rem A kiadoi tanusitvany telepitese egy celgepre. Rendszergazdai jog kell.
rem Eltavolitas:  install-cert.cmd -Remove
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-cert.ps1" %*
pause
