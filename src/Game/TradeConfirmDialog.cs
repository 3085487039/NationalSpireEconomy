using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;
using NationalSpireEconomy.Trade;

namespace NationalSpireEconomy.Game;

/// <summary>
/// <b>阶段 4 第一步的"双人确认界面"</b>：用<b>游戏自带的官方弹窗</b>（<c>NModalContainer</c> +
/// <c>NGenericPopup</c>）把只读成交预览摆给玩家，玩家点「确认/拒绝」即投上自己那一票。
/// <para>
/// <b>为什么用游戏自带弹窗而不是自绘控件</b>（UI 可行性调研结论）：
/// <list type="number">
/// <item>这两个类是 <c>public</c>，且游戏自己在 <c>NGame</c> 的多处流程里就是这么用的
/// （<c>NGenericPopup.Create()</c> → <c>NModalContainer.Instance.Add(popup, …)</c>）；</item>
/// <item>它加载的是游戏<b>已预载</b>的场景，不碰资源路径、不新建独立 <c>Window</c>
/// （原版 NationalSpire 的注释明确说独立窗口会触发游戏"失焦静音"）；</item>
/// <item>入树由 <c>NModalContainer.Add</c> 内部走 <c>AddChildSafely</c>，它自己处理主线程与延迟挂载。</item>
/// </list>
/// </para>
/// <para>
/// <b>它一律是"尽力而为"</b>：任何一步不满足（非主线程、模态层还没就绪、已有别的弹窗、
/// 场景没预载、节点名对不上）都只写一行日志然后返回 false——<b>聊天指令那条路仍然有效</b>
/// （玩家在私信里回「确认 &lt;单号&gt;」）。弹窗失败绝不能让游戏或交易流程出事。
/// </para>
/// <para>
/// <b>开关</b>：环境变量 <c>NSE_CONFIRM_DIALOG=0</c> 可整体关掉弹窗，只留聊天指令。
/// </para>
/// </summary>
public static class TradeConfirmDialog
{
    /// <summary>弹窗总开关（默认开；设 <c>NSE_CONFIRM_DIALOG=0</c> 关掉）。</summary>
    public static bool Enabled =>
        System.Environment.GetEnvironmentVariable("NSE_CONFIRM_DIALOG") is not ("0" or "false");

    /// <summary>成功弹出过几次（诊断用）。</summary>
    public static int Shown { get; private set; }

    /// <summary>最近一次的结论（诊断用）。</summary>
    public static string LastReport { get; private set; } = "(尚未弹出)";

    /// <summary>游戏弹窗里"确认/取消"两个按钮用的本地化键（游戏自己的键，不新造词条）。</summary>
    public const string ConfirmLocKey = "GENERIC_POPUP.confirm";

    /// <summary>取消键。</summary>
    public const string CancelLocKey = "GENERIC_POPUP.cancel";

    /// <summary>
    /// 弹窗正文里那行"点下去会发生什么"的说明。
    /// <para>
    /// <b>为什么「确认并执行」写在正文而不是按钮上</b>：游戏的通用弹窗按钮吃的是它<b>自己的</b>
    /// 本地化键（<c>GENERIC_POPUP.confirm</c>，界面上显示「好的」），要换字就得往原版的本地化表里
    /// 塞词条——那是铁律 1 不允许的。所以把语义写进正文：<b>按钮点下去就是确认并执行</b>，
    /// 正文负责把后果说清楚（甲方拍板的"确认并执行"由此落地）。
    /// </para>
    /// <para>
    /// 开关没开（<c>NSE_SETTLEMENT</c> 未设）时，文案<b>逐字保持阶段 4 第一步的样子</b>。
    /// </para>
    /// </summary>
    public static string BodySuffix => TradeConfirmationGate.SettlementWired
        ? "点下面的按钮即【确认并执行】：双方都确认之后会立即结算——这位 NPC 的余额会真的减少，" +
          "你的影子余额会真的增加，并追加一行审计。结算结果会写进日志与私信里的经济段。"
        : "确认只会登记你的同意，不会动任何余额。" + TradeConfirmationGate.SettlementIsNextPhase;

    /// <summary>弹窗标题：结算已接线时点明"这一步真的会动钱"。</summary>
    public static string Title => TradeConfirmationGate.SettlementWired
        ? "交易确认（确认并执行）"
        : "交易确认（只读预览）";

    /// <summary>
    /// 尝试弹一次确认框。返回 <c>true</c> 表示"弹窗已经挂上去、等玩家点"；
    /// <c>false</c> 表示这次没弹（原因写进 <see cref="LastReport"/> 与日志）。
    /// </summary>
    public static bool TryAsk(string worldId, string offerId, TradePreview preview, IEconomyLog? log = null)
    {
        if (!Enabled)
        {
            LastReport = "弹窗被环境变量 NSE_CONFIRM_DIALOG=0 关闭";
            return false;
        }

        try
        {
            // ① 主线程：非主线程建/挂 Godot 节点不安全（游戏自己的 AddChildSafely 也这么做）。
            if (!NGame.IsMainThread())
            {
                LastReport = "不在 Godot 主线程，已改为主线程重试（本次不弹）";
                try
                {
                    Callable.From(() => TryAsk(worldId, offerId, preview, log)).CallDeferred();
                }
                catch (Exception e)
                {
                    LastReport += "｜CallDeferred 失败：" + e.GetType().Name;
                }
                return false;
            }

            // ② 模态层：Instance 是在它的 _Ready 里赋值的，太早拿到 null 属正常。
            var modal = NModalContainer.Instance;
            if (modal == null)
            {
                LastReport = "NModalContainer.Instance 还没就绪（太早），本次改为走聊天指令";
                log?.Warn("trade", "[Economy-UI] " + LastReport);
                return false;
            }

            // ③ 单模态不变量：已有别的弹窗时 Add 会静默丢弃，所以先自己判。
            if (modal.OpenModal != null)
            {
                LastReport = "已有别的模态在开着，不抢（本次改为走聊天指令）";
                log?.Info("trade", "[Economy-UI] " + LastReport);
                return false;
            }

            // ④ 官方预载场景；TestMode 下 Create 返回 null。
            var popup = NGenericPopup.Create();
            if (popup == null)
            {
                LastReport = "NGenericPopup.Create() 返回 null（可能处于 TestMode），本次改为走聊天指令";
                log?.Warn("trade", "[Economy-UI] " + LastReport);
                return false;
            }

            modal.Add(popup, true);

            var vertical = popup.GetNodeOrNull<NVerticalPopup>("VerticalPopup");
            if (vertical == null)
            {
                LastReport = "弹窗里找不到 VerticalPopup 节点，文案未设置（仍会等玩家点按钮）";
                log?.Warn("trade", "[Economy-UI] " + LastReport);
            }
            else
            {
                vertical.SetText(Title, preview.ReadOnlyText + "\n\n" + BodySuffix);
                vertical.InitYesButton(
                    new LocString("main_menu_ui", ConfirmLocKey),
                    _ => Answer(worldId, offerId, confirm: true, log));
                vertical.InitNoButton(
                    new LocString("main_menu_ui", CancelLocKey),
                    _ => Answer(worldId, offerId, confirm: false, log));
            }

            Shown++;
            LastReport = $"已弹出第 {Shown} 次确认框（交易单 {offerId}）";
            log?.Info("trade", $"[Economy-UI] {LastReport}｜{preview.Title}｜" +
                              $"{MoneyUnits.Format(preview.AmountUsd)}｜★只读，未动任何余额");
            return true;
        }
        catch (Exception e)
        {
            LastReport = "弹窗抛异常（已忽略，改为走聊天指令）：" + e.GetType().Name + ": " + e.Message;
            try
            {
                log?.Error("trade", "[Economy-UI] " + LastReport, e);
            }
            catch (Exception)
            {
                // 日志都失败就只能放弃。
            }
            return false;
        }
    }

    /// <summary>玩家点了按钮之后：投上玩家那一票（<b>仍然不动钱</b>）。</summary>
    private static void Answer(string worldId, string offerId, bool confirm, IEconomyLog? log)
    {
        string source = confirm ? "确认弹窗·确认" : "确认弹窗·拒绝";
        var status = TradePreviewWiring.ApplyPlayerAnswer(worldId, offerId, confirm, log, source);
        LastReport = status == null
            ? "玩家点了按钮，但没能登记（身份槽过期？）"
            : $"玩家选择：{(confirm ? "确认" : "拒绝")}｜{status.DetailText}";
    }

    // ─────────────────────────────────────────────────────────────────
    // 阶段 4 第三步 §7.2：结算结果弹窗（"当场回执"）
    // ─────────────────────────────────────────────────────────────────

    /// <summary>结果框弹出过几次（诊断用）。</summary>
    public static int ResultsShown { get; private set; }

    /// <summary>最近一次结果框的结论（诊断用）。</summary>
    public static string LastResultReport { get; private set; } = "(尚未弹出)";

    /// <summary>最近一次结果框的正文（诊断用；玩家看到的就是它）。</summary>
    public static string LastResultBody { get; private set; } = "";

    /// <summary>
    /// <b>把结算结果当场摆到玩家眼前</b>（阶段 4 第三步 §7.2 的"两者都做"：V 段回执 + 复用官方弹窗）。
    /// <para>
    /// 与确认框共用同一套骨架（主线程 → 模态层 → 单模态不变量 → 官方预载场景），
    /// 区别只有一个：<b>只留一个"知道了"按钮</b>（<c>NVerticalPopup.HideNoButton</c>）——
    /// 结果不需要玩家再选一次，多一个按钮只会让人以为还能反悔。
    /// </para>
    /// <para>
    /// <b>它一律是"尽力而为"</b>：弹不出来只写一行日志。<b>绝不能因为弹窗失败而影响结算结果</b>
    /// ——钱是账本的事，弹窗只是通知；下一轮私信的 V 段里那条回执照样在。
    /// </para>
    /// </summary>
    public static bool TryShowResult(string worldId, SettlementOutcome outcome, string npcLabel,
        IEconomyLog? log = null)
    {
        if (!Enabled)
        {
            LastResultReport = "结果弹窗被环境变量 NSE_CONFIRM_DIALOG=0 关闭";
            return false;
        }

        try
        {
            if (!NGame.IsMainThread())
            {
                LastResultReport = "不在 Godot 主线程，已改为主线程重试（本次不弹，V 段回执照样在）";
                try
                {
                    Callable.From(() => TryShowResult(worldId, outcome, npcLabel, log)).CallDeferred();
                }
                catch (Exception e)
                {
                    LastResultReport += "｜CallDeferred 失败：" + e.GetType().Name;
                }
                return false;
            }

            var modal = NModalContainer.Instance;
            if (modal == null)
            {
                LastResultReport = "NModalContainer.Instance 还没就绪（太早），结果只写进 V 段与日志";
                log?.Warn("trade", "[Economy-UI] " + LastResultReport);
                return false;
            }

            if (modal.OpenModal != null)
            {
                LastResultReport = "已有别的模态在开着，不抢（结果只写进 V 段与日志）";
                log?.Info("trade", "[Economy-UI] " + LastResultReport);
                return false;
            }

            var popup = NGenericPopup.Create();
            if (popup == null)
            {
                LastResultReport = "NGenericPopup.Create() 返回 null（可能处于 TestMode），结果只写进 V 段与日志";
                log?.Warn("trade", "[Economy-UI] " + LastResultReport);
                return false;
            }

            modal.Add(popup, true);

            string title = ResultTitle(outcome);
            string body = TradeNoteText.ReceiptDetail(outcome, npcLabel);
            LastResultBody = body;

            var vertical = popup.GetNodeOrNull<NVerticalPopup>("VerticalPopup");
            if (vertical == null)
            {
                LastResultReport = "弹窗里找不到 VerticalPopup 节点，文案未设置（仍会等玩家点按钮）";
                log?.Warn("trade", "[Economy-UI] " + LastResultReport);
            }
            else
            {
                vertical.SetText(title, body);
                vertical.InitYesButton(
                    new LocString("main_menu_ui", ConfirmLocKey),
                    _ => { LastResultReport = "玩家点了「知道了」（结果框）"; });
                // 结果不需要第二个选择：用官方自己的方法把"取消"藏掉，
                // 免得玩家以为还能反悔（这里刻意不新造按钮文字，见类注释里的铁律 1）。
                vertical.HideNoButton();
            }

            ResultsShown++;
            LastResultReport = $"已弹出第 {ResultsShown} 次结果框（交易单 {outcome.OfferId}｜{outcome.Status}）";
            log?.Info("trade", $"[Economy-UI] {LastResultReport}｜{title}");
            foreach (string line in body.Split('\n'))
                log?.Info("trade", "[Economy-UI] " + line.TrimEnd('\r'));
            return true;
        }
        catch (Exception e)
        {
            LastResultReport = "结果弹窗抛异常（已忽略，V 段回执照样在）：" + e.GetType().Name + ": " + e.Message;
            try
            {
                log?.Error("trade", "[Economy-UI] " + LastResultReport, e);
            }
            catch (Exception)
            {
                // 日志都失败就只能放弃。
            }
            return false;
        }
    }

    /// <summary>结果框的标题：一眼看出"成了没有"。</summary>
    public static string ResultTitle(SettlementOutcome outcome) => outcome.Status switch
    {
        SettlementStatus.Applied => "✅ 交易已结算（钱已到账）",
        SettlementStatus.AlreadyApplied => "✅ 这笔之前已经结算过（本次未再动钱）",
        SettlementStatus.NotWired => "⏸ 结算尚未接线（钱没动）",
        SettlementStatus.NotReady => "⏳ 还不能结算（钱没动）",
        SettlementStatus.DirectionNotWired => "⛔ 这个方向还没接线（钱没动）",
        _ => "❌ 结算失败（钱没动）"
    };
}
