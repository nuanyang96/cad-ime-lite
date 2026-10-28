@echo off
chcp 437 >nul
setlocal
set "DST=%LOCALAPPDATA%\CadImeLite"
echo.
echo ===== Update CadImeLite (draw-area + WPF prefix + de-bounced reset) =====
echo Source : %~dp0
echo Target : %DST%
echo.

taskkill /F /IM CadImeLite.exe >nul 2>&1
if errorlevel 1 (echo [1/4] CadImeLite.exe was not running) else (echo [1/4] old process stopped)
ping -n 2 127.0.0.1 >nul

if not exist "%DST%" mkdir "%DST%"
copy /Y "%~dp0CadImeLite.exe" "%DST%\" >nul
if errorlevel 1 goto :fail
echo [2/4] copied CadImeLite.exe

copy /Y "%~dp0rules.ini" "%DST%\" >nul
if errorlevel 1 goto :fail
echo [3/4] copied rules.ini

start "" "%DST%\CadImeLite.exe"
echo [4/4] restarted
echo.
echo Log: %DST%\ime-lite.log
echo Expect: in MText editor Chinese, on command line English, after leaving CAD Chinese.
echo.
pause
exit /b 0

:fail
echo.
echo UPDATE FAILED: file locked or permission denied.
echo Quit CadImeLite from the tray icon first, then run this again.
echo.
pause
exit /b 1
