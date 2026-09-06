@echo off
chcp 65001 >nul
echo ============================================
echo   ALH Pro - Upload Ads (one click)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0上传广告.ps1"
if errorlevel 1 (
  echo.
  echo [Finished with errors. See above.]
)
pause