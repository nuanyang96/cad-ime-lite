@echo off
setlocal
title CadImeLite rollback to v7
set "SRC=%~dp0v7\CadImeLite.exe"
set "DST=%LOCALAPPDATA%\CadImeLite\CadImeLite.exe"
if not exist "%SRC%" goto nosrc
copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 goto copyfail
start "" "%DST%"
echo [OK] rolled back to v7.
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