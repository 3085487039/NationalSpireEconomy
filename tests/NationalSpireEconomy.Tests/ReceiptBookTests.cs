using NationalSpireEconomy.Core;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Tests;

/// <summary>
/// 幂等收据本。这是"同一件事不能做两次"的第一道闸，
/// 所以裁剪规则必须证明：<b>还在等确认的交易，它的防重键永远不会被裁掉。</b>
/// </summary>
public static class ReceiptBookTests
{
    public static Action[] Cases =>
    [
        MarkReturnsTrueOnlyOnce,
        MarkRejectsEmptyKey,
        DirtyTracksNewKeys,
        PrunesByAge,
        ProtectsKeysFromAgePruning,
        PrunesByCapKeepingNewest,
        ProtectedKeysSurviveCapOverflow,
        ProtectedKeysCanExceedCapWithoutLoss,
        RoundTripsThroughConstructor
    ];

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static void MarkReturnsTrueOnlyOnce()
    {
        var book = new ReceiptBook(now: T0);
        TestRunner.True(book.Mark("tick:w:1"), "首次登记应成功");
        TestRunner.False(book.Mark("tick:w:1"), "重复登记应返回 false");
        TestRunner.Equal(1, book.Count);
        TestRunner.True(book.Contains("tick:w:1"));
    }

    private static void MarkRejectsEmptyKey()
    {
        var book = new ReceiptBook(now: T0);
        TestRunner.Throws<ArgumentException>(() => book.Mark(""));
        TestRunner.Throws<ArgumentException>(() => book.Mark("   "));
    }

    private static void DirtyTracksNewKeys()
    {
        var book = new ReceiptBook(now: T0);
        book.Mark("a");
        book.Mark("a");
        book.Mark("b");
        var dirty = book.TakeDirty();
        TestRunner.Equal(2, dirty.Count);
        TestRunner.Equal(0, book.TakeDirty().Count);
    }

    private static void PrunesByAge()
    {
        var book = new ReceiptBook(now: T0);
        book.Mark("old", T0);
        book.Mark("new", T0 + TimeSpan.FromDays(29));

        var removed = book.Prune(now: T0 + TimeSpan.FromDays(31));
        TestRunner.Equal(1, removed.Count);
        TestRunner.Equal("old", removed[0]);
        TestRunner.True(book.Contains("new"), "未过期的收据不应被清掉");
    }

    private static void ProtectsKeysFromAgePruning()
    {
        var book = new ReceiptBook(now: T0);
        book.Mark("trade:pending-1", T0);
        book.Mark("tick:1", T0);

        // 即使两条都过了保鲜期，被引用的那条也必须留下。
        var removed = book.Prune(["trade:pending-1"], now: T0 + TimeSpan.FromDays(90));
        TestRunner.Equal(1, removed.Count);
        TestRunner.Equal("tick:1", removed[0]);
        TestRunner.True(book.Contains("trade:pending-1"), "Pending 交易引用的收据被误删，会导致重复扣款");
    }

    private static void PrunesByCapKeepingNewest()
    {
        var book = new ReceiptBook(maxReceipts: 5, now: T0);
        for (int i = 1; i <= 8; i++) book.Mark("k" + i, T0);

        var removed = book.Prune(now: T0);
        TestRunner.Equal(5, book.Count);
        TestRunner.Equal(3, removed.Count);
        // 最旧的三个先走。
        TestRunner.True(removed.Contains("k1") && removed.Contains("k2") && removed.Contains("k3"),
            "应按登记顺序裁掉最旧的");
        for (int i = 4; i <= 8; i++) TestRunner.True(book.Contains("k" + i), $"k{i} 应被保留");
    }

    private static void ProtectedKeysSurviveCapOverflow()
    {
        var book = new ReceiptBook(maxReceipts: 3, now: T0);
        book.Mark("p1", T0);
        book.Mark("p2", T0);
        book.Mark("free1", T0);
        book.Mark("free2", T0);
        book.Mark("free3", T0);

        var removed = book.Prune(["p1", "p2"], now: T0);
        TestRunner.Equal(3, book.Count);
        TestRunner.True(book.Contains("p1") && book.Contains("p2"), "被引用的收据不应因超限被裁");
        TestRunner.True(removed.Contains("free1"), "应优先裁掉未被引用的最旧项");
    }

    private static void ProtectedKeysCanExceedCapWithoutLoss()
    {
        // 极端情况：受保护的键本身就超过上限。此时防重优先于裁剪，一个都不能丢。
        var book = new ReceiptBook(maxReceipts: 2, now: T0);
        for (int i = 1; i <= 5; i++) book.Mark("p" + i, T0);

        var removed = book.Prune(["p1", "p2", "p3", "p4", "p5"], now: T0);
        TestRunner.Equal(5, book.Count);
        TestRunner.Equal(0, removed.Count);
    }

    private static void RoundTripsThroughConstructor()
    {
        var book = new ReceiptBook(now: T0);
        book.Mark("x");
        book.Mark("y");

        var restored = new ReceiptBook(book.ToList(), now: T0);
        TestRunner.Equal(2, restored.Count);
        TestRunner.True(restored.Contains("x"));
        TestRunner.True(restored.Mark("x") == false, "恢复后应记得 x 已登记");
    }
}
