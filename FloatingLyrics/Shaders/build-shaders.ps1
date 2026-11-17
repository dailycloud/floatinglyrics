# Compiles the Direct2D pixel shaders used by the liquid glass (requires the Windows SDK).
# The compiled .bin files are committed next to the sources and embedded into the app, so this
# only needs to run after editing a .hlsl file.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$kits = 'C:\Program Files (x86)\Windows Kits\10'
$fxc = Get-ChildItem "$kits\bin\*\x64\fxc.exe" | Sort-Object FullName -Descending | Select-Object -First 1
$include = Get-ChildItem "$kits\Include\*\um\d2d1effecthelpers.hlsli" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $fxc -or -not $include) { throw 'fxc.exe or d2d1effecthelpers.hlsli not found (install the Windows SDK).' }

Get-ChildItem "$here\*.hlsl" | ForEach-Object {
    $out = [IO.Path]::ChangeExtension($_.FullName, '.bin')
    & $fxc.FullName /nologo /O3 /T ps_4_0 /D D2D_FULL_SHADER /D D2D_ENTRY=main /E main `
        /I $include.DirectoryName /Fo $out $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "fxc failed for $($_.Name)" }
    Write-Host "compiled $($_.Name) -> $([IO.Path]::GetFileName($out))"
}
