using OrbSolver.Contracts;
using OrbSolver.Core;
using Xunit;

namespace OrbSolver.Core.Tests;

public sealed class PathValidatorTests
{
    private static BoardSnapshot CreateBoard(uint obstacleMask = 0)
    {
        var cells = new OrbType[30];
        Array.Fill(cells, OrbType.Water);
        return new BoardSnapshot(cells, obstacleMask: obstacleMask);
    }

    private static SolveOptions CreateOptions(int maxSteps = 10, bool allowDiagonal = false) =>
        new(
            MaxSteps: maxSteps,
            AllowDiagonal: allowDiagonal,
            Seed: 1,
            TimeBudgetMs: 1000,
            RequiredColorsMask: 0,
            RequiredColorsFirstWaveOnly: false,
            RequireAoeColorMask: 0,
            ColorWeights: ReadOnlyMemory<float>.Empty);

    [Fact]
    public void Validate_DefaultBoardSnapshot_ReturnsInvalidBoard()
    {
        var board = default(BoardSnapshot);
        var options = CreateOptions();
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.InvalidBoard, validation.Error);
    }

    [Fact]
    public void Validate_NegativeMaxSteps_ReturnsNegativeMaxSteps()
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: -1);
        var result = new SolveResult(new CellCoord(0, 0), ReadOnlyMemory<Direction>.Empty);

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.NegativeMaxSteps, validation.Error);
    }

    [Theory]
    [InlineData((byte)0, (byte)0)]
    [InlineData((byte)4, (byte)5)]
    [InlineData((byte)2, (byte)3)]
    public void Validate_ValidStartWithZeroMoves_ReturnsValid(byte row, byte col)
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 0);
        var result = new SolveResult(new CellCoord(row, col), ReadOnlyMemory<Direction>.Empty);

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.True(validation.IsValid);
        Assert.Equal(PathValidationError.None, validation.Error);
    }

    [Theory]
    [InlineData((byte)0, (byte)6)]   // Index == 6 < 30, must still fail explicit Row < 5 && Col < 6 check
    [InlineData((byte)5, (byte)0)]   // Index == 30
    [InlineData((byte)5, (byte)5)]
    [InlineData((byte)255, (byte)0)]
    public void Validate_StartOutOfBounds_ReturnsStartOutOfBounds(byte row, byte col)
    {
        var board = CreateBoard();
        var options = CreateOptions();
        var result = new SolveResult(new CellCoord(row, col), ReadOnlyMemory<Direction>.Empty);

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.StartOutOfBounds, validation.Error);
    }

    [Fact]
    public void Validate_StartOnObstacle_ReturnsStartOnObstacle()
    {
        var start = new CellCoord(2, 3);
        var board = CreateBoard(obstacleMask: 1u << start.Index);
        var options = CreateOptions();
        var result = new SolveResult(start, ReadOnlyMemory<Direction>.Empty);

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.StartOnObstacle, validation.Error);
    }

    [Fact]
    public void Validate_ExceedingMaxSteps_ReturnsTooManySteps()
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 2);
        var result = new SolveResult(
            new CellCoord(0, 0),
            new[] { Direction.Right, Direction.Right, Direction.Down });

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.TooManySteps, validation.Error);
    }

    [Fact]
    public void Validate_ExactMaxSteps_ReturnsValid()
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 3);
        var result = new SolveResult(
            new CellCoord(0, 0),
            new[] { Direction.Right, Direction.Down, Direction.Left });

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.True(validation.IsValid);
    }

    [Theory]
    [InlineData(Direction.UpLeft)]
    [InlineData(Direction.UpRight)]
    [InlineData(Direction.DownLeft)]
    [InlineData(Direction.DownRight)]
    public void Validate_DiagonalMoveWhenDisallowed_ReturnsDiagonalNotAllowed(Direction diagonalDir)
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 5, allowDiagonal: false);
        var result = new SolveResult(
            new CellCoord(2, 2),
            new[] { Direction.Right, diagonalDir });

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.DiagonalNotAllowed, validation.Error);
        Assert.Equal(1, validation.FailedStepIndex);
    }

    [Fact]
    public void Validate_AllEightDirectionsWhenAllowed_ReturnsValid()
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 8, allowDiagonal: true);
        var result = new SolveResult(
            new CellCoord(2, 2),
            new[]
            {
                Direction.Up,
                Direction.Down,
                Direction.Left,
                Direction.Right,
                Direction.UpLeft,
                Direction.DownRight,
                Direction.UpRight,
                Direction.DownLeft
            });

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Validate_Immediate180DegreeReversal_IsLegal()
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 4, allowDiagonal: true);
        var result = new SolveResult(
            new CellCoord(2, 2),
            new[] { Direction.Right, Direction.Left, Direction.UpLeft, Direction.DownRight });

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Validate_InvalidDirectionEnum_ReturnsInvalidDirection()
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 5, allowDiagonal: true);
        var result = new SolveResult(
            new CellCoord(2, 2),
            new[] { (Direction)99 });

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.InvalidDirection, validation.Error);
        Assert.Equal(0, validation.FailedStepIndex);
    }

    [Theory]
    [InlineData((byte)0, (byte)0, Direction.Up)]
    [InlineData((byte)0, (byte)0, Direction.Left)]
    [InlineData((byte)0, (byte)0, Direction.UpLeft)]
    [InlineData((byte)4, (byte)5, Direction.Down)]
    [InlineData((byte)4, (byte)5, Direction.Right)]
    [InlineData((byte)4, (byte)5, Direction.DownRight)]
    public void Validate_StepOutOfBounds_ReturnsStepOutOfBounds(byte startRow, byte startCol, Direction dir)
    {
        var board = CreateBoard();
        var options = CreateOptions(maxSteps: 2, allowDiagonal: true);
        var result = new SolveResult(new CellCoord(startRow, startCol), new[] { dir });

        var validation = PathValidator.Validate(in board, in options, in result);

        Assert.False(validation.IsValid);
        Assert.Equal(PathValidationError.StepOutOfBounds, validation.Error);
        Assert.Equal(0, validation.FailedStepIndex);
    }

    [Fact]
    public void Validate_StepOntoObstacleCardinalAndDiagonal_ReturnsStepOnObstacle()
    {
        var obstacle = new CellCoord(1, 2);
        var board = CreateBoard(obstacleMask: 1u << obstacle.Index);

        var cardinalOptions = CreateOptions(maxSteps: 5, allowDiagonal: false);
        var cardinalResult = new SolveResult(
            new CellCoord(1, 0),
            new[] { Direction.Right, Direction.Right });

        var cardinalValidation = PathValidator.Validate(in board, in cardinalOptions, in cardinalResult);
        Assert.False(cardinalValidation.IsValid);
        Assert.Equal(PathValidationError.StepOnObstacle, cardinalValidation.Error);
        Assert.Equal(1, cardinalValidation.FailedStepIndex);

        var diagOptions = CreateOptions(maxSteps: 5, allowDiagonal: true);
        var diagResult = new SolveResult(
            new CellCoord(0, 1),
            new[] { Direction.DownRight });

        var diagValidation = PathValidator.Validate(in board, in diagOptions, in diagResult);
        Assert.False(diagValidation.IsValid);
        Assert.Equal(PathValidationError.StepOnObstacle, diagValidation.Error);
        Assert.Equal(0, diagValidation.FailedStepIndex);
    }
}
