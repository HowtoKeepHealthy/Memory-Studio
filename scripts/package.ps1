param(
    [switch]$Test,
    [switch]$SkipNative
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskRelease = Join-Path $taskRoot 'artifacts\release'

# Publish the normal Windows application folder, including its runtime and DLLs.
& (Join-Path $PSScriptRoot 'build.ps1') -SkipNative:$SkipNative -Test:$Test
foreach ($taskDocument in @(
    @{ Source = (Join-Path $taskRoot 'README.md'); Name = 'README.md' },
    @{ Source = (Join-Path $taskRoot 'app\Assets\ThirdPartyNotices.txt'); Name = 'ThirdPartyNotices.txt' }
)) {
    if (-not (Test-Path -LiteralPath $taskDocument.Source)) { throw "Release document is missing: $($taskDocument.Source)" }
    Copy-Item -LiteralPath $taskDocument.Source -Destination (Join-Path $taskRelease $taskDocument.Name) -Force
}

& (Join-Path $PSScriptRoot 'create-shortcut.ps1') -ReleaseDirectory $taskRelease
Write-Host ''
Write-Host "Published application: $taskRelease\MemoryStudio.exe"
Write-Host 'Keep the release folder together. Its DLLs and runtime files are required.'
