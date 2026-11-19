@echo off
setlocal
title CadImeLite v7.1 update
set "SRC=%~dp0v7.1\CadImeLite.exe"
set "DST=%LOCALAPPDATA%\CadImeLite\CadImeLite.exe"
if not exist "%SRC%" goto nosrc
if not exist "%LOCALAPPDATA%\CadImeLite" mkdir "%LOCALAPPDATA%\CadImeLite"
copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 goto copyfail
start "" "%DST%"
echo [OK] v7.1 installed, tray restarted.
echo.
echo Verify with two AutoCAD windows open:
echo   1. In CAD A double-click a MTEXT, leave the cursor inside.
echo   2. Click CAD B drawing area, type L - the command prompt must appear.
echo   3. Back to CAD A editor - Chinese must still work.
echo.
pause
exit /b 0

:nosrc
echo [ERR] missing: %SRC%
pause
exit /b 1

:copyfail
echo [ERR] copy failed.
echo If CadImeLite is running, right-click the tray icon and choose Exit,
echo then run this file again.
pause
exit /b 1