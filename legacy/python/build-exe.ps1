# Build a standalone "Padra Platform Launcher.exe" (no Python on the target machine).
#   powershell -ExecutionPolicy Bypass -File .\build-exe.ps1
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

if (-not (Test-Path ".venv")) { python -m venv .venv }
& ".venv\Scripts\pip.exe" install --upgrade pip -q
& ".venv\Scripts\pip.exe" install -q -r requirements.txt
& ".venv\Scripts\pip.exe" install -q pyinstaller

& ".venv\Scripts\pyinstaller.exe" --noconfirm --clean --windowed --onefile `
  --name "Padra Platform Launcher" `
  --paths . `
  --hidden-import launcher.gateway `
  --hidden-import launcher.webui `
  --hidden-import launcher.gen_configs `
  --hidden-import launcher.discovery `
  --hidden-import launcher.netinfo `
  --hidden-import launcher.fleetfile `
  --hidden-import launcher.settings `
  --collect-all uvicorn `
  --collect-all fastapi `
  --collect-all httpx `
  --collect-all webview `
  --collect-submodules yaml `
  --add-data "launcher/static;launcher/static" `
  --add-data "launcher/locales;launcher/locales" `
  --add-data "models.yaml;." `
  app.pyw

Copy-Item -Force "models.yaml" "dist\models.yaml"
Write-Host ""
Write-Host "Built: dist\Padra Platform Launcher.exe"
