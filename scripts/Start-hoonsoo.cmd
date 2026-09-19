@echo off
cd /d "%~dp0"
if not exist "%SystemRoot%\System32\vcruntime140.dll" (
  echo Local OCR requires Microsoft Visual C++ x64 Runtime.
  echo Install prerequisites\vc_redist.x64.exe, then start hoonsoo.exe.
  pause
  exit /b 1
)
start "" "%~dp0hoonsoo.exe"
