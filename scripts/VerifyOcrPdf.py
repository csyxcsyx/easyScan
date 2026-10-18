"""Independent PDF text/coordinate check (requires pypdf, test-time only).
Usage: python scripts/VerifyOcrPdf.py artifacts/<run>/扫描优化/pdf-checks.json
"""
import json
import pathlib
import sys
from pypdf import PdfReader
from pypdf.generic import ContentStream

def normalized(text):
    return ''.join(text.split())

def verify(manifest):
    reports = []
    for example in json.loads(manifest.read_text(encoding='utf-8')):
        reader = PdfReader(example['File'])
        extracted = '\n'.join(page.extract_text() for page in reader.pages)
        assert normalized(extracted) == normalized(example['Text']), f"Unicode extraction mismatch: {example['File']}"
        expected = [[] for _ in reader.pages]
        for scan in example['Scans']:
            placement, result = scan['Placement'], scan['Result']
            index = placement['SheetIndex']
            height = float(reader.pages[index].mediabox.height)
            for block in result['Blocks']:
                for char in block['Characters']:
                    p = char['Polygon']
                    for i, rune in enumerate(char['Text']):
                        if ord(rune) < 32:
                            continue
                        t = i / len(char['Text']); end = (i + 1) / len(char['Text'])
                        def along(a, b, fraction):
                            return a['X'] + (b['X'] - a['X']) * fraction, a['Y'] + (b['Y'] - a['Y']) * fraction
                        tl, bl, br = along(p[0], p[1], t), along(p[3], p[2], t), along(p[3], p[2], end)
                        w, h = placement['Width'], placement['Height']
                        expected[index].append([(br[0]-bl[0])*w, -(br[1]-bl[1])*h,
                            (tl[0]-bl[0])*w, -(tl[1]-bl[1])*h,
                            placement['X']+bl[0]*w, height-placement['Y']-bl[1]*h])
        worst = 0
        for index, page in enumerate(reader.pages):
            actual = []; font = None; mode = 0
            for operands, operator in ContentStream(page.get_contents(), reader).operations:
                if operator == b'Tf': font = str(operands[0])
                if operator == b'Tr': mode = int(operands[0])
                if operator == b'Tm' and font == '/EasyScanOcr':
                    assert mode == 3, 'OCR glyphs must use invisible rendering mode'
                    actual.append([float(x) for x in operands])
            assert len(actual) == len(expected[index]), 'character geometry count mismatch'
            for a, b in zip(actual, expected[index]):
                error = max(abs(x-y) for x, y in zip(a, b)); worst = max(worst, error)
                assert error < .001, f'OCR geometry mismatch: {error} pt'
        reports.append(dict(file=example['File'], pages=len(reader.pages),
            characters=sum(map(len, expected)), unicode_text=True, invisible=True, max_coordinate_error_pt=worst))
    output = manifest.with_name('pdf-independent-report.json')
    output.write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'PASS: {len(reports)} PDFs; Unicode text, invisible glyphs and all character positions verified.')

if __name__ == '__main__':
    verify(pathlib.Path(sys.argv[1]))
