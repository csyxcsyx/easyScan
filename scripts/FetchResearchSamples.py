"""Download pinned research validation inputs; never included in portable distribution.
Usage: python scripts/FetchResearchSamples.py [artifacts/research-data]
DIBCO is a handwritten binarization benchmark, not a printed Chinese OCR corpus.
The GCDRNet photo has no supplied GT here and is for visual inspection only.
"""
import hashlib
import json
from pathlib import Path
import sys
import urllib.request
import zipfile

ASSETS = [
    ("real-photo.jpg", "https://raw.githubusercontent.com/ZZZHANG-jx/GCDRNet/415c97a9a64d0a796dbbad43485ce2ab102f2a7d/distorted/origin1000_22_in.jpg",
     "5f24d8dbd5b762fef24c4e0fc30092e0d05a5403c713663226d61a9155adea7e", "GCDRNet repository MIT; author example, no supplied paired GT"),
    ("dibco2018_Dataset.zip", "https://vc.ee.duth.gr/h-dibco2018/benchmark/dibco2018_Dataset.zip",
     "5a4eba97a2ffc3e7ef9bb231b9d40937a3275ebd01106d477a16fc719ca21e33", "Official H-DIBCO 2018 research benchmark; no explicit redistribution license found; local evaluation only"),
    ("dibco2018-GT.zip", "https://vc.ee.duth.gr/h-dibco2018/benchmark/dibco2018-GT.zip",
     "ed8d50e77ce4e08c690aa15cad2070172d67d4aa5a7a5f036edd8b47dca4d5f4", "Official H-DIBCO 2018 ground truth; same research-only handling"),
]

def fetch(folder):
    folder.mkdir(parents=True, exist_ok=True)
    manifest = []
    for name, url, expected, terms in ASSETS:
        path = folder / name
        if path.exists():
            data = path.read_bytes()
        else:
            with urllib.request.urlopen(url, timeout=45) as response:
                data = response.read()
        actual = hashlib.sha256(data).hexdigest()
        if actual != expected:
            raise ValueError(f"SHA-256 mismatch: {name}")
        if not path.exists():
            path.write_bytes(data)
        if name.endswith(".zip"):
            with zipfile.ZipFile(path) as archive:
                for member in archive.infolist():
                    target = (folder / member.filename).resolve()
                    if not target.is_relative_to(folder.resolve()):
                        raise ValueError("Unsafe ZIP path")
                    # Native evaluation executables and large MATLAB weight maps are unnecessary.
                    if member.filename.lower().endswith(".bmp") and member.filename.split("/")[0] in ("dataset", "gt"):
                        archive.extract(member, folder)
        manifest.append(dict(file=name, url=url, sha256=actual, bytes=len(data), terms=terms))
        print(f"Verified {name}")
    (folder / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

if __name__ == "__main__":
    fetch(Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/research-data"))
