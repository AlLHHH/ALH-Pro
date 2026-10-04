@echo off
setlocal
set "PS1="
for %%f in ("%~dp0*.ps1") do set "PS1=%%~ff"
if not defined PS1 ( echo [x] ALHPro diagnose script not found. & pause & exit /b 1 )
echo Collecting ALH Pro startup diagnostics ... please wait.
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
echo.
pause
