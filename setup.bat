@echo off
rem Runs setup.ps1 (all logic and Korean messages live there; keep this file ASCII + CRLF)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
if errorlevel 1 echo. & echo [FAILED] See the error message above.
pause
