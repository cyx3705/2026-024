using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RelationProbe;

/// <summary>
/// 阶段 B：把一个 .asm 作为独立顶层文档打开并整体导出为 Parasolid，再用 SolidWorks 核验产物。
///
/// 用户已实测：SE 的 GUI 里没有"从父装配导出某个子装配"的入口，但子装配自己打开是可以导出 XT 的。
/// 因此这里一律把 .asm 当独立文档开——顺带也就不存在父变换，几何只可能位于该文档自身坐标系。
/// 本探针的任务是把"只可能"变成实测数字。
/// </summary>
internal static partial class AssemblyExport
{
    private const string ProgId = "SldWorks.Application";
    private const string ProcessName = "SLDWORKS";
    private const int HeaderProbeBytes = 4096;

    [GeneratedRegex(@"FORMAT\s*=\s*([A-Za-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FormatPattern();

    public static ExportCheck Run(
        SolidEdgeSession session,
        string assemblyPath,
        string outputDirectory,
        bool verifyWithSolidWorks,
        List<string> notes)
    {
        var check = new ExportCheck { SourceAssembly = Path.GetFullPath(assemblyPath) };
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(assemblyPath) + ".x_t");
        if (File.Exists(outputPath))
        {
            check.Error = $"探针拒绝覆盖已存在的导出产物：{outputPath}";
            return check;
        }

        object? document = null;
        try
        {
            document = session.OpenDocument(check.SourceAssembly);
            var occurrences = SolidEdgeProbe.CollectOccurrences(document, notes);
            var leaves = occurrences.Where(item => !item.IsSubAssembly && item.Origin.Length == 3).ToList();
            check.LeafOccurrenceCount = occurrences.Count(item => !item.IsSubAssembly);
            check.OccurrenceOrigins = leaves.Select(item => item.Origin).ToList();

            check.ExportMember = TryExport(document, outputPath, check.Attempts);
            if (check.ExportMember is null)
            {
                check.Error = "没有任何导出成员成功产出 .x_t，详见 Attempts。";
                return check;
            }
        }
        catch (Exception ex)
        {
            check.Error = ObjectDumper.Describe(ex);
            return check;
        }
        finally
        {
            if (document is not null)
                session.CloseDocument(document);
        }

        if (!File.Exists(outputPath))
        {
            check.Error = $"导出成员 {check.ExportMember} 返回成功但文件不存在：{outputPath}";
            return check;
        }

        check.OutputPath = outputPath;
        check.OutputBytes = new FileInfo(outputPath).Length;
        check.ParasolidFormat = ReadParasolidFormat(outputPath);

        if (!verifyWithSolidWorks)
        {
            notes.Add("未启用 --sw-verify，跳过 SolidWorks 体数与坐标系核验。");
            return check;
        }

        try
        {
            VerifyWithSolidWorks(check, notes);
        }
        catch (Exception ex)
        {
            check.Error = "SolidWorks 核验失败：" + ObjectDumper.Describe(ex);
        }

        return check;
    }

    /// <summary>
    /// 依次尝试可能的导出成员。SaveCopyAs 优先：它不会把打开的文档重绑到新路径，
    /// 比 SaveAs 更不容易污染源文件。参数个数未知，1 参与补 Missing 的多参形式都试。
    /// </summary>
    private static string? TryExport(object document, string outputPath, List<string> attempts)
    {
        var (_, _, members) = TypeLibrary.Describe(document);
        var available = members
            .Where(member => member.Kind == "method")
            .Select(member => member.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        attempts.Add("文档上可用的 SaveXxx 成员：" + string.Join(", ", available
            .Where(name => name.StartsWith("Save", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)));

        foreach (var member in new[] { "SaveCopyAs", "SaveAs" })
        {
            if (available.Count > 0 && !available.Contains(member))
            {
                attempts.Add($"{member}：类型库里没有这个成员，跳过。");
                continue;
            }

            foreach (var extraArguments in new[] { 0, 8 })
            {
                var args = new object?[1 + extraArguments];
                args[0] = outputPath;
                for (var index = 1; index < args.Length; index++)
                    args[index] = Type.Missing;

                try
                {
                    Com.Invoke(document, member, args);
                    if (File.Exists(outputPath))
                    {
                        attempts.Add($"{member}（{args.Length} 参）：成功。");
                        return member;
                    }

                    attempts.Add($"{member}（{args.Length} 参）：调用未抛异常但没有产出文件。");
                }
                catch (Exception ex)
                {
                    attempts.Add($"{member}（{args.Length} 参）：{ObjectDumper.Describe(ex)}");
                }
            }
        }

        return null;
    }

    private static string? ReadParasolidFormat(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(HeaderProbeBytes, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);
            var header = Encoding.ASCII.GetString(buffer, 0, read);
            var match = FormatPattern().Match(header);
            return match.Success ? match.Groups[1].Value : "<文件头里没有 FORMAT=，很可能是二进制>";
        }
        catch (Exception ex)
        {
            return "<读取失败：" + ex.Message + ">";
        }
    }

    private static void VerifyWithSolidWorks(ExportCheck check, List<string> notes)
    {
        var xtPath = check.OutputPath
            ?? throw new InvalidOperationException("导出产物路径为空，无法核验。");
        var before = GetPids();
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false)
            ?? throw new InvalidOperationException("SldWorks.Application 未注册。");
        var application = (ISldWorks)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("SolidWorks COM 返回空实例。"));
        var owns = GetPids().Except(before).Count() == 1;
        notes.Add(owns
            ? "本探针新建了 SolidWorks 进程，核验后会退出它。"
            : "附着到已有的 SolidWorks 会话，核验后不会退出它，也不修改其可见性。");

        IModelDoc2? model = null;

        // SE 导出的 .x_t 带着 Parasolid 装配结构，SolidWorks 默认按装配体导入。
        // 子装配整体 XT 的全部意义是"先不做嵌套"，所以要把它压成多体零件：
        //   swImportMultBodyAsPartData        多体作为零件数据导入
        //   swImportDissolveTopLevelAssembly  打开时溶解顶层装配
        // 与 FeatureRecognizer 处理英文特征名同一原则：临时改、必恢复、不留痕。
        var toggles = new[]
        {
            (Id: (int)swUserPreferenceToggle_e.swImportMultBodyAsPartData, Desired: true),
            (Id: (int)swUserPreferenceToggle_e.swImportDissolveTopLevelAssemblyOnOpen, Desired: true),
        };
        var originals = new Dictionary<int, bool>();
        try
        {
            if (owns)
            {
                application.Visible = false;
                application.UserControl = false;
            }

            foreach (var toggle in toggles)
            {
                originals[toggle.Id] = application.GetUserPreferenceToggle(toggle.Id);
                application.SetUserPreferenceToggle(toggle.Id, toggle.Desired);
            }

            check.ImportMultiBodyAsPartOriginal =
                originals[(int)swUserPreferenceToggle_e.swImportMultBodyAsPartData];
            check.ImportDissolveAssemblyOriginal =
                originals[(int)swUserPreferenceToggle_e.swImportDissolveTopLevelAssemblyOnOpen];
            check.ImportIgnoreHiddenOriginal = application.GetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swImportIgnoreHiddenEntities);

            var errors = 0;
            var importData = application.GetImportFileData(xtPath);
            model = application.LoadFile4(xtPath, "r", importData, ref errors) as IModelDoc2;
            if (model is null || errors != 0)
                throw new InvalidOperationException($"LoadFile4 失败：errors={errors}");

            // 多体 Parasolid 在 SolidWorks 里落地成零件还是装配体，取决于用户的导入选项，
            // 不能假定。先问文档类型，再决定怎么数——强转 IPartDoc 在装配体上是 E_NOINTERFACE。
            var documentType = model.GetType();
            notes.Add($"导入返回：运行时类型 {((object)model).GetType().FullName}，"
                + $"GetType()={documentType}，标题={TryText(() => model.GetTitle())}，"
                + $"路径={TryText(() => model.GetPathName())}");
            check.SolidWorksDocumentType = documentType switch
            {
                (int)swDocumentTypes_e.swDocPART => "零件",
                (int)swDocumentTypes_e.swDocASSEMBLY => "装配体",
                (int)swDocumentTypes_e.swDocDRAWING => "工程图",
                _ => $"未知({documentType})",
            };

            if (documentType == (int)swDocumentTypes_e.swDocPART)
                ReadBodies(model, check);
            else if (documentType == (int)swDocumentTypes_e.swDocASSEMBLY)
                ReadComponents(model, check, notes);

            check.BodyCountMatches = check.SolidWorksBodyCount == check.LeafOccurrenceCount;
            ComputeMeanOffset(check);
        }
        finally
        {
            check.ImportPreferenceRestored = originals.Count > 0;
            foreach (var (id, original) in originals)
            {
                try
                {
                    application.SetUserPreferenceToggle(id, original);
                    if (application.GetUserPreferenceToggle(id) != original)
                        check.ImportPreferenceRestored = false;
                }
                catch
                {
                    check.ImportPreferenceRestored = false;
                }
            }

            if (model is not null)
            {
                try { application.CloseDoc(model.GetTitle()); } catch { }
                ObjectDumper.Release(model);
            }

            if (owns)
            {
                try { application.ExitApp(); } catch { }
            }

            ObjectDumper.Release(application);
        }
    }

    /// <summary>落地为多体零件：数实体并取每个实体的包围盒中心。</summary>
    private static void ReadBodies(IModelDoc2 model, ExportCheck check)
    {
        var raw = Com.Invoke(model, "GetBodies2", (int)swBodyType_e.swSolidBody, false);
        if (raw is not object[] bodies)
            return;
        check.SolidWorksBodyCount = bodies.Length;
        foreach (var item in bodies)
        {
            if (Com.Invoke(item, "GetBodyBox") is not double[] box || box.Length != 6)
                continue;
            check.BodyCenters.Add([(box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2]);
        }
    }

    /// <summary>落地为装配体：数组件，并按组件的包围盒中心参与坐标系核对。</summary>
    private static void ReadComponents(IModelDoc2 model, ExportCheck check, List<string> notes)
    {
        // 导入产生的是未保存的虚拟装配，IAssemblyDoc.GetComponents 在这种文档上返回 null；
        // 必须从活动配置的根组件往下遍历。
        var components = new List<object>();
        try
        {
            var root = model.ConfigurationManager.ActiveConfiguration.GetRootComponent3(true);
            CollectChildren(root, components);
        }
        catch (Exception ex)
        {
            notes.Add("根组件遍历失败：" + ObjectDumper.Describe(ex));
        }

        if (components.Count == 0)
        {
            var fallback = Com.TryGetProperty(model, "GetComponents");
            notes.Add($"导入落地为装配体但没有遍历到组件（GetComponents 实得 {fallback?.GetType().FullName ?? "null"}）。");
            return;
        }

        check.SolidWorksComponentCount = components.Count;
        check.SolidWorksBodyCount = components.Count;
        foreach (var component in components)
        {
            if (Com.Invoke(component, "GetBox", false, false) is not double[] box || box.Length != 6)
                continue;
            check.BodyCenters.Add([(box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2]);
        }
    }

    /// <summary>递归收集叶组件。虚拟装配里子装配同样是组件，只有叶节点参与体数比对。</summary>
    private static void CollectChildren(object? component, List<object> leaves)
    {
        if (component is null)
            return;
        if (Com.Invoke(component, "GetChildren") is not object[] children || children.Length == 0)
        {
            leaves.Add(component);
            return;
        }

        foreach (var child in children)
            CollectChildren(child, leaves);
    }

    /// <summary>
    /// 体包围盒中心的均值减去 occurrence 原点的均值。
    ///
    /// 两者不是同一个量（盒中心不是零件原点），所以不要求它为 0；它的作用是**证伪**：
    /// 若导出几何被整体搬到了别的坐标系，所有体会同向平移同一个向量，这个均值差会跟着整体跳变。
    /// 数量级落在零件尺度内即认为坐标系判断成立，出现远大于装配包围盒的偏移就是坐标系错了。
    /// </summary>
    private static void ComputeMeanOffset(ExportCheck check)
    {
        if (check.BodyCenters.Count == 0 || check.OccurrenceOrigins.Count == 0)
            return;
        var offset = new double[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var bodyMean = check.BodyCenters.Average(center => center[axis]);
            var originMean = check.OccurrenceOrigins.Average(origin => origin[axis]);
            offset[axis] = bodyMean - originMean;
        }

        check.MeanOffset = offset;
        check.MeanOffsetMagnitude = Math.Sqrt(offset.Sum(value => value * value));
    }

    private static string TryText(Func<string?> getter)
    {
        try { return getter() ?? "<null>"; }
        catch (Exception ex) { return "<失败:" + ex.Message + ">"; }
    }

    private static int[] GetPids()
    {
        try { return Process.GetProcessesByName(ProcessName).Select(process => process.Id).OrderBy(id => id).ToArray(); }
        catch { return []; }
    }
}
