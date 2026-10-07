# make dev: closes a running PuppyMacro, deletes the development build's data (so it starts
# again from the sample loops, macros and remaps), builds the current code (Debug) and starts it.
# make dev-keep (-Keep) keeps the data, to check that something stays saved.
param([switch]$Keep)
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

$dataFolder = Join-Path $env:APPDATA 'PuppyMacro Dev'
if (-not $Keep -and (Test-Path $dataFolder)) {
    Remove-Item $dataFolder -Recurse -Force
    Write-Host "Removed ${dataFolder}: starting from the samples"
}

dotnet build $project -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Start-Process -FilePath $exe
Write-Host "Started $exe"
