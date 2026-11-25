using System.Text.Json;
using OrbSolver.Core;

namespace OrbSolver.Runner;

internal static class WorkerIpcJson
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
    float[] ColorWeights)
{
    public static PuzzlePayload FromDatasetCase(DatasetCase puzzle)
    {
        var cellsSpan = puzzle.Cells.Span;
        byte[] cellBytes = new byte[cellsSpan.Length];
        for (int i = 0; i < cellsSpan.Length; i++)
        {
            cellBytes[i] = (byte)cellsSpan[i];
        }

        return new PuzzlePayload(
            Id: puzzle.Id,
            Cells: cellBytes,
            ObstacleMask: puzzle.ObstacleMask,
            PuzzleMask: puzzle.PuzzleMask,
            PuzzleColor: (byte)puzzle.PuzzleColor,
            MaxSteps: puzzle.MaxSteps,
            AllowDiagonal: puzzle.AllowDiagonal,
            Seed: puzzle.Seed,
            TimeBudgetMs: puzzle.TimeBudgetMs,
            RequiredColorsMask: puzzle.RequiredColorsMask,
            RequiredColorsFirstWaveOnly: puzzle.RequiredColorsFirstWaveOnly,
            RequireAoeColorMask: puzzle.RequireAoeColorMask,
            ColorWeights: puzzle.ColorWeights.ToArray());
    }
}

internal sealed record InitRequest(
    string SolverAssemblyPath,
    PuzzlePayload[] WarmupPuzzles)
{
    public string Type => "init";
}

internal sealed record SolveRequest(
    string Id,
    PuzzlePayload Puzzle)
{
    public string Type => "solve";
}

internal sealed record ShutdownRequest
{
    public string Type => "shutdown";
}

internal sealed record SolverLoadedMessage(
    string SolverName,
    string ModelTag,
    byte Mode);

internal sealed record InitResultMessage(
    bool Success,
    string? SolverName,
    string? ModelTag,
    byte Mode,
    string? ErrorType,
    string? ErrorMessage);

internal sealed record ProgressMessage(
    string Id,
    int Iteration,
    string Phase);

internal sealed record SolveResultMessage(
    string Id,
    string Status,
    string? ErrorMessage,
    byte StartRow,
    byte StartCol,
    byte[]? Moves,
    double WallMs,
    double CpuMs,
    long AllocatedBytes,
    double GcPauseMs,
    int Gen0,
    int Gen1,
    int Gen2);
