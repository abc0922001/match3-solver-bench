#Requires -Version 7.0
Set-StrictMode -Version Latest

$script:RegisteredTests = [System.Collections.Generic.List[object]]::new()
$script:TrackedWorkspaces = [System.Collections.Generic.List[string]]::new()
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:DefaultFixturesDir = Join-Path $PSScriptRoot 'fixtures'

function Copy-E2EDirectoryFiltered {
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$DestDir,
        [string[]]$ExcludeDirNames = @('bin', 'obj')
    )

    if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
        throw "Source directory not found: $SourceDir"
    }

    $null = New-Item -ItemType Directory -Path $DestDir -Force

    foreach ($item in @(Get-ChildItem -LiteralPath $SourceDir -Force)) {
        if ($item.PSIsContainer) {
            if ($ExcludeDirNames -contains $item.Name) {
                continue
            }
            $childDest = Join-Path $DestDir $item.Name
            Copy-E2EDirectoryFiltered -SourceDir $item.FullName -DestDir $childDest -ExcludeDirNames $ExcludeDirNames
        }
        else {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $DestDir $item.Name) -Force
        }
    }
}

function Get-JsonPropertyCI {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string[]]$CandidateNames
    )

    if ($null -eq $Object -or $Object -is [string] -or $Object -is [ValueType]) {
        return $null
    }

    foreach ($name in $CandidateNames) {
        foreach ($prop in $Object.PSObject.Properties) {
            if ([string]::Equals($prop.Name, $name, [StringComparison]::OrdinalIgnoreCase)) {
                return $prop.Value
            }
        }
    }

    return $null
}

function Get-AllJsonObjects {
    param(
        [Parameter(Mandatory)]$Node
    )

    $results = [System.Collections.Generic.List[object]]::new()
    if ($null -eq $Node -or $Node -is [string] -or $Node -is [ValueType]) {
        return $results.ToArray()
    }

    if ($Node -is [System.Collections.IEnumerable] -and -not ($Node -is [System.Collections.IDictionary])) {
        foreach ($item in $Node) {
            foreach ($sub in @(Get-AllJsonObjects -Node $item)) {
                $results.Add($sub)
            }
        }
        return $results.ToArray()
    }

    $results.Add($Node)

    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($val in $Node.Values) {
            foreach ($sub in @(Get-AllJsonObjects -Node $val)) {
                $results.Add($sub)
            }
        }
        return $results.ToArray()
    }

    foreach ($prop in $Node.PSObject.Properties) {
        foreach ($sub in @(Get-AllJsonObjects -Node $prop.Value)) {
            $results.Add($sub)
        }
    }

    return $results.ToArray()
}

function Register-E2ETest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][ValidateRange(1, 4)][int]$Tier,
        [Parameter(Mandatory)][string]$Feature,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$ScriptBlock
    )

    foreach ($existing in $script:RegisteredTests) {
        if ([string]::Equals($existing.Id, $Id, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Duplicate E2E test Id registered: '$Id'"
        }
    }

    $script:RegisteredTests.Add([PSCustomObject]@{
        Id          = $Id
        Tier        = $Tier
        Feature     = $Feature
        Name        = $Name
        ScriptBlock = $ScriptBlock
    })
}

function Get-E2ETests {
    [CmdletBinding()]
    param()

    return $script:RegisteredTests.ToArray()
}

function Clear-E2ETests {
    [CmdletBinding()]
    param()

    $script:RegisteredTests.Clear()
}

function Get-TrackedE2ETempWorkspaces {
    [CmdletBinding()]
    param()

    return $script:TrackedWorkspaces.ToArray()
}

function Clear-TrackedE2ETempWorkspaces {
    [CmdletBinding()]
    param(
        [string[]]$Paths = @()
    )

    if (@($Paths).Count -eq 0) {
        $script:TrackedWorkspaces.Clear()
        return
    }

    foreach ($p in $Paths) {
        $norm = [System.IO.Path]::GetFullPath($p).TrimEnd('\', '/')
        for ($i = $script:TrackedWorkspaces.Count - 1; $i -ge 0; $i--) {
            $cur = [System.IO.Path]::GetFullPath($script:TrackedWorkspaces[$i]).TrimEnd('\', '/')
            if ([string]::Equals($cur, $norm, [StringComparison]::OrdinalIgnoreCase)) {
                $script:TrackedWorkspaces.RemoveAt($i)
            }
        }
    }
}

function Get-E2EFixtureContent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FileName,
        [hashtable]$Tokens = @{},
        [string]$FixturesDir = $script:DefaultFixturesDir
    )

    $path = Join-Path $FixturesDir $FileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "E2E fixture file not found: $path"
    }

    $content = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    foreach ($key in $Tokens.Keys) {
        $placeholder = "{{$key}}"
        $content = $content.Replace($placeholder, [string]$Tokens[$key])
    }

    return $content
}

function New-E2ETempWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [switch]$IncludeGovernance,
        [switch]$IncludeCoreInGovernance,
        [switch]$IncludeWarmupDataset
    )

    if ($IncludeCoreInGovernance) {
        $IncludeGovernance = $true
    }

    $resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    $tempRoot = [System.IO.Path]::GetTempPath()
    $folderName = 'OrbSolverE2E_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $root = Join-Path $tempRoot $folderName

    $solversDir = Join-Path $root 'solvers'
    $datasetsDir = Join-Path $root 'datasets'
    $outputDir = Join-Path $root 'output'

    $null = New-Item -ItemType Directory -Path $root -Force
    $null = New-Item -ItemType Directory -Path $solversDir -Force
    $null = New-Item -ItemType Directory -Path $datasetsDir -Force
    $null = New-Item -ItemType Directory -Path $outputDir -Force

    $script:TrackedWorkspaces.Add($root)

    $governanceDir = ''
    $governanceSolversDir = ''
    $contractsCsproj = ''
    $coreCsproj = ''

    if ($IncludeGovernance) {
        $governanceDir = Join-Path $root 'governance'
        $governanceSolversDir = Join-Path $governanceDir 'solvers'
        $govSrcDir = Join-Path $governanceDir 'src'
        $govContractsDir = Join-Path $govSrcDir 'OrbSolver.Contracts'

        $null = New-Item -ItemType Directory -Path $governanceDir -Force
        $null = New-Item -ItemType Directory -Path $governanceSolversDir -Force
        $null = New-Item -ItemType Directory -Path $govSrcDir -Force

        $propsContent = Get-E2EFixtureContent -FileName 'GovernancePropsShim.xml' -Tokens @{ REPO_ROOT = $resolvedRepoRoot }
        $packagesContent = Get-E2EFixtureContent -FileName 'GovernancePackagesShim.xml' -Tokens @{ REPO_ROOT = $resolvedRepoRoot }
        $targetsContent = Get-E2EFixtureContent -FileName 'GovernanceTargetsShim.xml' -Tokens @{ REPO_ROOT = $resolvedRepoRoot }

        [System.IO.File]::WriteAllText((Join-Path $governanceDir 'Directory.Build.props'), $propsContent, $script:Utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $governanceDir 'Directory.Packages.props'), $packagesContent, $script:Utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $governanceSolversDir 'Directory.Build.targets'), $targetsContent, $script:Utf8NoBom)

        $repoContractsDir = Join-Path $resolvedRepoRoot 'src\OrbSolver.Contracts'
        if (-not (Test-Path -LiteralPath (Join-Path $repoContractsDir 'OrbSolver.Contracts.csproj') -PathType Leaf)) {
            throw "[PREREQUISITE_MISSING] Missing src/OrbSolver.Contracts/OrbSolver.Contracts.csproj under '$resolvedRepoRoot'."
        }

        Copy-E2EDirectoryFiltered -SourceDir $repoContractsDir -DestDir $govContractsDir
        $contractsCsproj = Join-Path $govContractsDir 'OrbSolver.Contracts.csproj'

        if ($IncludeCoreInGovernance) {
            $repoCoreDir = Join-Path $resolvedRepoRoot 'src\OrbSolver.Core'
            $repoCoreCsproj = Join-Path $repoCoreDir 'OrbSolver.Core.csproj'
            if (-not (Test-Path -LiteralPath $repoCoreCsproj -PathType Leaf)) {
                throw "[PREREQUISITE_MISSING] Missing src/OrbSolver.Core/OrbSolver.Core.csproj under '$resolvedRepoRoot'."
            }

            $govCoreDir = Join-Path $govSrcDir 'OrbSolver.Core'
            Copy-E2EDirectoryFiltered -SourceDir $repoCoreDir -DestDir $govCoreDir
            $coreCsproj = Join-Path $govCoreDir 'OrbSolver.Core.csproj'
        }
    }

    $ws = [PSCustomObject]@{
        Root                 = $root
        SolversDir           = $solversDir
        DatasetsDir          = $datasetsDir
        OutputDir            = $outputDir
        GovernanceDir        = $governanceDir
        GovernanceSolversDir = $governanceSolversDir
        ContractsCsproj      = $contractsCsproj
        CoreCsproj           = $coreCsproj
        RepoRoot             = $resolvedRepoRoot
    }

    if ($IncludeWarmupDataset) {
        $null = New-E2ESyntheticDataset -Workspace $ws -FileName 'warmup.json'
    }

    return $ws
}

function Remove-E2ETempWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Workspace
    )

    $rawPath = if ($Workspace -is [string]) {
        $Workspace
    }
    elseif ($null -ne $Workspace.PSObject.Properties['Root']) {
        [string]$Workspace.Root
    }
    else {
        throw "Remove-E2ETempWorkspace expects a path string or workspace object with .Root."
    }

    if ([string]::IsNullOrWhiteSpace($rawPath)) {
        throw "Remove-E2ETempWorkspace received an empty path."
    }

    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $targetFull = [System.IO.Path]::GetFullPath($rawPath).TrimEnd('\', '/')
    $leaf = Split-Path -Leaf $targetFull

    if (-not $targetFull.StartsWith($tempRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside temp root: '$targetFull' (temp root '$tempRoot')."
    }

    if ($leaf -notmatch '^OrbSolverE2E_[0-9a-f]{12}$') {
        throw "Refusing to remove non-E2E temp folder: '$targetFull'."
    }

    if (Test-Path -LiteralPath $targetFull) {
        $maxAttempts = 15
        $lastErr = ''
        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            try {
                Remove-Item -LiteralPath $targetFull -Recurse -Force -ErrorAction Stop
            }
            catch {
                $lastErr = $_.Exception.Message
                if (Test-Path -LiteralPath $targetFull) {
                    try {
                        $dirInfo = [System.IO.DirectoryInfo]::new($targetFull)
                        foreach ($entry in $dirInfo.EnumerateFileSystemInfos('*', [System.IO.SearchOption]::AllDirectories)) {
                            if (($entry.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                                $entry.Attributes = $entry.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
                            }
                        }
                        if (($dirInfo.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                            $dirInfo.Attributes = $dirInfo.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
                        }
                    }
                    catch {}

                    try {
                        [System.IO.Directory]::Delete($targetFull, $true)
                    }
                    catch {
                        $lastErr = $_.Exception.Message
                    }
                }
            }

            if (-not (Test-Path -LiteralPath $targetFull)) {
                break
            }

            if ($attempt -lt $maxAttempts) {
                $delayMs = if ($attempt -le 7) { 250 } else { 400 }
                Start-Sleep -Milliseconds $delayMs
            }
        }

        if (Test-Path -LiteralPath $targetFull) {
            $warnMsg = "Failed to remove E2E temp workspace '$targetFull' after $maxAttempts attempts: $lastErr"
            Write-Host "[WARN] $warnMsg" -ForegroundColor Yellow
            Write-Warning $warnMsg
        }
    }

    if (-not (Test-Path -LiteralPath $targetFull)) {
        Clear-TrackedE2ETempWorkspaces -Paths @($targetFull)
    }
}

function Invoke-NativeProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = (Get-Location).Path,
        [int]$TimeoutMs = 120000,
        [hashtable]$EnvironmentOverrides = @{},
        [string[]]$RemoveEnvironmentVariables = @('DOTNET_GCHeapHardLimit')
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    foreach ($arg in $ArgumentList) {
        [void]$psi.ArgumentList.Add($arg)
    }

    foreach ($varName in $RemoveEnvironmentVariables) {
        if (-not [string]::IsNullOrWhiteSpace($varName)) {
            $keysToRemove = @($psi.Environment.Keys | Where-Object { [string]::Equals($_, $varName, [StringComparison]::OrdinalIgnoreCase) })
            foreach ($k in $keysToRemove) {
                [void]$psi.Environment.Remove($k)
            }
        }
    }

    $psi.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $psi.Environment['DOTNET_NOLOGO'] = '1'

    foreach ($key in $EnvironmentOverrides.Keys) {
        $val = $EnvironmentOverrides[$key]
        if ($null -eq $val) {
            [void]$psi.Environment.Remove([string]$key)
        }
        else {
            $psi.Environment[[string]$key] = [string]$val
        }
    }

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $stdout = ''
    $stderr = ''
    $exitCode = -1

    try {
        if (-not $proc.Start()) {
            throw "Failed to start process '$FilePath'."
        }

        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()

        $exited = $proc.WaitForExit($TimeoutMs)
        if (-not $exited) {
            $timedOut = $true
            try { $proc.Kill($true) } catch {}
            $null = $proc.WaitForExit(2000)
            try { $null = $stdoutTask.Wait(1000) } catch {}
            try { $null = $stderrTask.Wait(1000) } catch {}
            if ($stdoutTask.IsCompletedSuccessfully) { $stdout = $stdoutTask.Result }
            if ($stderrTask.IsCompletedSuccessfully) { $stderr = $stderrTask.Result }
            $exitCode = -1
        }
        else {
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            $exitCode = $proc.ExitCode
        }
    }
    finally {
        $sw.Stop()
        $proc.Dispose()
    }

    $combined = (($stdout ?? '') + "`n" + ($stderr ?? '')).Trim()

    return [PSCustomObject]@{
        ExitCode       = $exitCode
        Stdout         = ($stdout ?? '')
        Stderr         = ($stderr ?? '')
        CombinedOutput = $combined
        TimedOut       = $timedOut
        DurationMs     = $sw.ElapsedMilliseconds
    }
}

function New-E2ETempSolver {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Workspace,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$SourceCode,
        [string]$SourceFileName = 'Solver.cs',
        [switch]$PlaceInGovernance,
        [switch]$ImportGovernanceTargets,
        [string]$TargetDirectory = '',
        [bool]$AllowUnsafeBlocks = $false,
        [string]$ExtraProperties = '',
        [string[]]$PackageReferences = @(),
        [string[]]$ExtraProjectReferences = @(),
        [switch]$SkipContractsReference,
        [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
        [switch]$SkipBuild,
        [switch]$AllowBuildFailure,
        [int]$TimeoutMs = 120000
    )

    if (-not [string]::IsNullOrWhiteSpace($TargetDirectory)) {
        $projectDir = $TargetDirectory
    }
    elseif ($PlaceInGovernance) {
        if ([string]::IsNullOrWhiteSpace($Workspace.GovernanceSolversDir)) {
            throw "Workspace was not created with -IncludeGovernance; cannot place solver '$Name' in governance."
        }
        $projectDir = Join-Path $Workspace.GovernanceSolversDir $Name
    }
    else {
        $projectDir = Join-Path $Workspace.SolversDir $Name
    }

    $null = New-Item -ItemType Directory -Path $projectDir -Force

    $extraPropsList = [System.Collections.Generic.List[string]]::new()
    if ($AllowUnsafeBlocks) {
        $extraPropsList.Add('    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>')
    }
    if (-not [string]::IsNullOrWhiteSpace($ExtraProperties)) {
        $extraPropsList.Add($ExtraProperties.TrimEnd())
    }
    $extraPropsBlock = ($extraPropsList -join "`n")

    $projRefLines = [System.Collections.Generic.List[string]]::new()
    if (-not $SkipContractsReference) {
        if (-not [string]::IsNullOrWhiteSpace($Workspace.ContractsCsproj) -and (Test-Path -LiteralPath $Workspace.ContractsCsproj -PathType Leaf)) {
            $projRefLines.Add("    <ProjectReference Include=`"$($Workspace.ContractsCsproj)`" />")
        }
        else {
            $tempContractsRoot = Join-Path $Workspace.Root '_contracts'
            $tempContractsDir = Join-Path $tempContractsRoot 'OrbSolver.Contracts'
            $tempContractsCsproj = Join-Path $tempContractsDir 'OrbSolver.Contracts.csproj'
            if (-not (Test-Path -LiteralPath $tempContractsCsproj -PathType Leaf)) {
                $repoContractsDir = Join-Path $Workspace.RepoRoot 'src\OrbSolver.Contracts'
                if (-not (Test-Path -LiteralPath (Join-Path $repoContractsDir 'OrbSolver.Contracts.csproj') -PathType Leaf)) {
                    throw "[PREREQUISITE_MISSING] Missing src/OrbSolver.Contracts/OrbSolver.Contracts.csproj under '$($Workspace.RepoRoot)'."
                }
                $null = New-Item -ItemType Directory -Path $tempContractsRoot -Force
                $propsShim = Get-E2EFixtureContent -FileName 'GovernancePropsShim.xml' -Tokens @{ REPO_ROOT = $Workspace.RepoRoot }
                [System.IO.File]::WriteAllText((Join-Path $tempContractsRoot 'Directory.Build.props'), $propsShim, $script:Utf8NoBom)
                Copy-E2EDirectoryFiltered -SourceDir $repoContractsDir -DestDir $tempContractsDir
            }
            $projRefLines.Add("    <ProjectReference Include=`"$tempContractsCsproj`" />")
        }
    }

    foreach ($extraRef in $ExtraProjectReferences) {
        if ([string]::IsNullOrWhiteSpace($extraRef)) { continue }
        if ($extraRef.TrimStart().StartsWith('<')) {
            $projRefLines.Add("    " + $extraRef.Trim())
        }
        else {
            $projRefLines.Add("    <ProjectReference Include=`"$extraRef`" />")
        }
    }

    $projRefBlock = if ($projRefLines.Count -gt 0) {
        "  <ItemGroup>`n" + ($projRefLines -join "`n") + "`n  </ItemGroup>"
    }
    else { '' }

    $pkgRefLines = [System.Collections.Generic.List[string]]::new()
    foreach ($pkg in $PackageReferences) {
        if ([string]::IsNullOrWhiteSpace($pkg)) { continue }
        $trimmed = $pkg.Trim()
        if ($trimmed.StartsWith('<')) {
            $pkgRefLines.Add("    $trimmed")
        }
        elseif ($trimmed -match '^([^@]+)@(.+)$') {
            $pkgRefLines.Add("    <PackageReference Include=`"$($Matches[1])`" Version=`"$($Matches[2])`" />")
        }
        else {
            $pkgRefLines.Add("    <PackageReference Include=`"$trimmed`" />")
        }
    }

    $pkgRefBlock = if ($pkgRefLines.Count -gt 0) {
        "  <ItemGroup>`n" + ($pkgRefLines -join "`n") + "`n  </ItemGroup>"
    }
    else { '' }

    $extraImportsBlock = if ($ImportGovernanceTargets) {
        $targetsPath = Join-Path $Workspace.RepoRoot 'solvers\Directory.Build.targets'
        "  <Import Project=`"$targetsPath`" />"
    }
    else { '' }

    $csprojContent = Get-E2EFixtureContent -FileName 'SolverProjectTemplate.xml' -Tokens @{
        EXTRA_PROPS   = $extraPropsBlock
        PROJECT_REFS  = $projRefBlock
        PACKAGE_REFS  = $pkgRefBlock
        EXTRA_IMPORTS = $extraImportsBlock
    }

    $csprojPath = Join-Path $projectDir "$Name.csproj"
    $sourcePath = Join-Path $projectDir $SourceFileName

    [System.IO.File]::WriteAllText($csprojPath, $csprojContent, $script:Utf8NoBom)
    [System.IO.File]::WriteAllText($sourcePath, $SourceCode, $script:Utf8NoBom)

    $buildResult = $null
    if (-not $SkipBuild) {
        $buildResult = Invoke-NativeProcess -FilePath 'dotnet' `
            -ArgumentList @('build', $csprojPath, '-c', $Configuration, '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
            -WorkingDirectory $projectDir `
            -TimeoutMs $TimeoutMs

        if (-not $AllowBuildFailure) {
            if ($buildResult.TimedOut -or $buildResult.ExitCode -ne 0) {
                throw "Temp solver '$Name' build failed (ExitCode=$($buildResult.ExitCode), TimedOut=$($buildResult.TimedOut)):`n$($buildResult.CombinedOutput)"
            }
        }
    }

    $dllPath = Join-Path $projectDir "bin\$Configuration\net10.0\$Name.dll"
    return [PSCustomObject]@{
        Name        = $Name
        Directory   = $projectDir
        ProjectDir  = $projectDir
        CsprojPath  = $csprojPath
        ProjectPath = $csprojPath
        SourcePath  = $sourcePath
        DllPath     = $dllPath
        BuildResult = $buildResult
    }
}

function ConvertTo-E2ETierName {
    param(
        [AllowNull()]$TierValue,
        [string]$DefaultTier = 'T1'
    )

    if ($null -eq $TierValue) {
        return $DefaultTier
    }

    $rawText = ([string]$TierValue).Trim()
    if ([string]::IsNullOrWhiteSpace($rawText)) {
        return $DefaultTier
    }

    if ($rawText -ieq 'Warmup' -or $rawText -eq '0') {
        return 'Warmup'
    }

    if ($rawText -match '^[Tt]?([1-5])$') {
        return "T$($Matches[1])"
    }

    return $rawText
}

function New-E2ESyntheticDataset {
    [CmdletBinding()]
    param(
        $Workspace = $null,
        [string]$FileName = 't1_basic_combo.json',
        [string]$OutputPath = '',
        [object[]]$Puzzles = @(),
        [switch]$WriteAllStandardFiles
    )

    if ($WriteAllStandardFiles) {
        if ($null -eq $Workspace) {
            throw "-WriteAllStandardFiles requires -Workspace."
        }
        $written = [System.Collections.Generic.List[string]]::new()
        $warmupPath = Join-Path $Workspace.DatasetsDir 'warmup.json'
        if (-not (Test-Path -LiteralPath $warmupPath -PathType Leaf)) {
            $written.Add((New-E2ESyntheticDataset -Workspace $Workspace -FileName 'warmup.json'))
        }
        $tierFiles = @(
            @{ Name = 't1_basic_combo.json'; Tier = 'T1'; MaxSteps = 25; Budget = 1000; Diag = $false },
            @{ Name = 't2_step_limited.json'; Tier = 'T2'; MaxSteps = 10; Budget = 500; Diag = $false },
            @{ Name = 't3_attribute_rules.json'; Tier = 'T3'; MaxSteps = 22; Budget = 1000; Diag = $false },
            @{ Name = 't4_obstacles_diagonal.json'; Tier = 'T4'; MaxSteps = 22; Budget = 1500; Diag = $true },
            @{ Name = 't5_puzzle_shield.json'; Tier = 'T5'; MaxSteps = 25; Budget = 2000; Diag = $false }
        )
        foreach ($tf in $tierFiles) {
            $p = Join-Path $Workspace.DatasetsDir $tf.Name
            if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
                $defaultPuzzle = Get-E2EMinimalPuzzle -Id "$($tf.Tier)-SYN-01" -Tier $tf.Tier -MaxSteps $tf.MaxSteps -TimeBudgetMs $tf.Budget -AllowDiagonal $tf.Diag
                $written.Add((New-E2ESyntheticDataset -Workspace $Workspace -FileName $tf.Name -Puzzles @($defaultPuzzle)))
            }
        }
        return $written.ToArray()
    }

    $targetPath = if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath
    }
    elseif ($null -ne $Workspace) {
        Join-Path $Workspace.DatasetsDir $FileName
    }
    else {
        throw "New-E2ESyntheticDataset requires either -Workspace or -OutputPath."
    }

    $parentDir = Split-Path -Parent $targetPath
    if (-not [string]::IsNullOrWhiteSpace($parentDir)) {
        $null = New-Item -ItemType Directory -Path $parentDir -Force
    }

    $effectivePuzzles = [System.Collections.Generic.List[object]]::new()
    if (@($Puzzles).Count -eq 0) {
        if ($FileName -ieq 'warmup.json') {
            foreach ($wp in @(Get-E2EWarmupPuzzles)) {
                $effectivePuzzles.Add($wp)
            }
        }
        else {
            $tierGuess = 'T1'
            if ($FileName -match '^t([1-5])_') {
                $tierGuess = "T$($Matches[1])"
            }
            elseif ($FileName -match '^warmup') {
                $tierGuess = 'Warmup'
            }
            $effectivePuzzles.Add((Get-E2EMinimalPuzzle -Id "$tierGuess-SYN-01" -Tier $tierGuess))
        }
    }
    else {
        $defaultDictTier = if ($FileName -ieq 'warmup.json') { 'Warmup' } else { 'T1' }
        foreach ($raw in $Puzzles) {
            if ($raw -is [System.Collections.IDictionary]) {
                $normalizedTier = if ($raw.Contains('tier')) {
                    ConvertTo-E2ETierName -TierValue $raw['tier'] -DefaultTier $defaultDictTier
                }
                else {
                    $defaultDictTier
                }
                if ($raw.Contains('obstacleCoords') -and $null -ne $raw['obstacleCoords']) { $obsCoords = [object[]]$raw['obstacleCoords'] } else { $obsCoords = [object[]]@() }
                if ($raw.Contains('puzzleCoords') -and $null -ne $raw['puzzleCoords']) { $puzCoords = [object[]]$raw['puzzleCoords'] } else { $puzCoords = [object[]]@() }
                if ($raw.Contains('requiredColors') -and $null -ne $raw['requiredColors']) { $reqColors = [string[]]$raw['requiredColors'] } else { $reqColors = [string[]]@() }
                if ($raw.Contains('requireAoeColors') -and $null -ne $raw['requireAoeColors']) { $aoeColors = [string[]]$raw['requireAoeColors'] } else { $aoeColors = [string[]]@() }
                $p = [ordered]@{
                    id                          = if ($raw.Contains('id')) { $raw['id'] } else { "$normalizedTier-SYN-01" }
                    tier                        = $normalizedTier
                    description                 = if ($raw.Contains('description')) { $raw['description'] } else { 'Synthetic E2E puzzle' }
                    board                       = if ($raw.Contains('board')) { $raw['board'] } else { (New-E2EZeroMatchBoard) }
                    obstacleCoords              = $obsCoords
                    puzzleCoords                = $puzCoords
                    puzzleColor                 = if ($raw.Contains('puzzleColor')) { $raw['puzzleColor'] } else { 'Empty' }
                    maxSteps                    = if ($raw.Contains('maxSteps')) { $raw['maxSteps'] } else { 20 }
                    allowDiagonal               = if ($raw.Contains('allowDiagonal')) { [bool]$raw['allowDiagonal'] } else { $false }
                    seed                        = if ($raw.Contains('seed')) { $raw['seed'] } else { 20260101 }
                    timeBudgetMs                = if ($raw.Contains('timeBudgetMs')) { $raw['timeBudgetMs'] } else { 1000 }
                    requiredColors              = $reqColors
                    requiredColorsFirstWaveOnly = if ($raw.Contains('requiredColorsFirstWaveOnly')) { [bool]$raw['requiredColorsFirstWaveOnly'] } else { $false }
                    requireAoeColors            = $aoeColors
                    colorWeights                = if ($raw.Contains('colorWeights')) { $raw['colorWeights'] } else {
                        [ordered]@{ Water = 1.0; Fire = 1.0; Wood = 1.0; Light = 1.0; Dark = 1.0; Heart = 1.0 }
                    }
                }
                if ($raw.Contains('referenceValue')) {
                    $p['referenceValue'] = $raw['referenceValue']
                }
                elseif ($raw.Contains('vRef')) {
                    $p['vRef'] = $raw['vRef']
                }
                $effectivePuzzles.Add($p)
            }
            else {
                $effectivePuzzles.Add($raw)
            }
        }
    }

    $json = @($effectivePuzzles.ToArray()) | ConvertTo-Json -AsArray -Depth 10
    [System.IO.File]::WriteAllText($targetPath, $json, $script:Utf8NoBom)
    return $targetPath
}

function Assert-OrbSolverRunnerPrerequisite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
        [switch]$SkipBuild
    )

    $resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    $runnerCsproj = Join-Path $resolvedRepoRoot 'src\OrbSolver.Runner\OrbSolver.Runner.csproj'
    if (-not (Test-Path -LiteralPath $runnerCsproj -PathType Leaf)) {
        throw "[PREREQUISITE_MISSING] Missing required Runner project: $runnerCsproj"
    }

    if ($SkipBuild) {
        $workerCsproj = Join-Path $resolvedRepoRoot 'src\OrbSolver.Worker\OrbSolver.Worker.csproj'
        $runnerDll = Join-Path $resolvedRepoRoot "src\OrbSolver.Runner\bin\$Configuration\net10.0\OrbSolver.Runner.dll"
        $workerDll = Join-Path $resolvedRepoRoot "src\OrbSolver.Worker\bin\$Configuration\net10.0\OrbSolver.Worker.dll"
        if (-not (Test-Path -LiteralPath $runnerDll -PathType Leaf) -or
            ((Test-Path -LiteralPath $workerCsproj -PathType Leaf) -and -not (Test-Path -LiteralPath $workerDll -PathType Leaf))) {
            throw "[PREREQUISITE_MISSING] OrbSolver.Runner/Worker binary for '$Configuration' not built (and -SkipBuild was specified): $runnerDll"
        }
    }
}

function Invoke-OrbSolverRunner {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$SolversDir = '',
        [string]$DatasetsDir = '',
        [string]$OutputDir = '',
        [string]$Filter = '',
        [switch]$Dev,
        [string[]]$ExtraArgs = @(),
        [string[]]$RawArguments,
        [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
        [switch]$UseDotnetRun,
        [switch]$SkipBuild,
        [string]$WorkingDirectory = '',
        [int]$TimeoutMs = 120000
    )

    $resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
    Assert-OrbSolverRunnerPrerequisite -RepoRoot $resolvedRepoRoot -Configuration $Configuration -SkipBuild:$SkipBuild
    $runnerCsproj = Join-Path $resolvedRepoRoot 'src\OrbSolver.Runner\OrbSolver.Runner.csproj'

    $workerCsproj = Join-Path $resolvedRepoRoot 'src\OrbSolver.Worker\OrbSolver.Worker.csproj'
    $runnerDll = Join-Path $resolvedRepoRoot "src\OrbSolver.Runner\bin\$Configuration\net10.0\OrbSolver.Runner.dll"
    $workerDll = Join-Path $resolvedRepoRoot "src\OrbSolver.Worker\bin\$Configuration\net10.0\OrbSolver.Worker.dll"

    $needsBuild = (-not (Test-Path -LiteralPath $runnerDll -PathType Leaf)) -or
                  ((Test-Path -LiteralPath $workerCsproj -PathType Leaf) -and -not (Test-Path -LiteralPath $workerDll -PathType Leaf))

    if ($needsBuild) {
        if ($SkipBuild) {
            throw "[PREREQUISITE_MISSING] OrbSolver.Runner/Worker binary for '$Configuration' not built (and -SkipBuild was specified): $runnerDll"
        }

        if (Test-Path -LiteralPath $workerCsproj -PathType Leaf) {
            $workerBuild = Invoke-NativeProcess -FilePath 'dotnet' `
                -ArgumentList @('build', $workerCsproj, '-c', $Configuration, '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
                -WorkingDirectory $resolvedRepoRoot `
                -TimeoutMs 180000
            if ($workerBuild.TimedOut -or $workerBuild.ExitCode -ne 0) {
                throw "Failed to build OrbSolver.Worker ($Configuration):`n$($workerBuild.CombinedOutput)"
            }
        }

        $runnerBuild = Invoke-NativeProcess -FilePath 'dotnet' `
            -ArgumentList @('build', $runnerCsproj, '-c', $Configuration, '--disable-build-servers', '-p:UseSharedCompilation=false', '-m:1', '--nologo') `
            -WorkingDirectory $resolvedRepoRoot `
            -TimeoutMs 180000
        if ($runnerBuild.TimedOut -or $runnerBuild.ExitCode -ne 0) {
            throw "Failed to build OrbSolver.Runner ($Configuration):`n$($runnerBuild.CombinedOutput)"
        }
    }

    $argList = [System.Collections.Generic.List[string]]::new()
    if ($PSBoundParameters.ContainsKey('RawArguments')) {
        foreach ($ra in $RawArguments) {
            $argList.Add($ra)
        }
    }
    else {
        if (-not [string]::IsNullOrWhiteSpace($SolversDir)) {
            $argList.Add('--solvers')
            $argList.Add($SolversDir)
        }
        if (-not [string]::IsNullOrWhiteSpace($DatasetsDir)) {
            $argList.Add('--datasets')
            $argList.Add($DatasetsDir)
        }
        if (-not [string]::IsNullOrWhiteSpace($OutputDir)) {
            $argList.Add('--output')
            $argList.Add($OutputDir)
        }
        if (-not [string]::IsNullOrWhiteSpace($Filter)) {
            $argList.Add('--filter')
            $argList.Add($Filter)
        }
        if ($Dev) {
            $argList.Add('--dev')
        }
        foreach ($ea in $ExtraArgs) {
            $argList.Add($ea)
        }
    }

    $workDir = if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) { $resolvedRepoRoot } else { $WorkingDirectory }

    $procRes = if ($UseDotnetRun) {
        $runArgs = @('run', '--project', $runnerCsproj, '-c', $Configuration, '--no-build', '--') + $argList.ToArray()
        Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList $runArgs -WorkingDirectory $workDir -TimeoutMs $TimeoutMs
    }
    else {
        $execArgs = @($runnerDll) + $argList.ToArray()
        Invoke-NativeProcess -FilePath 'dotnet' -ArgumentList $execArgs -WorkingDirectory $workDir -TimeoutMs $TimeoutMs
    }

    $jsonFiles = @()
    $mdFiles = @()
    if (-not [string]::IsNullOrWhiteSpace($OutputDir) -and (Test-Path -LiteralPath $OutputDir -PathType Container)) {
        $jsonFiles = @(Get-ChildItem -LiteralPath $OutputDir -Filter '*.json' -File | Sort-Object LastWriteTimeUtc -Descending | ForEach-Object { $_.FullName })
        $mdFiles = @(Get-ChildItem -LiteralPath $OutputDir -Filter '*.md' -File | Sort-Object LastWriteTimeUtc -Descending | ForEach-Object { $_.FullName })
    }

    $latestJson = if ($jsonFiles.Count -gt 0) { $jsonFiles[0] } else { '' }
    $latestMd = if ($mdFiles.Count -gt 0) { $mdFiles[0] } else { '' }

    return [PSCustomObject]@{
        ExitCode           = $procRes.ExitCode
        Stdout             = $procRes.Stdout
        Stderr             = $procRes.Stderr
        CombinedOutput     = $procRes.CombinedOutput
        TimedOut           = $procRes.TimedOut
        DurationMs         = $procRes.DurationMs
        JsonFiles          = $jsonFiles
        MarkdownFiles      = $mdFiles
        LatestJsonPath     = $latestJson
        LatestMarkdownPath = $latestMd
    }
}

function Get-ReportJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "JSON report file not found: $Path"
    }

    $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    return ($raw | ConvertFrom-Json -Depth 32)
}

function Get-ReportMarkdown {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Markdown report file not found: $Path"
    }

    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Find-JsonSolverNames {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$ReportJson
    )

    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $ordered = [System.Collections.Generic.List[string]]::new()

    foreach ($obj in @(Get-AllJsonObjects -Node $ReportJson)) {
        $solverName = Get-JsonPropertyCI -Object $obj -CandidateNames @('SolverName', 'Name')
        if ($solverName -is [string] -and -not [string]::IsNullOrWhiteSpace($solverName)) {
            $hasSolverMarkers = ($null -ne (Get-JsonPropertyCI -Object $obj -CandidateNames @('ModelTag', 'Mode', 'ExecutionMode', 'Cases', 'CaseResults', 'Results', 'TotalScore', 'WeightedScore', 'TierScores', 'TierAverages')))
            if ($hasSolverMarkers) {
                if ($names.Add($solverName)) {
                    $ordered.Add($solverName)
                }
            }
        }
    }

    return $ordered.ToArray()
}

function Get-JsonSolverCases {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$ReportJson,
        [Parameter(Mandatory)][string]$SolverName
    )

    $cases = [System.Collections.Generic.List[object]]::new()
    foreach ($obj in @(Get-AllJsonObjects -Node $ReportJson)) {
        $nameVal = Get-JsonPropertyCI -Object $obj -CandidateNames @('SolverName', 'Name')
        if ($nameVal -is [string] -and [string]::Equals($nameVal, $SolverName, [StringComparison]::OrdinalIgnoreCase)) {
            $caseList = Get-JsonPropertyCI -Object $obj -CandidateNames @('Cases', 'CaseResults', 'PuzzleResults', 'Results')
            if ($null -ne $caseList) {
                foreach ($c in @($caseList)) {
                    if ($null -ne $c) {
                        $cases.Add($c)
                    }
                }
            }
        }
        else {
            $caseSolver = Get-JsonPropertyCI -Object $obj -CandidateNames @('SolverName', 'Solver')
            $caseId = Get-JsonPropertyCI -Object $obj -CandidateNames @('PuzzleId', 'CaseId', 'Id')
            $caseScore = Get-JsonPropertyCI -Object $obj -CandidateNames @('CaseScore', 'Score')
            if (($caseSolver -is [string]) -and [string]::Equals($caseSolver, $SolverName, [StringComparison]::OrdinalIgnoreCase) -and ($null -ne $caseId) -and ($null -ne $caseScore)) {
                $cases.Add($obj)
            }
        }
    }

    return $cases.ToArray()
}

function Test-JsonFingerprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$ReportJson
    )

    $fp = Get-JsonPropertyCI -Object $ReportJson -CandidateNames @('EnvironmentFingerprint', 'Fingerprint', 'Environment')
    if ($null -eq $fp) {
        return $false
    }

    $osVal = Get-JsonPropertyCI -Object $fp -CandidateNames @('OS', 'OsDescription', 'OperatingSystem')
    $runtimeVal = Get-JsonPropertyCI -Object $fp -CandidateNames @('DotNetVersion', 'RuntimeVersion', 'FrameworkDescription', 'Runtime')
    $buildModeVal = Get-JsonPropertyCI -Object $fp -CandidateNames @('BuildMode', 'Configuration', 'BuildConfiguration')

    return (-not [string]::IsNullOrWhiteSpace([string]$osVal)) -and
           (-not [string]::IsNullOrWhiteSpace([string]$runtimeVal)) -and
           (-not [string]::IsNullOrWhiteSpace([string]$buildModeVal))
}

function Assert-True {
    [CmdletBinding()]
    param(
        [AllowNull()][Parameter(Mandatory)]$Condition,
        [string]$Message = 'Assertion failed: expected condition to be true.'
    )

    if (-not [bool]$Condition) {
        throw $Message
    }
}

function Assert-False {
    [CmdletBinding()]
    param(
        [AllowNull()][Parameter(Mandatory)]$Condition,
        [string]$Message = 'Assertion failed: expected condition to be false.'
    )

    if ([bool]$Condition) {
        throw $Message
    }
}

function Test-E2ESequence {
    param(
        [AllowNull()]$Value
    )

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [string] -or $Value -is [System.Collections.IDictionary] -or $Value -is [System.Xml.XmlNode]) {
        return $false
    }

    return ($Value -is [System.Collections.IEnumerable])
}

function Test-E2EValuesEqual {
    param(
        [AllowNull()]$Left,
        [AllowNull()]$Right
    )

    if ($null -eq $Left -and $null -eq $Right) {
        return $true
    }

    if ($null -eq $Left -or $null -eq $Right) {
        return $false
    }

    $isLeftSeq = Test-E2ESequence -Value $Left
    $isRightSeq = Test-E2ESequence -Value $Right
    if ($isLeftSeq -ne $isRightSeq) {
        return $false
    }

    if ($isLeftSeq -and $isRightSeq) {
        $leftItems = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Left) {
            $leftItems.Add($item)
        }

        $rightItems = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Right) {
            $rightItems.Add($item)
        }

        if ($leftItems.Count -ne $rightItems.Count) {
            return $false
        }

        for ($i = 0; $i -lt $leftItems.Count; $i++) {
            if (-not (Test-E2EValuesEqual -Left $leftItems[$i] -Right $rightItems[$i])) {
                return $false
            }
        }

        return $true
    }

    if (($Left -is [bool]) -ne ($Right -is [bool])) {
        return $false
    }

    return ($Left -eq $Right)
}

function Assert-Equal {
    [CmdletBinding()]
    param(
        $Expected,
        $Actual,
        [string]$Message = ''
    )

    $equal = Test-E2EValuesEqual -Left $Expected -Right $Actual

    if (-not $equal) {
        $detail = if ([string]::IsNullOrWhiteSpace($Message)) {
            "Assertion failed: Expected '$Expected', Actual '$Actual'."
        }
        else {
            "$Message (Expected '$Expected', Actual '$Actual')"
        }
        throw $detail
    }
}

function Assert-GreaterThan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Threshold,
        [string]$Message = ''
    )

    if (-not ($Actual -gt $Threshold)) {
        $detail = if ([string]::IsNullOrWhiteSpace($Message)) {
            "Assertion failed: Expected '$Actual' > '$Threshold'."
        }
        else {
            "$Message (Actual '$Actual', Threshold '$Threshold')"
        }
        throw $detail
    }
}

function Assert-LessOrEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Threshold,
        [string]$Message = ''
    )

    if (-not ($Actual -le $Threshold)) {
        $detail = if ([string]::IsNullOrWhiteSpace($Message)) {
            "Assertion failed: Expected '$Actual' <= '$Threshold'."
        }
        else {
            "$Message (Actual '$Actual', Threshold '$Threshold')"
        }
        throw $detail
    }
}

function Assert-Match {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$Text = '',
        [Parameter(Mandatory)][string]$Pattern,
        [string]$Message = ''
    )

    if (-not ($Text -match $Pattern)) {
        $detail = if ([string]::IsNullOrWhiteSpace($Message)) {
            "Assertion failed: Text did not match regex '$Pattern'. Text snippet: $($Text.Substring(0, [Math]::Min(300, $Text.Length)))"
        }
        else {
            "$Message (Pattern '$Pattern')"
        }
        throw $detail
    }
}

function Assert-Contains {
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()][AllowEmptyCollection()][Parameter(Mandatory)]$CollectionOrText,
        [Parameter(Mandatory)]$Expected,
        [string]$Message = ''
    )

    if ($null -eq $CollectionOrText) {
        throw $(if ([string]::IsNullOrWhiteSpace($Message)) { "Assertion failed: CollectionOrText was null when expecting '$Expected'." } else { "$Message (null input)" })
    }

    $found = $false
    if ($CollectionOrText -is [string]) {
        $found = $CollectionOrText.Contains([string]$Expected, [StringComparison]::Ordinal)
    }
    elseif ($CollectionOrText -is [System.Collections.IEnumerable]) {
        foreach ($item in $CollectionOrText) {
            if ($item -eq $Expected) {
                $found = $true
                break
            }
        }
    }
    else {
        throw "Assert-Contains requires a string or IEnumerable."
    }

    if (-not $found) {
        $detail = if ([string]::IsNullOrWhiteSpace($Message)) {
            "Assertion failed: Expected to find '$Expected'."
        }
        else {
            "$Message (Missing '$Expected')"
        }
        throw $detail
    }
}

function Assert-Throws {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [string]$MessagePattern = '',
        [string]$Message = ''
    )

    $threw = $false
    $caughtMsg = ''
    try {
        & $ScriptBlock
    }
    catch {
        $threw = $true
        $caughtMsg = $_.Exception.Message
    }

    if (-not $threw) {
        $detail = if ([string]::IsNullOrWhiteSpace($Message)) {
            "Assertion failed: Expected scriptblock to throw, but it completed without error."
        }
        else {
            $Message
        }
        throw $detail
    }

    if (-not [string]::IsNullOrWhiteSpace($MessagePattern) -and -not ($caughtMsg -match $MessagePattern)) {
        throw "Assertion failed: Thrown exception message '$caughtMsg' did not match '$MessagePattern'."
    }
}

Export-ModuleMember -Function @(
    'Register-E2ETest',
    'Get-E2ETests',
    'Clear-E2ETests',
    'New-E2ETempWorkspace',
    'Remove-E2ETempWorkspace',
    'Get-TrackedE2ETempWorkspaces',
    'Clear-TrackedE2ETempWorkspaces',
    'Invoke-NativeProcess',
    'New-E2ETempSolver',
    'New-E2ESyntheticDataset',
    'Assert-OrbSolverRunnerPrerequisite',
    'Invoke-OrbSolverRunner',
    'Get-ReportJson',
    'Get-ReportMarkdown',
    'Find-JsonSolverNames',
    'Get-JsonSolverCases',
    'Test-JsonFingerprint',
    'Get-E2EFixtureContent',
    'Assert-True',
    'Assert-False',
    'Assert-Equal',
    'Assert-GreaterThan',
    'Assert-LessOrEqual',
    'Assert-Match',
    'Assert-Contains',
    'Assert-Throws'
)
