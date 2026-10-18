"""Fetch the pinned, redistributable CPU OCR assets. Runtime never downloads files."""
import hashlib
import json
import pathlib
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
BASE = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/'
ASSETS = [
    ('det.onnx', 'onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx', '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae'),
    ('rec.onnx', 'onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx', '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5'),
    ('cls.onnx', 'onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx', '54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7'),
    ('dict.txt', 'paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt', 'd1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b'),
]

def fetch(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'easyScan-build/1.3'})
    with urllib.request.urlopen(request, timeout=90) as response:
        return response.read()

def main():
    target = ROOT / 'models' / 'ocr'
    target.mkdir(parents=True, exist_ok=True)
    manifest = []
    for name, relative, expected in ASSETS:
        path = target / name
        data = path.read_bytes() if path.exists() else None
        if data is None or hashlib.sha256(data).hexdigest() != expected:
            data = fetch(BASE + relative)
        digest = hashlib.sha256(data).hexdigest()
        if expected and digest != expected:
            raise ValueError(f'Hash mismatch: {name}: {digest}')
        if not path.exists() or path.read_bytes() != data:
            temporary = path.with_name(path.name + '.download')
            try:
                temporary.write_bytes(data)
                temporary.replace(path)
            finally:
                temporary.unlink(missing_ok=True)
        manifest.append(dict(file=name, url=BASE + relative, sha256=digest, bytes=len(data), license='Apache-2.0', upstream='RapidAI/RapidOCR v3.10.0 (PaddleOCR PP-OCRv5)'))
        print(name, len(data), digest, flush=True)
    (target / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')

if __name__ == '__main__':
    main()
