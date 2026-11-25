using OrbSolver.Contracts;
using OrbSolver.Core;
using Xunit;

namespace OrbSolver.Core.Tests;

public sealed class DatasetLoaderTests
{
    private const string ValidBoard30 = "WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW";

    [Fact]
    public void LoadFromJson_FullValidCase_ParsesAllFieldsAndConvertsToContracts()
    {
        string json = """
        [
          {
            "id": "T5-01",
            "tier": "T5",
            "description": "L-shape water puzzle + corner obstacles",
            "board": "WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW",
            "obstacleCoords": [[0, 0], [0, 5], [4, 0], [4, 5]],
            "puzzleCoords": [[1, 2], [2, 2], [3, 2], [3, 3]],
            "puzzleColor": "Water",
            "maxSteps": 24,
            "allowDiagonal": false,
            "seed": 20260101,
            "timeBudgetMs": 2000,
            "requiredColors": ["Water", "Fire", "Wood"],
            "requiredColorsFirstWaveOnly": true,
            "requireAoeColors": ["Dark"],
            "colorWeights": { "Water": 2.0, "Fire": 1.0, "Wood": 1.0, "Light": 1.0, "Dark": 1.5, "Heart": 0.5 },
            "referenceValue": 450.5
          }
        ]
        """;

        IReadOnlyList<DatasetCase> cases = DatasetLoader.LoadFromJson(json, "test.json");

        Assert.Single(cases);
        DatasetCase c = cases[0];
        Assert.Equal("T5-01", c.Id);
        Assert.Equal(PuzzleTier.T5, c.Tier);
        Assert.Equal("L-shape water puzzle + corner obstacles", c.Description);
        Assert.Equal(ValidBoard30, c.Board);
        Assert.Equal(30, c.Cells.Length);
        Assert.Equal(OrbType.Water, c.Cells.Span[0]);
        Assert.Equal(OrbType.Fire, c.Cells.Span[1]);
        Assert.Equal(OrbType.Water, c.Cells.Span[2]);
        Assert.Equal(OrbType.Light, c.Cells.Span[3]);
        Assert.Equal(OrbType.Dark, c.Cells.Span[4]);
        Assert.Equal(OrbType.Heart, c.Cells.Span[5]);
        Assert.Equal((1u << 0) | (1u << 5) | (1u << 24) | (1u << 29), c.ObstacleMask);
        Assert.Equal((1u << 8) | (1u << 14) | (1u << 20) | (1u << 21), c.PuzzleMask);
        Assert.Equal(OrbType.Water, c.PuzzleColor);
        Assert.Equal(24, c.MaxSteps);
        Assert.False(c.AllowDiagonal);
        Assert.Equal(20260101, c.Seed);
        Assert.Equal(2000, c.TimeBudgetMs);
        Assert.Equal((byte)((1 << 1) | (1 << 2) | (1 << 3)), c.RequiredColorsMask);
        Assert.True(c.RequiredColorsFirstWaveOnly);
        Assert.Equal((byte)(1 << (int)OrbType.Dark), c.RequireAoeColorMask);
        Assert.Equal(7, c.ColorWeights.Length);
        Assert.Equal(0f, c.ColorWeights.Span[0]);
        Assert.Equal(2.0f, c.ColorWeights.Span[(int)OrbType.Water]);
        Assert.Equal(1.5f, c.ColorWeights.Span[(int)OrbType.Dark]);
        Assert.Equal(0.5f, c.ColorWeights.Span[(int)OrbType.Heart]);
        Assert.Equal(450.5, c.ReferenceValue);

        BoardSnapshot snapshot = c.ToBoardSnapshot();
        Assert.Equal(c.ObstacleMask, snapshot.ObstacleMask);
        Assert.Equal(c.PuzzleMask, snapshot.PuzzleMask);
        Assert.Equal(c.PuzzleColor, snapshot.PuzzleColor);

        SolveOptions options = c.ToSolveOptions();
        Assert.Equal(24, options.MaxSteps);
        Assert.Equal(c.RequiredColorsMask, options.RequiredColorsMask);
        Assert.True(options.RequiredColorsFirstWaveOnly);
        Assert.Equal(c.RequireAoeColorMask, options.RequireAoeColorMask);
        Assert.Equal(7, options.ColorWeights.Length);
    }

    [Fact]
    public void LoadFromJson_MinimalValidCaseWithPartialWeightsAndVRef_AppliesDefaults()
    {
        string json = """
        [
          {
            "id": "W-01",
            "tier": "warmup",
            "board": "WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW",
            "maxSteps": 0,
            "allowDiagonal": true,
            "seed": 1,
            "timeBudgetMs": 500,
            "colorWeights": { "Water": 3.0 },
            "vRef": 120.0
          }
        ]
        """;

        IReadOnlyList<DatasetCase> cases = DatasetLoader.LoadFromJson(json);
        DatasetCase c = Assert.Single(cases);

        Assert.Equal(PuzzleTier.Warmup, c.Tier);
        Assert.Equal(string.Empty, c.Description);
        Assert.Equal(0u, c.ObstacleMask);
        Assert.Equal(0u, c.PuzzleMask);
        Assert.Equal(OrbType.Empty, c.PuzzleColor);
        Assert.Equal(0, c.RequiredColorsMask);
        Assert.False(c.RequiredColorsFirstWaveOnly);
        Assert.Equal(0, c.RequireAoeColorMask);
        Assert.Equal(0f, c.ColorWeights.Span[0]);
        Assert.Equal(3.0f, c.ColorWeights.Span[(int)OrbType.Water]);
        Assert.Equal(1.0f, c.ColorWeights.Span[(int)OrbType.Fire]);
        Assert.Equal(1.0f, c.ColorWeights.Span[(int)OrbType.Heart]);
        Assert.Equal(120.0, c.ReferenceValue);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000,}]""")]
    [InlineData("""/* comment */ [{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    public void LoadFromJson_InvalidRootEmptyTrailingCommaOrComments_Throws(string json)
    {
        Assert.Throws<DatasetValidationException>(() => DatasetLoader.LoadFromJson(json));
    }

    [Fact]
    public void LoadFromJson_DuplicateIds_Throws()
    {
        string json = """
        [
          {"id":"T1-01","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000},
          {"id":"T1-01","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":2,"timeBudgetMs":1000}
        ]
        """;

        var ex = Assert.Throws<DatasetValidationException>(() => DatasetLoader.LoadFromJson(json));
        Assert.Equal("id", ex.FieldName);
    }

    [Fact]
    public void LoadFromJson_DuplicatePropertyOrUnknownProperty_Throws()
    {
        string dupProp = """
        [
          {"id":"T1-01","id":"T1-02","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}
        ]
        """;
        Assert.Throws<DatasetValidationException>(() => DatasetLoader.LoadFromJson(dupProp));

        string unknownProp = """
        [
          {"id":"T1-01","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000,"extra":1}
        ]
        """;
        var ex = Assert.Throws<DatasetValidationException>(() => DatasetLoader.LoadFromJson(unknownProp));
        Assert.Equal("extra", ex.FieldName);
    }

    [Theory]
    [InlineData("""[{"tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1}]""")]
    public void LoadFromJson_MissingRequiredField_Throws(string json)
    {
        Assert.Throws<DatasetValidationException>(() => DatasetLoader.LoadFromJson(json));
    }

    [Theory]
    [InlineData("""[{"id":"1","tier":"T6","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"SHORT","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"EFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"wFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","obstacleCoords":[[0,6]],"maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","obstacleCoords":[[5,0]],"maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","obstacleCoords":[[1,1],[1,1]],"maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","obstacleCoords":[[1,1]],"puzzleCoords":[[1,1],[1,2],[1,3]],"maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","puzzleColor":"Water","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","puzzleCoords":[[0,0],[0,1],[0,2]],"puzzleColor":"Invalid","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","requiredColors":["Empty"],"maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","requireAoeColors":["Empty"],"maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":-1,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":0}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000,"colorWeights":{"Empty":1.0}}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000,"colorWeights":{"Water":-0.1}}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000,"colorWeights":{"Water":1e40}}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000,"referenceValue":-1.0}]""")]
    [InlineData("""[{"id":"1","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000,"referenceValue":100.0,"vRef":100.0}]""")]
    public void LoadFromJson_SchemaViolations_Throws(string json)
    {
        Assert.Throws<DatasetValidationException>(() => DatasetLoader.LoadFromJson(json));
    }

    [Fact]
    public void LoadBenchmarkDirectory_SeparatesWarmupAndTiers()
    {
        string tempDir = Path.Combine(AppContext.BaseDirectory, $"orb_dataset_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "warmup.json"), """
            [{"id":"W-01","tier":"Warmup","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":1,"timeBudgetMs":1000}]
            """);
            File.WriteAllText(Path.Combine(tempDir, "t2_step_limited.json"), """
            [{"id":"T2-01","tier":"T2","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":10,"allowDiagonal":false,"seed":2,"timeBudgetMs":500}]
            """);
            File.WriteAllText(Path.Combine(tempDir, "t1_basic_combo.json"), """
            [{"id":"T1-01","tier":"T1","board":"WFWLDHFWDLWHFWLDWFWLDHFWLDWHFW","maxSteps":24,"allowDiagonal":false,"seed":3,"timeBudgetMs":1000}]
            """);

            BenchmarkDataset dataset = DatasetLoader.LoadBenchmarkDirectory(tempDir);

            Assert.Single(dataset.WarmupCases);
            Assert.Equal("W-01", dataset.WarmupCases[0].Id);
            Assert.Equal(2, dataset.BenchmarkCases.Count);
            Assert.Equal("T1-01", dataset.BenchmarkCases[0].Id);
            Assert.Equal("T2-01", dataset.BenchmarkCases[1].Id);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
