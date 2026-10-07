# make dev: closes a running PuppyMacro, builds the current code (Debug) and starts it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'PuppyMacro/PuppyMacro.csproj'
$exe = Join-Path $root 'PuppyMacro/bin/Debug/net10.0-windows/win-x64/PuppyMacro.exe'

# Only one PuppyMacro can run at a time (installed or dev).
Get-Process -Name PuppyMacro -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Closing the running PuppyMacro ($($_.Path))"
    Stop-Process -Id $_.Id -Force
    $_.WaitForExit()
}

dotnet build $project -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Start-Process -FilePath $exe
Write-Host "Started $exe"
