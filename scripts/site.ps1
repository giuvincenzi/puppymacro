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
# Where GitHub Pages publishes the site: canonical links, link previews and sitemap.xml need absolute URLs.
$SiteUrl = 'https://puppymacro.gviam.app/'

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
# Only the characters HTML needs escaped: accented letters and other scripts stay readable in the pages.
function Encode([string]$text) { $text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;') }
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

# The home page's languages: the app's languages (AppLanguages.All), each with its folder on the site, its flag in
# assets/flags (Arabic, spoken in many countries, shows a letter instead) and its writing direction.
$HomeLanguages = @(
    [pscustomobject]@{ Code = 'en'; Folder = ''; Flag = 'gb'; Rtl = $false }
    [pscustomobject]@{ Code = 'it'; Folder = 'it'; Flag = 'it'; Rtl = $false }
    [pscustomobject]@{ Code = 'fr'; Folder = 'fr'; Flag = 'fr'; Rtl = $false }
    [pscustomobject]@{ Code = 'es'; Folder = 'es'; Flag = 'es'; Rtl = $false }
    [pscustomobject]@{ Code = 'de'; Folder = 'de'; Flag = 'de'; Rtl = $false }
    [pscustomobject]@{ Code = 'pt-BR'; Folder = 'pt-br'; Flag = 'br'; Rtl = $false }
    [pscustomobject]@{ Code = 'ro'; Folder = 'ro'; Flag = 'ro'; Rtl = $false }
    [pscustomobject]@{ Code = 'sq'; Folder = 'sq'; Flag = 'al'; Rtl = $false }
    [pscustomobject]@{ Code = 'ru'; Folder = 'ru'; Flag = 'ru'; Rtl = $false }
    [pscustomobject]@{ Code = 'ar'; Folder = 'ar'; Flag = ''; Letter = [string][char]0x0639; Rtl = $true }
    [pscustomobject]@{ Code = 'ja'; Folder = 'ja'; Flag = 'jp'; Rtl = $false }
    [pscustomobject]@{ Code = 'ko'; Folder = 'ko'; Flag = 'kr'; Rtl = $false }
    [pscustomobject]@{ Code = 'zh-Hans'; Folder = 'zh-hans'; Flag = 'cn'; Rtl = $false }
)

# The texts of one language's home page, as an ordered dictionary. Every language has exactly the keys of en.json, except
# the slogan: it stays in English, so only en.json has it.
function Read-HomeTexts([string]$siteDir, [string]$code) {
    $path = Join-Path $siteDir "home/$code.json"
    if (-not (Test-Path $path)) { throw "site/home/$code.json is missing: every language of the home page needs its texts." }
    $texts = [ordered]@{}
    foreach ($p in (Read-Text $path | ConvertFrom-Json).PSObject.Properties) { $texts[$p.Name] = [string]$p.Value }
    if ($code -ne 'en') {
        $englishKeys = @((Read-Text (Join-Path $siteDir 'home/en.json') | ConvertFrom-Json).PSObject.Properties.Name)
        if ($texts.Contains('slogan')) { throw "site/home/$code.json has the slogan: it stays in English, only en.json has it." }
        $missing = @($englishKeys | Where-Object { $_ -ne 'slogan' -and -not $texts.Contains($_) })
        if ($missing.Count) { throw "site/home/$code.json misses $($missing -join ', ')." }
        $extra = @($texts.Keys | Where-Object { $englishKeys -notcontains $_ })
        if ($extra.Count) { throw "site/home/$code.json has $($extra -join ', '), which en.json does not have." }
    }
    if (-not $texts['fine_print'].Contains('{version}')) { throw "site/home/$code.json: fine_print lost {version}." }
    return $texts
}

# A language's flag: its round flag, or its letter.
function Get-Flag($language, [string]$rootPath) {
    if ($language.Flag) { return "<img class=""flag"" src=""${rootPath}assets/flags/$($language.Flag).svg"" alt="""">" }
    return "<span class=""flag flag-letter"" aria-hidden=""true"">$($language.Letter)</span>"
}

# The header's language menu on one home page: the current language in the bar, every language in a grid, by its own name
# (an ordinal sort, the same in Windows PowerShell and PowerShell 7: Latin names alphabetical, then the other scripts).
function Get-LanguageMenu($current, [string]$rootPath) {
    $sorted = [System.Collections.Generic.List[object]]::new()
    foreach ($l in $HomeLanguages) { $sorted.Add($l) }
    $sorted.Sort([Comparison[object]] { param($a, $b) [StringComparer]::Ordinal.Compare($a.Texts['language_name'], $b.Texts['language_name']) })
    $label = Encode $current.Texts['language_menu']
    $name = Encode $current.Texts['language_name']
    $links = foreach ($l in $sorted) {
        $href = if ($l.Folder) { "$rootPath$($l.Folder)/" } elseif ($rootPath) { $rootPath } else { './' }
        $currentMark = if ($l.Code -eq $current.Code) { ' aria-current="true"' } else { '' }
        "            <a href=""$href"" hreflang=""$($l.Code)"" lang=""$($l.Code)"" data-language=""$($l.Code)""$currentMark>$(Get-Flag $l $rootPath)<span>$(Encode $l.Texts['language_name'])</span></a>"
    }
    @(
        '      <details class="language-menu">'
        "        <summary aria-label=""${label}: $name"">$(Get-Flag $current $rootPath)<span class=""language-name"">$name</span><svg viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2.2"" stroke-linecap=""round"" stroke-linejoin=""round"" aria-hidden=""true""><path d=""M6 9l6 6 6-6""/></svg></summary>"
        '        <div class="language-popup">'
        "          <p>$label</p>"
        '          <div class="language-grid">'
        $links
        '          </div>'
        '        </div>'
        '      </details>'
    ) -join "`n"
}

# The English home's banner, filled by site.js with the texts of the browser's language: each language's offer as JSON.
function Get-LanguageBanner {
    $offers = [ordered]@{}
    foreach ($l in $HomeLanguages | Where-Object { $_.Code -ne 'en' }) {
        $offers[$l.Code] = [ordered]@{
            text = $l.Texts['banner_text']; switch = $l.Texts['banner_switch']; stay = $l.Texts['banner_stay']
            url = "$($l.Folder)/"; dir = $(if ($l.Rtl) { 'rtl' } else { 'ltr' })
            flag = $(if ($l.Flag) { "assets/flags/$($l.Flag).svg" } else { $null }); letter = $l.Letter
        }
    }
    @(
        '<div class="language-banner" id="language-banner" hidden>'
        '  <div class="wrap">'
        '    <span class="flag"></span>'
        '    <span class="language-banner-text"></span>'
        '    <a class="button button-primary button-small" href="./"></a>'
        '    <button type="button" class="button button-secondary button-small"></button>'
        '  </div>'
        '</div>'
        "<script type=""application/json"" id=""language-offers"">$(ConvertTo-Json $offers -Compress -Depth 3)</script>"
    ) -join "`n"
}

function Set-NavTexts([string]$html, $texts) {
    $html.Replace('{{NAV_LABEL}}', (Encode $texts['nav_label'])).
        Replace('{{NAV_FEATURES}}', (Encode $texts['nav_features'])).
        Replace('{{NAV_GUIDE}}', (Encode $texts['nav_guide'])).
        Replace('{{NAV_PRIVACY}}', (Encode $texts['nav_privacy'])).
        Replace('{{NAV_MENU}}', (Encode $texts['nav_menu'])).
        Replace('{{NAV_DOWNLOAD}}', (Encode $texts['nav_download']))
}

function Set-FooterTexts([string]$html, $texts) {
    $html.Replace('{{FOOTER_LICENSE}}', (Encode $texts['footer_license'])).
        Replace('{{FOOTER_RELEASES}}', (Encode $texts['footer_releases']))
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

    # Home pages, one per language (discoverability.md): the texts of site/home/<code>.json filled into index.html.
    $homeTemplate = Read-Text (Join-Path $chrome 'index.html')
    $english = Read-HomeTexts $chrome 'en'
    foreach ($language in $HomeLanguages) {
        $language | Add-Member Texts $(if ($language.Code -eq 'en') { $english } else { Read-HomeTexts $chrome $language.Code })
        $language | Add-Member Url ($SiteUrl + $(if ($language.Folder) { "$($language.Folder)/" } else { '' }))
    }
    $alternates = @($HomeLanguages | ForEach-Object { "<link rel=""alternate"" hreflang=""$($_.Code)"" href=""$($_.Url)"">" }) +
        "<link rel=""alternate"" hreflang=""x-default"" href=""$SiteUrl"">"
    foreach ($language in $HomeLanguages) {
        $texts = $language.Texts
        $rootPath = if ($language.Folder) { '../' } else { '' }
        $homeHeader = $header.
            Replace('{{LANGUAGE_MENU}}', (Get-LanguageMenu $language $rootPath)).
            Replace('{{HOME}}', './').Replace('{{ROOT}}', $rootPath).Replace('{{GUIDE_CURRENT}}', '')
        $banner = if ($language.Code -eq 'en') { Get-LanguageBanner } else { '' }
        $page = $homeTemplate.
            Replace('{{HEADER}}', (Set-NavTexts $homeHeader $texts)).
            Replace('{{BANNER}}', $banner).
            Replace('{{FOOTER}}', (Set-FooterTexts $footer $texts)).
            Replace('{{ALTERNATES}}', ($alternates -join "`n")).
            Replace('{{PAGE_URL}}', $language.Url).
            Replace('{{LANG}}', $language.Code).
            Replace('{{DIR}}', $(if ($language.Rtl) { 'rtl' } else { 'ltr' })).
            Replace('{{ROOT}}', $rootPath).
            Replace('{{VERSION}}', $latest).
            Replace('{{SITE_URL}}', $SiteUrl)
        $values = [ordered]@{}
        foreach ($key in $texts.Keys) { $values[$key] = $texts[$key] }
        $values['slogan'] = $english['slogan']
        $values['description'] = "$($texts['summary']) $($texts['free_and_open_source'])"
        $values['fine_print'] = $texts['fine_print'].Replace('{version}', $latest)
        foreach ($key in $values.Keys) {
            $page = $page.
                Replace("{{attr:$key}}", (Encode $values[$key])).
                Replace("{{json:$key}}", (ConvertTo-Json $values[$key] -Compress)).
                Replace("{{$key}}", $values[$key])
        }
        if ($page -match '\{\{[^}]+\}\}') { throw "index.html has no text for $($Matches[0]) in site/home/$($language.Code).json" }
        $file = if ($language.Folder) { "$($language.Folder)/index.html" } else { 'index.html' }
        Write-Text (Join-Path $Output $file) $page
    }

    # The newest guide: guide/latest/ opens it, every version's page points to its page as the canonical one (so search
    # engines show the newest guide, not an older copy of it) and sitemap.xml lists its pages.
    $newest = if ($Preview) { $guides[0] } else { $guides | Where-Object Name -eq $latest }

    # The guide is only in English: the English texts, no language menu, links to the English home.
    $guideHeader = Set-NavTexts ($header.
        Replace('{{LANGUAGE_MENU}}', '').Replace('{{HOME}}', '../../').Replace('{{ROOT}}', '../../').
        Replace('{{GUIDE_CURRENT}}', ' aria-current="page"')) $english
    $guideFooter = Set-FooterTexts $footer $english
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
            $canonicalVersion = if ($newest.Sections.Slug -contains $s.Slug) { $newest.Name } else { $g.Name }
            $content = (Read-Text (Join-Path $g.Dir "guide/$($s.Slug).html")).TrimEnd()
            $content = ($content -split "`n" | ForEach-Object { if ($_.Trim()) { "    $_" } else { '' } }) -join "`n"
            $page = $template.
                Replace('{{HEADER}}', $guideHeader).Replace('{{FOOTER}}', $guideFooter).
                Replace('{{VERSION_OPTIONS}}', ($options -join "`n")).
                Replace('{{SECTION_LINKS}}', ($links -join "`n")).
                Replace('{{PAGER}}', ($pager -join "`n")).
                Replace('{{CONTENT}}', $content).
                Replace('{{CANONICAL}}', "${SiteUrl}guide/$canonicalVersion/$($s.Slug).html").
                Replace('{{SITE_URL}}', $SiteUrl).
                Replace('{{TITLE}}', (Encode $s.Title)).
                Replace('{{VERSION}}', $g.Name)
            Write-Text (Join-Path $out "$($s.Slug).html") $page
        }
        Write-Text (Join-Path $out 'index.html') (Redirect "$($sections[0].Slug).html")
    }

    # guide/latest/ always opens the newest released guide (the preview opens "next").
    foreach ($s in $newest.Sections) {
        Write-Text (Join-Path $Output "guide/latest/$($s.Slug).html") (Redirect "../$($newest.Name)/$($s.Slug).html")
    }
    Write-Text (Join-Path $Output 'guide/latest/index.html') (Redirect "../$($newest.Name)/")
    Write-Text (Join-Path $Output 'guide/index.html') (Redirect 'latest/')

    # sitemap.xml: the pages search engines should read, the home page in every language and the newest guide (submitted
    # in Google Search Console and Bing Webmaster Tools; a project site has no robots.txt of its own to name it).
    $urls = @($HomeLanguages | ForEach-Object { $_.Url }) +
        @($newest.Sections | ForEach-Object { "${SiteUrl}guide/$($newest.Name)/$($_.Slug).html" })
    $entries = $urls | ForEach-Object { "  <url><loc>$(Encode $_)</loc></url>" }
    Write-Text (Join-Path $Output 'sitemap.xml') (
        "<?xml version=""1.0"" encoding=""UTF-8""?>`n" +
        "<urlset xmlns=""http://www.sitemaps.org/schemas/sitemap/0.9"">`n" + ($entries -join "`n") + "`n</urlset>`n")

    Write-Host "Site built in $(Join-Path $root $Output): guide versions $(($guides.Name) -join ', ')"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
