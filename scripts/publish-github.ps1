param(
  [string]$Owner = "Henry1025",
  [string]$Repo = "mosheng",
  [ValidateSet("public", "private")]
  [string]$Visibility = "public"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  throw "GitHub CLI is not installed."
}

gh auth status | Out-Host
if ($LASTEXITCODE -ne 0) {
  throw "GitHub CLI is not authenticated. Run: gh auth login"
}

$fullName = "$Owner/$Repo"
$remoteUrl = "https://github.com/$fullName.git"

$remotes = @(git remote)
$hasOrigin = $remotes -contains "origin"

if (-not $hasOrigin) {
  $exists = $false
  gh repo view $fullName *> $null
  if ($LASTEXITCODE -eq 0) {
    $exists = $true
  }

  if (-not $exists) {
    gh repo create $fullName --source . --remote origin "--$Visibility" --description "Windows-first local AI voice input."
  } else {
    git remote add origin $remoteUrl
  }
}

git push -u origin main

Write-Host "Published https://github.com/$fullName"
