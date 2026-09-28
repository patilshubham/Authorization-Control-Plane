param(
    [string]$ApiBaseUrl = "http://localhost:8080",
    [int]$Requests = 1000,
    [int]$Concurrency = 20,
    [int]$WarmupRequests = 50,
    [double]$P95TargetMilliseconds = 50,
    [string]$TokenUrl = "http://localhost:8081/realms/authorization-local/protocol/openid-connect/token",
    [string]$RuntimeClientId = "pricing-management-runtime-client",
    [string]$RuntimeClientSecret = "pricing_management_dev_secret",
    [string]$ApplicationId = "pricing-management"
)

$ErrorActionPreference = "Stop"

function Get-RuntimeToken {
    param([string]$Url, [string]$ClientId, [string]$ClientSecret)

    $response = Invoke-RestMethod -Method Post -Uri $Url -ContentType "application/x-www-form-urlencoded" -Body @{
        grant_type    = "client_credentials"
        client_id     = $ClientId
        client_secret = $ClientSecret
    }

    return $response.access_token
}

$accessToken = Get-RuntimeToken -Url $TokenUrl -ClientId $RuntimeClientId -ClientSecret $RuntimeClientSecret

$body = @{
    applicationId = $ApplicationId
    subject = @{ type = "SERVICE_ACCOUNT" }
    resource = @{ type = "price"; id = "price-1" }
    action = "publish"
    context = @{ status = "READY_TO_PUBLISH" }
} | ConvertTo-Json -Depth 8

$authorizeUri = "$ApiBaseUrl/v1/authorize"

# A single pooled HttpClient (shared across runspaces) keeps connection reuse realistic and avoids the
# per-call overhead of Invoke-RestMethod, so the measured latency reflects the API rather than the client.
$handler = [System.Net.Http.SocketsHttpHandler]::new()
$handler.MaxConnectionsPerServer = [Math]::Max(1, $Concurrency)
$handler.PooledConnectionLifetime = [TimeSpan]::FromMinutes(5)
$httpClient = [System.Net.Http.HttpClient]::new($handler)
$httpClient.Timeout = [TimeSpan]::FromSeconds(30)

function Invoke-AuthorizeHttp {
    param([System.Net.Http.HttpClient]$Client, [string]$Uri, [string]$Payload, [string]$Token, [string]$CorrelationId)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Uri)
    $request.Headers.TryAddWithoutValidation("Authorization", "Bearer $Token") | Out-Null
    $request.Headers.TryAddWithoutValidation("X-Correlation-ID", $CorrelationId) | Out-Null
    $request.Content = [System.Net.Http.StringContent]::new($Payload, [System.Text.Encoding]::UTF8, "application/json")
    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    try {
        if (-not $response.IsSuccessStatusCode) {
            throw "Authorize returned HTTP $([int]$response.StatusCode)."
        }
    }
    finally {
        $response.Dispose()
        $request.Dispose()
    }
}

function Get-Percentile {
    param([double[]]$Sorted, [double]$Percentile)
    if ($Sorted.Count -eq 0) { return 0 }
    $rank = [int]([Math]::Ceiling($Sorted.Count * $Percentile / 100.0)) - 1
    $rank = [Math]::Min($Sorted.Count - 1, [Math]::Max(0, $rank))
    return [Math]::Round($Sorted[$rank], 2)
}

Write-Host "Warming API with $WarmupRequests requests..."
1..$WarmupRequests | ForEach-Object {
    Invoke-AuthorizeHttp -Client $httpClient -Uri $authorizeUri -Payload $body -Token $accessToken -CorrelationId "perf-warmup-$_"
}

Write-Host "Running $Requests requests at target concurrency $Concurrency..."
$throttle = [Math]::Max(1, $Concurrency)
$wall = [System.Diagnostics.Stopwatch]::StartNew()
$results = 1..$Requests | ForEach-Object -Parallel {
    $client = $using:httpClient
    $uri = $using:authorizeUri
    $payload = $using:body
    $token = $using:accessToken

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
    $request.Headers.TryAddWithoutValidation("Authorization", "Bearer $token") | Out-Null
    $request.Headers.TryAddWithoutValidation("X-Correlation-ID", "perf-authorize-$_") | Out-Null
    $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, "application/json")

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $watch.Stop()
    try {
        if (-not $response.IsSuccessStatusCode) {
            throw "Authorize returned HTTP $([int]$response.StatusCode)."
        }
    }
    finally {
        $response.Dispose()
        $request.Dispose()
    }
    $watch.Elapsed.TotalMilliseconds
} -ThrottleLimit $throttle
$wall.Stop()

$httpClient.Dispose()
$handler.Dispose()

$ordered = @($results | Sort-Object)
$average = [Math]::Round((($ordered | Measure-Object -Average).Average), 2)
$min = [Math]::Round($ordered[0], 2)
$max = [Math]::Round($ordered[-1], 2)
$p50 = Get-Percentile -Sorted $ordered -Percentile 50
$p90 = Get-Percentile -Sorted $ordered -Percentile 90
$p95 = Get-Percentile -Sorted $ordered -Percentile 95
$p99 = Get-Percentile -Sorted $ordered -Percentile 99
$throughput = [Math]::Round($Requests / $wall.Elapsed.TotalSeconds, 1)

Write-Host "Requests:    $Requests"
Write-Host "Concurrency: $Concurrency"
Write-Host "Wall clock:  $([Math]::Round($wall.Elapsed.TotalSeconds, 2)) s"
Write-Host "Throughput:  $throughput req/s"
Write-Host "Latency ms:  min=$min avg=$average p50=$p50 p90=$p90 p95=$p95 p99=$p99 max=$max"

if ($p95 -gt $P95TargetMilliseconds) {
    throw "P95 $p95 ms exceeded target $P95TargetMilliseconds ms."
}

Write-Host "Performance sanity passed."
