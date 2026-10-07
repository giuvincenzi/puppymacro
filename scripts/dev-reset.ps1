# make dev-reset: deletes the development build's data (%AppData%\PuppyMacro Dev).
# The next `make dev` starts again with the sample loops, macros and remaps.
$root = Split-Path -Parent $PSScriptRoot
$devExe = Join-Path $root 'PuppyMacro/bin/Debug/net10.0-windows/win-x64/PuppyMacro.exe'
$dataFolder = Join-Path $env:APPDATA 'PuppyMacro Dev'

# A running development build would write its data again when it closes.
Get-Process -Name PuppyMacro -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq [System.IO.Path]::GetFullPath($devExe) } |
    ForEach-Object {
        Write-Host "Closing the running development build"
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit()
    }

if (Test-Path $dataFolder) {
    Remove-Item $dataFolder -Recurse -Force
    Write-Host "Removed $dataFolder"
} else {
    Write-Host "Nothing to remove: $dataFolder does not exist"
}
