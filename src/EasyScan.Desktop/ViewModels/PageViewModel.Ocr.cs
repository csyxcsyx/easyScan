using CommunityToolkit.Mvvm.ComponentModel;
using EasyScan.Core;
using System.IO;

namespace EasyScan.Desktop.ViewModels;

public sealed partial class PageViewModel
{
    private ScanSettings? _ocrSettings;
    private (long Length, long Modified) _ocrStamp;
    public OcrPageResult? OcrResult { get; private set; }
    [ObservableProperty] private string ocrState = "未识别";
    [ObservableProperty] private string ocrError = "";
    public bool HasCurrentOcr
    {
        get
        {
            if (OcrResult is null || _ocrSettings != Settings) return false;
            try { var file = new FileInfo(Source.Document.Path); return file.Exists && (file.Length, file.LastWriteTimeUtc.Ticks) == _ocrStamp; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }
    public string OcrText => HasCurrentOcr ? OcrResult!.Text : "";
    public string OcrSummary => !HasCurrentOcr ? (OcrResult is not null && OcrState == "已识别" ? "已过期，请重新识别" : OcrState) : OcrResult!.Blocks.Count == 0 ? "未检测到文字" :
        $"{OcrResult.Blocks.Count} 行文字" + (OcrResult.Blocks.Count(x => x.Confidence < .8) is var count && count > 0 ? $" · {count} 行置信度较低，请核对" : "");

    public bool SetOcrResult(OcrPageResult result, ScanSettings settings, (long Length, long Modified)? sourceVersion = null)
    {
        if (settings != Settings) { OcrState = "扫描参数已更改，请重新识别"; return false; }
        var file = new FileInfo(Source.Document.Path);
        if (sourceVersion.HasValue && sourceVersion.Value != (file.Length, file.LastWriteTimeUtc.Ticks))
        { OcrState = "原文件已更改，请重新识别"; return false; }
        _ocrStamp = (file.Length, file.LastWriteTimeUtc.Ticks); _ocrSettings = settings; OcrResult = result;
        OcrError = ""; OcrState = "已识别"; NotifyOcrValidity(); return true;
    }
    private void NotifyOcrValidity()
    {
        if (OcrResult is not null && !HasCurrentOcr) OcrState = "已过期，请重新识别";
        else if (HasCurrentOcr) OcrState = "已识别";
        foreach (var name in new[] { nameof(OcrResult), nameof(HasCurrentOcr), nameof(OcrText), nameof(OcrSummary) }) OnPropertyChanged(name);
    }
    partial void OnOcrStateChanged(string value) => OnPropertyChanged(nameof(OcrSummary));
}
