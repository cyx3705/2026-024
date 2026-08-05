using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SE2SW.Contracts;

namespace SE2SW.Worker;

internal sealed class SolidWorksInteropBridge : IDisposable
{
    private readonly object _application;
    private readonly Type _applicationInterface;
    private readonly Type _modelInterface;
    private readonly Type _extensionInterface;
    private readonly Type _frameInterface;
    private readonly Type _featureInterface;
    private readonly Type _sketchInterface;
    private readonly Type _sketchManagerInterface;
    private readonly Type _partInterface;
    private readonly Type _bodyInterface;
    private readonly Type _entityInterface;
    private readonly Type _faceInterface;
    private readonly Type _surfaceInterface;
    private readonly Type _selectionManagerInterface;
    private readonly Type _selectDataInterface;
    private readonly Type _assemblyInterface;
    private readonly Type _componentInterface;
    private readonly Type _mathUtilityInterface;
    private readonly Type _mathTransformInterface;
    private readonly string _installDirectory;
    private Type? _featureWorksInterface;

    private SolidWorksInteropBridge(object application, Assembly interopAssembly, string installDirectory)
    {
        _installDirectory = installDirectory;
        _applicationInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISldWorks");
        _modelInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IModelDoc2");
        _extensionInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IModelDocExtension");
        _frameInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IFrame");
        _featureInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IFeature");
        _sketchInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISketch");
        _sketchManagerInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISketchManager");
        _partInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IPartDoc");
        _bodyInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IBody2");
        _entityInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IEntity");
        _faceInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IFace2");
        _surfaceInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISurface");
        _selectionManagerInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISelectionMgr");
        _selectDataInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.ISelectData");
        _assemblyInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IAssemblyDoc");
        _componentInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IComponent2");
        _mathUtilityInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IMathUtility");
        _mathTransformInterface = GetType(interopAssembly, "SolidWorks.Interop.sldworks.IMathTransform");

        var unknown = Marshal.GetIUnknownForObject(application);
        try
        {
            _application = Marshal.GetTypedObjectForIUnknown(unknown, _applicationInterface);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public static SolidWorksInteropBridge Create(object application, Type applicationComType)
    {
        var installDirectory = ResolveInstallDirectory(applicationComType.GUID);
        var interopPath = Path.Combine(installDirectory, "api", "redist", "SolidWorks.Interop.sldworks.dll");
        if (!File.Exists(interopPath))
        {
            throw new FileNotFoundException(
                "未在 SolidWorks 安装目录找到官方 Interop 程序集。",
                interopPath);
        }
        return new SolidWorksInteropBridge(application, Assembly.LoadFrom(interopPath), installDirectory);
    }

    public long GetWindowHandle()
    {
        object? frame = null;
        try
        {
            frame = Invoke(_applicationInterface, _application, "IFrameObject");
            return frame is null
                ? 0
                : Convert.ToInt64(Invoke(_frameInterface, frame, "GetHWndx64"));
        }
        finally
        {
            ComRelease.Final(frame);
        }
    }

    public object? GetImportFileData(string path)
        => Invoke(_applicationInterface, _application, "GetImportFileData", path);

    public object? LoadFile4(string path, string arguments, object? importData, out int errors)
    {
        object?[] parameters = [path, arguments, importData, 0];
        var model = Invoke(_applicationInterface, _application, "LoadFile4", parameters);
        errors = Convert.ToInt32(parameters[3]);
        return model;
    }

    public int GetDocumentType(object model)
        => Convert.ToInt32(Invoke(_modelInterface, model, "GetType"));

    public string GetTitle(object model)
        => Convert.ToString(Invoke(_modelInterface, model, "GetTitle")) ?? string.Empty;

    public object GetExtension(object model)
        => Invoke(_modelInterface, model, "get_Extension")
            ?? throw new InvalidOperationException("SolidWorks 未返回 ModelDocExtension。");

    public bool SaveAs3(
        object extension,
        string path,
        int version,
        int options,
        out int errors,
        out int warnings)
    {
        object?[] parameters = [path, version, options, null, null, 0, 0];
        var saved = Convert.ToBoolean(Invoke(_extensionInterface, extension, "SaveAs3", parameters));
        errors = Convert.ToInt32(parameters[5]);
        warnings = Convert.ToInt32(parameters[6]);
        return saved;
    }

    public void CloseDocument(string title)
        => Invoke(_applicationInterface, _application, "CloseDoc", title);

    public void ExitApplication()
        => Invoke(_applicationInterface, _application, "ExitApp");

    // ---------------- V3.0：装配体创建 ----------------

    public string GetAssemblyTemplate()
    {
        const int defaultAssemblyTemplate = 9;
        var template = Convert.ToString(Invoke(
            _applicationInterface,
            _application,
            "GetUserPreferenceStringValue",
            defaultAssemblyTemplate)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;
        var resolved = Convert.ToString(Invoke(
            _applicationInterface,
            _application,
            "GetDocumentTemplate",
            2, template, 0, 0d, 0d));
        return string.IsNullOrWhiteSpace(resolved) ? template : resolved;
    }

    public object? NewAssembly(string template)
        => Invoke(_applicationInterface, _application, "NewDocument", template, 0, 0d, 0d);

    public string InstallDirectory => _installDirectory;

    // swDocumentTypes_e
    private const int DocumentTypePart = 1;
    private const int DocumentTypeAssembly = 2;

    public object? OpenPart(string path, out int errors, out int warnings)
        => OpenDocument(path, DocumentTypePart, out errors, out warnings);

    /// <summary>V3.3：嵌套装配要把子装配 .SLDASM 也打开，文档类型不能再写死为零件。</summary>
    // swFileLoadError_e.swFileWithSameTitleAlreadyOpen
    private const int FileWithSameTitleAlreadyOpen = 65536;

    /// <summary>
    /// 打开组件文档。已经开着的同一个文件直接复用——SolidWorks 会用
    /// <c>swFileWithSameTitleAlreadyOpen</c> 拒绝重复打开，而这在嵌套装配里是常态：
    /// 上一层已经把某个零件作为组件打开过。**只有路径也相同才复用**；
    /// 同名不同路径必须失败，否则会把别人的几何插进来。
    /// </summary>
    public object? OpenComponentDocument(string path, out int errors, out int warnings)
    {
        var documentType = ConversionPathLayout.HasExtension(path, ConversionArtifactKind.SolidWorksAssembly)
            ? DocumentTypeAssembly
            : DocumentTypePart;
        var model = OpenDocument(path, documentType, out errors, out warnings);
        if (model is not null && errors == 0)
            return model;
        if ((errors & FileWithSameTitleAlreadyOpen) == 0)
            return model;

        var existing = FindOpenDocument(path);
        if (existing is null)
            return model;
        // FindOpenDocument 已按绝对路径匹配，这里再核一次：同名不同路径绝不复用。
        if (!CanReuseOpenDocument(path, Convert.ToString(Invoke(_modelInterface, existing, "GetPathName"))))
            return model;
        errors = 0;
        return existing;
    }

    /// <summary>
    /// 已打开的文档能不能当作本次要打开的那个来用。
    /// **只有路径完全相同才可以**——同名不同路径复用会把别人的几何插进装配。
    /// </summary>
    internal static bool CanReuseOpenDocument(string wanted, string? openPath)
    {
        if (string.IsNullOrWhiteSpace(wanted) || string.IsNullOrWhiteSpace(openPath))
            return false;
        return string.Equals(Path.GetFullPath(wanted), Path.GetFullPath(openPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解释标题占用：会话里已经开着一个同名文档，但它不是我们要的那个文件。
    ///
    /// 借用用户会话时这很常见——用户手工导入过一个同名零件（多半还没保存），
    /// SolidWorks 就会用 <c>swFileWithSameTitleAlreadyOpen</c> 拒绝打开真正的组件。
    /// 返回一句能直接照做的话；判断不出占用者时返回 null，由调用方沿用原始错误码。
    /// </summary>
    public string? DescribeTitleConflict(string path)
    {
        var wantedTitle = Path.GetFileName(path);
        object? document = null;
        try
        {
            document = Invoke(_applicationInterface, _application, "GetFirstDocument");
            while (document is not null)
            {
                var title = Convert.ToString(Invoke(_modelInterface, document, "GetTitle")) ?? string.Empty;
                if (title.Equals(wantedTitle, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileNameWithoutExtension(title).Equals(
                        Path.GetFileNameWithoutExtension(wantedTitle), StringComparison.OrdinalIgnoreCase))
                {
                    var openPath = Convert.ToString(Invoke(_modelInterface, document, "GetPathName")) ?? string.Empty;
                    return string.IsNullOrWhiteSpace(openPath)
                        ? $"SolidWorks 里已经开着一个未保存的同名文档「{title}」，占用了标题。"
                          + "请在 SolidWorks 中关闭它（无需保存）后重跑；不关闭就无法插入真正的组件。"
                        : $"SolidWorks 里已经开着同名但不同文件的文档「{title}」（{openPath}）。"
                          + "请先关闭它再重跑——同名不同路径绝不复用，否则会把别人的几何插进装配。";
                }

                document = Invoke(_modelInterface, document, "GetNext");
            }
        }
        catch
        {
            // 诊断失败不能盖掉真正的错误，交回 null 让调用方用原始错误码。
        }

        return null;
    }

    /// <summary>按绝对路径找一个已经打开的文档。找不到返回 null。</summary>
    public object? FindOpenDocument(string path)
    {
        var target = Path.GetFullPath(path);
        object? document = null;
        try
        {
            document = Invoke(_applicationInterface, _application, "GetFirstDocument");
            while (document is not null)
            {
                var current = Convert.ToString(Invoke(_modelInterface, document, "GetPathName")) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(current)
                    && string.Equals(Path.GetFullPath(current), target, StringComparison.OrdinalIgnoreCase))
                {
                    return document;
                }

                var next = Invoke(_modelInterface, document, "GetNext");
                document = next;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private object? OpenDocument(string path, int documentType, out int errors, out int warnings)
    {
        object?[] parameters = [path, documentType, 1, string.Empty, 0, 0];
        var model = Invoke(_applicationInterface, _application, "OpenDoc6", parameters);
        errors = Convert.ToInt32(parameters[4]);
        warnings = Convert.ToInt32(parameters[5]);
        return model;
    }

    public object GetMathUtility()
        => Invoke(_applicationInterface, _application, "GetMathUtility")
            ?? throw new InvalidOperationException("SolidWorks 未返回 MathUtility。");

    public object? AddComponent(object assembly, string partPath)
        => Invoke(_assemblyInterface, assembly, "AddComponent5", partPath, 0, string.Empty, false, string.Empty, 0d, 0d, 0d);

    public object CreateTransform(object mathUtility, double[] values)
        => Invoke(_mathUtilityInterface, mathUtility, "CreateTransform", values)
            ?? throw new InvalidOperationException("SolidWorks CreateTransform 返回 null。");

    public void SetComponentTransform(object component, object transform)
        => Invoke(_componentInterface, component, "set_Transform2", transform);

    /// <summary>
    /// 读组件的当前变换。
    ///
    /// **不 final 释放返回的 MathTransform**：SolidWorks 对同一组件反复交还同一个 RCW，
    /// <c>FinalReleaseComObject</c> 会把它清零，下一次读就报
    /// "COM object that has been separated from its underlying RCW"。
    /// V3.3 每个组件只读一次所以没暴露；V3.5 的校验回滚要反复读，一读就炸。
    /// </summary>
    public double[] GetComponentTransform(object component)
    {
        var transform = Invoke(_componentInterface, component, "get_Transform2")
            ?? throw new InvalidOperationException("SolidWorks 组件没有 Transform2。");
        var raw = Invoke(_mathTransformInterface, transform, "get_ArrayData") as Array
            ?? throw new InvalidDataException("SolidWorks MathTransform.ArrayData 无效。");
        return raw.Cast<object>().Select(Convert.ToDouble).ToArray();
    }

    public bool SelectComponent(object component, bool append)
        => Convert.ToBoolean(Invoke(_componentInterface, component, "Select4", append, null, false));

    public bool IsComponentFixed(object component)
        => Convert.ToBoolean(Invoke(_componentInterface, component, "IsFixed"));

    public void FixSelectedComponents(object assembly)
        => Invoke(_assemblyInterface, assembly, "FixComponent");

    // ---------------- V3.5：配合重建 ----------------

    public void UnfixSelectedComponents(object assembly)
        => Invoke(_assemblyInterface, assembly, "UnfixComponent");

    /// <summary>swSolidBody = 0。子装配组件返回空，实体在它的子组件里。</summary>
    public IReadOnlyList<object> GetComponentBodies(object component)
    {
        object?[] parameters = [0, false];
        var raw = Invoke(_componentInterface, component, "GetBodies3", parameters) as Array;
        return raw is null ? [] : raw.Cast<object>().Where(item => item is not null).ToArray()!;
    }

    public IReadOnlyList<object> GetComponentChildren(object component)
    {
        var raw = Invoke(_componentInterface, component, "GetChildren") as Array;
        return raw is null ? [] : raw.Cast<object>().Where(item => item is not null).ToArray()!;
    }

    public IReadOnlyList<object> GetBodyFaces(object body)
    {
        var raw = Invoke(_bodyInterface, body, "GetFaces") as Array;
        return raw is null ? [] : raw.Cast<object>().Where(item => item is not null).ToArray()!;
    }

    public object? GetFaceSurface(object face)
        => Invoke(_faceInterface, face, "GetSurface");

    public bool SurfaceIsPlane(object surface)
        => Convert.ToBoolean(Invoke(_surfaceInterface, surface, "IsPlane"));

    public bool SurfaceIsCylinder(object surface)
        => Convert.ToBoolean(Invoke(_surfaceInterface, surface, "IsCylinder"));

    public bool SurfaceIsCone(object surface)
        => Convert.ToBoolean(Invoke(_surfaceInterface, surface, "IsCone"));

    public double[]? GetSurfaceParameters(object surface, string property)
        => Invoke(_surfaceInterface, surface, "get_" + property) as double[];

    public object GetSelectionManager(object model)
        => Invoke(_modelInterface, model, "get_SelectionManager")
            ?? throw new InvalidOperationException("SolidWorks 未返回 SelectionMgr。");

    public int GetSelectedObjectCount(object selectionManager)
        => Convert.ToInt32(Invoke(_selectionManagerInterface, selectionManager, "GetSelectedObjectCount2", -1));

    public int GetSelectedObjectType(object selectionManager, int index)
        => Convert.ToInt32(Invoke(_selectionManagerInterface, selectionManager, "GetSelectedObjectType3", index, -1));

    public int GetSelectedObjectMark(object selectionManager, int index)
        => Convert.ToInt32(Invoke(_selectionManagerInterface, selectionManager, "GetSelectedObjectMark", index));

    /// <summary>
    /// 选中的实体属于哪个组件。为 null 说明这个面没有携带组件上下文——配合会因此被拒。
    ///
    /// **绝不释放返回值**：它就是调用方手里那个组件的同一个 RCW，
    /// 释放掉等于把正在用的组件弄死。诊断代码不能损坏被诊断的对象。
    /// </summary>
    public bool SelectedObjectHasComponent(object selectionManager, int index)
    {
        try
        {
            return Invoke(_selectionManagerInterface, selectionManager, "GetSelectedObjectsComponent4", index, -1)
                is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 零件全部实体的体积与面数。用于在特征识别前后比对几何是否被改变。
    /// 读不出来时返回 null——判定方必须把"量不到"当作不安全，而不是当作一致。
    /// </summary>
    public (double Volume, int FaceCount)? MeasureSolidGeometry(object model)
    {
        try
        {
            var raw = Invoke(_partInterface, model, "GetBodies2", 0 /* swSolidBody */, false) as Array;
            if (raw is null)
                return null;
            var volume = 0d;
            var faces = 0;
            var counted = 0;
            foreach (var item in raw)
            {
                if (item is null)
                    continue;
                if (Invoke(_bodyInterface, item, "GetMassProperties", 1) is not double[] mass || mass.Length < 4)
                    return null;
                volume += mass[3];
                faces += Convert.ToInt32(Invoke(_bodyInterface, item, "GetFaceCount"));
                counted++;
            }

            return counted == 0 ? null : (volume, faces);
        }
        catch
        {
            return null;
        }
    }

    public string GetFeatureTypeName(object feature)
        => Convert.ToString(Invoke(_featureInterface, feature, "GetTypeName2")) ?? string.Empty;

    public IReadOnlyList<FeatureTreeEntry> ReadTopLevelFeatureTree(object model)
    {
        var result = new List<FeatureTreeEntry>();
        var feature = FirstFeature(model);
        while (feature is not null && result.Count < 512)
        {
            object? next = null;
            try
            {
                next = NextFeature(feature);
                result.Add(new FeatureTreeEntry(FeatureName(feature), FeatureTypeName(feature)));
            }
            finally
            {
                ComRelease.Final(feature);
                feature = next;
            }
        }

        return result;
    }

    /// <summary>
    /// 配合要求两侧实体都带 mark=1。实测 <c>IEntity::Select2(append, mark)</c> 虽然返回 true，
    /// 但 <c>AddMate5</c> 仍报 errorStatus=1（IncorrectSelections）——SW 认的是
    /// <c>Select4 + ISelectData.Mark</c> 这条路。
    /// </summary>
    public bool SelectEntityForMate(object model, object entity, bool append, int mark)
    {
        // 不做 final 释放：SelectionMgr 是 SolidWorks 每次交还的同一个对象，
        // FinalReleaseComObject 会把它的 RCW 清零，之后所有调用都报
        // "COM object that has been separated from its underlying RCW"。实测踩过一次。
        var selectionManager = GetSelectionManager(model);
        var selectData = Invoke(_selectionManagerInterface, selectionManager, "CreateSelectData");
        if (selectData is null)
            return false;
        Invoke(_selectDataInterface, selectData, "set_Mark", mark);
        return Convert.ToBoolean(Invoke(_entityInterface, entity, "Select4", append, selectData));
    }

    /// <summary>
    /// AddMate5。两侧实体必须已按 mark=1 选中。
    /// 返回 null 或 errorStatus != 0 都算失败，由调用方回滚。
    /// </summary>
    public object? AddMate(
        object assembly,
        int mateType,
        int align,
        bool flip,
        double distance,
        double angle,
        out int errorStatus)
    {
        object?[] parameters =
        [
            mateType, align, flip,
            distance, distance, distance,
            1d, 1d,
            angle, angle, angle,
            false, false, 0,
            0,
        ];
        var mate = Invoke(_assemblyInterface, assembly, "AddMate5", parameters);
        errorStatus = Convert.ToInt32(parameters[^1]);
        return mate;
    }

    public bool SelectFeature(object model, object feature)
        => Convert.ToBoolean(Invoke(_featureInterface, feature, "Select2", false, 0));

    public void DeleteSelection(object model)
        => Invoke(_modelInterface, model, "EditDelete");

    public bool ForceRebuild(object model)
        => Convert.ToBoolean(Invoke(_modelInterface, model, "ForceRebuild3", false));

    // ---------------- V2.0：特征识别与草图完全定义 ----------------

    public bool GetUserPreferenceToggle(int preference)
        => Convert.ToBoolean(Invoke(_applicationInterface, _application, "GetUserPreferenceToggle", preference));

    public void SetUserPreferenceToggle(int preference, bool value)
        => Invoke(_applicationInterface, _application, "SetUserPreferenceToggle", preference, value);

    /// <summary>FeatureWorks 只作用于活动文档；附着到用户已有会话时导入的文档不一定是活动的。</summary>
    public int ActivateDocument(string title)
    {
        object?[] parameters = [title, false, 0, 0];
        object? activated = null;
        try
        {
            activated = Invoke(_applicationInterface, _application, "ActivateDoc3", parameters);
            return Convert.ToInt32(parameters[3]);
        }
        finally
        {
            // ActivateDoc3 can return the same RCW held by the import loop. Release one COM
            // reference only; FinalReleaseComObject would invalidate the caller's model RCW.
            ComRelease.One(activated);
        }
    }

    public string GetActiveDocumentTitle()
    {
        object? active = null;
        try
        {
            active = Invoke(_applicationInterface, _application, "get_ActiveDoc");
            return active is null ? "(无活动文档)" : GetTitle(active);
        }
        catch (Exception ex)
        {
            return "(读取失败:" + ex.Message + ")";
        }
        finally
        {
            ComRelease.One(active);
        }
    }

    public object? GetAddInObject(string progId)
        => Invoke(_applicationInterface, _application, "GetAddInObject", progId);

    public int LoadAddIn(string path)
        => Convert.ToInt32(Invoke(_applicationInterface, _application, "LoadAddIn", path));

    public int UnloadAddIn(string path)
        => Convert.ToInt32(Invoke(_applicationInterface, _application, "UnloadAddIn", path));

    /// <summary>
    /// FeatureWorks 的 InprocServer32 是相对路径 ".\fworks\fworks.dll"，
    /// 必须拼上 SolidWorks 安装目录。CLSID 来自 FeatureWorks.FeatureWorksApp 的注册。
    /// </summary>
    public string? ResolveFeatureWorksPath()
    {
        const string featureWorksClassId = "{7CF8CA03-1DCE-11d1-A89B-0020AF351FA9}";
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{featureWorksClassId}\InprocServer32");
        if (key?.GetValue(null) is not string registered || string.IsNullOrWhiteSpace(registered))
            return null;
        var server = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
        return Path.IsPathFullyQualified(server)
            ? server
            : Path.GetFullPath(Path.Combine(_installDirectory, server));
    }

    public int RecognizeFeatureAutomatic(object featureWorks, int options)
        => Convert.ToInt32(Invoke(FeatureWorksInterface(), featureWorks, "RecognizeFeatureAutomatic", options));

    public bool CreateFeatures(object featureWorks, short options)
        => Convert.ToBoolean(Invoke(FeatureWorksInterface(), featureWorks, "CreateFeatures", options));

    public bool SetAdvancedOptions(object featureWorks, short options)
        => Convert.ToBoolean(Invoke(FeatureWorksInterface(), featureWorks, "SetAdvancedOptions", options));

    public bool SetPerformanceOptions(object featureWorks, short options)
        => Convert.ToBoolean(Invoke(FeatureWorksInterface(), featureWorks, "SetPerformanceOptions", options));

    public void ClearSelection(object model)
        => Invoke(_modelInterface, model, "ClearSelection2", true);

    public object? FirstFeature(object model)
        => Invoke(_modelInterface, model, "FirstFeature");

    public object? NextFeature(object feature)
        => Invoke(_featureInterface, feature, "GetNextFeature");

    public string FeatureTypeName(object feature)
        => Convert.ToString(Invoke(_featureInterface, feature, "GetTypeName2")) ?? string.Empty;

    public string FeatureName(object feature)
        => Convert.ToString(Invoke(_featureInterface, feature, "get_Name")) ?? string.Empty;

    public bool SelectFeature(object feature)
        => Convert.ToBoolean(Invoke(_featureInterface, feature, "Select2", false, 0));

    public object? SpecificFeature(object feature)
        => Invoke(_featureInterface, feature, "GetSpecificFeature2");

    public object GetSketchManager(object model)
        => Invoke(_modelInterface, model, "get_SketchManager")
            ?? throw new InvalidOperationException("SolidWorks 未返回 SketchManager。");

    public void InsertSketch(object sketchManager, bool updateEditRebuild)
        => Invoke(_sketchManagerInterface, sketchManager, "InsertSketch", updateEditRebuild);

    public bool SelectByID2(object extension, string name, string type, int mark)
        => Convert.ToBoolean(Invoke(
            _extensionInterface,
            extension,
            "SelectByID2",
            name, type, 0d, 0d, 0d, false, mark, null, 0));

    /// <summary>返回值不可用：SDK 明确写 "Not currently defined"，必须靠 GetConstrainedStatus 验收。</summary>
    public void FullyDefineSketch(object sketchManager, int relations)
        => Invoke(
            _sketchManagerInterface,
            sketchManager,
            "FullyDefineSketch",
            true, true, relations, true, 1, null, 1, null, 1, 1);

    public int GetConstrainedStatus(object sketch)
        => Convert.ToInt32(Invoke(_sketchInterface, sketch, "GetConstrainedStatus"));

    private Type FeatureWorksInterface()
    {
        if (_featureWorksInterface is not null)
            return _featureWorksInterface;

        var path = Path.Combine(_installDirectory, "api", "redist", "SolidWorks.Interop.fworks.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "未在 SolidWorks 安装目录找到 FeatureWorks 官方 Interop 程序集。",
                path);
        }

        _featureWorksInterface = GetType(
            Assembly.LoadFrom(path),
            "SolidWorks.Interop.fworks.IFeatureWorksApp");
        return _featureWorksInterface;
    }

    public void Dispose()
        => ComRelease.Final(_application);

    private static object? Invoke(Type interfaceType, object target, string methodName, params object?[]? parameters)
    {
        try
        {
            var method = interfaceType.GetMethod(methodName)
                ?? throw new MissingMethodException(interfaceType.FullName, methodName);
            return method.Invoke(target, parameters);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static Type GetType(Assembly assembly, string name)
        => assembly.GetType(name, throwOnError: true)
            ?? throw new TypeLoadException($"SolidWorks Interop 缺少类型：{name}");

    private static string ResolveInstallDirectory(Guid applicationClassId)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{applicationClassId}}}\LocalServer32");
        var registeredServer = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(registeredServer))
            throw new InvalidOperationException("无法从 COM 注册解析 SolidWorks 安装路径。");

        var executablePath = Environment.ExpandEnvironmentVariables(registeredServer.Trim().Trim('"'));
        return Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("SolidWorks COM 注册路径无效。");
    }
}

internal readonly record struct FeatureTreeEntry(string Name, string TypeName);
