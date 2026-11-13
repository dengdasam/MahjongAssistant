"""Rasterise the vendored open-licensed SVG icon to PNG via headless Chromium.

Icon art (third-party, not drawn by this project):
  iconoven/mahjong-tile  •  MIT  •  https://iconoven.com  •  via Iconify
See src/Mahjong.Desktop/assets/icons/NOTICE.md

mahjong-tile-small.svg is the same art with the tile mark filled in; it is used
only for the 16-32px sizes, where a hairline stroke turns to mush.

Run: python tools/rasterize_icon.py && python tools/make_icon.py
"""
import asyncio
import pathlib

from playwright.async_api import async_playwright

ROOT = pathlib.Path(__file__).resolve().parent.parent
ICONS = ROOT / "tools/icons"
PREVIEW = ROOT / "reverse/icons"


async def shoot(svg_path: pathlib.Path, out: pathlib.Path, size: int, color: str) -> None:
    svg = svg_path.read_text(encoding="utf-8")
    svg = svg.replace('width="1em" height="1em"', f'width="{size}" height="{size}"')
    html = (
        "<!doctype html><meta charset='utf-8'>"
        "<style>html,body{margin:0;padding:0;background:transparent}"
        f"svg{{color:{color};display:block;width:{size}px;height:{size}px}}</style>"
        + svg
    )
    out.parent.mkdir(parents=True, exist_ok=True)
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        page = await browser.new_page(viewport={"width": size, "height": size}, device_scale_factor=1)
        await page.set_content(html)
        await page.screenshot(
            path=str(out), omit_background=True,
            clip={"x": 0, "y": 0, "width": size, "height": size})
        await browser.close()
    print(f"{out}  {size}x{size}")


async def main() -> None:
    await shoot(ICONS / "mahjong-tile.svg", PREVIEW / "glyph_1024.png", 1024, "#FBF7EA")
    await shoot(ICONS / "mahjong-tile-small.svg", PREVIEW / "glyph_small_1024.png", 1024, "#FBF7EA")


if __name__ == "__main__":
    asyncio.run(main())
