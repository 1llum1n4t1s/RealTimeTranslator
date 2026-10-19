Set-StrictMode -Version Latest

function Get-RttWorkRoot {
    $configuration = Join-Path $PSScriptRoot '../Directory.Build.local.props'
    if (-not $IsWindows -or $env:GITHUB_ACTIONS -eq 'true' -or $env:TF_BUILD -eq 'true' -or $env:CI -eq 'true') { return $null }
    if (Test-Path -LiteralPath $configuration) {
        [xml] $settings = Get-Content -LiteralPath $configuration -Raw
        return [IO.Path]::GetFullPath([string]$settings.Project.PropertyGroup.RttWorkRoot).TrimEnd('\', '/')
    }
    if ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) -eq 'D:\Dev\RealTimeTranslator') {
        throw 'Directory.Build.local.props が必要です。正本設定なしでCへfallbackしません。'
    }
    return $null
}

function Assert-RttPath([string] $Path, [string] $Root, [string] $BaseDirectory = (Get-Location).Path, [switch] $AllowFileLeaf) {
    if (-not [IO.Path]::IsPathFullyQualified($Root) -or -not (Test-Path -LiteralPath ([IO.Path]::GetPathRoot($Root)) -PathType Container)) { throw "作業ドライブが利用できません: $Root" }
    $full = [IO.Path]::GetFullPath($Path, $BaseDirectory)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $prefix = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, $comparison)) { throw "開発出力は $Root の作業別directoryに限定します: $full" }
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        $allowedFileLeaf = $AllowFileLeaf -and $cursor -eq $full -and $item -is [IO.FileInfo]
        if ($item -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($item -isnot [IO.DirectoryInfo] -and -not $allowedFileLeaf))) { throw "開発出力の祖先にリンク/ファイルは使用できません: $cursor" }
    }
    return $full
}

function Assert-RttEnvironment([string] $Root) {
    if (-not $Root) { return }
    foreach ($name in @('TEMP', 'TMP')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not $value) { throw "$name を開発workroot配下に指定してください。scripts/Dev.ps1 を使用してください。" }
        $null = Assert-RttPath $value $Root
    }
}
