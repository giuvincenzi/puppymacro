# make build: the distribution of the version in PuppyMacro.csproj, in dist/:
# PuppyMacro-win-Setup.exe (installer) and the packages the installed app downloads to update.
# The Release workflow runs this same script, then uploads dist/ to GitHub.
param(
    # Markdown file with the release notes (the workflow passes the CHANGELOG section).
    [string]$ReleaseNotes,
    # Repository to download the previous release from, to build the delta update package.
    [string]$PreviousReleaseRepo,
    [string]$Token
)
$ErrorActionPreference = 'Stop'
$VpkVersion = '1.2.161'   # Velopack packaging tool; keep it equal to the Velopack package in PuppyMacro.csproj
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$vpk = Join-Path $root '.tools/vpk.exe'

$version = ([xml](Get-Content PuppyMacro/PuppyMacro.csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in PuppyMacro/PuppyMacro.csproj." }
Write-Host "Building the distribution of PuppyMacro $version"

function Invoke-Checked {
    & $args[0] $args[1..($args.Count - 1)]
    if ($LASTEXITCODE -ne 0) { throw "Failed: $($args -join ' ')" }
}

Invoke-Checked dotnet publish PuppyMacro/PuppyMacro.csproj -c Release -p:PublishProfile=Folder
# vpk in .tools/ (a tool manifest is refused when the repository is on a network path, e.g. WSL).
$hasVpk = (Test-Path $vpk) -and ((dotnet tool list --tool-path .tools) -match "^vpk\s+$([regex]::Escape($VpkVersion))\s")
if (-not $hasVpk) {
    if (Test-Path .tools) { Remove-Item .tools -Recurse -Force }
    Invoke-Checked dotnet tool install vpk --version $VpkVersion --tool-path .tools
}

if (Test-Path dist) { Remove-Item dist -Recurse -Force }
if ($PreviousReleaseRepo) {
    Invoke-Checked $vpk download github --repoUrl $PreviousReleaseRepo --token $Token --outputDir dist
}

$pack = @(
    'pack',
    '--packId', 'PuppyMacro',
    '--packVersion', $version,
    '--packTitle', 'PuppyMacro',
    '--packAuthors', 'Giuseppe Vincenzi',
    '--packDir', 'publish',
    '--mainExe', 'PuppyMacro.exe',
    '--icon', 'PuppyMacro/Assets/PuppyMacro.ico',
    '--framework', 'net10.0-x64-desktop,webview2',   # WebView2: the macro editor's Code view
    '--noPortable',   # a portable copy would not update itself
    '--outputDir', 'dist'
)
if ($ReleaseNotes) { $pack += @('--releaseNotes', $ReleaseNotes) }
Invoke-Checked $vpk @pack

Write-Host "Done: $(Join-Path $root 'dist/PuppyMacro-win-Setup.exe')"
