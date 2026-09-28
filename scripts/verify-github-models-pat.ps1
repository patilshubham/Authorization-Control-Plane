# Verify a GitHub personal access token (PAT) works as the AI provider for this app.
#
# The AI features (F1 Policy Authoring Copilot, F2 Decision Explainer) call a provider-neutral
# IAiAssistant. When the provider is GitHub Models, authentication is a GitHub PAT with the
# account-level "Models: Read-only" permission (and, for enterprise/SSO orgs, the PAT must be
# authorized for the org via "Configure SSO").
#
# This script runs three escalating checks and reports pass/fail for each:
#   1. Identity      - the token is valid and (for SSO orgs) authorized.
#   2. Models access - the token can read the GitHub Models catalog (Models permission granted).
#   3. Inference     - a real chat completion succeeds; this is exactly what F1/F2 do at runtime.
#
# The token is never printed. Provide it via the GH_MODELS_TOKEN environment variable (preferred)
# or the -Token parameter. Set it directly in your terminal so it is not captured elsewhere:
#   $env:GH_MODELS_TOKEN = 'your-PAT'
#   ./scripts/verify-github-models-pat.ps1
param(
    [string]$Token = $env:GH_MODELS_TOKEN,
    [string]$ApiBaseUrl = "https://api.github.com",
    [string]$ModelsBaseUrl = "https://models.github.ai",
    [string]$Model = "openai/gpt-4.1-mini"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Host "No token provided. Set it in your terminal first, then re-run:" -ForegroundColor Yellow
    Write-Host "  `$env:GH_MODELS_TOKEN = 'your-PAT'" -ForegroundColor Yellow
    exit 2
}

$headers = @{
    Authorization          = "Bearer $Token"
    "X-GitHub-Api-Version" = "2022-11-28"
}

$failures = 0

function Get-HttpStatus {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    $response = $ErrorRecord.Exception.Response
    if ($null -ne $response -and $null -ne $response.StatusCode) {
        return [int]$response.StatusCode
    }
    return $null
}

# 1. Identity ----------------------------------------------------------------------------------
Write-Host "[1/3] Checking token identity ..." -ForegroundColor Cyan
try {
    $user = Invoke-RestMethod -Uri "$ApiBaseUrl/user" -Headers $headers
    Write-Host "  PASS  Authenticated as '$($user.login)'." -ForegroundColor Green
}
catch {
    $status = Get-HttpStatus $_
    switch ($status) {
        401 { Write-Host "  FAIL  401 Unauthorized - the token is invalid or expired." -ForegroundColor Red }
        403 { Write-Host "  FAIL  403 Forbidden - likely not authorized for the org. Use 'Configure SSO' on the PAT." -ForegroundColor Red }
        default { Write-Host "  FAIL  Could not authenticate (HTTP $status): $($_.Exception.Message)" -ForegroundColor Red }
    }
    $failures++
}

# 2. Models catalog access ---------------------------------------------------------------------
Write-Host "[2/3] Checking GitHub Models access (catalog) ..." -ForegroundColor Cyan
try {
    $catalog = Invoke-RestMethod -Uri "$ModelsBaseUrl/catalog/models" -Headers $headers
    $count = @($catalog).Count
    Write-Host "  PASS  Models permission granted ($count models visible)." -ForegroundColor Green
}
catch {
    $status = Get-HttpStatus $_
    switch ($status) {
        403 { Write-Host "  FAIL  403 Forbidden - PAT is missing 'Models: Read-only', or the org has not enabled GitHub Models." -ForegroundColor Red }
        default { Write-Host "  FAIL  Could not read the models catalog (HTTP $status): $($_.Exception.Message)" -ForegroundColor Red }
    }
    $failures++
}

# 3. Inference (the real F1/F2 call) -----------------------------------------------------------
Write-Host "[3/3] Running a chat completion with model '$Model' ..." -ForegroundColor Cyan
try {
    $body = @{
        model    = $Model
        messages = @(
            @{ role = "user"; content = "Reply with exactly: PAT OK" }
        )
    } | ConvertTo-Json -Depth 5

    $completion = Invoke-RestMethod -Method Post -Uri "$ModelsBaseUrl/inference/chat/completions" `
        -Headers ($headers + @{ "Content-Type" = "application/json" }) `
        -Body $body

    $reply = $completion.choices[0].message.content
    Write-Host "  PASS  Inference succeeded. Model replied: '$reply'." -ForegroundColor Green
}
catch {
    $status = Get-HttpStatus $_
    switch ($status) {
        404 { Write-Host "  FAIL  404 - model '$Model' not found. Pass a valid id from the catalog via -Model." -ForegroundColor Red }
        429 { Write-Host "  WARN  429 - rate/quota limited. The token itself is valid." -ForegroundColor Yellow }
        default { Write-Host "  FAIL  Inference failed (HTTP $status): $($_.Exception.Message)" -ForegroundColor Red }
    }
    if ($status -ne 429) { $failures++ }
}

Write-Host ""
if ($failures -eq 0) {
    Write-Host "All checks passed - the PAT works for our GitHub Models AI use case." -ForegroundColor Green
    exit 0
}

Write-Host "$failures check(s) failed - see messages above." -ForegroundColor Red
exit 1
