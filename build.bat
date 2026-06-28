@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title LANStrike Builder

set "SDK_DIR=%CD%\.dotnet-sdk"
set "DOTNET=%SDK_DIR%\dotnet.exe"
set "DOTNET_CLI_HOME=%CD%\.dotnet-home"
set "NUGET_PACKAGES=%CD%\.nuget"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1"
set "OUT=%~dp0..\..\outputs\LANStrike"

echo [1/3] Checking .NET SDK...
if exist "%DOTNET%" goto :sdk_ready

set "GLOBAL_DOTNET=C:\Program Files\dotnet\dotnet.exe"
if exist "%GLOBAL_DOTNET%" (
  "%GLOBAL_DOTNET%" --list-sdks 2^>nul | findstr /r "^[89][.]" >nul
  if not errorlevel 1 (
    set "DOTNET=%GLOBAL_DOTNET%"
    goto :sdk_ready
  )
)

echo No .NET SDK found. Downloading the official .NET 8 SDK...
echo This is a one-time download and can take several minutes.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.\dotnet-install.ps1'; & '.\dotnet-install.ps1' -Channel '8.0' -Quality 'GA' -InstallDir '.\.dotnet-sdk' -NoPath"
if errorlevel 1 goto :install_failed
if not exist "%DOTNET%" goto :install_failed

:sdk_ready
echo [2/3] Building the game...
if exist "%OUT%" rmdir /s /q "%OUT%"
"%DOTNET%" publish "%CD%\LANStrike.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%OUT%"
if errorlevel 1 goto :build_failed
if not exist "%OUT%\LANStrike.exe" goto :build_failed

echo [3/3] Build complete.
echo EXE: %OUT%\LANStrike.exe
start "" "%OUT%\LANStrike.exe"
exit /b 0

:install_failed
echo.
echo ERROR: The .NET SDK download or installation failed.
echo Check your Internet connection, then run this file again.
pause
exit /b 1

:build_failed
echo.
echo ERROR: The game did not build. LANStrike.exe was not created.
pause
exit /b 1
