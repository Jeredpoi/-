"""Плавность по записи экрана: сколько реально разных кадров в секунду было на каждом этапе стенда.

Кадры, не отличающиеся от предыдущего (mpdecimate), не считаются: это и есть «застывший» экран.
Пишет report.md и report.json, достаёт ключевые кадры PNG из середины и конца каждого этапа.
"""
import json, re, subprocess, sys
from datetime import datetime
from pathlib import Path

d = Path(sys.argv[1])
rec = d / "rec.mp4"
def ts(s):
    s = s.strip().lstrip("﻿")
    s = re.sub(r"(\.\d{6})\d+", r"\1", s).replace("Z", "+00:00")
    return datetime.fromisoformat(s).timestamp()

start = ts((d / "rec-start.txt").read_text(encoding="utf-8-sig"))
phases = []
for line in (d / "bench-phases.txt").read_text(encoding="utf-8-sig").splitlines():
    if "\t" in line:
        t, name = line.split("\t", 1)
        phases.append((ts(t) - start, name))

# моменты, когда картинка на экране реально менялась
out = subprocess.run(["ffmpeg", "-hide_banner", "-i", str(rec), "-vf", "mpdecimate=hi=768:lo=320:frac=0.05,showinfo",
                      "-f", "null", "-"], capture_output=True, text=True, errors="replace").stderr
changes = [float(m) for m in re.findall(r"pts_time:([\d.]+)", out)]

# сколько кадров в секунду реально успевала снять запись (потолок для всех цифр ниже)
probe = subprocess.run(["ffprobe", "-v", "error", "-count_packets", "-show_entries", "stream=nb_read_packets,duration",
                        "-of", "csv=p=0", str(rec)], capture_output=True, text=True).stdout.strip().split(",")
try: cap_fps = round(int(probe[1]) / float(probe[0]), 1)
except Exception: cap_fps = None

# этапы «idle» — ожидание между действиями: паузы там нормальны и в счёт не идут
rows = []
for i, (t0, name) in enumerate(phases):
    t1 = phases[i + 1][0] if i + 1 < len(phases) else t0 + 3
    safe = re.sub(r"[^\w-]", "_", name)[:30]
    if name == "idle" or name == "done":
        continue
    c = [x for x in changes if t0 <= x < t1]
    # от начала действия до последней смены картинки; начальная задержка отклика — отдельно
    gaps = [b - a for a, b in zip(c, c[1:])]
    active = (c[-1] - c[0]) if len(c) > 1 else 0
    rows.append({
        "phase": name, "start_s": round(t0, 2), "len_s": round(t1 - t0, 2), "changes": len(c),
        "first_change_ms": round((c[0] - t0) * 1000) if c else None,
        "fps_active": round((len(c) - 1) / active, 1) if active > 0 else 0,
        "worst_gap_ms": round(max(gaps) * 1000) if gaps else None,
        "gaps_over_50ms": sum(g > 0.05 for g in gaps),
        "gaps_over_100ms": sum(g > 0.1 for g in gaps),
    })
    for tag, at in (("mid", (t0 + t1) / 2), ("end", t1 - 0.05)):
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", f"{max(0, at):.2f}", "-i", str(rec),
                        "-frames:v", "1", "-vf", "scale=960:-1", str(d / f"frame-{i:02d}-{safe}-{tag}.png")])

(d / "report.json").write_text(json.dumps({"capture_fps": cap_fps, "phases": rows}, ensure_ascii=False, indent=1), encoding="utf-8")
md = [f"Запись экрана успевала ~{cap_fps} к/с — это потолок для столбца «к/с».", "",
      "| этап | начало, с | длина, с | смен картинки | отклик, мс | к/с | худшая пауза, мс | пауз >50 мс | пауз >100 мс |",
      "|---|---|---|---|---|---|---|---|---|"]
for r in rows:
    md.append(f"| {r['phase']} | {r['start_s']} | {r['len_s']} | {r['changes']} | {r['first_change_ms']} | {r['fps_active']} | {r['worst_gap_ms']} | {r['gaps_over_50ms']} | {r['gaps_over_100ms']} |")
(d / "report.md").write_text("\n".join(md) + "\n", encoding="utf-8")
print("\n".join(md))
