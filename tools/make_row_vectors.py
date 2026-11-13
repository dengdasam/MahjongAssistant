"""Generate golden vectors for the original `choose_hand` heuristic.

The function is pulled straight out of desktop_assistant.pyc and driven with
synthesised detections (hand row, opponent rivers, stray edge tiles), so the C#
port can be diffed against the real implementation instead of an assumption.
"""

import builtins
import json
import marshal
import random
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CODE = marshal.loads((ROOT / "reverse" / "pyc" / "desktop_assistant.pyc").read_bytes()[16:])


def find(name):
    found = None

    def walk(code):
        nonlocal found
        for const in code.co_consts:
            if isinstance(const, types.CodeType):
                if const.co_name == name:
                    found = const
                walk(const)

    walk(CODE)
    return found


choose_hand = types.FunctionType(find("choose_hand"), {"__builtins__": builtins}, "choose_hand")


def det(x, y, w, h, conf):
    return {"bbox": [x, y, w, h], "confidence": round(conf, 3)}


def make_case(rng):
    width = rng.choice([960, 1280, 1440, 1600, 1920])
    height = rng.choice([600, 720, 810, 900, 1080])
    items = []

    # Hand along the bottom edge: 13 or 14 tiles, occasionally split by a gap.
    count = rng.choice([0, 3, 13, 14, 14, 14])
    tile_w = rng.choice([42, 46, 52, 58])
    tile_h = int(tile_w * rng.uniform(1.25, 1.45))
    gap = rng.choice([2, 4, 6])
    y = height - tile_h - rng.choice([8, 20, 40])
    x = rng.randint(40, max(41, width // 4))
    split_at = rng.randint(3, max(4, count - 2)) if count >= 8 and rng.random() < 0.35 else -1
    for i in range(count):
        if i == split_at:
            x += tile_w * 2  # force a new segment
        items.append(det(x, y, tile_w, tile_h, rng.uniform(0.28, 0.95)))
        x += tile_w + gap

    # Opponent rivers: rows in the middle of the table.
    for _ in range(rng.randint(0, 3)):
        row_y = rng.randint(int(height * 0.22), int(height * 0.62))
        row_x = rng.randint(int(width * 0.15), int(width * 0.45))
        for i in range(rng.randint(1, 8)):
            items.append(det(row_x + i * (tile_w + gap), row_y, tile_w, tile_h, rng.uniform(0.26, 0.9)))

    # Side melds / stray tiles hugging the left and right edges.
    for _ in range(rng.randint(0, 4)):
        side_y = rng.randint(int(height * 0.3), int(height * 0.8))
        if rng.random() < 0.5:
            items.append(det(rng.randint(0, 20), side_y, tile_w, tile_h, rng.uniform(0.3, 0.85)))
        else:
            items.append(det(width - tile_w - rng.randint(0, 20), side_y, tile_w, tile_h, rng.uniform(0.3, 0.85)))

    # A few far-flung detections to exercise the edge filters.
    for _ in range(rng.randint(0, 3)):
        items.append(det(rng.randint(0, width - tile_w), rng.randint(0, height - tile_h),
                         tile_w, tile_h, rng.uniform(0.25, 0.6)))

    rng.shuffle(items)
    edge, row, counts = choose_hand(items, width, height)
    return {
        "width": width,
        "height": height,
        "detections": items,
        "expected": {
            "edge": edge,
            "row": [d["bbox"] for d in row],
            "counts": counts,
        },
    }


def main():
    rng = random.Random(20261113)
    cases = [make_case(rng) for _ in range(120)]
    out = ROOT / "reverse" / "hand_row_vectors.json"
    out.write_text(json.dumps({"cases": cases}, ensure_ascii=False, indent=1), encoding="utf-8")
    sizes = [len(c["expected"]["row"]) for c in cases]
    edges = {}
    for c in cases:
        edges[c["expected"]["edge"]] = edges.get(c["expected"]["edge"], 0) + 1
    print(f"wrote {out} cases={len(cases)} row_min={min(sizes)} row_max={max(sizes)} edges={edges}")


if __name__ == "__main__":
    main()
