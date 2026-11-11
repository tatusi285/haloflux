@echo off
rem Keeps the wallpaper alive: started every few minutes by Task Scheduler.
rem If the process is missing (crash, Explorer restart, manual kill) it is relaunched.

set "EXE=%~dp0..\bin\Release\net9.0-windows\HalofluxDesk.exe"

tasklist /FI "IMAGENAME eq HalofluxDesk.exe" 2>nul | find /I "HalofluxDesk.exe" >nul
if not errorlevel 1 exit /b 0

if not exist "%EXE%" exit /b 0

start "" "%EXE%" --wall
