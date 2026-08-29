<#
.SYNOPSIS
    Applies the .sql migrations in a folder to a MySQL database running in a Docker container.

.DESCRIPTION
    Files are applied in version order (V01__, V02__, ... V10__), one at a time.
    If a migration fails the run stops immediately, because later migrations
    normally assume the earlier ones succeeded.

    Applied migrations are tracked in a schema_migrations table inside the target
    database: the table is created if absent, the filenames already listed in it
    are read before the loop, those files are skipped, and each filename is
    inserted once its file has succeeded. A migration therefore runs exactly once
    per database and does not have to be safe to re-run.

    That is what V03 needs. Adding a column has no safe-to-re-run form on MySQL 8 -
    ALTER TABLE ... ADD COLUMN IF NOT EXISTS is MariaDB syntax and is a syntax
    error here - so without tracking a second run would fail on it.

    A database migrated before the tracking existed has no schema_migrations rows,
    so V01 and V02 are applied once more on the first tracked run and then
    recorded. Both are CREATE TABLE IF NOT EXISTS, so that costs nothing.

    A filename is recorded only after its file succeeded, so a migration that
    failed is retried on the next run rather than being skipped as done.

.EXAMPLE
    $env:MYSQL_PASSWORD = 'CustomerLocalDev!23'
    .\apply_migrations.ps1 -Database customerdb -User customer_svc

.EXAMPLE
    .\apply_migrations.ps1 -Database jobdb -User job_svc -Password 'JobLocalDev!23'
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Database,

    [Parameter(Mandatory = $true)]
    [string]$User,

    [string]$Password = $env:MYSQL_PASSWORD,

    [string]$MigrationsPath = (Join-Path $PSScriptRoot '..\..\database\migrations'),

    [string]$Container = 'assms-mysql'
)

$ErrorActionPreference = 'Stop'

# Runs a SQL string through the mysql client in the container and returns its
# output as lines. --batch --skip-column-names keeps that output to bare values,
# so a SELECT comes back as one filename per line with no header or box drawing.
# Native commands do not throw, so every caller checks $LASTEXITCODE itself.
function Invoke-MySqlQuery {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sql
    )

    $Sql | docker exec -i -e "MYSQL_PWD=$Password" $Container mysql -u $User --batch --skip-column-names $Database
}

if ([string]::IsNullOrWhiteSpace($Password)) {
    Write-Host "No password supplied. Pass -Password or set the MYSQL_PASSWORD environment variable." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $MigrationsPath)) {
    Write-Host "Migrations folder not found: $MigrationsPath" -ForegroundColor Red
    exit 1
}

# Sort on the number extracted from the V<n>__ prefix, so V2 runs before V10
# regardless of whether the version numbers are zero-padded.
$files = @(
    Get-ChildItem -Path $MigrationsPath -Filter *.sql |
        Sort-Object @{ Expression = { if ($_.Name -match '^V(\d+)__') { [int]$Matches[1] } else { [int]::MaxValue } } }, Name
)

if ($files.Count -eq 0) {
    Write-Host "No .sql files found in $MigrationsPath" -ForegroundColor Yellow
    exit 0
}

# The tracking table is not itself a migration file: it has to exist before the
# first migration is looked at, and it belongs to this script rather than to the
# schema the migrations build.
$createTrackingSql = @'
CREATE TABLE IF NOT EXISTS schema_migrations (
    filename   VARCHAR(255) NOT NULL,
    applied_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (filename)
);
'@

Invoke-MySqlQuery -Sql $createTrackingSql | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "Could not create or reach schema_migrations in '$Database' (mysql exit code $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

# Captured before the trimming pipeline so $LASTEXITCODE is read straight off
# the mysql call and not off whatever ran after it.
$appliedRows = @(Invoke-MySqlQuery -Sql 'SELECT filename FROM schema_migrations;')

if ($LASTEXITCODE -ne 0) {
    Write-Host "Could not read schema_migrations from '$Database' (mysql exit code $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

$alreadyApplied = @($appliedRows | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })

Write-Host "Checking $($files.Count) migration file(s) against '$Database' in container '$Container'..." -ForegroundColor Cyan
Write-Host ""

$applied = @()
$skipped = @()
$failedFile = $null
$failedCode = 0

foreach ($file in $files) {
    if ($alreadyApplied -contains $file.Name) {
        Write-Host "  -- $($file.Name) (already applied)" -ForegroundColor DarkGray
        $skipped += $file.Name
        continue
    }

    Write-Host "  -> $($file.Name)"

    Get-Content $file.FullName | docker exec -i -e "MYSQL_PWD=$Password" $Container mysql -u $User $Database

    if ($LASTEXITCODE -ne 0) {
        $failedFile = $file.Name
        $failedCode = $LASTEXITCODE
        break
    }

    # Doubling any single quote keeps a filename that contains one from ending
    # the literal early. Migration names have never had one, but the value is
    # still going into SQL as text and the mysql client takes no parameters.
    $escapedName = $file.Name.Replace("'", "''")

    Invoke-MySqlQuery -Sql "INSERT INTO schema_migrations (filename) VALUES ('$escapedName');" | Out-Null

    if ($LASTEXITCODE -ne 0) {
        # The migration ran but was not recorded, so the next run would apply it
        # a second time - which for a column addition is exactly the failure the
        # tracking exists to prevent. Stop here rather than leave it to be found
        # later, and stop before any later migration muddies which one it was.
        Write-Host "     applied, but could not be recorded in schema_migrations" -ForegroundColor Red
        $failedFile = $file.Name
        $failedCode = $LASTEXITCODE
        break
    }

    $applied += $file.Name
}

Write-Host ""
Write-Host "----- Summary -----"

if ($skipped.Count -gt 0) {
    Write-Host "Already applied, skipped ($($skipped.Count)):" -ForegroundColor DarkGray
    foreach ($name in $skipped) {
        Write-Host "  $name" -ForegroundColor DarkGray
    }
}

if ($applied.Count -gt 0) {
    Write-Host "Applied ($($applied.Count)):" -ForegroundColor Green
    foreach ($name in $applied) {
        Write-Host "  $name" -ForegroundColor Green
    }
} else {
    Write-Host "Applied: none"
}

if ($failedFile) {
    Write-Host "FAILED on: $failedFile (mysql exit code $failedCode)" -ForegroundColor Red

    # Everything the loop never reached: the total less the ones it skipped, the
    # ones it applied, and the one it broke on.
    $notRun = $files.Count - $skipped.Count - $applied.Count - 1
    if ($notRun -gt 0) {
        Write-Host "Stopped early - $notRun later migration(s) not run." -ForegroundColor Yellow
    }

    exit 1
}

Write-Host "All migrations applied successfully." -ForegroundColor Green
exit 0
