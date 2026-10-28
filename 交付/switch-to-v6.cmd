@echo off
setlocal
cd /d "%~dp0"
echo [1/5] stopping old CadImeLite...
taskkill /f /im CadImeLite.exe >nul 2>&1
set /a Tries=0
:waitloop
ping -n 2 127.0.0.1 >nul
tasklist /fi "imagename eq CadImeLite.exe" 2>nul | find /i "CadImeLite.exe" >nul
if errorlevel 1 goto stopped
set /a Tries+=1
if %Tries% LSS 5 goto waitloop
echo [warn] still running after 5 checks; single-instance guard will handle it
:stopped
echo [2/5] backing up old exe/rules (only once)...
if not exist "CadImeLite.exe.bak-v5-20261028" copy /y "CadImeLite.exe" "CadImeLite.exe.bak-v5-20261028" >nul
if not exist "rules.ini.bak-v5-20261028" copy /y "rules.ini" "rules.ini.bak-v5-20261028" >nul
echo [3/5] installing new build...
copy /y "CadImeLite-v6.exe" "CadImeLite.exe" >nul
copy /y "rules-v6-auto.ini" "rules.ini" >nul
echo [4/5] starting (single instance guard is active)...
start "" "CadImeLite.exe"
echo [5/5] done. Only one tray icon should appear.
ping -n 4 127.0.0.1 >nul
exit
