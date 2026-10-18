using CommunityToolkit.Mvvm.ComponentModel;
using EasyScan.Core;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.ViewModels;

public sealed partial class PageViewModel(PageSource source) : ObservableObject
{
    private readonly List<ScanSettings> _undo = [];
    private readonly Stack<ScanSettings> _redo = [];
    private DateTime _lastEdit;
    private string _lastKind = "";
    private ScanSettings _settings = new();
    private ScanSettings _initialSettings = new();
    private ScanSettings? _cropSession;
    private string _sessionState = "", _sessionDescription = "";

    public PageSource Source { get; private set; } = source;
    public string Name => Source.DisplayName;
    public ScanSettings Settings => _settings;
    public CropQuad Crop => _settings.Crop;
    public CurvedEdges? CurvedEdges => _settings.CurvedEdges;
    public bool HasCurvedEdges => _settings.CurvedEdges is not null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsReady => State is "已就绪" or "待确认";
    public string Dimensions => Source.Document.IsPdf ? "PDF 页面 · 300 DPI" : Source.Width > 0 ? $"{Source.Width:N0} × {Source.Height:N0}" : "";
    public event EventHandler? SettingsChanged;

    [ObservableProperty] private BitmapSource? thumbnail;
    [ObservableProperty] private string state = "排队中";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string cropDescription = "";
    [ObservableProperty] private PhotoQuality quality = new(false, false, false, 0);
    [ObservableProperty] private double cropConfidence;
    public string QualitySummary => Quality.Summary;
    public bool HasQualityIssues => Quality.HasIssues;
    partial void OnQualityChanged(PhotoQuality value) { OnPropertyChanged(nameof(QualitySummary)); OnPropertyChanged(nameof(HasQualityIssues)); }

    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(IsReady));

    public double ShadowStrength { get => _settings.ShadowRemoval * 100; set => Edit(_settings with { ShadowRemoval = value / 100 }, "enhance"); }
    public double Brightness { get => _settings.Brightness; set => Edit(_settings with { Brightness = value }, "enhance"); }
    public double Contrast { get => _settings.Contrast; set => Edit(_settings with { Contrast = value }, "enhance"); }
    public bool AutoExposure { get => _settings.AutoExposure; set => Edit(_settings with { AutoExposure = value }, "exposure"); }
    public bool CorrectColorCast { get => _settings.CorrectColorCast; set => Edit(_settings with { CorrectColorCast = value }, "color"); }
    public double HighlightRecovery { get => _settings.HighlightRecovery * 100; set => Edit(_settings with { HighlightRecovery = value / 100 }, "highlight"); }
    public double ColorBoost { get => _settings.ColorBoost * 100; set => Edit(_settings with { ColorBoost = value / 100 }, "color"); }
    public double NoiseReduction { get => _settings.NoiseReduction * 100; set => Edit(_settings with { NoiseReduction = value / 100 }, "detail"); }
    public double DetailEnhancement { get => _settings.DetailEnhancement * 100; set => Edit(_settings with { DetailEnhancement = value / 100 }, "detail"); }
    public bool FlattenCurvedEdges { get => _settings.FlattenCurvedEdges; set => Edit(_settings with { FlattenCurvedEdges = value }, "curve"); }
    public double EdgeInset { get => _settings.EdgeInset * 100; set => Edit(_settings with { EdgeInset = value / 100 }, "inset"); }
    public bool IsNatural { get => _settings.Preset == EnhancementPreset.Natural; set { if (value) Edit(_settings with { Preset = EnhancementPreset.Natural }, "preset"); } }
    public bool IsWhitePaper { get => _settings.Preset == EnhancementPreset.WhitePaper; set { if (value) Edit(_settings with { Preset = EnhancementPreset.WhitePaper }, "preset"); } }

    public void Initialize(CropDetection detection, int width, int height)
    {
        Source = Source with { Width = width, Height = height };
        _initialSettings = new ScanSettings { Crop = detection.Crop, CurvedEdges = detection.CurvedEdges,
            EdgeInset = detection.Crop == CropQuad.FullFrame ? 0 : .0015 };
        _settings = _initialSettings;
        _undo.Clear(); _redo.Clear();
        CropDescription = detection.Description;
        CropConfidence = detection.Confidence;
        State = detection.Confidence < .65 ? "待确认" : "已就绪";
        Error = "";
        NotifySettings();
        OnPropertyChanged(nameof(Dimensions));
    }

    public void Edit(ScanSettings settings, string kind = "edit")
    {
        if (settings == _settings) return;
        if (_cropSession is null && (kind != _lastKind || DateTime.UtcNow - _lastEdit > TimeSpan.FromMilliseconds(500)))
        {
            _undo.Add(_settings);
            if (_undo.Count > 60) _undo.RemoveAt(0);
        }
        _lastKind = kind; _lastEdit = DateTime.UtcNow;
        if (_cropSession is null) _redo.Clear();
        _settings = settings;
        NotifySettings();
    }

    public void SetCrop(CropQuad crop)
    {
        if (!crop.IsValid) return;
        var curves = _settings.CurvedEdges;
        if (curves is not null)
        {
            var before = Core.CurvedEdges.Straight(_settings.Crop).Points; var after = Core.CurvedEdges.Straight(crop).Points;
            var moved = curves.Points.Select((p, i) => new NormalizedPoint(Math.Clamp(p.X + after[i].X - before[i].X, 0, 1), Math.Clamp(p.Y + after[i].Y - before[i].Y, 0, 1))).ToArray();
            curves = new(moved[0], moved[1], moved[2], moved[3]);
            if (!curves.IsValidFor(crop)) curves = null;
        }
        SetBoundary(crop, curves);
    }
    public void SetBoundary(CropQuad crop, CurvedEdges? curves)
    {
        if (!crop.IsValid || (curves is not null && !curves.IsValidFor(crop))) return;
        Edit(_settings with { Crop = crop, CurvedEdges = curves }, "crop");
        CropDescription = "手动裁剪";
        if (_cropSession is null) State = "已就绪";
    }

    public void ApplyDetection(CropDetection detection)
    {
        Edit(_settings with { Crop = detection.Crop, CurvedEdges = detection.CurvedEdges,
            EdgeInset = detection.Crop == CropQuad.FullFrame ? 0 : _settings.EdgeInset }, "detect");
        CropConfidence = detection.Confidence; CropDescription = detection.Description;
        State = detection.Confidence < .65 ? "待确认" : "已就绪";
    }
    public void BeginCropSession()
    {
        _cropSession = _settings; _sessionState = State; _sessionDescription = CropDescription;
    }
    public void EndCropSession(bool commit)
    {
        if (_cropSession is not { } snapshot) return;
        _cropSession = null; _lastKind = "";
        if (commit)
        {
            if (_settings != snapshot)
            {
                _undo.Add(snapshot); _redo.Clear();
                if (_undo.Count > 60) _undo.RemoveAt(0);
            }
            State = "已就绪";
        }
        else { _settings = snapshot; State = _sessionState; CropDescription = _sessionDescription; }
        NotifySettings();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(_settings); _settings = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _lastKind = ""; NotifySettings();
    }
    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(_settings); _settings = _redo.Pop(); _lastKind = ""; NotifySettings();
    }
    public void Reset() => Edit(_initialSettings, "reset");

    private void NotifySettings()
    {
        foreach (var name in new[] { nameof(Settings), nameof(Crop), nameof(CurvedEdges), nameof(HasCurvedEdges), nameof(ShadowStrength), nameof(Brightness), nameof(Contrast),
            nameof(IsNatural), nameof(IsWhitePaper), nameof(AutoExposure), nameof(CorrectColorCast), nameof(HighlightRecovery), nameof(ColorBoost),
            nameof(NoiseReduction), nameof(DetailEnhancement), nameof(FlattenCurvedEdges), nameof(EdgeInset), nameof(CanUndo), nameof(CanRedo) }) OnPropertyChanged(name);
        NotifyOcrValidity();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
