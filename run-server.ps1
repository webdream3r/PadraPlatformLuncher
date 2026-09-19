# Headless gateway (no window).
#   .\run-server.ps1            serve this computer only
#   .\run-server.ps1 --lan      serve every machine on the local network
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
dotnet run --project src\Padra.Web\Padra.Web.csproj -c Release -- --config models.yaml @args
