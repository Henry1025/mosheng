@echo off
setlocal
set "APP=%~dp0MoshengNativeApp\dist\Mosheng.exe"
if not exist "%APP%" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0MoshengNativeApp\build.ps1"
)
start "" "%APP%"
endlocal
