# Writes server.json, PuppyMacro's entry in the official MCP Registry (discoverability.md), from the title in
# site/home/en.json and the version in PuppyMacro/Version.props. Nothing of it is kept in the repository: the workflows
# build it when they need it (test: readme_and_mcp_registry_test checks it; publish_release: publish_mcp_registry
# publishes it).
#
#   pwsh scripts/mcp-server-json.ps1 -Output server.json
param(
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding $false

$texts = [System.IO.File]::ReadAllText((Join-Path $root 'site/home/en.json'), $utf8) | ConvertFrom-Json
[xml]$props = [System.IO.File]::ReadAllText((Join-Path $root 'PuppyMacro/Version.props'), $utf8)
$version = ([string]$props.Project.PropertyGroup.Version).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "PuppyMacro/Version.props has no X.Y.Z version: '$version'." }

# The registry takes at most 100 characters; an entry has no package, so it must say the app is needed.
$description = "$($texts.title). Needs the PuppyMacro app installed."
if ($description.Length -gt 100) { throw "The MCP Registry description has $($description.Length) characters, over 100: '$description'." }

$server = [ordered]@{
    '$schema' = 'https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json'
    name = 'io.github.giuvincenzi/puppymacro'
    title = 'PuppyMacro'
    description = $description
    version = $version
    websiteUrl = 'https://puppymacro.gviam.app/guide/latest/ai.html'
    repository = [ordered]@{ url = 'https://github.com/giuvincenzi/puppymacro'; source = 'github'; id = '1409153612' }
    icons = @([ordered]@{ src = 'https://puppymacro.gviam.app/assets/logo.png'; mimeType = 'image/png'; sizes = @('256x256') })
}
[System.IO.File]::WriteAllText($Output, (ConvertTo-Json $server -Depth 5), $utf8)
Write-Host "$Output written: $($server.name) $version"
