@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package.ps1" %*
set "MemoryStudioPackageExit=%errorlevel%"
if not "%MemoryStudioPackageExit%"=="0" pause
exit /b %MemoryStudioPackageExit%
