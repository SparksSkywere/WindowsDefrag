# Builds Windows Installer packages for Windows Defrag:
#   - WindowsDefrag.msi
#   - WindowsDefrag-Setup.exe  (WiX Burn bootstrapper)
#
# Requires: .NET SDK 8+ (WiX Toolset is pulled via NuGet as WixToolset.Sdk)

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppProj = Join-Path $Root 'src\WindowsDefrag\WindowsDefrag.csproj'
$MsiProj = Join-Path $Root 'installer\wix\WindowsDefrag.Installer\WindowsDefrag.Installer.wixproj'
$BundleProj = Join-Path $Root 'installer\wix\WindowsDefrag.Bundle\WindowsDefrag.Bundle.wixproj'
$PayloadDir = Join-Path $Root 'artifacts\payload'
$DistDir = Join-Path $Root 'dist'
$IconSrc = Join-Path $Root 'assets\installer.ico'
$AppIcon = Join-Path $Root 'src\WindowsDefrag\Assets\app.ico'

Write-Host '=== Windows Defrag — Windows Installer (WiX / MSI) ===' -ForegroundColor Cyan
Write-Host "Root: $Root"
Write-Host 'Publisher: Skywere Industries'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK (dotnet) is required to build the WiX installer.'
}

if (-not (Test-Path $IconSrc)) {
    throw "Production icon missing: $IconSrc"
}

# Keep MSI / Bundle version in lockstep with the app Version (MajorUpgrade depends on it).
$csprojText = Get-Content $AppProj -Raw
if ($csprojText -match '<Version>([^<]+)</Version>') {
    $AppVersion = $Matches[1].Trim()
    Write-Host "App version: $AppVersion" -ForegroundColor Green
    foreach ($wxs in @(
        (Join-Path $Root 'installer\wix\WindowsDefrag.Installer\Package.wxs'),
        (Join-Path $Root 'installer\wix\WindowsDefrag.Bundle\Bundle.wxs')
    )) {
        if (-not (Test-Path $wxs)) { continue }
        $text = Get-Content $wxs -Raw
        $updated = [regex]::Replace($text, 'Version="\d+\.\d+\.\d+"', "Version=`"$AppVersion`"", 1)
        if ($updated -ne $text) {
            Set-Content -Path $wxs -Value $updated -Encoding UTF8 -NoNewline
            Write-Host "  Synced version in $(Split-Path $wxs -Leaf)" -ForegroundColor Green
        }
    }
} else {
    Write-Host 'WARNING: Could not read <Version> from app csproj; WiX versions left as-is.' -ForegroundColor Yellow
}

# Sync icon BEFORE publish so ApplicationIcon is baked into the EXE
New-Item -ItemType Directory -Path (Split-Path $AppIcon) -Force | Out-Null
Copy-Item $IconSrc $AppIcon -Force
Write-Host "Icon synced: assets\installer.ico -> src\...\Assets\app.ico ($((Get-Item $AppIcon).Length) bytes)" -ForegroundColor Green

if (Test-Path $PayloadDir) { Remove-Item $PayloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $PayloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

# Clean obj icons / force rebuild of app host with new icon
$objDir = Join-Path $Root 'src\WindowsDefrag\obj'
if (Test-Path $objDir) {
    Get-ChildItem $objDir -Recurse -Filter 'apphost.exe' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

Write-Host "`n[1/3] Publishing Windows Defrag ($Runtime)..." -ForegroundColor Yellow
dotnet publish $AppProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $PayloadDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

$exe = Join-Path $PayloadDir 'WindowsDefrag.exe'
if (-not (Test-Path $exe)) { throw "Missing $exe after publish." }

Get-ChildItem $PayloadDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Ship loose icon next to EXE (shortcuts / About fallback)
$iconDstDir = Join-Path $PayloadDir 'Assets'
New-Item -ItemType Directory -Path $iconDstDir -Force | Out-Null
Copy-Item $IconSrc (Join-Path $iconDstDir 'app.ico') -Force
Copy-Item $IconSrc (Join-Path $PayloadDir 'app.ico') -Force
Write-Host "  Icon: $IconSrc"

Write-Host ("  Payload: {0:N1} MB" -f ((Get-ChildItem $PayloadDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

Write-Host "`n[2/3] Building MSI (Windows Installer)..." -ForegroundColor Yellow
Get-ChildItem (Join-Path $Root 'installer\wix\WindowsDefrag.Installer\bin') -Recurse -Filter '*.msi' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

dotnet build $MsiProj -c $Configuration -p:PayloadDir="$PayloadDir" --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

$msi = Get-ChildItem (Join-Path $Root 'installer\wix\WindowsDefrag.Installer') -Recurse -Filter 'WindowsDefrag.msi' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msi) { throw 'WindowsDefrag.msi was not produced.' }
Copy-Item $msi.FullName -Destination (Join-Path $DistDir 'WindowsDefrag.msi') -Force
Write-Host ("  MSI: {0:N1} MB -> dist\WindowsDefrag.msi" -f ($msi.Length / 1MB))

Write-Host "`n[3/3] Building Setup.exe (WiX Burn bootstrapper)..." -ForegroundColor Yellow
Get-ChildItem (Join-Path $Root 'installer\wix\WindowsDefrag.Bundle\bin') -Recurse -Filter '*.exe' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

dotnet build $BundleProj -c $Configuration -p:PayloadDir="$PayloadDir" --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'Bundle build failed.' }

$setup = Get-ChildItem (Join-Path $Root 'installer\wix\WindowsDefrag.Bundle') -Recurse -Filter 'WindowsDefrag-Setup.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $setup) { throw 'WindowsDefrag-Setup.exe was not produced.' }
Copy-Item $setup.FullName -Destination (Join-Path $DistDir 'WindowsDefrag-Setup.exe') -Force
Copy-Item $setup.FullName -Destination (Join-Path $Root 'installer\WindowsDefrag-Setup.exe') -Force
Copy-Item $msi.FullName -Destination (Join-Path $Root 'installer\WindowsDefrag.msi') -Force

Write-Host ''
Write-Host '=== Build succeeded ===' -ForegroundColor Green
Write-Host "  dist\WindowsDefrag.msi"
Write-Host "  dist\WindowsDefrag-Setup.exe"
Write-Host "  Icon source: assets\installer.ico"
Write-Host ''
Write-Host 'Install (full wizard with folder Browse):'
Write-Host '  msiexec /i .\dist\WindowsDefrag.msi'
Write-Host ''
Write-Host 'Install (Setup.exe; Options = install folder):'
Write-Host '  .\dist\WindowsDefrag-Setup.exe'
Write-Host ''
