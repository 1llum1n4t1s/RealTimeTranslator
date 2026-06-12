# release-local.ps1 — ローカル署名付き Velopack リリース
#
# SimplySign (Certum クラウド署名) は Desktop 接続 + スマホトークンが必要で
# GitHub Actions からは署名できないため、リリースは本スクリプトでローカル実行する。
# 旧 CI リリース (.github/workflows/release.yml + velopack.yml) はこのスクリプトに置換済み。
#
# 前提:
#   - SimplySign Desktop が接続済み (証明書が CurrentUser\My に見えていること)
#   - Directory.Build.props の <Version> がリリースしたいバージョンになっていること (/vava 済み)
#   - C:\Users\IMT\dev\Secret\secrets.json に cloudflare.api_token があること
#
# 使い方:
#   pwsh scripts/release-local.ps1                # フルリリース (build + sign + upload + cleanup)
#   pwsh scripts/release-local.ps1 -SkipUpload    # ビルド + 署名のみ (アップロードしない動作確認用)
#   pwsh scripts/release-local.ps1 -Runtimes win-x64   # 対象 RID を絞る (テスト用)

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [string[]]$Runtimes = @('win-x64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---- 定数 (旧 CI 版 release.yml / velopack.yml と揃える) ----
# Velopack (vpk) は常に最新安定版を使う (ゆろ君ルール): NuGet から実行時に最新を解決して pin する
$VpkVersion = (Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/vpk/index.json' -TimeoutSec 30).versions |
    Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
if (-not $VpkVersion) { throw 'vpk の最新安定版バージョンの取得に失敗しました (NuGet API)' }
Write-Host "vpk 最新安定版: $VpkVersion"
$WranglerVersion = '4.92.0'         # サプライチェーン対策でバージョン固定
$Bucket = 'realtimetranslator-updates'
$BaseUrl = 'https://rtt.nephilim.jp'
$AccountId = '10901bfadbf1005164774a7350082985'
$SecretsPath = 'C:\Users\IMT\dev\Secret\secrets.json'
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'
# /n (Subject 名) で選択: 証明書の年次更新で thumbprint が変わっても動く
$SignParams = "/n `"$CertSubjectName`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"

# WASAPI Process Loopback が必要なため win-x64 self-contained に固定 (CI build.yml と同一方針)
$RuntimeMatrix = @{
    'win-x64' = @{ Channel = 'win-x64' }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot
$WorkDir = Join-Path $RepoRoot 'local-release'
$ArtifactsDir = Join-Path $WorkDir 'artifacts'

function Invoke-Native {
    param([string]$Description, [scriptblock]$Block)
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$Description が失敗しました (exit $LASTEXITCODE)" }
}

# ---- 0. プリフライト ----
Write-Host '== プリフライト ==' -ForegroundColor Cyan

# Git Bash (MSYS) 経由で起動すると括弧入り環境変数が落ちて、ビルドツールチェーンの
# vswhere.exe 解決が壊れることがあるため補完する (self-contained ビルドでも無害)
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)' }

# ローカルでは VS Installer ディレクトリが PATH に無いことがある → 追加 (vswhere.exe 解決用)
$vsInstallerDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ($env:PATH -notlike "*$vsInstallerDir*") { $env:PATH = "$env:PATH;$vsInstallerDir" }

# vpk (dotnet tool) が要求するランタイムがローカルに無い場合に備えてロールフォワード
$env:DOTNET_ROLL_FORWARD = 'Major'

$version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Directory.Build.props から <Version> を取得できませんでした' }
Write-Host "バージョン: $version"

# SimplySign 接続確認 (証明書が見えなければ署名できないので最初に落とす)
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -like "CN=$CertSubjectName*" -and $_.NotAfter -gt (Get-Date) }
if (-not $cert) {
    throw "署名証明書 (CN=$CertSubjectName) が見つかりません。SimplySign Desktop を起動してトークンでログインしてください。"
}
Write-Host "署名証明書: $($cert.Subject) (期限 $($cert.NotAfter.ToString('yyyy-MM-dd')))"

# vpk を固定バージョンで用意
$vpkInstalled = (dotnet tool list --global | Select-String -SimpleMatch 'vpk') -match [regex]::Escape($VpkVersion)
if (-not $vpkInstalled) {
    Write-Host "vpk $VpkVersion をインストールします..."
    dotnet tool uninstall --global vpk 2>$null | Out-Null
    Invoke-Native 'vpk のインストール' { dotnet tool install --global vpk --version $VpkVersion }
}

# Cloudflare トークン (アップロード時のみ必要)
if (-not $SkipUpload) {
    $secrets = Get-Content $SecretsPath -Raw | ConvertFrom-Json
    if (-not $secrets.cloudflare.api_token) { throw "secrets.json に cloudflare.api_token が見つかりません" }
    $env:CLOUDFLARE_API_TOKEN = $secrets.cloudflare.api_token
    $env:CLOUDFLARE_ACCOUNT_ID = $AccountId
}

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

# ---- 1. ビルド + 署名付きパッケージング (RID ごと) ----
foreach ($runtime in $Runtimes) {
    $config = $RuntimeMatrix[$runtime]
    if (-not $config) { throw "未知の runtime: $runtime" }
    $publishDir = Join-Path $WorkDir "publish-$runtime"

    Write-Host "== publish: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "dotnet publish ($runtime)" {
        dotnet publish src/RealTimeTranslator.UI/RealTimeTranslator.UI.csproj -c Release -r $runtime `
            --self-contained true -o $publishDir
    }

    foreach ($required in @('RealTimeTranslator.UI.exe')) {
        if (-not (Test-Path (Join-Path $publishDir $required))) {
            throw "$required が publish 出力にありません ($runtime)"
        }
    }

    Write-Host "== vpk pack + 署名: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "vpk pack ($runtime)" {
        vpk pack `
            --packId RealTimeTranslator `
            --packVersion $version `
            --packTitle 'RealTimeTranslator' `
            --packAuthors '1llum1n4t1s' `
            --mainExe RealTimeTranslator.UI.exe `
            --icon (Join-Path 'icon' 'app.ico') `
            --packDir $publishDir `
            --outputDir $ArtifactsDir `
            --channel $config.Channel `
            --shortcuts 'StartMenu,Desktop' `
            --signParams $SignParams
    }
}

# 署名検証 (Setup.exe が正しく署名されているかリリース前に確認)
Write-Host '== 署名検証 ==' -ForegroundColor Cyan
foreach ($exe in Get-ChildItem $ArtifactsDir -Filter '*.exe') {
    $sig = Get-AuthenticodeSignature $exe.FullName
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "CN=$CertSubjectName*") {
        throw "署名検証失敗: $($exe.Name) → $($sig.Status)"
    }
    Write-Host "  ✅ $($exe.Name): Valid ($($sig.SignerCertificate.Subject -replace ',.*$'))"
}

if ($SkipUpload) {
    Write-Host "`n✅ -SkipUpload 指定のためここで終了。成果物: $ArtifactsDir" -ForegroundColor Green
    Get-ChildItem $ArtifactsDir | Format-Table Name, @{n='Size(MB)'; e={[math]::Round($_.Length/1MB,1)}}
    return
}

# ---- 2. R2 アップロード ----
# - releases.{channel}.json (manifest) は同 channel の旧版を上書き
# - *.nupkg は put のみ (過去版は cleanup ステップが manifest 基準で削除)
Write-Host '== R2 アップロード ==' -ForegroundColor Cyan
$uploaded = 0
foreach ($f in Get-ChildItem $ArtifactsDir -File) {
    Write-Host "  ↑ $($f.Name)"
    Invoke-Native "R2 put ($($f.Name))" {
        pnpm dlx "wrangler@$WranglerVersion" r2 object put "$Bucket/$($f.Name)" --file $f.FullName --remote
    }
    $uploaded++
}
Write-Host "✅ R2 アップロード完了: $uploaded ファイル"

# ---- 3. 配信確認 (CDN/edge 伝播チェック) ----
# 旧 CI の /rere #A2-008 + #F-R2-004 対応を踏襲: releases.{channel}.json の HTTP 200 だけでなく
# アップロードした全ファイルを HEAD で到達性確認し、部分アップロード失敗の中間状態露出を防ぐ。
Write-Host '== 配信確認 ==' -ForegroundColor Cyan
foreach ($runtime in $Runtimes) {
    $channel = $RuntimeMatrix[$runtime].Channel
    $url = "$BaseUrl/releases.$channel.json"
    $resp = Invoke-WebRequest -Uri $url -TimeoutSec 30 -MaximumRetryCount 3 -RetryIntervalSec 5
    Write-Host "  $url → HTTP $($resp.StatusCode) ($($resp.RawContentLength) bytes)"
}
$headFailed = 0
foreach ($f in Get-ChildItem $ArtifactsDir -File) {
    $url = "$BaseUrl/$($f.Name)"
    try {
        $resp = Invoke-WebRequest -Uri $url -Method Head -TimeoutSec 15 -MaximumRetryCount 2 -RetryIntervalSec 3
        Write-Host "  HEAD $($f.Name) → HTTP $($resp.StatusCode)"
    } catch {
        Write-Warning "  R2 配信物が見つかりません: $url — $($_.Exception.Message)"
        $headFailed++
    }
}
if ($headFailed -gt 0) {
    throw "配信検証失敗 — $headFailed 件のファイルが R2 から取得できません。wrangler r2 object list を確認してください。"
}

# ---- 4. 旧バージョン nupkg のクリーンアップ (Aggressive 戦略) ----
# ローカル artifacts の manifest (= 今アップロードしたものと同一) から keep set を作り、
# R2 上の「.nupkg かつ manifest 外」だけを削除する。固定ファイル名 (Setup.exe /
# Portable.zip / RELEASES* / assets.*.json / releases.*.json) は対象外なので安全。
Write-Host '== 旧 nupkg クリーンアップ ==' -ForegroundColor Cyan
$keep = @{}
$manifests = Get-ChildItem $ArtifactsDir -Filter 'releases.*.json'
if (-not $manifests) { throw 'artifacts に releases.*.json が見つかりません' }
foreach ($m in $manifests) {
    foreach ($asset in (Get-Content $m.FullName -Raw | ConvertFrom-Json).Assets) {
        if ($asset.FileName) { $keep[$asset.FileName] = $true }
    }
}
Write-Host "  保持対象 nupkg: $($keep.Count) 件"

$api = "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket"
$headers = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }

$allKeys = [System.Collections.Generic.List[string]]::new()
$cursor = ''
while ($true) {
    $uri = "$api/objects?per_page=1000" + $(if ($cursor) { "&cursor=$cursor" })
    $resp = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 30
    foreach ($obj in $resp.result) { $allKeys.Add($obj.key) }
    # 全件 1 ページに収まると result_info が省略される (StrictMode 下では直接参照が throw)
    $info = $resp.PSObject.Properties['result_info']
    if (-not $info -or -not $info.Value) { break }
    $truncated = $info.Value.PSObject.Properties['is_truncated']
    if (-not $truncated -or -not $truncated.Value) { break }
    $cursorProp = $info.Value.PSObject.Properties['cursor']
    $cursor = if ($cursorProp) { $cursorProp.Value } else { '' }
    if (-not $cursor) { break }
}

$toDelete = $allKeys | Where-Object { $_ -like '*.nupkg' -and -not $keep.ContainsKey($_) }
if (-not $toDelete) {
    Write-Host '  ✅ 削除対象なし'
} else {
    $deleted = 0; $failed = 0
    foreach ($key in $toDelete) {
        $encoded = [uri]::EscapeDataString($key)
        try {
            Invoke-RestMethod -Method Delete -Uri "$api/objects/$encoded" -Headers $headers -TimeoutSec 30 | Out-Null
            Write-Host "  🗑️  $key"
            $deleted++
        } catch {
            Write-Warning "  削除失敗: $key — $($_.Exception.Message)"
            $failed++
        }
    }
    Write-Host "  🧹 クリーンアップ: $deleted 削除 / $failed 失敗"
    # 全件失敗は token 権限等の異常なので fail (一部失敗は次回リリースで再試行される)
    if ($failed -gt 0 -and $deleted -eq 0) { throw '旧 nupkg の削除がすべて失敗しました。API token の権限を確認してください。' }
}

Write-Host "`n🎉 リリース完了: v$version → $BaseUrl" -ForegroundColor Green
