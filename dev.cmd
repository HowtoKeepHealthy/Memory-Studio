@echo off
set "PATH=%~dp0.tools\dotnet;%~dp0.tools\llvm-mingw\bin;%PATH%"
cd /d "%~dp0"
echo Memory Studio development environment
echo dotnet / clang++ / build.cmd / run.cmd are ready.
cmd /k
