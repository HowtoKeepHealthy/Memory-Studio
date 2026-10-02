param(
    [switch]$Test,
    [switch]$SkipNative
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskArtifacts = Join-Path $taskRoot 'artifacts'
$taskNative = Join-Path $taskArtifacts 'native'
$taskPortable = Join-Path $taskArtifacts 'portable'
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'

# Share pinned SDK/toolchain installation and native compilation with build.cmd.
# The normal folder publish remains available through build.cmd.
& (Join-Path $PSScriptRoot 'build.ps1') -SkipPublish -SkipNative:$SkipNative -Test:$Test

foreach ($taskAsset in @('memory_core.dll', 'DemoTarget.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskNative $taskAsset))) {
        throw "Required native asset is missing: $taskAsset"
    }
}

# The staged publish is unique, so no old DLLs can hide a missing bundle item.
$taskStagingBase = Join-Path $taskArtifacts 'package-staging'
$taskStage = Join-Path $taskStagingBase ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null

function Remove-TaskDirectory([string]$Directory, [string]$AllowedBase) {
    $taskAbsolute = [IO.Path]::GetFullPath($Directory)
    $taskAllowed = [IO.Path]::GetFullPath($AllowedBase).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $taskAbsolute.StartsWith($taskAllowed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the expected artifact subtree: $taskAbsolute"
    }
    if (Test-Path -LiteralPath $taskAbsolute) {
        Remove-Item -LiteralPath $taskAbsolute -Recurse -Force
    }
}

$taskIsolated = $null
$taskTestBase = Join-Path $taskArtifacts 'package-tests'
try {
    # Native runtime and memory_core.dll self-extract. DemoTarget is a managed
    # embedded resource resolved by BundledAssets, not a loose content file.
    # IncludeAllContentForSelfExtract is deliberately disabled: Microsoft calls
    # that legacy .NET Core 3.1 compatibility mode and recommends embedded assets.
    # https://learn.microsoft.com/dotnet/core/deploying/single-file/overview
    $taskPublishArgs = @(
        'publish', (Join-Path $taskRoot 'app\MemoryStudio.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-o', $taskStage,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:IncludeAllContentForSelfExtract=false',
        '-p:EnableCompressionInSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=embedded'
    )
    & $taskDotnet @taskPublishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Single-file WPF publish failed.' }
    $taskPublishedExe = Join-Path $taskStage 'MemoryStudio.exe'
    if (-not (Test-Path -LiteralPath $taskPublishedExe)) { throw 'Single-file executable was not generated.' }

    $taskLooseFiles = @(Get-ChildItem -LiteralPath $taskStage -Recurse -File | Where-Object {
        $_.FullName -ne $taskPublishedExe -and $_.Extension -ne '.pdb'
    })
    if ($taskLooseFiles.Count -gt 0) {
        throw ('Publish left unbundled files; a portable EXE must include them: ' + ($taskLooseFiles.Name -join ', '))
    }

    if ($Test) {
        $taskIsolated = Join-Path $taskTestBase ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $taskIsolated -Force | Out-Null
        $taskIsolatedExe = Join-Path $taskIsolated 'MemoryStudio.exe'
        Copy-Item -LiteralPath $taskPublishedExe -Destination $taskIsolatedExe
        $taskReport = Join-Path $taskArtifacts 'portable-self-test.txt'
        if (Test-Path -LiteralPath $taskReport) { Remove-Item -LiteralPath $taskReport -Force }

        # Force a fresh bundle extraction cache, then run only the copied EXE.
        # This detects dependencies accidentally satisfied by a folder publish.
        $taskSavedExtract = [Environment]::GetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'Process')
        $taskExtract = Join-Path $taskIsolated 'bundle-cache'
        try {
            [Environment]::SetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', $taskExtract, 'Process')
            $taskProcess = Start-Process -FilePath $taskIsolatedExe -WorkingDirectory $taskIsolated -ArgumentList @('--self-test', ('"' + $taskReport + '"')) -PassThru -WindowStyle Hidden
            if (-not $taskProcess.WaitForExit(60000)) {
                Stop-Process -Id $taskProcess.Id -Force
                throw 'Portable self-test exceeded its 60-second timeout.'
            }
            if (Test-Path -LiteralPath $taskReport) { Get-Content -LiteralPath $taskReport }
            if ($taskProcess.ExitCode -ne 0) { throw "Portable self-test failed with exit code $($taskProcess.ExitCode)." }
            if (-not (Test-Path -LiteralPath $taskReport)) { throw 'Portable self-test did not generate its report.' }
        }
        finally {
            [Environment]::SetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', $taskSavedExtract, 'Process')
        }
    }

    New-Item -ItemType Directory -Path $taskPortable -Force | Out-Null
    $taskFinalExe = Join-Path $taskPortable 'MemoryStudio.exe'
    Copy-Item -LiteralPath $taskPublishedExe -Destination $taskFinalExe -Force
    $taskSizeMiB = [Math]::Round((Get-Item -LiteralPath $taskFinalExe).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Portable single EXE: $taskFinalExe ($taskSizeMiB MiB)"
    Write-Host 'Windows x64; .NET runtime, native memory core and demo target are included.'
    if ($Test) { Write-Host "Isolated self-test passed. Report: $taskReport" }
}
finally {
    Remove-TaskDirectory $taskStage $taskStagingBase
    if ($taskIsolated) { Remove-TaskDirectory $taskIsolated $taskTestBase }
}
