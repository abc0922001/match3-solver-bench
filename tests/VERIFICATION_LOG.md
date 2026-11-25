# OrbSolverBenchmark 驗證紀錄

本紀錄採集於最終 Release 組態建置完成後（`2026-11-25T11:57:52Z`），所在 Git 分支為 `teamwork`。

## 1. 環境與方案建置

執行指令：
```powershell
dotnet build -c Release
```
實際結果（`ExitCode = 0`）：
- `match3-solver-bench.sln` 內全部 10 個專案皆順利建置完成（`0` 個警告、`0` 個錯誤）：
  - `OrbSolver.Contracts`
  - `OrbSolver.Core`
  - `OrbSolver.Worker`
  - `OrbSolver.Runner`
  - `Baseline.Random`
  - `Baseline.ReferenceBeam`
  - `Solver.Template`
  - `OrbSolver.Core.Tests`
  - `OrbSolver.Runner.Tests`
  - `OrbSolver.Security.Tests`

## 2. 單元測試、整合測試與資料集結構驗證

執行指令：
```powershell
dotnet test -c Release --no-build --logger "console;verbosity=normal"
```
實際結果（`ExitCode = 0`，總計 `142/142` 通過）：
- `OrbSolver.Core.Tests`：`102 / 102` 通過（`0` 失敗、`0` 略過）——涵蓋 `PathValidator`、`BoardSimulator`（4/8 方向移動、無天降連鎖、十字／L 型交叉 Combo 合併、障礙物阻擋、包含外部沾連失效的嚴格拼圖盾）、`DatasetLoader`、`ScoringEngine` 與 `RepositoryDatasetTests`（驗證 `warmup.json` 共 3 題與 `t1..t5` 共 50 題皆滿足 0 預先消除珠子、有效層級規格與可自行消除之 T5 形狀，以及 `docs/prompt_template.md` 與 `solvers/Solver.Template` 檢查）。
- `OrbSolver.Runner.Tests`：`33 / 33` 通過（`0` 失敗、`0` 略過）——涵蓋 `RunnerOptions`、`SolverDiscovery`、`EnvironmentFingerprint`、`BenchmarkCoordinator`、`ReportGenerator` 與 `WorkerIntegrationTests`。
- `OrbSolver.Security.Tests`：`7 / 7` 通過（`0` 失敗、`0` 略過）。

## 3. 防作弊 MSBuild 政策與惡意解題器隔離實證

執行指令：
```powershell
dotnet test tests/OrbSolver.Security.Tests/OrbSolver.Security.Tests.csproj -c Release --no-build --logger "console;verbosity=detailed"
```
實際輸出（`ExitCode = 0`，`7 / 7` 通過）：
- `AntiCheatPolicyTests.SolutionExcludesIntentionalFailingFixtures`：通過（`tests/Fixtures/` 測試專案已正確排除於 `match3-solver-bench.sln` 之外）。
- `AntiCheatPolicyTests.NuGetPackageReference_IsBlockedAtCompileTime`：
  ```text
  ExitCode=1
  solvers\Directory.Build.targets(3,5): error : [Anti-Cheat] Solver project 'Fixture.NuGetCheat' is forbidden from referencing external NuGet packages: xunit [tests\Fixtures\AntiCheat\Fixture.NuGetCheat\Fixture.NuGetCheat.csproj]
  ```
- `AntiCheatPolicyTests.AllowUnsafeBlocks_IsBlockedAtCompileTime`：
  ```text
  ExitCode=1
  solvers\Directory.Build.targets(6,5): error : [Anti-Cheat] Solver project 'Fixture.UnsafeCheat' is forbidden from enabling AllowUnsafeBlocks. [tests\Fixtures\AntiCheat\Fixture.UnsafeCheat\Fixture.UnsafeCheat.csproj]
  ```
- `AntiCheatPolicyTests.OrbSolverCoreReference_IsBlockedAtCompileTime`：
  ```text
  ExitCode=1
  solvers\Directory.Build.targets(13,5): error : [Anti-Cheat] Solver project 'Fixture.CoreRefCheat' may only reference OrbSolver.Contracts. Found: ..\..\..\..\src\OrbSolver.Core\OrbSolver.Core.csproj [tests\Fixtures\AntiCheat\Fixture.CoreRefCheat\Fixture.CoreRefCheat.csproj]
  ```
- `MaliciousIsolationTests.InfiniteLoopSolver_IsHardKilledAsTle_AndNextCaseRecovers`：
  ```text
  [TLE] Status=TLE, Score=0.00, Reason=Exceeded hard timeout (750 ms).
  [TLE-Recovery] Status=OK, Score=100.00
  ```
- `MaliciousIsolationTests.OomSolver_ExceedingGcHeapHardLimit_IsIntercepted_AndNextCaseRecovers`：
  ```text
  [OOM] Status=OOM, Score=0.00, Reason=OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.
  [OOM-Recovery] Status=OK, Score=100.00
  ```
- `MaliciousIsolationTests.SingleThreadSolverSpawningThreads_IsMarkedRuleViolation_AndNextCaseRecovers`：
  ```text
  [MT] Status=RULE_VIOLATION, Score=0.00, WallMs=80.11, CpuMs=1120.65, Reason=SingleThread rule violation: CpuMs=1120.65 > WallMs*1.15 (92.13).
  [MT-Recovery] Status=OK, Score=100.00
  ```

## 4. PowerShell 7 端對端測試套件（`tests/E2E/Invoke-E2ETests.ps1`）

執行指令：
```powershell
pwsh -NoProfile -File .\tests\E2E\Invoke-E2ETests.ps1 -SkipBuild
```
實際結果（`ExitCode = 0`）：
```text
Total: 60 | Passed: 60 | Failed: 0 | Duration: 174474 ms
```
（涵蓋 Tier 1 與 Tier 2 治理規則 `.sln`/`.gitignore`/`Directory.*`、MSBuild `[Anti-Cheat]` 防作弊檢查、`OrbSolver.Runner` 命令列介面與 Release 建置防護、`Solver.Template` 預設略過與 `--filter` 指定行為、`OrbSolver.Contracts` 不可變性，以及 `docs/prompt_template.md` / `README.md` 驗證項目。）

## 5. 正式 Release 基準評測執行（全部 50 題 T1–T5 題庫 + 3 題暖身）

執行指令：
```powershell
dotnet run --project src/OrbSolver.Runner -c Release --no-build -- --solvers ./solvers --datasets ./datasets --output ./reports
```
實際主控台輸出（`ExitCode = 0`）：
```text
Running solver 'Baseline.Random'...
Running solver 'Baseline.ReferenceBeam'...
JSON report written to: C:\Users\DIDI\Documents\Git\match3-solver-bench\reports\benchmark_report_20261125_115752_378.json
Markdown report written to: C:\Users\DIDI\Documents\Git\match3-solver-bench\reports\benchmark_report_20261125_115752_378.md
```

環境指紋：
- 作業系統：`Microsoft Windows 10.0.19045`
- 處理器：`12th Gen Intel(R) Core(TM) i7-12700K`（`20` 個邏輯核心、`X64`）
- .NET 執行期：`.NET 10.0.15`（`Server GC: 停用`、`建置模式: Release`）

單執行緒排行榜摘要：
| 名次 | 解題器 | 模型標籤 | 總分 | T1 | T2 | T3 | T4 | T5 | 掛鐘中位數 (ms) | 峰值工作集 (MiB) |
|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | Baseline.ReferenceBeam | baseline-reference-beam | 97.26 | 100.00 | 100.00 | 98.51 | 100.00 | 90.25 | 3.897 | 33.13 |
| 2 | Baseline.Random | baseline-random | 38.56 | 43.03 | 36.52 | 38.33 | 38.78 | 37.07 | 0.004 | 29.03 |

- `Solver.Template` 在預設目錄掃描時已正確略過。
- `多執行緒排行榜`區段呈現空榜狀態（`_此執行模式目前無評測之解題器。_`）。
- `Baseline.ReferenceBeam` 達成 `50/50` 題全數 `OK`（總分 `97.26` 對比 `Baseline.Random` 的 `38.56`），並在 Markdown 與 JSON 報告中完整記錄三層記憶體指標（`AllocatedBytes`、`GcPauseMs`、`PeakWorkingSetBytes`、`PeakPagedMemoryBytes`）與雙軌計時數據（`WallMs`、`CpuMs`）。
