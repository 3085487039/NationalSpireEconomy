namespace NationalSpireEconomy.Trade;

/// <summary>
/// 两段式执行器。
/// <para>
/// <b>为什么必须两段</b>：模型提出报价之后，玩家可能隔很久才确认。这中间余额会变、
/// 性格可能被 <c>[Profile: ...]</c> 改写、NPC 可能被交易走或退役。所以
/// <see cref="Reserve"/> 只生成待确认单并<b>不动任何余额</b>（只做一次预校验，
/// 把当时的校验码存进 <c>ValidationStamp</c>），而 <see cref="Commit"/> 必须
/// <b>重新跑完整校验链</b>才动账——这正是原版 <c>PrivateContracts.Confirm</c> 的做法。
/// </para>
/// </summary>
public interface ITradeExecutor
{
    /// <summary>生成待确认交易单。不改变任何余额。</summary>
    ReserveResult Reserve(TradeContext ctx, TradeIntent intent);

    /// <summary>玩家确认后真正执行。内部必须重新校验。</summary>
    CommitResult Commit(TradeContext ctx, string offerId);
}

/// <summary><see cref="ITradeExecutor.Reserve"/> 的结果。</summary>
public sealed record ReserveResult(
    bool Ok,
    string? OfferId,
    TradeCheck Check,
    bool SoftDeclined = false,
    string InjectLine = "")
{
    public static ReserveResult Rejected(TradeCheck check) => new(false, null, check);

    public static ReserveResult Soft(TradeCheck check) =>
        new(false, null, check, SoftDeclined: true, InjectLine: check.InjectLine);
}

/// <summary><see cref="ITradeExecutor.Commit"/> 的结果。</summary>
public sealed record CommitResult(
    bool Ok,
    TradeCheck Check,
    string? LedgerEntryId,
    long NpcBalanceAfter,
    long PlayerBalanceAfter)
{
    public static CommitResult Failed(TradeCheck check, long npcBalance = 0, long playerBalance = 0) =>
        new(false, check, null, npcBalance, playerBalance);
}
