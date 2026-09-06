@echo off
echo ============================================
echo   ALH Pro - Upload All (Ads + Tips)
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload-all.ps1"
if errorlevel 1 (
  echo.
  echo [Finished with errors. See above.]
)
pause