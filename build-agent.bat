@echo off
rem Runs build-agent.ps1 (all logic and Korean messages live there; keep this file ASCII + CRLF)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-agent.ps1"
if errorlevel 1 echo. & echo [FAILED] See the error message above.
pause
