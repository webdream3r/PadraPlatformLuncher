@echo off
rem Padra Platform Launcher - Windows launcher. Double-click this file.
setlocal
cd /d "%~dp0"

set "PYW="
set "PY="
where pythonw.exe >nul 2>&1 && set "PYW=pythonw.exe"
where py.exe >nul 2>&1 && set "PY=py.exe -3"
if not defined PY where python.exe >nul 2>&1 && set "PY=python.exe"

if not defined PY (
  echo Python 3.10 or newer was not found on PATH.
  echo Install it with:  winget install --id Python.Python.3.12
  pause
  exit /b 1
)

if defined PYW (
  start "" %PYW% "%~dp0app.pyw"
) else (
  start "" %PY% "%~dp0app.pyw"
)
exit /b 0
