@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Building distribution packages (Setup / Portable / SingleFile / Bundle / Update)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1" %*
echo.
pause
