<#
.SYNOPSIS
    Builds the plugin, packages it, and updates manifest.json for a self hosted repository.

.DESCRIPTION
    Jellyfin installs a plugin by downloading the zip named in sourceUrl, verifying its MD5
    against the checksum field, and extracting it into <plugins>/<name>_<version>. The zip must
    therefore contain TVHeadEnd.dll at its root, and the checksum has to match the exact file
    that ends up being served — which is why packaging and manifest generation belong in one
    step rather than two.

.PARAMETER Version
    Four part version, e.g. 14.0.0.0. Must match the version in Directory.Build.props.

.PARAMETER Changelog
    Text shown in the Jellyfin plugin catalogue for this version.

.EXAMPLE
    .\tools\New-PluginRelease.ps1 -Version 14.0.0.0 -Changelog "Fixes recording deletion"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Changelog = '',

    # Where the release asset will be reachable. The default matches GitHub's layout for a
    # release tagged with the version.
    [string]$DownloadUrlTemplate = 'https://github.com/marcomoerz/jellyfin-plugin-tvheadend/releases/download/v{0}/tvheadend_{0}.zip'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $repoRoot 'dist'
$stageDir = Join-Path $distDir 'stage'
$zipPath = Join-Path $distDir "tvheadend_$Version.zip"
$manifestPath = Join-Path $repoRoot 'manifest.json'

# --- build -------------------------------------------------------------------------------
Write-Host "Building $Version..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repoRoot 'TVHeadEnd\TVHeadEnd.csproj') --configuration Release --output $stageDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- package -----------------------------------------------------------------------------
# Only the plugin assembly. Jellyfin loads every DLL in the plugin folder, so shipping the
# Jellyfin assemblies we compiled against would shadow the server's own.
$dll = Join-Path $stageDir 'TVHeadEnd.dll'
if (-not (Test-Path $dll)) { throw "TVHeadEnd.dll not found in $stageDir" }

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $dll -DestinationPath $zipPath -CompressionLevel Optimal

$checksum = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
Write-Host "Packaged $zipPath (md5 $checksum)" -ForegroundColor Cyan

# --- manifest ----------------------------------------------------------------------------
# targetAbi is read from build.yaml so the two cannot drift apart.
$buildYaml = Get-Content (Join-Path $repoRoot 'build.yaml') -Raw
$targetAbi = [regex]::Match($buildYaml, 'targetAbi:\s*"?([\d.]+)"?').Groups[1].Value
$guid = [regex]::Match($buildYaml, 'guid:\s*"?([0-9a-fA-F-]+)"?').Groups[1].Value
if (-not $targetAbi) { throw 'could not read targetAbi from build.yaml' }

$newVersion = [ordered]@{
    version    = $Version
    changelog  = $Changelog
    targetAbi  = $targetAbi
    sourceUrl  = ($DownloadUrlTemplate -f $Version)
    checksum   = $checksum
    timestamp  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $package = $manifest[0]
    # Newest first, and never two entries for the same version.
    $kept = @($package.versions | Where-Object { $_.version -ne $Version })
    $package.versions = @($newVersion) + $kept
} else {
    $manifest = @(
        [ordered]@{
            guid        = $guid
            name        = 'TVHeadend'
            description = 'Provides live TV using TVHeadend as the source.'
            overview    = 'Manage TVHeadend from Jellyfin'
            owner       = 'marcomoerz'
            category    = 'LiveTV'
            imageUrl    = 'https://repo.jellyfin.org/releases/plugin/images/jellyfin-plugin-tvheadend.png'
            versions    = @($newVersion)
        }
    )
}

# Jellyfin deserialises the manifest as PackageInfo[], so the top level has to stay an array.
# ConvertTo-Json unwraps a single element array, hence the manual brackets. And no BOM: it is
# written by Set-Content -Encoding utf8 on Windows PowerShell and trips up strict JSON readers.
$json = '[' + ($manifest[0] | ConvertTo-Json -Depth 6) + ']'
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Updated $manifestPath" -ForegroundColor Green

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Yellow
Write-Host "  1. gh release create v$Version `"$zipPath`" --title v$Version --notes `"$Changelog`""
Write-Host '  2. commit and push manifest.json'
