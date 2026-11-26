"""Capture and encode NTFS Tracer's English dark-mode README demo on Windows."""

import argparse
import gzip
from pathlib import Path
import shutil
import subprocess
from tempfile import TemporaryDirectory

from PIL import Image, ImageDraw


DIRECTORY = Path(__file__).resolve().parent
RING = (60, 164, 243)
POINTER = ((0, 0), (0, 20), (5, 15), (9, 23), (13, 21), (9, 14), (16, 14))
SCENES = (
    ("00-all", None, 750),
    ("re", "search", 140),
    ("repo", None, 170),
    ("report", None, 410),
    ("01-search", "result", 850),
    ("02-history", "history", 1050),
    ("old-name", "old", 750),
    ("02-history", "new", 820),
    ("03-hex", "hex", 1800),
    ("08-return", "back", 630),
    ("04-transactions", "transactions", 720),
    ("05-timestamps", "txrow", 1950),
    ("06-leads", "leads", 650),
    ("07-lead-evidence", "leadrow", 1800),
)


def encode(captures, output):
    points = {name: (int(x), int(y)) for name, x, y in
              (line.split() for line in (captures / "clicks.txt").read_text().splitlines())}
    screens = {}
    for name, _, _ in SCENES:
        if name not in screens:
            with Image.open(captures / f"{name}.png") as original:
                screens[name] = original.convert("RGB")
    width, height = screens[SCENES[0][0]].size
    if any(screen.size != (width, height) for screen in screens.values()):
        raise ValueError("Capture size changed during the demo.")
    position = (width - 40, 60)
    frames, durations, clicks = [], [], {}
    samples = Image.new("RGB", (width // 4 * 4, height // 4 * 4))
    for index, screen in enumerate(screens.values()):
        thumb = screen.resize((width // 4, height // 4), Image.Resampling.NEAREST)
        samples.paste(thumb, (index % 4 * thumb.width, index // 4 * thumb.height))
    palette = Image.new("P", (1, 1))
    palette.putpalette(samples.quantize(colors=253).getpalette()[:759] + [*RING, 255, 255, 255, 24, 28, 34])

    def frame(screen, duration, ring=None):
        rendered = screen.copy()
        x, y = position
        draw = ImageDraw.Draw(rendered)
        if ring:
            draw.ellipse((x - ring, y - ring, x + ring, y + ring), outline=RING, width=3)
        draw.polygon([(x + dx, y + dy) for dx, dy in POINTER], fill="white", outline=(24, 28, 34), width=2)
        frames.append(rendered.quantize(palette=palette, dither=Image.Dither.NONE))
        durations.append(duration)

    previous = None
    for name, target, hold in SCENES:
        screen = screens[name]
        if target:
            end = points[target]
            if not (0 <= end[0] < width and 0 <= end[1] < height):
                raise ValueError(f"Click is outside the capture: {target}")
            start = position
            for step in range(1, 6):
                t = step / 5
                t = t * t * (3 - 2 * t)
                position = tuple(round(a + (b - a) * t) for a, b in zip(start, end))
                frame(previous, 40)
            clicks[sum(durations) + 20] = end
            frame(previous, 90, ring=9)
            frame(screen, 80, ring=14)
        frame(screen, hold)
        previous = screen

    output.mkdir(parents=True, exist_ok=True)
    gif = output / "full-app-walkthrough-dark.gif"
    frames[0].save(gif, save_all=True, append_images=frames[1:], duration=durations, loop=0, optimize=True)
    with Image.open(gif) as demo:
        elapsed = 0
        for index in range(demo.n_frames):
            demo.seek(index)
            end = elapsed + demo.info["duration"]
            for instant, (x, y) in list(clicks.items()):
                if elapsed <= instant < end:
                    if RING not in demo.convert("RGB").crop((x - 10, y - 10, x + 11, y + 11)).getdata():
                        raise RuntimeError(f"Click indicator missing at {instant} ms")
                    del clicks[instant]
            elapsed = end
        if clicks or elapsed != sum(durations):
            raise RuntimeError("GIF timing does not match the captured sequence.")
        print(f"{demo.n_frames} frames, {elapsed / 1000:.2f}s, {width}x{height}")
    shutil.copyfile(captures / "02-history.png", output / "file-history-dark.png")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", type=Path, help="Path to the built Windows ntfstracer-cli.exe.")
    parser.add_argument("--captures", type=Path, help="Encode an existing set of captures instead of running WPF.")
    parser.add_argument("--output", type=Path, default=DIRECTORY.parents[1] / "docs/media")
    args = parser.parse_args()
    if args.captures:
        encode(args.captures, args.output)
        return
    if not args.engine:
        parser.error("--engine is required when capturing.")
    with TemporaryDirectory(prefix="ntfs-readme-") as temporary:
        scratch = Path(temporary)
        captures = scratch / "frames"
        captures.mkdir()
        (captures / "$LogFile").write_bytes(gzip.decompress((DIRECTORY / "synthetic-logfile.gz").read_bytes()))
        build = scratch / "build"
        subprocess.run(["dotnet", "build", str(DIRECTORY / "Capture.csproj"), "-c", "Release",
                        "--artifacts-path", str(build), f"-p:JournalIndexPath={args.engine.resolve()}"], check=True)
        subprocess.run(["dotnet", str(build / "bin/Capture/release/Capture.dll"), str(captures)], check=True)
        encode(captures, args.output)


if __name__ == "__main__":
    main()
