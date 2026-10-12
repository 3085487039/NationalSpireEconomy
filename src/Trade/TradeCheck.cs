namespace NationalSpireEconomy.Trade;

/// <summary>
/// 一条校验结果。<see cref="Ok"/> 为 true 时表示这一环通过。
/// <para>
/// <b>软失败</b>（<see cref="Soft"/>）是一条独立的通路：它同样以 <c>Ok = false</c> 表达"这笔做不成"，
/// 但调用方<b>不能</b>落 <c>TradeOffer</c>、不能生成失败卡片，而是要把 <see cref="InjectLine"/>
/// 写进 <c>NpcEconomy.PendingVoice</c>，让 NPC 用自己的口吻在对话里自然说出来。
/// </para>
/// </summary>
public sealed record TradeCheck(
    bool Ok,
    string Code,
    string Message,
    bool Soft = false,
    string InjectLine = "")
{
    /// <summary>校验通过。</summary>
    public static readonly TradeCheck Pass = new(true, "OK", "");

    public static TradeCheck Fail(string code, string message) => new(false, code, message);

    /// <summary>软失败：不报错，只留一句素材给下一轮 Prompt。</summary>
    public static TradeCheck SoftDecline(string code, string injectLine, string message = "")
        => new(false, code, message, Soft: true, InjectLine: injectLine);

    public bool Failed => !Ok;
}

/// <summary>
/// 校验失败的 Code 常量。集中在这里，一方面避免各处字符串拼错导致幂等键与日志分叉，
/// 另一方面让"哪些 Code 属于软失败"这件事只有一个定义点。
/// </summary>
public static class TradeCodes
{
    public const string Ok = "OK";

    // 1. 形状
    public const string BadKind = "BAD_KIND";
    public const string BadShape = "BAD_SHAPE";
    public const string ParseError = "PARSE_ERROR";

    // 2. 金额边界
    public const string AmountTooSmall = "AMOUNT_TOO_SMALL";
    public const string AmountTooLarge = "AMOUNT_TOO_LARGE";

    // 3. 目录
    public const string ItemUnknown = "ITEM_UNKNOWN";
    public const string ItemOutOfStock = "ITEM_OUT_OF_STOCK";
    public const string ItemPriceMismatch = "ITEM_PRICE_MISMATCH";
    public const string ItemQuantityInvalid = "ITEM_QUANTITY_INVALID";

    // 4. 余额
    public const string NpcInsufficient = "NPC_INSUFFICIENT";
    public const string PlayerInsufficient = "PLAYER_INSUFFICIENT";
    public const string NpcSoftDecline = "NPC_SOFT_DECLINE";

    // 5. 频次
    public const string DailyLimit = "DAILY_LIMIT";

    // 6. 性格
    public const string PersonalityRefusesCharity = "PERSONALITY_REFUSES_CHARITY";
    public const string PersonalityCap = "PERSONALITY_CAP";
    public const string PersonalityLowEmotion = "PERSONALITY_LOW_EMOTION";
    public const string PersonalityRefusesLoan = "PERSONALITY_REFUSES_LOAN";
    public const string NoCounterparty = "NO_COUNTERPARTY";

    // 7. 好感
    public const string FavourTooLow = "FAVOUR_TOO_LOW";

    // 8. 幂等
    public const string AlreadyApplied = "ALREADY_APPLIED";

    // 执行期
    public const string OfferNotFound = "OFFER_NOT_FOUND";
    public const string OfferNotPending = "OFFER_NOT_PENDING";
    public const string OfferExpired = "OFFER_EXPIRED";
    public const string ExecuteFailed = "EXECUTE_FAILED";
}
