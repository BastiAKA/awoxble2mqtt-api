# Creates the full AwoxController database environment in one go: the database + application user
# (InitDB.sql), then every table in foreign-key dependency order. Idempotent — every statement is
# CREATE ... IF NOT EXISTS, so it's safe to re-run.
#
# Requires the `mysql` client on PATH and admin (root) credentials to create the database/user.
#
#   # local database:
#   .\database\CreateDBEnv.ps1 -Password <rootPassword>
#   # remote database (e.g. the Pi):
#   .\database\CreateDBEnv.ps1 -MySqlHost 192.168.1.53 -User root -Password <rootPassword>
#
# After this, point ConnectionStrings:AwoxDb at the same db/user/password (see docs/CONFIGURATION.md).
# If you changed the db name / user / password, edit InitDB.sql and the `USE` line in the table scripts.

param(
    [string]$MySqlHost = "localhost",
    [int]$Port = 3306,
    [string]$User = "root",
    [Parameter(Mandatory = $true)][string]$Password
)
$ErrorActionPreference = "Stop"
$dir = $PSScriptRoot

# Order matters: DB+user first, then meshes (no FK) → lamps (FK meshes) → app_settings → scenes →
# scene_items (FK scenes + lamps).
$scripts = @(
    "InitDB.sql",
    "tables\01_meshes.sql",
    "tables\02_lamps.sql",
    "tables\03_app_settings.sql",
    "tables\04_scenes.sql",
    "tables\05_scene_items.sql",
    "SeedSettings.sql"          # runtime-tunable defaults into app_settings (after the table exists)
)

foreach ($s in $scripts) {
    $path = Join-Path $dir $s
    if (-not (Test-Path $path)) { throw "Missing SQL script: $path" }
    Write-Host "-> $s"
    Get-Content $path -Raw | & mysql -h $MySqlHost -P $Port -u $User "-p$Password"
    if ($LASTEXITCODE -ne 0) { throw "mysql failed on $s (exit $LASTEXITCODE)" }
}

Write-Host "Database environment ready (database + user + all tables)."
