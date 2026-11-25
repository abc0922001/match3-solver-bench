#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('1', '2', '3', '4', 'All')]
    [string]$Tier = 'All',

    [string[]]$Filter = @(),

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild,

    [switch]$KeepTempOnFailure,

    [string]$JsonReportPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$HarnessPath = Join-Path $PSScriptRoot 'E2E.Harness.psm1'
$FixturesDir = Join-Path $PSScriptRoot 'fixtures'
$CasesDir = Join-Path $PSScriptRoot 'cases'
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

Import-Module $HarnessPath -Force
Clear-E2ETests
Clear-TrackedE2ETempWorkspaces

if (Test-Path -LiteralPath $FixturesDir -PathType Container) {
    $fixtureScripts = @(Get-ChildItem -LiteralPath $FixturesDir -Filter '*.ps1' -File | Sort-Object Name)
    foreach ($fs in $fixtureScripts) {
        . $fs.FullName
    }
}

if (Test-Path -LiteralPath $CasesDir -PathType Container) {
    $caseScripts = @(Get-ChildItem -LiteralPath $CasesDir -Filter '*.ps1' -File | Sort-Object Name)
    foreach ($cs in $caseScripts) {
        . $cs.FullName
    }
}

$allTests = @(Get-E2ETests)

$tierFiltered = if ($Tier -eq 'All') {
    $allTests
}
else {
    $targetTier = [int]$Tier
    @($allTests | Where-Object { $_.Tier -eq $targetTier })
}

$patterns = [System.Collections.Generic.List[string]]::new()
foreach ($rawPattern in @($Filter)) {
    if ([string]::IsNullOrWhiteSpace($rawPattern)) { continue }
    foreach ($part in ($rawPattern -split ',')) {
        $trimmed = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
            $patterns.Add($trimmed)
        }
    }
}

$selectedTests = if ($patterns.Count -eq 0) {
    $tierFiltered
}
else {
    @(
        $tierFiltered | Where-Object {
            $t = $_
            $matched = $false
            foreach ($p in $patterns) {
                $likeExpr = if ($p -match '[*?]') { $p } else { "*$p*" }
                if (($t.Id -like $likeExpr) -or ($t.Name -like $likeExpr) -or ($t.Feature -like $likeExpr)) {
                    $matched = $true
                    break
                }
            }
            $matched
        }
    )
}

function Write-E2EJsonSummary {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Total,
        [Parameter(Mandatory)][int]$Passed,
        [Parameter(Mandatory)][int]$Failed,
        [Parameter(Mandatory)][long]$DurationMs,
        [AllowEmptyCollection()][Parameter(Mandatory)][object[]]$Results
    )

    $resolvedPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $dir = Split-Path -Parent $resolvedPath
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        $null = New-Item -ItemType Directory -Path $dir -Force
    }

    $payload = [ordered]@{
        TimestampUtc  = [DateTime]::UtcNow.ToString('o')
        Configuration = $Configuration
        Tier          = $Tier
        Filter        = @($patterns.ToArray())
        Total         = $Total
        Passed        = $Passed
        Failed        = $Failed
        DurationMs    = $DurationMs
        Results       = @($Results)
    }

    $json = ConvertTo-Json -InputObject $payload -Depth 10
    [System.IO.File]::WriteAllText($resolvedPath, $json, $Utf8NoBom)
}

if (@($selectedTests).Count -eq 0) {
    Write-Host "[ERROR] No E2E tests matched Tier='$Tier' Filter='$($patterns -join ',')'." -ForegroundColor Red
    if (-not [string]::IsNullOrWhiteSpace($JsonReportPath)) {
        Write-E2EJsonSummary -Path $JsonReportPath -Total 0 -Passed 0 -Failed 0 -DurationMs 0 -Results @()
    }
    exit 2
}

if (-not $SkipBuild) {
    $slnPath = Join-Path $RepoRoot 'match3-solver-bench.sln'
    Write-Host "[BUILD] Building '$slnPath' ($Configuration)..."
    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $slnPath, '-c', $Configuration, '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $RepoRoot `
        -TimeoutMs 180000

    if ($buildRes.TimedOut -or $buildRes.ExitCode -ne 0) {
        Write-Host "[ERROR] Solution build failed (ExitCode=$($buildRes.ExitCode), TimedOut=$($buildRes.TimedOut)):" -ForegroundColor Red
        Write-Host $buildRes.CombinedOutput
        if (-not [string]::IsNullOrWhiteSpace($JsonReportPath)) {
            Write-E2EJsonSummary -Path $JsonReportPath -Total @($selectedTests).Count -Passed 0 -Failed @($selectedTests).Count -DurationMs $buildRes.DurationMs -Results @()
        }
        exit 1
    }
    Write-Host "[BUILD] Solution build succeeded [$($buildRes.DurationMs) ms]."
}

$Context = [PSCustomObject]@{
    RepoRoot          = $RepoRoot
    E2ERoot           = $PSScriptRoot
    FixturesDir       = $FixturesDir
    Configuration     = $Configuration
    SkipBuild         = [bool]$SkipBuild
    KeepTempOnFailure = [bool]$KeepTempOnFailure
}

$results = [System.Collections.Generic.List[object]]::new()
$overallSw = [System.Diagnostics.Stopwatch]::StartNew()
$passedCount = 0
$failedCount = 0

foreach ($test in $selectedTests) {
    $beforeWorkspaces = @(Get-TrackedE2ETempWorkspaces)
    $caseSw = [System.Diagnostics.Stopwatch]::StartNew()
    $passed = $false
    $errorMsg = ''

    try {
        & $test.ScriptBlock $Context
        $passed = $true
        $passedCount++
    }
    catch {
        $passed = $false
        $failedCount++
        $errorMsg = $_.Exception.Message
    }
    finally {
        $caseSw.Stop()
        $afterWorkspaces = @(Get-TrackedE2ETempWorkspaces)
        $createdByTest = @($afterWorkspaces | Where-Object { $beforeWorkspaces -notcontains $_ })
        foreach ($wsPath in $createdByTest) {
            if ($KeepTempOnFailure -and -not $passed) {
                Write-Host "[KEEP-TEMP] $wsPath" -ForegroundColor Yellow
                Clear-TrackedE2ETempWorkspaces -Paths @($wsPath)
            }
            else {
                try {
                    Remove-E2ETempWorkspace -Workspace $wsPath
                }
                catch {
                    Write-Warning "Cleanup failed for '$wsPath': $($_.Exception.Message)"
                }
            }
        }
    }

    if ($passed) {
        Write-Host "[PASS] $($test.Id) ($($test.Feature) | Tier $($test.Tier)) $($test.Name) [$($caseSw.ElapsedMilliseconds) ms]" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $($test.Id) ($($test.Feature) | Tier $($test.Tier)) $($test.Name) [$($caseSw.ElapsedMilliseconds) ms]" -ForegroundColor Red
        Write-Host "       $errorMsg" -ForegroundColor Red
    }

    $results.Add([ordered]@{
        Id         = $test.Id
        Tier       = $test.Tier
        Feature    = $test.Feature
        Name       = $test.Name
        Passed     = $passed
        DurationMs = $caseSw.ElapsedMilliseconds
        Error      = $errorMsg
    })
}

$overallSw.Stop()
$totalCount = $results.Count

Write-Host ""
Write-Host "Total: $totalCount | Passed: $passedCount | Failed: $failedCount | Duration: $($overallSw.ElapsedMilliseconds) ms"

if (-not [string]::IsNullOrWhiteSpace($JsonReportPath)) {
    Write-E2EJsonSummary -Path $JsonReportPath -Total $totalCount -Passed $passedCount -Failed $failedCount -DurationMs $overallSw.ElapsedMilliseconds -Results $results.ToArray()
}

if ($totalCount -gt 0 -and $failedCount -eq 0) {
    exit 0
}
else {
    exit 1
}
