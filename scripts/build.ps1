# make build: the distribution of the version in PuppyMacro.csproj, in dist/:
# PuppyMacro-win-Setup.exe (installer) and the packages the installed app downloads to update.
# The Release workflow runs this same script, then uploads dist/ to GitHub.
# Code signing (Certum, through ssign): on when CERTUM_EMAIL and CERTUM_OTP (the SimplySign TOTP
# seed) or CERTUM_TOKEN (a 6-digit code from the SimplySign app) are set; see build-and-release.md.
param(
    # Markdown file with the release notes (the workflow passes the CHANGELOG section).
    [string]$ReleaseNotes,
    # Repository to download the previous release from, to build the delta update package.
    [string]$PreviousReleaseRepo,
    [string]$Token,
    # Fail instead of building unsigned files (the Release workflow).
    [switch]$RequireSigning
)
$ErrorActionPreference = 'Stop'
$VpkVersion = '1.2.161'   # Velopack packaging tool; keep it equal to the Velopack package in PuppyMacro.csproj
# ssign (https://github.com/Le-Syl21/ssign) signs with the Certum SimplySign cloud certificate.
# Pinned: it receives the signing secrets, so a new version is reviewed first, then its zip's SHA-256 goes here.
$SsignVersion = '0.1.7'
$SsignSha256 = '4EE5389A74DDBEB67E18FE8C8B963734CA13DE7B24BEFFBE430C3F427ECC5E40'
$Publisher = 'Giuseppe Vincenzi'   # name in the code signing certificate
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$vpk = Join-Path $root '.tools/vpk.exe'

$version = ([xml](Get-Content PuppyMacro/PuppyMacro.csproj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in PuppyMacro/PuppyMacro.csproj." }
Write-Host "Building the distribution of PuppyMacro $version"

$sign = [bool]($env:CERTUM_EMAIL -and ($env:CERTUM_OTP -or $env:CERTUM_TOKEN))
if ($RequireSigning -and -not $sign) {
    throw "Signing is required: set CERTUM_EMAIL and CERTUM_OTP (or CERTUM_TOKEN)."
}

function Invoke-Checked {
    & $args[0] $args[1..($args.Count - 1)]
    if ($LASTEXITCODE -ne 0) { throw "Failed: $($args -join ' ')" }
}

# Every exe and dll of the package, and Setup, must carry a valid signature; ours (signer $Publisher)
# also a timestamp, so they stay valid after the certificate expires. ssign does not check the
# timestamp it receives: Windows does it here.
function Assert-Signed {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $full = Get-ChildItem dist -Filter "PuppyMacro-$version-full.nupkg" | Select-Object -First 1
    if (-not $full) { throw "No full package for $version in dist/." }
    $unpacked = Join-Path ([IO.Path]::GetTempPath()) "puppymacro-signcheck-$PID"
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    [IO.Compression.ZipFile]::ExtractToDirectory($full.FullName, $unpacked)
    try {
        $files = @(Get-ChildItem $unpacked -Recurse -Include *.exe, *.dll) + @(Get-Item dist/PuppyMacro-win-Setup.exe)
        $bad = @()
        foreach ($file in $files) {
            $signature = Get-AuthenticodeSignature $file.FullName
            $ours = [bool]($signature.SignerCertificate -and
                $signature.SignerCertificate.Subject -match [regex]::Escape($Publisher))
            if ($signature.Status -ne 'Valid') { $bad += "$($file.Name): $($signature.Status)" }
            elseif ($ours -and -not $signature.TimeStamperCertificate) { $bad += "$($file.Name): no timestamp" }
            elseif ($file.Name -in 'PuppyMacro.exe', 'PuppyMacro-win-Setup.exe' -and -not $ours) {
                $bad += "$($file.Name): not signed by $Publisher"
            }
        }
        if ($bad) { throw "Signature check failed:`n  $($bad -join "`n  ")" }
        Write-Host "Signatures checked: $($files.Count) files"
    } finally {
        Remove-Item $unpacked -Recurse -Force
    }
}

Invoke-Checked dotnet publish PuppyMacro/PuppyMacro.csproj -c Release -p:PublishProfile=Folder
# vpk in .tools/ (a tool manifest is refused when the repository is on a network path, e.g. WSL).
$hasVpk = (Test-Path $vpk) -and ((dotnet tool list --tool-path .tools) -match "^vpk\s+$([regex]::Escape($VpkVersion))\s")
if (-not $hasVpk) {
    if (Test-Path .tools) { Remove-Item .tools -Recurse -Force }
    Invoke-Checked dotnet tool install vpk --version $VpkVersion --tool-path .tools
}

if ($sign) {
    $ssignDir = Join-Path $root ".tools/ssign-$SsignVersion"
    if (-not (Test-Path (Join-Path $ssignDir 'ssign.exe'))) {
        $zip = Join-Path ([IO.Path]::GetTempPath()) "ssign-$SsignVersion-$PID.zip"
        Invoke-WebRequest -UseBasicParsing -OutFile $zip `
            "https://github.com/Le-Syl21/ssign/releases/download/v$SsignVersion/ssign-windows-x86_64.zip"
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($hash -ne $SsignSha256) {
            Remove-Item $zip
            throw "The ssign $SsignVersion download has SHA-256 $hash, expected $SsignSha256."
        }
        Expand-Archive $zip $ssignDir -Force
        Remove-Item $zip
    }
    # On the PATH, so the sign template needs no quoted path (Windows PowerShell passes quotes badly).
    $env:PATH = "$ssignDir;$env:PATH"
}

if (Test-Path dist) { Remove-Item dist -Recurse -Force }
if ($PreviousReleaseRepo) {
    Invoke-Checked $vpk download github --repoUrl $PreviousReleaseRepo --token $Token --outputDir dist
}

$pack = @(
    'pack',
    '--packId', 'PuppyMacro',
    '--packVersion', $version,
    '--packTitle', 'PuppyMacro',
    '--packAuthors', $Publisher,
    '--packDir', 'publish',
    '--mainExe', 'PuppyMacro.exe',
    '--icon', 'PuppyMacro/Assets/PuppyMacro.ico',
    '--framework', 'net10.0-x64-desktop,webview2',   # WebView2: the macro editor's Code view
    '--noPortable',   # a portable copy would not update itself
    '--outputDir', 'dist'
)
if ($ReleaseNotes) { $pack += @('--releaseNotes', $ReleaseNotes) }
if ($sign) {
    # Velopack skips the files already signed and trusted (Microsoft's) and calls ssign for the
    # rest, up to 20 files per call and never in parallel: the first call logs in, the next ones
    # reuse its session (parallel logins with the same TOTP code are refused).
    $pack += @(
        '--signTemplate', 'ssign --name PuppyMacro --url https://giuvincenzi.github.io/puppymacro/ {{file...}}',
        '--signParallel', '20'
    )
}
try {
    Invoke-Checked $vpk @pack
} finally {
    # ssign keeps its login (it can sign for about 30 minutes) in ssign\session.json, in the first of
    # XDG_RUNTIME_DIR, HOME\.cache and TEMP that is set: never leave it behind.
    $cache = if ($env:HOME) { Join-Path $env:HOME '.cache' }
    foreach ($folder in $env:XDG_RUNTIME_DIR, $cache, $env:TEMP) {
        if ($folder) { Remove-Item (Join-Path $folder 'ssign') -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($sign) {
    Assert-Signed
} else {
    Write-Warning 'Not signed: CERTUM_EMAIL and CERTUM_OTP (or CERTUM_TOKEN) are not set.'
}
Write-Host "Done: $(Join-Path $root 'dist/PuppyMacro-win-Setup.exe')"
