# Release workflow: decides whether the version in PuppyMacro.csproj is published, and checks it.
#   - its tag vX.Y.Z already exists: nothing to publish (publish=false), not an error;
#   - otherwise CHANGELOG.md must have "## X.Y.Z", nothing under "## Unreleased", and X.Y.Z must
#     be exactly the next version after the latest tag (scripts/release-version.ps1).
# Writes "publish" and "version" to $GITHUB_OUTPUT and the release notes to -NotesFile.
param(
    [Parameter(Mandatory)] [string]$NotesFile,
    # Tag names; default: the repository's tags (the workflow checks out all of them).
    [string[]]$Tags
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-version.ps1')
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Set-Output([string]$name, [string]$value) {
    if ($env:GITHUB_OUTPUT) { Add-Content -Path $env:GITHUB_OUTPUT -Value "$name=$value" -Encoding utf8 }
    Write-Host "$name=$value"
}

$version = ([xml](Get-Content PuppyMacro/PuppyMacro.csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "PuppyMacro.csproj <Version> '$version' is not X.Y.Z." }
Set-Output 'version' $version

if (-not $Tags) {
    $Tags = @(git tag --list 'v*')
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the tags.' }
}
if ($Tags -contains "v$version") {
    Write-Host "v$version already exists: nothing to release."
    Set-Output 'publish' 'false'
    exit 0
}

$lines = Get-Content CHANGELOG.md -Encoding UTF8
$section = Get-ChangelogSection $lines $version
if ($null -eq $section) { throw "CHANGELOG.md has no '## $version' section." }
$unreleased = Get-ChangelogSection $lines 'Unreleased'
if ($unreleased | Where-Object { $_ -match '^- ' }) {
    throw "CHANGELOG.md still has entries under '## Unreleased': they belong to '## $version'."
}

$latest = Get-LatestVersion $Tags
if ($latest) {
    $expected = Get-NextVersion $latest $section $version
    if ($expected -ne $version) {
        throw "PuppyMacro.csproj says $version, but after v$latest the CHANGELOG makes it $expected. Use make release-pr."
    }
}
else {
    Get-NextVersion ([version]'0.0.0') $section $version | Out-Null # validates the section
}

[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $NotesFile)),
    (($section -join "`n").Trim() + "`n"), (New-Object System.Text.UTF8Encoding $false))
Write-Host "Releasing PuppyMacro $version"
Set-Output 'publish' 'true'
