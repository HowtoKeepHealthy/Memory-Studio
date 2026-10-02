@echo off
if not exist "%~dp0artifacts\release\MemoryStudio.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
  if errorlevel 1 exit /b 1
)
start "" "%~dp0artifacts\release\MemoryStudio.exe"
