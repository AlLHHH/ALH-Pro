@echo off
echo ============================================
echo   ALH Pro - Upload Ads (one click)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload-ads.ps1"
if errorlevel 1 (
  echo.
  echo [Finished with errors. See above.]
)
pause