@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Launch
if errorlevel 1 (
    pause
    exit /b 1
)
