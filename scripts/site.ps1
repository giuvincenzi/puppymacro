# Builds the website: the home page and one user guide per released version.
#
#   make docs   -> scripts/site.ps1 -Preview   preview in site-preview/, the guide of the
#                                               current code is added as version "next"
#   Release     -> scripts/site.ps1             published site in _site/, released versions only
#
# Each guide version comes from site/guide/ at the git tag vX.Y.Z of that release, so the guide
# of an old version never changes. Home page, page template and style come from the latest
# release (or from the current code with -Preview), so every version has the same look.
param(
    [string]$Output,
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $Output) { $Output = if ($Preview) { 'site-preview' } else { '_site' } }
$utf8 = New-Object System.Text.UTF8Encoding $false

function Read-Text([string]$path) { [System.IO.File]::ReadAllText($path, $utf8) }
function Write-Text([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}
# git commands whose failure is an answer, not an error (Windows PowerShell stops on native stderr).
function Test-Git {
    $ErrorActionPreference = 'Continue'
    & git @args 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}
function Encode([string]$text) { [System.Net.WebUtility]::HtmlEncode($text) }
function Redirect([string]$target) {
    "<!doctype html><html lang=""en""><head><meta charset=""utf-8""><title>PuppyMacro guide</title>" +
    "<meta http-equiv=""refresh"" content=""0; url=$target""><link rel=""canonical"" href=""$target""></head>" +
    "<body><a href=""$target"">PuppyMacro guide</a></body></html>"
}

# Sections of one guide version: "slug|Title" lines of sections.txt.
function Read-Sections([string]$guideDir) {
    Get-Content -Encoding UTF8 (Join-Path $guideDir 'sections.txt') |
        Where-Object { $_ -and -not $_.StartsWith('#') } |
        ForEach-Object { $parts = $_.Split('|', 2); [pscustomobject]@{ Slug = $parts[0].Trim(); Title = $parts[1].Trim() } }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("puppymacro-site-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # Released versions that have the site: tags vX.Y.Z with site/guide/sections.txt, newest first.
    # They need git; a preview without git (e.g. WSL with git only on the Linux side) shows "next" only.
    $hasGit = [bool](Get-Command git -ErrorAction SilentlyContinue)
    if (-not $hasGit -and -not $Preview) { throw 'git is required to build the released guide versions.' }
    if (-not $hasGit) { Write-Warning 'git not found by Windows PowerShell: the preview shows only the guide of the current code.' }
    if ($hasGit) { Test-Git fetch --tags --quiet | Out-Null }
    $versions = @(if ($hasGit) {
        git tag --list 'v*' |
            Where-Object { $_ -match '^v(\d+)\.(\d+)\.(\d+)$' } |
            ForEach-Object { [pscustomobject]@{ Name = $_.Substring(1); Tag = $_; Sort = [version]$_.Substring(1) } } |
            Where-Object { Test-Git cat-file -e "$($_.Tag):site/guide/sections.txt" } |
            Sort-Object Sort -Descending
    })
    foreach ($v in $versions) {
        $zip = Join-Path $work "$($v.Name).zip"
        git archive --format=zip -o $zip $v.Tag site
        if ($LASTEXITCODE -ne 0) { throw "git archive failed for $($v.Tag)" }
        Expand-Archive -Path $zip -DestinationPath (Join-Path $work $v.Name)
        $v | Add-Member Dir (Join-Path $work "$($v.Name)/site")
    }

    $guides = @()
    if ($Preview) { $guides += [pscustomobject]@{ Name = 'next'; Label = 'next (unreleased)'; Dir = (Join-Path $root 'site') } }
    for ($i = 0; $i -lt $versions.Count; $i++) {
        $label = if ($i -eq 0) { "$($versions[$i].Name) (latest)" } else { $versions[$i].Name }
        $guides += [pscustomobject]@{ Name = $versions[$i].Name; Label = $label; Dir = $versions[$i].Dir }
    }
    if ($guides.Count -eq 0) { throw 'No release has site/guide yet. Use make docs to preview.' }
    foreach ($g in $guides) { $g | Add-Member Sections @(Read-Sections (Join-Path $g.Dir 'guide')) }

    # Look and home page: current code for a preview, else the latest release.
    $chrome = if ($Preview) { Join-Path $root 'site' } else { $versions[0].Dir }
    $latest = if ($versions.Count -gt 0) { $versions[0].Name } else { 'next' }

    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
    New-Item -ItemType Directory -Path $Output | Out-Null
    Copy-Item (Join-Path $chrome 'assets') (Join-Path $Output 'assets') -Recurse
    Copy-Item (Join-Path $chrome 'favicon.ico') $Output

    $header = Read-Text (Join-Path $chrome '_header.html')
    $footer = Read-Text (Join-Path $chrome '_footer.html')
    $template = Read-Text (Join-Path $chrome 'guide/_page.html')

    $homePage = (Read-Text (Join-Path $chrome 'index.html')).
        Replace('{{HEADER}}', $header.Replace('{{ROOT}}', '').Replace('{{GUIDE_CURRENT}}', '')).
        Replace('{{FOOTER}}', $footer).
        Replace('{{VERSION}}', $latest)
    Write-Text (Join-Path $Output 'index.html') $homePage

    $guideHeader = $header.Replace('{{ROOT}}', '../../').Replace('{{GUIDE_CURRENT}}', ' aria-current="page"')
    foreach ($g in $guides) {
        $out = Join-Path $Output "guide/$($g.Name)"
        $img = Join-Path $g.Dir 'guide/img'
        if (Test-Path $img) { Copy-Item $img (Join-Path $out 'img') -Recurse -Force }
        $sections = $g.Sections
        for ($i = 0; $i -lt $sections.Count; $i++) {
            $s = $sections[$i]
            $options = foreach ($other in $guides) {
                $target = if ($other.Sections.Slug -contains $s.Slug) { "$($s.Slug).html" } else { "$($other.Sections[0].Slug).html" }
                $selected = if ($other.Name -eq $g.Name) { ' selected' } else { '' }
                "        <option value=""../$($other.Name)/$target""$selected>$(Encode $other.Label)</option>"
            }
            $links = foreach ($l in $sections) {
                $current = if ($l.Slug -eq $s.Slug) { ' aria-current="page"' } else { '' }
                "      <a href=""$($l.Slug).html""$current>$(Encode $l.Title)</a>"
            }
            $pager = @()
            if ($i -gt 0) { $p = $sections[$i - 1]; $pager += "      <a class=""prev"" href=""$($p.Slug).html""><span>Previous</span>$(Encode $p.Title)</a>" }
            if ($i -lt $sections.Count - 1) { $n = $sections[$i + 1]; $pager += "      <a class=""next"" href=""$($n.Slug).html""><span>Next</span>$(Encode $n.Title)</a>" }
            $content = (Read-Text (Join-Path $g.Dir "guide/$($s.Slug).html")).TrimEnd()
            $content = ($content -split "`n" | ForEach-Object { if ($_.Trim()) { "    $_" } else { '' } }) -join "`n"
            $page = $template.
                Replace('{{HEADER}}', $guideHeader).Replace('{{FOOTER}}', $footer).
                Replace('{{VERSION_OPTIONS}}', ($options -join "`n")).
                Replace('{{SECTION_LINKS}}', ($links -join "`n")).
                Replace('{{PAGER}}', ($pager -join "`n")).
                Replace('{{CONTENT}}', $content).
                Replace('{{TITLE}}', (Encode $s.Title)).
                Replace('{{VERSION}}', $g.Name)
            Write-Text (Join-Path $out "$($s.Slug).html") $page
        }
        Write-Text (Join-Path $out 'index.html') (Redirect "$($sections[0].Slug).html")
    }

    # guide/latest/ always opens the newest released guide (the preview opens "next").
    $newest = if ($Preview) { $guides[0] } else { $guides | Where-Object Name -eq $latest }
    foreach ($s in $newest.Sections) {
        Write-Text (Join-Path $Output "guide/latest/$($s.Slug).html") (Redirect "../$($newest.Name)/$($s.Slug).html")
    }
    Write-Text (Join-Path $Output 'guide/latest/index.html') (Redirect "../$($newest.Name)/")
    Write-Text (Join-Path $Output 'guide/index.html') (Redirect 'latest/')

    Write-Host "Site built in $(Join-Path $root $Output): guide versions $(($guides.Name) -join ', ')"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
