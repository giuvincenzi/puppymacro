# make test: builds and runs the unit tests (tests/PuppyMacro.Tests, xUnit v3).
# Release configuration: the code users get, and a running dev build (bin/Debug) does not lock it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
dotnet test --project tests/PuppyMacro.Tests/PuppyMacro.Tests.csproj -c Release
exit $LASTEXITCODE
