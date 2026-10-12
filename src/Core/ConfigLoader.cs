using System.Text.Json;
using System.Text.Json.Nodes;

namespace NationalSpireEconomy.Core;

public enum ConfigIssueLevel
{
    Warning,
    Error
}

public sealed record ConfigIssue(ConfigIssueLevel Level, string File, string Message)
{
    public override string ToString() => $"[{Level}] {File}: {Message}";
}

/// <summary>一次加载的结果。配置有 Error 时调用方应拒绝启用模组功能，而不是带着坏配置继续跑。</summary>
public sealed class ConfigLoadResult
{
    public required EconomyConfig Economy { get; init; }
    public required PersonalityConfig Personality { get; init; }
    public required PromptConfig Prompt { get; init; }
    public required IReadOnlyList<ConfigIssue> Issues { get; init; }
    public required string RootDirectory { get; init; }

    public bool HasErrors => Issues.Any(i => i.Level == ConfigIssueLevel.Error);
    public IEnumerable<ConfigIssue> Errors => Issues.Where(i => i.Level == ConfigIssueLevel.Error);
    public IEnumerable<ConfigIssue> Warnings => Issues.Where(i => i.Level == ConfigIssueLevel.Warning);
}

/// <summary>
/// 三份 JSON 配置的加载、校验与热重载。Core 层不依赖 Godot，
/// 所以路径由调用方给出（接线层传 <c>user://</c> 或模组目录）。
/// </summary>
public sealed class ConfigLoader
{
    public const string EconomyFileName = "economy_config.json";
    public const string PersonalityFileName = "personality_economy.json";
    public const string PromptFileName = "ai_prompt_economy.json";

    /// <summary>
    /// NationalSpire <c>PersonalityLibrary.Kinds</c> 的规范列表（0.26.7，来源 <c>ExpandedPersonality.cs</c> 的 Dispositions）。
    /// 作为校验基准：缺项报警告，多项也报警告（说明原版加了新性格而我们没跟上）。
    /// </summary>
    public static readonly string[] CanonicalPersonalityKinds =
    [
        "暴躁喷子", "尖刻挑刺者", "战绩踩头者", "打法优越者", "嘴硬争胜者", "嫉妒比较者",
        "怨气抱怨者", "反感说教者", "狂热拥护者", "护短熟人", "夸张吹捧者", "热心同好",
        "兴奋追随者", "较真维护者", "自我炫耀者", "故意挑事者", "反串拱火者", "看戏起哄者",
        "反骨抬杠者", "自认客观者", "刻薄玩笑者", "不服输挑战者", "直率求助者", "自嘲找同伴者"
    ];

    private static readonly string[] PromptTableKeys = ["moneyAttitude", "intentPhrase", "incomeSource", "weightBands"];

    /// <summary>
    /// 措辞表在 JSON 里可能嵌在带 <c>_comment</c> 说明的对象内，
    /// 也可能直接平铺在顶层。两处都读，显式键优先于顶层同名表。
    /// </summary>
    private static readonly string[] NestedTableContainers = ["_weightBands", "weightBands"];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _root;
    private readonly IEconomyLog _log;
    private readonly Dictionary<string, DateTime> _stamps = new();

    public ConfigLoader(string rootDirectory, IEconomyLog? log = null)
    {
        _root = rootDirectory;
        _log = log ?? ConsoleEconomyLog.Instance;
    }

    public string PathOf(string fileName) => Path.Combine(_root, fileName);

    /// <summary>加载并校验。文件缺失时使用内置默认值并给出 Error，便于在启动日志里立刻暴露部署问题。</summary>
    public ConfigLoadResult Load()
    {
        var issues = new List<ConfigIssue>();

        var economy = ReadOrNull<EconomyConfig>(EconomyFileName, issues) ?? new EconomyConfig();
        var personality = ReadOrNull<PersonalityConfig>(PersonalityFileName, issues) ?? new PersonalityConfig();
        var prompt = ReadPrompt(issues);

        ValidateEconomy(economy, issues);
        ValidatePersonality(personality, issues);
        ValidatePrompt(prompt, issues);

        RememberStamps();

        foreach (var issue in issues)
        {
            if (issue.Level == ConfigIssueLevel.Error) _log.Error("config", issue.ToString());
            else _log.Warn("config", issue.ToString());
        }
        if (issues.Count == 0) _log.Info("config", $"配置加载完成，目录 {_root}");

        return new ConfigLoadResult
        {
            Economy = economy,
            Personality = personality,
            Prompt = prompt,
            Issues = issues,
            RootDirectory = _root
        };
    }

    /// <summary>文件有变动才重新加载；用于「改完 JSON 不必重启游戏」。</summary>
    public ConfigLoadResult? ReloadIfChanged()
    {
        foreach (string file in new[] { EconomyFileName, PersonalityFileName, PromptFileName })
        {
            string path = PathOf(file);
            if (!File.Exists(path)) continue;
            if (!_stamps.TryGetValue(file, out var stamp) || stamp != File.GetLastWriteTimeUtc(path))
            {
                _log.Info("config", $"检测到 {file} 变动，重新加载。");
                return Load();
            }
        }
        return null;
    }

    private void RememberStamps()
    {
        _stamps.Clear();
        foreach (string file in new[] { EconomyFileName, PersonalityFileName, PromptFileName })
        {
            string path = PathOf(file);
            if (File.Exists(path)) _stamps[file] = File.GetLastWriteTimeUtc(path);
        }
    }

    private T? ReadOrNull<T>(string fileName, List<ConfigIssue> issues) where T : class
    {
        string path = PathOf(fileName);
        if (!File.Exists(path))
        {
            issues.Add(new(ConfigIssueLevel.Error, fileName, "文件不存在，已使用内置默认值。"));
            return null;
        }
        try
        {
            string text = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<T>(text, SerializerOptions);
            if (value == null) issues.Add(new(ConfigIssueLevel.Error, fileName, "解析结果为空。"));
            return value;
        }
        catch (JsonException e)
        {
            issues.Add(new(ConfigIssueLevel.Error, fileName, "JSON 语法错误：" + e.Message));
            return null;
        }
        catch (IOException e)
        {
            issues.Add(new(ConfigIssueLevel.Error, fileName, "读取失败：" + e.Message));
            return null;
        }
    }

    private PromptConfig ReadPrompt(List<ConfigIssue> issues)
    {
        const string fileName = PromptFileName;
        string path = PathOf(fileName);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var prompt = new PromptConfig(values);

        if (!File.Exists(path))
        {
            issues.Add(new(ConfigIssueLevel.Error, fileName, "文件不存在，注入模板将回退到内置默认。"));
            foreach (var key in PromptTableKeys) prompt.AddTable(key, new Dictionary<string, string>());
            return prompt;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions);
            if (root is not JsonObject obj)
            {
                issues.Add(new(ConfigIssueLevel.Error, fileName, "顶层必须是 JSON 对象。"));
                return prompt;
            }

            foreach (var pair in obj)
            {
                switch (pair.Value)
                {
                    case JsonValue value when value.TryGetValue<string>(out var text):
                        values[pair.Key] = text;
                        break;
                    case JsonObject nested:
                    {
                        // 容器对象（例如 _weightBands）：整个对象是【一张】表，
                        // 必须在"跳过下划线键"之前处理，否则容器自身会被当成注释丢掉。
                        if (NestedTableContainers.Contains(pair.Key))
                        {
                            var container = new Dictionary<string, string>(StringComparer.Ordinal);
                            foreach (var inner in nested)
                            {
                                if (inner.Key.StartsWith('_')) continue;
                                if (inner.Value is JsonValue innerValue && innerValue.TryGetValue<string>(out var innerText))
                                    container[inner.Key] = innerText;
                            }
                            tables[pair.Key.TrimStart('_')] = container;
                            break;
                        }

                        var table = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var entry in nested)
                        {
                            if (entry.Key.StartsWith('_')) continue;   // _comment 之类的结构化键
                            if (entry.Value is JsonValue item && item.TryGetValue<string>(out var itemText))
                                table[entry.Key] = itemText;
                        }
                        tables[pair.Key] = table;
                        break;
                    }
                    // 数组与其它类型忽略，配置里只应有字符串与字符串表。
                }
            }
        }
        catch (JsonException e)
        {
            issues.Add(new(ConfigIssueLevel.Error, fileName, "JSON 语法错误：" + e.Message));
        }
        catch (IOException e)
        {
            issues.Add(new(ConfigIssueLevel.Error, fileName, "读取失败：" + e.Message));
        }

        foreach (var key in PromptTableKeys)
            prompt.AddTable(key, tables.TryGetValue(key, out var table) ? table : new Dictionary<string, string>());

        return prompt;
    }

    // ── 校验 ──────────────────────────────────────────────────────────

    private static void ValidateEconomy(EconomyConfig config, List<ConfigIssue> issues)
    {
        const string file = EconomyFileName;
        if (config.ConfigVersion <= 0)
            issues.Add(new(ConfigIssueLevel.Warning, file, "configVersion 缺失或为 0。"));

        if (config.Seeding.Tiers.Count == 0)
        {
            issues.Add(new(ConfigIssueLevel.Error, file, "seeding.tiers 为空，无法播种初始资金。"));
        }
        else
        {
            // 兜底档位：不按 Role/Identities/Rating 匹配的那一项，按定义就不会命中任何人，
            // 它的作用只是「其他档位都没匹配上时用」。
            var fallback = config.Seeding.Tiers
                .Where(t => t.Roles.Count == 0 && t.IdentitiesContains.Count == 0 && t.RatingAtLeast is null)
                .ToArray();

            foreach (var tier in config.Seeding.Tiers)
            {
                if (tier.Min < 0 || tier.Max < tier.Min)
                    issues.Add(new(ConfigIssueLevel.Error, file, $"档位 {tier.Id} 的金额区间非法：{tier.Min} ~ {tier.Max}。"));
                if (tier.Max > config.Seeding.MaxWealth)
                    issues.Add(new(ConfigIssueLevel.Warning, file,
                        $"档位 {tier.Id} 的 max {tier.Max} 超过 seeding.maxWealth {config.Seeding.MaxWealth}，将被夹住。"));
                if (tier.Weight <= 0)
                    issues.Add(new(ConfigIssueLevel.Error, file, $"档位 {tier.Id} 的权重必须为正。"));
                bool isFallback = tier.Roles.Count == 0 && tier.IdentitiesContains.Count == 0 && tier.RatingAtLeast is null;
                if (isFallback && fallback.Length > 1)
                    issues.Add(new(ConfigIssueLevel.Warning, file,
                        $"存在 {fallback.Length} 个无条件兜底档位，只有第一个会生效：{string.Join("、", fallback.Select(t => t.Id))}"));
            }

            if (fallback.Length == 0)
                issues.Add(new(ConfigIssueLevel.Warning, file,
                    "缺少无条件兜底档位；未命中任何档位的 NPC 会退回 defaults，金额可能不符合预期。"));
        }

        if (config.Seeding.Curve is not ("log-uniform" or "uniform"))
            issues.Add(new(ConfigIssueLevel.Error, file, $"seeding.curve 只支持 log-uniform 或 uniform，实际为 {config.Seeding.Curve}。"));

        if (config.Season.TransferFeeClamp.Count != 2 || config.Season.TransferFeeClamp[0] > config.Season.TransferFeeClamp[1])
            issues.Add(new(ConfigIssueLevel.Error, file, "season.transferFeeClamp 必须是 [下限, 上限] 且下限不大于上限。"));

        if (config.Season.ExtremeGapRatio is <= 0 or >= 1)
            issues.Add(new(ConfigIssueLevel.Error, file, $"season.extremeGapRatio 应在 (0,1) 内，实际为 {config.Season.ExtremeGapRatio}。"));

        foreach (string id in config.Season.TierCapRatio.Keys)
            if (config.Season.TierCapRatio[id] is <= 0 or > 1)
                issues.Add(new(ConfigIssueLevel.Error, file, $"tierCapRatio[{id}] 应在 (0,1] 内。"));

        if (config.Trade.MinAmount < 0)
            issues.Add(new(ConfigIssueLevel.Error, file, "trade.minAmount 不能为负。"));
        if (config.Trade.MaxSingleAmount < config.Trade.MinAmount)
            issues.Add(new(ConfigIssueLevel.Error, file, "trade.maxSingleAmount 小于 minAmount。"));
        if (config.Trade.AllowedKinds.Count == 0)
            issues.Add(new(ConfigIssueLevel.Error, file, "trade.allowedKinds 为空，任何交易都会被拒。"));
        if (config.Trade.OfferExpiryDays <= 0)
            issues.Add(new(ConfigIssueLevel.Error, file, "trade.offerExpiryDays 必须为正。"));

        // minEmotionToAccept = 0 是合法的，表示关闭"金额分量够不够"这条校验；
        // 为负则会让情绪值恒大于门槛，等于静默关闭，多半是手误，所以单独拦一下。
        if (config.Trade.MinEmotionToAccept < 0)
            issues.Add(new(ConfigIssueLevel.Error, file,
                $"trade.minEmotionToAccept 不能为负（0 表示关闭该校验），实际 {config.Trade.MinEmotionToAccept}。"));
        if (config.Trade.MinEmotionToAccept > 10)
            issues.Add(new(ConfigIssueLevel.Warning, file,
                $"trade.minEmotionToAccept = {config.Trade.MinEmotionToAccept} 极高，" +
                "NPC 几乎会拒绝一切出钱（情绪值 = amount / max(500, balance × 0.10)）。"));

        if (config.Trade.DailyLimitPerNpc <= 0)
            issues.Add(new(ConfigIssueLevel.Error, file,
                $"trade.dailyLimitPerNpc 必须为正，实际 {config.Trade.DailyLimitPerNpc}，否则任何交易都会被限流拒掉。"));

        // 好感度取值域是 -100..100（PrivateMessages.Favour）。门槛超出这个范围时，
        // 要么永远通过、要么永远拒绝，都属于配置错误而不是运行期意外。
        if (config.Trade.RequireFavourForNpcPayout is < -100 or > 100)
            issues.Add(new(ConfigIssueLevel.Error, file,
                $"trade.requireFavourForNpcPayout 应在 -100~100 内，实际 {config.Trade.RequireFavourForNpcPayout}。"));

        // 白名单里要有"玩家给钱"和"玩家收钱"两类，否则交易系统只能单向工作。
        // 这里只做措辞级别的提醒：具体 Kind 名的语义由 TradeEngine 的校验链决定。
        if (config.Trade.AllowedKinds.Count > 0 && !config.Trade.AllowedKinds.Contains("转账"))
            issues.Add(new(ConfigIssueLevel.Warning, file,
                "allowedKinds 不含「转账」，玩家与 NPC 之间的通用转账将无法发起。"));
    }

    private static void ValidatePersonality(PersonalityConfig config, List<ConfigIssue> issues)
    {
        const string file = PersonalityFileName;

        var missing = CanonicalPersonalityKinds.Where(k => !config.Kinds.ContainsKey(k)).ToArray();
        if (missing.Length > 0)
            issues.Add(new(ConfigIssueLevel.Warning, file,
                $"以下 {missing.Length} 种性格缺少映射，将回退到 defaults：{string.Join("、", missing)}"));

        var extra = config.Kinds.Keys.Where(k => !CanonicalPersonalityKinds.Contains(k)).ToArray();
        if (extra.Length > 0)
            issues.Add(new(ConfigIssueLevel.Warning, file,
                $"以下 {extra.Length} 项不在原版性格列表中（可能是原版新增，或拼写错误）：{string.Join("、", extra)}"));

        foreach (var (kind, profile) in config.Kinds)
        {
            if (profile.Give is < 0.2 or > 1.5)
                issues.Add(new(ConfigIssueLevel.Error, file, $"{kind}.give 应在 0.2~1.5，实际 {profile.Give}。"));
            if (profile.CharitySensitivity is < 0 or > 0.95)
                issues.Add(new(ConfigIssueLevel.Error, file, $"{kind}.charitySensitivity 应在 0~0.95，实际 {profile.CharitySensitivity}。"));
            if (profile.Bargain is < 0 or > 0.95)
                issues.Add(new(ConfigIssueLevel.Error, file, $"{kind}.bargain 应在 0~0.95，实际 {profile.Bargain}。"));
            if (profile.Thrift is < 0.05 or > 1.0)
                issues.Add(new(ConfigIssueLevel.Error, file, $"{kind}.thrift 应在 0.05~1.0，实际 {profile.Thrift}。"));
            if (profile.Initiative is < 0 or > 1.0)
                issues.Add(new(ConfigIssueLevel.Error, file, $"{kind}.initiative 应在 0~1.0，实际 {profile.Initiative}。"));
            if (profile.Tags.Count == 0)
                issues.Add(new(ConfigIssueLevel.Warning, file, $"{kind} 没有 tags，Prompt 注入的「金钱观」会缺少人话素材。"));
        }

        if (config.FavorTiers.Tiers.Count == 0)
        {
            issues.Add(new(ConfigIssueLevel.Error, file, "favorTiers 为空，好感度修正无法生效。"));
        }
        else
        {
            foreach (var tier in config.FavorTiers.Tiers)
            {
                if (tier.Min > tier.Max)
                    issues.Add(new(ConfigIssueLevel.Error, file, $"好感档位 {tier.Id} 的区间非法：{tier.Min}~{tier.Max}。"));
                if (tier.CharityMultiplier is < 0 or > 1)
                    issues.Add(new(ConfigIssueLevel.Error, file, $"好感档位 {tier.Id} 的 charityMultiplier 应在 0~1。"));
                if (tier.Line is not ("sharp" or "gentle"))
                    issues.Add(new(ConfigIssueLevel.Error, file, $"好感档位 {tier.Id} 的 line 只能是 sharp 或 gentle。"));
            }

            // 覆盖性：好感度取值范围内必须总能命中一个档位，否则会落到未定义行为。
            foreach (var probe in new[] { -100, -1, 0, 19, 20, 49, 50, 79, 80, 100 })
                if (!config.FavorTiers.Tiers.Any(t => probe >= t.Min && probe <= t.Max))
                    issues.Add(new(ConfigIssueLevel.Error, file, $"好感度 {probe} 没有对应的档位。"));
        }

        var missingSharp = CanonicalPersonalityKinds.Where(k => !config.RefusalLines.Charity.Sharp.ContainsKey(k)).ToArray();
        if (missingSharp.Length > 0)
            issues.Add(new(ConfigIssueLevel.Warning, file,
                $"charity.sharp 缺少 {missingSharp.Length} 项，将回退到 default：{string.Join("、", missingSharp)}"));

        var missingGentle = CanonicalPersonalityKinds.Where(k => !config.RefusalLines.Charity.Gentle.ContainsKey(k)).ToArray();
        if (missingGentle.Length > 0)
            issues.Add(new(ConfigIssueLevel.Warning, file,
                $"charity.gentle 缺少 {missingGentle.Length} 项，将回退到 default：{string.Join("、", missingGentle)}"));

        foreach (string line in new[] { "sharp", "gentle" })
        {
            var table = line == "sharp" ? config.RefusalLines.Charity.Sharp : config.RefusalLines.Charity.Gentle;
            if (!table.ContainsKey("default"))
                issues.Add(new(ConfigIssueLevel.Error, file, $"charity.{line} 缺少 default 兜底台词。"));
        }

        if (!config.RefusalLines.Cap.ContainsKey("default"))
            issues.Add(new(ConfigIssueLevel.Error, file, "cap 缺少 default 兜底台词。"));
        if (string.IsNullOrWhiteSpace(config.RefusalLines.Soft.DefaultLine))
            issues.Add(new(ConfigIssueLevel.Error, file, "soft.defaultLine 不能为空。"));
    }

    private static void ValidatePrompt(PromptConfig prompt, List<ConfigIssue> issues)
    {
        const string file = PromptFileName;

        foreach (string key in new[] { "stableTierLine", "stableMoneyView", "stableIntentLine", "volatileCurrent", "volatileLastEntry", "volatilePendingVoice" })
            if (!prompt.TryGet(key, out _))
                issues.Add(new(ConfigIssueLevel.Error, file, $"缺少必需模板 {key}。"));

        // 稳定段必须零数字，否则前缀缓存会随余额抖动。这里只检查模板本身，
        // 运行期的实际文本由 CacheGuard 再校验一次。
        foreach (string key in new[] { "stableTierLine", "stableMoneyView", "stableIntentLine" })
        {
            if (!prompt.TryGet(key, out var template)) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(template, @"\d"))
                issues.Add(new(ConfigIssueLevel.Error, file,
                    $"{key} 含有数字字面量。稳定段必须零数字，数字只能出现在易变段，否则会破坏前缀缓存。"));
        }
    }
}
