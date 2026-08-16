using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace HistoryMinerva;

public sealed class ConversionFileRow : INotifyPropertyChanged
{
    /// <summary>已有产物、且本轮不会重做时的状态文字。</summary>
    public const string ExistingStatus = "已存在";

    /// <summary>已有产物、但本轮会连同特征一起重做时的状态文字。</summary>
    public const string PendingReworkStatus = "待整备";

    public const string ReadyStatus = "就绪";

    private bool _isSelected;
    private bool _regeneratesExistingOutput;
    private string _status;
    private string _detail = "";
    private string _featureText = "";
    private string _sketchText = "";
    private bool _hasFeatureWarning;

    public ConversionFileRow(ScanCandidate candidate, bool regeneratesExistingOutput = false, string? id = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        SourcePath = candidate.SourcePath;
        XtPath = candidate.XtPath;
        SolidWorksPath = candidate.SolidWorksPath;
        HasExistingOutput = candidate.HasExistingOutput;
        _regeneratesExistingOutput = regeneratesExistingOutput;
        _isSelected = !candidate.HasExistingOutput || regeneratesExistingOutput;
        _status = !candidate.HasExistingOutput
            ? ReadyStatus
            : regeneratesExistingOutput ? PendingReworkStatus : ExistingStatus;
    }

    public string Id { get; }
    public string SourcePath { get; }
    public string XtPath { get; }
    public string SolidWorksPath { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string XtFileName => Path.GetFileName(XtPath);
    public string SolidWorksFileName => Path.GetFileName(SolidWorksPath);
    public bool HasExistingOutput { get; }

    /// <summary>
    /// 本轮会把已有产物连同特征一起重做。开启特征识别的装配转换就是这种情形：
    /// 已有 SLDPRT 里有没有特征，不打开文档就判断不了，而打开的代价与重新导入相当，
    /// 所以 <c>AssemblyPartReusePlanner</c> 一律重做。
    ///
    /// 此时再显示"已存在"并锁住复选框，说的就是假话——文件确实在，缺的正是这一轮要补的
    /// 特征树。现场事故：SW 自整备的整目录零件全被标成"已存在"，用户以为没法整备。
    /// </summary>
    public bool RegeneratesExistingOutput
    {
        get => _regeneratesExistingOutput;
        set
        {
            if (_regeneratesExistingOutput == value)
                return;
            SetField(ref _regeneratesExistingOutput, value);
            if (!HasExistingOutput)
                return;
            // 只在两个空闲状态之间切换，不覆盖转换过程中写进来的进度或结果。
            if (_status is ExistingStatus or PendingReworkStatus)
                Status = value ? PendingReworkStatus : ExistingStatus;
            IsSelected = value;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (HasExistingOutput && !RegeneratesExistingOutput && value)
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
