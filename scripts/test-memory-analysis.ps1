param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [string]$NativeDllPath = '')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Local .NET SDK is missing. Run build.cmd first.' }
if ([string]::IsNullOrWhiteSpace($NativeDllPath)) { $NativeDllPath = Join-Path $repoRoot 'artifacts\native\memory_core.dll' }
elseif (-not [IO.Path]::IsPathRooted($NativeDllPath)) { $NativeDllPath = Join-Path $repoRoot $NativeDllPath }
$NativeDllPath = [IO.Path]::GetFullPath($NativeDllPath)
if (-not (Test-Path -LiteralPath $NativeDllPath -PathType Leaf)) { throw "Native DLL is missing: $NativeDllPath" }
$runName = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$runRoot = Join-Path $repoRoot (Join-Path 'artifacts\memory-analysis' $runName)
$appRoot = Join-Path $repoRoot 'app'
$appCopy = Join-Path $runRoot 'app'
$harnessCopy = Join-Path $runRoot 'harness'
New-Item -ItemType Directory -Path $appCopy, $harnessCopy -Force | Out-Null
Push-Location $repoRoot
try {
    $files = @(& rg --files app -g '!**/bin/**' -g '!**/obj/**')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate application sources.' }
    foreach ($relative in $files) {
        $source = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
        if (-not $source.StartsWith($appRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Source outside app: $source" }
        $target = Join-Path $appCopy $source.Substring($appRoot.Length + 1)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
    foreach ($name in @('Program.cs', 'MemoryAnalysis.csproj')) { Copy-Item -LiteralPath (Join-Path $repoRoot "tests\memory_analysis\$name") -Destination (Join-Path $harnessCopy $name) }
    & $dotnet run --project (Join-Path $harnessCopy 'MemoryAnalysis.csproj') --configuration $Configuration `
        "-p:RepositoryRoot=$repoRoot" "-p:AppSourcePath=$appCopy" "-p:NativeDllPath=$NativeDllPath" -- $runRoot
    if ($LASTEXITCODE -ne 0) { throw "Analysis regression failed. See $runRoot" }
    Write-Host "Analysis report and screenshots: $runRoot"
} finally { Pop-Location }
