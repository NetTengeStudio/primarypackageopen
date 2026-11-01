"""Inspect imported, unmodified audio. Raw FFmpeg output is retained alongside the summary."""
import argparse
import json
from pathlib import Path
import re
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--out", required=True, type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
args.out.mkdir(parents=True, exist_ok=True)
results = []
for path in sorted((root / "Content/Systems/RPWheel/Emotions/Sounds").glob("*.ogg")):
    run = subprocess.run(["ffmpeg", "-hide_banner", "-nostdin", "-i", str(path), "-af", "ebur128=peak=true,astats=metadata=0:reset=0", "-f", "null", "-"], capture_output=True, encoding="utf-8", errors="replace", timeout=30)
    (args.out / (path.stem + ".log")).write_text(run.stderr, encoding="utf-8")
    if run.returncode:
        raise RuntimeError(f"FFmpeg failed for {path.name}; inspect preserved log")
    integrated = re.findall(r"I:\s*(-?[\d.]+) LUFS", run.stderr)
    peaks = re.findall(r"Peak level dB:\s*(-?[\d.]+)", run.stderr)
    results.append({"file": path.name, "integratedLUFS": float(integrated[-1]) if integrated else None, "peakDBFS": float(peaks[-1]) if peaks else None})
representative = next((root / "Content/Systems/RPWheel/Emotions/Sounds").glob("scream*.ogg"))
for name, graph in [("waveform", "showwavespic=s=1000x240:colors=65b6ff"), ("spectrum", "showspectrumpic=s=1000x400:legend=1:scale=log")]:
    run = subprocess.run(["ffmpeg", "-hide_banner", "-nostdin", "-y", "-i", str(representative), "-lavfi", graph, "-frames:v", "1", str(args.out / (name + ".png"))], capture_output=True, encoding="utf-8", errors="replace", timeout=30)
    (args.out / (name + ".log")).write_text(run.stderr, encoding="utf-8")
    if run.returncode:
        raise RuntimeError(f"FFmpeg {name} failed; inspect preserved log")
(args.out / "summary.json").write_text(json.dumps({"files": results, "representative": representative.name, "audioModified": False}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"files": len(results), "representative": representative.name, "peakMaxDBFS": max(r["peakDBFS"] for r in results if r["peakDBFS"] is not None)}))
