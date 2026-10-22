@echo off
setlocal
set "ARCH=%PROCESSOR_ARCHITECTURE%"
if defined PROCESSOR_ARCHITEW6432 set "ARCH=%PROCESSOR_ARCHITEW6432%"
set "RID="
if /i "%ARCH%"=="AMD64" set "RID=win-x64"
if /i "%ARCH%"=="ARM64" set "RID=win-arm64"
if not defined RID (
    echo Unsupported Windows architecture. This package supports x64 and ARM64. 1>&2
    exit /b 1
)
"%~dp0%RID%\Talewright.Host.Desktop.exe" %*
exit /b %errorlevel%
