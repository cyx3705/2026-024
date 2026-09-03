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
    private string _material = "";
    private string _surfaceTreatment = "";
    private string _heatTreatment = "";
    private string _drawingText = "";
    private string _partName = "";

    /// <summary>
    /// 建一行。<c>showsTargetName</c> 为 true 时「文件」列显示产物名而不是源文件名——
    /// 属性整备就是这一档：那一列要给的是**改完之后**的文件名，而不是一个用户马上就要
    /// 改掉的旧名字。V4.8 之前这靠另开一列「改名后预览」，于是同一个东西在表里占两列，
    /// 而改名成功之后「文件」那一列还继续显示一个已经不存在的文件。
    /// </summary>
    public ConversionFileRow(
        ScanCandidate candidate,
        bool regeneratesExistingOutput = false,
        string? id = null,
        bool showsTargetName = false)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        SourcePath = candidate.SourcePath;
        XtPath = candidate.XtPath;
        SolidWorksPath = candidate.SolidWorksPath;
        HasExistingOutput = candidate.HasExistingOutput;
        ShowsTargetName = showsTargetName;
        _regeneratesExistingOutput = regeneratesExistingOutput;
        _isSelected = !candidate.HasExistingOutput || regeneratesExistingOutput;
        _status = !candidate.HasExistingOutput
            ? ReadyStatus
            : regeneratesExistingOutput ? PendingReworkStatus : ExistingStatus;
    }

    public string Id { get; }
    public string SourcePath { get; private set; }
    public string XtPath { get; private set; }
    public string SolidWorksPath { get; private set; }
    public string FileName => Path.GetFileName(SourcePath);
    public string XtFileName => Path.GetFileName(XtPath);
    public string SolidWorksFileName => Path.GetFileName(SolidWorksPath);

    /// <summary>「文件」列显示的名字。见构造函数的 <c>showsTargetName</c>。</summary>
    public string DisplayName => ShowsTargetName ? SolidWorksFileName : FileName;

    /// <inheritdoc cref="DisplayName"/>
    public bool ShowsTargetName { get; }
    public bool HasExistingOutput { get; }

    /// <summary>
    /// 把这一行改挂到新的源/目标路径上，**保留行对象本身**。
    ///
    /// 图号前缀改一个字，改名计划整份重建，每一行的目标路径都跟着变。若为此把
    /// <c>Parts</c> 清空重填，几百个零件就是几百次控件重建加几百条集合变更通知——
    /// 那正是「改一格卡一下」的来源。行 Id 由源路径定死（见 <c>PropertyPrepPlanner</c>），
    /// 所以同一个零件在前后两份计划里仍是同一行，可以原地更新。
    /// </summary>
    public void Rebind(ScanCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (SourcePath == candidate.SourcePath
            && XtPath == candidate.XtPath
            && SolidWorksPath == candidate.SolidWorksPath)
        {
            return;
        }

        SourcePath = candidate.SourcePath;
        XtPath = candidate.XtPath;
        SolidWorksPath = candidate.SolidWorksPath;
        Raise(nameof(SourcePath));
        Raise(nameof(XtPath));
        Raise(nameof(SolidWorksPath));
        Raise(nameof(FileName));
        Raise(nameof(XtFileName));
        Raise(nameof(SolidWorksFileName));
        Raise(nameof(DisplayName));
    }

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

    /// <summary>
    /// V4.7 属性整备：材料、表面处理、热处理三槽的待写值。
    ///
    /// 值住在行上而不是计划里，因为 <c>AssemblyRenamePlan</c> 每次改图号前缀都会整份重建，
    /// 而用户填的材料不该跟着前缀一起被清掉。ViewModel 负责按源路径把它们搬回新行。
    /// 空串表示这一槽本轮不写，不会把模板里已有的值抹掉。
    /// </summary>
    public string Material
    {
        get => _material;
        set => SetField(ref _material, value ?? "");
    }

    /// <inheritdoc cref="Material"/>
    public string SurfaceTreatment
    {
        get => _surfaceTreatment;
        set => SetField(ref _surfaceTreatment, value ?? "");
    }

    /// <inheritdoc cref="Material"/>
    public string HeatTreatment
    {
        get => _heatTreatment;
        set => SetField(ref _heatTreatment, value ?? "");
    }

    /// <summary>
    /// V4.9 属性整备「图号」列。值是本轮计划算出来的图号文本，**只读**：
    /// 它由前缀加装配层级序号定死，逐行改会让同一层出现两套编号规则，
    /// 而那正是这个模块存在的理由。前缀为空时它是空串，也就是这一轮删图号。
    /// </summary>
    public string DrawingText
    {
        get => _drawingText;
        set => SetField(ref _drawingText, value ?? "");
    }

    /// <summary>
    /// V4.9 属性整备「名称」列。文件名里图号之后的那一段，**可逐行改**。
    ///
    /// 它同时决定目标文件名和「名称」属性槽——两者永远取自这一个字符串，
    /// 所以不会出现「文件名叫阀体、属性里写着阀盖」的两份真话。
    /// 与三个属性槽一样，权威记在 ViewModel 的记账里，行只是显示。
    /// </summary>
    public string PartName
    {
        get => _partName;
        set => SetField(ref _partName, value ?? "");
    }

    /// <summary>
    /// 这一行归本模块编号，因此文件会被改名、「名称」列可以改。
    ///
    /// 与 <see cref="WritesProperties"/> 是两件事：装配体也编号也改名，但不写属性。
    /// 小组件内部那些未编号的件两者都是 false——它们保持原名，改了也不会落盘。
    /// </summary>
    public bool RenamesFile { get; set; }

    /// <summary>
    /// 这一行的属性会不会真的落盘：只有拿到图号的 <c>.SLDPRT</c> 才会。
    ///
    /// 判定在建行时一次算好，不是每次取数再回改名计划里查一遍——页面每刷新一次表，
    /// 就要为每行的三列各查一次，几百个零件就是十万次线性查找。
    /// </summary>
    public bool WritesProperties { get; set; }

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

    private void Raise(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
