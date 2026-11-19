@echo off
setlocal
set "DST=%LOCALAPPDATA%\CadImeLite"
echo.
echo ===== Install CadImeLite =====
echo Target dir: %DST%
echo.
if not exist "%DST%" mkdir "%DST%"
if not exist "%DST%" goto fail
copy /Y "%~dp0CadImeLite.exe" "%DST%\" >nul
if errorlevel 1 goto fail
copy /Y "%~dp0rules.ini" "%DST%\" >nul
if errorlevel 1 goto fail
echo [1/2] copied CadImeLite.exe + rules.ini
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CadImeLite /t REG_SZ /d "\"%DST%\CadImeLite.exe\"" /f >nul
if errorlevel 1 goto fail
echo [2/2] autorun added: HKCU\...\Run\CadImeLite
start "" "%DST%\CadImeLite.exe"
echo.
echo Started in tray. Log: %DST%\ime-lite.log
echo Rules: %DST%\rules.ini  (restart app after editing)
echo.
pause
exit /b 0
:fail
echo.
echo FAILED: copy or registry write denied. Run as administrator.
echo.
pause
exit /b 1