#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#     "pillow>=10.0.0",
# ]
# ///
"""Generate a multi-size .ico (and optional 256x256 .png) icon for Percolator.

Design: Lime green rounded rectangle with a white node-graph "P".
Usage:  uv run generate_icon.py [output_path] [--no-png]
"""

import argparse
from pathlib import Path
from PIL import Image, ImageDraw

RGBA = tuple[int, int, int, int]

# High-resolution supersampled canvas for crisp anti-aliasing
CANVAS_SIZE = 1024

# Standard Windows / Avalonia multi-frame icon sizes
ICO_SIZES: list[tuple[int, int]] = [
    (16, 16),
    (24, 24),
    (32, 32),
    (48, 48),
    (64, 64),
    (128, 128),
    (256, 256),
]

# Palette
LIME_TOP: RGBA = (132, 232, 42, 255)      # #84E82A
LIME_BOTTOM: RGBA = (76, 186, 20, 255)    # #4CBA14
BORDER_COLOR: RGBA = (58, 148, 14, 255)   # #3A940E (keeps contrast on light taskbars)
WHITE: RGBA = (255, 255, 255, 255)


def make_vertical_gradient(size: int, top: RGBA, bottom: RGBA) -> Image.Image:
    """Create a vertical RGBA gradient via a 1xN strip stretched horizontally."""
    strip = Image.new("RGBA", (1, size))
    strip.putdata([
        tuple(int(t + (b - t) * (y / (size - 1))) for t, b in zip(top, bottom))
        for y in range(size)
    ])
    return strip.resize((size, size), Image.Resampling.NEAREST)


def render_icon(size: int = CANVAS_SIZE) -> Image.Image:
    """Render the Percolator icon on a transparent RGBA canvas."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # 1. Rounded rectangle mask + lime green gradient fill
    margin = int(size * 0.04)
    radius = int(size * 0.22)
    bounds = (margin, margin, size - margin, size - margin)

    mask = Image.new("L", (size, size), 0)
    mask_draw = ImageDraw.Draw(mask)
    mask_draw.rounded_rectangle(bounds, radius=radius, fill=255)

    gradient = make_vertical_gradient(size, LIME_TOP, LIME_BOTTOM)
    img.paste(gradient, (0, 0), mask)

    # Subtle outer border so the icon stays defined on light backgrounds
    draw = ImageDraw.Draw(img)
    border_width = max(2, int(size * 0.02))
    draw.rounded_rectangle(bounds, radius=radius, outline=BORDER_COLOR, width=border_width)

    # 2. White node diagram forming the letter "P"
    # Normalized coordinates (x, y) in [0, 1]
    nodes_norm: dict[str, tuple[float, float]] = {
        "stem_bottom": (0.32, 0.78),
        "stem_mid":    (0.32, 0.54),
        "stem_top":    (0.32, 0.24),
        "bowl_top":    (0.58, 0.24),
        "bowl_apex":   (0.71, 0.39),
        "bowl_bottom": (0.58, 0.54),
    }

    edges: list[tuple[str, str]] = [
        ("stem_bottom", "stem_mid"),
        ("stem_mid", "stem_top"),
        ("stem_top", "bowl_top"),
        ("bowl_top", "bowl_apex"),
        ("bowl_apex", "bowl_bottom"),
        ("bowl_bottom", "stem_mid"),
    ]

    nodes = {k: (x * size, y * size) for k, (x, y) in nodes_norm.items()}

    # Draw edges first
    edge_width = max(2, int(size * 0.055))
    for a, b in edges:
        draw.line([nodes[a], nodes[b]], fill=WHITE, width=edge_width)

    # Draw nodes on top (circles larger than the edges so the graph reads clearly)
    node_radius = size * 0.068
    for x, y in nodes.values():
        bbox = (x - node_radius, y - node_radius, x + node_radius, y + node_radius)
        draw.ellipse(bbox, fill=WHITE)

    return img


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate a multi-size .ico (and optional 256x256 .png) for Percolator."
    )
    parser.add_argument(
        "output",
        nargs="?",
        type=Path,
        default=Path("app.ico"),
        help="Output .ico file path (default: app.ico)",
    )
    parser.add_argument(
        "--no-png",
        action="store_true",
        help="Skip generating the companion 256x256 .png file",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    out_ico: Path = args.output
    out_ico.parent.mkdir(parents=True, exist_ok=True)

    master = render_icon(CANVAS_SIZE)
    master.save(out_ico, format="ICO", sizes=ICO_SIZES)

    if args.no_png:
        print(f"Generated {out_ico} ({len(ICO_SIZES)} sizes)")
    else:
        out_png = out_ico.with_suffix(".png")
        master.resize((256, 256), Image.Resampling.LANCZOS).save(out_png, format="PNG")
        print(f"Generated {out_ico} ({len(ICO_SIZES)} sizes) and {out_png}")


if __name__ == "__main__":
    main()