@echo off
rem HtUtil Agent version check (read only). Right-click - Run as administrator to see the service process too.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-agent.ps1"
pause
