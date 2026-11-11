@echo off
chcp 65001 >nul
cd /d "%~dp0"
if "%~1"=="" (
  echo Usage: push-update.bat ^<target folder^>
  echo   push-update.bat \\NAS\share\mdv
  echo   push-update.bat D:\MDV-Updates
  echo   push-update.bat http-folder-on-your-intranet
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1" -PushOnly -PushTo "%~1"
echo.
pause
