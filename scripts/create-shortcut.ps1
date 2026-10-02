param([string]$ReleaseDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $taskRoot 'artifacts\release'
}
elseif (-not [IO.Path]::IsPathRooted($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $taskRoot $ReleaseDirectory
}
$taskRelease = [IO.Path]::GetFullPath($ReleaseDirectory)
$taskExecutable = Join-Path $taskRelease 'MemoryStudio.exe'
if (-not (Test-Path -LiteralPath $taskExecutable -PathType Leaf)) {
    throw "Build the application before creating its shortcut: $taskExecutable"
}
$taskDesktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
if ([string]::IsNullOrWhiteSpace($taskDesktop) -or -not (Test-Path -LiteralPath $taskDesktop -PathType Container)) {
    throw 'The current user desktop directory is unavailable.'
}
$taskLink = Join-Path $taskDesktop 'Memory Studio.lnk'
$taskShell = $null
$taskShortcut = $null
try {
    $taskShell = New-Object -ComObject WScript.Shell
    $taskShortcut = $taskShell.CreateShortcut($taskLink)
    $taskShortcut.TargetPath = $taskExecutable
    $taskShortcut.WorkingDirectory = $taskRelease
    $taskShortcut.Arguments = ''
    $taskShortcut.IconLocation = $taskExecutable + ',0'
    $taskShortcut.Description = 'Memory Studio - Windows memory inspection and scanning'
    $taskShortcut.WindowStyle = 1
    $taskShortcut.Save()
}
finally {
    foreach ($taskComObject in @($taskShortcut, $taskShell)) {
        if ($null -ne $taskComObject -and [Runtime.InteropServices.Marshal]::IsComObject($taskComObject)) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskComObject) | Out-Null
        }
    }
}
Write-Host "Desktop shortcut: $taskLink"
Write-Host "Target: $taskExecutable"
