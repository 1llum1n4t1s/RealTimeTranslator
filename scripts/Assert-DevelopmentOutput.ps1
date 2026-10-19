param([string] $MSBuildPaths, [string] $MSBuildFiles, [string] $ProjectDirectory, [string] $ConfiguredRoot)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/DevelopmentOutput.ps1"
$root = Get-RttWorkRoot
Assert-RttEnvironment $root
if ($root) {
    if ($ConfiguredRoot -ne $root) { throw 'RttWorkRoot はローカル正本設定から変更できません。' }
    foreach ($path in $MSBuildPaths.Split('|')) {
        if ($path) { $null = Assert-RttPath $path $root $ProjectDirectory }
    }
    foreach ($path in $MSBuildFiles.Split('|')) {
        if ($path) { $null = Assert-RttPath $path $root $ProjectDirectory -AllowFileLeaf }
    }
}
