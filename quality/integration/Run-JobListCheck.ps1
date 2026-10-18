[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet = Join-Path (Split-Path $repo -Parent) 'temp\dotnet-sdk8\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$runId = Get-Date -Format 'yyyyMMddTHHmmssfff'
$container = "assms-ase-job-list-$runId"
$artifacts = Join-Path $repo "quality\artifacts\job-list-$runId"
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$random = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($random)
$password = [BitConverter]::ToString($random).Replace('-', '')
$started = $false

try {
    docker run --rm -d --name $container `
        -e "MYSQL_ROOT_PASSWORD=$password" -e 'MYSQL_DATABASE=jobdb' `
        -p '127.0.0.1::3306' 'mysql:8.0.40' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the disposable MySQL container.' }
    $started = $true

    $ready = $false
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        docker exec -e "MYSQL_PWD=$password" $container mysqladmin ping -h 127.0.0.1 -u root --silent *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'Disposable MySQL did not become ready.' }

    $migrations = Get-ChildItem -LiteralPath (Join-Path $repo 'database\migrations') -Filter 'V*.sql' |
        Sort-Object Name
    foreach ($migration in $migrations) {
        Get-Content -LiteralPath $migration.FullName -Raw | docker exec -i -e "MYSQL_PWD=$password" $container mysql -u root jobdb
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($migration.Name)" }
        Write-Output "Applied $($migration.Name)"
    }

    $portLine = docker port $container '3306/tcp'
    if ($LASTEXITCODE -ne 0 -or $portLine -notmatch '^127\.0\.0\.1:(\d+)$') {
        throw 'Could not identify the local disposable MySQL port.'
    }
    $port = [int]$Matches[1]
    $env:ASSMS_ASE_JOB_LIST_CONNECTION = "Server=127.0.0.1;Port=$port;Database=jobdb;User ID=root;Password=$password;SslMode=None"
    & $dotnet run --project (Join-Path $PSScriptRoot 'JobListCheck.csproj') --configuration Release |
        Tee-Object -FilePath (Join-Path $artifacts 'results.txt')
    if ($LASTEXITCODE -ne 0) { throw 'The JobRepository integration check failed.' }
    Write-Output "Evidence: $artifacts"
}
finally {
    Remove-Item Env:ASSMS_ASE_JOB_LIST_CONNECTION -ErrorAction SilentlyContinue
    if ($started) {
        docker rm -f $container *> $null
    }
}
