// Pre/postprocessing adapted from RapidAI/RapidOCR and PaddleOCR (Apache-2.0).
// See licenses/OCR-NOTICE.txt for attribution and the pinned model manifest.
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Security.Cryptography;

namespace EasyScan.Core;

public sealed partial class OnnxOcrEngine(string? modelDirectory = null) : IOcrEngine
{
    private readonly string _directory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "ocr");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _detector, _classifier, _recognizer;
    private string[] _characters = [];
    private bool _disposed;
    public string Version => "PP-OCRv5/RapidOCR-v3.10.0/4d97c44a-5825fc7e-54379ae5-d1979e9f/pipeline-2";
    private static readonly (string File, string Hash)[] Assets =
    [
        ("det.onnx", "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae"),
        ("rec.onnx", "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5"),
        ("cls.onnx", "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7"),
        ("dict.txt", "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b")
    ];

    public async Task<OcrPageResult> RecognizeAsync(Mat image, IProgress<OcrProgress>? progress = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (image.Empty() || image.Type() != MatType.CV_8UC3) throw new ArgumentException("OCR 需要有效的彩色扫描图。");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("正在加载离线模型…")); Initialize(token);
                progress?.Report(new("正在检测文字…"));
                var boxes = Detect(image, token);
                var blocks = ReadLines(image, boxes, progress, token);
                token.ThrowIfCancellationRequested();
                return new OcrPageResult(image.Width, image.Height, blocks);
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void Initialize(CancellationToken token)
    {
        if (_detector is not null) return;
        foreach (var asset in Assets)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(_directory, asset.File);
            if (!File.Exists(path)) throw new InvalidOperationException("缺少离线 OCR 模型，请保留便携包中的 models/ocr 文件夹，或运行 scripts/FetchOcrModels.py。");
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"OCR 模型或字典校验失败：{asset.File}，请恢复完整便携包。");
        }
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            EnableCpuMemArena = false,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        InferenceSession? det = null, cls = null, rec = null;
        try
        {
            det = new(Path.Combine(_directory, "det.onnx"), options); token.ThrowIfCancellationRequested();
            cls = new(Path.Combine(_directory, "cls.onnx"), options); token.ThrowIfCancellationRequested();
            rec = new(Path.Combine(_directory, "rec.onnx"), options); token.ThrowIfCancellationRequested();
            _characters = ["", .. File.ReadAllLines(Path.Combine(_directory, "dict.txt")), " "];
            var classes = rec.OutputMetadata.First().Value.Dimensions.Last();
            if (classes > 0 && classes != _characters.Length) throw new InvalidDataException("OCR 模型与字符字典不匹配。");
            _detector = det; _classifier = cls; _recognizer = rec;
        }
        catch { det?.Dispose(); cls?.Dispose(); rec?.Dispose(); throw; }
    }

    private static (float[] Data, int[] Shape) Run(InferenceSession session, DenseTensor<float> tensor, CancellationToken token)
        => RunOutput(session, tensor, token, value => (value.ToArray(), value.Dimensions.ToArray()));

    private static T RunOutput<T>(InferenceSession session, DenseTensor<float> tensor, CancellationToken token, Func<Tensor<float>, T> consume)
    {
        token.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var registration = token.Register(() => options.Terminate = true);
        try
        {
            using var output = session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)], session.OutputNames, options);
            token.ThrowIfCancellationRequested();
            var value = output.First().AsTensor<float>();
            return consume(value);
        }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    // Models use BGR CHW input. Detector uses ImageNet normalization; cls/rec use [-1,1].
    private static DenseTensor<float> Tensor(Mat image, int height, int width, bool detector, out int contentWidth)
    {
        contentWidth = detector ? width : Math.Clamp((int)Math.Ceiling(height * image.Width / (double)image.Height), 1, width);
        using var resized = new Mat(); Cv2.Resize(image, resized, new Size(contentWidth, height));
        var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });
        var values = tensor.Buffer.Span; var rows = resized.AsRows<Vec3b>();
        float[] mean = [.485f, .456f, .406f], std = [.229f, .224f, .225f];
        for (var y = 0; y < height; y++)
        {
            var row = rows[y];
            for (var x = 0; x < contentWidth; x++)
                for (var c = 0; c < 3; c++)
                {
                    var value = row[x][c] / 255f;
                    values[c * height * width + y * width + x] = detector ? (value - mean[c]) / std[c] : (value - .5f) * 2;
                }
        }
        return tensor;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _gate.Wait();
        try { _detector?.Dispose(); _classifier?.Dispose(); _recognizer?.Dispose(); }
        finally { _gate.Release(); }
    }
}
