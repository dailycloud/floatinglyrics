# Builds the release package that the widget's updater installs:
#   artifacts\FloatingLyrics-win-x64.zip
# Usage:  .\build-release.ps1 -Version 1.1.0
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'FloatingLyrics.csproj'
$out = Join-Path $PSScriptRoot 'artifacts'
$publish = Join-Path $out 'publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $project -c Release -p:Platform=x64 -p:PublishProfile=win-x64.pubxml `
    -p:PublishTrimmed=false -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Never ship local secrets or logs.
Get-ChildItem $publish -Recurse -Include 'settings.json', '*.log' | Remove-Item -Force

$zip = Join-Path $out 'FloatingLyrics-win-x64.zip'
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
