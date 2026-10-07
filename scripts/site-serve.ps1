# make docs-serve: builds the website preview (like make docs) and serves it at
# http://localhost:<Port>/ until Ctrl+C. Uses .NET's HttpListener: nothing to install.
param(
    [int]$Port = 8080,
    # Do not open the browser (for checks).
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

& (Join-Path $PSScriptRoot 'site.ps1') -Preview
$site = Join-Path $root 'site-preview'

$types = @{
    '.html' = 'text/html; charset=utf-8'; '.css' = 'text/css; charset=utf-8'; '.js' = 'text/javascript; charset=utf-8'
    '.png' = 'image/png'; '.jpg' = 'image/jpeg'; '.svg' = 'image/svg+xml'; '.ico' = 'image/x-icon'; '.json' = 'application/json'
}

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
$url = "http://localhost:$Port/"
Write-Host "Serving $site at $url (Ctrl+C to stop)"
if (-not $NoBrowser) { Start-Process $url }

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $path = [System.Uri]::UnescapeDataString($context.Request.Url.AbsolutePath).TrimStart('/')
        $file = Join-Path $site $path
        if (Test-Path $file -PathType Container) { $file = Join-Path $file 'index.html' }

        $fullSite = [System.IO.Path]::GetFullPath($site)
        $fullFile = [System.IO.Path]::GetFullPath($file)
        $response = $context.Response
        if ($fullFile.StartsWith($fullSite, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path $fullFile -PathType Leaf)) {
            $bytes = [System.IO.File]::ReadAllBytes($fullFile)
            $extension = [System.IO.Path]::GetExtension($fullFile).ToLowerInvariant()
            $response.ContentType = if ($types.ContainsKey($extension)) { $types[$extension] } else { 'application/octet-stream' }
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        else {
            $response.StatusCode = 404
        }
        $response.Close()
    }
}
finally {
    $listener.Stop()
}
