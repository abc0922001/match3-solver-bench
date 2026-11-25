using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrbSolver.Runner;

internal static class ReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public sealed record GeneratedReportPaths(string JsonPath, string MarkdownPath);

    private sealed record BenchmarkJsonReport(
        DateTimeOffset GeneratedAtUtc,
        EnvironmentFingerprint EnvironmentFingerprint,
        string Configuration,
        LeaderboardsSection Leaderboards,
        IReadOnlyList<SolverReportRecord> Solvers);

    private sealed record LeaderboardsSection(
        IReadOnlyList<LeaderboardEntry> SingleThread,
        IReadOnlyList<LeaderboardEntry> MultiThread);

    public static GeneratedReportPaths WriteReports(BenchmarkRunResult result, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string fullOutputDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDir);

        string stamp = result.Fingerprint.TimestampUtc.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        (string jsonPath, string mdPath) = ResolveUniqueReportPaths(fullOutputDir, stamp);

        string jsonContent = BuildJson(result);
        string mdContent = BuildMarkdown(result);

        File.WriteAllText(jsonPath, jsonContent, Utf8NoBom);
        File.WriteAllText(mdPath, mdContent, Utf8NoBom);

        return new GeneratedReportPaths(jsonPath, mdPath);
    }

    internal static string BuildJson(BenchmarkRunResult result)
    {
        var dto = new BenchmarkJsonReport(
            GeneratedAtUtc: result.Fingerprint.TimestampUtc,
            EnvironmentFingerprint: result.Fingerprint,
            Configuration: result.Configuration,
            Leaderboards: new LeaderboardsSection(
                SingleThread: result.SingleThreadLeaderboard,
                MultiThread: result.MultiThreadLeaderboard),
            Solvers: result.Solvers);

        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    internal static string BuildMarkdown(BenchmarkRunResult result)
    {
        var sb = new StringBuilder();
        EnvironmentFingerprint fp = result.Fingerprint;

        sb.AppendLine("# OrbSolver 基準評測報告");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **產生時間（UTC）**：`{fp.TimestampUtc:yyyy-MM-ddTHH:mm:ssZ}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **建置模式**：`{fp.BuildMode}`");
        sb.AppendLine();

        sb.AppendLine("## 環境指紋");
        sb.AppendLine();
        sb.AppendLine("| 項目 | 數值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| 作業系統 | {EscapeMd(fp.OS)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| 處理器 | {EscapeMd(fp.CpuModel)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| 邏輯核心數 | {fp.LogicalCores} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| 系統架構 | {fp.Architecture} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| .NET 執行期 | {EscapeMd(fp.DotNetVersion)} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Server GC | {(fp.IsServerGC ? "啟用" : "停用")} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| 建置模式 | {EscapeMd(fp.BuildMode)} |");
        sb.AppendLine();

        AppendLeaderboardTable(sb, "單執行緒排行榜", result.SingleThreadLeaderboard);
        AppendLeaderboardTable(sb, "多執行緒排行榜", result.MultiThreadLeaderboard);

        sb.AppendLine("## 解題器詳細成績");
        sb.AppendLine();

        foreach (SolverReportRecord solver in result.Solvers)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {solver.SolverName}（`{solver.ModelTag}`，`{FormatModeLabel(solver.Mode)}`）");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **總分**：`{solver.TotalScore:F2}`");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **峰值工作集**：`{solver.PeakWorkingSetBytes / (1024.0 * 1024.0):F2} MiB`（`{solver.PeakWorkingSetBytes}` 位元組）");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **峰值分頁記憶體**：`{solver.PeakPagedMemoryBytes / (1024.0 * 1024.0):F2} MiB`（`{solver.PeakPagedMemoryBytes}` 位元組）");
            sb.AppendLine();

            sb.AppendLine("| 層級 | 題數 | 平均分 | 權重 | 加權貢獻 |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (TierSummaryRecord tier in solver.TierSummaries)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {tier.Tier} | {tier.CaseCount} | {tier.AverageScore:F2} | {tier.Weight:P0} | {tier.WeightedContribution:F2} |");
            }
            sb.AppendLine();

            sb.AppendLine("| 題號 | 層級 | 狀態 | 單題分數 | 品質分 | 速度分 | 記憶體分 | 原始價值 V | 連擊數 | 掛鐘 (ms) | CPU (ms) | 配置量 (B) | GC 停頓 (ms) |");
            sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (ScoredCaseRecord c in solver.Cases)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {c.PuzzleId} | {c.Tier} | {c.Status} | {c.CaseScore:F2} | {c.QualityScore:F2} | {c.SpeedScore:F2} | {c.MemoryScore:F2} | {c.RawValue:F1} | {c.TotalCombos} | {c.WallMs:F3} | {c.CpuMs:F3} | {c.AllocatedBytes} | {c.GcPauseMs:F3} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendLeaderboardTable(
        StringBuilder sb,
        string heading,
        IReadOnlyList<LeaderboardEntry> entries)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {heading}");
        sb.AppendLine();

        if (entries.Count == 0)
        {
            sb.AppendLine("_此執行模式目前無評測之解題器。_");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| 名次 | 解題器 | 模型標籤 | 總分 | T1 | T2 | T3 | T4 | T5 | 掛鐘中位數 (ms) | 峰值工作集 (MiB) |");
        sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (LeaderboardEntry e in entries)
        {
            double t1 = GetTierAvg(e.TierScores, "T1");
            double t2 = GetTierAvg(e.TierScores, "T2");
            double t3 = GetTierAvg(e.TierScores, "T3");
            double t4 = GetTierAvg(e.TierScores, "T4");
            double t5 = GetTierAvg(e.TierScores, "T5");
            double peakWsMib = e.PeakWorkingSetBytes / (1024.0 * 1024.0);

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {e.Rank} | {EscapeMd(e.SolverName)} | {EscapeMd(e.ModelTag)} | {e.TotalScore:F2} | {t1:F2} | {t2:F2} | {t3:F2} | {t4:F2} | {t5:F2} | {e.MedianWallMs:F3} | {peakWsMib:F2} |");
        }

        sb.AppendLine();
    }

    private static string FormatModeLabel(string mode) => mode switch
    {
        "SingleThread" => "單執行緒",
        "MultiThread" => "多執行緒",
        _ => mode
    };

    private static double GetTierAvg(IReadOnlyList<TierSummaryRecord> tiers, string tierName)
    {
        for (int i = 0; i < tiers.Count; i++)
        {
            if (string.Equals(tiers[i].Tier, tierName, StringComparison.OrdinalIgnoreCase))
            {
                return tiers[i].AverageScore;
            }
        }

        return 0.0;
    }

    private static (string JsonPath, string MdPath) ResolveUniqueReportPaths(string outputDir, string stamp)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            string suffix = attempt == 0 ? stamp : $"{stamp}_{attempt}";
            string jsonPath = Path.Combine(outputDir, $"benchmark_report_{suffix}.json");
            string mdPath = Path.Combine(outputDir, $"benchmark_report_{suffix}.md");
            if (!File.Exists(jsonPath) && !File.Exists(mdPath))
            {
                return (jsonPath, mdPath);
            }
        }

        string fallback = $"{stamp}_{Guid.NewGuid():N}";
        return (
            Path.Combine(outputDir, $"benchmark_report_{fallback}.json"),
            Path.Combine(outputDir, $"benchmark_report_{fallback}.md"));
    }

    private static string EscapeMd(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);
}
