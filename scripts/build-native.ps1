param(
    [string]$OutputDirectory = '',
    [switch]$DebugBuild
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $projectRoot '.tools\llvm-mingw'
$compiler = Join-Path $toolRoot 'bin\x86_64-w64-mingw32-clang++.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $tag = '20260922'
    $assetName = "llvm-mingw-$tag-ucrt-x86_64.zip"
    $downloadUrl = "https://github.com/mstorsjo/llvm-mingw/releases/download/$tag/$assetName"
    $expectedSha256 = 'e3ad77d117a4bea19a7a3b333341824d79a5a371004a10e25b8504e7b3047666'
    $archivePath = Join-Path $projectRoot ".tools\$assetName"
    $extractRoot = Join-Path $projectRoot '.tools\llvm-mingw-extract'
    New-Item -ItemType Directory -Path (Split-Path -Parent $archivePath) -Force | Out-Null
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host "Downloading official llvm-mingw $tag (Windows x64 UCRT)..."
        Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
    }
    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) { throw 'The official toolchain archive SHA-256 does not match. Archive retained for diagnosis.' }
    Write-Host "Verified SHA-256: $actualSha256"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
    $extractedTool = Join-Path $extractRoot "llvm-mingw-$tag-ucrt-x86_64"
    if (-not (Test-Path -LiteralPath (Join-Path $extractedTool 'bin\x86_64-w64-mingw32-clang++.exe'))) { throw 'Expected compiler not present in official archive.' }
    if (Test-Path -LiteralPath $toolRoot) { throw 'Incomplete .tools\llvm-mingw directory exists; refusing to replace it.' }
    $allowedToolRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.tools')) + [IO.Path]::DirectorySeparatorChar
    foreach ($movePath in @($extractedTool, $toolRoot)) {
        if (-not [IO.Path]::GetFullPath($movePath).StartsWith($allowedToolRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Toolchain move resolved outside this project .tools directory.'
        }
    }
    $checkedWorkspace = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
    foreach ($checkedTarget in @($extractedTool, $toolRoot)) {
        $checkedAbsolute = [System.IO.Path]::GetFullPath($checkedTarget)
        if (-not $checkedAbsolute.StartsWith($checkedWorkspace, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Toolchain move target is outside the project workspace.' }
    }
    Move-Item -LiteralPath $extractedTool -Destination $toolRoot
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'artifacts\native' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$libraryPath = Join-Path $OutputDirectory 'memory_core.dll'
$importPath = Join-Path $OutputDirectory 'libmemory_core.dll.a'
$optimization = if ($DebugBuild) { '-O0' } else { '-O2' }
& $compiler '-std=c++20' $optimization '-g' '-Wall' '-Wextra' '-Wpedantic' '-shared' '-static' '-static-libgcc' '-static-libstdc++' '-Wl,--dynamicbase,--nxcompat' "-Wl,--out-implib,$importPath" (Join-Path $projectRoot 'native\memory_core.cpp') (Join-Path $projectRoot 'native\trace_core.cpp') '-o' $libraryPath
if ($LASTEXITCODE -ne 0) { throw "Native compiler failed with exit code $LASTEXITCODE." }
Write-Host "Built $libraryPath"
