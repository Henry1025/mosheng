param(
  [string]$Version = "v0.1.0-alpha.1"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$appRoot = Join-Path $repo "MoshengNativeApp"
$exe = Join-Path $appRoot "dist\Mosheng.exe"
$distResources = Join-Path $appRoot "dist\resources"
$build = Join-Path $appRoot "build.ps1"
$releaseRoot = Join-Path $repo "releases"
$packageName = "Mosheng-$Version-windows-x64"
$packageDir = Join-Path $releaseRoot $packageName
$zipPath = Join-Path $releaseRoot "$packageName.zip"
$releaseDocs = Join-Path $repo "docs\release"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build

if (Test-Path -LiteralPath $packageDir) {
  Remove-Item -LiteralPath $packageDir -Recurse -Force
}

New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Copy-Item -LiteralPath $exe -Destination (Join-Path $packageDir "Mosheng.exe") -Force
if (Test-Path -LiteralPath $distResources) {
  Copy-Item -LiteralPath $distResources -Destination (Join-Path $packageDir "resources") -Recurse -Force
}
Get-ChildItem -LiteralPath $releaseDocs -Filter "*.md" | ForEach-Object {
  Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $packageDir $_.Name) -Force
}

Set-Content -LiteralPath (Join-Path $packageDir "VERSION.txt") -Value @(
  "Mosheng $Version"
  "Windows x64 alpha build"
  "Built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
) -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
  Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force

Write-Host "Package directory: $packageDir"
Write-Host "Zip package: $zipPath"
