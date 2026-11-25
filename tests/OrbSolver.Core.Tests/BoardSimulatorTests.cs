using OrbSolver.Contracts;
using OrbSolver.Core;
using Xunit;

namespace OrbSolver.Core.Tests;

public sealed class BoardSimulatorTests
{
    private static OrbType[] ParseBoardString(string board)
    {
        Assert.Equal(30, board.Length);
        var cells = new OrbType[30];
        for (int i = 0; i < 30; i++)
        {
            cells[i] = board[i] switch
            {
                'W' => OrbType.Water,
                'F' => OrbType.Fire,
                'G' => OrbType.Wood,
                'L' => OrbType.Light,
                'D' => OrbType.Dark,
                'H' => OrbType.Heart,
                _ => throw new ArgumentException($"Unexpected char {board[i]}")
            };
        }

        return cells;
    }

    private static string CreateAlternatingNoMatchPattern() =>
        // 5 rows x 6 cols, guaranteed zero H/V 3-in-a-row matches:
        "WFGLDH" +
        "DHWFGL" +
        "GLDHWF" +
        "WFGLDH" +
        "DHWFGL";

    private static SolveOptions DefaultOptions(
        int maxSteps = 10,
        bool allowDiagonal = false,
        byte requiredColorsMask = 0,
        bool requiredColorsFirstWaveOnly = false,
        byte requireAoeColorMask = 0) =>
        new(
            MaxSteps: maxSteps,
            AllowDiagonal: allowDiagonal,
            Seed: 42,
            TimeBudgetMs: 1000,
            RequiredColorsMask: requiredColorsMask,
            RequiredColorsFirstWaveOnly: requiredColorsFirstWaveOnly,
            RequireAoeColorMask: requireAoeColorMask,
            ColorWeights: ReadOnlyMemory<float>.Empty);

    [Fact]
    public void Simulate_BoardWithEmptyOrb_ThrowsArgumentException()
    {
        OrbType[] cells = ParseBoardString(CreateAlternatingNoMatchPattern());
        cells[12] = OrbType.Empty;
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions();
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        Assert.Throws<ArgumentException>(() => BoardSimulator.Simulate(in board, in options, in result));
    }

    [Fact]
    public void Simulate_DefaultBoardSnapshot_ThrowsArgumentException()
    {
        var board = default(BoardSnapshot);
        var options = DefaultOptions();
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        Assert.Throws<ArgumentException>(() => BoardSimulator.Simulate(in board, in options, in result));
    }

    [Fact]
    public void Simulate_InvalidPuzzleColor_ThrowsArgumentException()
    {
        OrbType[] cells = ParseBoardString(CreateAlternatingNoMatchPattern());
        var board = new BoardSnapshot(cells, puzzleMask: 0b111u, puzzleColor: (OrbType)99);
        var options = DefaultOptions();
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        Assert.Throws<ArgumentException>(() => BoardSimulator.Simulate(in board, in options, in result));
    }

    [Fact]
    public void Simulate_InvalidPath_ReturnsInvalidSimulationResult()
    {
        OrbType[] cells = ParseBoardString(CreateAlternatingNoMatchPattern());
        var board = new BoardSnapshot(cells, obstacleMask: 1u << 0);
        var options = DefaultOptions(requiredColorsMask: (byte)(1 << (int)OrbType.Water));
        var result = new SolveResult(new CellCoord(0, 0), new[] { Direction.Right });

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.False(sim.IsValidPath);
        Assert.Equal(PathValidationError.StartOnObstacle, sim.PathValidation.Error);
        Assert.Equal(1, sim.Steps);
        Assert.True(sim.HasShields);
        Assert.False(sim.AllShieldsSatisfied);
        Assert.Equal(0, sim.TotalCombos);
        Assert.True(sim.FinalBoard.IsEmpty);
    }

    [Fact]
    public void Simulate_ZeroStepNoMatchBoard_ReturnsZeroCombosAndUnchangedBoard()
    {
        OrbType[] cells = ParseBoardString(CreateAlternatingNoMatchPattern());
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(0, sim.Steps);
        Assert.Equal(0, sim.WaveCount);
        Assert.Equal(0, sim.TotalCombos);
        Assert.Equal(0, sim.TotalEliminated);
        Assert.False(sim.HasShields);
        Assert.True(sim.AllShieldsSatisfied);
        Assert.Equal(cells, sim.FinalBoard.ToArray());
        Assert.False(BoardSimulator.HasAnyMatch(board.Cells));
    }

    [Fact]
    public void Simulate_PathReplaySwapsOrbsAndFormsMatch()
    {
        // Row 0: W W F W G L -> swap (0,2) with (0,3) gives W W W F G L
        OrbType[] cells = ParseBoardString(
            "WWFWGL" +
            "DHWFGL" +
            "GLDHWF" +
            "WFGLDH" +
            "DHWFGL");
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions(maxSteps: 1);
        var result = new SolveResult(new CellCoord(0, 2), new[] { Direction.Right });

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(1, sim.Steps);
        Assert.Equal(1, sim.WaveCount);
        Assert.Equal(1, sim.TotalCombos);
        Assert.Equal(3, sim.TotalEliminated);
        Assert.Equal(3, sim.GetEliminatedCount(OrbType.Water));
        // Top-left 3 cells cleared and nothing above row 0, so they remain Empty.
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[0]);
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[1]);
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[2]);
        Assert.Equal(OrbType.Fire, sim.FinalBoard.Span[3]);
    }

    [Fact]
    public void Simulate_CrossAndLIntersection_MergeIntoSingleCombo()
    {
        // Row 1 cols 1..3 = W, Col 2 rows 0..2 = W (5-orb Water cross at (1,2))
        OrbType[] cells = ParseBoardString(
            "LDWGHL" +
            "FWWWHL" +
            "FDWFDH" +
            "GLDHWF" +
            "WFGLDH");
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(1, sim.WaveCount);
        Assert.Equal(1, sim.TotalCombos);
        Assert.Equal(5, sim.TotalEliminated);
        Assert.Equal(5, sim.GetEliminatedCount(OrbType.Water));
        Assert.Equal((byte)(1 << (int)OrbType.Water), sim.AoeColorsMask);
    }

    [Fact]
    public void Simulate_DiagonalSwap_MovesOrbAndClears()
    {
        // Row 0: W W G L D H, Row 1: D H W F G L -> diagonal DownRight from (1,2)=W? No, swap (0,3)=L with (1,2)=W -> no, swap (1,1)=H with (0,2)=G?
        // Put W at (0,0), (0,1), and (1,3); swap (1,3)=W UpLeft to (0,2) -> wait: UpLeft from (1,3) lands at (0,2)!
        OrbType[] cells = ParseBoardString(
            "WWGLDH" +
            "DHFWGL" +
            "GLDHWF" +
            "WFGLDH" +
            "DHWFGL");
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions(maxSteps: 1, allowDiagonal: true);
        var result = new SolveResult(new CellCoord(1, 3), new[] { Direction.UpLeft });

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(1, sim.WaveCount);
        Assert.Equal(1, sim.TotalCombos);
        Assert.Equal(3, sim.GetEliminatedCount(OrbType.Water));
    }

    [Fact]
    public void Simulate_SixInARow_CountsAsOneComboAndTriggersAoe()
    {
        OrbType[] cells = ParseBoardString(
            "WWWWWW" +
            "DHWFGL" +
            "GLDHWF" +
            "WFGLDH" +
            "DHWFGL");
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions(maxSteps: 0, requireAoeColorMask: (byte)(1 << (int)OrbType.Water));
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(1, sim.TotalCombos);
        Assert.Equal(6, sim.TotalEliminated);
        Assert.True(sim.AoeColorsSatisfied);
    }

    [Fact]
    public void Simulate_StrictPuzzleShield_DisconnectedOrPartialMatch_Fails()
    {
        // PuzzleMask has 6 cells across 2 disconnected groups: (1,0..2) and (3,3..5)
        uint puzzleMask = (1u << 6) | (1u << 7) | (1u << 8) | (1u << 21) | (1u << 22) | (1u << 23);
        OrbType[] cells = ParseBoardString(
            "FGLDHD" +
            "WWWDFG" +
            "LGDFHL" +
            "HDFWWW" +
            "FGLDHG");
        var board = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Water);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(2, sim.TotalCombos);
        Assert.False(sim.PuzzleShieldSatisfied);
    }

    [Fact]
    public void HasAnyMatch_InvalidLength_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => BoardSimulator.HasAnyMatch(new OrbType[10]));
    }

    [Fact]
    public void Simulate_DiagonallyTouchingSameColorMatches_RemainTwoCombos()
    {
        // Row 0 cols 0..2 = W, Row 1 cols 3..5 = W (touching only diagonally at (0,2)-(1,3))
        OrbType[] cells = ParseBoardString(
            "WWWFGL" +
            "FGLWWW" +
            "DHWFGL" +
            "GLDHWF" +
            "WFGLDH");
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(1, sim.WaveCount);
        Assert.Equal(2, sim.TotalCombos);
        Assert.Equal(6, sim.TotalEliminated);
        Assert.Equal(0, sim.AoeColorsMask);
    }

    [Fact]
    public void Simulate_GravityAndCascade_TriggersWave2WithoutSkyfall()
    {
        // Wave 1 clears Row 3 cols 0..2 (Fire: FFF).
        // Above Row 3:
        //   Row 0: W D H L G D
        //   Row 1: L G D F H L
        //   Row 2: W D H G L D
        //   Row 3: F F F W D H
        //   Row 4: W W G L D H
        // After Wave 1 clears (3,0), (3,1), (3,2):
        //   Col 0 drops: row0=Empty, row1=W, row2=L, row3=W, row4=W -> W at (3,0)&(4,0), not 3
        // Wait: let's construct an explicit clean cascade:
        //   Row 1: G G F ... -> wait: let's place Water at (2,0), (2,2) and Water at (1,1) above Fire at (2,1)?
        //   Even simpler:
        //   Row 2: W L W G D H
        //   Row 3: F F F D G L  (Fire clears at row 3 cols 0,1,2)
        //   Row 4: G W G H L D  (Wait: col 1 has W at row 4, L at row 2 -> not match)
        //   Make col 0, 1, 2 align at row 3 after Wave 1 clears row 3? No, row 2 drops to row 3!
        //   So put W at (2,0), (3,1)? Wait: (3,1) is cleared!
        //   Put F at (3,0), (3,1), (3,2) -> cleared in Wave 1.
        //   Put W at (2,0), (1,1)? No, col 1 only has 1 cell cleared below (2,1)! So (2,1) drops 1 row to (3,1).
        //   Therefore put W at (2,0), (2,1), (2,2)? They'd match in Wave 1!
        //   So put F at (3,0), (3,1), (3,2); put W at (2,0), (2,2) and W at (4,1)! Wait: after row 3 clears:
        //     Col 0: (2,0) drops to (3,0) -> W
        //     Col 1: (4,1) stays at (4,1) -> W? No, needs row 3 col 1!
        //     To make (3,0), (3,1), (3,2) all W in Wave 2, put W at (2,0), (2,2) and W at (1,1) with F at (2,1) AND (3,1)?
        //     Wait: if F is at (2,1), (3,1), (4,1) AND F is at (3,0), (3,2), then F forms a cross in Wave 1!
        //     Then Col 1 clears 3 cells ((2,1),(3,1),(4,1)) so (1,1) drops 3 rows to (4,1), while (2,0) drops 1 row to (3,0)!
        //   Instead: put W at (2,0), (2,2), and at (3,1) wait - if Wave 1 clears (3,0),(3,1),(3,2) = FFF,
        //   and we want W at row 3 cols 0,1,2? Then Col 1 must have TWO orbs cleared below W, or W is at (2,1) already? No, if W is at (2,1), W is already at (2,1).
        //   Aha! Put W at (1,0), (1,2) [wait no, match at row 1? No, (1,0) and (1,2) don't match!],
        //   OR match HORIZONTALLY in Wave 2 at Row 4:
        //   Wave 1: Row 3 cols 0..2 = F F F; Row 4 cols 0, 2 = W, col 1 = L (wait: L at (4,1) stays at (4,1)).
        //   Actually: put W at (3,1)??? No, (3,1) is F. Put W at (2,1), and put F at (3,1), (4,1)? No, F at (3,0),(3,1),(3,2) clears row 3!
        //   Wait: if Wave 1 clears (3,0),(3,1),(3,2)=F, then (2,1) drops to (3,1). If (3,0) wasn't cleared? No, we need (3,0) NOT cleared!
        //   Easy: Wave 1 clears Col 1 rows 2,3,4 (F at (2,1),(3,1),(4,1))! No, let's clear Row 2 cols 0,1,2 = F F F? Still drops 1 row in cols 0,1,2.
        //   Just match at Row 4 by clearing Row 4 col 1 ONLY? No, match needs 3!
        //   Think! Wave 1 clears Row 2 cols 1,2,3 = F F F.
        //   Wait: (3,0) = W, (3,4) = W? No, 5 cols wide.
        //   What about a VERTICAL match in Col 1? No, Wave 2 horizontal match at Row 3:
        //     Row 1: D L G H D L
        //     Row 2: H D W G L D   <- (2,2) = W
        //     Row 3: W W F D H L   <- (3,0)=W, (3,1)=W, (3,2)=F ... Wait: if F is at (3,2),(3,3),(3,4), then (2,2)=W drops to (3,2) and forms W W W at (3,0),(3,1),(3,2)!
        OrbType[] cells = ParseBoardString(
            "DLGHDL" +
            "HDGDHF" +
            "HLWGDL" +
            "WWFFFD" +
            "GDHLWD");
        // Verify Wave 1: only (3,2),(3,3),(3,4) = FFF!
        // After Wave 1 clears (3,2),(3,3),(3,4):
        //   (2,2)=W drops to (3,2)=W -> Row 3 now has W W W G? Wait: (3,0)=W, (3,1)=W, (3,2)=W -> Wave 2 matches W W W!
        //   And top cells (0,2), (0,3), (0,4) become Empty (no skyfall!).
        Assert.True(BoardSimulator.HasAnyMatch(cells));
        var board = new BoardSnapshot(cells);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(2, sim.WaveCount);
        Assert.Equal(2, sim.TotalCombos);
        Assert.Equal(6, sim.TotalEliminated);
        Assert.Equal(3, sim.GetEliminatedCount(OrbType.Fire));
        Assert.Equal(3, sim.GetEliminatedCount(OrbType.Water));
        Assert.Equal(1, sim.Waves.Span[0].WaveNumber);
        Assert.Equal((byte)(1 << (int)OrbType.Fire), sim.Waves.Span[0].EliminatedColorsMask);
        Assert.Equal(2, sim.Waves.Span[1].WaveNumber);
        Assert.Equal((byte)(1 << (int)OrbType.Water), sim.Waves.Span[1].EliminatedColorsMask);
        // No skyfall: top cells of cols 0..4 that dropped must be Empty
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[0]); // col 0 dropped in Wave 2
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[1]); // col 1 dropped in Wave 2
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[2]); // col 2 dropped in Wave 1 & 2
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[3]); // col 3 dropped in Wave 1
        Assert.Equal(OrbType.Empty, sim.FinalBoard.Span[4]); // col 4 dropped in Wave 1
        Assert.NotEqual(OrbType.Empty, sim.FinalBoard.Span[5]); // col 5 untouched
    }

    [Fact]
    public void Simulate_RequiredColors_FirstWaveOnlyFlagDistinguishesWave1VsCascade()
    {
        // Same cascade board: Wave 1 clears Fire, Wave 2 clears Water
        OrbType[] cells = ParseBoardString(
            "DLGHDL" +
            "HDGDHF" +
            "HLWGDL" +
            "WWFFFD" +
            "GDHLWD");
        var board = new BoardSnapshot(cells);
        byte reqMask = (byte)((1 << (int)OrbType.Fire) | (1 << (int)OrbType.Water));
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        var allWavesOpts = DefaultOptions(maxSteps: 0, requiredColorsMask: reqMask, requiredColorsFirstWaveOnly: false);
        SimulationResult allWavesSim = BoardSimulator.Simulate(in board, in allWavesOpts, in result);
        Assert.True(allWavesSim.RequiredColorsSatisfied);
        Assert.True(allWavesSim.AllShieldsSatisfied);

        var wave1Opts = DefaultOptions(maxSteps: 0, requiredColorsMask: reqMask, requiredColorsFirstWaveOnly: true);
        SimulationResult wave1Sim = BoardSimulator.Simulate(in board, in wave1Opts, in result);
        Assert.False(wave1Sim.RequiredColorsSatisfied);
        Assert.False(wave1Sim.AllShieldsSatisfied);
    }

    [Fact]
    public void Simulate_RequireAoeColor_RequiresSingleComboOfAtLeast5()
    {
        // Two separate 3-orb Water combos (total 6 Water orbs, max group size 3 -> must FAIL AOE)
        OrbType[] twoThrees = ParseBoardString(
            "WWWFGL" +
            "FGLWWW" +
            "DHWFGL" +
            "GLDHWF" +
            "WFGLDH");
        var boardTwoThrees = new BoardSnapshot(twoThrees);
        var aoeOpts = DefaultOptions(maxSteps: 0, requireAoeColorMask: (byte)(1 << (int)OrbType.Water));
        var zeroResult = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult simFail = BoardSimulator.Simulate(in boardTwoThrees, in aoeOpts, in zeroResult);
        Assert.Equal(6, simFail.GetEliminatedCount(OrbType.Water));
        Assert.False(simFail.AoeColorsSatisfied);

        // Single 5-orb Water combo -> must PASS AOE
        OrbType[] fiveInRow = ParseBoardString(
            "WWWWWF" +
            "DHWFGL" +
            "GLDHWF" +
            "WFGLDH" +
            "DHWFGL");
        var boardFive = new BoardSnapshot(fiveInRow);

        SimulationResult simPass = BoardSimulator.Simulate(in boardFive, in aoeOpts, in zeroResult);
        Assert.Equal(5, simPass.GetEliminatedCount(OrbType.Water));
        Assert.True(simPass.AoeColorsSatisfied);
    }

    [Fact]
    public void Simulate_StrictPuzzleShield_ExactWave1Match_Passes()
    {
        // L-shape Water puzzle at (1,2), (2,2), (3,2), (3,3)? Note (3,2),(3,3) is length 2, not >= 3.
        // Use valid self-eliminable 5-cell L-shape: (1,2),(2,2),(3,2),(3,3),(3,4)
        uint puzzleMask = (1u << (1 * 6 + 2))
            | (1u << (2 * 6 + 2))
            | (1u << (3 * 6 + 2))
            | (1u << (3 * 6 + 3))
            | (1u << (3 * 6 + 4));

        OrbType[] cells = ParseBoardString(
            "FGLDHD" +
            "FDWHLG" +
            "LGWFDH" +
            "HDWWWF" +
            "FGLDHG");
        // Verify: Water at (1,2),(2,2),(3,2),(3,3),(3,4). All other cells non-matching.
        var boardSpecific = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Water);
        var boardAnyColor = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Empty);
        var boardWrongColor = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Fire);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult simPass = BoardSimulator.Simulate(in boardSpecific, in options, in result);
        Assert.True(simPass.PuzzleShieldSatisfied);
        Assert.True(simPass.AllShieldsSatisfied);

        SimulationResult simAny = BoardSimulator.Simulate(in boardAnyColor, in options, in result);
        Assert.True(simAny.PuzzleShieldSatisfied);

        SimulationResult simWrongColor = BoardSimulator.Simulate(in boardWrongColor, in options, in result);
        Assert.False(simWrongColor.PuzzleShieldSatisfied);
        Assert.False(simWrongColor.AllShieldsSatisfied);
    }

    [Fact]
    public void Simulate_StrictPuzzleShield_ExternalSameColorMatchedAdhesion_Fails()
    {
        // PuzzleMask = (1,1),(1,2),(1,3) (row 1 cols 1..3 = Water).
        // External matched Water at (1,4) -> 4-in-a-row W W W W at (1,1..4) -> groupMask has 4 bits != 3-bit PuzzleMask -> MUST FAIL!
        uint puzzleMask = (1u << (1 * 6 + 1)) | (1u << (1 * 6 + 2)) | (1u << (1 * 6 + 3));
        OrbType[] cells = ParseBoardString(
            "FGLDHD" +
            "FWWWWG" +
            "LGDFHL" +
            "HDGLDF" +
            "FGLDHG");
        var board = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Water);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.IsValidPath);
        Assert.Equal(4, sim.GetEliminatedCount(OrbType.Water));
        Assert.False(sim.PuzzleShieldSatisfied);
    }

    [Fact]
    public void Simulate_StrictPuzzleShield_ExternalUnmatchedSameColorAdjacent_Passes()
    {
        // PuzzleMask = (1,1),(1,2),(1,3) = Water.
        // Place an UNMATCHED stray Water at (2,1) (adjacent to (1,1), not part of any >=3 run).
        uint puzzleMask = (1u << (1 * 6 + 1)) | (1u << (1 * 6 + 2)) | (1u << (1 * 6 + 3));
        OrbType[] cells = ParseBoardString(
            "FGLDHD" +
            "FWWWLG" +
            "LWDFHL" +
            "HDGLDF" +
            "FGLDHG");
        var board = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Water);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.PuzzleShieldSatisfied);
        Assert.Equal(3, sim.GetEliminatedCount(OrbType.Water));
    }

    [Fact]
    public void Simulate_StrictPuzzleShield_DisjointSameColorComboElsewhere_Passes()
    {
        // PuzzleMask = (1,1),(1,2),(1,3) = Water; separate Water 3-in-a-row at (4,3),(4,4),(4,5).
        uint puzzleMask = (1u << (1 * 6 + 1)) | (1u << (1 * 6 + 2)) | (1u << (1 * 6 + 3));
        OrbType[] cells = ParseBoardString(
            "FGLDHD" +
            "FWWWLG" +
            "LGDFHL" +
            "HDGLDF" +
            "FGLWWW");
        var board = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Water);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.True(sim.PuzzleShieldSatisfied);
        Assert.Equal(2, sim.TotalCombos);
        Assert.Equal(6, sim.GetEliminatedCount(OrbType.Water));
    }

    [Fact]
    public void Simulate_StrictPuzzleShield_CompletedInWave2Only_Fails()
    {
        // Wave 2 forms W W W at (3,0),(3,1),(3,2) after Wave 1 clears F F F at (3,2),(3,3),(3,4).
        uint puzzleMask = (1u << (3 * 6 + 0)) | (1u << (3 * 6 + 1)) | (1u << (3 * 6 + 2));
        OrbType[] cells = ParseBoardString(
            "DLGHDL" +
            "HDGDHF" +
            "HLWGDL" +
            "WWFFFD" +
            "GDHLWD");
        var board = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Water);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.Equal(2, sim.WaveCount);
        Assert.False(sim.PuzzleShieldSatisfied);
    }

    [Fact]
    public void Simulate_StrictPuzzleShield_NoWave1Match_Fails()
    {
        uint puzzleMask = (1u << (1 * 6 + 1)) | (1u << (1 * 6 + 2)) | (1u << (1 * 6 + 3));
        OrbType[] cells = ParseBoardString(CreateAlternatingNoMatchPattern());
        var board = new BoardSnapshot(cells, puzzleMask: puzzleMask, puzzleColor: OrbType.Empty);
        var options = DefaultOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        SimulationResult sim = BoardSimulator.Simulate(in board, in options, in result);

        Assert.Equal(0, sim.WaveCount);
        Assert.False(sim.PuzzleShieldSatisfied);
    }
}
