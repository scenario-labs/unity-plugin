"""Builds the README's animated images from the frames ScenarioDev.Media.Run renders in Unity.

Input: Library/ScenarioDevLogs/media/<name>/0000.png... and info.json (one folder per result).
Output: docs/images/*.gif, each a grid of labelled tiles. Needs ffmpeg and ImageMagick (the labels:
some ffmpeg builds have no text filter).

    python3 tools/build_media.py
"""
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FRAMES = os.path.join(ROOT, "Library", "ScenarioDevLogs", "media")
OUT = os.path.join(ROOT, "docs", "images")
FONT = "/System/Library/Fonts/SFNS.ttf"
FPS = 12

# name: (tiles as (folder, label), columns, tile px, seconds)
GRIDS = {
    "3d-operations": ([
        ("chest", "Generated · Rodin Gen-2.5"),
        ("chest-retexture", "Retexture · Tripo 3.0"),
        ("chest-texture-edit", "Texture edit · Tencent"),
        ("chest-prompt-edit", "Prompt edit · Claude Opus 5.5 3D"),
        ("chest-lego", "Stylize LEGO · Tripo"),
        ("chest-voxel", "Stylize voxels · Tripo"),
        ("chest-parts", "Split into parts · Hunyuan 3D Part"),
        ("chest-remesh", "Remesh to quads · Meshy"),
    ], 4, 240, 4),
    "generate-3d": ([
        ("fox", "Meshy 7.1"),
        ("knight", "Meshy 7.1"),
        ("armor", "Texture edit · Tencent"),
        ("tree-voxels", "Voxel Crafter"),
    ], 4, 280, 4),
    "animate": ([
        ("rig-meshy-walk", "Rig + walk · Meshy"),
        ("rig-tripo-walk", "Rig + walk · Tripo"),
        ("motion-attack", "Motion library · Meshy"),
        ("motion-meshy", "Text to motion · Meshy"),
    ], 4, 280, 3),
    "run-jump": ([
        ("run-jump-uthana", "Run, jump forward, jump · Uthana"),
    ], 1, 440, 8),
}


def label_png(text, size, folder, index):
    """A transparent tile-sized PNG with the label at the bottom, over a soft shadow."""
    path = os.path.join(folder, f"label-{index}.png")
    point = max(13, size // 19)
    subprocess.run(["magick", "-size", f"{size}x{size}", "xc:none", "-font", FONT, "-pointsize", str(point),
                    "-gravity", "south", "-fill", "#00000099", "-annotate", f"+1+{size // 22 - 1}", text,
                    "-fill", "#ffffffeb", "-annotate", f"+0+{size // 22}", text, path], check=True)
    return path


def build(name, tiles, columns, size, seconds, temp):
    inputs, filters = [], []
    n = len(tiles)
    for i, (folder, label) in enumerate(tiles):
        info = json.load(open(os.path.join(FRAMES, folder, "info.json")))
        inputs += ["-stream_loop", "-1", "-framerate", str(info["fps"]), "-i", os.path.join(FRAMES, folder, "%04d.png")]
    for i, (folder, label) in enumerate(tiles):
        inputs += ["-loop", "1", "-i", label_png(label, size, temp, i)]
    for i in range(n):
        # Each tile loops its own clip; a turntable is one turn in the grid's length.
        filters.append(
            f"[{i}:v]trim=duration={seconds},setpts=PTS-STARTPTS,fps={FPS},scale={size}:{size}:flags=lanczos[v{i}];"
            f"[v{i}][{n + i}:v]overlay=0:0:shortest=1[t{i}]")
    rows = (len(tiles) + columns - 1) // columns
    if len(tiles) == 1:
        filters.append("[t0]copy[grid]")
    else:
        layout = "|".join(f"{(i % columns) * size}_{(i // columns) * size}" for i in range(len(tiles)))
        filters.append("".join(f"[t{i}]" for i in range(len(tiles))) + f"xstack=inputs={len(tiles)}:layout={layout}:fill=0x2a2c31[grid]")
    filters.append("[grid]split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle")
    out = os.path.join(OUT, name + ".gif")
    cmd = ["ffmpeg", "-y", "-loglevel", "error"] + inputs + ["-filter_complex", ";".join(filters), "-t", str(seconds), "-loop", "0", out]
    subprocess.run(cmd, check=True)
    print(f"{out}: {columns * size if len(tiles) > 1 else size}x{rows * size}, {os.path.getsize(out) / 1e6:.1f} MB")


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    only = sys.argv[1:]
    with tempfile.TemporaryDirectory() as temp:
        for name, spec in GRIDS.items():
            if not only or name in only:
                build(name, *spec, temp)
