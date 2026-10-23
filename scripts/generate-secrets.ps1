<#
.SYNOPSIS
    Generates the secrets for the Cleanse backend and the Cleanse frontend in one pass.

.DESCRIPTION
    Every secret the two services need, printed ready to paste into the CDP Portal secrets
    manager. Values are generated with a cryptographically secure RNG.

    The point of doing both services together is that two of the values must agree across them:
    the API key the frontend sends is the one the backend compares against. Generating them
    separately is how they drift.

    Nothing is written to disk and nothing is logged. Paste the values into the Portal, then
    close the terminal.

    Portal -> your service -> Secrets. One secret per environment; secrets are never shared
    between services, so the frontend needs its own copy of the shared API key.

.PARAMETER Environment
    The CDP environment the secrets are destined for. Used only to label the output.

.PARAMETER Format
    Table  - human-readable, grouped by service. The default.
    Env    - KEY=value lines, for pasting into a local .env file.
    Json   - one JSON object per service, for bulk entry.

.PARAMETER IncludeExternal
    Also list the secrets that are issued by another team and cannot be generated here, so the
    checklist is complete.

.EXAMPLE
    ./generate-secrets.ps1 -Environment dev

.EXAMPLE
    ./generate-secrets.ps1 -Environment prod -Format Json -IncludeExternal

.NOTES
    Re-running generates fresh values. Rotating the API key means updating both services, and
    the two deployments are not atomic: set the new key on the backend first only if you are
    prepared for a window of 401s, otherwise shutter the frontend for the swap.
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'test', 'perf-test', 'ext-test', 'prod')]
    [string] $Environment = 'dev',

    [ValidateSet('Table', 'Env', 'Json')]
    [string] $Format = 'Table',

    [switch] $IncludeExternal
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-Secret {
    <#
        .SYNOPSIS
            A URL- and shell-safe random string, from the OS CSPRNG.
        .DESCRIPTION
            Base64url rather than base64: these values travel through env vars, YAML and HTTP
            headers, and '+', '/' and '=' each cause trouble in at least one of those.
    #>
    param([int] $Bytes = 48)

    $buffer = [byte[]]::new($Bytes)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($buffer)
    }
    finally {
        $rng.Dispose()
    }

    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# Generated once, then issued to both services. These are the values that must agree.
$sharedApiKey = New-Secret -Bytes 48

$backend = [ordered]@{
    'Auth__ApiKey__Key'  = @{
        Value = $sharedApiKey
        Note  = 'Shared key for the x-cleanse-api-key header. Must equal the frontend CLEANSE_API_KEY.'
    }
    'Auth__Reset__Key'   = @{
        Value = New-Secret -Bytes 48
        Note  = 'Guards POST /v1/maintenance/reset, which permanently deletes data. Leave unset in prod to disable the endpoint entirely.'
    }
}

$frontend = [ordered]@{
    'SESSION_COOKIE_PASSWORD' = @{
        # @hapi/yar refuses anything shorter than 32 characters; 32 bytes gives 43.
        Value = New-Secret -Bytes 32
        Note  = 'Signs and encrypts the session cookie. At least 32 characters.'
    }
    'CLEANSE_API_KEY'         = @{
        Value = $sharedApiKey
        Note  = 'The same value as the backend Auth__ApiKey__Key. Only used when CLEANSE_API_AUTH is apiKey; the deployed default is the STS web identity token, which needs no secret.'
    }
    # People type or paste these, so they are shorter than the machine keys; 18 bytes gives 24
    # characters, still far beyond guessing.
    'PASSWORD'                = @{
        Value = New-Secret -Bytes 18
        Note  = 'The password everyone signs in to the frontend with. Share it only with the people who use the service.'
    }
    'ADMIN_PASSWORD'          = @{
        Value = New-Secret -Bytes 18
        Note  = 'The second password the /admin area asks for, after PASSWORD. Share it only with the people who run the service.'
    }
}

$external = [ordered]@{
    'cleanse / Rules__Krds__ApiKey' = 'Issued by the KRDS team for the Data Bridge in this environment. Cannot be generated here.'
}

function Write-Table {
    param([string] $Service, [System.Collections.Specialized.OrderedDictionary] $Secrets)

    Write-Host ''
    Write-Host "  $Service" -ForegroundColor Cyan
    Write-Host "  $('-' * $Service.Length)" -ForegroundColor Cyan

    foreach ($name in $Secrets.Keys) {
        Write-Host ''
        Write-Host "  $name" -ForegroundColor Yellow
        Write-Host "  $($Secrets[$name].Value)"
        Write-Host "  $($Secrets[$name].Note)" -ForegroundColor DarkGray
    }
}

function Write-EnvLines {
    param([string] $Service, [System.Collections.Specialized.OrderedDictionary] $Secrets)

    Write-Host ''
    Write-Host "# $Service"
    foreach ($name in $Secrets.Keys) {
        Write-Host "$name=$($Secrets[$name].Value)"
    }
}

function ConvertTo-PlainMap {
    param([System.Collections.Specialized.OrderedDictionary] $Secrets)

    $map = [ordered]@{}
    foreach ($name in $Secrets.Keys) {
        $map[$name] = $Secrets[$name].Value
    }
    return $map
}

switch ($Format) {
    'Table' {
        Write-Host ''
        Write-Host "Cleanse secrets for $Environment" -ForegroundColor Green
        Write-Host 'Paste into Portal -> service -> Secrets. Do not commit these anywhere.' -ForegroundColor DarkGray

        Write-Table -Service 'cleanse (backend)' -Secrets $backend
        Write-Table -Service 'cleanse-app (frontend)' -Secrets $frontend
    }

    'Env' {
        Write-EnvLines -Service 'cleanse (backend)' -Secrets $backend
        Write-EnvLines -Service 'cleanse-app (frontend)' -Secrets $frontend
    }

    'Json' {
        [ordered]@{
            environment   = $Environment
            'cleanse'     = ConvertTo-PlainMap -Secrets $backend
            'cleanse-app' = ConvertTo-PlainMap -Secrets $frontend
        } | ConvertTo-Json -Depth 3
    }
}

if ($IncludeExternal) {
    Write-Host ''
    Write-Host '  Not generated here' -ForegroundColor Cyan
    Write-Host '  ------------------' -ForegroundColor Cyan
    foreach ($name in $external.Keys) {
        Write-Host ''
        Write-Host "  $name" -ForegroundColor Yellow
        Write-Host "  $($external[$name])" -ForegroundColor DarkGray
    }
}

Write-Host ''
