# Release functions (dot-source this file): the version in PuppyMacro/Version.props and the CHANGELOG sections.
# Used by the create_release_pr workflow (the next version, the changelog section, the version file), the
# publish_release workflow (the version and its release notes) and make build (the version).
#
# The next version is the latest tag vX.Y.Z plus:
#   - a minor step (X.Y+1.0) if "## Unreleased" has entries under "### Added" or "### Changed";
#   - a patch step (X.Y.Z+1) if it has entries only under "### Fixed".

$script:Subsections = @('Added', 'Changed', 'Fixed')
$script:Utf8 = New-Object System.Text.UTF8Encoding $false

# A file of the repository at $Root, as a full path: .NET's file calls resolve a relative path against the process's
# current folder, not PowerShell's.
function Get-RepoFile([string]$Root, [string]$Path) { Join-Path (Resolve-Path $Root).ProviderPath $Path }

function Get-VersionFile([string]$Root) { Get-RepoFile $Root 'PuppyMacro/Version.props' }

# The version in PuppyMacro/Version.props ("1.17.0").
function Get-ProjectVersion([string]$Root) {
    $version = ([xml](Get-Content (Get-VersionFile $Root))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "PuppyMacro/Version.props: <Version> '$version' is not X.Y.Z." }
    return $version
}

function Set-ProjectVersion([string]$Root, [string]$Version) {
    $path = Get-VersionFile $Root
    $text = [System.IO.File]::ReadAllText($path, $script:Utf8)
    $updated = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
    if ($updated -eq $text) { throw "PuppyMacro/Version.props: <Version> not found or already $Version." }
    [System.IO.File]::WriteAllText($path, $updated, $script:Utf8)
}

function Read-Changelog([string]$Root) { [System.IO.File]::ReadAllLines((Get-RepoFile $Root 'CHANGELOG.md'), $script:Utf8) }

# Lines of a "## <title>" section of CHANGELOG.md, without its heading. $null if missing.
function Get-ChangelogSection([string[]]$Lines, [string]$Title) {
    $found = $false
    $section = New-Object System.Collections.Generic.List[string]
    foreach ($line in $Lines) {
        if ($line -match '^## ') {
            if ($found) { break }
            $found = $line.Trim() -eq "## $Title"
            continue
        }
        if ($found) { $section.Add($line) }
    }
    if ($found) { return , $section.ToArray() }
    return $null
}

# Number of entries ("- " lines) per subsection. Fails on entries outside a known subsection.
function Get-ChangelogEntries([string[]]$Section, [string]$Title) {
    $counts = @{ Added = 0; Changed = 0; Fixed = 0 }
    $current = $null
    foreach ($line in $Section) {
        if ($line -match '^### (.+)$') {
            $current = $Matches[1].Trim()
            if ($script:Subsections -notcontains $current) {
                throw "CHANGELOG.md '## $Title': unknown subsection '### $current' (use ### Added, ### Changed or ### Fixed)."
            }
            continue
        }
        if ($line -match '^- ') {
            if (-not $current) { throw "CHANGELOG.md '## $Title': entry outside ### Added / ### Changed / ### Fixed: $line" }
            $counts[$current]++
        }
    }
    return $counts
}

# The entries of "## Unreleased"; fails when it is missing or has none (nothing to release).
function Get-UnreleasedSection([string[]]$Lines) {
    $section = Get-ChangelogSection $Lines 'Unreleased'
    if ($null -eq $section) { throw "CHANGELOG.md has no '## Unreleased' section." }
    $counts = Get-ChangelogEntries $section 'Unreleased'
    if ($counts.Added + $counts.Changed + $counts.Fixed -eq 0) {
        throw "CHANGELOG.md '## Unreleased' has no entries: nothing to release."
    }
    return , $section
}

# Latest release tag ("vX.Y.Z") among the given tag names, as [version]. $null if there is none.
function Get-LatestVersion([string[]]$Tags) {
    $versions = $Tags | Where-Object { $_ -match '^v(\d+)\.(\d+)\.(\d+)$' } | ForEach-Object { [version]$_.Substring(1) }
    return ($versions | Sort-Object -Descending | Select-Object -First 1)
}

# The version that follows $Latest for the entries of $Section (minor with Added or Changed, patch with Fixed only).
function Get-NextVersion([version]$Latest, [string[]]$Section) {
    $counts = Get-ChangelogEntries $Section 'Unreleased'
    if ($counts.Added + $counts.Changed -gt 0) {
        return "$($Latest.Major).$($Latest.Minor + 1).0"
    }
    return "$($Latest.Major).$($Latest.Minor).$($Latest.Build + 1)"
}

# "## Unreleased" + its entries  ->  "## Unreleased" (empty) + "## X.Y.Z" + the entries. Returns the entries.
function Move-UnreleasedEntries([string]$Root, [string]$Version) {
    $lines = Read-Changelog $Root
    $entries = ((Get-UnreleasedSection $lines) -join "`n").Trim()
    $out = New-Object System.Collections.Generic.List[string]
    $inUnreleased = $false
    foreach ($line in $lines) {
        if ($line.Trim() -eq '## Unreleased') {
            $out.Add('## Unreleased'); $out.Add(''); $out.Add("## $Version"); $out.Add(''); $out.Add($entries); $out.Add('')
            $inUnreleased = $true
            continue
        }
        if ($inUnreleased -and $line -match '^## ') { $inUnreleased = $false }
        if (-not $inUnreleased) { $out.Add($line) }
    }
    [System.IO.File]::WriteAllText((Get-RepoFile $Root 'CHANGELOG.md'), (($out -join "`n").TrimEnd() + "`n"), $script:Utf8)
    return $entries
}

# The release notes of a version: its CHANGELOG section. Fails when it is missing or empty.
function Get-ReleaseNotes([string]$Root, [string]$Version) {
    $section = Get-ChangelogSection (Read-Changelog $Root) $Version
    if ($null -eq $section -or -not ($section | Where-Object { $_ -match '^- ' })) {
        throw "CHANGELOG.md has no entries under '## $Version'."
    }
    return ($section -join "`n").Trim()
}

# A value for the next steps of a GitHub workflow job (and on the console).
function Set-StepOutput([string]$Name, [string]$Value) {
    if ($env:GITHUB_OUTPUT) { Add-Content -Path $env:GITHUB_OUTPUT -Value "$Name=$Value" -Encoding utf8 }
    Write-Host "$Name=$Value"
}
