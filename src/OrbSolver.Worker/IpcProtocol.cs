using System.Text.Json;

namespace OrbSolver.Worker;

internal static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

internal sealed record PuzzlePayload(
    string Id,
    byte[] Cells,
    uint ObstacleMask,
    uint PuzzleMask,
    byte PuzzleColor,
    int MaxSteps,
    bool AllowDiagonal,
    int Seed,
    int TimeBudgetMs,
    byte RequiredColorsMask,
    bool RequiredColorsFirstWaveOnly,
    byte RequireAoeColorMask,
    float[] ColorWeights);

internal sealed record InitRequest(
    string Type,
    string SolverAssemblyPath,
    PuzzlePayload[] WarmupPuzzles);

internal sealed record SolveRequest(
    string Type,
    string Id,
    PuzzlePayload Puzzle);

internal sealed record SolverLoadedMessage(
    string SolverName,
    string ModelTag,
    byte Mode)
{
    public string Type => "solverLoaded";
}

internal sealed record InitResultMessage(
    bool Success,
    string? SolverName,
    string? ModelTag,
    byte Mode,
    string? ErrorType,
    string? ErrorMessage)
{
    public string Type => "initResult";
}

internal sealed record ProgressMessage(
    string Id,
    int Iteration,
    string Phase)
{
    public string Type => "progress";
}

internal sealed record SolveResultMessage(
    string Id,
    string Status,
    string? ErrorMessage,
    byte StartRow,
    byte StartCol,
    byte[] Moves,
    double WallMs,
    double CpuMs,
    long AllocatedBytes,
    double GcPauseMs,
    int Gen0,
    int Gen1,
    int Gen2)
{
    public string Type => "solveResult";
}

internal sealed record ShutdownAckMessage
{
    public string Type => "shutdownAck";
}
