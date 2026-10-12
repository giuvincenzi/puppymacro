# make dev: closes a running PuppyMacro, deletes the development build's data (so it starts
# again from the sample loops, macros and remaps), builds the current code (Debug) and starts it.
# make dev keep=1 (-Keep) keeps the data, to check that something stays saved.
# -Screenshots (the create_release_pr workflow, before scripts/screenshots.ps1) starts it with PUPPYMACRO_SCREENSHOTS=1:
# no development build marks (strip, DEV), so the screenshots look like a release.
param([switch]$Keep, [switch]$Screenshots)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'PuppyMacro/PuppyMacro.csproj'
$exe = Join-Path $root 'PuppyMacro/bin/Debug/net10.0-windows10.0.18362.0/win-x64/PuppyMacro.exe'

# Only one PuppyMacro can run at a time (installed or dev).
Get-Process -Name PuppyMacro -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Closing the running PuppyMacro ($($_.Path))"
    # Also AI bridges (PuppyMacro.exe --mcp): they exit by themselves when PuppyMacro closes, maybe already gone here.
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    $_.WaitForExit()
}

$dataFolder = Join-Path $env:APPDATA 'PuppyMacro Dev'
if (-not $Keep -and (Test-Path $dataFolder)) {
    Remove-Item $dataFolder -Recurse -Force
    Write-Host "Removed ${dataFolder}: starting from the samples"
}

dotnet build $project -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# -Screenshots: PUPPYMACRO_SCREENSHOTS=1, inherited by PuppyMacro (no development build marks).
if ($Screenshots) { $env:PUPPYMACRO_SCREENSHOTS = '1' }
Start-Process -FilePath $exe
Write-Host "Started $exe"
