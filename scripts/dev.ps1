# Developer helper for the local Docker stack (compose.dev.yaml). Usage: scripts\dev.ps1 <command> [args]
param([string]$Command = 'help', [Parameter(ValueFromRemainingArguments)][string[]]$Rest)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
function Compose { docker compose -f compose.dev.yaml @args; if ($LASTEXITCODE) { exit $LASTEXITCODE } }

switch ($Command) {
  'setup' {
    if (Test-Path .env) { Write-Host '.env already exists; leaving it untouched.' } else {
      $bytes = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
      $pw = -join ($bytes | ForEach-Object { $_.ToString('x2') })
      (Get-Content .env.example -Raw).Replace('change-me-dev-only', $pw) | Set-Content .env -NoNewline -Encoding ascii
      Write-Host 'Created .env' }
  }
  'up'      { Compose up -d --build --wait; Compose ps }
  'stop'    { Compose stop }                      # keeps containers and the database volume
  'status'  { Compose ps }
  'logs'    { Compose logs -f --tail=100 @Rest }
  'restart' { Compose restart @Rest }
  'rebuild' { Compose up -d --build @Rest }       # after changing a Dockerfile / SDK version
  'seed'    { node scripts/dev-seed.mjs }
  'migrate' { Compose restart api }               # the dev API applies pending migrations on start
  'test-backend'  { dotnet test PulseReserve.sln }
  'test-frontend' { Push-Location frontend/pulsereserve-web; npm run lint; npm run test:ci; npm run build; Pop-Location }
  'psql'    { Compose exec postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"' }
  default   { Write-Host 'commands: setup up stop status logs [svc] restart <svc> rebuild [svc] seed migrate test-backend test-frontend psql'
              Write-Host 'Data is kept across stop/up. Deleting data (docker compose down -v) is deliberately not offered here.' }
}
