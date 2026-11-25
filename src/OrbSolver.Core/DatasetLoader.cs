using System.Text.Json;
using OrbSolver.Contracts;

namespace OrbSolver.Core;

/// <summary>
/// 識別關卡資料集案例的基準測試難度階級或預熱群組。
/// </summary>
public enum PuzzleTier
{
    /// <summary>
    /// 用於 JIT／Dynamic PGO 預熱的預熱資料集（不計入基準測試總分）。
    /// </summary>
    Warmup = 0,

    /// <summary>
    /// Tier 1：基礎 Combo 最佳化（權重 15%）。
    /// </summary>
    T1 = 1,

    /// <summary>
    /// Tier 2：短步數限制下的路徑效率（權重 15%）。
    /// </summary>
    T2 = 2,

    /// <summary>
    /// Tier 3：指定屬性消珠與全體攻擊（AOE）限制條件（權重 20%）。
    /// </summary>
    T3 = 3,

    /// <summary>
    /// Tier 4：風化／燃燒障礙物與斜向移動（權重 25%）。
    /// </summary>
    T4 = 4,

    /// <summary>
    /// Tier 5：嚴格拼圖盾與複合限制條件（權重 25%）。
    /// </summary>
    T5 = 5
}

/// <summary>
/// 當資料集檔案或 JSON 酬載違反嚴格關卡結構定義（schema）時擲出的例外狀況。
/// </summary>
public sealed class DatasetValidationException : Exception
{
    /// <summary>
    /// 初始化 <see cref="DatasetValidationException"/> 的新執行個體。
    /// </summary>
    /// <param name="message">驗證錯誤訊息。</param>
    /// <param name="filePath">選填的來源檔案路徑或識別名稱。</param>
    /// <param name="caseId">選填的關卡案例識別碼。</param>
    /// <param name="fieldName">選填的驗證失敗 JSON 欄位名稱。</param>
    /// <param name="innerException">選填的基礎例外狀況。</param>
    public DatasetValidationException(
        string message,
        string? filePath = null,
        string? caseId = null,
        string? fieldName = null,
        Exception? innerException = null)
        : base(FormatMessage(message, filePath, caseId, fieldName), innerException)
    {
        FilePath = filePath;
        CaseId = caseId;
        FieldName = fieldName;
    }

    /// <summary>
    /// 取得資料集檔案路徑或來源名稱（若有提供）。
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    /// 取得失敗的案例識別碼（若已知）。
    /// </summary>
    public string? CaseId { get; }

    /// <summary>
    /// 取得失敗的 JSON 欄位名稱（若適用）。
    /// </summary>
    public string? FieldName { get; }

    private static string FormatMessage(string message, string? filePath, string? caseId, string? fieldName)
    {
        List<string> context = [];
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            context.Add($"file='{filePath}'");
        }

        if (!string.IsNullOrWhiteSpace(caseId))
        {
            context.Add($"case='{caseId}'");
        }

        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            context.Add($"field='{fieldName}'");
        }

        return context.Count == 0
            ? message
            : $"{message} ({string.Join(", ", context)})";
    }
}

/// <summary>
/// 從 JSON 資料集定義載入且經過驗證的不可變關卡案例。
/// </summary>
public sealed class DatasetCase
{
    private readonly OrbType[] _cells;
    private readonly float[] _colorWeights;

    internal DatasetCase(
        string id,
        PuzzleTier tier,
        string description,
        string board,
        OrbType[] cells,
        uint obstacleMask,
        uint puzzleMask,
        OrbType puzzleColor,
        int maxSteps,
        bool allowDiagonal,
        int seed,
        int timeBudgetMs,
        byte requiredColorsMask,
        bool requiredColorsFirstWaveOnly,
        byte requireAoeColorMask,
        float[] colorWeights,
        double? referenceValue)
    {
        Id = id;
        Tier = tier;
        Description = description;
        Board = board;
        _cells = cells;
        ObstacleMask = obstacleMask;
        PuzzleMask = puzzleMask;
        PuzzleColor = puzzleColor;
        MaxSteps = maxSteps;
        AllowDiagonal = allowDiagonal;
        Seed = seed;
        TimeBudgetMs = timeBudgetMs;
        RequiredColorsMask = requiredColorsMask;
        RequiredColorsFirstWaveOnly = requiredColorsFirstWaveOnly;
        RequireAoeColorMask = requireAoeColorMask;
        _colorWeights = colorWeights;
        ReferenceValue = referenceValue;
    }

    /// <summary>
    /// 取得唯一的關卡識別碼。
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 取得關卡難度階級分類。
    /// </summary>
    public PuzzleTier Tier { get; }

    /// <summary>
    /// 取得選填的人類可讀關卡說明。
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// 取得原始 30 字元盤面字串（<c>W/F/G/L/D/H</c>）。
    /// </summary>
    public string Board { get; }

    /// <summary>
    /// 取得列優先排列的 30 格珠子配置。
    /// </summary>
    public ReadOnlyMemory<OrbType> Cells => _cells;

    /// <summary>
    /// 取得 30 位元空間障礙物遮罩。
    /// </summary>
    public uint ObstacleMask { get; }

    /// <summary>
    /// 取得 30 位元空間拼圖盾遮罩。
    /// </summary>
    public uint PuzzleMask { get; }

    /// <summary>
    /// 取得拼圖盾所需的珠子顏色；若允許任一均勻單一顏色，則為 <see cref="OrbType.Empty"/>。
    /// </summary>
    public OrbType PuzzleColor { get; }

    /// <summary>
    /// 取得允許的最大移動步數。
    /// </summary>
    public int MaxSteps { get; }

    /// <summary>
    /// 取得是否允許斜向移動。
    /// </summary>
    public bool AllowDiagonal { get; }

    /// <summary>
    /// 取得決定性隨機數產生器（RNG）種子。
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// 取得以毫秒為單位的軟性執行時間預算。
    /// </summary>
    public int TimeBudgetMs { get; }

    /// <summary>
    /// 取得必須消除的顏色位元遮罩（<c>1 &lt;&lt; (int)OrbType</c>）。
    /// </summary>
    public byte RequiredColorsMask { get; }

    /// <summary>
    /// 取得 <see cref="RequiredColorsMask"/> 是否必須僅於第 1 波滿足。
    /// </summary>
    public bool RequiredColorsFirstWaveOnly { get; }

    /// <summary>
    /// 取得要求至少一組大小 &gt;= 5 Combo 的顏色位元遮罩（<c>1 &lt;&lt; (int)OrbType</c>）。
    /// </summary>
    public byte RequireAoeColorMask { get; }

    /// <summary>
    /// 取得長度為 7 的顏色權重陣列，以 <c>(int)OrbType</c> 為索引（<c>[0] = 0f</c>，<c>[1..6] &gt;= 0f</c>）。
    /// </summary>
    public ReadOnlyMemory<float> ColorWeights => _colorWeights;

    /// <summary>
    /// 取得選填的靜態基準參考原始價值（<c>V_ref</c>，若 JSON 中有指定）。
    /// </summary>
    public double? ReferenceValue { get; }

    /// <summary>
    /// 將此關卡定義轉換為不可變的 <see cref="BoardSnapshot"/>。
    /// </summary>
    /// <returns><see cref="BoardSnapshot"/> 執行個體。</returns>
    public BoardSnapshot ToBoardSnapshot() =>
        new(Cells, ObstacleMask, PuzzleMask, PuzzleColor);

    /// <summary>
    /// 將此關卡定義轉換為不可變的 <see cref="SolveOptions"/>。
    /// </summary>
    /// <returns><see cref="SolveOptions"/> 執行個體。</returns>
    public SolveOptions ToSolveOptions() =>
        new(
            MaxSteps,
            AllowDiagonal,
            Seed,
            TimeBudgetMs,
            RequiredColorsMask,
            RequiredColorsFirstWaveOnly,
            RequireAoeColorMask,
            ColorWeights);
}

/// <summary>
/// 表示已載入的基準測試資料集目錄，區分為預熱案例與計分階級案例（T1–T5）。
/// </summary>
public sealed class BenchmarkDataset
{
    internal BenchmarkDataset(
        IReadOnlyList<DatasetCase> warmupCases,
        IReadOnlyList<DatasetCase> benchmarkCases,
        IReadOnlyDictionary<PuzzleTier, IReadOnlyList<DatasetCase>> casesByTier)
    {
        WarmupCases = warmupCases;
        BenchmarkCases = benchmarkCases;
        CasesByTier = casesByTier;
    }

    /// <summary>
    /// 取得預熱關卡案例（從 <c>warmup.json</c>／<see cref="PuzzleTier.Warmup"/> 載入）。
    /// </summary>
    public IReadOnlyList<DatasetCase> WarmupCases { get; }

    /// <summary>
    /// 取得依階級順序排列的所有 T1–T5 計分基準測試案例。
    /// </summary>
    public IReadOnlyList<DatasetCase> BenchmarkCases { get; }

    /// <summary>
    /// 取得依 <see cref="PuzzleTier"/> 分組的案例集合。
    /// </summary>
    public IReadOnlyDictionary<PuzzleTier, IReadOnlyList<DatasetCase>> CasesByTier { get; }
}

/// <summary>
/// OrbSolverBenchmark 關卡的嚴格 JSON 資料集剖析器與結構定義（schema）驗證器。
/// </summary>
public static class DatasetLoader
{
    private static readonly JsonDocumentOptions StrictJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    private static readonly HashSet<string> AllowedCaseProperties = new(StringComparer.Ordinal)
    {
        "id",
        "tier",
        "description",
        "board",
        "obstacleCoords",
        "puzzleCoords",
        "puzzleColor",
        "maxSteps",
        "allowDiagonal",
        "seed",
        "timeBudgetMs",
        "requiredColors",
        "requiredColorsFirstWaveOnly",
        "requireAoeColors",
        "colorWeights",
        "referenceValue",
        "vRef"
    };

    private static readonly string[] RequiredCaseProperties =
    [
        "id",
        "tier",
        "board",
        "maxSteps",
        "allowDiagonal",
        "seed",
        "timeBudgetMs"
    ];

    /// <summary>
    /// 從磁碟載入並嚴格驗證 JSON 陣列資料集檔案。
    /// </summary>
    /// <param name="filePath">JSON 資料集檔案的路徑。</param>
    /// <returns>經過驗證的 <see cref="DatasetCase"/> 項目清單。</returns>
    /// <exception cref="ArgumentException">當 <paramref name="filePath"/> 為 null 或空白字元時擲出。</exception>
    /// <exception cref="FileNotFoundException">當 <paramref name="filePath"/> 不存在時擲出。</exception>
    /// <exception cref="DatasetValidationException">當 JSON 剖析或結構定義驗證失敗時擲出。</exception>
    public static IReadOnlyList<DatasetCase> LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Dataset file path must not be null or whitespace.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Dataset file not found: {filePath}", filePath);
        }

        string json = File.ReadAllText(filePath);
        return LoadFromJson(json, filePath);
    }

    /// <summary>
    /// 剖析並嚴格驗證 JSON 陣列資料集字串。
    /// </summary>
    /// <param name="json">代表非空關卡案例物件陣列的 JSON 文字。</param>
    /// <param name="sourceName">選填的來源檔案名稱，用於診斷訊息上下文。</param>
    /// <returns>經過驗證的 <see cref="DatasetCase"/> 項目清單。</returns>
    /// <exception cref="ArgumentNullException">當 <paramref name="json"/> 為 <see langword="null"/> 時擲出。</exception>
    /// <exception cref="DatasetValidationException">當 JSON 剖析或結構定義驗證失敗時擲出。</exception>
    public static IReadOnlyList<DatasetCase> LoadFromJson(string json, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, StrictJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new DatasetValidationException(
                $"Invalid JSON syntax: {ex.Message}",
                sourceName,
                innerException: ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new DatasetValidationException(
                    $"Dataset root must be a JSON array, found {doc.RootElement.ValueKind}.",
                    sourceName);
            }

            int length = doc.RootElement.GetArrayLength();
            if (length == 0)
            {
                throw new DatasetValidationException(
                    "Dataset array must not be empty.",
                    sourceName);
            }

            List<DatasetCase> cases = new(length);
            HashSet<string> seenIds = new(StringComparer.Ordinal);

            int index = 0;
            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                DatasetCase parsed = ParseCase(element, index, sourceName);
                if (!seenIds.Add(parsed.Id))
                {
                    throw new DatasetValidationException(
                        $"Duplicate puzzle id '{parsed.Id}'.",
                        sourceName,
                        parsed.Id,
                        "id");
                }

                cases.Add(parsed);
                index++;
            }

            return cases;
        }
    }

    /// <summary>
    /// 從 <paramref name="directoryPath"/> 載入所有 <c>*.json</c> 資料集檔案，將預熱關卡與 T1–T5 階級分開。
    /// </summary>
    /// <param name="directoryPath">包含 <c>warmup.json</c> 與各階級資料集檔案的目錄。</param>
    /// <returns>將預熱與計分案例分組的 <see cref="BenchmarkDataset"/>。</returns>
    /// <exception cref="ArgumentException">當 <paramref name="directoryPath"/> 為 null 或空白字元時擲出。</exception>
    /// <exception cref="DirectoryNotFoundException">當 <paramref name="directoryPath"/> 不存在時擲出。</exception>
    /// <exception cref="DatasetValidationException">當不存在 JSON 檔案、結構定義驗證失敗，或跨檔案出現重複識別碼時擲出。</exception>
    public static BenchmarkDataset LoadBenchmarkDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Dataset directory path must not be null or whitespace.", nameof(directoryPath));
        }

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Dataset directory not found: {directoryPath}");
        }

        string[] files = Directory.GetFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        if (files.Length == 0)
        {
            throw new DatasetValidationException(
                "Dataset directory contains no .json files.",
                directoryPath);
        }

        List<DatasetCase> warmupCases = [];
        List<DatasetCase> benchmarkCases = [];
        Dictionary<PuzzleTier, List<DatasetCase>> grouped = new();
        HashSet<string> globalIds = new(StringComparer.Ordinal);

        foreach (string file in files)
        {
            bool isWarmupFile = string.Equals(
                Path.GetFileName(file),
                "warmup.json",
                StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<DatasetCase> loaded = LoadFromFile(file);
            foreach (DatasetCase item in loaded)
            {
                if (!globalIds.Add(item.Id))
                {
                    throw new DatasetValidationException(
                        $"Duplicate puzzle id '{item.Id}' across dataset files.",
                        file,
                        item.Id,
                        "id");
                }

                if (isWarmupFile && item.Tier != PuzzleTier.Warmup)
                {
                    throw new DatasetValidationException(
                        $"warmup.json may only contain Warmup tier cases, found '{item.Tier}'.",
                        file,
                        item.Id,
                        "tier");
                }

                if (!isWarmupFile && item.Tier == PuzzleTier.Warmup)
                {
                    throw new DatasetValidationException(
                        $"Warmup tier cases must be placed in warmup.json, found in '{Path.GetFileName(file)}'.",
                        file,
                        item.Id,
                        "tier");
                }

                if (!grouped.TryGetValue(item.Tier, out List<DatasetCase>? bucket))
                {
                    bucket = [];
                    grouped[item.Tier] = bucket;
                }

                bucket.Add(item);
                if (item.Tier == PuzzleTier.Warmup)
                {
                    warmupCases.Add(item);
                }
                else
                {
                    benchmarkCases.Add(item);
                }
            }
        }

        benchmarkCases.Sort((a, b) => a.Tier.CompareTo(b.Tier));

        Dictionary<PuzzleTier, IReadOnlyList<DatasetCase>> readOnlyGroups = new(grouped.Count);
        foreach ((PuzzleTier tier, List<DatasetCase> list) in grouped)
        {
            readOnlyGroups[tier] = list.AsReadOnly();
        }

        return new BenchmarkDataset(
            warmupCases.AsReadOnly(),
            benchmarkCases.AsReadOnly(),
            readOnlyGroups);
    }

    /// <summary>
    /// 驗證並將單一 JSON 物件項目剖析為 <see cref="DatasetCase"/>。
    /// </summary>
    /// <param name="element">代表單一關卡案例的 JSON 物件項目。</param>
    /// <param name="index">項目在所屬陣列中以零為起始的索引（用於錯誤診斷）。</param>
    /// <param name="sourceName">選填的來源檔案名稱，用於錯誤診斷。</param>
    /// <returns>經過驗證的 <see cref="DatasetCase"/>。</returns>
    /// <exception cref="DatasetValidationException">當違反任何必填欄位、型別、座標或數值限制條件時擲出。</exception>
    public static DatasetCase ParseCase(JsonElement element, int index = 0, string? sourceName = null)
    {
        string fallbackCaseId = $"#{index}";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DatasetValidationException(
                $"Dataset element at index {index} must be a JSON object, found {element.ValueKind}.",
                sourceName,
                fallbackCaseId);
        }

        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (!AllowedCaseProperties.Contains(prop.Name))
            {
                throw new DatasetValidationException(
                    $"Unknown property '{prop.Name}'.",
                    sourceName,
                    TryPeekId(element) ?? fallbackCaseId,
                    prop.Name);
            }

            if (!properties.TryAdd(prop.Name, prop.Value))
            {
                throw new DatasetValidationException(
                    $"Duplicate property '{prop.Name}'.",
                    sourceName,
                    TryPeekId(element) ?? fallbackCaseId,
                    prop.Name);
            }
        }

        string? peekedId = TryPeekId(element);
        foreach (string required in RequiredCaseProperties)
        {
            if (!properties.ContainsKey(required))
            {
                throw new DatasetValidationException(
                    $"Missing required property '{required}'.",
                    sourceName,
                    peekedId ?? fallbackCaseId,
                    required);
            }
        }

        string id = ParseId(properties["id"], sourceName, fallbackCaseId);
        PuzzleTier tier = ParseTier(properties["tier"], sourceName, id);

        string description = string.Empty;
        if (properties.TryGetValue("description", out JsonElement descEl) && descEl.ValueKind != JsonValueKind.Null)
        {
            if (descEl.ValueKind != JsonValueKind.String)
            {
                throw new DatasetValidationException(
                    $"Property 'description' must be a string or null, found {descEl.ValueKind}.",
                    sourceName,
                    id,
                    "description");
            }

            description = descEl.GetString() ?? string.Empty;
        }

        (string boardString, OrbType[] cells) = ParseBoard(properties["board"], sourceName, id);

        uint obstacleMask = properties.TryGetValue("obstacleCoords", out JsonElement obstacleEl)
            ? ParseCoordMask(obstacleEl, "obstacleCoords", sourceName, id)
            : 0u;

        uint puzzleMask = properties.TryGetValue("puzzleCoords", out JsonElement puzzleEl)
            ? ParseCoordMask(puzzleEl, "puzzleCoords", sourceName, id)
            : 0u;

        if ((obstacleMask & puzzleMask) != 0)
        {
            throw new DatasetValidationException(
                "obstacleCoords and puzzleCoords must not overlap.",
                sourceName,
                id,
                "puzzleCoords");
        }

        OrbType puzzleColor = OrbType.Empty;
        if (properties.TryGetValue("puzzleColor", out JsonElement puzzleColorEl))
        {
            puzzleColor = ParsePuzzleColor(puzzleColorEl, sourceName, id);
        }

        if (puzzleMask == 0 && puzzleColor != OrbType.Empty)
        {
            throw new DatasetValidationException(
                $"puzzleColor '{puzzleColor}' cannot be specified when puzzleCoords is empty.",
                sourceName,
                id,
                "puzzleColor");
        }

        int maxSteps = ParseInt32(properties["maxSteps"], "maxSteps", sourceName, id);
        if (maxSteps < 0)
        {
            throw new DatasetValidationException(
                $"maxSteps must be >= 0, found {maxSteps}.",
                sourceName,
                id,
                "maxSteps");
        }

        bool allowDiagonal = ParseBoolean(properties["allowDiagonal"], "allowDiagonal", sourceName, id);
        int seed = ParseInt32(properties["seed"], "seed", sourceName, id);

        int timeBudgetMs = ParseInt32(properties["timeBudgetMs"], "timeBudgetMs", sourceName, id);
        if (timeBudgetMs <= 0)
        {
            throw new DatasetValidationException(
                $"timeBudgetMs must be > 0, found {timeBudgetMs}.",
                sourceName,
                id,
                "timeBudgetMs");
        }

        byte requiredColorsMask = properties.TryGetValue("requiredColors", out JsonElement reqColorsEl)
            ? ParseColorMask(reqColorsEl, "requiredColors", sourceName, id)
            : (byte)0;

        bool requiredColorsFirstWaveOnly = properties.TryGetValue("requiredColorsFirstWaveOnly", out JsonElement firstWaveEl)
            && ParseBoolean(firstWaveEl, "requiredColorsFirstWaveOnly", sourceName, id);

        byte requireAoeColorMask = properties.TryGetValue("requireAoeColors", out JsonElement aoeColorsEl)
            ? ParseColorMask(aoeColorsEl, "requireAoeColors", sourceName, id)
            : (byte)0;

        float[] colorWeights = properties.TryGetValue("colorWeights", out JsonElement weightsEl)
            ? ParseColorWeights(weightsEl, sourceName, id)
            : CreateDefaultColorWeights();

        bool hasReferenceValue = properties.TryGetValue("referenceValue", out JsonElement refValueEl);
        bool hasVRef = properties.TryGetValue("vRef", out JsonElement vRefEl);
        if (hasReferenceValue && hasVRef)
        {
            throw new DatasetValidationException(
                "Properties 'referenceValue' and 'vRef' cannot both be present on the same case.",
                sourceName,
                id,
                "referenceValue");
        }

        double? referenceValue = null;
        if (hasReferenceValue)
        {
            referenceValue = ParseReferenceValue(refValueEl, "referenceValue", sourceName, id);
        }
        else if (hasVRef)
        {
            referenceValue = ParseReferenceValue(vRefEl, "vRef", sourceName, id);
        }

        return new DatasetCase(
            id,
            tier,
            description,
            boardString,
            cells,
            obstacleMask,
            puzzleMask,
            puzzleColor,
            maxSteps,
            allowDiagonal,
            seed,
            timeBudgetMs,
            requiredColorsMask,
            requiredColorsFirstWaveOnly,
            requireAoeColorMask,
            colorWeights,
            referenceValue);
    }

    private static string? TryPeekId(JsonElement obj)
    {
        if (obj.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            string? val = idEl.GetString();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        return null;
    }

    private static string ParseId(JsonElement el, string? sourceName, string fallbackCaseId)
    {
        if (el.ValueKind != JsonValueKind.String)
        {
            throw new DatasetValidationException(
                $"Property 'id' must be a string, found {el.ValueKind}.",
                sourceName,
                fallbackCaseId,
                "id");
        }

        string? value = el.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DatasetValidationException(
                "Property 'id' must not be empty or whitespace.",
                sourceName,
                fallbackCaseId,
                "id");
        }

        return value;
    }

    private static PuzzleTier ParseTier(JsonElement el, string? sourceName, string caseId)
    {
        if (el.ValueKind != JsonValueKind.String)
        {
            throw new DatasetValidationException(
                $"Property 'tier' must be a string, found {el.ValueKind}.",
                sourceName,
                caseId,
                "tier");
        }

        string? value = el.GetString();
        return value?.ToUpperInvariant() switch
        {
            "WARMUP" => PuzzleTier.Warmup,
            "T1" => PuzzleTier.T1,
            "T2" => PuzzleTier.T2,
            "T3" => PuzzleTier.T3,
            "T4" => PuzzleTier.T4,
            "T5" => PuzzleTier.T5,
            _ => throw new DatasetValidationException(
                $"Invalid tier '{value}'. Expected Warmup, T1, T2, T3, T4, or T5.",
                sourceName,
                caseId,
                "tier")
        };
    }

    private static (string Raw, OrbType[] Cells) ParseBoard(JsonElement el, string? sourceName, string caseId)
    {
        if (el.ValueKind != JsonValueKind.String)
        {
            throw new DatasetValidationException(
                $"Property 'board' must be a string, found {el.ValueKind}.",
                sourceName,
                caseId,
                "board");
        }

        string raw = el.GetString() ?? string.Empty;
        if (raw.Length != PathValidator.BoardCellCount)
        {
            throw new DatasetValidationException(
                $"Board string must have length {PathValidator.BoardCellCount}, found {raw.Length}.",
                sourceName,
                caseId,
                "board");
        }

        OrbType[] cells = new OrbType[PathValidator.BoardCellCount];
        for (int i = 0; i < raw.Length; i++)
        {
            cells[i] = raw[i] switch
            {
                'W' => OrbType.Water,
                'F' => OrbType.Fire,
                'G' => OrbType.Wood,
                'L' => OrbType.Light,
                'D' => OrbType.Dark,
                'H' => OrbType.Heart,
                _ => throw new DatasetValidationException(
                    $"Invalid board character '{raw[i]}' at index {i}. Allowed: W, F, G, L, D, H.",
                    sourceName,
                    caseId,
                    "board")
            };
        }

        return (raw, cells);
    }

    private static uint ParseCoordMask(JsonElement el, string fieldName, string? sourceName, string caseId)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new DatasetValidationException(
                $"Property '{fieldName}' must be an array of [row, col] pairs, found {el.ValueKind}.",
                sourceName,
                caseId,
                fieldName);
        }

        uint mask = 0;
        int idx = 0;
        foreach (JsonElement pair in el.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2)
            {
                throw new DatasetValidationException(
                    $"Coordinate entry [{idx}] in '{fieldName}' must be a 2-element integer array [row, col].",
                    sourceName,
                    caseId,
                    fieldName);
            }

            JsonElement rowEl = pair[0];
            JsonElement colEl = pair[1];
            if (rowEl.ValueKind != JsonValueKind.Number
                || colEl.ValueKind != JsonValueKind.Number
                || !rowEl.TryGetInt32(out int row)
                || !colEl.TryGetInt32(out int col))
            {
                throw new DatasetValidationException(
                    $"Coordinate entry [{idx}] in '{fieldName}' must contain integer values.",
                    sourceName,
                    caseId,
                    fieldName);
            }

            if ((uint)row >= PathValidator.BoardRows || (uint)col >= PathValidator.BoardCols)
            {
                throw new DatasetValidationException(
                    $"Coordinate ({row}, {col}) in '{fieldName}' is out of bounds (row 0..4, col 0..5).",
                    sourceName,
                    caseId,
                    fieldName);
            }

            uint bit = 1u << (row * PathValidator.BoardCols + col);
            if ((mask & bit) != 0)
            {
                throw new DatasetValidationException(
                    $"Duplicate coordinate ({row}, {col}) in '{fieldName}'.",
                    sourceName,
                    caseId,
                    fieldName);
            }

            mask |= bit;
            idx++;
        }

        return mask;
    }

    private static OrbType ParsePuzzleColor(JsonElement el, string? sourceName, string caseId)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            return OrbType.Empty;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            throw new DatasetValidationException(
                $"Property 'puzzleColor' must be a string or null, found {el.ValueKind}.",
                sourceName,
                caseId,
                "puzzleColor");
        }

        string? value = el.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return OrbType.Empty;
        }

        if (string.Equals(value, "Empty", StringComparison.OrdinalIgnoreCase))
        {
            return OrbType.Empty;
        }

        if (TryParseNonEmptyOrbType(value, out OrbType orbType))
        {
            return orbType;
        }

        throw new DatasetValidationException(
            $"Invalid puzzleColor '{value}'.",
            sourceName,
            caseId,
            "puzzleColor");
    }

    private static int ParseInt32(JsonElement el, string fieldName, string? sourceName, string caseId)
    {
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int value))
        {
            throw new DatasetValidationException(
                $"Property '{fieldName}' must be a 32-bit integer.",
                sourceName,
                caseId,
                fieldName);
        }

        return value;
    }

    private static bool ParseBoolean(JsonElement el, string fieldName, string? sourceName, string caseId)
    {
        if (el.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (el.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        throw new DatasetValidationException(
            $"Property '{fieldName}' must be a boolean, found {el.ValueKind}.",
            sourceName,
            caseId,
            fieldName);
    }

    private static byte ParseColorMask(JsonElement el, string fieldName, string? sourceName, string caseId)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            throw new DatasetValidationException(
                $"Property '{fieldName}' must be an array of orb color names, found {el.ValueKind}.",
                sourceName,
                caseId,
                fieldName);
        }

        byte mask = 0;
        int idx = 0;
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new DatasetValidationException(
                    $"Item [{idx}] in '{fieldName}' must be a string, found {item.ValueKind}.",
                    sourceName,
                    caseId,
                    fieldName);
            }

            string? name = item.GetString();
            if (!TryParseNonEmptyOrbType(name, out OrbType orbType))
            {
                throw new DatasetValidationException(
                    $"Invalid orb color '{name}' at index [{idx}] in '{fieldName}'.",
                    sourceName,
                    caseId,
                    fieldName);
            }

            mask |= (byte)(1 << (int)orbType);
            idx++;
        }

        return mask;
    }

    private static float[] ParseColorWeights(JsonElement el, string? sourceName, string caseId)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new DatasetValidationException(
                $"Property 'colorWeights' must be a JSON object, found {el.ValueKind}.",
                sourceName,
                caseId,
                "colorWeights");
        }

        float[] weights = CreateDefaultColorWeights();
        HashSet<OrbType> seenColors = [];

        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (!TryParseNonEmptyOrbType(prop.Name, out OrbType orbType))
            {
                throw new DatasetValidationException(
                    $"Invalid orb color key '{prop.Name}' in 'colorWeights'.",
                    sourceName,
                    caseId,
                    "colorWeights");
            }

            if (!seenColors.Add(orbType))
            {
                throw new DatasetValidationException(
                    $"Duplicate color key '{prop.Name}' in 'colorWeights'.",
                    sourceName,
                    caseId,
                    "colorWeights");
            }

            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double rawWeight))
            {
                throw new DatasetValidationException(
                    $"Weight for '{prop.Name}' in 'colorWeights' must be a finite number.",
                    sourceName,
                    caseId,
                    "colorWeights");
            }

            float weight = (float)rawWeight;
            if (!float.IsFinite(weight) || weight < 0f)
            {
                throw new DatasetValidationException(
                    $"Weight for '{prop.Name}' in 'colorWeights' must be finite and >= 0, found {rawWeight}.",
                    sourceName,
                    caseId,
                    "colorWeights");
            }

            weights[(int)orbType] = weight;
        }

        return weights;
    }

    private static double? ParseReferenceValue(JsonElement el, string fieldName, string? sourceName, string caseId)
    {
        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double value))
        {
            throw new DatasetValidationException(
                $"Property '{fieldName}' must be a finite number or null.",
                sourceName,
                caseId,
                fieldName);
        }

        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new DatasetValidationException(
                $"Property '{fieldName}' must be finite and >= 0, found {value}.",
                sourceName,
                caseId,
                fieldName);
        }

        return value;
    }

    private static float[] CreateDefaultColorWeights() =>
        [0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f];

    private static bool TryParseNonEmptyOrbType(string? name, out OrbType orbType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            orbType = OrbType.Empty;
            return false;
        }

        switch (name.ToUpperInvariant())
        {
            case "WATER":
                orbType = OrbType.Water;
                return true;
            case "FIRE":
                orbType = OrbType.Fire;
                return true;
            case "WOOD":
                orbType = OrbType.Wood;
                return true;
            case "LIGHT":
                orbType = OrbType.Light;
                return true;
            case "DARK":
                orbType = OrbType.Dark;
                return true;
            case "HEART":
                orbType = OrbType.Heart;
                return true;
            default:
                orbType = OrbType.Empty;
                return false;
        }
    }
}
