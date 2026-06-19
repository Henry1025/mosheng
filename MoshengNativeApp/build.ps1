param(
  [switch]$Run
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\Mosheng.cs"
$dist = Join-Path $root "dist"
$out = Join-Path $dist "Mosheng.exe"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $csc)) {
  throw "Cannot find .NET Framework compiler: $csc"
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /utf8output `
  /out:$out `
  /reference:System.dll `
  /reference:System.Core.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  /reference:System.Net.Http.dll `
  /reference:System.Security.dll `
  /reference:System.Web.Extensions.dll `
  $src

if ($LASTEXITCODE -ne 0) {
  throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Built $out"

if ($Run) {
  Start-Process -FilePath $out
}
