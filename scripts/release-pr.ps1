# make release-pr: prepares the release in the worktree the Makefile created from origin/main.
#   - next version = latest tag + minor if "## Unreleased" has Added/Changed, patch if only Fixed
#     (scripts/release-version.ps1);
#   - CHANGELOG.md: the Unreleased entries move to "## X.Y.Z", an empty "## Unreleased" stays above;
#   - PuppyMacro.csproj: <Version>X.Y.Z</Version>;
#   - -MessageFile: the commit message (and pull request title and body), "Release X.Y.Z".
# git and gh run in the Makefile, so this script only reads and writes files.
param(
    [Parameter(Mandatory)] [string]$Worktree,
    [Parameter(Mandatory)] [string]$TagsFile,
    [Parameter(Mandatory)] [string]$MessageFile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-version.ps1')
$utf8 = New-Object System.Text.UTF8Encoding $false
# .NET file calls do not follow PowerShell's current location: use full paths.
$Worktree = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $Worktree))
$TagsFile = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $TagsFile))
$MessageFile = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $MessageFile))

$latest = Get-LatestVersion @(Get-Content $TagsFile)
if (-not $latest) { throw 'No release tag vX.Y.Z found.' }

$changelogPath = Join-Path $Worktree 'CHANGELOG.md'
$lines = [System.IO.File]::ReadAllLines($changelogPath, $utf8)
$unreleased = Get-ChangelogSection $lines 'Unreleased'
if ($null -eq $unreleased) { throw "CHANGELOG.md has no '## Unreleased' section." }
$version = Get-NextVersion $latest $unreleased 'Unreleased'

# "## Unreleased" + its entries  ->  "## Unreleased" (empty) + "## X.Y.Z" + the entries.
$entries = ($unreleased -join "`n").Trim()
$out = New-Object System.Collections.Generic.List[string]
$inUnreleased = $false
foreach ($line in $lines) {
    if ($line.Trim() -eq '## Unreleased') {
        $out.Add('## Unreleased'); $out.Add(''); $out.Add("## $version"); $out.Add(''); $out.Add($entries); $out.Add('')
        $inUnreleased = $true
        continue
    }
    if ($inUnreleased -and $line -match '^## ') { $inUnreleased = $false }
    if (-not $inUnreleased) { $out.Add($line) }
}
[System.IO.File]::WriteAllText($changelogPath, (($out -join "`n").TrimEnd() + "`n"), $utf8)

$csprojPath = Join-Path $Worktree 'PuppyMacro/PuppyMacro.csproj'
$csproj = [System.IO.File]::ReadAllText($csprojPath, $utf8)
$updated = [regex]::Replace($csproj, '<Version>[^<]*</Version>', "<Version>$version</Version>")
if ($updated -eq $csproj) { throw "PuppyMacro.csproj: <Version> not found or already $version." }
[System.IO.File]::WriteAllText($csprojPath, $updated, $utf8)

$message = "Release $version`n`nMerging this pull request publishes PuppyMacro $version.`n`n$entries`n"
[System.IO.File]::WriteAllText($MessageFile, $message, $utf8)
Write-Host "Prepared release $version (previous: v$latest)"
