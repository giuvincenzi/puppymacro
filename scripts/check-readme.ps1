# Checks that README.md says the title and description written in site/home/en.json (discoverability.md): the text
# between its "Title and description" comments must be "<title>. <summary> <free_and_open_source>". Run by the test
# workflow (readme_and_mcp_registry_test).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding $false

$texts = [System.IO.File]::ReadAllText((Join-Path $root 'site/home/en.json'), $utf8) | ConvertFrom-Json
$expected = "$($texts.title). $($texts.summary) $($texts.free_and_open_source)"

$readme = [System.IO.File]::ReadAllText((Join-Path $root 'README.md'), $utf8)
$match = [regex]::Match($readme, '<!-- Title and description:[^\n]*-->\s*(?<text>[\s\S]*?)\s*<!-- End of title and description -->')
if (-not $match.Success) { throw 'README.md has lost its "Title and description" comments.' }
$actual = ($match.Groups['text'].Value -replace '\s+', ' ').Trim()
if ($actual -ne $expected) {
    throw "README.md does not say the title and description of site/home/en.json. Write between its comments:`n$expected"
}
Write-Host 'README.md says the title and description of site/home/en.json.'
