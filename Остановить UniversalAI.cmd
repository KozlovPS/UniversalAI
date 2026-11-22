@echo off
start "" "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -WindowStyle Hidden -File "%~dp0scripts\UniversalAI-Launcher.ps1" -Stop
exit /b
