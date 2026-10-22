<#
.SYNOPSIS
Runs the Cleanse backend in Docker the way CDP runs it.

.DESCRIPTION
Builds the service from this repository and starts compose.yml:

  postgres   Aurora stand-in with CDP's two roles: cleanse_ddl owns the schema, the service
             connects as cleanse with DML rights only.
  liquibase  Applies changelog/ and exits, as Portal > Apply Changelog does.
  floci      AWS emulator providing the cleanse-imports S3 bucket.
  cleanse    This service, with ASPNETCORE_ENVIRONMENT=Production, so the Postgres and S3 paths
             are exercised rather than SQLite and a temp directory.

Waits until everything is healthy, checks the API answers, and prints how to reach it. The
database lives in a Docker volume and survives restarts; -Reset starts from empty.

Host ports default to 8085 (API), 5432 (Postgres) and 4566 (floci). Move them with the
CLEANSE_PORT, CLEANSE_POSTGRES_PORT and CLEANSE_FLOCI_PORT environment variables.

.PARAMETER NoBuild
Reuse the last image instead of rebuilding it. Faster, but ignores code changes made since. The
image build runs the Cleanse.Test and Cleanse.Rules.Test suites.

.PARAMETER Reset
Delete the database volume first, so the stack starts empty. With -Down, delete it on the way out.

.PARAMETER Follow
Follow the service's logs once it is up. Ctrl+C stops following; the stack keeps running.

.PARAMETER Down
Stop the stack and exit. The database is kept unless -Reset is also given.

.PARAMETER SkipSmoke
Skip the HTTP checks made once the stack reports healthy.

.PARAMETER KrdsSnapshot
Path to a KRDS snapshot (.sqlite). Turns the rules engine on against it, mounted read-only.
Defaults to $env:CLEANSE_KRDS_SNAPSHOT; leave both unset to keep the engine off.

.EXAMPLE
./dev-docker-up.ps1

.EXAMPLE
./dev-docker-up.ps1 -KrdsSnapshot A:\tmp\defra-krds-duckdb\krds-db_20261019070003.sqlite

.EXAMPLE
./dev-docker-up.ps1 -Down -Reset
#>
[CmdletBinding()]
param(
  [switch]$NoBuild,
  [switch]$Reset,
  [switch]$Follow,
  [switch]$Down,
  [switch]$SkipSmoke,
  [string]$KrdsSnapshot = $env:CLEANSE_KRDS_SNAPSHOT
)

$ErrorActionPreference = 'Stop'

# Matches `name:` in compose.yml. Passed explicitly so COMPOSE_PROJECT_NAME cannot redirect it.
$project = 'cleanse'
$apiKey = 'local-development-key'

function Write-Step([string]$Message) {
  Write-Host "==> $Message" -ForegroundColor Cyan
}

# Windows PowerShell turns a native command's redirected stderr into terminating errors under
# ErrorActionPreference=Stop; the exit code is the signal that matters, so read that instead.
function Invoke-Native([scriptblock]$Command) {
  $previous = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { & $Command } finally { $ErrorActionPreference = $previous }
}

function Get-HostPort([string]$Variable, [int]$Default) {
  $value = [Environment]::GetEnvironmentVariable($Variable)
  if ([string]::IsNullOrWhiteSpace($value)) {
    return $Default
  }

  $port = 0
  if (-not [int]::TryParse($value, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
    throw "$Variable must be a port number between 1 and 65535; found '$value'."
  }

  return $port
}

function Assert-Docker {
  if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'The docker CLI was not found. Install Docker Desktop (or another Docker engine) and try again.'
  }

  $server = Invoke-Native { docker version --format '{{.Server.Version}}' 2>$null }
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$server")) {
    throw 'Docker is installed but the engine is not reachable. Start Docker Desktop and try again.'
  }

  $compose = Invoke-Native { docker compose version --short 2>$null }
  if ($LASTEXITCODE -ne 0) {
    throw 'Docker Compose v2 (the "docker compose" plugin) is required.'
  }

  Write-Host "    Docker engine $server, Compose $compose"
}

# floci runs its init hooks with bash inside a Linux container, where a carriage return becomes
# part of each command. .gitattributes keeps new checkouts LF, but a clone made before it existed
# keeps CRLF until the files are checked out again, so repair them here. Git sees no change.
function Repair-ScriptLineEndings {
  $root = Join-Path $PSScriptRoot 'compose'
  foreach ($script in Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.sh') {
    $text = [IO.File]::ReadAllText($script.FullName)
    if ($text.Contains("`r")) {
      [IO.File]::WriteAllText($script.FullName, ($text -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
      Write-Host "    Converted $($script.Name) to LF line endings; with CRLF it fails inside the container." -ForegroundColor Yellow
    }
  }
}

# Names whoever holds a port, for the error message only. Best effort, hence the retries: on some
# Windows machines the listener table briefly omits a listener that is accepting connections.
function Get-PortOwner([int]$Port) {
  if (-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
    return $null
  }

  for ($attempt = 1; $attempt -le 10; $attempt++) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
      Select-Object -First 1
    if ($listener) {
      $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
      if ($process) {
        return "$($process.ProcessName) (PID $($process.Id))"
      }

      return "PID $($listener.OwningProcess)"
    }

    Start-Sleep -Milliseconds 200
  }

  return $null
}

# A connection attempt is the reliable test for a port in use, unlike the listener table. Windows
# takes about two seconds to report a refused connection, so every attempt runs at once against a
# short deadline: anything listening locally answers within milliseconds.
function Get-PortsInUse([int[]]$Ports) {
  $attempts = @(foreach ($port in $Ports) {
    foreach ($address in [Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Loopback) {
      try {
        $client = [Net.Sockets.TcpClient]::new($address.AddressFamily)
        [pscustomobject]@{ Port = $port; Client = $client; Task = $client.ConnectAsync($address, $port) }
      }
      catch {
        # No IPv6 on this machine; the IPv4 attempt still covers the port.
      }
    }
  })

  try {
    $deadline = [DateTime]::UtcNow.AddMilliseconds(750)
    while ([DateTime]::UtcNow -lt $deadline -and @($attempts | Where-Object { -not $_.Task.IsCompleted }).Count -gt 0) {
      Start-Sleep -Milliseconds 50
    }

    return @($attempts | Where-Object { $_.Task.Status -eq 'RanToCompletion' } | ForEach-Object { $_.Port } | Sort-Object -Unique)
  }
  finally {
    foreach ($attempt in $attempts) {
      $attempt.Client.Dispose()
    }
  }
}

# Fails before anything starts, rather than part-way through with a half-created stack.
function Assert-PortsFree {
  $expectedFree = @()

  foreach ($binding in $portBindings) {
    $port = $binding.Port
    $holders = @(Invoke-Native { docker ps --filter "publish=$port" --format '{{.Names}}' } | Where-Object { $_ })
    $ours = @(Invoke-Native { docker ps --filter "label=com.docker.compose.project=$project" --filter "publish=$port" --format '{{.Names}}' } | Where-Object { $_ })
    $theirs = @($holders | Where-Object { $ours -notcontains $_ })

    if ($theirs.Count -gt 0) {
      throw "Port $port ($($binding.Service)) is published by the container(s) $($theirs -join ', '). Stop them, or set $($binding.Variable) to another port."
    }

    # Already ours: compose updates the running stack in place.
    if ($ours.Count -eq 0) {
      $expectedFree += $binding
    }
  }

  if ($expectedFree.Count -eq 0) {
    return
  }

  $inUse = Get-PortsInUse @($expectedFree | ForEach-Object { $_.Port })
  foreach ($binding in $expectedFree) {
    if ($inUse -contains $binding.Port) {
      $owner = Get-PortOwner $binding.Port
      if (-not $owner) {
        $owner = 'another process'
      }

      throw "Port $($binding.Port) ($($binding.Service)) is in use by $owner. Stop it, or set $($binding.Variable) to another port."
    }
  }
}

function Show-Failure {
  Write-Host ''
  Write-Host 'The stack did not come up healthy. Current state:' -ForegroundColor Red
  & docker compose --project-name $project @composeFiles ps --all
  foreach ($service in 'floci', 'postgres', 'liquibase', 'cleanse') {
    Write-Host "---- $service, last 30 lines ----" -ForegroundColor DarkGray
    & docker compose --project-name $project @composeFiles logs --no-color --tail 30 $service
  }
}

function Test-Stack {
  Write-Step 'Checking the API'
  $base = "http://localhost:$apiPort"

  try {
    $health = Invoke-WebRequest -UseBasicParsing -Uri "$base/health" -TimeoutSec 10
    Write-Host "    GET /health          $($health.StatusCode) $($health.Content)"

    $diagnostics = Invoke-RestMethod -Uri "$base/v1/diagnostics" -Headers @{ 'x-cleanse-api-key' = $apiKey } -TimeoutSec 30
  }
  catch {
    throw "The API did not answer as expected: $($_.Exception.Message) See: docker compose logs cleanse"
  }

  Write-Host ("    GET /v1/diagnostics  database {0} ({1}), rules data {2}" -f
    $diagnostics.database.state, $diagnostics.database.detail, $diagnostics.krds.state)

  if ($diagnostics.database.state -ne 'ok') {
    throw "The service cannot use Postgres: $($diagnostics.database.detail)"
  }

  if ($rulesOn -and $diagnostics.krds.state -ne 'ok') {
    throw "The rules engine cannot read the snapshot: $($diagnostics.krds.detail)"
  }
}

function Show-Summary {
  $base = "http://localhost:$apiPort"

  Write-Host ''
  Write-Host 'Cleanse is up, on Production settings against Postgres and floci S3.' -ForegroundColor Green
  Write-Host "  API         $base   (header x-cleanse-api-key: $apiKey)"
  Write-Host "  OpenAPI     $base/v1/openapi.json"
  Write-Host "  Postgres    localhost:$postgresPort, database cleanse. Service login cleanse/cleanse; owner cleanse_ddl/cleanse_ddl"
  Write-Host "  S3          http://localhost:$flociPort (floci), bucket cleanse-imports"

  if ($rulesOn) {
    Write-Host "  Rules       on, reading $KrdsSnapshot"
    Write-Host "              start a run: Invoke-RestMethod -Method Post $base/v1/runs/trigger -Headers @{ 'x-cleanse-api-key' = '$apiKey' }"
  }
  else {
    Write-Host '  Rules       off; pass -KrdsSnapshot <path> to run them against a local snapshot'
  }

  Write-Host ''
  if ($apiPort -eq 8085) {
    Write-Host "  Frontend    cleanse-app's dev-up.ps1 talks to this backend as it is"
  }
  else {
    Write-Host "  Frontend    set CLEANSE_API_URL=$base before starting cleanse-app"
  }

  Write-Host '  Logs        docker compose logs -f cleanse   (or ./dev-docker-up.ps1 -NoBuild -Follow)'
  Write-Host '  Stop        ./dev-docker-up.ps1 -Down         (add -Reset to delete the database)'
}

# The service logs ECS JSON for OpenSearch; this keeps the time, level, message and any error.
function Format-LogLine([string]$Line) {
  if (-not $Line.StartsWith('{')) {
    return $Line
  }

  try {
    $entry = $Line | ConvertFrom-Json
  }
  catch {
    return $Line
  }

  $time = ''
  if ($entry.'@timestamp') {
    $time = ([DateTimeOffset]$entry.'@timestamp').ToLocalTime().ToString('HH:mm:ss')
  }

  $text = '{0} {1,-11} {2}' -f $time, $entry.'log.level', $entry.message
  if ($entry.'error.type' -or $entry.error) {
    $type = if ($entry.'error.type') { $entry.'error.type' } else { $entry.error.type }
    $message = if ($entry.'error.message') { $entry.'error.message' } else { $entry.error.message }
    $text += " | ${type}: $message"
  }

  return $text
}

$previousSnapshot = $env:CLEANSE_KRDS_SNAPSHOT
Push-Location $PSScriptRoot

try {
  $apiPort = Get-HostPort 'CLEANSE_PORT' 8085
  $postgresPort = Get-HostPort 'CLEANSE_POSTGRES_PORT' 5432
  $flociPort = Get-HostPort 'CLEANSE_FLOCI_PORT' 4566
  $portBindings = @(
    @{ Port = $apiPort; Service = 'cleanse'; Variable = 'CLEANSE_PORT' },
    @{ Port = $postgresPort; Service = 'postgres'; Variable = 'CLEANSE_POSTGRES_PORT' },
    @{ Port = $flociPort; Service = 'floci'; Variable = 'CLEANSE_FLOCI_PORT' }
  )

  $composeFiles = @('--file', 'compose.yml')

  Write-Step 'Checking Docker'
  Assert-Docker

  if ($Down) {
    Write-Step 'Stopping the Cleanse stack'
    $downArgs = @('down', '--remove-orphans')
    if ($Reset) {
      $downArgs += '--volumes'
    }

    & docker compose --project-name $project @composeFiles @downArgs
    if ($LASTEXITCODE -ne 0) {
      throw 'docker compose down failed; see the output above.'
    }

    if ($Reset) {
      Write-Host 'Stopped, and the database deleted.' -ForegroundColor Green
    }
    else {
      Write-Host 'Stopped. The database is kept; -Down -Reset deletes it.' -ForegroundColor Green
    }

    return
  }

  $rulesOn = -not [string]::IsNullOrWhiteSpace($KrdsSnapshot)
  if ($rulesOn) {
    if (-not (Test-Path -LiteralPath $KrdsSnapshot -PathType Leaf)) {
      throw "KRDS snapshot not found: $KrdsSnapshot"
    }

    $KrdsSnapshot = (Resolve-Path -LiteralPath $KrdsSnapshot).ProviderPath
    # Read by compose.krds.yml. Forward slashes keep the bind source unambiguous on Windows.
    $env:CLEANSE_KRDS_SNAPSHOT = $KrdsSnapshot -replace '\\', '/'
    $composeFiles += @('--file', 'compose.krds.yml')
  }

  Write-Step 'Checking ports and scripts'
  Repair-ScriptLineEndings
  Assert-PortsFree

  if ($Reset) {
    Write-Step 'Deleting the database volume'
    & docker compose --project-name $project @composeFiles down --volumes --remove-orphans
    if ($LASTEXITCODE -ne 0) {
      throw 'docker compose down failed; see the output above.'
    }
  }

  $upArgs = @('up', '--detach', '--wait', '--wait-timeout', '300', '--remove-orphans')
  if ($NoBuild) {
    Write-Step 'Starting the stack (no rebuild)'
  }
  else {
    Write-Step 'Building the service image and starting the stack'
    $upArgs += '--build'
  }

  & docker compose --project-name $project @composeFiles @upArgs
  if ($LASTEXITCODE -ne 0) {
    Show-Failure
    throw 'The stack did not come up healthy; see the state and logs above.'
  }

  if (-not $SkipSmoke) {
    Test-Stack
  }

  Show-Summary

  if ($Follow) {
    Write-Step 'Following the service logs (Ctrl+C stops following; the stack keeps running)'
    & docker compose --project-name $project @composeFiles logs --follow --no-log-prefix --tail 20 cleanse |
      ForEach-Object { Format-LogLine $_ }
  }
}
finally {
  $env:CLEANSE_KRDS_SNAPSHOT = $previousSnapshot
  Pop-Location
}
