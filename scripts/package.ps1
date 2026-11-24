# Copyright (C) 2026 luzi82
# SPDX-License-Identifier: AGPL-3.0-or-later
[CmdletBinding()]
param(
    [string]$RNSReloadedInterfacesDll,
    [string]$RNSReloadedRepo
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$config = Get-Content -LiteralPath (Join-Path $projectRoot 'ModConfig.json') -Raw | ConvertFrom-Json
$version = $config.ModVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'ModVersion must be a three-part version.' }
$buildArgs = @('build', (Join-Path $projectRoot 'RNSClipboardProbe.csproj'), '-c', 'Release', '--disable-build-servers', '-p:EnableDiagnostics=false')
if ($RNSReloadedInterfacesDll) { $buildArgs += '-p:RNSReloadedInterfacesDll=' + [IO.Path]::GetFullPath($RNSReloadedInterfacesDll) }
if ($RNSReloadedRepo) { $buildArgs += '-p:RNSReloadedRepo=' + [IO.Path]::GetFullPath($RNSReloadedRepo) }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
$buildRoot = Join-Path $projectRoot 'bin/Release/net9.0-windows'
$dll = Join-Path $buildRoot 'RNSClipboardProbe.dll'
& dotnet run --project (Join-Path $projectRoot 'tests/ReleaseChecks') -c Release --disable-build-servers -- $dll
if ($LASTEXITCODE -ne 0) { throw 'Release checks failed.' }

# A new staging directory avoids mixing files left over from previous builds.
$stage = Join-Path $projectRoot ('.packaging/' + $version + '-' + [Guid]::NewGuid().ToString('N'))
$installStage = Join-Path $stage 'install/RNSClipboardProbe'
$sourceStage = Join-Path $stage 'source'
$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $installStage, $sourceStage, $dist -Force | Out-Null
foreach ($name in @('RNSClipboardProbe.dll', 'ModConfig.json', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $name) -Destination (Join-Path $installStage $name)
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $installStage 'README.md')

# Explicit source allowlist: no game data, binaries, backups or local captures.
$sourceFiles = @(Get-ChildItem -LiteralPath $projectRoot -File | Where-Object {
    $_.Extension -in @('.cs', '.csproj', '.py') -or $_.Name -in @('README.md', 'LICENSE', 'ModConfig.json', '.gitignore', '.gitattributes')
})
foreach ($folder in @('docs', 'scripts', 'tests', '.github')) {
    $folderPath = Join-Path $projectRoot $folder
    if (Test-Path -LiteralPath $folderPath) {
        $sourceFiles += Get-ChildItem -LiteralPath $folderPath -File -Recurse -Force | Where-Object {
            $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
            $relative -notmatch '(^|[\\/])(bin|obj|__pycache__)([\\/]|$)' -and $_.Extension -in @('.cs', '.csproj', '.ps1', '.py', '.md', '.yml', '.yaml')
        }
    }
}
foreach ($file in $sourceFiles) {
    $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName)
    $destination = Join-Path $sourceStage $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$installZip = Join-Path $dist "rns-clipboard-probe-$version.zip"
$sourceZip = Join-Path $dist "rns-clipboard-probe-$version-source.zip"
Compress-Archive -LiteralPath $installStage -DestinationPath $installZip -Force
Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath $sourceZip -Force
$hashes = foreach ($zip in @($installZip, $sourceZip)) {
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip)
}
Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Value $hashes -Encoding ascii
Get-Item -LiteralPath $installZip, $sourceZip, (Join-Path $dist 'SHA256SUMS.txt') | Select-Object Name, Length, FullName
