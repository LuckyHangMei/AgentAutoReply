@echo off
cd /d "%~dp0"
if not exist "%~dp0artifacts\CodexAutoYes.exe" (
  dotnet publish "%~dp0CodexAutoYes.csproj" -c Release --no-self-contained -o "%~dp0artifacts"
  if errorlevel 1 (
    echo Build failed. .NET 10 SDK is required.
    pause
    exit /b 1
  )
)
start "" "%~dp0artifacts\CodexAutoYes.exe"
