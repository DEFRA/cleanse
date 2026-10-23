<#
.SYNOPSIS
    Checks the Cleanse backend deployed on CDP, through the developer ephemeral gateway.

.DESCRIPTION
    Calls the deployed service the way a developer can from the DEFRA network:

        https://ephemeral-protected.api.<environment>.cdp-int.defra.cloud/cleanse/<endpoint>

    and reports each check as PASS, WARN or FAIL:

        Gateway   The gateway accepts your developer key (x-api-key).
        Liveness  GET /health answers. Liveness only; it needs no service key.
        API key   GET /v1/diagnostics with cleanse's own key (x-cleanse-api-key) is accepted.
        Database  The issue store, Aurora Postgres.
        Storage   The S3 bucket that uploaded import files go to.
        KRDS      The KRDS Data Bridge, asked through its own /health.
        KRDS read Whether cleanse can read the Data Bridge with its key: the latest-snapshot read
                  API a rule run calls. Only a genuine connection passes.

    The last four are read from /v1/diagnostics, which reports each dependency as ok, failed
    or unknown. Unknown is a warning: usually configuration that has not been set yet.

    Keys are read from the User environment first, so a key replaced in Windows settings is
    picked up without restarting the terminal, and then from the current session. They are
    only ever sent as request headers; nothing prints them.

.PARAMETER Environment
    The CDP environment to check. Developer keys only work in the environment they were
    issued for.

.PARAMETER GroupId
    Limits the issue counts to one rule group, for example cts-sam.

.PARAMETER GatewayKeyVariable
    The environment variable holding your developer key. Generate one in the CDP Portal from
    your user profile; it lasts 24 hours, or 2 in prod through break glass.

.PARAMETER ServiceKeyVariable
    The environment variable holding cleanse's own key: the Auth__ApiKey__Key secret for the
    environment.

.PARAMETER Json
    Also print the full /v1/diagnostics response.

.EXAMPLE
    ./scripts/check-remote-health.ps1

.EXAMPLE
    ./scripts/check-remote-health.ps1 -Environment ext-test -ServiceKeyVariable CLEANSE_APIKEY_EXTTEST

.NOTES
    For people, not pipelines: CDP developer keys are personal and short-lived, and CDP asks
    that they are not used for automated tests. Exits 1 when any check fails, otherwise 0.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'test', 'perf-test', 'ext-test', 'prod')]
    [string] $Environment = 'dev',

    [string] $GroupId,

    [string] $GatewayKeyVariable = 'CDP_EPHEMERAL_GW_KEY',

    [string] $ServiceKeyVariable = 'CLEANSE_APIKEY',

    [switch] $Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$service = 'cleanse'
$base = "https://ephemeral-protected.api.$Environment.cdp-int.defra.cloud/$service"
$timeoutSeconds = 60
$results = New-Object 'System.Collections.Generic.List[object]'
$diagnosticsBody = $null

function Get-Key([string] $Variable) {
    # User scope first: a terminal inherits its own copy when it starts, so that copy goes stale
    # as soon as a 24-hour developer key is replaced in Windows settings.
    foreach ($scope in 'User', 'Process') {
        $value = [Environment]::GetEnvironmentVariable($Variable, $scope)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [pscustomobject]@{ Value = $value.Trim(); Source = $scope }
        }
    }

    return $null
}

function Write-Result([string] $Check, [string] $Outcome, [string] $Detail) {
    $results.Add([pscustomobject]@{ Check = $Check; Outcome = $Outcome })

    $colour = switch ($Outcome) { 'PASS' { 'Green' } 'WARN' { 'Yellow' } default { 'Red' } }
    Write-Host ('  {0}  ' -f $Outcome) -ForegroundColor $colour -NoNewline
    Write-Host ('{0,-9} {1}' -f $Check, $Detail)
}

function Write-Info([string] $Label, [string] $Detail) {
    Write-Host '  INFO  ' -ForegroundColor DarkGray -NoNewline
    Write-Host ('{0,-9} {1}' -f $Label, $Detail)
}

function Format-Body([string] $Body) {
    $text = ($Body -replace '\s+', ' ').Trim()
    if ($text.Length -gt 160) {
        $text = $text.Substring(0, 160) + '...'
    }

    return $text
}

function Format-Utc($Value) {
    if ($null -eq $Value -or "$Value" -eq '') {
        return 'never'
    }

    # PowerShell 7 turns ISO dates into DateTime while parsing JSON; Windows PowerShell leaves strings.
    $utc = if ($Value -is [datetime]) {
        $Value.ToUniversalTime()
    }
    else {
        [DateTimeOffset]::Parse([string] $Value, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
    }

    return $utc.ToString("yyyy-MM-dd HH:mm 'UTC'", [Globalization.CultureInfo]::InvariantCulture)
}

function Get-FailureMessage([Exception] $Exception) {
    for ($e = $Exception; $null -ne $e; $e = $e.InnerException) {
        if ($e -is [System.Threading.Tasks.TaskCanceledException] -or $e -is [TimeoutException]) {
            return "no answer within $timeoutSeconds seconds"
        }
    }

    return $Exception.GetBaseException().Message
}

function Invoke-Gateway([string] $Path, [hashtable] $Headers) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, "$base$Path")
    foreach ($name in $Headers.Keys) {
        [void] $request.Headers.TryAddWithoutValidation($name, [string] $Headers[$name])
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return [pscustomobject]@{ Status = [int] $response.StatusCode; Body = $body; Ms = $watch.ElapsedMilliseconds; Failure = $null }
        }
        finally {
            $response.Dispose()
        }
    }
    catch {
        return [pscustomobject]@{ Status = 0; Body = ''; Ms = $watch.ElapsedMilliseconds; Failure = (Get-FailureMessage $_.Exception) }
    }
    finally {
        $request.Dispose()
    }
}

# The gateway checks the developer key before it calls the service, so a 403 is always the key,
# and anything after that came from, or on behalf of, the service.
function Test-GatewayAccepted([string] $Path, $Answer) {
    if ($Answer.Status -eq 0) {
        Write-Result 'Gateway' 'FAIL' "Could not reach the gateway: $($Answer.Failure.TrimEnd('.')). It is only reachable from the DEFRA network, so check Zscaler or the VPN."
        return $false
    }

    if ($Answer.Status -eq 403) {
        Write-Result 'Gateway' 'FAIL' "The gateway refused the developer key in $GatewayKeyVariable (403 on $Path). Keys last 24 hours and only work in the environment they were issued for: generate a new one for $Environment in the Portal, from your user profile."
        return $false
    }

    return $true
}

function Invoke-Checks {
    if (-not $gatewayKey) {
        Write-Result 'Gateway' 'FAIL' "$GatewayKeyVariable is not set. Generate a developer key for $Environment in the Portal, from your user profile, and store it in that User environment variable."
        return
    }

    $health = Invoke-Gateway '/health' @{ 'x-api-key' = $gatewayKey.Value }
    if (-not (Test-GatewayAccepted '/health' $health)) {
        return
    }

    Write-Result 'Gateway' 'PASS' 'Developer key accepted'

    if ($health.Status -eq 200) {
        Write-Result 'Liveness' 'PASS' ('GET /health -> 200 {0} ({1} ms)' -f (Format-Body $health.Body), $health.Ms)
    }
    elseif ($health.Status -eq 503 -and $health.Body -match '"shuttered"\s*:\s*true') {
        Write-Result 'Liveness' 'FAIL' "$service is shuttered in $Environment."
        return
    }
    elseif ($health.Status -in 502, 503, 504) {
        Write-Result 'Liveness' 'FAIL' ("GET /health -> {0} after {1} ms: no good answer from {2}. Is it deployed and running in {3}? {4}" -f $health.Status, $health.Ms, $service, $Environment, (Format-Body $health.Body)).TrimEnd()
        return
    }
    else {
        Write-Result 'Liveness' 'FAIL' ('GET /health -> {0} {1}' -f $health.Status, (Format-Body $health.Body)).TrimEnd()
        return
    }

    if (-not $serviceKey) {
        Write-Result 'API key' 'FAIL' "$ServiceKeyVariable is not set. It holds cleanse's Auth__ApiKey__Key secret for $Environment."
        return
    }

    $path = '/v1/diagnostics'
    if ($GroupId) {
        $path += '?groupId=' + [Uri]::EscapeDataString($GroupId)
    }

    $diagnostics = Invoke-Gateway $path @{ 'x-api-key' = $gatewayKey.Value; 'x-cleanse-api-key' = $serviceKey.Value }
    if (-not (Test-GatewayAccepted $path $diagnostics)) {
        return
    }

    if ($diagnostics.Status -eq 401) {
        Write-Result 'API key' 'FAIL' "$service refused the key in $ServiceKeyVariable (401). It must equal the Auth__ApiKey__Key secret for $Environment."
        return
    }

    if ($diagnostics.Status -ne 200) {
        Write-Result 'API key' 'FAIL' ('GET {0} -> {1} {2}' -f $path, $diagnostics.Status, (Format-Body $diagnostics.Body)).TrimEnd()
        return
    }

    Write-Result 'API key' 'PASS' ('GET {0} -> 200 ({1} ms)' -f $path, $diagnostics.Ms)
    $script:diagnosticsBody = $diagnostics.Body
    $report = $diagnostics.Body | ConvertFrom-Json

    $dependencies = [ordered]@{ Database = 'database'; Storage = 'storage'; KRDS = 'krds'; 'KRDS read' = 'krdsAccess' }
    foreach ($label in $dependencies.Keys) {
        $property = $report.PSObject.Properties[$dependencies[$label]]
        if ($null -eq $property) {
            Write-Result $label 'WARN' 'Not reported by the version deployed here.'
            continue
        }

        $dependency = $property.Value
        $outcome = switch ($dependency.state) { 'ok' { 'PASS' } 'failed' { 'FAIL' } default { 'WARN' } }

        $text = (@($dependency.provider, $dependency.target) | Where-Object { $_ }) -join ' '
        if ($dependency.detail) {
            $text += ": $($dependency.detail)"
        }

        $elapsed = [double] $dependency.elapsedMilliseconds
        if ($elapsed -gt 0) {
            $text += ' ({0} ms)' -f $elapsed.ToString('0.#', [Globalization.CultureInfo]::InvariantCulture)
        }

        Write-Result $label $outcome $text
    }

    $issues = $report.PSObject.Properties['issues']
    if ($issues -and $issues.Value.state -eq 'ok') {
        $scope = if ($GroupId) { "in group $GroupId" } else { 'across all groups' }
        Write-Info 'Issues' ('{0} {1}' -f $issues.Value.total, $scope)
    }

    $reset = $report.PSObject.Properties['lastResetAtUtc']
    if ($reset) {
        Write-Info 'Reset' ('Last data reset: {0}' -f (Format-Utc $reset.Value))
    }
}

if ($PSVersionTable.PSEdition -eq 'Desktop') {
    # Windows PowerShell: HttpClient lives in its own assembly, and older .NET Framework
    # defaults may not offer TLS 1.2.
    Add-Type -AssemblyName System.Net.Http
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

$gatewayKey = Get-Key $GatewayKeyVariable
$serviceKey = Get-Key $ServiceKeyVariable

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)

try {
    Write-Host "==> Checking $service in $Environment through the developer ephemeral gateway" -ForegroundColor Cyan
    Write-Host "    $base"
    Write-Host ('    Keys: {0} ({1}), {2} ({3})' -f
        $GatewayKeyVariable, $(if ($gatewayKey) { "$($gatewayKey.Source) environment" } else { 'not set' }),
        $ServiceKeyVariable, $(if ($serviceKey) { "$($serviceKey.Source) environment" } else { 'not set' }))
    Write-Host ''

    Invoke-Checks

    if ($Json -and $diagnosticsBody) {
        Write-Host ''
        Write-Host '==> /v1/diagnostics' -ForegroundColor Cyan
        $diagnosticsBody | ConvertFrom-Json | ConvertTo-Json -Depth 8
    }
}
finally {
    $client.Dispose()
}

$failed = @($results | Where-Object Outcome -eq 'FAIL').Count
$warned = @($results | Where-Object Outcome -eq 'WARN').Count
$passed = @($results | Where-Object Outcome -eq 'PASS').Count

Write-Host ''
if ($failed -gt 0) {
    Write-Host ('Unhealthy: {0} failed, {1} warning(s), {2} passed.' -f $failed, $warned, $passed) -ForegroundColor Red
    exit 1
}

$colour = if ($warned -gt 0) { 'Yellow' } else { 'Green' }
Write-Host ('Healthy: {0} passed, {1} warning(s).' -f $passed, $warned) -ForegroundColor $colour
exit 0
