@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Get-Model.ps1" %*
set "result=%ERRORLEVEL%"
echo.
pause
exit /b %result%
