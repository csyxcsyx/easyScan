# Third-party notices

easyScan contains the following third-party components. Original license/notice files available in NuGet packages are copied into `licenses/` during packaging. The .NET runtime notice files are also retained in the published directory.

| Component | Version | License / upstream |
|---|---|---|
| .NET / WPF | bundled .NET 10 runtime | MIT; [dotnet](https://github.com/dotnet) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT; [CommunityToolkit](https://github.com/CommunityToolkit/dotnet) |
| OpenCvSharp4 and Windows runtime | 4.13.0.20260627 | Apache-2.0; [OpenCvSharp](https://github.com/shimat/opencvsharp) |
| OpenCV and bundled native dependencies | 4.13.0 | Apache-2.0 and respective third-party licenses; [OpenCV](https://github.com/opencv/opencv) |
| Magick.NET-Q8-x64 / Core | 15.0.0 | Apache-2.0; [Magick.NET](https://github.com/dlemstra/Magick.NET); full native dependencies notice included in `licenses/` |
| PDFtoImage | 5.4.0 | MIT; [PDFtoImage](https://github.com/sungaila/PDFtoImage) |
| SkiaSharp / NativeAssets.Win32 | 4.150.1 | MIT and native Skia notices; [SkiaSharp](https://github.com/mono/SkiaSharp) |
| bblanchon.PDFium.Win32 | 152.0.7961 | Apache-2.0 for packaging; BSD-style PDFium and respective native dependencies; [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) |
| PDFsharp | 6.2.4 | MIT; [PDFsharp](https://github.com/empira/PDFsharp/tree/v6.2.4) |
| System.Security.Cryptography.Pkcs | 10.0.13 | MIT; Microsoft .NET |
| Microsoft.Extensions.* | resolved versions in packages.lock.json | MIT; Microsoft .NET |
| Microsoft.ML.OnnxRuntime / Managed | 1.29.0 | MIT and native dependency notices; [ONNX Runtime](https://github.com/microsoft/onnxruntime) |
| Clipper2 | 2.0.0 | Boost Software License 1.0; [Clipper2](https://github.com/AngusJohnson/Clipper2) |
| PP-OCRv5 mobile detector, classifier, Chinese recognizer and dictionary | RapidOCR v3.10.0 distribution, SHA-256 pinned | Apache-2.0; [RapidOCR](https://github.com/RapidAI/RapidOCR), [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR); provenance in models/ocr/manifest.json |
| OCR pre/postprocessing reference | RapidOCR / PaddleOCR | Apache-2.0; ported to C# with existing OpenCV; see licenses/OCR-NOTICE.txt |
| GlyphLessFont (pdf.ttf) / invisible text-layer reference | Tesseract | Apache-2.0; [pdf_ttf.h](https://github.com/tesseract-ocr/tesseract/blob/main/src/api/pdf_ttf.h), [PDF renderer](https://github.com/tesseract-ocr/tesseract/blob/main/src/api/pdfrenderer.cpp) |

Segoe UI, Microsoft YaHei UI and Segoe MDL2 Assets are supplied by Windows and are not redistributed in this package.

The paper detector's line/contour combination was informed by the algorithm description in [OpenCV Document Scanner](https://github.com/andrewdcampbell/OpenCV-Document-Scanner). Its source is not included; the detector is implemented using the existing OpenCV APIs.
