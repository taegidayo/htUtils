@echo off
rem Builds publish\viewer\HtUtil.Viewer.exe (keep this file ASCII + CRLF)
dotnet publish "%~dp0viewer\HtUtil.Viewer.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0publish\viewer"
if errorlevel 1 echo. & echo [FAILED] See the error message above.
pause
