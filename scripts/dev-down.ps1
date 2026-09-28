# Stops the Authorization Control Plane local stack (containers remain removable; data volumes kept).
# Run from the repository root: .\scripts\dev-down.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot "deploy/local/.env"
$composeFile = Join-Path $repoRoot "deploy/local/docker-compose.yml"

if (-not (Test-Path $envFile)) {
    $envFile = Join-Path $repoRoot "deploy/local/.env.example"
}

Write-Host "Stopping local stack..."
docker compose --env-file $envFile -f $composeFile down
