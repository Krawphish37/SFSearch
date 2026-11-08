@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0sfs.ps1" %*
exit /b %ERRORLEVEL%
