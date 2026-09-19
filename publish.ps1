# Build "Padra Platform Launcher.exe" plus the gateway into dist\.
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

function Invoke-DotnetPublish {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & dotnet publish @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

# The gateway: self-contained so the target machine needs no .NET runtime.
Invoke-DotnetPublish @("src\Padra.Web\Padra.Web.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true",
  "-p:PublishSingleFile=false", "-o", $dist
)

# The window: single file, same folder, starts the gateway next to it.
Invoke-DotnetPublish @("src\Padra.Desktop\Padra.Desktop.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true",
  "-o", $dist
)

Copy-Item -Force models.yaml (Join-Path $dist "models.yaml")

Write-Host ""
Write-Host "Built: $dist\Padra Platform Launcher.exe"
Write-Host "Headless: $dist\Padra.Server.exe --config models.yaml [--lan]"
