"""Run the extracted (unmodified) Python logic to produce golden test vectors.

The C# port is validated against these vectors, so behaviour is compared with
the original program rather than guessed from its README.

Usage: python make_golden.py
Output: reverse/golden_vectors.json
"""
from __future__ import annotations

import json
import marshal
import random
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYC = ROOT / "reverse" / "pyc" / "mahjong_probability.pyc"
OUT = ROOT / "reverse" / "golden_vectors.json"


def load_reference():
    code = marshal.loads(PYC.read_bytes()[16:])
    module = types.ModuleType("mahjong_probability")
    module.__dict__["__name__"] = "mahjong_probability"
    exec(code, module.__dict__)
    return module


def build_hand(rng: random.Random, count: int) -> list[int]:
    pool = [t for t in range(34) for _ in range(4)]
    rng.shuffle(pool)
    return sorted(pool[:count])


def case(mp, cid: str, hand: list[int], visible=(), open_melds=0,
         rivers=((), (), ()), opponent_melds=((), (), ())):
    hand = list(hand)
    logical = len(hand) + open_melds * 3
    assert logical in (13, 14), (cid, logical)
    visible = list(visible)

    record = {
        "id": cid,
        "hand": hand,
        "visible": visible,
        "open_melds": open_melds,
        "rivers": [list(r) for r in rivers],
        "opponent_melds": [[list(m) for m in seat] for seat in opponent_melds],
        "expected": {},
    }
    exp = record["expected"]
    exp["shanten"] = mp.shanten(hand, open_melds)
    exp["is_win"] = mp.is_win(hand, open_melds)

    thirteen = hand if logical == 13 else hand[:-1]
    exp["waits"] = [
        {"tile": mp.TILE_NAMES[t], "remaining": v}
        for t, v in mp.waits(thirteen, open_melds).items()
    ]
    exp["improving_tiles"] = [
        {"tile": mp.TILE_NAMES[t], "remaining": v}
        for t, v in mp.improving_tiles(thirteen, visible, open_melds).items()
    ]
    if logical == 14:
        exp["discard_analysis"] = mp.discard_analysis(
            hand, visible, rivers, open_melds, opponent_melds
        )
    return record


def main() -> None:
    mp = load_reference()
    rng = random.Random(20261113)
    cases = []

    named = [
        ("win_standard", [0, 0, 0, 1, 2, 3, 9, 9, 9, 18, 19, 20, 27, 27]),
        ("tenpai_two_sided", [0, 1, 2, 3, 4, 5, 9, 10, 11, 18, 19, 20, 27, 28]),
        ("seven_pairs_tenpai", [0, 0, 2, 2, 4, 4, 9, 9, 11, 11, 27, 27, 29]),
        ("seven_pairs_win", [0, 0, 2, 2, 4, 4, 9, 9, 11, 11, 27, 27, 29, 29]),
        ("kokushi_tenpai", [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33]),
        ("kokushi_win", [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33, 0]),
        ("honors_heavy", [27, 27, 27, 28, 28, 29, 30, 30, 31, 32, 33, 33, 0, 1]),
        ("terminals_heavy", [0, 0, 8, 8, 9, 9, 17, 17, 18, 26, 26, 27, 28, 29]),
        ("one_suit_flush", [0, 0, 1, 1, 2, 2, 3, 4, 5, 6, 7, 8, 8, 8]),
        ("triplet_heavy", [0, 0, 0, 2, 2, 2, 9, 9, 9, 18, 18, 18, 27, 27]),
    ]
    for cid, hand in named:
        cases.append(case(mp, cid, hand))

    # random 14-tile states + 13-tile states
    for i in range(16):
        hand = build_hand(rng, 14)
        cases.append(case(mp, f"random14_{i:02d}", hand))
    for i in range(6):
        hand = build_hand(rng, 13)
        cases.append(case(mp, f"random13_{i:02d}", hand))

    # visible / river / meld variants exercise the risk heuristic
    hand = [0, 0, 0, 1, 2, 3, 9, 9, 9, 18, 19, 20, 27, 27]
    cases.append(case(mp, "visible_river", hand, visible=hand + [0, 9, 27, 31],
                      rivers=[[0, 9, 27], [1, 2, 31], [8, 17, 26]],
                      opponent_melds=[[[9, 9, 9]], [], [[27, 27, 27]]]))
    # exposed meld states: 11 / 8 physical tiles
    cases.append(case(mp, "one_open_meld", [0, 1, 2, 9, 10, 11, 18, 19, 20, 27, 27],
                      open_melds=1))
    cases.append(case(mp, "two_open_melds", [0, 1, 2, 9, 10, 11, 27, 27],
                      open_melds=2))

    # CLI-style snapshot analysis (tile-name input)
    snapshot = {
        "hand": [mp.TILE_NAMES[t] for t in hand],
        "rivers": [["1万", "1筒", "东"], ["2万", "1条"], ["9筒", "白"]],
        "melds": [[], [], []],
        "confidence": {"hand": 0.95},
    }
    snap_result = mp.analyze_snapshot(snapshot)

    payload = {
        "generator": "tools/make_golden.py",
        "source": "MahjongDesktopAssistant.exe / PYZ / mahjong_probability (CPython 3.13)",
        "tile_ids": "0-8 万, 9-17 筒, 18-26 条, 27-33 东南西北中发白, 34-41 花牌",
        "tile_names": mp.TILE_NAMES,
        "cases": cases,
        "snapshot_case": {"input": snapshot, "expected": snap_result},
    }
    OUT.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[done] {len(cases)} cases -> {OUT} ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
