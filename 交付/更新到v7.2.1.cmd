@echo off
setlocal
title CadImeLite v7.2.1 update
set "SRC=%~dp0v7.2.1\CadImeLite.exe"
set "DST=%LOCALAPPDATA%\CadImeLite\CadImeLite.exe"
if not exist "%SRC%" goto nosrc
if not exist "%LOCALAPPDATA%\CadImeLite" mkdir "%LOCALAPPDATA%\CadImeLite"
copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 goto copyfail
start "" "%DST%"
echo [OK] v7.2.1 installed, tray restarted. No need to restart AutoCAD.
echo.
echo Only the log display changed vs v7.2. Behaviour is identical.
echo.
pause
exit /b 0

:nosrc
echo [ERR] missing: %SRC%
pause
exit /b 1

:copyfail
echo [ERR] copy failed - CadImeLite is probably still running.
echo Right-click the tray icon and choose Exit, then run this file again.
pause
exit /b 1