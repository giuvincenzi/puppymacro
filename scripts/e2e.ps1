# make e2e: builds the development build and the end-to-end tests, then runs them.
# The tests close any running PuppyMacro (also the installed one), use the development build's
# data folder (%AppData%\PuppyMacro Dev) and move the mouse and press keys: do not use the PC
# while they run.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Get-Process -Name PuppyMacro -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Closing the running PuppyMacro ($($_.Path))"
    Stop-Process -Id $_.Id -Force
    $_.WaitForExit()
}

dotnet build PuppyMacro/PuppyMacro.csproj -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test --project tests/PuppyMacro.E2E/PuppyMacro.E2E.csproj
exit $LASTEXITCODE
