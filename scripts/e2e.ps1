# make e2e: builds the development build and the end-to-end tests, then runs them.
# make e2e CATEGORY=Macros runs only the tests of that category (their [Trait("Category", ...)]:
# UI, Macros, Loops, Remaps, Recording, Overlay).
# The tests close any running PuppyMacro (also the installed one), use the development build's
# data folder (%AppData%\PuppyMacro Dev) and move the mouse and press keys: do not use the PC
# while they run.
param([string]$Category = '')
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

$filter = @()
if ($Category) { $filter = @('--filter-trait', "Category=$Category") }
dotnet test --project tests/PuppyMacro.E2E/PuppyMacro.E2E.csproj @filter
exit $LASTEXITCODE
