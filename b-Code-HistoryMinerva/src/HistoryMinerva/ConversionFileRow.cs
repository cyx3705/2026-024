using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SE2SW;

public sealed class ConversionFileRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status;
    private string _detail = "";
    private string _featureText = "";
    private string _sketchText = "";
    private bool _hasFeatureWarning;

    public ConversionFileRow(ScanCandidate candidate)
    {
        Id = Guid.NewGuid().ToString("N");
        SourcePath = candidate.SourcePath;
        XtPath = candidate.XtPath;
        SolidWorksPath = candidate.SolidWorksPath;
        HasExistingOutput = candidate.HasExistingOutput;
        _isSelected = !candidate.HasExistingOutput;
        _status = candidate.HasExistingOutput ? "已存在" : "就绪";
    }

    public string Id { get; }
    public string SourcePath { get; }
    public string XtPath { get; }
    public string SolidWorksPath { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string XtFileName => Path.GetFileName(XtPath);
    public string SolidWorksFileName => Path.GetFileName(SolidWorksPath);
    public bool HasExistingOutput { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (HasExistingOutput && value)
                return;
            SetField(ref _isSelected, value);
        }
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    /// <summary>V2.0：识别出的特征数。</summary>
    public string FeatureText
    {
        get => _featureText;
        set => SetField(ref _featureText, value);
    }

    /// <summary>V2.0：草图完全定义比例，形如 4/4。</summary>
    public string SketchText
    {
        get => _sketchText;
        set => SetField(ref _sketchText, value);
    }

    /// <summary>识别为空、或有草图未能完全定义时为 true，用于着色。</summary>
    public bool HasFeatureWarning
    {
        get => _hasFeatureWarning;
        set => SetField(ref _hasFeatureWarning, value);
    }

    public void ResetFeatureResult()
    {
        FeatureText = "";
        SketchText = "";
        HasFeatureWarning = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
