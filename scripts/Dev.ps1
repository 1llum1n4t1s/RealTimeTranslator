[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkDirectory,
    [ValidateSet('dotnet')] [string] $Tool = 'dotnet',
    [Parameter(ValueFromRemainingArguments)] [string[]] $ToolArguments
)
$ErrorActionPreference = 'Stop'
# pwsh -Fileの残余引数bindingで「-p:Value」が2要素になる場合もMSBuild書式へ戻す。
$normalized = [Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt @($ToolArguments).Count; $i++) {
    $value = $ToolArguments[$i]
    if ($value -eq '--') { for (; $i -lt $ToolArguments.Count; $i++) { $normalized.Add($ToolArguments[$i]) }; break }
    if ($value -match '^[-/](p|property|getProperty|getItem|t|target|bl|binarylogger|flp\d*|fileloggerparameters\d*)$' -and $i + 1 -lt $ToolArguments.Count -and -not $ToolArguments[$i + 1].StartsWith('-')) {
        if ($value -notmatch '^[-/]p$' -or $ToolArguments[$i + 1].Contains('=')) { $value += ':' + $ToolArguments[++$i] }
    }
    $normalized.Add($value)
}
$ToolArguments = $normalized.ToArray()
if ($ToolArguments.Count -gt 0 -and $ToolArguments[0] -eq 'test') {
    $separator = [Array]::IndexOf($ToolArguments, '--')
    if ($separator -ge 0) {
        foreach ($setting in $ToolArguments[($separator + 1)..($ToolArguments.Count - 1)]) {
            if ($setting -notmatch '^(RunConfiguration\.MaxCpuCount|MSTest\.Parallelize\.Workers)=\d+$' -and $setting -notmatch '^MSTest\.Parallelize\.Scope=(MethodLevel|ClassLevel)$') { throw 'testの追加runsettingsは並列数/Scopeだけを許可します。出力設定の迂回は禁止です。' }
        }
    }
}

. "$PSScriptRoot/DevelopmentOutput.ps1"
$root = Get-RttWorkRoot
if (-not $root) { $root = Join-Path ([IO.Path]::GetTempPath()) 'Codex/RealTimeTranslator' }
$work = Assert-RttPath $WorkDirectory $root
$temp = Assert-RttPath (Join-Path $work 'temp') $root
$build = Assert-RttPath (Join-Path $work 'build') $root
$results = Assert-RttPath (Join-Path $work 'results') $root
$fixtures = Assert-RttPath (Join-Path $work 'fixtures') $root
$cliHome = Assert-RttPath (Join-Path $work 'dotnet-home') $root
$cache = Assert-RttPath (Join-Path $work 'cache') $root
# 任意のprops/targets差替えは正本guardを外すので、開発wrapperでは受け付けない。
$outputProperties = '^(BaseOutputPath|OutputPath|OutDir|BaseIntermediateOutputPath|IntermediateOutputPath|MSBuildProjectExtensionsPath|ProjectAssetsFile|ArtifactsPath|PublishDir|PublishUrl|RestorePackagesPath|RestoreOutputPath|VSTestResultsDirectory|TestResultsDirectory|RttRunDirectory)$'
for ($i = 1; $i -lt @($ToolArguments).Count; $i++) {
    $arg = $ToolArguments[$i]
    if ($arg -eq '--') { break }
    if ($arg -match '(?i)(DirectoryBuildPropsPath|DirectoryBuildTargetsPath|ImportDirectoryBuildProps|ImportDirectoryBuildTargets|CustomBefore|CustomAfter)=') { throw '正本props/targetsの差替えは使用できません。' }
    if ($arg -match '^[-/](p|property):(.+)$') {
        foreach ($assignment in ($Matches[2] -split ';')) {
            $pair = $assignment -split '=', 2
            if ($pair.Count -eq 2 -and $pair[0] -match $outputProperties) { $null = Assert-RttPath $pair[1] $root -AllowFileLeaf }
        }
    }
    if ($arg -match '^--?(output|o|artifacts-path|results-directory|diag)(?:=|:)(.+)$') { $null = Assert-RttPath $Matches[2] $root -AllowFileLeaf }
    elseif ($arg -in @('--output','-o','--artifacts-path','--results-directory','--diag')) {
        if (++$i -ge $ToolArguments.Count) { throw '出力pathがありません。' }
        $null = Assert-RttPath $ToolArguments[$i] $root -AllowFileLeaf
    }
    if ($arg -match '^[-/](l|logger|dl|distributedlogger)(:|$)') { throw '任意MSBuild loggerは開発wrapperで使用できません。' }
    if ($arg -in @('--settings','-s') -or $arg -match '^--settings[=:]') { throw '外部runsettingsは使用できません。並列数などは正本の開発設定で指定してください。' }
    if ($arg -eq '--logger' -or $arg -match '^--logger[=:]') {
        if ($arg -eq '--logger') { if (++$i -ge $ToolArguments.Count) { throw 'logger指定がありません。' }; $logger = $ToolArguments[$i] }
        else { $logger = ($arg -split '[=:]',2)[1] }
        if (($logger -split ';',2)[0] -notin @('console','trx','html')) { throw '開発wrapperで許可するtest loggerはconsole/trx/htmlだけです。' }
        foreach ($m in [regex]::Matches($logger,'(?i)(?:^|;)(LogFileName|LogFilePrefix)=([^;]+)')) {
            $null = Assert-RttPath $m.Groups[2].Value $root $results -AllowFileLeaf
        }
    }
}

# MSBuildのloggerは初期targetより前に開くため、親process側で検査する。
if ($Tool -eq 'dotnet') {
    foreach ($argument in $ToolArguments) {
        if ($argument.TrimStart().StartsWith('@', [StringComparison]::Ordinal)) { throw '明示response file (@...) は起動前の出力検査を迂回するため使用できません。引数を直接指定してください。' }
        if ($argument -match '^[-/](bl|binarylogger)(:|$)') {
            $value = ($argument -split ':', 2 | Select-Object -Skip 1) -join ':'
            if (-not $value) { throw 'binary loggerにはwork directory配下の絶対pathを明示してください。' }
            $parameters = @($value -split ';')
            foreach ($parameter in @($parameters | Select-Object -Skip 1)) {
                if ($parameter -notmatch '^(?i)ProjectImports=(None|Embed|ZipFile)$') { throw 'binary loggerのpathは先頭の1件だけ明示してください。追加はProjectImports指定だけを受け付けます。' }
            }
            $logPath = $parameters[0] -replace '^(?i)LogFile=', ''
            $null = Assert-RttPath $logPath $root -AllowFileLeaf
        }
        if ($argument -match '^[-/](fl|filelogger)\d*$') { throw '既定file loggerはrepoへ書くため禁止です。LogFileをwork directory配下へ明示してください。' }
        if ($argument -match '^[-/](flp|fileloggerparameters)\d*:(.*)$') {
            $logFiles = [regex]::Matches($Matches[2], '(?i)(?:^|;)\s*LogFile\s*=\s*([^;]*)')
            if ($logFiles.Count -ne 1 -or [string]::IsNullOrWhiteSpace($logFiles[0].Groups[1].Value)) { throw 'file loggerのLogFileはちょうど1件だけ明示してください。重複/不明な指定は使用できません。' }
            $null = Assert-RttPath $logFiles[0].Groups[1].Value $root -AllowFileLeaf
        }
    }
    # runは-noAutoResponseをアプリへ転送する。SDKとproject/CWD祖先の自動rspを読む前に拒否する。
    if (@($ToolArguments).Count -gt 0 -and $ToolArguments[0] -eq 'run') {
        $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $starts = [Collections.Generic.List[string]]::new()
        $starts.Add((Get-Location).Path)
        for ($index = 1; $index -lt $ToolArguments.Count; $index++) {
            $value = $ToolArguments[$index]
            if ($value -eq '--') { break }
            if ($value -in @('--project', '-p', '--file')) {
                if ($index + 1 -lt $ToolArguments.Count) { $value = $ToolArguments[++$index] }
            } elseif ($value -match '^(?:--project|--file|-p)(?:=|:)(.+)$') { $value = $Matches[1] }
            elseif ($value.StartsWith('-')) { continue }
            if ($value -match '\.(csproj|fsproj|vbproj|sln|slnx|cs)$' -or (Test-Path -LiteralPath $value -PathType Container)) {
                $full = [IO.Path]::GetFullPath($value)
                $starts.Add($(if (Test-Path -LiteralPath $full -PathType Container) { $full } else { [IO.Path]::GetDirectoryName($full) }))
            }
        }
        foreach ($start in $starts) {
            for ($cursor = $start; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
                $null = $directories.Add($cursor)
                $globalJson = Join-Path $cursor 'global.json'
                if (Test-Path -LiteralPath $globalJson) {
                    $settings = Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json
                    $sdkSettings = $settings.PSObject.Properties['sdk']
                    if ($sdkSettings -and $sdkSettings.Value -and $sdkSettings.Value.PSObject.Properties['paths']) { throw 'runのカスタムSDK探索pathsでは自動responseを安全に検査できません。通常SDKと直接引数を使用してください。' }
                }
            }
        }
        $hostExecutable = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
        $hostFile = [IO.FileInfo]::new($hostExecutable)
        if ($hostFile.LinkTarget) { $hostExecutable = $hostFile.ResolveLinkTarget($true).FullName }
        $sdkRoot = Join-Path ([IO.Path]::GetDirectoryName($hostExecutable)) 'sdk'
        if (-not (Test-Path -LiteralPath $sdkRoot -PathType Container)) { throw 'runのSDK directoryを安全に確認できません。' }
        foreach ($sdk in Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction Stop) { $null = $directories.Add($sdk.FullName) }
        foreach ($directory in $directories) {
            foreach ($name in @('MSBuild.rsp', 'Directory.Build.rsp')) {
                $candidate = Join-Path $directory $name
                if (Test-Path -LiteralPath $candidate) { throw "runの自動response fileは起動前の出力検査を迂回するため使用できません: $candidate" }
            }
        }
    }
}
# 検査完了後にだけ作成し、子process全体へ同じtempを継承する。
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$saved = @{}
foreach ($name in @('TEMP', 'TMP', 'TMPDIR', 'RTT_WORK_DIRECTORY', 'RTT_TEST_OUTPUT_ROOT', 'DOTNET_CLI_HOME', 'NUGET_HTTP_CACHE_PATH', 'NUGET_SCRATCH', 'NUGET_PACKAGES', 'MSBUILDDISABLENODEREUSE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:TEMP = $temp; $env:TMP = $temp; $env:TMPDIR = $temp; $env:RTT_WORK_DIRECTORY = $work
    $env:RTT_TEST_OUTPUT_ROOT = $fixtures; $env:DOTNET_CLI_HOME = $cliHome
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $cache 'http'; $env:NUGET_SCRATCH = Join-Path $temp 'nuget'
    $env:NUGET_PACKAGES = Join-Path $cache 'packages'; $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = 'true'
    switch ($Tool) {
        'dotnet' {
            $arguments = @($ToolArguments)
            if ($arguments.Count -gt 0 -and $arguments[0] -in @('build', 'restore', 'run', 'publish', 'msbuild', 'pack', 'test')) {
                $additional = @("-p:ArtifactsPath=$build", "-p:RttRunDirectory=$work", "-p:VSTestResultsDirectory=$results", "-p:TestResultsDirectory=$results", "-nodeReuse:false")
                if ($arguments[0] -ne 'run') { $additional += '-noAutoResponse' }
                if ($arguments[0] -eq 'test') { $additional += @('--results-directory', $results) }
                $separator = [Array]::IndexOf($arguments, '--')
                if ($separator -lt 0) { $arguments += $additional }
                else { $arguments = @($arguments[0..($separator - 1)]) + $additional + @($arguments[$separator..($arguments.Count - 1)]) }
            }
            & dotnet @arguments
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "開発コマンドが失敗しました: $LASTEXITCODE" }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
