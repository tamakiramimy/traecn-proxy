#requires -Version 5.1
<#
.SYNOPSIS
    下载指定 GitHub Release 的 linux 自包含产物，校验并解压为 Docker 构建上下文。
#>
[CmdletBinding()]
param(
    [string]$Version = 'v0.3.1',
    [string]$Repo = 'tamakiramimy/traecn-proxy'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root '.artifacts'
$publish = Join-Path $artifacts 'publish'

$rids = @{ 'amd64' = 'linux-x64'; 'arm64' = 'linux-arm64' }

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish | Out-Null

foreach ($arch in $rids.Keys) {
    $rid = $rids[$arch]
    $zip = Join-Path $artifacts "trancn-proxy-$Version-$rid.zip"
    if (-not (Test-Path $zip)) {
        $url = "https://github.com/$Repo/releases/download/$Version/trancn-proxy-$Version-$rid.zip"
        Write-Host "下载 $url"
        Invoke-WebRequest -Uri $url -OutFile $zip
    }
    Write-Host "$rid sha256: $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())"
    Expand-Archive $zip -DestinationPath (Join-Path $publish $arch) -Force
}

# v0.3.1 及更早的 Release 包不含 wwwroot，需要从同名标签补齐管理端静态资源
$missing = $rids.Keys | Where-Object { -not (Test-Path (Join-Path $publish "$_\wwwroot")) }
if ($missing) {
    $wwwrootZip = Join-Path $artifacts "wwwroot-$Version.zip"
    git -C $root archive --format=zip -o $wwwrootZip $Version wwwroot
    foreach ($arch in $missing) {
        Expand-Archive $wwwrootZip -DestinationPath (Join-Path $publish $arch) -Force
    }
}

Write-Host "构建上下文已就绪: $publish"
