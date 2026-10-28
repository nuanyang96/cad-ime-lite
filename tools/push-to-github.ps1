# push-to-github.ps1 — 用 GitHub API 推送当前仓库（本机 git 协议连 github.com 会超时）
# 用法：$env:DSH_SECRET_GITHUB_TOKEN_PAT 已存在时，直接运行本脚本
# 说明：内容取自本地 git 对象（git cat-file），所以远端与本地仓库逐字节一致

param(
  [string]$Owner = 'nuanyang96',
  [string]$Repo  = 'cad-ime-lite',
  [string]$RepoDir = (Split-Path -Parent $PSScriptRoot),
  [string]$Message = 'update'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$tok = $env:DSH_SECRET_GITHUB_TOKEN_PAT
if (-not $tok) { throw '缺少环境变量 DSH_SECRET_GITHUB_TOKEN_PAT' }
$h = @{ Authorization = "token $tok"; 'User-Agent' = 'push-helper'; Accept = 'application/vnd.github+json' }
$utf8 = [Text.Encoding]::UTF8

function PostJson($uri, $obj, $method = 'Post') {
  $bytes = $utf8.GetBytes(($obj | ConvertTo-Json -Depth 8 -Compress))
  Invoke-RestMethod -Uri $uri -Method $method -Headers $h -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}

$rows = @((& git -C $RepoDir -c core.quotepath=false ls-tree -r HEAD 2>&1) | Where-Object { $_ -ne '' })
$entries = New-Object System.Collections.ArrayList
foreach ($line in $rows) {
  if ($line -notmatch '^(\d+) blob ([0-9a-f]+)\t(.+)$') { continue }
  $sha = $matches[2]; $path = $matches[3]
  $tmp = Join-Path $env:TEMP ('gobj-' + $sha)
  & cmd /c "git -C `"$RepoDir`" cat-file blob $sha > `"$tmp`"" 2>&1 | Out-Null
  $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($tmp))
  Remove-Item $tmp -Force -ErrorAction SilentlyContinue
  $blob = PostJson "https://api.github.com/repos/$Owner/$Repo/git/blobs" @{ content = $b64; encoding = 'base64' }
  [void]$entries.Add(@{ path = $path; mode = '100644'; type = 'blob'; sha = $blob.sha })
}

$head = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/git/refs/heads/main" -Headers $h -TimeoutSec 30
$tree = PostJson "https://api.github.com/repos/$Owner/$Repo/git/trees" @{ tree = $entries }
$commit = PostJson "https://api.github.com/repos/$Owner/$Repo/git/commits" @{ message = $Message; tree = $tree.sha; parents = @($head.object.sha) }
$ref = PostJson "https://api.github.com/repos/$Owner/$Repo/git/refs/heads/main" @{ sha = $commit.sha; force = $true } 'Patch'
Write-Host ('pushed ' + $entries.Count + ' files -> ' + $ref.object.sha)