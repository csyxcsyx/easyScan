using PdfSharp.Pdf;
using System.Globalization;
using System.Text;

namespace EasyScan.Core;

/// <summary>Invisible Type0 text with an embedded glyphless font and explicit Unicode mapping.
/// The visible page remains entirely owned by the existing image/watermark renderer.</summary>
public sealed class PdfTextLayerWriter(PdfDocument document)
{
    private readonly Dictionary<string, ushort> _characters = [];
    private PdfDictionary? _font, _unicode, _glyphMap;
    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public void Add(PdfPage page, PaperPlacement placement, OcrPageResult result)
    {
        if (result.Blocks.Count == 0) return;
        Initialize();
        var resources = page.Elements.GetDictionary("/Resources") ?? new PdfDictionary(document);
        page.Elements["/Resources"] = resources;
        var fonts = resources.Elements.GetDictionary("/Font") ?? new PdfDictionary(document);
        resources.Elements["/Font"] = fonts; fonts.Elements["/EasyScanOcr"] = _font!.Reference!;
        var text = new StringBuilder("q\nBT\n/EasyScanOcr 1 Tf\n3 Tr\n");
        foreach (var block in result.Blocks)
        {
            var characters = block.Characters.Count > 0 ? block.Characters : new[] { new OcrCharacter(block.Text, block.Confidence, block.Polygon) };
            foreach (var character in characters)
            {
                var runes = character.Text.EnumerateRunes().ToArray();
                for (var i = 0; i < runes.Length; i++)
                {
                    var value = runes[i].ToString();
                    if (Rune.IsControl(runes[i])) continue;
                    if (!_characters.TryGetValue(value, out var cid))
                    {
                        if (_characters.Count >= 65534) throw new InvalidOperationException("PDF 中不同字符过多，无法创建文字层。");
                        cid = (ushort)(_characters.Count + 1); _characters.Add(value, cid);
                    }
                    var p = character.Polygon;
                    if (p.Length != 4 || p.Any(x => !double.IsFinite(x.X) || !double.IsFinite(x.Y))) continue;
                    NormalizedPoint Along(NormalizedPoint a, NormalizedPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    var tl = Along(p[0], p[1], i / (double)runes.Length);
                    var bl = Along(p[3], p[2], i / (double)runes.Length);
                    var br = Along(p[3], p[2], (i + 1d) / runes.Length);
                    var x = placement.X + bl.X * placement.Width;
                    var y = page.Height.Point - placement.Y - bl.Y * placement.Height;
                    text.Append(Number((br.X - bl.X) * placement.Width)).Append(' ')
                        .Append(Number(-(br.Y - bl.Y) * placement.Height)).Append(' ')
                        .Append(Number((tl.X - bl.X) * placement.Width)).Append(' ')
                        .Append(Number(-(tl.Y - bl.Y) * placement.Height)).Append(' ')
                        .Append(Number(x)).Append(' ').Append(Number(y)).Append(" Tm\n<")
                        .Append(cid.ToString("X4")).Append("> Tj\n");
                }
            }
        }
        text.Append("ET\nQ\n");
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(text.ToString()));
        UpdateMappings();
    }

    private PdfDictionary AddObject(PdfDictionary dictionary) { document.Internals.AddObject(dictionary); return dictionary; }
    private void Initialize()
    {
        if (_font is not null) return;
        var file = AddObject(new PdfDictionary(document));
        using var font = typeof(PdfTextLayerWriter).Assembly.GetManifestResourceStream("EasyScan.Core.Assets.pdf.ttf")
            ?? throw new InvalidOperationException("缺少 PDF 文字层字体资源。");
        using var memory = new MemoryStream(); font.CopyTo(memory);
        file.CreateStream(memory.ToArray()); file.Elements.SetInteger("/Length1", (int)memory.Length);
        var descriptor = AddObject(new PdfDictionary(document));
        descriptor.Elements.SetName("/Type", "/FontDescriptor"); descriptor.Elements.SetName("/FontName", "/GlyphLessFont");
        descriptor.Elements.SetInteger("/Flags", 5); descriptor.Elements.SetInteger("/Ascent", 1000);
        descriptor.Elements.SetInteger("/Descent", -1); descriptor.Elements.SetInteger("/CapHeight", 1000);
        descriptor.Elements.SetInteger("/ItalicAngle", 0); descriptor.Elements.SetInteger("/StemV", 80);
        descriptor.Elements["/FontBBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(1000), new PdfInteger(1000));
        descriptor.Elements["/FontFile2"] = file.Reference!;
        _glyphMap = AddObject(new PdfDictionary(document));
        var descendant = AddObject(new PdfDictionary(document));
        descendant.Elements.SetName("/Type", "/Font"); descendant.Elements.SetName("/Subtype", "/CIDFontType2");
        descendant.Elements.SetName("/BaseFont", "/GlyphLessFont"); descendant.Elements.SetInteger("/DW", 1000);
        descendant.Elements["/FontDescriptor"] = descriptor.Reference!; descendant.Elements["/CIDToGIDMap"] = _glyphMap.Reference!;
        var system = new PdfDictionary(document); system.Elements.SetString("/Registry", "Adobe");
        system.Elements.SetString("/Ordering", "Identity"); system.Elements.SetInteger("/Supplement", 0);
        descendant.Elements["/CIDSystemInfo"] = system;
        _unicode = AddObject(new PdfDictionary(document));
        _font = AddObject(new PdfDictionary(document));
        _font.Elements.SetName("/Type", "/Font"); _font.Elements.SetName("/Subtype", "/Type0");
        _font.Elements.SetName("/BaseFont", "/GlyphLessFont"); _font.Elements.SetName("/Encoding", "/Identity-H");
        _font.Elements["/DescendantFonts"] = new PdfArray(document, descendant.Reference!);
        _font.Elements["/ToUnicode"] = _unicode.Reference!;
    }

    private void UpdateMappings()
    {
        var map = new byte[(_characters.Count + 1) * 2];
        foreach (var cid in _characters.Values) map[cid * 2 + 1] = 1;
        SetStream(_glyphMap!, map);
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /EasyScanUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var batch in _characters.Chunk(100))
        {
            cmap.Append(batch.Length).Append(" beginbfchar\n");
            foreach (var entry in batch) cmap.Append('<').Append(entry.Value.ToString("X4")).Append("> <")
                .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(entry.Key))).Append(">\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        SetStream(_unicode!, Encoding.ASCII.GetBytes(cmap.ToString()));
    }
    private static void SetStream(PdfDictionary dictionary, byte[] bytes)
    {
        if (dictionary.Stream is null) dictionary.CreateStream(bytes);
        else dictionary.Stream.Value = bytes;
    }
}
