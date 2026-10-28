@echo off
setlocal
echo.
echo ===== Disable old tool: CadImeSwitch =====
echo.
taskkill /F /IM CadImeSwitch.exe >nul 2>&1
if errorlevel 1 (echo [1/3] CadImeSwitch.exe not running) else (echo [1/3] CadImeSwitch.exe stopped)
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v CadImeSwitch /f >nul 2>&1
if errorlevel 1 (echo [2/3] autorun entry already absent) else (echo [2/3] autorun entry removed)
set "FOUND="
for /d %%V in ("%APPDATA%\Autodesk\AutoCAD *") do (
  for /d %%R in ("%%V\*") do (
    for /d %%S in ("%%R\*") do (
      if exist "%%S\Support\acaddoc.lsp" (
        move /Y "%%S\Support\acaddoc.lsp" "%%S\Support\acaddoc.lsp.disabled" >nul 2>&1
        if not errorlevel 1 (
          echo [3/3] disabled: %%S\Support\acaddoc.lsp
          set "FOUND=1"
        )
      )
    )
  )
)
if not defined FOUND echo [3/3] no acaddoc.lsp found
echo.
echo Done. Now press Shift or Ctrl+Space to test Chinese.
echo Restore: rename acaddoc.lsp.disabled back, re-add the Run entry.
echo.
pause
exit /b 0