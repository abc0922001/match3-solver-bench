using System.Text;
using System.Text.Json;
using OrbSolver.Contracts;

namespace OrbSolver.Worker;

internal static class Program
{
    public static int Main(string[] args)
    {
        // 在載入任何外掛之前，先擷取專用的 UTF-8 NDJSON stdout 串流，並將求解器的 Console 輸出設為靜音。
        using Stream stdoutStream = Console.OpenStandardOutput();
        using TextWriter ipcWriter = TextWriter.Synchronized(
            new StreamWriter(stdoutStream, new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        using var stdinReader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

        IOrbSolver? loadedSolver = null;

        while (stdinReader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? requestType = null;
            string? requestId = null;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement typeEl))
                {
                    WriteMessage(ipcWriter, new SolveResultMessage(
                        Id: string.Empty,
                        Status: "RE",
                        ErrorMessage: "Malformed IPC request: missing 'type' discriminator.",
                        StartRow: 0,
                        StartCol: 0,
                        Moves: [],
                        WallMs: 0.0,
                        CpuMs: 0.0,
                        AllocatedBytes: 0L,
                        GcPauseMs: 0.0,
                        Gen0: 0,
                        Gen1: 0,
                        Gen2: 0));
                    continue;
                }

                requestType = typeEl.GetString();
                if (root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    requestId = idEl.GetString();
                }

                switch (requestType)
                {
                    case "init":
                    {
                        InitRequest? req = root.Deserialize<InitRequest>(IpcJson.Options);
                        if (req is null)
                        {
                            WriteMessage(ipcWriter, new InitResultMessage(false, null, null, 0, "LOAD_ERROR", "Failed to deserialize init request."));
                            break;
                        }

                        SolverLoadOutcome outcome = SolverLoader.Load(req.SolverAssemblyPath);
                        if (!outcome.Success || outcome.Solver is null)
                        {
                            loadedSolver = null;
                            WriteMessage(ipcWriter, new InitResultMessage(
                                Success: false,
                                SolverName: outcome.SolverName,
                                ModelTag: outcome.ModelTag,
                                Mode: (byte)outcome.Mode,
                                ErrorType: outcome.ErrorType ?? "LOAD_ERROR",
                                ErrorMessage: outcome.ErrorMessage));
                            break;
                        }

                        loadedSolver = outcome.Solver;
                        WriteMessage(ipcWriter, new SolverLoadedMessage(
                            outcome.SolverName!,
                            outcome.ModelTag ?? string.Empty,
                            (byte)outcome.Mode));

                        BenchmarkExecutor.RunWarmup(
                            loadedSolver,
                            req.WarmupPuzzles ?? [],
                            progress => WriteMessage(ipcWriter, progress));

                        WriteMessage(ipcWriter, new InitResultMessage(
                            Success: true,
                            SolverName: outcome.SolverName,
                            ModelTag: outcome.ModelTag,
                            Mode: (byte)outcome.Mode,
                            ErrorType: null,
                            ErrorMessage: null));
                        break;
                    }

                    case "solve":
                    {
                        SolveRequest? req = root.Deserialize<SolveRequest>(IpcJson.Options);
                        string id = req?.Id ?? requestId ?? string.Empty;

                        if (loadedSolver is null)
                        {
                            WriteMessage(ipcWriter, new SolveResultMessage(
                                Id: id,
                                Status: "RE",
                                ErrorMessage: "Worker received 'solve' before a valid solver was initialized.",
                                StartRow: 0,
                                StartCol: 0,
                                Moves: [],
                                WallMs: 0.0,
                                CpuMs: 0.0,
                                AllocatedBytes: 0L,
                                GcPauseMs: 0.0,
                                Gen0: 0,
                                Gen1: 0,
                                Gen2: 0));
                            break;
                        }

                        if (req?.Puzzle is null)
                        {
                            WriteMessage(ipcWriter, new SolveResultMessage(
                                Id: id,
                                Status: "RE",
                                ErrorMessage: "Solve request missing puzzle payload.",
                                StartRow: 0,
                                StartCol: 0,
                                Moves: [],
                                WallMs: 0.0,
                                CpuMs: 0.0,
                                AllocatedBytes: 0L,
                                GcPauseMs: 0.0,
                                Gen0: 0,
                                Gen1: 0,
                                Gen2: 0));
                            break;
                        }

                        SolveResultMessage result = BenchmarkExecutor.ExecuteSolve(
                            loadedSolver,
                            id,
                            req.Puzzle,
                            progress => WriteMessage(ipcWriter, progress));
                        WriteMessage(ipcWriter, result);
                        break;
                    }

                    case "shutdown":
                    {
                        WriteMessage(ipcWriter, new ShutdownAckMessage());
                        return 0;
                    }

                    default:
                    {
                        WriteMessage(ipcWriter, new SolveResultMessage(
                            Id: requestId ?? string.Empty,
                            Status: "RE",
                            ErrorMessage: $"Unknown IPC request type '{requestType}'.",
                            StartRow: 0,
                            StartCol: 0,
                            Moves: [],
                            WallMs: 0.0,
                            CpuMs: 0.0,
                            AllocatedBytes: 0L,
                            GcPauseMs: 0.0,
                            Gen0: 0,
                            Gen1: 0,
                            Gen2: 0));
                        break;
                    }
                }
            }
            catch (OutOfMemoryException ex)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                if (string.Equals(requestType, "init", StringComparison.Ordinal))
                {
                    WriteMessage(ipcWriter, new InitResultMessage(false, null, null, 0, "OOM", ex.Message));
                }
                else
                {
                    WriteMessage(ipcWriter, new SolveResultMessage(
                        Id: requestId ?? string.Empty,
                        Status: "OOM",
                        ErrorMessage: ex.Message,
                        StartRow: 0,
                        StartCol: 0,
                        Moves: [],
                        WallMs: 0.0,
                        CpuMs: 0.0,
                        AllocatedBytes: 0L,
                        GcPauseMs: 0.0,
                        Gen0: 0,
                        Gen1: 0,
                        Gen2: 0));
                }
            }
            catch (Exception ex)
            {
                if (string.Equals(requestType, "init", StringComparison.Ordinal))
                {
                    WriteMessage(ipcWriter, new InitResultMessage(false, null, null, 0, "LOAD_ERROR", $"{ex.GetType().Name}: {ex.Message}"));
                }
                else
                {
                    WriteMessage(ipcWriter, new SolveResultMessage(
                        Id: requestId ?? string.Empty,
                        Status: "RE",
                        ErrorMessage: $"{ex.GetType().Name}: {ex.Message}",
                        StartRow: 0,
                        StartCol: 0,
                        Moves: [],
                        WallMs: 0.0,
                        CpuMs: 0.0,
                        AllocatedBytes: 0L,
                        GcPauseMs: 0.0,
                        Gen0: 0,
                        Gen1: 0,
                        Gen2: 0));
                }
            }
        }

        return 0;
    }

    private static void WriteMessage<T>(TextWriter writer, T message)
    {
        writer.WriteLine(IpcJson.Serialize(message));
    }
}
