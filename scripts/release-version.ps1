# Release version rules, shared by scripts/release-pr.ps1 and scripts/release-check.ps1 (dot-source it).
#
# The next version is the latest tag vX.Y.Z plus:
#   - a minor step (X.Y+1.0) if the release has entries under "### Added" or "### Changed";
#   - a patch step (X.Y.Z+1) if it has entries only under "### Fixed".

$script:Subsections = @('Added', 'Changed', 'Fixed')

# Full path of a path given on the command line (.NET file calls do not follow PowerShell's
# current location). Absolute paths stay as they are.
function Get-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) { return [System.IO.Path]::GetFullPath($Path) }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $Path))
}

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

# Latest release tag ("vX.Y.Z") among the given tag names, as [version]. $null if there is none.
function Get-LatestVersion([string[]]$Tags) {
    $versions = $Tags | Where-Object { $_ -match '^v(\d+)\.(\d+)\.(\d+)$' } | ForEach-Object { [version]$_.Substring(1) }
    return ($versions | Sort-Object -Descending | Select-Object -First 1)
}

# The version that follows $Latest for a release whose CHANGELOG section is $Section.
function Get-NextVersion([version]$Latest, [string[]]$Section, [string]$Title) {
    $counts = Get-ChangelogEntries $Section $Title
    if ($counts.Added + $counts.Changed + $counts.Fixed -eq 0) {
        throw "CHANGELOG.md '## $Title' has no entries: nothing to release."
    }
    if ($counts.Added + $counts.Changed -gt 0) {
        return "$($Latest.Major).$($Latest.Minor + 1).0"
    }
    return "$($Latest.Major).$($Latest.Minor).$($Latest.Build + 1)"
}
