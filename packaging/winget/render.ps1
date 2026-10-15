<#
.SYNOPSIS
  Fills the winget manifest templates for one release, in the folder layout winget-pkgs expects.

.EXAMPLE
  ./packaging/winget/render.ps1 -Version 1.0.0 -InstallerPath out/LayoutLock.exe -OutputRoot dist
  # -> dist/manifests/a/AlekseyBaryshnikov/LayoutLock/1.0.0/*.yaml
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $InstallerPath,
    [string] $OutputRoot = "dist",
    [string] $ReleaseDate = (Get-Date -Format "yyyy-MM-dd")
)

$ErrorActionPreference = "Stop"

$sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash
$target = Join-Path $OutputRoot "manifests/a/AlekseyBaryshnikov/LayoutLock/$Version"
New-Item -ItemType Directory -Force -Path $target | Out-Null

Get-ChildItem -Path $PSScriptRoot -Filter "*.yaml" | ForEach-Object {
    $content = (Get-Content -Raw -LiteralPath $_.FullName) `
        -replace '\{\{VERSION\}\}', $Version `
        -replace '\{\{SHA256\}\}', $sha256 `
        -replace '\{\{RELEASE_DATE\}\}', $ReleaseDate
    # Template marker comments are for this repo only, not for winget-pkgs.
    $content = ($content -split "`n" | Where-Object { $_ -notmatch '^# Template:' }) -join "`n"
    Set-Content -LiteralPath (Join-Path $target $_.Name) -Value $content -NoNewline -Encoding utf8NoBOM
}

Write-Host "winget manifests for $Version (SHA256 $sha256) written to $target"
$target
