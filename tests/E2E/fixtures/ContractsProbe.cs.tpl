using System;
using OrbSolver.Contracts;

if (args.Length < 2 || args[0] != "--mode")
{
    Console.Error.WriteLine("Usage: ContractsProbe --mode <mode>");
    return 2;
}

string mode = args[1];

switch (mode)
{
    case "valid-snapshot":
    {
        var cells = new OrbType[30];
        for (int i = 0; i < 30; i++)
        {
            cells[i] = (OrbType)((i % 6) + 1);
        }

        uint obstacleMask = (1u << 0) | (1u << 29);
        uint puzzleMask = (1u << 5) | (1u << 11) | (1u << 17);
        var snapshot = new BoardSnapshot(cells, obstacleMask, puzzleMask, OrbType.Water);

        if (snapshot.Cells.Length != 30) throw new Exception("Expected 30 cells.");
        if (snapshot.Cells[0] != OrbType.Water || snapshot.Cells[29] != OrbType.Heart) throw new Exception("Cell endpoints mismatch.");
        if (snapshot.ObstacleMask != obstacleMask) throw new Exception("ObstacleMask mismatch.");
        if (snapshot.PuzzleMask != puzzleMask) throw new Exception("PuzzleMask mismatch.");
        if (snapshot.PuzzleColor != OrbType.Water) throw new Exception("PuzzleColor mismatch.");

        Console.WriteLine("PROBE_OK:valid-snapshot");
        return 0;
    }

    case "cellcoord-grid":
    {
        var seen = new bool[30];
        for (byte r = 0; r < 5; r++)
        {
            for (byte c = 0; c < 6; c++)
            {
                var coord = new CellCoord(r, c);
                int expected = r * 6 + c;
                if (coord.Index != expected)
                {
                    throw new Exception($"CellCoord({r},{c}).Index={coord.Index}, expected {expected}");
                }
                if (seen[coord.Index]) throw new Exception($"Duplicate index {coord.Index}");
                seen[coord.Index] = true;
            }
        }

        var weights = new float[] { 0f, 1f, 1.5f, 1f, 1f, 1f, 0.5f };
        var opts = new SolveOptions(20, true, 42, 1000, 0x0E, true, 0x02, weights);
        var res = new SolveResult(new CellCoord(2, 3), new[] { Direction.Up, Direction.DownRight });
        if (opts.MaxSteps != 20 || !opts.AllowDiagonal || opts.Seed != 42 || opts.TimeBudgetMs != 1000)
            throw new Exception("SolveOptions mismatch.");
        if (res.Start.Index != 15 || res.Moves.Length != 2)
            throw new Exception("SolveResult mismatch.");
        if ((byte)ExecutionMode.SingleThread != 0 || (byte)ExecutionMode.MultiThread != 1)
            throw new Exception("ExecutionMode values mismatch.");

        Console.WriteLine("PROBE_OK:cellcoord-grid");
        return 0;
    }

    case "short-cells":
    {
        return AssertBadCellsLength(new OrbType[29], "short-cells");
    }

    case "long-cells":
    {
        return AssertBadCellsLength(new OrbType[31], "long-cells");
    }

    case "empty-cells":
    {
        try
        {
            _ = new BoardSnapshot(ReadOnlyMemory<OrbType>.Empty);
            Console.Error.WriteLine("Expected ArgumentException for empty-cells.");
            return 1;
        }
        catch (ArgumentException ex) when (ex.ParamName == "cells")
        {
            Console.WriteLine("PROBE_OK:empty-cells");
            return 0;
        }
    }

    case "readonlyspan-contract":
    {
        if (!typeof(BoardSnapshot).IsValueType)
            throw new Exception("BoardSnapshot must be a value type.");

        var prop = typeof(BoardSnapshot).GetProperty(nameof(BoardSnapshot.Cells))
            ?? throw new Exception("Missing Cells property.");
        if (prop.PropertyType != typeof(ReadOnlySpan<OrbType>))
            throw new Exception($"Expected ReadOnlySpan<OrbType>, got {prop.PropertyType}");
        if (prop.CanWrite)
            throw new Exception("Cells property must be read-only.");

        var cells = new OrbType[30];
        Array.Fill(cells, OrbType.Fire);
        var snapshot = new BoardSnapshot(cells);
        ReadOnlySpan<OrbType> span = snapshot.Cells;
        if (span.Length != 30 || span[0] != OrbType.Fire)
            throw new Exception("ReadOnlySpan read check failed.");

        Console.WriteLine("PROBE_OK:readonlyspan-contract");
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown mode: {mode}");
        return 2;
}

static int AssertBadCellsLength(OrbType[] cells, string modeName)
{
    try
    {
        _ = new BoardSnapshot(cells);
        Console.Error.WriteLine($"Expected ArgumentException for {modeName}.");
        return 1;
    }
    catch (ArgumentException ex) when (ex.ParamName == "cells")
    {
        Console.WriteLine($"PROBE_OK:{modeName}");
        return 0;
    }
}
