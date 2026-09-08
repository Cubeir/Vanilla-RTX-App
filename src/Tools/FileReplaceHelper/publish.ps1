# Republishes FileReplaceHelper for all three architectures and copies the results into
# src\Assets\Tools\, where the main app picks them up as ordinary per-platform Content
# assets (see the Assets\Tools ItemGroup in "Vanilla RTX App.csproj").
#
# Run this after any change to Program.cs, app.manifest, or the csproj itself - the checked-in
# exes are NOT rebuilt automatically by building the main app. Then rebuild/republish the main
# app as normal.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root "FileReplaceHelper.csproj"
$assetsDir = Join-Path $root "..\..\Assets\Tools" | Resolve-Path

$targets = @(
    @{ Rid = "win-x64";   Platform = "x64";   AssetName = "FileReplaceHelper.x64.exe" },
    @{ Rid = "win-x86";   Platform = "x86";   AssetName = "FileReplaceHelper.x86.exe" },
    @{ Rid = "win-arm64"; Platform = "ARM64"; AssetName = "FileReplaceHelper.arm64.exe" }
)

foreach ($target in $targets) {
    Write-Host "Publishing $($target.Rid)..." -ForegroundColor Cyan

    dotnet publish $csproj -c Release -r $target.Rid --self-contained `
        -p:Platform=$($target.Platform) --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($target.Rid)" }

    $publishDir = Join-Path $root "bin\$($target.Platform)\Release\net10.0-windows\$($target.Rid)\publish"
    $sourceExe = Join-Path $publishDir "FileReplaceHelper.exe"
    $destExe = Join-Path $assetsDir $target.AssetName

    Copy-Item $sourceExe $destExe -Force
    Write-Host "  -> $destExe" -ForegroundColor Green
}

Write-Host "`nDone. Review the diff under src\Assets\Tools\ and commit if it looks right." -ForegroundColor Yellow
