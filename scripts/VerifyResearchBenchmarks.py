"""Check isolated baseline/current research benchmark gates (Python standard library only).
Usage: python scripts/VerifyResearchBenchmarks.py [artifacts/research-bench]
"""
import json
from pathlib import Path
import statistics
import sys

def verify(folder):
    def load(tag, mode):
        return json.loads((folder / f"{tag}-{mode}.json").read_text(encoding="utf-8"))
    summary = {}
    for mode in ("binary", "enhance"):
        baseline, current = load("baseline", mode), load("current", mode)
        assert baseline["convertParameters"] == 3 and current["convertParameters"] == 4
        rows = []
        for old, new in zip(baseline["measurements"], current["measurements"]):
            assert (old["width"], old["height"]) == (new["width"], new["height"])
            median = new["medianMs"] / old["medianMs"]
            p95 = new["p95Ms"] / old["p95Ms"]
            memory = new["peakMiB"] / old["peakMiB"]
            assert median <= 1.10, f"{mode} median budget: {median}"
            assert p95 <= 1.20, f"{mode} P95 budget: {p95}"
            if mode == "binary":
                assert old["hash"] == new["hash"], "Binary pixels differ"
                assert memory <= .80, f"Binary memory budget: {memory}"
            rows.append(dict(width=old["width"], height=old["height"], median_ratio=median, p95_ratio=p95, peak_memory_ratio=memory))
        summary[mode] = rows
    old = load("baseline", "holdout")["measurements"]
    new = load("current", "holdout")["measurements"]
    assert len(old) == len(new) == 12
    before = statistics.mean(x["mae"] for x in old); after = statistics.mean(x["mae"] for x in new)
    assert after < before
    summary["synthetic_holdout"] = dict(count=12, mae_before=before, mae_after=after,
        color_mae_before=statistics.mean(x["colorMae"] for x in old), color_mae_after=statistics.mean(x["colorMae"] for x in new))
    old = load("baseline", "dibco")["measurements"]; new = load("current", "dibco")["measurements"]
    assert len(old) == len(new) == 10
    assert all(abs(a["rawF1"] - b["rawF1"]) < 1e-12 for a, b in zip(old, new))
    before = statistics.mean(x["enhancedF1"] for x in old); after = statistics.mean(x["enhancedF1"] for x in new)
    assert after >= before
    summary["dibco"] = dict(count=10, enhanced_f1_before=before, enhanced_f1_after=after)
    for count in (10, 30):
        rows = []
        for tag in ("baseline", "current"):
            data = load(tag, f"batch{count}")["measurements"][0]
            assert len(data["times"]) == count
            # Allocators may retain a small pool; reject unbounded page-proportional growth.
            assert max(data["workingMiB"][-5:]) - min(data["workingMiB"][-5:]) < 16
            rows.append(dict(median_ms=statistics.median(data["times"]), peak_mib=data["peakMiB"], last_working_mib=data["workingMiB"][-1]))
        assert rows[1]["median_ms"] / rows[0]["median_ms"] <= 1.10
        summary[f"batch{count}"] = rows
    old = load("baseline", "ocr")["measurements"][0]; new = load("current", "ocr")["measurements"][0]
    assert old["text"] == new["text"]
    assert statistics.median(new["warmMs"]) / statistics.median(old["warmMs"]) <= 1.10
    summary["ocr"] = dict(warm_median_before=statistics.median(old["warmMs"]), warm_median_after=statistics.median(new["warmMs"]), cold_before=old["coldMs"], cold_after=new["coldMs"])
    old = load("baseline", "ocr-long")["measurements"][0]; new = load("current", "ocr-long")["measurements"][0]
    assert new["cer"] <= .01 and new["cer"] < old["cer"]
    summary["known_box_long_line"] = dict(cer_before=old["cer"], cer_after=new["cer"])
    (folder / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("PASS: quality, OCR, binary equivalence, timing, memory and 10/30-page stability gates")

if __name__ == "__main__":
    verify(Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/research-bench"))
