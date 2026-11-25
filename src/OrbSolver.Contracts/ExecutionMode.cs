namespace OrbSolver.Contracts;

/// <summary>
/// 宣告 <see cref="IOrbSolver"/> 的執行並行模型，用於排行榜分組與執行階段反作弊規範檢查。
/// </summary>
public enum ExecutionMode : byte
{
    /// <summary>
    /// 單執行緒求解器。禁止產生平行背景工作執行緒，並由 Runner 強制執行檢查
    /// （當 <c>WallTime &gt;= 20 ms</c> 且 <c>CpuTime &gt; WallTime * 1.15</c> 時會觸發 <c>RULE_VIOLATION</c>）。
    /// </summary>
    SingleThread = 0,

    /// <summary>
    /// 多執行緒求解器，允許跨 CPU 核心進行平行搜尋。
    /// </summary>
    MultiThread = 1
}
