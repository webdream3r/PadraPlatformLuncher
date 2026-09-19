# Headless gateway (no control panel window).
#   .\start.ps1              serve this computer only
#   .\start.ps1 --lan        serve every machine on the local network
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
if (-not (Test-Path ".venv")) {
  python -m venv .venv
  & ".venv\Scripts\pip.exe" install --upgrade pip -q
  & ".venv\Scripts\pip.exe" install -q -r requirements.txt
}
& ".venv\Scripts\python.exe" -m launcher --config models.yaml @args
