# Stops the Authorization Control Plane local stack AND removes its data volumes for a clean database.
# WARNING: this deletes the local PostgreSQL data. Run from the repository root: .\scripts\dev-reset.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot "deploy/local/.env"
$composeFile = Join-Path $repoRoot "deploy/local/docker-compose.yml"

if (-not (Test-Path $envFile)) {
    $envFile = Join-Path $repoRoot "deploy/local/.env.example"
}

Write-Host "Stopping local stack and removing volumes (database will be wiped)..."
docker compose --env-file $envFile -f $composeFile down -v
