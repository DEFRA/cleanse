$ErrorActionPreference = 'Stop'

Set-Location $PSScriptRoot

Write-Host 'Starting Cleanse backend at http://localhost:8085'
dotnet run --project (Join-Path $PSScriptRoot 'Cleanse\Cleanse.csproj') --launch-profile Cleanse
