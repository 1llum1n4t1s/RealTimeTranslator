# release-local.ps1 — ローカル署名付き Velopack リリース
#
# SimplySign (Certum クラウド署名) は Desktop 接続 + スマホトークンが必要で
# GitHub Actions からは署名できないため、リリースは本スクリプトでローカル実行する。
# 旧 CI リリース (.github/workflows/release.yml + velopack.yml) はこのスクリプトに置換済み。
#
# 前提:
#   - SimplySign Desktop が接続済み (証明書が CurrentUser\My に見えていること)
#   - Directory.Build.props の <Version> がリリースしたいバージョンになっていること (/vava 済み)
#   - D:\Dev\Secret\secrets.json に cloudflare.api_token があること
#
# 使い方:
#   pwsh scripts/release-local.ps1                # フルリリース (build + sign + upload + cleanup)
#   pwsh scripts/release-local.ps1 -SkipUpload    # ビルド + 署名のみ (アップロードしない動作確認用)
#   pwsh scripts/release-local.ps1 -SkipCleanup   # 配信確認後も外部の作業先を保持する
#   WorkDirectory は未作成の絶対パス、DirectoryBuildPropsPath はその配下へ出力する既存 props。

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [switch]$SkipCleanup,
    [string[]]$Runtimes = @('win-x64'),
    [string]$WorkDirectory,
    [string]$DirectoryBuildPropsPath,
    [string]$LedgerDirectory
)


# 検証済みの作業対象だけを標準APIで削除する。
function Remove-LocalWorkItem {
    param([string[]]$LiteralPath, [string]$AllowedRoot, [switch]$IgnoreMissing, [switch]$AllowLeafJunction)
    if ([string]::IsNullOrWhiteSpace($AllowedRoot) -or -not [IO.Path]::IsPathRooted($AllowedRoot)) { throw 'Cleanup root must be absolute.' }
    $boundary = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    foreach ($target in $LiteralPath) {
        if ([string]::IsNullOrWhiteSpace($target) -or -not [IO.Path]::IsPathRooted($target)) { throw 'Cleanup target must be absolute.' }
        $resolved = [IO.Path]::GetFullPath($target)
        if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target is outside the allowed root.' }
        $ancestor = $resolved
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and -not ($AllowLeafJunction -and $ancestor -eq $resolved -and $item.LinkType -eq 'Junction')) { throw 'Cleanup path contains a reparse point.' }
            }
            $ancestor = Split-Path $ancestor -Parent
        }
        if (-not (Test-Path -LiteralPath $resolved)) { if ($IgnoreMissing) { continue }; throw "Cleanup target is missing: $resolved" }
        $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
        $leafLink = [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
        if ($item.PSIsContainer -and -not $leafLink) {
            if (@(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Cleanup directory contains a reparse point.' }
        }
        Remove-Item -LiteralPath $resolved -Recurse:($item.PSIsContainer -and -not $leafLink) -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $resolved) { throw "Cleanup target remains: $resolved" }
    }
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseInitialTemp = $env:TEMP
$releaseInitialTmp = $env:TMP

# ---- 定数 (旧 CI 版 release.yml / velopack.yml と揃える) ----
# Velopack (vpk) の最新安定版はプリフライト後に解決し、今回専用の tool-path へ入れる。
$WranglerVersion = '4.151.1'        # サプライチェーン対策でバージョン固定
$Bucket = 'realtimetranslator-updates'
$BaseUrl = 'https://rtt.kagayoi.com'
$AccountId = '10901bfadbf1005164774a7350082985'
$SecretsPath = 'D:\Dev\Secret\secrets.json'
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'
# /n (Subject 名) で選択: 証明書の年次更新で thumbprint が変わっても動く
$SignParams = "/n `"$CertSubjectName`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"

# WASAPI Process Loopback が必要なため win-x64 self-contained に固定 (CI build.yml と同一方針)
$RuntimeMatrix = @{
    'win-x64' = @{ Channel = 'win-x64' }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Invoke-Native {
    param([string]$Description, [scriptblock]$Block)
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$Description が失敗しました (exit $LASTEXITCODE)" }
}

function Assert-ExternalPath {
    param([string]$Path)
    if ($Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\)' -or [WildcardPattern]::ContainsWildcardCharacters($Path)) {
        throw "絶対パスを指定してください: $Path"
    }
    $absolute = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]'\/')
    $codexRoot = if ($env:CODEX_HOME) { [IO.Path]::GetFullPath($env:CODEX_HOME) } else { Join-Path $env:USERPROFILE '.codex' }
    foreach ($forbidden in @($RepoRoot, (Join-Path $env:USERPROFILE '.codex'), $codexRoot)) {
        $root = $forbidden.TrimEnd([char[]]'\/')
        if ($absolute.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $absolute.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "作業先はリポジトリと Codex ユーザーディレクトリの外に指定してください: $absolute"
        }
    }
    if (($absolute -split '[\\/]') -contains 'artifacts') { throw "artifacts というフォルダー名は使用できません: $absolute" }
    $ancestor = $absolute
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "作業先と親フォルダーに reparse point は使用できません: $ancestor"
            }
            if ($item.PSIsContainer -and (Test-Path -LiteralPath (Join-Path $ancestor '.git'))) {
                throw "作業先はすべてのリポジトリの外に限定します: $ancestor"
            }
        }
        $parent = Split-Path -Parent $ancestor
        if ($parent -eq $ancestor) { break }
        $ancestor = $parent
    }
    return $absolute
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

# XPath で取得 (member enumeration は Version を持たない PropertyGroup 混在時に StrictMode で throw する)
$versionNode = ([xml](Get-Content 'Directory.Build.props' -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { $versionNode.InnerText.Trim() } else { $null }
if (-not $version) { throw 'Directory.Build.props から <Version> を取得できませんでした' }
Write-Host "バージョン: $version"
if ($Runtimes.Count -ne 1 -or $Runtimes[0] -ne 'win-x64') { throw 'リリース対象は win-x64 のみです。' }
if (-not $WorkDirectory) {
    $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) "Codex\RealTimeTranslator\release-$version-$([Guid]::NewGuid().ToString('N'))"
}
$WorkDir = Assert-ExternalPath $WorkDirectory
if (Test-Path -LiteralPath $WorkDir) { throw "既存の作業先は上書きしません。新しい WorkDirectory を指定してください: $WorkDir" }
if (-not $LedgerDirectory) {
    $LedgerDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Codex\TaskArtifacts\RealTimeTranslator\release-$version-$([Guid]::NewGuid().ToString('N'))\cleanup"
}
$LedgerDir = Assert-ExternalPath $LedgerDirectory
if ($LedgerDir.Equals($WorkDir, [StringComparison]::OrdinalIgnoreCase) -or $LedgerDir.StartsWith($WorkDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'LedgerDirectory は清掃対象の WorkDirectory の外に指定してください。'
}
if ($DirectoryBuildPropsPath) {
    $DirectoryBuildPropsPath = Assert-ExternalPath $DirectoryBuildPropsPath
    if (-not (Test-Path -LiteralPath $DirectoryBuildPropsPath -PathType Leaf)) { throw 'DirectoryBuildPropsPath が見つかりません。' }
}
$PackagesDir = Join-Path $WorkDir 'packages'
New-Item -ItemType Directory -Path $PackagesDir | Out-Null
Write-Host "作業先: $WorkDir / 清掃台帳: $LedgerDir"

# 生成物、ツールの download/cache/log、一時ファイルを今回の作業先へ集約する。
foreach ($name in @('TEMP', 'TMP', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'npm_config_cache',
        'npm_config_logs_dir', 'npm_config_store_dir', 'npm_config_cache_dir', 'npm_config_state_dir',
        'PNPM_HOME', 'XDG_CACHE_HOME', 'XDG_CONFIG_HOME', 'WRANGLER_HOME')) {
    $directory = Join-Path $WorkDir "cache\$name"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [Environment]::SetEnvironmentVariable($name, $directory, 'Process')
}
$env:WRANGLER_LOG_PATH = Join-Path $WorkDir 'wrangler.log'
$env:WRANGLER_SEND_METRICS = 'false'
$env:npm_config_update_notifier = 'false'
$env:npm_config_userconfig = Join-Path $WorkDir 'npmrc'
Set-Content -LiteralPath $env:npm_config_userconfig -Value ''

if (-not $DirectoryBuildPropsPath) {
    $DirectoryBuildPropsPath = Join-Path $WorkDir 'ReleaseBuild.props'
    $repoProps = [Security.SecurityElement]::Escape((Join-Path $RepoRoot 'Directory.Build.props'))
    $buildRoot = [Security.SecurityElement]::Escape((Join-Path $WorkDir 'build'))
    @"
<Project>
  <Import Project="$repoProps" />
  <PropertyGroup>
    <BaseOutputPath>$buildRoot\bin\`$(MSBuildProjectName)\</BaseOutputPath>
    <OutputPath>`$(BaseOutputPath)`$(Configuration)\</OutputPath>
    <BaseIntermediateOutputPath>$buildRoot\obj\`$(MSBuildProjectName)\</BaseIntermediateOutputPath>
    <IntermediateOutputPath>`$(BaseIntermediateOutputPath)`$(Configuration)\</IntermediateOutputPath>
    <MSBuildProjectExtensionsPath>`$(IntermediateOutputPath)</MSBuildProjectExtensionsPath>
    <DefaultItemExcludes>`$(DefaultItemExcludes);`$(MSBuildProjectDirectory)\bin\**;`$(MSBuildProjectDirectory)\obj\**</DefaultItemExcludes>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $DirectoryBuildPropsPath -Encoding utf8
}
# 呼び出し側が渡した props も評価して、全プロジェクトの出力境界を確認する。
foreach ($projectName in @('RealTimeTranslator.Core', 'RealTimeTranslator.UI', 'RealTimeTranslator.Tests')) {
    $project = Get-Item -LiteralPath (Join-Path $RepoRoot "src\$projectName\$projectName.csproj")
    $properties = & dotnet msbuild $project.FullName -nologo -p:Configuration=Release -p:Platform=x64 `
        "-p:DirectoryBuildPropsPath=$DirectoryBuildPropsPath" -getProperty:BaseOutputPath,OutputPath,BaseIntermediateOutputPath,IntermediateOutputPath,MSBuildProjectExtensionsPath
    if ($LASTEXITCODE -ne 0) { throw "外部出力先の評価に失敗しました: $($project.Name)" }
    foreach ($property in (($properties -join "`n" | ConvertFrom-Json).Properties.PSObject.Properties)) {
        $output = Assert-ExternalPath $property.Value
        if (-not $output.StartsWith($WorkDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "$($project.Name) の $($property.Name) が WorkDirectory の外です: $output"
        }
    }
}

# SimplySign 接続確認 (証明書が見えなければ署名できないので最初に落とす)
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -like "CN=$CertSubjectName*" -and $_.NotAfter -gt (Get-Date) }
if (-not $cert) {
    throw "署名証明書 (CN=$CertSubjectName) が見つかりません。SimplySign Desktop を起動してトークンでログインしてください。"
}
Write-Host "署名証明書: $($cert.Subject) (期限 $($cert.NotAfter.ToString('yyyy-MM-dd')))"

# グローバル vpk は変更しない。今回専用の最新安定版を使う。
$VpkVersion = (Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/vpk/index.json' -TimeoutSec 30).versions |
    Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
if (-not $VpkVersion) { throw 'vpk の最新安定版バージョンを取得できませんでした。' }
$toolDir = Join-Path $WorkDir 'tools'
Write-Host "vpk 最新安定版: $VpkVersion"
Invoke-Native 'vpk のインストール' { dotnet tool install --tool-path $toolDir vpk --version $VpkVersion }
$vpkExe = Join-Path $toolDir 'vpk.exe'

# Cloudflare トークン (アップロード時のみ必要)
if (-not $SkipUpload) {
    $secrets = Get-Content $SecretsPath -Raw | ConvertFrom-Json
    if (-not $secrets.cloudflare.api_token) { throw "secrets.json に cloudflare.api_token が見つかりません" }
    $env:CLOUDFLARE_API_TOKEN = $secrets.cloudflare.api_token
    $env:CLOUDFLARE_ACCOUNT_ID = $AccountId
}

# 毎回新しい外部 obj を使うため、古い ReadyToRun キャッシュは再利用しない。
Invoke-Native 'locked RID restore' {
    dotnet restore RealTimeTranslator.slnx -r win-x64 --locked-mode -p:Configuration=Release -p:Platform=x64 `
        "-p:DirectoryBuildPropsPath=$DirectoryBuildPropsPath"
}

# ---- 1. ビルド + 署名付きパッケージング (RID ごと) ----
foreach ($runtime in $Runtimes) {
    $config = $RuntimeMatrix[$runtime]
    if (-not $config) { throw "未知の runtime: $runtime" }
    $publishDir = Join-Path $WorkDir "publish-$runtime"

    Write-Host "== publish: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "dotnet publish ($runtime)" {
        dotnet publish src/RealTimeTranslator.UI/RealTimeTranslator.UI.csproj -c Release -r $runtime `
            --self-contained true --no-restore -p:Platform=x64 "-p:DirectoryBuildPropsPath=$DirectoryBuildPropsPath" -o $publishDir
    }

    foreach ($required in @('RealTimeTranslator.UI.exe')) {
        if (-not (Test-Path (Join-Path $publishDir $required))) {
            throw "$required が publish 出力にありません ($runtime)"
        }
    }

    Write-Host "== vpk pack + 署名: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "vpk pack ($runtime)" {
        & $vpkExe pack `
            --packId RealTimeTranslator `
            --packVersion $version `
            --packTitle 'RealTimeTranslator' `
            --packAuthors 'Kagayoi' `
            --mainExe RealTimeTranslator.UI.exe `
            --icon (Join-Path 'icon' 'app.ico') `
            --packDir $publishDir `
            --outputDir $PackagesDir `
            --channel $config.Channel `
            --runtime $runtime `
            --shortcuts 'StartMenuRoot,Desktop' `
            --signParams $SignParams
    }
}

# 署名検証 (Setup.exe が正しく署名されているかリリース前に確認)
Write-Host '== 署名検証 ==' -ForegroundColor Cyan
foreach ($exe in Get-ChildItem $PackagesDir -Filter '*.exe') {
    $sig = Get-AuthenticodeSignature $exe.FullName
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "CN=$CertSubjectName*") {
        throw "署名検証失敗: $($exe.Name) → $($sig.Status)"
    }
    Write-Host "  ✅ $($exe.Name): Valid ($($sig.SignerCertificate.Subject -replace ',.*$'))"
}

if ($SkipUpload) {
    Write-Host "`n✅ -SkipUpload 指定のため署名済み成果物を保持します: $PackagesDir" -ForegroundColor Green
    Get-ChildItem $PackagesDir | Format-Table Name, @{n='Size(MB)'; e={[math]::Round($_.Length/1MB,1)}}
    return
}

# ---- 2. R2 アップロード ----
# - releases.{channel}.json (manifest) は同 channel の旧版を上書き
# - *.nupkg は put のみ (過去版は cleanup ステップが manifest 基準で削除)
Write-Host '== R2 アップロード ==' -ForegroundColor Cyan
$uploaded = 0
# 更新 metadata は、参照する package と assets をすべて置いてから最後に公開する。
$packageFiles = @(Get-ChildItem $PackagesDir -File)
$uploadFiles = @($packageFiles | Sort-Object @{ Expression = { if ($_.Name -like 'releases.*.json' -or $_.Name -like 'RELEASES*') { 1 } else { 0 } } }, Name)
foreach ($f in $uploadFiles) {
    Write-Host "  ↑ $($f.Name)"
    Invoke-Native "R2 put ($($f.Name))" {
        pnpm dlx "wrangler@$WranglerVersion" r2 object put "$Bucket/$($f.Name)" --file $f.FullName --remote
    }
    $uploaded++
}
Write-Host "✅ R2 アップロード完了: $uploaded ファイル"

# ---- 2.5 配信実物の SHA256 確認と、実証された stale URL のみのパージ ----
Write-Host '== 配信物 SHA256 確認 ==' -ForegroundColor Cyan
$cfHeaders = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
$downloadDir = Join-Path $WorkDir 'download'
New-Item -ItemType Directory -Path $downloadDir | Out-Null
function Test-DeliveredHash {
    param([IO.FileInfo]$File, [string]$Url, [switch]$Fresh)
    $download = Join-Path $downloadDir "$([Guid]::NewGuid().ToString('N'))-$($File.Name)"
    $requestUrl = if ($Fresh) { "$Url`?release_check=$([Guid]::NewGuid().ToString('N'))" } else { $Url }
    Invoke-WebRequest -Uri $requestUrl -OutFile $download -TimeoutSec 300 -MaximumRetryCount 2 -RetryIntervalSec 3 | Out-Null
    $localHash = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash
    $remoteHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    $matches = $localHash -eq $remoteHash
    Write-Host "  $($File.Name): SHA256 $(if ($matches) { '一致' } else { '不一致' })"
    [PSCustomObject]@{ File = $File.Name; Url = $requestUrl; LocalSHA256 = $localHash; RemoteSHA256 = $remoteHash; Matches = $matches } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $WorkDir 'delivery-verification.jsonl')
    return $matches
}
$staleFiles = @()
foreach ($file in $packageFiles) {
    $url = "$BaseUrl/$([uri]::EscapeDataString($file.Name))"
    if (-not (Test-DeliveredHash $file $url -Fresh)) { throw "新規取得でも配信物が一致しません: $url" }
    # 利用者が取得する query なし URL も確認し、新しい cache key の一致だけで完了にしない。
    if (-not (Test-DeliveredHash $file $url)) { $staleFiles += $file }
}
if ($staleFiles.Count -gt 0) {
    # 一意なバージョン名への不一致は upload 異常として停止し、cache purge で隠さない。
    if (@($staleFiles | Where-Object { $_.Name -match '\d+\.\d+\.\d+' }).Count -gt 0) {
        throw 'バージョン付き配信物の SHA256 が一致しません。成果物を保持します。'
    }
    $hostname = ([uri]$BaseUrl).Host
    $zoneName = $hostname -replace '^[^.]+\.', ''
    $zoneResp = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$zoneName&account.id=$AccountId" -Headers $cfHeaders -TimeoutSec 30
    $zones = @($zoneResp.result | Where-Object { $_.name -eq $zoneName -and $_.account.id -eq $AccountId })
    if (-not $zoneResp.success -or $zones.Count -ne 1 -or -not $hostname.EndsWith(".$zoneName", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'パージ対象の account / Zone / hostname を一意に確認できません。成果物を保持します。'
    }
    # hostname が今回の R2 bucket の custom domain であることを確認する。
    $domainResp = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket/domains/custom" -Headers $cfHeaders -TimeoutSec 30
    $domains = @($domainResp.result.domains | Where-Object { $_.domain -eq $hostname -and $_.zoneId -eq $zones[0].id -and $_.enabled })
    if (-not $domainResp.success -or $domains.Count -ne 1) { throw 'R2 bucket と配信 hostname の対応を確認できません。' }
    $purgeUrls = @($staleFiles | ForEach-Object { "$BaseUrl/$([uri]::EscapeDataString($_.Name))" })
    $purgeBody = [PSCustomObject]@{ files = $purgeUrls } | ConvertTo-Json -Compress
    $purgeResp = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$($zones[0].id)/purge_cache" `
        -Headers $cfHeaders -ContentType 'application/json' -Body $purgeBody -TimeoutSec 30
    if (-not $purgeResp.success) { throw '実証された stale URL のパージに失敗しました。' }
    foreach ($file in $staleFiles) {
        $url = "$BaseUrl/$([uri]::EscapeDataString($file.Name))"
        if (-not (Test-DeliveredHash $file $url)) { throw "パージ後も配信物が一致しません: $url" }
    }
} else {
    Write-Host '  全配信物が一致したためパージは不要です。'
}

# ---- 4. 旧バージョン nupkg のクリーンアップ (Aggressive 戦略) ----
# ローカル packages の manifest (= 今アップロードしたものと同一) から keep set を作り、
# R2 上の「.nupkg かつ manifest 外」だけを削除する。固定ファイル名 (Setup.exe /
# Portable.zip / RELEASES* / assets.*.json / releases.*.json) は対象外なので安全。
Write-Host '== 旧 nupkg クリーンアップ ==' -ForegroundColor Cyan
$keep = @{}
$manifests = Get-ChildItem $PackagesDir -Filter 'releases.*.json'
if (-not $manifests) { throw 'packages に releases.*.json が見つかりません' }
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

# 全プロジェクト共通の保持ポリシー: 直近 2 バージョン。
# 旧実装は '*.nupkg' だけを削除対象にしていたため、バージョン付きの配布物
# (zip / deb / rpm / AppImage 等) が R2 に永久に溜まっていた (Ferry で 351 個 7.2GB)。
$KeepVersionCount = 2
$versionPattern = '(\d+\.\d+\.\d+)'
$allVersions = @(
    $allKeys | ForEach-Object {
        $m = [regex]::Match($_, $versionPattern)
        if ($m.Success) { $m.Groups[1].Value }
    } | Sort-Object -Property { [version]$_ } -Unique
)
$keepVersions = @($allVersions | Select-Object -Last $KeepVersionCount)
Write-Host "  保持バージョン: $($keepVersions -join ', ') (全 $($allVersions.Count) 世代)"

$toDelete = $allKeys | Where-Object {
    # manifest が参照するファイルは絶対保持 (消すと自動更新が壊れる)
    if ($keep.ContainsKey($_)) { return $false }
    # 固定ファイル名はバージョン文字列を含まない = 毎リリース上書きなので保持
    $m = [regex]::Match($_, $versionPattern)
    if (-not $m.Success) { return $false }
    return $keepVersions -notcontains $m.Groups[1].Value
}
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

# ---- 5. 今回の外部作業先だけを清掃 ----
# SkipUpload と途中失敗は復元・再開用に保持する。再生成 restore は行わない。
function Remove-ReleaseWorkDirectory {
    param([string]$WorkDir, [string]$LedgerDir)

    # pnpm の専用 store にできた junction はリンク自身を先に清掃する。
    # 削除処理は子孫の reparse point を含むフォルダーの再帰削除を拒否する。
    $links = @(Get-ChildItem -LiteralPath $WorkDir -Recurse -Directory -Force |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    foreach ($link in $links) {
        if ($link.LinkType -ne 'Junction' -or @($link.Target).Count -ne 1 -or
            -not ([IO.Path]::GetFullPath([string]$link.Target)).StartsWith($WorkDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "今回の作業先内へ向く junction 以外は保持します: $($link.FullName)"
        }
    }
    foreach ($link in $links) {
        Remove-LocalWorkItem -LiteralPath $link.FullName -AllowedRoot $WorkDir -AllowLeafJunction
    }
    Remove-LocalWorkItem -LiteralPath $WorkDir -AllowedRoot (Split-Path -Parent $WorkDir)
    if (Test-Path -LiteralPath $WorkDir) { throw "清掃後も作業先が残っています: $WorkDir" }
}
if ($SkipCleanup) {
    Write-Host "作業先を保持します: $WorkDir"
} else {
    $env:TEMP = $releaseInitialTemp
    $env:TMP = $releaseInitialTmp
    Remove-ReleaseWorkDirectory -WorkDir $WorkDir -LedgerDir $LedgerDir
}

Write-Host "`n🎉 リリース完了: v$version → $BaseUrl" -ForegroundColor Green
