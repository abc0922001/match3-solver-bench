# Shared synthetic dataset helpers for E2E tests (no .csproj files).
Set-StrictMode -Version Latest

function New-E2EZeroMatchBoard {
    [CmdletBinding()]
    param(
        [int]$Variant = 0
    )

    # Cyclic shift of W F G L D H across 5x6 guarantees zero horizontal or vertical >=3 matches.
    $alphabet = @('W', 'F', 'G', 'L', 'D', 'H')
    $chars = [System.Text.StringBuilder]::new(30)
    for ($r = 0; $r -lt 5; $r++) {
        for ($c = 0; $c -lt 6; $c++) {
            $idx = ($r + $c + $Variant) % 6
            [void]$chars.Append($alphabet[$idx])
        }
    }
    return $chars.ToString()
}

function Get-E2EMinimalPuzzle {
    [CmdletBinding()]
    param(
        [string]$Id = 'T1-E2E-01',
        [string]$Tier = 'T1',
        [string]$Description = 'Synthetic E2E puzzle',
        [string]$Board = '',
        [object[]]$ObstacleCoords = @(),
        [object[]]$PuzzleCoords = @(),
        [string]$PuzzleColor = 'Empty',
        [int]$MaxSteps = 20,
        [bool]$AllowDiagonal = $false,
        [int]$Seed = 20260101,
        [int]$TimeBudgetMs = 1000,
        [string[]]$RequiredColors = @(),
        [bool]$RequiredColorsFirstWaveOnly = $false,
        [string[]]$RequireAoeColors = @(),
        [hashtable]$ColorWeights = $null
    )

    $normalizedTier = if ([string]::IsNullOrWhiteSpace($Tier)) {
        'T1'
    }
    else {
        $trimmedTier = $Tier.Trim()
        if ($trimmedTier -ieq 'Warmup' -or $trimmedTier -eq '0') {
            'Warmup'
        }
        elseif ($trimmedTier -match '^[Tt]?([1-5])$') {
            "T$($Matches[1])"
        }
        else {
            $trimmedTier
        }
    }

    $boardVariant = switch ($normalizedTier) {
        'Warmup' { 0 }
        'T1'     { 0 }
        'T2'     { 1 }
        'T3'     { 2 }
        'T4'     { 3 }
        'T5'     { 4 }
        default  { 0 }
    }

    if ([string]::IsNullOrEmpty($Board)) {
        $Board = New-E2EZeroMatchBoard -Variant $boardVariant
    }

    if ($null -eq $ColorWeights) {
        $ColorWeights = [ordered]@{
            Water = 1.0
            Fire  = 1.0
            Wood  = 1.0
            Light = 1.0
            Dark  = 1.0
            Heart = 1.0
        }
    }

    return [ordered]@{
        id                          = $Id
        tier                        = $normalizedTier
        description                 = $Description
        board                       = $Board
        obstacleCoords              = @($ObstacleCoords)
        puzzleCoords                = @($PuzzleCoords)
        puzzleColor                 = $PuzzleColor
        maxSteps                    = $MaxSteps
        allowDiagonal               = $AllowDiagonal
        seed                        = $Seed
        timeBudgetMs                = $TimeBudgetMs
        requiredColors              = @($RequiredColors)
        requiredColorsFirstWaveOnly = $RequiredColorsFirstWaveOnly
        requireAoeColors            = @($RequireAoeColors)
        colorWeights                = $ColorWeights
    }
}

function Get-E2EWarmupPuzzles {
    [CmdletBinding()]
    param()

    return @(
        (Get-E2EMinimalPuzzle -Id 'WARMUP-01' -Tier 'Warmup' -Description 'Warmup 1' -Board (New-E2EZeroMatchBoard -Variant 0) -Seed 1001 -TimeBudgetMs 500),
        (Get-E2EMinimalPuzzle -Id 'WARMUP-02' -Tier 'Warmup' -Description 'Warmup 2' -Board (New-E2EZeroMatchBoard -Variant 1) -Seed 1002 -TimeBudgetMs 500),
        (Get-E2EMinimalPuzzle -Id 'WARMUP-03' -Tier 'Warmup' -Description 'Warmup 3' -Board (New-E2EZeroMatchBoard -Variant 2) -Seed 1003 -TimeBudgetMs 500)
    )
}
