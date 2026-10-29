# Packs a self-contained win-x64 publish folder into an unsigned .msix for Microsoft Store upload.
#
#   pwsh win/msix/build-msix.ps1 -PublishDir publish/win-x64 -Version 1.2.3 -Output KawaPaint.msix
#
# The identity parameters must match Partner Center (Product management > Product identity).
# Without them the placeholders below still produce a valid package, handy for checking the
# manifest locally, but the Store will refuse it.
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Output,
    [string] $IdentityName = 'KawaPaint.Dev',
    [string] $Publisher = 'CN=KawaPaint-Dev',
    [string] $PublisherDisplayName = 'KawaPaint'
)

$ErrorActionPreference = 'Stop'

# MSIX wants four numeric parts and the Store wants the last one to be 0: "v1.2" -> "1.2.0.0".
$parts = @(($Version -replace '^[vV]', '' -replace '[-+].*$', '').Split('.') | Where-Object { $_ -ne '' })
if ($parts.Count -gt 3 -or ($parts | Where-Object { $_ -notmatch '^\d+$' })) {
    throw "Version '$Version' must look like 1, 1.2 or 1.2.3 (the Store reserves the fourth part)."
}
while ($parts.Count -lt 3) { $parts += '0' }
$msixVersion = ($parts + '0') -join '.'

$staging = Join-Path ([IO.Path]::GetTempPath()) "kawapaint-msix-$([guid]::NewGuid())"
Copy-Item -Recurse $PublishDir $staging
Copy-Item -Recurse (Join-Path $PSScriptRoot 'Assets') (Join-Path $staging 'Assets')

# SecurityElement.Escape keeps an "&" or "<" in a publisher name from breaking the XML.
$esc = { param($s) [Security.SecurityElement]::Escape($s) }
$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppxManifest.xml'))
$manifest = $manifest.Replace('$IdentityName$', (& $esc $IdentityName))
$manifest = $manifest.Replace('$Publisher$', (& $esc $Publisher))
$manifest = $manifest.Replace('$PublisherDisplayName$', (& $esc $PublisherDisplayName))
$manifest = $manifest.Replace('$Version$', $msixVersion)
[IO.File]::WriteAllText((Join-Path $staging 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

# The scale-qualified logo names need a resources.pri so the manifest's unqualified names resolve.
$sdkBin = Get-Item "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64" |
    Where-Object { Test-Path (Join-Path $_.FullName 'makeappx.exe') } |
    Sort-Object { [version]$_.Parent.Name } -Descending | Select-Object -First 1
if (-not $sdkBin) { throw 'Windows 10/11 SDK not found (makeappx.exe / makepri.exe).' }
$makepri = Join-Path $sdkBin.FullName 'makepri.exe'
$makeappx = Join-Path $sdkBin.FullName 'makeappx.exe'

# makepri wants absolute paths; the config lives outside the staging folder so it is not packed.
$priConfig = "$staging.priconfig.xml"
& $makepri createconfig /cf $priConfig /dq fr-FR_en-US /pv 10.0.0 /o
if ($LASTEXITCODE) { throw "makepri createconfig failed ($LASTEXITCODE)" }
& $makepri new /pr $staging /cf $priConfig /mn (Join-Path $staging 'AppxManifest.xml') /of (Join-Path $staging 'resources.pri') /o
if ($LASTEXITCODE) { throw "makepri new failed ($LASTEXITCODE)" }
Remove-Item $priConfig

& $makeappx pack /d $staging /p $Output /o
if ($LASTEXITCODE) { throw "makeappx pack failed ($LASTEXITCODE)" }
Remove-Item -Recurse -Force $staging
Write-Host "Packed $Output (version $msixVersion)"
