# make clean: removes build output.
$root = Split-Path -Parent $PSScriptRoot
foreach ($dir in 'PuppyMacro/bin', 'PuppyMacro/obj', 'publish', 'dist', 'site-preview', '_site') {
    $path = Join-Path $root $dir
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
        Write-Host "Removed $dir"
    }
}
