# Starts the Authorization Control Plane local stack (Postgres, Keycloak, API, Portal).
# Copies deploy/local/.env from the template if it does not exist, then runs docker compose up --build.
# Run from the repository root: .\scripts\dev-up.ps1
[CmdletBinding()]
param(
    [switch]$Detached
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot "deploy/local/.env"
$envTemplate = Join-Path $repoRoot "deploy/local/.env.example"
$composeFile = Join-Path $repoRoot "deploy/local/docker-compose.yml"

if (-not (Test-Path $envFile)) {
    Write-Host "Creating deploy/local/.env from template..."
    Copy-Item $envTemplate $envFile -Force
}

$composeArgs = @("compose", "--env-file", $envFile, "-f", $composeFile, "up", "--build")
if ($Detached) {
    $composeArgs += "-d"
}

Write-Host "Starting local stack..."
docker @composeArgs
