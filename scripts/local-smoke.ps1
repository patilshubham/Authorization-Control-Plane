# Runtime authorization smoke test.
#
# Exercises both runtime caller flows the API supports:
#   1. Machine-to-machine (client-credentials): the subject is the calling service, derived from the
#      token's client-id claim (azp). The pricing-management runtime service account holds the
#      pricing-lead role (price.publish when READY_TO_PUBLISH).
#   2. User (interactive / direct-access grant): the subject is the end user, derived from the token's
#      preferred_username claim. user7.lead@icis.com holds pricing-lead (price.publish when READY).
#
# The subject is always taken from the verified token, never from request input; a conflicting body
# subject is rejected with 403 SUBJECT_MISMATCH.
param(
    [string]$ApiBaseUrl = "http://localhost:8080",
    [string]$PortalBaseUrl = "http://localhost:5173",
    [string]$TokenUrl = "http://localhost:8081/realms/authorization-local/protocol/openid-connect/token",
    [string]$ApplicationId = "pricing-management",
    [string]$WrongApplicationId = "market-reference",
    [string]$RuntimeClientId = "pricing-management-runtime-client",
    [string]$RuntimeClientSecret = "pricing_management_dev_secret",
    [string]$UserClientId = "pricing-management-user-client",
    [string]$UserClientSecret = "pricing_management_user_dev_secret",
    [string]$UserName = "user7.lead@icis.com",
    [string]$UserPassword = "pricing_lead_dev_password"
)

$ErrorActionPreference = "Stop"

function Get-ClientCredentialsToken {
    param([string]$Url, [string]$ClientId, [string]$ClientSecret)

    $response = Invoke-RestMethod -Method Post -Uri $Url -ContentType "application/x-www-form-urlencoded" -Body @{
        grant_type    = "client_credentials"
        client_id     = $ClientId
        client_secret = $ClientSecret
    }

    return $response.access_token
}

function Get-UserToken {
    param([string]$Url, [string]$ClientId, [string]$ClientSecret, [string]$Username, [string]$Password)

    $response = Invoke-RestMethod -Method Post -Uri $Url -ContentType "application/x-www-form-urlencoded" -Body @{
        grant_type    = "password"
        client_id     = $ClientId
        client_secret = $ClientSecret
        username      = $Username
        password      = $Password
        scope         = "openid"
    }

    return $response.access_token
}

function Invoke-Authorize {
    param([string]$AccessToken, [object]$Body)

    $headers = @{
        "Authorization"    = "Bearer $AccessToken"
        "X-Correlation-ID" = "local-smoke-$([Guid]::NewGuid().ToString('N'))"
    }
    $json = $Body | ConvertTo-Json -Depth 12
    return Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/v1/authorize" -Headers $headers -Body $json -ContentType "application/json"
}

function Invoke-AuthorizeExpectingStatus {
    param(
        [string]$AccessToken,
        [object]$Body,
        [int]$ExpectedStatus,
        [string]$ExpectedErrorCode,
        [string]$Path = "/v1/authorize"
    )

    $headers = @{ "X-Correlation-ID" = "local-smoke-$([Guid]::NewGuid().ToString('N'))" }
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $headers["Authorization"] = "Bearer $AccessToken"
    }
    $json = $Body | ConvertTo-Json -Depth 12
    try {
        Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl$Path" -Headers $headers -Body $json -ContentType "application/json" | Out-Null
        throw "Expected HTTP $ExpectedStatus but the request succeeded."
    }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        $actual = [int]$_.Exception.Response.StatusCode
        if ($actual -ne $ExpectedStatus) {
            throw "Expected HTTP $ExpectedStatus but received $actual."
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedErrorCode)) {
            $actualCode = $null
            if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
                try { $actualCode = ($_.ErrorDetails.Message | ConvertFrom-Json).error.code } catch { $actualCode = $null }
            }
            if ($actualCode -ne $ExpectedErrorCode) {
                throw "Expected error code '$ExpectedErrorCode' but received '$actualCode' (HTTP $actual)."
            }
        }
    }
}

function Invoke-AuthorizeBatch {
    param([string]$AccessToken, [object]$Body)

    $headers = @{
        "Authorization"    = "Bearer $AccessToken"
        "X-Correlation-ID" = "local-smoke-$([Guid]::NewGuid().ToString('N'))"
    }
    $json = $Body | ConvertTo-Json -Depth 12
    return Invoke-RestMethod -Method Post -Uri "$ApiBaseUrl/v1/authorize/batch" -Headers $headers -Body $json -ContentType "application/json"
}

Write-Host "Checking health endpoints..."
Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/health/live" | Out-Null
Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl/health/ready" | Out-Null
Invoke-WebRequest -Method Get -Uri $PortalBaseUrl | Out-Null

# -- Flow 1: machine-to-machine (client-credentials) ------------------------------
Write-Host "[M2M] Acquiring client-credentials token for $RuntimeClientId..."
$serviceToken = Get-ClientCredentialsToken -Url $TokenUrl -ClientId $RuntimeClientId -ClientSecret $RuntimeClientSecret

Write-Host "[M2M] Authorizing price.publish READY_TO_PUBLISH (service subject derived from azp)..."
$m2mAllow = Invoke-Authorize -AccessToken $serviceToken -Body @{
    applicationId = $ApplicationId
    subject       = @{ type = "SERVICE_ACCOUNT" }
    resource      = @{ type = "price"; id = "price-1" }
    action        = "publish"
    context       = @{ status = "READY_TO_PUBLISH" }
}
if (-not $m2mAllow.allowed) {
    throw "[M2M] Expected allow for price.publish, received deny reason: $($m2mAllow.denyReason)"
}

Write-Host "[M2M] Authorizing price.publish without the required context expecting deny..."
$m2mDeny = Invoke-Authorize -AccessToken $serviceToken -Body @{
    applicationId = $ApplicationId
    subject       = @{ type = "SERVICE_ACCOUNT" }
    resource      = @{ type = "price"; id = "price-1" }
    action        = "publish"
}
if ($m2mDeny.allowed) {
    throw "[M2M] Expected deny for price.publish without READY_TO_PUBLISH context, received allow decision $($m2mDeny.decisionId)"
}

Write-Host "[M2M] Sending a conflicting body subject expecting 403 SUBJECT_MISMATCH..."
Invoke-AuthorizeExpectingStatus -AccessToken $serviceToken -ExpectedStatus 403 -ExpectedErrorCode "SUBJECT_MISMATCH" -Body @{
    applicationId = $ApplicationId
    subject       = @{ type = "USER"; email = "someone.else@icis.com" }
    resource      = @{ type = "price"; id = "price-1" }
    action        = "publish"
    context       = @{ status = "READY_TO_PUBLISH" }
}

Write-Host "[M2M] Authorizing a batch (mixed allow/deny) expecting ordered results..."
$m2mBatch = Invoke-AuthorizeBatch -AccessToken $serviceToken -Body @{
    applicationId = $ApplicationId
    subject       = @{ type = "SERVICE_ACCOUNT" }
    checks        = @(
        @{ resource = @{ type = "price"; id = "price-1" }; action = "publish"; context = @{ status = "READY_TO_PUBLISH" } },
        @{ resource = @{ type = "price"; id = "price-2" }; action = "publish" }
    )
}
if ($m2mBatch.results.Count -ne 2) {
    throw "[M2M] Expected 2 batch results, received $($m2mBatch.results.Count)."
}
if (-not $m2mBatch.results[0].allowed) {
    throw "[M2M] Expected first batch check to be allowed, received deny $($m2mBatch.results[0].denyReason)."
}
if ($m2mBatch.results[1].allowed) {
    throw "[M2M] Expected second batch check to be denied (missing context), received allow."
}

# -- Negative caller authentication cases -----------------------------------------
Write-Host "[NEG] Missing bearer token expecting 401 CALLER_UNAUTHENTICATED..."
Invoke-AuthorizeExpectingStatus -AccessToken "" -ExpectedStatus 401 -ExpectedErrorCode "CALLER_UNAUTHENTICATED" -Body @{
    applicationId = $ApplicationId
    subject       = @{ type = "SERVICE_ACCOUNT" }
    resource      = @{ type = "price"; id = "price-1" }
    action        = "publish"
    context       = @{ status = "READY_TO_PUBLISH" }
}

Write-Host "[NEG] Valid token for the wrong application expecting 403 CALLER_APPLICATION_MISMATCH..."
Invoke-AuthorizeExpectingStatus -AccessToken $serviceToken -ExpectedStatus 403 -ExpectedErrorCode "CALLER_APPLICATION_MISMATCH" -Body @{
    applicationId = $WrongApplicationId
    subject       = @{ type = "SERVICE_ACCOUNT" }
    resource      = @{ type = "price"; id = "price-1" }
    action        = "publish"
    context       = @{ status = "READY_TO_PUBLISH" }
}

# -- Flow 2: user (interactive) ---------------------------------------------------
Write-Host "[USER] Acquiring password-grant token for $UserName via $UserClientId..."
$userToken = Get-UserToken -Url $TokenUrl -ClientId $UserClientId -ClientSecret $UserClientSecret -Username $UserName -Password $UserPassword

Write-Host "[USER] Authorizing price.publish (user subject derived from preferred_username)..."
$userAllow = Invoke-Authorize -AccessToken $userToken -Body @{
    applicationId = $ApplicationId
    subject       = @{ type = "USER" }
    resource      = @{ type = "price"; id = "price-1" }
    action        = "publish"
    context       = @{ status = "READY_TO_PUBLISH" }
}
if (-not $userAllow.allowed) {
    throw "[USER] Expected allow for price.publish, received deny reason: $($userAllow.denyReason)"
}

Write-Host "[USER] Authorizing price.publish without the required context expecting deny..."
$userDeny = Invoke-Authorize -AccessToken $userToken -Body @{
    applicationId = $ApplicationId
    subject       = @{ type = "USER" }
    resource      = @{ type = "price"; id = "price-1" }
    action        = "publish"
}
if ($userDeny.allowed) {
    throw "[USER] Expected deny for price.publish without READY_TO_PUBLISH context, received allow $($userDeny.decisionId)"
}

Write-Host "Local smoke passed. M2M allow: $($m2mAllow.decisionId). User allow: $($userAllow.decisionId). Batch results: $($m2mBatch.results.Count)."
