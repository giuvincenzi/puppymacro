@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET SDK was not found.
    echo Install it from PowerShell with:
    echo     winget install --id Microsoft.DotNet.SDK.10 --exact
    echo Then close and reopen this window.
    pause
    exit /b 1
)

echo Building PuppyMacro (local folder build)...
dotnet publish "PuppyMacro\PuppyMacro.csproj" -c Release -p:PublishProfile=Folder
if errorlevel 1 (
    echo.
    echo Build failed. Copy the errors above and send them over.
    pause
    exit /b 1
)

echo.
echo Done: "%~dp0publish\PuppyMacro.exe"
pause
