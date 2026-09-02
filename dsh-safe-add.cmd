@echo off
REM dsh-safe-add.cmd — safely add a DSH web plugin through the safety module.
REM Usage: dsh-safe-add.cmd <plugin-spec>
REM   e.g.  dsh-safe-add.cmd "github:Small-tailqwq/dsh-deep-whale#path:/skin-manager"
REM Behavior: snapshots the current profile state, runs `dsh plugin add`, marks the
REM install as pending, and (if install fails) auto-rolls-back.
setlocal
set "PKG=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%PKG%scripts\dsh-safety.ps1" -Action Add -Plugin "%~1" -Config "%PKG%config.json"
echo.
echo Next steps:
echo   1. Restart DSh (tray icon -> Restart Server, or relaunch the shortcut).
echo   2. If the server stays healthy, the supervisor auto-verifies; if it crashes,
echo      it auto-rolls-back to the pre-install snapshot.
endlocal
