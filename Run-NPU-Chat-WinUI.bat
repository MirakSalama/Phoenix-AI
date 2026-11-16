@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0scripts\chat_lfm25_x8.ps1" -WinUI
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
    echo Phoenix AI setup exited with code %RESULT%.
    pause
)
exit /b %RESULT%
