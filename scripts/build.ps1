param([switch]$Test, [switch]$SkipNative, [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) {
    $taskTools = Join-Path $taskRoot '.tools'
    New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
    $taskInstaller = Join-Path $taskTools 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $taskInstaller -UseBasicParsing
    & $taskInstaller -Version '10.0.401' -InstallDir (Join-Path $taskTools 'dotnet') -NoPath
    if (-not (Test-Path -LiteralPath $taskDotnet)) { throw '.NET SDK installation failed.' }
}
if (-not $SkipNative) { & (Join-Path $PSScriptRoot 'build-native.ps1') }
& (Join-Path $PSScriptRoot 'install-assembler.ps1')
$taskCompiler = Join-Path $taskRoot '.tools\llvm-mingw\bin\clang++.exe'
$taskNative = Join-Path $taskRoot 'artifacts\native'
New-Item -ItemType Directory -Path $taskNative -Force | Out-Null
foreach ($taskName in @('DemoTarget', 'native_integration', 'native_scan_extensions', 'native_unknown_scan')) {
    $taskSource = Join-Path $taskRoot ("tests\" + $taskName + '.cpp')
    $taskFlags = @('-std=c++20', '-O2', '-static')
    if ($taskName -ne 'DemoTarget') { $taskFlags += '-municode' }
    & $taskCompiler @taskFlags $taskSource -o (Join-Path $taskNative ($taskName + '.exe'))
    if ($LASTEXITCODE -ne 0) { throw "$taskName build failed." }
}
$taskRelease = Join-Path $taskRoot 'artifacts\release'
if (-not $SkipPublish) {
    & $taskDotnet publish (Join-Path $taskRoot 'app\MemoryStudio.csproj') -c Release -r win-x64 --self-contained true -o $taskRelease -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed.' }
}
if ($Test) {
    Push-Location $taskNative
    try { & (Join-Path $taskNative 'native_integration.exe') (Join-Path $taskNative 'memory_core.dll'); if ($LASTEXITCODE -ne 0) { throw 'Native integration tests failed.' } }
    finally { Pop-Location }
    & (Join-Path $taskNative 'native_scan_extensions.exe') (Join-Path $taskNative 'memory_core.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Native scan extension tests failed.' }
    & (Join-Path $taskNative 'native_unknown_scan.exe') (Join-Path $taskNative 'memory_core.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Unknown snapshot regression tests failed.' }
    if (-not $SkipPublish) {
        $taskReport = Join-Path $taskRoot 'artifacts\managed-test.txt'
        $taskApp = Start-Process -FilePath (Join-Path $taskRelease 'MemoryStudio.exe') -ArgumentList @('--self-test', ('"' + $taskReport + '"')) -Wait -PassThru -WindowStyle Hidden
        if ($taskApp.ExitCode -ne 0) { if (Test-Path -LiteralPath $taskReport) { Get-Content -LiteralPath $taskReport }; throw 'Managed integration tests failed.' }
        Get-Content -LiteralPath $taskReport
    }
}
if ($SkipPublish) { Write-Host "Built native assets: $taskNative" }
else { Write-Host "Built: $taskRelease\MemoryStudio.exe" }
