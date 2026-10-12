using NationalSpire;
using NationalSpireEconomy.Economy;

namespace NationalSpireEconomy.Game;

/// <summary>
/// 把原版 <see cref="CareerPerson"/> 投影成 <see cref="INpcFacts"/>。
/// <para>
/// <b>这是整个适配层唯一的"翻译点"</b>：<c>Economy/</c> 里的档位判定与播种只认
/// <see cref="INpcFacts"/>，永远看不到原版类型。这样经济逻辑可以在没有游戏的情况下单测，
/// 而原版升级时只需要改这一个文件。
/// </para>
/// <para>
/// <b>刻意不做缓存</b>：<see cref="CareerPerson"/> 的字段会被 AI 与赛事结算随时改写
/// （尤其是 <c>Personality.Kind</c> 被 <c>[Profile: ...]</c> 改），缓存一份就等于埋一个
/// "性格明明变了但档位没重算"的 bug。投影是纯字段搬运，成本可忽略。
/// </para>
/// </summary>
public static class NpcFactsProjection
{
    /// <summary>
    /// 投影一个人。<paramref name="person"/> 为 null 时返回 null——
    /// 调用方（例如遍历 <c>DeletedPeople</c>）可以安全地跳过。
    /// </summary>
    public static NpcFacts? Project(CareerPerson? person)
    {
        if (person == null) return null;

        return new NpcFacts(
            person.Id ?? "",
            person.Role ?? "",
            Rating: person.Rating,
            MaxAscension: person.MaxAscension,
            ClubId: person.ClubId ?? "",
            PersonalityKind: KindOf(person),
            PersonalityVersion: VersionOf(person),
            Identities: person.Identities ?? [],
            // 2026-10-12：把人名也带上。它是确认弹窗里"对手方：酸橙汽水"的来源——
            // 少了它，弹窗只能显示 pro2_5 这种内部 id，看着像调试输出。
            Name: NameOf(person));
    }

    /// <summary>
    /// 这个人的显示名（原版 <c>CareerPerson.Name</c>）。
    /// 没名字时返回空串，展示端负责回退到 id —— 绝不在这里编一个名字出来。
    /// </summary>
    public static string NameOf(CareerPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return person.Name ?? "";
    }

    /// <summary>批量投影，自动跳过 null 与同 id 的重复项（保留第一个）。</summary>
    public static List<NpcFacts> ProjectAll(IEnumerable<CareerPerson?> people)
    {
        ArgumentNullException.ThrowIfNull(people);

        var result = new List<NpcFacts>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var person in people)
        {
            var facts = Project(person);
            if (facts == null) continue;
            if (facts.Id.Length == 0) continue;          // 没有 id 的人不进我们的账本
            if (!seen.Add(facts.Id)) continue;
            result.Add(facts);
        }
        return result;
    }

    /// <summary>
    /// 性格名。原版把 <c>Kind</c> 存在 <c>PersonalityProfile</c> 里；
    /// 还没跑过 <c>PersonalityLibrary</c> 升级的人这里是空串，
    /// 下游会回退到 <c>defaults</c> 画像，属于预期行为而不是错误。
    /// </summary>
    public static string KindOf(CareerPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return person.Personality?.Kind ?? "";
    }

    /// <summary>性格版本号。用于侦测原版性格被改写后需要重算画像。</summary>
    public static int VersionOf(CareerPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return person.Personality?.Version ?? 0;
    }

    /// <summary>
    /// 这个人是不是"人类玩家"（多人模式下可能有多个）。
    /// 原版用 <c>CareerData.HumanIds</c> 标记真人；真人<b>不进我们的账本</b>——
    /// 他们的钱在 <c>CareerData.Credits</c> 里，由 <see cref="PlayerWallet"/> 管。
    /// </summary>
    public static bool IsHuman(CareerData data, string personId)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrEmpty(personId)) return false;
        if (string.Equals(personId, data.LocalHumanId, StringComparison.Ordinal)) return true;
        return data.HumanIds?.Contains(personId, StringComparer.Ordinal) == true;
    }

    /// <summary>
    /// 投影"所有该进账本的人"：跳过真人、跳过没有 id 的人。
    /// 这是播种与工资结算的输入。
    /// </summary>
    public static List<NpcFacts> ProjectLedgerPeople(CareerData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var people = (data.People ?? [])
            .Where(p => p != null && !IsHuman(data, p.Id ?? ""));
        return ProjectAll(people);
    }

    /// <summary>
    /// 投影已经退役/被删的人。原版把它们移到 <c>DeletedPeople</c>；
    /// 我们的账本保留余额，所以也要能看到他们。
    /// </summary>
    public static List<NpcFacts> ProjectDeletedPeople(CareerData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var people = (data.DeletedPeople?.Values ?? Enumerable.Empty<CareerPerson>())
            .Where(p => p != null && !IsHuman(data, p.Id ?? ""));
        return ProjectAll(people);
    }
}
