param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$compiler64 = Join-Path $projectRoot '.tools\llvm-mingw\bin\x86_64-w64-mingw32-clang++.exe'
$compiler32 = Join-Path $projectRoot '.tools\llvm-mingw\bin\i686-w64-mingw32-clang++.exe'
foreach ($tool in @($dotnet, $compiler64, $compiler32)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Project-local tool missing: $tool. Run build.cmd first." }
}
$sdkVersion = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.401') { throw "Expected portable .NET SDK 10.0.401, found $sdkVersion." }
$artifactRoot = Join-Path $projectRoot 'artifacts\process-tools'
$nativeRoot = Join-Path $artifactRoot 'native'
$pauseRoot = Join-Path $artifactRoot 'pause'
$pointerRoot = Join-Path $artifactRoot 'pointers'
$pauseObj = (Join-Path $artifactRoot 'obj-pause') + [IO.Path]::DirectorySeparatorChar
$pointerObj = (Join-Path $artifactRoot 'obj-pointers') + [IO.Path]::DirectorySeparatorChar
foreach ($directory in @($nativeRoot, $pauseRoot, $pointerRoot, $pauseObj, $pointerObj)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

& (Join-Path $PSScriptRoot 'build-native.ps1') -OutputDirectory $nativeRoot
$library = Join-Path $nativeRoot 'memory_core.dll'
$demo64 = Join-Path $nativeRoot 'DemoTarget64.exe'
$demo32 = Join-Path $nativeRoot 'DemoTarget32.exe'
$pointer64 = Join-Path $nativeRoot 'PointerTarget64.exe'
$pointer32 = Join-Path $nativeRoot 'PointerTarget32.exe'
$compileFlags = @('-std=c++20', '-O2', '-Wall', '-Wextra', '-Wpedantic', '-static', '-static-libgcc', '-static-libstdc++')
foreach ($fixture in @(
    @{ Compiler = $compiler64; Source = 'tests\DemoTarget.cpp'; Output = $demo64 },
    @{ Compiler = $compiler32; Source = 'tests\DemoTarget.cpp'; Output = $demo32 },
    @{ Compiler = $compiler64; Source = 'tests\native_pointer_target.cpp'; Output = $pointer64 },
    @{ Compiler = $compiler32; Source = 'tests\native_pointer_target.cpp'; Output = $pointer32 }
)) {
    & $fixture.Compiler @compileFlags (Join-Path $projectRoot $fixture.Source) '-o' $fixture.Output
    if ($LASTEXITCODE -ne 0) { throw "Fixture compiler failed: $($fixture.Output)." }
}

foreach ($suite in @(
    @{ Project = 'ProcessControlTests'; Output = $pauseRoot; Obj = $pauseObj; Fixture64 = $demo64; Fixture32 = $demo32; Log = 'process-control-test.txt' },
    @{ Project = 'PointerTests'; Output = $pointerRoot; Obj = $pointerObj; Fixture64 = $pointer64; Fixture32 = $pointer32; Log = 'pointer-test.txt' }
)) {
    $project = Join-Path $projectRoot "tests\process_control\$($suite.Project).csproj"
    & $dotnet build $project '-c' 'Release' '-o' $suite.Output "-p:BaseIntermediateOutputPath=$($suite.Obj)" "-p:MSBuildProjectExtensionsPath=$($suite.Obj)" '--nologo'
    if ($LASTEXITCODE -ne 0) { throw "Managed test build failed: $($suite.Project)." }
    $assembly = Join-Path $suite.Output "$($suite.Project).dll"
    & $dotnet $assembly $library $suite.Fixture64 $suite.Fixture32 | Tee-Object -FilePath (Join-Path $artifactRoot $suite.Log)
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $($suite.Project). See artifacts/process-tools/$($suite.Log)." }
}
Write-Host 'Process tools integration passed: 32 process control checks and 24 pointer checks.'
