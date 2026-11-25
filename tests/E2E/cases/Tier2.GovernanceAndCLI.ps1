#Requires -Version 7.0
Set-StrictMode -Version Latest

# ==============================================================================
# Tier 2: Negative / Boundary / Error E2E Tests (F01-F05, F15 -> 30 cases)
# ==============================================================================

# --- F01: Root Governance & .gitignore (5 cases) ---

Register-E2ETest -Id 'T2-F01-01' -Tier 2 -Feature 'F01' -Name 'CentralPackageVersionOverrideEnabled=false rejects PackageReference Version override' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'CpmOverrideNs'; CLASS_NAME = 'CpmOverrideApp'; SOLVER_NAME = 'CpmOverrideApp'; MODEL_TAG = 'cpm'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }

    # Place outside governance/solvers so CPM NU1008/NU1011/NU1013 triggers cleanly at restore time.
    $probeDir = Join-Path $ws.GovernanceDir 'CpmOverrideProbe'
    $res = New-E2ETempSolver -Workspace $ws -Name 'CpmOverrideProbe' -SourceCode $code -TargetDirectory $probeDir -PackageReferences @('xunit@2.9.2') -Configuration Release -AllowBuildFailure
    Assert-False $res.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res.BuildResult.ExitCode -ne 0) "Expected CPM version override to fail, got ExitCode=0:`n$($res.BuildResult.CombinedOutput)"
    Assert-Match -Text $res.BuildResult.CombinedOutput -Pattern 'NU10(08|11|13)' -Message 'Expected NuGet CPM error code (NU1008/NU1011/NU1013).'
}

Register-E2ETest -Id 'T2-F01-02' -Tier 2 -Feature 'F01' -Name 'Repo root contains exactly one .sln and zero .slnx files' -ScriptBlock {
    param($ctx)

    $slnFiles = @(Get-ChildItem -LiteralPath $ctx.RepoRoot -Filter '*.sln' -File)
    $slnxFiles = @(Get-ChildItem -LiteralPath $ctx.RepoRoot -Filter '*.slnx' -File)

    Assert-Equal 1 $slnFiles.Count "Expected exactly 1 .sln file at repo root, found $($slnFiles.Count)."
    Assert-Equal 'match3-solver-bench.sln' $slnFiles[0].Name
    Assert-Equal 0 $slnxFiles.Count "Expected 0 .slnx files at repo root, found $($slnxFiles.Count)."

    $listRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @('sln', $slnFiles[0].FullName, 'list') -WorkingDirectory $ctx.RepoRoot
    Assert-Equal 0 $listRes.ExitCode "dotnet sln list failed:`n$($listRes.CombinedOutput)"
    Assert-Contains $listRes.Stdout 'OrbSolver.Contracts.csproj' 'Expected OrbSolver.Contracts.csproj in .sln.'
}

Register-E2ETest -Id 'T2-F01-03' -Tier 2 -Feature 'F01' -Name 'Intentional failing tests/Fixtures projects are excluded from .sln and do not break Release build' -ScriptBlock {
    param($ctx)

    $fixturesRoot = Join-Path $ctx.RepoRoot 'tests\Fixtures'
    $fixtureProjs = @(
        if (Test-Path -LiteralPath $fixturesRoot -PathType Container) {
            Get-ChildItem -LiteralPath $fixturesRoot -Recurse -Filter '*.csproj' -File
        }
    )

    if ($fixtureProjs.Count -eq 0) {
        throw "[PREREQUISITE_MISSING] Expected >= 1 intentional failing fixture .csproj under '$fixturesRoot'."
    }

    $slnPath = Join-Path $ctx.RepoRoot 'match3-solver-bench.sln'
    $listRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @('sln', $slnPath, 'list') -WorkingDirectory $ctx.RepoRoot
    Assert-Equal 0 $listRes.ExitCode
    Assert-False ($listRes.Stdout -match 'Fixtures') "tests/Fixtures projects must NOT be registered in '$slnPath'."

    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $slnPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $ctx.RepoRoot `
        -TimeoutMs 180000
    Assert-Equal 0 $buildRes.ExitCode "Root solution Release build failed:`n$($buildRes.CombinedOutput)"
}

Register-E2ETest -Id 'T2-F01-04' -Tier 2 -Feature 'F01' -Name 'Generated report artifacts under reports/ and .trx files are ignored by git' -ScriptBlock {
    param($ctx)

    $probePaths = @(
        'reports/benchmark_report_20260101.json',
        'reports/benchmark_report_20260101.md',
        'reports/sub/latest.md',
        'test.trx'
    )

    $ignoreRes = Invoke-NativeProcess -FilePath 'git' `
        -ArgumentList (@('-C', $ctx.RepoRoot, 'check-ignore', '-v') + $probePaths) `
        -WorkingDirectory $ctx.RepoRoot

    Assert-Equal 0 $ignoreRes.ExitCode "git check-ignore failed:`n$($ignoreRes.CombinedOutput)"
    foreach ($p in $probePaths) {
        Assert-Contains $ignoreRes.Stdout $p "Expected '$p' to match .gitignore."
    }

    $statusRes = Invoke-NativeProcess -FilePath 'git' -ArgumentList @('-C', $ctx.RepoRoot, 'status', '--porcelain', 'reports') -WorkingDirectory $ctx.RepoRoot
    Assert-Equal 0 $statusRes.ExitCode
    Assert-Equal '' $statusRes.Stdout.Trim() "Expected zero untracked/modified files under reports/, got:`n$($statusRes.Stdout)"
}

Register-E2ETest -Id 'T2-F01-05' -Tier 2 -Feature 'F01' -Name 'Root Directory.Build.props AllowUnsafeBlocks=false blocks unsafe code with CS0227' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $code = Get-E2EFixtureContent -FileName 'UnsafeSolver.cs.tpl' -Tokens @{
        NAMESPACE   = 'UnsafePropsNs'
        CLASS_NAME  = 'UnsafePropsProbe'
        SOLVER_NAME = 'UnsafePropsProbe'
        MODEL_TAG   = 'unsafe-props'
    }

    # Place outside governance/solvers so Directory.Build.props (not EnforceSolverPolicy) causes CS0227.
    $probeDir = Join-Path $ws.GovernanceDir 'UnsafePropsProbe'
    $res = New-E2ETempSolver -Workspace $ws -Name 'UnsafePropsProbe' -SourceCode $code -TargetDirectory $probeDir -AllowUnsafeBlocks $false -Configuration Release -AllowBuildFailure
    Assert-False $res.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res.BuildResult.ExitCode -ne 0) "Expected unsafe code to fail under Directory.Build.props, got ExitCode=0."
    Assert-Match -Text $res.BuildResult.CombinedOutput -Pattern 'CS0227' -Message 'Expected compiler error CS0227 when AllowUnsafeBlocks=false.'
}

# --- F02: Anti-Cheat MSBuild Policy (5 cases) ---

Register-E2ETest -Id 'T2-F02-01' -Tier 2 -Feature 'F02' -Name 'EnforceSolverPolicy blocks external NuGet PackageReference with [Anti-Cheat]' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'CheatNugetNs'; CLASS_NAME = 'CheatNuget'; SOLVER_NAME = 'CheatNuget'; MODEL_TAG = 'cheat-nuget'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }

    $res = New-E2ETempSolver -Workspace $ws -Name 'CheatNuget' -SourceCode $code -PlaceInGovernance -PackageReferences @('xunit') -Configuration Release -AllowBuildFailure
    Assert-False $res.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res.BuildResult.ExitCode -ne 0) 'Expected NuGet PackageReference in solver to fail build.'
    Assert-Contains $res.BuildResult.CombinedOutput '[Anti-Cheat]'
}

Register-E2ETest -Id 'T2-F02-02' -Tier 2 -Feature 'F02' -Name 'EnforceSolverPolicy blocks AllowUnsafeBlocks=true with [Anti-Cheat]' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $code = Get-E2EFixtureContent -FileName 'UnsafeSolver.cs.tpl' -Tokens @{
        NAMESPACE   = 'CheatUnsafeNs'
        CLASS_NAME  = 'CheatUnsafe'
        SOLVER_NAME = 'CheatUnsafe'
        MODEL_TAG   = 'cheat-unsafe'
    }

    $res = New-E2ETempSolver -Workspace $ws -Name 'CheatUnsafe' -SourceCode $code -PlaceInGovernance -AllowUnsafeBlocks $true -Configuration Release -AllowBuildFailure
    Assert-False $res.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res.BuildResult.ExitCode -ne 0) 'Expected AllowUnsafeBlocks=true in solver to fail build.'
    Assert-Contains $res.BuildResult.CombinedOutput '[Anti-Cheat]'
}

Register-E2ETest -Id 'T2-F02-03' -Tier 2 -Feature 'F02' -Name 'EnforceSolverPolicy blocks ProjectReference to OrbSolver.Core with [Anti-Cheat]' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance -IncludeCoreInGovernance
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'CheatCoreNs'; CLASS_NAME = 'CheatCore'; SOLVER_NAME = 'CheatCore'; MODEL_TAG = 'cheat-core'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }

    $res = New-E2ETempSolver -Workspace $ws -Name 'CheatCore' -SourceCode $code -PlaceInGovernance -ExtraProjectReferences @($ws.CoreCsproj) -Configuration Release -AllowBuildFailure
    Assert-False $res.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res.BuildResult.ExitCode -ne 0) 'Expected OrbSolver.Core ProjectReference in solver to fail build.'
    Assert-Contains $res.BuildResult.CombinedOutput '[Anti-Cheat]'
}

Register-E2ETest -Id 'T2-F02-04' -Tier 2 -Feature 'F02' -Name 'EnforceSolverPolicy blocks ProjectReference to another solver with [Anti-Cheat]' -ScriptBlock {
    param($ctx)

    $beamProj = Join-Path $ctx.RepoRoot 'solvers\Baseline.ReferenceBeam\Baseline.ReferenceBeam.csproj'
    if (-not (Test-Path -LiteralPath $beamProj -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$beamProj'."
    }

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $beamDestDir = Join-Path $ws.GovernanceSolversDir 'Baseline.ReferenceBeam'
    $srcBeamDir = Split-Path -Parent $beamProj

    foreach ($item in @(Get-ChildItem -LiteralPath $srcBeamDir -Force)) {
        if ($item.PSIsContainer -and ($item.Name -in @('bin', 'obj'))) { continue }
        if ($item.PSIsContainer) {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $beamDestDir $item.Name) -Recurse -Force
        }
        else {
            $null = New-Item -ItemType Directory -Path $beamDestDir -Force
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $beamDestDir $item.Name) -Force
        }
    }

    $tempBeamCsproj = Join-Path $beamDestDir 'Baseline.ReferenceBeam.csproj'
    # Update relative Contracts ProjectReference inside copied temp Baseline.ReferenceBeam if present
    $beamXmlText = [System.IO.File]::ReadAllText($tempBeamCsproj, [System.Text.Encoding]::UTF8)
    $beamXmlText = $beamXmlText -replace '(\.\.\\|\.\./)+src[/\\]OrbSolver\.Contracts[/\\]OrbSolver\.Contracts\.csproj', $ws.ContractsCsproj
    [System.IO.File]::WriteAllText($tempBeamCsproj, $beamXmlText, [System.Text.UTF8Encoding]::new($false))

    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'CheatPeerNs'; CLASS_NAME = 'CheatPeer'; SOLVER_NAME = 'CheatPeer'; MODEL_TAG = 'cheat-peer'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }

    $res = New-E2ETempSolver -Workspace $ws -Name 'CheatPeer' -SourceCode $code -PlaceInGovernance -ExtraProjectReferences @($tempBeamCsproj) -Configuration Release -AllowBuildFailure
    Assert-False $res.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res.BuildResult.ExitCode -ne 0) 'Expected cross-solver ProjectReference to fail build.'
    Assert-Contains $res.BuildResult.CombinedOutput '[Anti-Cheat]'
}

Register-E2ETest -Id 'T2-F02-05' -Tier 2 -Feature 'F02' -Name 'EnforceSolverPolicy flags NuGet, AllowUnsafeBlocks, and forbidden ProjectReference together' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance

    $helperCode = @'
namespace HelperLibNs;
public static class Helper { public static int Value => 42; }
'@
    $helperDir = Join-Path $ws.GovernanceDir 'HelperLib'
    $helper = New-E2ETempSolver -Workspace $ws -Name 'HelperLib' -SourceCode $helperCode -SourceFileName 'Helper.cs' -TargetDirectory $helperDir -SkipContractsReference -Configuration Release
    Assert-Equal 0 $helper.BuildResult.ExitCode

    $cheatCode = Get-E2EFixtureContent -FileName 'UnsafeSolver.cs.tpl' -Tokens @{
        NAMESPACE   = 'CheatMultiNs'
        CLASS_NAME  = 'CheatMulti'
        SOLVER_NAME = 'CheatMulti'
        MODEL_TAG   = 'cheat-multi'
    }

    $resAll3 = New-E2ETempSolver -Workspace $ws -Name 'CheatMulti3' -SourceCode $cheatCode -PlaceInGovernance -AllowUnsafeBlocks $true -PackageReferences @('xunit') -ExtraProjectReferences @($helper.CsprojPath) -Configuration Release -AllowBuildFailure
    Assert-False $resAll3.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($resAll3.BuildResult.ExitCode -ne 0) 'Expected CheatMulti3 build to fail.'
    Assert-Contains $resAll3.BuildResult.CombinedOutput '[Anti-Cheat]'
    Assert-Contains $resAll3.BuildResult.CombinedOutput 'NuGet'

    $res2 = New-E2ETempSolver -Workspace $ws -Name 'CheatMulti2' -SourceCode $cheatCode -PlaceInGovernance -AllowUnsafeBlocks $true -ExtraProjectReferences @($helper.CsprojPath) -Configuration Release -AllowBuildFailure
    Assert-False $res2.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res2.BuildResult.ExitCode -ne 0) 'Expected CheatMulti2 build to fail.'
    Assert-Contains $res2.BuildResult.CombinedOutput '[Anti-Cheat]'
    Assert-Contains $res2.BuildResult.CombinedOutput 'AllowUnsafeBlocks'

    $validCheatCode = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'CheatMulti1Ns'; CLASS_NAME = 'CheatMulti1'; SOLVER_NAME = 'CheatMulti1'; MODEL_TAG = 'cheat-multi-1'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $res1 = New-E2ETempSolver -Workspace $ws -Name 'CheatMulti1' -SourceCode $validCheatCode -PlaceInGovernance -AllowUnsafeBlocks $false -ExtraProjectReferences @($helper.CsprojPath) -Configuration Release -AllowBuildFailure
    Assert-False $res1.BuildResult.TimedOut 'dotnet build timed out.'
    Assert-True ($res1.BuildResult.ExitCode -ne 0) 'Expected CheatMulti1 build to fail.'
    Assert-Contains $res1.BuildResult.CombinedOutput '[Anti-Cheat]'
    Assert-Contains $res1.BuildResult.CombinedOutput 'may only reference OrbSolver.Contracts'
}

# --- F03: OrbSolver.Runner CLI Flags & Arg Validation (5 cases) ---

Register-E2ETest -Id 'T2-F03-01' -Tier 2 -Feature 'F03' -Name 'Runner rejects unknown CLI flag without writing reports' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'BadFlagNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'bad-flag'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $code -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -OutputDir $ws.OutputDir -RawArguments @('--solvers', $ws.SolversDir, '--datasets', $ws.DatasetsDir, '--output', $ws.OutputDir, '--unknown-flag') -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0) 'Expected unknown CLI flag to fail.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput)) 'Expected diagnostic error output.'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count 'Expected zero output files on CLI error.'
}

Register-E2ETest -Id 'T2-F03-02' -Tier 2 -Feature 'F03' -Name 'Runner rejects trailing flag missing its value argument' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -OutputDir $ws.OutputDir -RawArguments @('--datasets', $ws.DatasetsDir, '--output', $ws.OutputDir, '--solvers') -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0) 'Expected missing flag value to fail.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput)) 'Expected diagnostic error output.'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count 'Expected zero output files on CLI error.'
}

Register-E2ETest -Id 'T2-F03-03' -Tier 2 -Feature 'F03' -Name 'Runner rejects non-existent --solvers directory' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $missingSolvers = Join-Path $ws.Root 'missing_solvers'

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $missingSolvers -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0) 'Expected non-existent --solvers directory to fail.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput))
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count
}

Register-E2ETest -Id 'T2-F03-04' -Tier 2 -Feature 'F03' -Name 'Runner rejects non-existent --datasets directory' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'MissDsNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'miss-ds'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $code -Configuration Release
    $missingDatasets = Join-Path $ws.Root 'missing_datasets'

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $missingDatasets -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0) 'Expected non-existent --datasets directory to fail.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput))
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count
}

Register-E2ETest -Id 'T2-F03-05' -Tier 2 -Feature 'F03' -Name 'Runner fails cleanly when --filter matches zero solvers' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'NoMatchNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'no-match'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $code -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'NonExistentSolverName' -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0) 'Expected non-matching --filter to return non-zero.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput))
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count
}

# --- F04: Release Build Guard (#if DEBUG + --dev) (5 cases) ---

Register-E2ETest -Id 'T2-F04-01' -Tier 2 -Feature 'F04' -Name 'Debug Runner refuses execution without --dev and explains Release guard' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Debug -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'DbgBlockNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'dbg-block'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $code -Configuration Debug

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Debug -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0) 'Expected Debug Runner without --dev to exit non-zero.'
    Assert-Match -Text $res.CombinedOutput -Pattern '(?i)(release|--dev|debug)'
}

Register-E2ETest -Id 'T2-F04-02' -Tier 2 -Feature 'F04' -Name 'Debug Runner without --dev writes zero official report files' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Debug -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'DbgNoOutNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'dbg-noout'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $code -Configuration Debug

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Debug -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0)
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count 'Debug Runner without --dev must write zero report files.'
}

Register-E2ETest -Id 'T2-F04-03' -Tier 2 -Feature 'F04' -Name 'Debug Runner with --filter but without --dev is still blocked' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Debug -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'DbgFilterNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'dbg-filter'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $code -Configuration Debug

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'SolverA' -Configuration Debug -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0)
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count
}

Register-E2ETest -Id 'T2-F04-04' -Tier 2 -Feature 'F04' -Name 'Debug Runner rejects misspelled --deev flag without running benchmark' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Debug -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -OutputDir $ws.OutputDir -RawArguments @('--solvers', $ws.SolversDir, '--datasets', $ws.DatasetsDir, '--output', $ws.OutputDir, '--deev') -Configuration Debug -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0)
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count
}

Register-E2ETest -Id 'T2-F04-05' -Tier 2 -Feature 'F04' -Name 'Debug Runner with empty arguments is blocked with diagnostic message' -ScriptBlock {
    param($ctx)

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -RawArguments @() -Configuration Debug -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0)
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput))
}

# --- F05: Solver Scanning, --filter & Solver.Template Skip (5 cases) ---

Register-E2ETest -Id 'T2-F05-01' -Tier 2 -Feature 'F05' -Name 'Directory containing only Solver.Template leaves zero runnable solvers on default scan' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $tmplCode = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'OnlyTmplNs'; CLASS_NAME = 'TemplateSolver'; SOLVER_NAME = 'Solver.Template'; MODEL_TAG = 'template'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        throw new NotImplementedException();'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'Solver.Template' -SourceCode $tmplCode -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0) 'Expected non-zero exit when only Solver.Template exists without --filter.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput))
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count
}

Register-E2ETest -Id 'T2-F05-02' -Tier 2 -Feature 'F05' -Name 'Completely empty --solvers directory fails cleanly with diagnostic' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-True ($res.ExitCode -ne 0)
    Assert-True (-not [string]::IsNullOrWhiteSpace($res.CombinedOutput))
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $ws.OutputDir -Recurse -File).Count
}

Register-E2ETest -Id 'T2-F05-03' -Tier 2 -Feature 'F05' -Name 'Non-solver library DLL in --solvers directory does not crash Runner when valid solver exists' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $emptyLibCode = @'
namespace EmptyLibNs;
public sealed class NotASolver { public int Ping() => 1; }
'@
    $null = New-E2ETempSolver -Workspace $ws -Name 'EmptyLib' -SourceCode $emptyLibCode -SourceFileName 'NotASolver.cs' -SkipContractsReference -Configuration Release

    $validCode = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'ValidPeerNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'valid-peer'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $validCode -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner crashed when EmptyLib DLL was present:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    Assert-Contains $solvers 'SolverA'
}

Register-E2ETest -Id 'T2-F05-04' -Tier 2 -Feature 'F05' -Name 'Trailing comma and space in --filter handles input without unhandled exception' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    foreach ($name in @('SolverA', 'SolverB')) {
        $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
            USING_BLOCK = ''; NAMESPACE = "${name}CommaNs"; CLASS_NAME = $name; SOLVER_NAME = $name; MODEL_TAG = 'comma'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
        }
        $null = New-E2ETempSolver -Workspace $ws -Name $name -SourceCode $code -Configuration Release
    }

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'SolverA, ' -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-False ($res.CombinedOutput -match 'Unhandled exception|NullReferenceException') "Runner threw unhandled exception:`n$($res.CombinedOutput)"

    if ($res.ExitCode -eq 0) {
        $json = Get-ReportJson -Path $res.LatestJsonPath
        $solvers = @(Find-JsonSolverNames -ReportJson $json)
        Assert-Contains $solvers 'SolverA'
        Assert-False ($solvers -contains 'SolverB') 'SolverB should not appear when filtering SolverA.'
    }
}

Register-E2ETest -Id 'T2-F05-05' -Tier 2 -Feature 'F05' -Name 'Repo Solver.Template throws NotImplementedException yielding RE (0 score) when explicitly filtered' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $templateProj = Join-Path $ctx.RepoRoot 'solvers\Solver.Template\Solver.Template.csproj'
    if (-not (Test-Path -LiteralPath $templateProj -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$templateProj'."
    }

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir (Join-Path $ctx.RepoRoot 'solvers') -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'Solver.Template' -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner failed on filtered Solver.Template:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    $tmplName = @($solvers | Where-Object { $_ -match 'Template' }) | Select-Object -First 1
    Assert-True (-not [string]::IsNullOrWhiteSpace($tmplName)) 'Expected Solver.Template in JSON report.'

    $cases = @(Get-JsonSolverCases -ReportJson $json -SolverName $tmplName)
    Assert-GreaterThan $cases.Count 0
    foreach ($c in $cases) {
        $rawCase = ($c | ConvertTo-Json -Depth 10)
        Assert-Match -Text $rawCase -Pattern '"RE"'
        Assert-Match -Text $rawCase -Pattern '"(CaseScore|Score)"\s*:\s*0(\.0+)?\b'
    }
}

# --- F15: docs/prompt_template.md, README.md & OrbSolver.Contracts (5 cases) ---

Register-E2ETest -Id 'T2-F15-01' -Tier 2 -Feature 'F15' -Name 'BoardSnapshot rejects 29-cell array with ArgumentException(cells)' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $probeDir = Join-Path $ws.Root 'probe'
    $null = New-Item -ItemType Directory -Path $probeDir -Force

    $csproj = Get-E2EFixtureContent -FileName 'ConsoleProbeProjectTemplate.xml' -Tokens @{
        EXTRA_PROPS  = ''
        PROJECT_REFS = "  <ItemGroup>`n    <ProjectReference Include=`"$($ws.ContractsCsproj)`" />`n  </ItemGroup>"
    }
    $programCs = Get-E2EFixtureContent -FileName 'ContractsProbe.cs.tpl'
    $csprojPath = Join-Path $probeDir 'ContractsProbe.csproj'
    [System.IO.File]::WriteAllText($csprojPath, $csproj, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $probeDir 'Program.cs'), $programCs, [System.Text.UTF8Encoding]::new($false))

    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $csprojPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $probeDir
    Assert-Equal 0 $buildRes.ExitCode "ContractsProbe build failed:`n$($buildRes.CombinedOutput)"

    $dllPath = Join-Path $probeDir 'bin\Release\net10.0\ContractsProbe.dll'
    $runRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @($dllPath, '--mode', 'short-cells') -WorkingDirectory $probeDir
    Assert-Equal 0 $runRes.ExitCode "ContractsProbe short-cells failed:`n$($runRes.CombinedOutput)"
    Assert-Contains $runRes.Stdout 'PROBE_OK:short-cells'
}

Register-E2ETest -Id 'T2-F15-02' -Tier 2 -Feature 'F15' -Name 'BoardSnapshot rejects 31-cell array with ArgumentException(cells)' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $probeDir = Join-Path $ws.Root 'probe'
    $null = New-Item -ItemType Directory -Path $probeDir -Force

    $csproj = Get-E2EFixtureContent -FileName 'ConsoleProbeProjectTemplate.xml' -Tokens @{
        EXTRA_PROPS  = ''
        PROJECT_REFS = "  <ItemGroup>`n    <ProjectReference Include=`"$($ws.ContractsCsproj)`" />`n  </ItemGroup>"
    }
    $programCs = Get-E2EFixtureContent -FileName 'ContractsProbe.cs.tpl'
    $csprojPath = Join-Path $probeDir 'ContractsProbe.csproj'
    [System.IO.File]::WriteAllText($csprojPath, $csproj, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $probeDir 'Program.cs'), $programCs, [System.Text.UTF8Encoding]::new($false))

    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $csprojPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $probeDir
    Assert-Equal 0 $buildRes.ExitCode "ContractsProbe build failed:`n$($buildRes.CombinedOutput)"

    $dllPath = Join-Path $probeDir 'bin\Release\net10.0\ContractsProbe.dll'
    $runRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @($dllPath, '--mode', 'long-cells') -WorkingDirectory $probeDir
    Assert-Equal 0 $runRes.ExitCode "ContractsProbe long-cells failed:`n$($runRes.CombinedOutput)"
    Assert-Contains $runRes.Stdout 'PROBE_OK:long-cells'
}

Register-E2ETest -Id 'T2-F15-03' -Tier 2 -Feature 'F15' -Name 'BoardSnapshot rejects empty ReadOnlyMemory<OrbType> with ArgumentException(cells)' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $probeDir = Join-Path $ws.Root 'probe'
    $null = New-Item -ItemType Directory -Path $probeDir -Force

    $csproj = Get-E2EFixtureContent -FileName 'ConsoleProbeProjectTemplate.xml' -Tokens @{
        EXTRA_PROPS  = ''
        PROJECT_REFS = "  <ItemGroup>`n    <ProjectReference Include=`"$($ws.ContractsCsproj)`" />`n  </ItemGroup>"
    }
    $programCs = Get-E2EFixtureContent -FileName 'ContractsProbe.cs.tpl'
    $csprojPath = Join-Path $probeDir 'ContractsProbe.csproj'
    [System.IO.File]::WriteAllText($csprojPath, $csproj, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $probeDir 'Program.cs'), $programCs, [System.Text.UTF8Encoding]::new($false))

    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $csprojPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $probeDir
    Assert-Equal 0 $buildRes.ExitCode "ContractsProbe build failed:`n$($buildRes.CombinedOutput)"

    $dllPath = Join-Path $probeDir 'bin\Release\net10.0\ContractsProbe.dll'
    $runRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @($dllPath, '--mode', 'empty-cells') -WorkingDirectory $probeDir
    Assert-Equal 0 $runRes.ExitCode "ContractsProbe empty-cells failed:`n$($runRes.CombinedOutput)"
    Assert-Contains $runRes.Stdout 'PROBE_OK:empty-cells'
}

Register-E2ETest -Id 'T2-F15-04' -Tier 2 -Feature 'F15' -Name 'docs/prompt_template.md explicitly states NuGet, unsafe, Core, and static caching prohibitions' -ScriptBlock {
    param($ctx)

    $promptPath = Join-Path $ctx.RepoRoot 'docs\prompt_template.md'
    if (-not (Test-Path -LiteralPath $promptPath -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$promptPath'."
    }

    $text = [System.IO.File]::ReadAllText($promptPath, [System.Text.Encoding]::UTF8)
    Assert-Match -Text $text -Pattern '(?i)(nuget|package)' -Message 'Missing NuGet prohibition in docs/prompt_template.md.'
    Assert-Match -Text $text -Pattern '(?i)(unsafe|AllowUnsafeBlocks)' -Message 'Missing unsafe prohibition in docs/prompt_template.md.'
    Assert-Match -Text $text -Pattern 'OrbSolver\.Core' -Message 'Missing OrbSolver.Core prohibition in docs/prompt_template.md.'
    Assert-Match -Text $text -Pattern '(?i)static' -Message 'Missing static cross-call caching prohibition in docs/prompt_template.md.'
}

Register-E2ETest -Id 'T2-F15-05' -Tier 2 -Feature 'F15' -Name 'BoardSnapshot.Cells is immutable ReadOnlySpan<OrbType> and rejects mutation with CS0200' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance

    # 1. Runtime/reflection check for ReadOnlySpan<OrbType>
    $probeDir = Join-Path $ws.Root 'probe'
    $null = New-Item -ItemType Directory -Path $probeDir -Force
    $csproj = Get-E2EFixtureContent -FileName 'ConsoleProbeProjectTemplate.xml' -Tokens @{
        EXTRA_PROPS  = ''
        PROJECT_REFS = "  <ItemGroup>`n    <ProjectReference Include=`"$($ws.ContractsCsproj)`" />`n  </ItemGroup>"
    }
    $programCs = Get-E2EFixtureContent -FileName 'ContractsProbe.cs.tpl'
    $csprojPath = Join-Path $probeDir 'ContractsProbe.csproj'
    [System.IO.File]::WriteAllText($csprojPath, $csproj, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $probeDir 'Program.cs'), $programCs, [System.Text.UTF8Encoding]::new($false))

    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $csprojPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $probeDir
    Assert-Equal 0 $buildRes.ExitCode "ContractsProbe build failed:`n$($buildRes.CombinedOutput)"

    $dllPath = Join-Path $probeDir 'bin\Release\net10.0\ContractsProbe.dll'
    $runRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @($dllPath, '--mode', 'readonlyspan-contract') -WorkingDirectory $probeDir
    Assert-Equal 0 $runRes.ExitCode "ContractsProbe readonlyspan-contract failed:`n$($runRes.CombinedOutput)"
    Assert-Contains $runRes.Stdout 'PROBE_OK:readonlyspan-contract'

    # 2. Negative compile check verifying mutation fails with CS0200
    $mutDir = Join-Path $ws.Root 'mutation_probe'
    $null = New-Item -ItemType Directory -Path $mutDir -Force
    $mutCsproj = Get-E2EFixtureContent -FileName 'ConsoleProbeProjectTemplate.xml' -Tokens @{
        EXTRA_PROPS  = ''
        PROJECT_REFS = "  <ItemGroup>`n    <ProjectReference Include=`"$($ws.ContractsCsproj)`" />`n  </ItemGroup>"
    }
    $mutCs = Get-E2EFixtureContent -FileName 'ContractsMutationProbe.cs.tpl'
    $mutCsprojPath = Join-Path $mutDir 'ContractsMutationProbe.csproj'
    [System.IO.File]::WriteAllText($mutCsprojPath, $mutCsproj, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $mutDir 'Program.cs'), $mutCs, [System.Text.UTF8Encoding]::new($false))

    $mutBuildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $mutCsprojPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $mutDir
    Assert-False $mutBuildRes.TimedOut 'dotnet build timed out.'
    Assert-True ($mutBuildRes.ExitCode -ne 0) 'Expected mutating BoardSnapshot.Cells[0] to fail compilation.'
    Assert-Match -Text $mutBuildRes.CombinedOutput -Pattern 'CS(0200|8331)' -Message 'Expected CS0200/CS8331 read-only indexer compile error.'
}
