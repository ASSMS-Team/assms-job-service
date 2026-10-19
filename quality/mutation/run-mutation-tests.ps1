<#
.SYNOPSIS
    Runs Stryker.NET mutation testing against this service's unit tests.

.DESCRIPTION
    Used both locally and by the GitHub Actions "Mutation testing" workflow, so
    a local run and a pipeline run do exactly the same thing.

    Each run is written to mutation-reports/<timestamp>/ and copied to
    mutation-reports/latest/ (both git-ignored):
      mutation-report.html  open in a browser to inspect every mutant
      mutation-report.json  machine-readable result
      summary.md            mutation score and mutant counts

    Configuration (project, tests, exclusions, thresholds) lives in
    quality/mutation/stryker-config.json next to this script.

.EXAMPLE
    ./quality/mutation/run-mutation-tests.ps1
#>
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$service = Split-Path $repoRoot -Leaf
$config = Join-Path $PSScriptRoot 'stryker-config.json'
$reportsRoot = Join-Path $repoRoot 'mutation-reports'
$runName = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$runDir = Join-Path $reportsRoot $runName
$latestDir = Join-Path $reportsRoot 'latest'

Push-Location $repoRoot
try {
    Write-Host "Mutation testing $service -> mutation-reports/$runName" -ForegroundColor Cyan

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed (exit code $LASTEXITCODE)." }

    dotnet stryker --config-file $config --output $runDir
    $strykerExit = $LASTEXITCODE

    $jsonReport = Join-Path $runDir 'reports\mutation-report.json'
    if (-not (Test-Path $jsonReport)) { throw "Stryker produced no report (exit code $strykerExit)." }

    # Flatten the report so every run folder has the same layout as latest/.
    Get-ChildItem (Join-Path $runDir 'reports') | Move-Item -Destination $runDir -Force
    Remove-Item (Join-Path $runDir 'reports') -Recurse -Force

    # Mutation score as Stryker defines it: detected / (detected + undetected),
    # where detected = Killed + Timeout and undetected = Survived + NoCoverage.
    $report = Get-Content (Join-Path $runDir 'mutation-report.json') -Raw | ConvertFrom-Json
    $counts = @{}
    foreach ($file in $report.files.PSObject.Properties) {
        foreach ($mutant in $file.Value.mutants) {
            $counts[$mutant.status] = 1 + [int]$counts[$mutant.status]
        }
    }
    $killed = [int]$counts['Killed']; $timeout = [int]$counts['Timeout']
    $survived = [int]$counts['Survived']; $noCoverage = [int]$counts['NoCoverage']
    $detected = $killed + $timeout
    $score = if (($detected + $survived + $noCoverage) -gt 0) { [math]::Round(100 * $detected / ($detected + $survived + $noCoverage), 2) } else { 0 }
    $coveredScore = if (($detected + $survived) -gt 0) { [math]::Round(100 * $detected / ($detected + $survived), 2) } else { 0 }
    $thresholds = $report.thresholds
    $rating = if ($score -ge $thresholds.high) { 'High' } elseif ($score -ge $thresholds.low) { 'Acceptable' } else { 'Low' }

    $commit = (git rev-parse --short HEAD) 2>$null
    $strykerVersion = (Get-Content (Join-Path $repoRoot 'dotnet-tools.json') -Raw | ConvertFrom-Json).tools.'dotnet-stryker'.version
    $summary = @"
## Mutation testing: $service

| Metric | Value |
|---|---|
| Mutation score | **$score%** ($rating; high >= $($thresholds.high)%, low >= $($thresholds.low)%) |
| Score on covered code | $coveredScore% |
| Killed | $killed |
| Timeout (counted as detected) | $timeout |
| Survived | $survived |
| No coverage | $noCoverage |
| Compile errors | $([int]$counts['CompileError']) |
| Ignored | $([int]$counts['Ignored']) |

Commit ``$commit``, run $runName, Stryker.NET $strykerVersion. Open ``mutation-report.html`` for every mutant.
"@
    [System.IO.File]::WriteAllText((Join-Path $runDir 'summary.md'), $summary, (New-Object System.Text.UTF8Encoding $false))

    if (Test-Path $latestDir) { Remove-Item $latestDir -Recurse -Force }
    Copy-Item $runDir $latestDir -Recurse

    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }

    Write-Host ''
    Write-Host $summary
    Write-Host ''
    Write-Host "HTML report: $(Join-Path $latestDir 'mutation-report.html')" -ForegroundColor Green
    exit $strykerExit
}
finally {
    Pop-Location
}
