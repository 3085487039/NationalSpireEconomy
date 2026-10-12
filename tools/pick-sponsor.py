#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""挑一个"能过闸"的赞助对象，并给出该要多少钱。

背景（2026-10-11 真机实测）：NPC → 玩家这条路上有两道闸，金额必须同时过：

  (a) 情绪闸（太小会被拒，PERSONALITY_LOW_EMOTION）
      情绪值 = 金额 / max(500, 余额 × 0.10)   必须 ≥ trade.minEmotionToAccept（默认 1.0）
      ⇒ 金额 ≥ max(500, 余额 × 10%)

  (b) 性格上限闸（太大会被拒，PERSONALITY_CAP）
      上限 = 余额 × season.tierCapRatio[档位] × 性格.give × (1 - 0.4 × 性格.thrift)
      再被 trade.maxSingleAmount 夹住
      ⇒ 金额 ≤ 上限

所以存在一个窗口 [max(500, 余额×10%), 上限]；**有些 NPC 这个窗口是空的**（例如 2026-10-11
实测的 p3：余额 6300、尖刻挑刺者 ⇒ 上限 428 但情绪闸要 630 —— 对它任何金额都会被拒）。

**姓名**：游戏界面里看到的是人名，不是 `crowd-5-4` 这种 id。所以本工具会去原版生涯存档
（`…\\SlayTheSpire2\\steam\\<steamid>\\modded\\profileN\\national_spire_career-<hash>.json`，**只读**）
里把 id 映射成 `Name`，直接把"该找谁"告诉你。找不到存档时退化为只显示 id。

用法：
    python tools/pick-sponsor.py                       # 用默认世界与默认路径
    python tools/pick-sponsor.py --top 20
    python tools/pick-sponsor.py --exclude p7,crowd-6-9,p3
    python tools/pick-sponsor.py --npc p3              # 只诊断某个人

依赖：只读 config/、影子存档与原版生涯存档，纯标准库。
"""

import argparse
import glob
import json
import math
import os
import sys

# 已经被聊过、有历史包袱的人（默认排除；可用 --exclude 覆盖）
# 真机记录：p7(苹果)、tour-c64-5(月台上的茶32)、crowd-6-9、p3(双皮奶)、crowd-6-4(茶袋忘拿)、
#           crowd-3-4(莱纳德)、crowd-4-3(面包袋漏了33)
DEFAULT_EXCLUDE = [
    "p7", "tour-c64-5", "crowd-6-9", "p3", "crowd-6-4", "crowd-3-4", "crowd-4-3",
    "tour-c00-5", "tour-c60-5",
]


def load_json(path):
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def load_names(world_id):
    """从原版生涯存档里取 id → 人名。只读，失败就返回空表。"""
    appdata = os.environ.get("APPDATA", "")
    pattern = os.path.join(appdata, "SlayTheSpire2", "steam", "*", "modded", "profile*",
                           "national_spire_career*.json")
    for path in glob.glob(pattern):
        if path.endswith(".bak") or ".pre-life" in path or path.endswith(".selected"):
            continue
        try:
            data = load_json(path)
        except Exception:
            continue
        if (data.get("WorldId") or "") != world_id:
            continue
        people = data.get("People") or []
        return {str(p.get("Id")): p.get("Name") or "" for p in people if p.get("Id")}
    return {}


def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    data_root = os.path.join(
        os.environ.get("APPDATA", ""), "SlayTheSpire2", "national_spire_economy")

    parser = argparse.ArgumentParser(description="挑一个能过双闸的赞助对象并给出金额")
    parser.add_argument("--config", default=os.path.join(root, "config"))
    parser.add_argument("--worlds", default=os.path.join(data_root, "worlds"))
    parser.add_argument("--world", default="e35d0119", help="WorldId（存档文件名，不含 .json）")
    parser.add_argument("--top", type=int, default=12)
    parser.add_argument("--exclude", default=",".join(DEFAULT_EXCLUDE),
                        help="逗号分隔的 PersonId，排除已聊过的人（默认已排除几个）")
    parser.add_argument("--npc", default="", help="只诊断这个人（PersonId）")
    args = parser.parse_args()

    economy = load_json(os.path.join(args.config, "economy_config.json"))
    personality = load_json(os.path.join(args.config, "personality_economy.json"))
    world_path = os.path.join(args.worlds, args.world + ".json")
    if not os.path.isfile(world_path):
        print("找不到影子存档：%s" % world_path, file=sys.stderr)
        return 2
    world = load_json(world_path)

    cap_ratio = economy["season"]["tierCapRatio"]
    max_single = economy["trade"]["maxSingleAmount"]
    min_emotion = economy["trade"].get("minEmotionToAccept", 1.0)
    kinds = personality["kinds"]
    defaults = personality["defaults"]
    emotion_scale = 0.10
    emotion_floor = 500

    def profile(kind):
        return kinds.get(kind, defaults)

    def gates(balance, tier, kind):
        prof = profile(kind)
        cap = math.floor(balance * cap_ratio.get(tier, cap_ratio["default"])
                         * prof["give"] * (1 - 0.4 * prof["thrift"]))
        cap = max(0, min(cap, max_single))
        need = math.ceil(max(emotion_floor, balance * emotion_scale * min_emotion))
        return cap, need

    def recommend(need):
        """取窗口下沿，向上取整到 50：要得越少，模型越容易答应。"""
        return int(math.ceil(need / 50.0) * 50)

    if args.npc:
        npc = world["Npcs"].get(args.npc)
        if not npc:
            print("账本里没有 %s" % args.npc, file=sys.stderr)
            return 2
        names = load_names(args.world)
        cap, need = gates(npc["Balance"], npc.get("TierId", "default"),
                          npc.get("PersonalityKind", ""))
        print("%s（%s）：余额 $%d｜档位 %s｜性格 %s" % (
            args.npc, names.get(args.npc) or "姓名未知", npc["Balance"],
            npc.get("TierId"), npc.get("PersonalityKind")))
        print("  上限（PERSONALITY_CAP）= $%d" % cap)
        print("  情绪闸要 ≥ $%d" % need)
        print("  窗口：%s" % ("$%d ~ $%d，建议要 $%d" % (need, cap, recommend(need))
                              if cap >= need else "空 —— 对它任何金额都会被拒，换人"))
        return 0

    exclude = {p.strip() for p in args.exclude.split(",") if p.strip()}
    names = load_names(args.world)
    rows = []
    for pid, npc in world["Npcs"].items():
        cap, need = gates(npc["Balance"], npc.get("TierId", "default"),
                          npc.get("PersonalityKind", ""))
        rec = recommend(need)
        # 舒服的条件：窗口非空、且上限留出一倍余量（模型更容易答应，也不怕估算误差）
        if cap >= rec and cap >= 2 * need and pid not in exclude:
            rows.append((rec, pid, npc["Balance"], npc.get("PersonalityKind", ""), cap, need))

    rows.sort(key=lambda row: (row[0], -row[4]))
    print("世界 %s：%d 人，其中「能过双闸且上限留一倍余量」的 %d 人（已排除 %s）"
          % (args.world, len(world["Npcs"]), len(rows), ",".join(sorted(exclude)) or "无"))
    if not names:
        print("（没读到原版生涯存档，只显示 id —— 请在游戏里按 id 找人对不上时用 --npc 单独诊断）")
    print()
    print("%-14s %-16s %8s %-12s %9s %8s" % ("人名", "id", "余额", "性格", "上限cap", "建议要"))
    for rec, pid, balance, kind, cap, need in rows[:args.top]:
        print("%-14s %-16s %8d %-12s %9d %8s"
              % (names.get(pid) or "?", pid, balance, kind, cap, "$" + str(rec)))
    print()
    if rows:
        print("用法：在游戏里打开与『%s』的私信，说「赞助我 %s 块交房租」。"
              % (names.get(rows[0][1]) or rows[0][1], rows[0][0]))
    print("注意：被拒（PERSONALITY_CAP / PERSONALITY_LOW_EMOTION）会写一次 LastRefusedCharityDay，"
          "同一个人 7 天内更冷 —— 换人比反复磨有效。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
