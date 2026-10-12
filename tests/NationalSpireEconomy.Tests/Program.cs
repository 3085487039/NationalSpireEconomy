using NationalSpireEconomy.Tests;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// GameRefsEnabled=true 时，测试会构造原版的 CareerPerson / CareerData，
// 于是运行期必须能解析 sts2 / NationalSpire / 0Harmony。
// 这些 DLL 是 <Private>false</Private>，不会复制到测试输出目录，所以在这里按需加载。
// 找不到时静默降级（游戏没装、NSE_GAME_DIR 未设置……），而不是崩掉整套测试。
#if !NSE_NO_GAME
NseProbe.Register();
#endif

Console.WriteLine("NationalSpireEconomy 单元测试");
Console.WriteLine("第 1 步：MoneyUnits + Core/（配置、哈希、日志、模板渲染）");
Console.WriteLine("第 2 步：影子存档（数据模型、原子写、备份、WorldId 隔离、幂等收据）");
Console.WriteLine("第 3 步：档位判定、性格经济画像、初始资金播种");
Console.WriteLine("第 5 步：交易引擎（协议解析、八条校验器、两段式执行）");
Console.WriteLine("第 6 步：Prompt 注入、意向演进、审计");
Console.WriteLine("阶段 3：S 段注入接线 + 影子存档日期推进（tick）");
Console.WriteLine("阶段 3-B：V 段注入（易变段：余额 / 本季收支 / 最近一笔流水 / 未竟之言）");

TestRunner.Run(
    ("MoneyUnits", MoneyUnitsTests.Cases),
    ("DeterministicHash", DeterministicHashTests.Cases),
    ("PromptConfig.Render", PromptRenderTests.Cases),
    ("ConfigLoader（跑真实 config/ 目录）", ConfigLoaderTests.Cases),
    ("ReceiptBook（幂等防重）", ReceiptBookTests.Cases),
    ("EconomyStore（影子存档）", EconomyStoreTests.Cases),
    ("EconomyTier（档位判定）", EconomyTierTests.Cases),
    ("PersonalityEconomy（性格经济画像）", PersonalityEconomyTests.Cases),
    ("EconomySeed（资金播种）", EconomySeedTests.Cases),
    ("EconomyLedger/Tick（记账与结算）", EconomyLedgerTests.Cases),
    ("TradeProtocol（[Trade:]/[Buy:] 解析与剔除）", TradeProtocolTests.Cases),
    ("TradeValidators（八条校验器）", TradeValidatorTests.Cases),
    ("TradeEngine（两段式执行：Reserve/Commit）", TradeEngineTests.Cases),
    ("PromptInjector（S/V 段注入与缓存守卫）", PromptInjectorTests.Cases),
    ("IntentEvolve（意向演进）", IntentEvolveTests.Cases),
    ("EconomyAudit（审计追加写）", EconomyAuditTests.Cases),
    ("SystemPromptInsert（S 段插入位置、幂等、只注入 S 段）", StableInsertionTests.Cases),
    ("CurrentSegmentInsert（V 段落点、幂等、两段数字隔离）", VolatileInsertionTests.Cases),
    ("阶段 4：只读成交预览（不动钱）", TradePreviewTests.Cases),
    ("阶段 4：双人确认闸门（幂等、不动钱）", TradeConfirmationTests.Cases),
    ("阶段 4：交易意图补全（方向 / 双方 / 回声）", TradeIntentCompleterTests.Cases),
    ("阶段 4 第二步 §7.1：影子玩家钱包（幂等 / 导入水位线）", ShadowPlayerWalletTests.Cases),
    ("阶段 4：玩家私信指令解析（原版壳 / 只认行首）", PlayerTradeCommandTests.Cases),
    ("阶段 4：转账声明检测（嘴上说转了 / 账上没动）", TransferClaimDetectorTests.Cases),
    ("阶段 4 第二步 §7.2/§7.3：结算 + 强制落盘 + 审计", TradeSettlementTests.Cases),
    ("阶段 4 第三步：账本真相通道（回执 / 未成立 / 落点）", TradeNoteTextTests.Cases)
#if !NSE_NO_GAME
    ,
    // ⚠ 顺序有讲究：TradeMarkerInterceptor 的流式用例断言"一次 Feed 恰好一条日志"，
    // 而 Feed 会在模组已初始化的前提下继续调用 TradePreviewWiring.HandleModelIntent（多出日志行）。
    // 它必须排在 ModEntry / PlanAInjection 这些**会建立 Runtime** 的套件之前，
    // 否则它测的就不是"抽取器"，而是"上一个套件留下的全局状态"。
    ("TradeMarkerInterceptor（[Trade:] 流式抽取）", TradeMarkerInterceptorTests.Cases),
    ("ModEntry（模组入口与配置门禁）", ModEntryTests.Cases),
    ("NpcFactsProjection（原版人→账本事实投影）", NpcFactsProjectionTests.Cases),
    ("PlayerWallet（Credits+CreditFraction 接线）", PlayerWalletTests.Cases),
    ("InjectWiring（S 段注入 + 影子存档日期推进）", InjectWiringTests.Cases),
    ("PlanAInjection（方案 A：RequestPrivateAsync 末端注入）", PlanAInjectionTests.Cases),
    ("PathProbe（旁路取证：私信链路谁被执行、谁装了补丁）", PathProbeTests.Cases)
#endif
    );

return TestRunner.ExitCode;
