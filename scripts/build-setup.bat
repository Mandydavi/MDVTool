@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo Building MDV Setup...
dotnet build src\MDVTool.Setup\MDVTool.Setup.csproj -c Release -v:q -nologo
if errorlevel 1 goto fail
copy /Y "src\MDVTool.Setup\bin\Release\net48\MDVSetup.exe" "bin\MDVSetup.exe" >nul
if not exist "setup.json" copy /Y "src\MDVTool.Setup\setup.json" "setup.json" >nul
echo.
echo Done: MDVSetup.exe (about 30 KB, no .NET runtime needed)
echo Put it next to "MDV Tool.exe" for offline install, or fill in setup.json url for online install.
goto end
:fail
echo Build failed.
exit /b 1
:end
