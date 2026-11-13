"""Extract bundled Python 3.13 modules from the PyInstaller one-dir build.

Usage:
    python extract_pyc.py [path/to/MahjongDesktopAssistant.exe]

Outputs (under ./reverse):
    pyc/*.pyc            loadable .pyc files (useful for decompilers / exec-as-reference)
    dis/*.dis.txt        full CPython 3.13 bytecode disassembly
    meta.json            module imports, function signatures, constants

This script is read-only with respect to the original software.
"""
from __future__ import annotations

import dis
import importlib.util
import json
import marshal
import struct
import sys
import types
import zlib
from pathlib import Path

EXE = (
    Path(sys.argv[1])
    if len(sys.argv) > 1
    else Path(r"D:\Software\麻将概率助手电脑版\MahjongDesktopAssistant.exe")
)
ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "reverse"
PYZ_MODULES = (
    "mahjong_probability",
    "training_journal",
    "wall_counter",
    "yolo_screen_detect",
)

COOKIE_MAGIC = b"MEI\x0c\x0b\x0a\x0b\x0e"


def read_carchive(path: Path) -> tuple[list[tuple[str, str, bytes]], bytes]:
    data = path.read_bytes()
    pos = data.rfind(COOKIE_MAGIC)
    if pos < 0:
        raise RuntimeError("PyInstaller cookie not found")
    _, pkg_len, toc_off, toc_len, pyver, pylib = struct.unpack(
        "!8sIIII64s", data[pos : pos + 88]
    )
    print(f"[archive] python={pyver} pylib={pylib.rstrip(bytes(1)).decode()}")
    base = pos + 88 - pkg_len
    toc = data[base + toc_off : base + toc_off + toc_len]

    entries: list[tuple[str, str, bytes]] = []
    pyz = b""
    i = 0
    while i < len(toc):
        (entry_len,) = struct.unpack("!I", toc[i : i + 4])
        off, clen, ulen, flag, typ = struct.unpack("!IIIBc", toc[i + 4 : i + 18])
        name = toc[i + 18 : i + entry_len].rstrip(b"\x00").decode("utf-8", "replace")
        raw = data[base + off : base + off + clen]
        if flag:
            raw = zlib.decompress(raw)
        if name == "PYZ.pyz":
            pyz = raw
        entries.append((typ.decode(), name, raw))
        i += entry_len
    return entries, pyz


def pyz_modules(pyz: bytes) -> dict[str, tuple[int, int, int]]:
    toc_pos = struct.unpack("!I", pyz[8:12])[0]
    toc = marshal.loads(pyz[toc_pos:])
    return dict(toc.items() if isinstance(toc, dict) else toc)


def decode_module(pyz: bytes, entry: tuple[int, int, int]) -> types.CodeType:
    _, off, length = entry
    return marshal.loads(zlib.decompress(pyz[off : off + length]))


def write_pyc(dest: Path, code: types.CodeType) -> None:
    with dest.open("wb") as f:
        f.write(importlib.util.MAGIC_NUMBER)
        f.write(struct.pack("<III", 0, 0, 0))  # flags, timestamp, source size
        marshal.dump(code, f)


def walk(code: types.CodeType):
    yield code
    for const in code.co_consts:
        if isinstance(const, types.CodeType):
            yield from walk(const)


def describe(code: types.CodeType) -> dict:
    functions = []
    imports = []
    constants: list[str] = []
    seen_const = set()
    for c in walk(code):
        argc = c.co_argcount + c.co_kwonlyargcount
        functions.append(
            {
                "name": c.co_name,
                "line": c.co_firstlineno,
                "args": list(c.co_varnames[:argc]),
                "locals": list(c.co_varnames),
                "names": list(c.co_names),
            }
        )
        for ins in dis.get_instructions(c):
            if ins.opname in ("IMPORT_NAME", "IMPORT_FROM"):
                imports.append(f"{ins.opname}:{ins.argval}")
        for const in c.co_consts:
            if isinstance(const, types.CodeType) or const is None:
                continue
            if isinstance(const, (str, int, float, tuple, frozenset, bool)):
                text = repr(const)
                if text not in seen_const and len(text) < 300:
                    seen_const.add(text)
                    constants.append(text)
    return {
        "max_line": max((c.co_firstlineno for c in walk(code)), default=0),
        "imports": sorted(set(imports)),
        "functions": functions,
        "constants": constants,
    }


def main() -> None:
    for sub in ("pyc", "dis"):
        (OUT / sub).mkdir(parents=True, exist_ok=True)

    entries, pyz = read_carchive(EXE)
    mods = pyz_modules(pyz)
    scripts = {name: raw for typ, name, raw in entries if typ == "s"}

    targets: dict[str, types.CodeType] = {}
    for name in PYZ_MODULES:
        targets[name] = decode_module(pyz, mods[name])
    for name, raw in scripts.items():
        if name.startswith("pyi_") or name.startswith("pyiboot"):
            continue
        try:
            targets[name] = marshal.loads(raw)
        except Exception as exc:  # pragma: no cover - defensive
            print(f"[skip] {name}: {exc}")

    meta = {}
    for name, code in targets.items():
        write_pyc(OUT / "pyc" / f"{name}.pyc", code)
        with (OUT / "dis" / f"{name}.dis.txt").open("w", encoding="utf-8") as f:
            f.write(f"# {name} | CPython 3.13 bytecode\n")
            dis.dis(code, file=f, depth=None, show_caches=False)
        meta[name] = describe(code)
        print(
            f"[ok] {name:<24} ~{meta[name]['max_line']:>4} lines, "
            f"{len(meta[name]['functions']):>3} code objects, "
            f"{len(meta[name]['constants']):>3} constants"
        )

    (OUT / "meta.json").write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"[done] {OUT}")


if __name__ == "__main__":
    main()
