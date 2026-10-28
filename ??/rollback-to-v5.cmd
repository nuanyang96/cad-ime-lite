@echo off
setlocal
cd /d "%~dp0"
echo [1/3] stopping CadImeLite...
taskkill /f /im CadImeLite.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul
echo [2/3] restoring v5...
if exist "CadImeLite.exe.bak-v5-20261028" copy /y "CadImeLite.exe.bak-v5-20261028" "CadImeLite.exe" >nul
if exist "rules.ini.bak-v5-20261028" copy /y "rules.ini.bak-v5-20261028" "rules.ini" >nul
echo [3/3] starting v5...
start "" "CadImeLite.exe"
echo done. rollback finished.
ping -n 4 127.0.0.1 >nul
exit
