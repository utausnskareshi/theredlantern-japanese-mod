<#
.SYNOPSIS
  配布用ZIPを作る。ビルド → dist\TheRedLanternJP-v<バージョン>.zip を生成する。

.EXAMPLE
  pwsh tools\build_release.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TheRedLanternJP\TheRedLanternJP.csproj'

[xml]$csproj = Get-Content $project -Encoding UTF8
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'csproj から Version を読み取れませんでした。' }

dotnet build $project -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }

$name = "TheRedLanternJP-v$version"
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage 'translations') -Force | Out-Null

$bin = Join-Path $root 'src\TheRedLanternJP\bin\Release'
Copy-Item (Join-Path $bin 'TheRedLanternJP.exe') $stage
Copy-Item (Join-Path $bin 'TheRedLanternJP.exe.config') $stage
Copy-Item (Join-Path $root 'translations\ja_supplement.tsv') (Join-Path $stage 'translations')
Copy-Item (Join-Path $root 'packaging\README.txt') $stage
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii

Write-Host ''
Write-Host "作成しました: $zip"
Write-Host ("サイズ: {0:N0} バイト" -f (Get-Item $zip).Length)
Write-Host "SHA-256: $hash"
