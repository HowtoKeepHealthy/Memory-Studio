param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$NativeDllPath = '',
    [switch]$HistoryOverlapOnly,
    [switch]$ScrollOnly
)
$ErrorActionPreference = 'Stop'
if ($HistoryOverlapOnly -and $ScrollOnly) { throw '请选择一个独立专项。' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw '未找到本地 .NET SDK。请先运行 build.cmd。' }
if ([string]::IsNullOrWhiteSpace($NativeDllPath)) { $NativeDllPath = Join-Path $repoRoot 'artifacts\native\memory_core.dll' }
elseif (-not [IO.Path]::IsPathRooted($NativeDllPath)) { $NativeDllPath = Join-Path $repoRoot $NativeDllPath }
$NativeDllPath = [IO.Path]::GetFullPath($NativeDllPath)
foreach ($required in @($NativeDllPath, (Join-Path $repoRoot 'artifacts\native\DemoTarget.exe'), (Join-Path $repoRoot 'artifacts\native\nasm.exe'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "缺少测试依赖：$required。请先运行 build.cmd。" }
}

# Each run gets its own sources, obj and bin; production project outputs are never modified.
$runName = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$runRoot = Join-Path $repoRoot (Join-Path 'artifacts\browser-windows' $runName)
$appCopy = Join-Path $runRoot 'app'
$appRoot = Join-Path $repoRoot 'app'
$harnessCopy = Join-Path $runRoot 'harness'
New-Item -ItemType Directory -Path $appCopy, $harnessCopy -Force | Out-Null
Push-Location $repoRoot
try {
    if (Get-Command rg -ErrorAction SilentlyContinue) {
        $files = @(& rg --files app -g '!**/bin/**' -g '!**/obj/**')
        if ($LASTEXITCODE -ne 0) { throw '无法列出 app 源文件。' }
    } else {
        $files = @(Get-ChildItem -LiteralPath $appRoot -File -Recurse | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        } | ForEach-Object { $_.FullName.Substring($repoRoot.Length + 1) })
    }
    foreach ($relative in $files) {
        $source = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
        if (-not $source.StartsWith($appRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "源文件不在 app 目录：$source" }
        $target = Join-Path $appCopy $source.Substring($appRoot.Length + 1)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
    foreach ($name in @('Program.cs', 'BrowserWindows.csproj')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "tests\browser_windows\$name") -Destination (Join-Path $harnessCopy $name)
    }
    $testArguments = @($repoRoot, $runRoot)
    if ($HistoryOverlapOnly) { $testArguments += '--history-overlap-only'; Write-Host '只运行内存撤销重叠/部分失败专项；不会打开窗口。' }
    elseif ($ScrollOnly) { $testArguments += '--scroll-only'; Write-Host '只运行真实原生滚轮专项；短暂显示主窗和浏览器，结束后恢复鼠标位置。' }
    else { Write-Host '运行独立 WPF 真窗口集成：将短暂显示浏览器和主窗口。' }
    & $dotnet run --project (Join-Path $harnessCopy 'BrowserWindows.csproj') --configuration $Configuration `
        "-p:RepositoryRoot=$repoRoot" "-p:AppSourcePath=$appCopy" "-p:NativeDllPath=$NativeDllPath" -- @testArguments
    $testCode = $LASTEXITCODE
    Write-Host "报告：$(Join-Path $runRoot 'browser-window-results.txt')"
    if (-not $HistoryOverlapOnly) { Write-Host "截图目录：$runRoot" }
    if ($testCode -ne 0) { throw "浏览窗口测试未通过（退出码 $testCode）。" }
    Write-Host '浏览窗口测试全部通过。'
} finally { Pop-Location }
