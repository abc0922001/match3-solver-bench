#Requires -Version 7.0
Set-StrictMode -Version Latest

# ==============================================================================
# Tier 1: Positive / Happy-Path E2E Tests (F01-F05, F15 -> 30 cases)
# ==============================================================================

# --- F01: Root Governance & .gitignore (5 cases) ---

Register-E2ETest -Id 'T1-F01-01' -Tier 1 -Feature 'F01' -Name 'Root .sln exists in classic format and builds cleanly in Release' -ScriptBlock {
    param($ctx)

    $slnPath = Join-Path $ctx.RepoRoot 'match3-solver-bench.sln'
    $slnxPath = Join-Path $ctx.RepoRoot 'match3-solver-bench.slnx'

    Assert-True (Test-Path -LiteralPath $slnPath -PathType Leaf) "Expected solution file '$slnPath' to exist."
    Assert-False (Test-Path -LiteralPath $slnxPath) "Did not expect '.slnx' file '$slnxPath' to exist."

    $slnText = [System.IO.File]::ReadAllText($slnPath, [System.Text.Encoding]::UTF8)
    Assert-Match -Text $slnText -Pattern 'Format Version 12\.00' -Message 'Classic .sln Format Version 12.00 header missing.'

    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $slnPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $ctx.RepoRoot `
        -TimeoutMs 180000

    Assert-False $buildRes.TimedOut 'dotnet build timed out.'
    Assert-Equal 0 $buildRes.ExitCode "dotnet build -c Release failed:`n$($buildRes.CombinedOutput)"
}

Register-E2ETest -Id 'T1-F01-02' -Tier 1 -Feature 'F01' -Name 'Solution test projects execute and pass via dotnet test -c Release' -ScriptBlock {
    param($ctx)

    $slnPath = Join-Path $ctx.RepoRoot 'match3-solver-bench.sln'
    $slnListRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @('sln', $slnPath, 'list') -WorkingDirectory $ctx.RepoRoot
    Assert-Equal 0 $slnListRes.ExitCode "dotnet sln list failed:`n$($slnListRes.CombinedOutput)"

    $testProjects = @($slnListRes.Stdout -split "`r?`n" | Where-Object { $_ -match 'Tests\.csproj\s*$' })
    if ($testProjects.Count -eq 0) {
        throw "[PREREQUISITE_MISSING] match3-solver-bench.sln does not yet register any *Tests.csproj test projects."
    }

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $trxPath = Join-Path $ws.OutputDir 'e2e_sln_test.trx'

    $testRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('test', $slnPath, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo', '--logger', "trx;LogFileName=$trxPath") `
        -WorkingDirectory $ctx.RepoRoot `
        -TimeoutMs 180000

    Assert-False $testRes.TimedOut 'dotnet test timed out.'
    Assert-Equal 0 $testRes.ExitCode "dotnet test -c Release failed:`n$($testRes.CombinedOutput)"
    Assert-True (Test-Path -LiteralPath $trxPath -PathType Leaf) "Expected TRX log at '$trxPath'."

    [xml]$trxXml = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trxXml.TestRun.ResultSummary.Counters
    Assert-True ($null -ne $counters) 'Missing ResultSummary.Counters in TRX XML.'
    Assert-GreaterThan ([int]$counters.total) 0 'Expected total executed unit/security tests > 0 in TRX.'
    Assert-Equal 0 ([int]$counters.failed) "Expected 0 failed tests in TRX, got $($counters.failed)."
}

Register-E2ETest -Id 'T1-F01-03' -Tier 1 -Feature 'F01' -Name '.gitignore ignores bin/, obj/, .vs/, and reports/' -ScriptBlock {
    param($ctx)

    $gitignorePath = Join-Path $ctx.RepoRoot '.gitignore'
    Assert-True (Test-Path -LiteralPath $gitignorePath -PathType Leaf) "Expected '.gitignore' at '$gitignorePath'."

    $probePaths = @('bin/probe.dll', 'obj/probe.o', '.vs/probe.suo', 'reports/probe_report.md')
    $gitRes = Invoke-NativeProcess -FilePath 'git' `
        -ArgumentList (@('-C', $ctx.RepoRoot, 'check-ignore', '-v') + $probePaths) `
        -WorkingDirectory $ctx.RepoRoot

    Assert-Equal 0 $gitRes.ExitCode "git check-ignore failed:`n$($gitRes.CombinedOutput)"
    foreach ($p in $probePaths) {
        Assert-Contains $gitRes.Stdout $p "Expected '$p' to be ignored by .gitignore."
    }
}

Register-E2ETest -Id 'T1-F01-04' -Tier 1 -Feature 'F01' -Name 'Directory.Build.props enforces net10.0, Nullable=enable, AllowUnsafeBlocks=false, Deterministic=true' -ScriptBlock {
    param($ctx)

    $propsPath = Join-Path $ctx.RepoRoot 'Directory.Build.props'
    Assert-True (Test-Path -LiteralPath $propsPath -PathType Leaf) "Missing '$propsPath'."

    $contractsCsproj = Join-Path $ctx.RepoRoot 'src\OrbSolver.Contracts\OrbSolver.Contracts.csproj'
    $msbuildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('msbuild', $contractsCsproj, '-getProperty:TargetFramework', '-getProperty:Nullable', '-getProperty:AllowUnsafeBlocks', '-getProperty:Deterministic') `
        -WorkingDirectory $ctx.RepoRoot

    Assert-Equal 0 $msbuildRes.ExitCode "dotnet msbuild -getProperty failed:`n$($msbuildRes.CombinedOutput)"
    $parsed = $msbuildRes.Stdout | ConvertFrom-Json -Depth 10
    Assert-Equal 'net10.0' $parsed.Properties.TargetFramework 'TargetFramework mismatch.'
    Assert-Equal 'enable' $parsed.Properties.Nullable 'Nullable mismatch.'
    Assert-Equal 'false' $parsed.Properties.AllowUnsafeBlocks 'AllowUnsafeBlocks mismatch.'
    Assert-Equal 'true' $parsed.Properties.Deterministic 'Deterministic mismatch.'
}

Register-E2ETest -Id 'T1-F01-05' -Tier 1 -Feature 'F01' -Name 'Directory.Packages.props enables CPM with pinned xUnit and Test SDK versions' -ScriptBlock {
    param($ctx)

    $packagesPropsPath = Join-Path $ctx.RepoRoot 'Directory.Packages.props'
    Assert-True (Test-Path -LiteralPath $packagesPropsPath -PathType Leaf) "Missing '$packagesPropsPath'."

    $contractsCsproj = Join-Path $ctx.RepoRoot 'src\OrbSolver.Contracts\OrbSolver.Contracts.csproj'
    $msbuildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('msbuild', $contractsCsproj, '-getProperty:ManagePackageVersionsCentrally', '-getProperty:CentralPackageVersionOverrideEnabled') `
        -WorkingDirectory $ctx.RepoRoot

    Assert-Equal 0 $msbuildRes.ExitCode "dotnet msbuild -getProperty failed:`n$($msbuildRes.CombinedOutput)"
    $parsed = $msbuildRes.Stdout | ConvertFrom-Json -Depth 10
    Assert-Equal 'true' $parsed.Properties.ManagePackageVersionsCentrally 'ManagePackageVersionsCentrally must be true.'
    Assert-Equal 'false' $parsed.Properties.CentralPackageVersionOverrideEnabled 'CentralPackageVersionOverrideEnabled must be false.'

    [xml]$pkgXml = Get-Content -LiteralPath $packagesPropsPath -Raw
    $pkgMap = @{}
    foreach ($pv in @($pkgXml.Project.ItemGroup.PackageVersion)) {
        if ($null -ne $pv) {
            $pkgMap[$pv.Include] = $pv.Version
        }
    }

    Assert-Equal '2.9.3' $pkgMap['xunit'] 'Pinned xunit version mismatch.'
    Assert-Equal '3.0.2' $pkgMap['xunit.runner.visualstudio'] 'Pinned xunit.runner.visualstudio version mismatch.'
    Assert-Equal '17.13.0' $pkgMap['Microsoft.NET.Test.Sdk'] 'Pinned Microsoft.NET.Test.Sdk version mismatch.'
}

# --- F02: Anti-Cheat MSBuild Policy (5 cases) ---

Register-E2ETest -Id 'T1-F02-01' -Tier 1 -Feature 'F02' -Name 'Valid solver referencing only OrbSolver.Contracts builds under governance targets' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK    = ''
        NAMESPACE      = 'ValidSolverNs'
        CLASS_NAME     = 'ValidSolver'
        SOLVER_NAME    = 'ValidSolver'
        MODEL_TAG      = 'e2e-valid'
        EXECUTION_MODE = 'ExecutionMode.SingleThread'
        SOLVE_BODY     = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }

    $solver = New-E2ETempSolver -Workspace $ws -Name 'ValidSolver' -SourceCode $code -PlaceInGovernance -Configuration Release
    Assert-Equal 0 $solver.BuildResult.ExitCode
    Assert-True (Test-Path -LiteralPath $solver.DllPath -PathType Leaf) "Expected built DLL at '$($solver.DllPath)'."
}

Register-E2ETest -Id 'T1-F02-02' -Tier 1 -Feature 'F02' -Name 'Case-insensitive OrbSolver.Contracts ProjectReference is allowed by EnforceSolverPolicy' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $lowerContractsRef = Join-Path (Split-Path -Parent $ws.ContractsCsproj) 'orbsolver.contracts.csproj'

    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK    = ''
        NAMESPACE      = 'CaseVariantNs'
        CLASS_NAME     = 'CaseVariantSolver'
        SOLVER_NAME    = 'CaseVariantSolver'
        MODEL_TAG      = 'e2e-case'
        EXECUTION_MODE = 'ExecutionMode.SingleThread'
        SOLVE_BODY     = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }

    $solver = New-E2ETempSolver -Workspace $ws -Name 'CaseVariantSolver' -SourceCode $code -PlaceInGovernance -SkipContractsReference -ExtraProjectReferences @($lowerContractsRef) -Configuration Release
    Assert-Equal 0 $solver.BuildResult.ExitCode
    Assert-True (Test-Path -LiteralPath $solver.DllPath -PathType Leaf) "Expected built DLL at '$($solver.DllPath)'."
}

Register-E2ETest -Id 'T1-F02-03' -Tier 1 -Feature 'F02' -Name 'Solver using pure .NET 10 BCL features builds cleanly under governance targets' -ScriptBlock {
    param($ctx)

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $code = Get-E2EFixtureContent -FileName 'BclFeaturesSolver.cs.tpl' -Tokens @{
        NAMESPACE   = 'BclSolverNs'
        CLASS_NAME  = 'BclSolver'
        SOLVER_NAME = 'BclSolver'
        MODEL_TAG   = 'e2e-bcl'
    }

    $solver = New-E2ETempSolver -Workspace $ws -Name 'BclSolver' -SourceCode $code -PlaceInGovernance -Configuration Release
    Assert-Equal 0 $solver.BuildResult.ExitCode
    Assert-True (Test-Path -LiteralPath $solver.DllPath -PathType Leaf) "Expected built DLL at '$($solver.DllPath)'."
}

Register-E2ETest -Id 'T1-F02-04' -Tier 1 -Feature 'F02' -Name 'Baseline.Random and Baseline.ReferenceBeam compile under solvers/Directory.Build.targets' -ScriptBlock {
    param($ctx)

    $randomProj = Join-Path $ctx.RepoRoot 'solvers\Baseline.Random\Baseline.Random.csproj'
    $beamProj = Join-Path $ctx.RepoRoot 'solvers\Baseline.ReferenceBeam\Baseline.ReferenceBeam.csproj'

    if (-not (Test-Path -LiteralPath $randomProj -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$randomProj'."
    }
    if (-not (Test-Path -LiteralPath $beamProj -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$beamProj'."
    }

    foreach ($proj in @($randomProj, $beamProj)) {
        $res = Invoke-NativeProcess -FilePath 'dotnet' `
            -ArgumentList @('build', $proj, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
            -WorkingDirectory $ctx.RepoRoot `
            -TimeoutMs 180000
        Assert-Equal 0 $res.ExitCode "Build failed for '$proj':`n$($res.CombinedOutput)"

        $targetCheck = Invoke-NativeProcess -FilePath 'dotnet' `
            -ArgumentList @('msbuild', $proj, '-p:Configuration=Release', '-getTargetResult:EnforceSolverPolicy') `
            -WorkingDirectory $ctx.RepoRoot
        Assert-Equal 0 $targetCheck.ExitCode "EnforceSolverPolicy target check failed for '$proj':`n$($targetCheck.CombinedOutput)"
    }
}

Register-E2ETest -Id 'T1-F02-05' -Tier 1 -Feature 'F02' -Name 'solvers/Solver.Template compiles cleanly in Release under EnforceSolverPolicy' -ScriptBlock {
    param($ctx)

    $templateProj = Join-Path $ctx.RepoRoot 'solvers\Solver.Template\Solver.Template.csproj'
    if (-not (Test-Path -LiteralPath $templateProj -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$templateProj'."
    }

    $res = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $templateProj, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $ctx.RepoRoot `
        -TimeoutMs 180000
    Assert-Equal 0 $res.ExitCode "Build failed for '$templateProj':`n$($res.CombinedOutput)"

    $dllPath = Join-Path $ctx.RepoRoot 'solvers\Solver.Template\bin\Release\net10.0\Solver.Template.dll'
    Assert-True (Test-Path -LiteralPath $dllPath -PathType Leaf) "Expected Solver.Template DLL at '$dllPath'."
}

# --- F03: OrbSolver.Runner CLI Flags, Help & Arg Validation (5 cases) ---

Register-E2ETest -Id 'T1-F03-01' -Tier 1 -Feature 'F03' -Name 'OrbSolver.Runner --help displays CLI options and exits 0' -ScriptBlock {
    param($ctx)

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -Configuration Release -RawArguments @('--help') -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Expected --help exit 0:`n$($res.CombinedOutput)"
    foreach ($flag in @('--solvers', '--datasets', '--output', '--filter', '--dev')) {
        Assert-Contains $res.CombinedOutput $flag "Expected help text to document '$flag'."
    }
}

Register-E2ETest -Id 'T1-F03-02' -Tier 1 -Feature 'F03' -Name 'Runner creates non-existent nested --output directory and emits MD and JSON reports' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'CliOutNs'; CLASS_NAME = 'SolverA'; SOLVER_NAME = 'SolverA'; MODEL_TAG = 'cli-test'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverA' -SourceCode $code -PlaceInGovernance -Configuration Release

    $nestedOut = Join-Path $ws.OutputDir 'nested\sub_out'
    Assert-False (Test-Path -LiteralPath $nestedOut) 'Nested output directory should not exist beforehand.'

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.GovernanceSolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $nestedOut -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner failed:`n$($res.CombinedOutput)"
    Assert-True (Test-Path -LiteralPath $nestedOut -PathType Container) 'Runner did not create nested output directory.'
    Assert-GreaterThan $res.JsonFiles.Count 0 'Expected at least 1 JSON report file.'
    Assert-GreaterThan $res.MarkdownFiles.Count 0 'Expected at least 1 Markdown report file.'

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $md = Get-ReportMarkdown -Path $res.LatestMarkdownPath
    Assert-True ($null -ne $json) 'Parsed JSON report should not be null.'
    Assert-GreaterThan $md.Length 20 'Markdown report should be non-empty.'
}

Register-E2ETest -Id 'T1-F03-03' -Tier 1 -Feature 'F03' -Name 'Runner --filter single solver selects only that solver' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    foreach ($name in @('SolverA', 'SolverB')) {
        $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
            USING_BLOCK = ''; NAMESPACE = "${name}Ns"; CLASS_NAME = $name; SOLVER_NAME = $name; MODEL_TAG = 'filter-1'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
        }
        $null = New-E2ETempSolver -Workspace $ws -Name $name -SourceCode $code -PlaceInGovernance -Configuration Release
    }

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.GovernanceSolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'SolverA' -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $md = Get-ReportMarkdown -Path $res.LatestMarkdownPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    Assert-Contains $solvers 'SolverA' 'Expected SolverA in JSON report.'
    Assert-False ($solvers -contains 'SolverB') 'Did not expect SolverB in JSON report.'
    Assert-Contains $md 'SolverA' 'Expected SolverA in Markdown report.'
    Assert-False ($md.Contains('SolverB', [StringComparison]::Ordinal)) 'Did not expect SolverB in Markdown report.'
}

Register-E2ETest -Id 'T1-F03-04' -Tier 1 -Feature 'F03' -Name 'Runner comma-separated --filter runs both specified solvers and excludes others' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    foreach ($name in @('SolverA', 'SolverB', 'SolverC')) {
        $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
            USING_BLOCK = ''; NAMESPACE = "${name}Ns"; CLASS_NAME = $name; SOLVER_NAME = $name; MODEL_TAG = 'filter-2'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
        }
        $null = New-E2ETempSolver -Workspace $ws -Name $name -SourceCode $code -PlaceInGovernance -Configuration Release
    }

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.GovernanceSolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'SolverA,SolverB' -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $md = Get-ReportMarkdown -Path $res.LatestMarkdownPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    Assert-Contains $solvers 'SolverA'
    Assert-Contains $solvers 'SolverB'
    Assert-False ($solvers -contains 'SolverC') 'Did not expect SolverC in JSON report.'
    Assert-False ($md.Contains('SolverC', [StringComparison]::Ordinal)) 'Did not expect SolverC in Markdown report.'
}

Register-E2ETest -Id 'T1-F03-05' -Tier 1 -Feature 'F03' -Name 'Runner resolves relative --solvers, --datasets, and --output paths from working directory' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'RelPathNs'; CLASS_NAME = 'SolverRel'; SOLVER_NAME = 'SolverRel'; MODEL_TAG = 'rel-path'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverRel' -SourceCode $code -Configuration Release

    $relReports = Join-Path $ws.Root 'reports'
    $null = New-Item -ItemType Directory -Path $relReports -Force

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -WorkingDirectory $ws.Root -OutputDir $relReports -RawArguments @('--solvers', './solvers', '--datasets', './datasets', '--output', './reports') -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner with relative paths failed:`n$($res.CombinedOutput)"
    Assert-GreaterThan $res.JsonFiles.Count 0 'Expected JSON report in ./reports.'
    Assert-GreaterThan $res.MarkdownFiles.Count 0 'Expected Markdown report in ./reports.'
}

# --- F04: Release Build Guard (#if DEBUG + --dev) (5 cases) ---

Register-E2ETest -Id 'T1-F04-01' -Tier 1 -Feature 'F04' -Name 'Release Runner executes cleanly without --dev' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'RelGuardNs'; CLASS_NAME = 'SolverRelGuard'; SOLVER_NAME = 'SolverRelGuard'; MODEL_TAG = 'rel-guard'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverRelGuard' -SourceCode $code -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Release Runner failed:`n$($res.CombinedOutput)"
    Assert-GreaterThan $res.JsonFiles.Count 0
    Assert-GreaterThan $res.MarkdownFiles.Count 0
}

Register-E2ETest -Id 'T1-F04-02' -Tier 1 -Feature 'F04' -Name 'Release Runner report EnvironmentFingerprint records Release build mode' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'RelModeNs'; CLASS_NAME = 'SolverRelMode'; SOLVER_NAME = 'SolverRelMode'; MODEL_TAG = 'rel-mode'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverRelMode' -SourceCode $code -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Release Runner failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $md = Get-ReportMarkdown -Path $res.LatestMarkdownPath
    Assert-True (Test-JsonFingerprint -ReportJson $json) 'Expected populated EnvironmentFingerprint in JSON report.'

    $rawJson = [System.IO.File]::ReadAllText($res.LatestJsonPath, [System.Text.Encoding]::UTF8)
    Assert-Match -Text $rawJson -Pattern '(?i)"(BuildMode|Configuration|BuildConfiguration)"\s*:\s*"Release"' -Message 'Expected Release build mode in JSON.'
    Assert-Match -Text $md -Pattern 'Release' -Message 'Expected Release build mode in Markdown.'
}

Register-E2ETest -Id 'T1-F04-03' -Tier 1 -Feature 'F04' -Name 'Debug Runner with --dev executes benchmark and scores solvers' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Debug -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'DebugDevNs'; CLASS_NAME = 'SolverDebugDev'; SOLVER_NAME = 'SolverDebugDev'; MODEL_TAG = 'dbg-dev'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverDebugDev' -SourceCode $code -Configuration Debug

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Dev -Configuration Debug -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Debug Runner with --dev failed:`n$($res.CombinedOutput)"
    Assert-GreaterThan $res.JsonFiles.Count 0
    Assert-GreaterThan $res.MarkdownFiles.Count 0

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $cases = @(Get-JsonSolverCases -ReportJson $json -SolverName 'SolverDebugDev')
    Assert-GreaterThan $cases.Count 0 'Expected scored cases for SolverDebugDev.'
}

Register-E2ETest -Id 'T1-F04-04' -Tier 1 -Feature 'F04' -Name 'Debug Runner with --dev marks Debug or Dev build mode in reports' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Debug -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'ModeProbeNs'; CLASS_NAME = 'SolverModeProbe'; SOLVER_NAME = 'SolverModeProbe'; MODEL_TAG = 'mode-probe'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverModeProbe' -SourceCode $code -Configuration Debug

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Dev -Configuration Debug -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Debug Runner with --dev failed:`n$($res.CombinedOutput)"

    $rawJson = [System.IO.File]::ReadAllText($res.LatestJsonPath, [System.Text.Encoding]::UTF8)
    $md = Get-ReportMarkdown -Path $res.LatestMarkdownPath
    Assert-Match -Text $rawJson -Pattern '(?i)(Debug|Dev)' -Message 'Expected Debug/Dev marker in JSON report.'
    Assert-Match -Text $md -Pattern '(?i)(Debug|Dev)' -Message 'Expected Debug/Dev marker in Markdown report.'
}

Register-E2ETest -Id 'T1-F04-05' -Tier 1 -Feature 'F04' -Name 'Release Runner accepts --dev harmlessly if supplied' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles
    $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'RelDevNs'; CLASS_NAME = 'SolverRelDev'; SOLVER_NAME = 'SolverRelDev'; MODEL_TAG = 'rel-dev'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'SolverRelDev' -SourceCode $code -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Dev -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Release Runner with --dev failed:`n$($res.CombinedOutput)"
    Assert-GreaterThan $res.JsonFiles.Count 0
}

# --- F05: Solver Scanning, --filter & Solver.Template Default Skip (5 cases) ---

Register-E2ETest -Id 'T1-F05-01' -Tier 1 -Feature 'F05' -Name 'Default scan of repo solvers/ discovers Baseline.Random and Baseline.ReferenceBeam' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    foreach ($req in @('solvers\Baseline.Random\Baseline.Random.csproj', 'solvers\Baseline.ReferenceBeam\Baseline.ReferenceBeam.csproj', 'solvers\Solver.Template\Solver.Template.csproj')) {
        $p = Join-Path $ctx.RepoRoot $req
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
            throw "[PREREQUISITE_MISSING] Missing '$p'."
        }
    }

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir (Join-Path $ctx.RepoRoot 'solvers') -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Default repo solvers scan failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    Assert-True ($null -ne ($solvers | Where-Object { $_ -match 'Random' })) 'Expected Baseline.Random in report.'
    Assert-True ($null -ne ($solvers | Where-Object { $_ -match 'ReferenceBeam' })) 'Expected Baseline.ReferenceBeam in report.'
}

Register-E2ETest -Id 'T1-F05-02' -Tier 1 -Feature 'F05' -Name 'Default scan of repo solvers/ skips Solver.Template' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    foreach ($req in @('solvers\Baseline.Random\Baseline.Random.csproj', 'solvers\Baseline.ReferenceBeam\Baseline.ReferenceBeam.csproj', 'solvers\Solver.Template\Solver.Template.csproj')) {
        $p = Join-Path $ctx.RepoRoot $req
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
            throw "[PREREQUISITE_MISSING] Missing '$p'."
        }
    }

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir (Join-Path $ctx.RepoRoot 'solvers') -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Default repo solvers scan failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $md = Get-ReportMarkdown -Path $res.LatestMarkdownPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    Assert-False ($null -ne ($solvers | Where-Object { $_ -match 'Solver\.Template' })) 'Solver.Template must be skipped in default JSON report.'
    Assert-False ($md.Contains('Solver.Template', [StringComparison]::OrdinalIgnoreCase)) 'Solver.Template must be skipped in default Markdown report.'
}

Register-E2ETest -Id 'T1-F05-03' -Tier 1 -Feature 'F05' -Name 'Explicit --filter Solver.Template includes it and records RE (0 score)' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $templateProj = Join-Path $ctx.RepoRoot 'solvers\Solver.Template\Solver.Template.csproj'
    if (-not (Test-Path -LiteralPath $templateProj -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$templateProj'."
    }

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir (Join-Path $ctx.RepoRoot 'solvers') -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'Solver.Template' -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner with --filter Solver.Template failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    $tmplName = @($solvers | Where-Object { $_ -match 'Template' }) | Select-Object -First 1
    Assert-True (-not [string]::IsNullOrWhiteSpace($tmplName)) 'Expected Solver.Template in filtered report.'

    $cases = @(Get-JsonSolverCases -ReportJson $json -SolverName $tmplName)
    Assert-GreaterThan $cases.Count 0 'Expected case results for Solver.Template.'
    foreach ($c in $cases) {
        $rawCase = ($c | ConvertTo-Json -Depth 10)
        Assert-Match -Text $rawCase -Pattern '"RE"' -Message 'Expected RE gate status on Solver.Template case.'
        Assert-Match -Text $rawCase -Pattern '"(CaseScore|Score)"\s*:\s*0(\.0+)?\b' -Message 'Expected 0 score on Solver.Template case.'
    }
}

Register-E2ETest -Id 'T1-F05-04' -Tier 1 -Feature 'F05' -Name 'Explicit --filter Baseline.ReferenceBeam,Solver.Template runs both and excludes Random' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    foreach ($req in @('solvers\Baseline.Random\Baseline.Random.csproj', 'solvers\Baseline.ReferenceBeam\Baseline.ReferenceBeam.csproj', 'solvers\Solver.Template\Solver.Template.csproj')) {
        $p = Join-Path $ctx.RepoRoot $req
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
            throw "[PREREQUISITE_MISSING] Missing '$p'."
        }
    }

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir (Join-Path $ctx.RepoRoot 'solvers') -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Filter 'Baseline.ReferenceBeam,Solver.Template' -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    $beamName = @($solvers | Where-Object { $_ -match 'ReferenceBeam' }) | Select-Object -First 1
    $tmplName = @($solvers | Where-Object { $_ -match 'Template' }) | Select-Object -First 1
    Assert-True (-not [string]::IsNullOrWhiteSpace($beamName)) 'Expected ReferenceBeam.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($tmplName)) 'Expected Solver.Template.'
    Assert-False ($null -ne ($solvers | Where-Object { $_ -match 'Random' })) 'Did not expect Baseline.Random.'

    $beamCases = @(Get-JsonSolverCases -ReportJson $json -SolverName $beamName)
    Assert-GreaterThan $beamCases.Count 0 'Expected case results for Baseline.ReferenceBeam.'
    foreach ($c in $beamCases) {
        $rawCase = ($c | ConvertTo-Json -Depth 10)
        Assert-Match -Text $rawCase -Pattern '"OK"' -Message 'Expected OK status on Baseline.ReferenceBeam case.'
    }

    $tmplCases = @(Get-JsonSolverCases -ReportJson $json -SolverName $tmplName)
    Assert-GreaterThan $tmplCases.Count 0 'Expected case results for Solver.Template.'
    foreach ($c in $tmplCases) {
        $rawCase = ($c | ConvertTo-Json -Depth 10)
        Assert-Match -Text $rawCase -Pattern '"RE"' -Message 'Expected RE gate status on Solver.Template case.'
        Assert-Match -Text $rawCase -Pattern '"(CaseScore|Score)"\s*:\s*0(\.0+)?\b' -Message 'Expected 0 score on Solver.Template case.'
    }
}

Register-E2ETest -Id 'T1-F05-05' -Tier 1 -Feature 'F05' -Name 'Custom multi-solver directory discovers Alpha and Beta while skipping staged Solver.Template' -ScriptBlock {
    param($ctx)

    Assert-OrbSolverRunnerPrerequisite -RepoRoot $ctx.RepoRoot -Configuration Release -SkipBuild:$ctx.SkipBuild

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot
    $null = New-E2ESyntheticDataset -Workspace $ws -WriteAllStandardFiles

    foreach ($name in @('Solver.Alpha', 'Solver.Beta')) {
        $code = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
            USING_BLOCK = ''; NAMESPACE = ($name -replace '\.', ''); CLASS_NAME = ($name -replace '\.', ''); SOLVER_NAME = $name; MODEL_TAG = 'multi-scan'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        return new SolveResult(new CellCoord(0, 0), Array.Empty<Direction>());'
        }
        $null = New-E2ETempSolver -Workspace $ws -Name $name -SourceCode $code -Configuration Release
    }

    $tmplCode = Get-E2EFixtureContent -FileName 'SimpleValidSolver.cs.tpl' -Tokens @{
        USING_BLOCK = ''; NAMESPACE = 'SolverTemplateNs'; CLASS_NAME = 'TemplateSolver'; SOLVER_NAME = 'Solver.Template'; MODEL_TAG = 'template'; EXECUTION_MODE = 'ExecutionMode.SingleThread'; SOLVE_BODY = '        throw new NotImplementedException();'
    }
    $null = New-E2ETempSolver -Workspace $ws -Name 'Solver.Template' -SourceCode $tmplCode -Configuration Release

    $res = Invoke-OrbSolverRunner -RepoRoot $ctx.RepoRoot -SolversDir $ws.SolversDir -DatasetsDir $ws.DatasetsDir -OutputDir $ws.OutputDir -Configuration Release -SkipBuild:$ctx.SkipBuild
    Assert-Equal 0 $res.ExitCode "Runner failed:`n$($res.CombinedOutput)"

    $json = Get-ReportJson -Path $res.LatestJsonPath
    $solvers = @(Find-JsonSolverNames -ReportJson $json)
    Assert-Contains $solvers 'Solver.Alpha'
    Assert-Contains $solvers 'Solver.Beta'
    Assert-False ($solvers -contains 'Solver.Template') 'Staged Solver.Template should be skipped.'
}

# --- F15: docs/prompt_template.md, README.md & OrbSolver.Contracts (5 cases) ---

Register-E2ETest -Id 'T1-F15-01' -Tier 1 -Feature 'F15' -Name 'docs/prompt_template.md exists and documents full IOrbSolver spec and rules' -ScriptBlock {
    param($ctx)

    $promptPath = Join-Path $ctx.RepoRoot 'docs\prompt_template.md'
    if (-not (Test-Path -LiteralPath $promptPath -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing '$promptPath'."
    }

    $text = [System.IO.File]::ReadAllText($promptPath, [System.Text.Encoding]::UTF8)
    Assert-GreaterThan $text.Length 200 'docs/prompt_template.md is unexpectedly short.'
    foreach ($token in @('IOrbSolver', 'BoardSnapshot', 'SolveOptions', 'SolveResult', 'OrbType', 'Direction', 'Seed', 'CancellationToken')) {
        Assert-Contains $text $token "docs/prompt_template.md missing '$token'."
    }
    Assert-Match -Text $text -Pattern '(6\s*[x×]\s*5|5\s*[x×]\s*6|30)' -Message 'Expected 6x5 / 30-cell board specification.'
}

Register-E2ETest -Id 'T1-F15-02' -Tier 1 -Feature 'F15' -Name 'README.md documents tiers, 65/20/15 weights, baselines, template, and CLI commands' -ScriptBlock {
    param($ctx)

    $readmePath = Join-Path $ctx.RepoRoot 'README.md'
    Assert-True (Test-Path -LiteralPath $readmePath -PathType Leaf) "Missing '$readmePath'."

    $text = [System.IO.File]::ReadAllText($readmePath, [System.Text.Encoding]::UTF8)
    foreach ($token in @('15%', '20%', '25%', '65%', 'Solver.Template', 'docs/prompt_template.md', 'dotnet build -c Release', 'dotnet run --project src/OrbSolver.Runner -c Release')) {
        Assert-Contains $text $token "README.md missing '$token'."
    }
}

Register-E2ETest -Id 'T1-F15-03' -Tier 1 -Feature 'F15' -Name 'OrbSolver.Contracts has zero external NuGet or Project dependencies' -ScriptBlock {
    param($ctx)

    $contractsCsproj = Join-Path $ctx.RepoRoot 'src\OrbSolver.Contracts\OrbSolver.Contracts.csproj'
    Assert-True (Test-Path -LiteralPath $contractsCsproj -PathType Leaf) "Missing '$contractsCsproj'."

    [xml]$csprojXml = Get-Content -LiteralPath $contractsCsproj -Raw
    $pkgRefs = @($csprojXml.SelectNodes('//PackageReference'))
    $projRefs = @($csprojXml.SelectNodes('//ProjectReference'))
    Assert-Equal 0 $pkgRefs.Count 'OrbSolver.Contracts.csproj must have zero PackageReference elements.'
    Assert-Equal 0 $projRefs.Count 'OrbSolver.Contracts.csproj must have zero ProjectReference elements.'

    $ws = New-E2ETempWorkspace -RepoRoot $ctx.RepoRoot -IncludeGovernance
    $buildRes = Invoke-NativeProcess -FilePath 'dotnet' `
        -ArgumentList @('build', $ws.ContractsCsproj, '-c', 'Release', '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
        -WorkingDirectory $ws.GovernanceDir
    Assert-Equal 0 $buildRes.ExitCode "Building temp OrbSolver.Contracts failed:`n$($buildRes.CombinedOutput)"

    $depsPath = Join-Path (Split-Path -Parent $ws.ContractsCsproj) 'bin\Release\net10.0\OrbSolver.Contracts.deps.json'
    Assert-True (Test-Path -LiteralPath $depsPath -PathType Leaf) "Missing deps.json at '$depsPath'."

    $depsJson = Get-ReportJson -Path $depsPath
    $libNames = @($depsJson.libraries.PSObject.Properties | ForEach-Object { $_.Name })
    Assert-Equal 1 $libNames.Count "Expected only 1 library in OrbSolver.Contracts.deps.json, found: $($libNames -join ', ')"
    Assert-Match -Text $libNames[0] -Pattern '^OrbSolver\.Contracts/'
}

Register-E2ETest -Id 'T1-F15-04' -Tier 1 -Feature 'F15' -Name 'BoardSnapshot constructs cleanly with 30 cells and exposes masks and PuzzleColor' -ScriptBlock {
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
    $runRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @($dllPath, '--mode', 'valid-snapshot') -WorkingDirectory $probeDir
    Assert-Equal 0 $runRes.ExitCode "ContractsProbe failed:`n$($runRes.CombinedOutput)"
    Assert-Contains $runRes.Stdout 'PROBE_OK:valid-snapshot'
}

Register-E2ETest -Id 'T1-F15-05' -Tier 1 -Feature 'F15' -Name 'CellCoord(r, c).Index computes r * 6 + c across all 30 grid cells' -ScriptBlock {
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
    $runRes = Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList @($dllPath, '--mode', 'cellcoord-grid') -WorkingDirectory $probeDir
    Assert-Equal 0 $runRes.ExitCode "ContractsProbe failed:`n$($runRes.CombinedOutput)"
    Assert-Contains $runRes.Stdout 'PROBE_OK:cellcoord-grid'
}
