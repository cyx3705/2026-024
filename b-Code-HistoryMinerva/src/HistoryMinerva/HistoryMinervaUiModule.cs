using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryMinerva.Bom;
using HistoryMinerva.Contracts;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace HistoryMinerva;

public sealed class HistoryMinervaUiModule : IModuleContextAware, IDisposable
{
    private IModuleContext? _context;
    private MappingRuntimePaths? _runtimePaths;
    private AssemblyViewModel? _viewModel;

    private static readonly ParameterSpec[] PageContentParameters =
    [
        new ParameterSpec
        {
            Name = "content",
            Description = "页面当前转换内容",
            Required = false,
        },
    ];

    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null)
            throw new InvalidOperationException("Minerva UI 宿主上下文已注入。");

        _context = context;
        _runtimePaths = MappingRuntimePaths.FromEnvironment(context.Environment);

        // Must register during Attach. Vulcan FinalizeMetas runs before CreateUi
        // injects ShellUi, so gating on _shellUi drops conversion commands after
        // reload and the page reports 未知指令.
        context.RegisterCommands(registry =>
        {
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.probe",
                CommandClass = "conversion",
                Summary = "通过命令总线探查当前选择的 Minerva 装配体",
                Example = "minerva.conversion.probe",
                Readonly = true,
                RequiresUiThread = true,
                Parameters = PageContentParameters,
                Handler = ProbeCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.run",
                CommandClass = "conversion",
                Summary = "通过命令总线执行当前选择的 Minerva 转换",
                Example = "minerva.conversion.run",
                RequiresUiThread = true,
                Parameters = PageContentParameters,
                Handler = ConvertCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.cancel",
                CommandClass = "conversion",
                Summary = "取消当前 Minerva 转换或探查",
                Example = "minerva.conversion.cancel",
                RequiresUiThread = true,
                Handler = CommandDescriptor.Sync(CancelCurrent),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.describe",
                CommandClass = "ui",
                Summary = "返回 Minerva Aurora 描述式页面",
                Readonly = true,
                HiddenReason = "Aurora 页面描述内部协议，不对远程消费面暴露",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(DescribePage())),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.data",
                CommandClass = "ui",
                Summary = "返回 Minerva 页面组件所需数据",
                Readonly = true,
                HiddenReason = "Aurora 页面取数内部协议，不对远程消费面暴露",
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(Data),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.actions",
                CommandClass = "ui",
                Summary = "声明 Minerva 页面动作",
                Readonly = true,
                HiddenReason = "Aurora 页面动作内部协议，不对远程消费面暴露",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(ActionsJson)),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.source",
                CommandClass = "ui",
                Summary = "设置 Minerva 转换来源路径",
                RequiresUiThread = true,
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(SetSource),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.picksource",
                CommandClass = "ui",
                Summary = "按当前转换内容选择装配体文件或零件文件夹",
                RequiresUiThread = true,
                AllowUnspecifiedParameters = true,
                HiddenReason = "Aurora 来源选择器内部协议，不对远程消费面暴露",
                Handler = CommandDescriptor.Sync(PickSource),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.content",
                CommandClass = "ui",
                Summary = "设置 Minerva 转换内容",
                RequiresUiThread = true,
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(SetContent),
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.options",
                CommandClass = "ui",
                Summary = "设置 Minerva 转换选项",
                RequiresUiThread = true,
                AllowUnspecifiedParameters = true,
                Handler = SetOptionAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.property",
                CommandClass = "ui",
                Summary = "统一设置批量改名与打包表的件别",
                RequiresUiThread = true,
                AllowUnspecifiedParameters = true,
                HiddenReason = "Aurora 件别内部协议，不对远程消费面暴露",
                Handler = SetPropertyAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.cell",
                CommandClass = "ui",
                Summary = "修改零件表里某一行的名称或件别",
                RequiresUiThread = true,
                AllowUnspecifiedParameters = true,
                HiddenReason = "Aurora 单元格编辑内部协议，不对远程消费面暴露",
                Handler = EditCellAsync,
            });
        });
    }

    private AssemblyViewModel GetViewModel()
    {
        if (_viewModel is null)
        {
            if (_context is null || _runtimePaths is null)
                throw new InvalidOperationException("Minerva 模块尚未装配。");
            _viewModel = new AssemblyViewModel(_runtimePaths)
            {
                // V4.10.4：打包时经总线借用 HistoryApollo 查外购件品牌。Apollo 装没装、配没配密钥，
                // 要到发出那一行指令时才知道——不在就是失败回执，那一格写 N/A，打包照常完成。
                BrandLookup = _context.Bus is { } bus ? PurchasedBrandLookup.OverBus(bus) : null,
            };
            // V4.10.5：打包开关记在本机，页面实例重建（热重载、重启）后保持上次选择（V4.16 起六个）。
            _viewModel.SetPackageSteps(PackageOptions.Load(PackageOptionsPath));
        }

        return _viewModel;
    }

    public void Dispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        _context = null;
        _runtimePaths = null;
    }

    private async Task<CommandResult> ProbeCurrentAsync(CommandContext command)
    {
        var contentError = ApplyPageContent(command);
        if (contentError is not null)
            return contentError;

        var viewModel = GetViewModel();
        if (!viewModel.CanProbe)
            return CommandResult.Fail(viewModel.StatusText);

        var result = await ConversionCommandHandlers.ProbeAsync(viewModel, command).ConfigureAwait(true);
        // 解析出来的零件行要立刻出现在表里。
        await RefreshPropertyPrepTableAsync(command).ConfigureAwait(true);
        return result;
    }

    private async Task<CommandResult> ConvertCurrentAsync(CommandContext command)
    {
        var contentError = ApplyPageContent(command);
        if (contentError is not null)
            return contentError;

        var viewModel = GetViewModel();
        if (!viewModel.CanConvert)
        {
            return CommandResult.Fail(
                viewModel.IsRenameMode ? viewModel.RenameBlockedReason
                    : viewModel.IsPackMode ? viewModel.PackBlockedReason
                    : viewModel.StatusText);
        }

        var result = await ConversionCommandHandlers.ConvertAsync(viewModel, command).ConfigureAwait(true);
        // 写入之后索引已经挪到新文件名上（见 AssemblyViewModel.ReindexAfterRename）。
        // 不刷这一下，表里显示的还是一批已经不存在的旧文件。
        await RefreshPropertyPrepTableAsync(command).ConfigureAwait(true);
        return result;
    }

    /// <summary>
    /// Aurora 转换内容下拉默认只改选择通道。选来源、解析、改名必须先套用页面当前内容，
    /// 否则批量改名仍按默认零件文件夹去选目录。
    /// </summary>
    private CommandResult? ApplyPageContent(CommandContext command)
    {
        var content = command.GetString("content")?.Trim();
        if (string.IsNullOrWhiteSpace(content) || content.StartsWith('{'))
            return null;

        var result = SetContent(command);
        return result.Success ? null : result;
    }

    private CommandResult CancelCurrent(CommandContext _)
    {
        var viewModel = _viewModel;
        if (viewModel is null)
            return CommandResult.Fail("Minerva 页面尚未创建。");
        // 没有在跑的操作时点取消，结果就是"什么都不用做"，不是失败：
        // 报失败会在控制台留一条红字，还会让宿主把控制台抢到前面（V4.15）。
        return viewModel.Cancel()
            ? CommandResult.Ok("已请求取消 Minerva 当前操作。")
            : CommandResult.Ok("Minerva 当前没有进行中的操作，无需取消。");
    }

    private CommandResult PickSource(CommandContext command)
    {
        try
        {
            var contentError = ApplyPageContent(command);
            if (contentError is not null)
                return CommandResult.Ok("未选择来源：" + contentError.Message);

            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                ?? Application.Current?.MainWindow;
            var picked = GetViewModel().SelectedMappingContent.IsAssemblySource
                ? SourcePickDialog.ShowFile(owner)
                : SourcePickDialog.ShowFolder(owner);
            return picked is null
                ? CommandResult.Ok("已取消选择")
                : CommandResult.Ok("已选择来源", picked);
        }
        catch (Exception ex)
        {
            return CommandResult.Ok("未选择来源：" + ex.Message);
        }
    }

    private CommandResult SetSource(CommandContext command)
    {
        var contentError = ApplyPageContent(command);
        if (contentError is not null)
            return CommandResult.Ok("未更改来源：" + contentError.Message);

        var path = command.GetString("path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return CommandResult.Ok("来源路径为空，未更改。");

        try
        {
            GetViewModel().SetSourcePath(path);
            return CommandResult.Ok("已设置 Minerva 转换来源。");
        }
        catch (Exception ex)
        {
            return CommandResult.Ok("未更改来源：" + ex.Message);
        }
    }

    private CommandResult SetContent(CommandContext command)
    {
        var value = command.GetString("content")?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return CommandResult.Fail("缺少 content");

        var option = MappingContentOption.Available.FirstOrDefault(item =>
            string.Equals(item.DisplayName, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Kind.ToString(), value, StringComparison.OrdinalIgnoreCase));
        if (option is null && int.TryParse(value, out var index)
            && index >= 0 && index < MappingContentOption.Available.Count)
            option = MappingContentOption.Available[index];
        if (option is null)
            return CommandResult.Fail("未知转换内容");

        GetViewModel().SelectedMappingContent = option;
        return CommandResult.Ok("已设置 Minerva 转换内容。");
    }

    private CommandResult SetOption(CommandContext command)
    {
        var option = command.GetString("option")?.Trim().ToLowerInvariant();
        var value = command.GetString("value")?.Trim();
        if (string.IsNullOrWhiteSpace(option))
            return CommandResult.Fail("需要 option");
        if (!string.Equals(option, "prefix", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(value))
            return CommandResult.Fail("需要 option 和 value");

        var model = GetViewModel();
        try
        {
            switch (option)
            {
                case "recognize":
                    model.RecognizeFeatures = ParseSwitch(value ?? string.Empty);
                    break;
                case "continue":
                    model.ContinueWhenPartFails = ParseSwitch(value ?? string.Empty);
                    break;
                case "mates":
                    model.RebuildMates = ParseSwitch(value ?? string.Empty);
                    break;
                case "prefix":
                    model.DrawingPrefix = value ?? string.Empty;
                    break;
                case var step when PackageOptions.Keys.ContainsKey(step):
                    var enabled = ParseSwitch(value ?? string.Empty);
                    var steps = PackageOptions.With(model.PackageSteps, step, enabled);
                    model.SetPackageSteps(steps);
                    var label = $"打包步骤「{PackageStepLabel(step)}」已{(enabled ? "开启" : "关闭")}";
                    try
                    {
                        PackageOptions.Save(PackageOptionsPath, steps);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 记不住只影响下次打开页面时的初值，本轮开关照样生效；报出来，不吞掉。
                        return CommandResult.Ok($"{label}，但未能保存到本机：{ex.Message}");
                    }

                    return CommandResult.Ok(step == "brandai"
                        ? enabled
                            ? "AI 查品牌已开启：打包时用 DeepSeek 联网搜索外购件品牌，文件名里没有名称的顺带查名称（按次计费）。"
                            : "AI 查品牌已关闭：打包不调用 DeepSeek、不搜索，品牌列与外购件清单 H 列留空。"
                        : label + "。");
                default:
                    return CommandResult.Fail("未知转换选项；支持 recognize、continue、mates、prefix、machinedbom、purchasedbom、brandai、step、dwg、pdf");
            }
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.Fail(ex.Message);
        }

        return CommandResult.Ok("已更新 Minerva 转换选项。");
    }

    /// <summary>
    /// 设置选项，然后按需刷表。
    ///
    /// 图号前缀是唯一会改变表格内容的选项：「文件」列显示的就是改完之后的名字，
    /// 不刷这一下，用户填完前缀看到的还是一整表旧文件名。
    /// </summary>
    private async Task<CommandResult> SetOptionAsync(CommandContext command)
    {
        var result = SetOption(command);
        if (result.Success)
            await RefreshPropertyPrepTableAsync(command).ConfigureAwait(true);
        return result;
    }

    private static bool ParseSwitch(string value)
        => value.Equals("开启", StringComparison.OrdinalIgnoreCase)
            || value.Equals("开", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 零件表那张表的节点 id。改属性以后要按节点刷它，否则界面停在旧值上。
    ///
    /// 每种转换内容各有自己的表节点（Aurora 的 <c>switch</c> 分支各建一份），
    /// 刷别人的节点是越权，而且刷的还是一个当下并不存在的节点。
    /// </summary>
    private const string PropertyPartsTableNode = "sw-property-parts";

    /// <inheritdoc cref="PropertyPartsTableNode"/>
    private const string PackagePartsTableNode = "sw-package-parts";

    /// <summary>统一设置件别。</summary>
    private async Task<CommandResult> SetPropertyAsync(CommandContext command)
    {
        var field = command.GetString("field")?.Trim().ToLowerInvariant();
        var value = command.GetString("value")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(field))
            return CommandResult.Fail("需要 field");

        var model = GetViewModel();
        string message;
        switch (field)
        {
            case "kind":
                // V4.12 统一设置件别。「件别」是框的总状态：读数，不是动作。
                if (!PartKinds.TryParseLabel(value, out var kind))
                    return CommandResult.Ok(value.Length == 0 || value == PartKinds.BoxPlaceholder
                        ? "件别保持每行各自的值；要改某一行，点那一行的件别格。"
                        : $"未知件别「{value}」；支持 机加件、外购件、排除。");
                if (model.SetAllPartKinds(kind, out var skipped, out var kindReason) is not { } kindChanged)
                    return CommandResult.Ok(kindReason);
                message = $"已把 {kindChanged} 行的件别统一为{PartKinds.Label(kind)}"
                    + (skipped > 0 ? $"；{skipped} 行改不成（「{ConversionPathLayout.ReferencePartsDirectoryName}」目录下的件，或子文件夹里的装配体），保持原样" : string.Empty);
                // 件别一变两张表都重算，刷整表并把底下各框对齐（同点件别格）。
                await RefreshPropertyPrepTableAsync(command).ConfigureAwait(true);
                return CommandResult.Ok(message);
            default:
                return CommandResult.Fail("未知字段；支持 kind");
        }

    }

    /// <summary>点单元格改一行的名称或件别。取消时命令仍然成功——失败指令会让宿主抢控制台（REQ-002）。</summary>
    private async Task<CommandResult> EditCellAsync(CommandContext command)
    {
        var fieldText = command.GetString("field")?.Trim().ToLowerInvariant();
        var rowId = command.GetString("id")?.Trim();
        if (string.IsNullOrWhiteSpace(fieldText) || string.IsNullOrWhiteSpace(rowId))
            return CommandResult.Fail("需要 field 和 id");
        if (fieldText is not ("name" or "kind"))
            return CommandResult.Fail("未知列；单元格只支持 name、kind");

        var bus = _context?.Bus;
        if (bus is null)
            return CommandResult.Fail("Minerva 尚未接入命令总线。");

        if (fieldText == "name")
            return await EditNameAsync(bus, command, rowId).ConfigureAwait(true);
        return await CycleKindAsync(command, rowId).ConfigureAwait(true);
    }

    /// <summary>
    /// 点「名称」格改这一行的名字。
    ///
    /// 用 <c>prompt</c> 而不是 <c>choice</c>：名称是自由文本，没有候选可言。
    /// 预填当前值，用户改的是那一段而不是从空白重打一遍。
    /// 取消时命令仍然成功——失败指令会让宿主抢控制台并重排停靠（REQ-002）。
    /// </summary>
    private async Task<CommandResult> EditNameAsync(ICommandBus bus, CommandContext command, string rowId)
    {
        var model = GetViewModel();
        if (!model.RenamesFile(rowId))
            return CommandResult.Ok("这一行不改名（标准件/外购件内部），名称未修改。");

        var dialog = await bus.ExecuteAsync(
            "aurora.ui.dialog kind=prompt"
                + " title=名称"
                + " body=" + CommandParser.QuoteArg("文件名是「图号 名称」，这里改的是空格后面那一段")
                + " value=" + CommandParser.QuoteArg(model.GetPartName(rowId))
                + " cancel=取消",
            command.Source,
            command.Cancellation).ConfigureAwait(true);
        if (!dialog.Success)
            return CommandResult.Ok("未修改名称。");

        if (!model.SetPartName(rowId, DialogValue(dialog), out var error))
            return CommandResult.Ok(error);

        await RefreshPartsTableAsync(command).ConfigureAwait(true);
        return CommandResult.Ok($"名称已改为 {model.GetPartName(rowId)}");
    }

    /// <summary>
    /// V4.11：点件别格，机加件 → 外购件 → 排除 轮换。手感照 Janus「操作」格——点了就改，不弹窗。
    ///
    /// 件别一变，批量改名的编号和打包的清单都要重算，所以刷的是整张表而不只是这一格；
    /// 底下的件别框要跟着对齐。
    /// 改不了（没解析、正在执行、参考部件目录）时命令仍然成功，只把原因说出来（REQ-002）。
    /// </summary>
    private async Task<CommandResult> CycleKindAsync(CommandContext command, string rowId)
    {
        var model = GetViewModel();
        var name = model.FindRow(rowId) is { } row ? Path.GetFileName(row.SourcePath) : rowId;
        if (model.CyclePartKind(rowId, out var reason) is not { } next)
            return CommandResult.Ok(reason);

        await RefreshPropertyPrepTableAsync(command).ConfigureAwait(true);
        return CommandResult.Ok($"{name} 的件别已改为{PartKinds.Label(next)}");
    }

    /// <summary>把底下的件别框和图号前缀对齐到表格现状（<c>aurora.ui.panelset</c>，程序回写不触发控件提交）。</summary>
    private async Task SyncPropertyPanelAsync(CommandContext command)
    {
        var bus = _context?.Bus;
        if (bus is null || _viewModel is not { } model)
            return;

        if (model.IsPackMode)
        {
            _ = await bus.ExecuteAsync(
                "aurora.ui.panelset panel=" + PackagePanelNode
                    + " control=batch-kind"
                    + " value=" + CommandParser.QuoteArg(model.KindBoxText()),
                command.Source,
                command.Cancellation).ConfigureAwait(true);
            return;
        }

        if (!model.IsRenameMode)
            return;

        foreach (var (control, value) in new[]
                 {
                     ("batch-kind", model.KindBoxText()),
                     ("prefix", model.DrawingPrefix),
                 })
        {
            _ = await bus.ExecuteAsync(
                "aurora.ui.panelset panel=" + PropertyPanelNode
                    + " control=" + control
                    + " value=" + CommandParser.QuoteArg(value),
                command.Source,
                command.Cancellation).ConfigureAwait(true);
        }
    }

    /// <summary>批量改名那一排框所在的面板 id。回写要按它定位。</summary>
    private const string PropertyPanelNode = "sw-property-options";

    /// <summary>整体打包底下那排控件所在的面板 id（V4.12 件别框）。</summary>
    private const string PackagePanelNode = "sw-package-options";

    /// <summary>
    /// 单元格动作是 Aurora 直接打到总线的，表格不会自己重取。不显式刷这一格，
    /// 用户看到的还是改之前的值——而旧值与新值长得一模一样地都是「一个字符串」。
    /// </summary>
    private async Task RefreshPartsTableAsync(CommandContext command)
    {
        var bus = _context?.Bus;
        if (bus is null)
            return;
        var node = _viewModel is { IsPackMode: true } ? PackagePartsTableNode : PropertyPartsTableNode;
        _ = await bus.ExecuteAsync(
            "aurora.ui.refreshdata node=" + node,
            command.Source,
            command.Cancellation).ConfigureAwait(true);
    }

    /// <summary>
    /// 刷批量改名与整体打包那两张表。另外三种转换内容的表由 Aurora 自己按
    /// <c>dataSource</c> 取，不需要也不允许在这里代刷（见 <see cref="PropertyPartsTableNode"/>）。
    /// </summary>
    private async Task RefreshPropertyPrepTableAsync(CommandContext command)
    {
        if (_viewModel is not { IsRenameMode: true } and not { IsPackMode: true })
            return;
        await RefreshPartsTableAsync(command).ConfigureAwait(true);
        // 表一变，底下那排框的读数就可能变了：解析后前缀是刚认出来的。
        await SyncPropertyPanelAsync(command).ConfigureAwait(true);
    }

    private static string DialogValue(CommandResult result) => result.Data switch
    {
        string value => value.Trim(),
        _ => result.Message.Trim(),
    };

    private static string PackageStepLabel(string option) => option switch
    {
        "machinedbom" => "机加单",
        "purchasedbom" => "外购单",
        "brandai" => "AI 查品牌",
        "step" => "STEP",
        "dwg" => "DWG",
        "pdf" => "PDF",
        _ => option,
    };

    /// <summary>打包开关的本机存档位置（V4.10.5）。</summary>
    private string PackageOptionsPath
        => PackageOptions.PathIn(
            (_runtimePaths ?? throw new InvalidOperationException("Minerva 模块尚未装配。")).ModuleDataDirectory);

    /// <summary>
    /// 页面描述里只有六个打包开关的初值是活的：页面在就取页面上的，否则取本机记下的上次选择。
    /// 其余部分是常量；开关拨动之后由控件自己持有状态，不需要再重发描述。
    /// 占位写成 <c>__PKG_开关名__</c>。
    /// </summary>
    private string DescribePage()
    {
        var steps = _viewModel?.PackageSteps
            ?? (_runtimePaths is null ? PackageSteps.All : PackageOptions.Load(PackageOptionsPath));
        var json = DescribeJson;
        foreach (var option in PackageOptions.Keys.Keys)
        {
            json = json.Replace(
                $"__PKG_{option.ToUpperInvariant()}__",
                PackageOptions.Get(steps, option) ? "true" : "false",
                StringComparison.Ordinal);
        }

        return json;
    }

    private CommandResult Data(CommandContext command)
    {
        var view = command.GetString("view")?.Trim().ToLowerInvariant();
        var model = _viewModel;
        return view switch
        {
            "parts" => CommandResult.Ok("Minerva 零件", PartRows(model)),
            "content" => CommandResult.Ok("Minerva 转换内容", MappingContentOption.Available
                .Select((item, index) => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["value"] = item.DisplayName,
                    ["index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }).ToList()),
            _ => CommandResult.Fail("未知 view；支持 parts、content"),
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> PartRows(AssemblyViewModel? viewModel)
    {
        if (viewModel is not { } model)
            return [];

        return model.Parts.Select(row => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            // id 是隐藏字段：单元格动作按被点那一行的同名字段取值，不读当前选中行。
            ["id"] = row.Id,
            // 批量改名下 DisplayName 给的是**改完之后**的文件名，不再另开一列「改名后预览」：
            // 同一个东西占两列，而改名成功之后「文件」那一列显示的还是一个已经不存在的文件。
            ["file"] = row.DisplayName,
            // V4.9：批量改名不再显示「文件」，而是把它拆成图号与名称两列——
            // 文件名永远是这两段拼出来的，拆开显示才改得动其中一段。
            ["drawing"] = row.DrawingText,
            ["name"] = row.PartName,
            ["status"] = row.Status,
            ["detail"] = row.Detail,
            ["features"] = row.FeatureText,
            ["sketches"] = row.SketchText,
            // V4.10 整体打包三列。打包表里「drawing」那一列对机加件是图号、
            // 对外购件是规格——两者在各自那张 BOM 上占的正是同一格。
            ["quantity"] = row.QuantityText,
            ["hasdrawing"] = row.DrawingStateText,
            ["category"] = row.CategoryText,
            // V4.10.4：外购件品牌，打包时联网查询；机加件与没查过的行为空。
            ["brand"] = row.BrandText,
        }).ToList();
    }

    private const string DescribeJson = """
        {
          "schemaVersion": 1,
          "owner": "HistoryMinerva",
          "pages": [{
            "id": "mapping", "title": "Minerva",
            "scene": "HistoryMinerva",
            "placement": { "side": "center", "ratio": 0.75, "visible": true, "singleton": true },
            "content": { "type": "stack", "gap": "tight", "children": [
              { "type": "panel", "id": "mapping-controls", "rows": [{ "mode": "flex", "widgets": [
                { "kind": "sourcePicker", "id": "source", "label": "来源", "value": "", "selectCommand": "minerva.ui.picksource content={selection.minerva.content.value}", "commitAction": "minerva.source.set", "flex": true },
                { "kind": "textbox", "id": "content", "label": "转换内容", "mode": "select", "value": "Solid Edge .par → SolidWorks .SLDPRT", "channel": "minerva.content", "commitAction": "minerva.content.set", "optionsSource": { "command": "minerva.ui.data", "args": { "view": "content" } }, "minWidth": 260 }
              ] }] },
              { "type": "switch", "id": "mapping-modes", "source": "{selection.minerva.content.value}", "children": [
                { "case": "Solid Edge .par → SolidWorks .SLDPRT", "type": "stack", "gap": "tight", "children": [
                  { "type": "panel", "id": "part-actions", "rows": [{ "mode": "even", "widgets": [{ "kind": "button", "action": "minerva.conversion.run", "text": "转换全部零件" }, { "kind": "button", "action": "minerva.conversion.cancel", "text": "取消" }] }] },
                  { "type": "table", "id": "part-parts", "dataSource": { "command": "minerva.ui.data", "args": { "view": "parts" } }, "columns": [{ "key": "file", "title": "文件", "width": "220" }, { "key": "status", "title": "状态", "width": "90" }, { "key": "detail", "title": "详情", "width": "*" }, { "key": "features", "title": "特征", "width": "70" }, { "key": "sketches", "title": "草图", "width": "70" }] },
                  { "type": "panel", "id": "conversion-options", "text": "转换选项", "rows": [{ "mode": "even", "widgets": [{ "kind": "switch", "id": "part-recognize", "label": "识别特征与草图", "value": "false", "action": "minerva.options.recognize" }, { "kind": "switch", "id": "part-continue", "label": "失败继续", "value": "true", "action": "minerva.options.continue" }, { "kind": "switch", "id": "part-mates", "label": "重建装配关系", "value": "false", "action": "minerva.options.mates" }] }] }
                ] },
                { "case": "Solid Edge .asm → SolidWorks .SLDASM", "type": "stack", "gap": "tight", "children": [
                  { "type": "panel", "id": "se-assembly-actions", "rows": [{ "mode": "even", "widgets": [{ "kind": "button", "action": "minerva.conversion.probe", "text": "解析装配体" }, { "kind": "button", "action": "minerva.conversion.run", "text": "开始转换" }, { "kind": "button", "action": "minerva.conversion.cancel", "text": "取消" }] }] },
                  { "type": "table", "id": "se-assembly-parts", "dataSource": { "command": "minerva.ui.data", "args": { "view": "parts" } }, "columns": [{ "key": "file", "title": "文件", "width": "220" }, { "key": "status", "title": "状态", "width": "90" }, { "key": "detail", "title": "详情", "width": "*" }, { "key": "features", "title": "特征", "width": "70" }, { "key": "sketches", "title": "草图", "width": "70" }] },
                  { "type": "panel", "id": "se-assembly-options", "text": "转换选项", "rows": [{ "mode": "even", "widgets": [{ "kind": "switch", "id": "se-recognize", "label": "识别特征与草图", "value": "false", "action": "minerva.options.recognize" }, { "kind": "switch", "id": "se-continue", "label": "失败继续", "value": "true", "action": "minerva.options.continue" }, { "kind": "switch", "id": "se-mates", "label": "重建装配关系", "value": "true", "action": "minerva.options.mates" }] }] }
                ] },
                { "case": "SolidWorks .SLDASM → SolidWorks .SLDASM（特征整备）", "type": "stack", "gap": "tight", "children": [
                  { "type": "panel", "id": "sw-feature-actions", "rows": [{ "mode": "even", "widgets": [{ "kind": "button", "action": "minerva.conversion.probe", "text": "解析装配体" }, { "kind": "button", "action": "minerva.conversion.run", "text": "开始整备" }, { "kind": "button", "action": "minerva.conversion.cancel", "text": "取消" }] }] },
                  { "type": "table", "id": "sw-feature-parts", "dataSource": { "command": "minerva.ui.data", "args": { "view": "parts" } }, "columns": [{ "key": "file", "title": "文件", "width": "220" }, { "key": "status", "title": "状态", "width": "90" }, { "key": "detail", "title": "详情", "width": "*" }, { "key": "features", "title": "特征", "width": "70" }, { "key": "sketches", "title": "草图", "width": "70" }] },
                  { "type": "panel", "id": "sw-feature-options", "text": "转换选项", "rows": [{ "mode": "even", "widgets": [{ "kind": "switch", "id": "sw-feature-recognize", "label": "识别特征与草图", "value": "true", "action": "minerva.options.recognize" }, { "kind": "switch", "id": "sw-feature-continue", "label": "失败继续", "value": "true", "action": "minerva.options.continue" }, { "kind": "switch", "id": "sw-feature-mates", "label": "重建装配关系", "value": "true", "action": "minerva.options.mates" }] }] }
                ] },
                { "case": "SolidWorks .SLDASM → 批量改名", "type": "stack", "gap": "tight", "children": [
                  { "type": "panel", "id": "sw-property-actions", "rows": [{ "mode": "even", "widgets": [{ "kind": "button", "action": "minerva.conversion.probe", "text": "解析装配体" }, { "kind": "button", "action": "minerva.conversion.run", "text": "改名" }, { "kind": "button", "action": "minerva.conversion.cancel", "text": "取消" }] }] },
                  { "type": "table", "id": "sw-property-parts", "dataSource": { "command": "minerva.ui.data", "args": { "view": "parts" } }, "columns": [{ "key": "drawing", "title": "图号", "width": "150" }, { "key": "name", "title": "名称", "width": "*", "cellAction": "minerva.cell.name" }, { "key": "category", "title": "件别", "width": "90", "cellAction": "minerva.cell.kind" }, { "key": "status", "title": "状态", "width": "80" }] },
                  { "type": "panel", "id": "sw-property-options", "text": "图号前缀", "rows": [{ "mode": "flex", "widgets": [{ "kind": "textbox", "id": "batch-kind", "label": "", "mode": "select", "options": ["件别", "机加件", "外购件", "排除"], "commitAction": "minerva.property.kind", "minWidth": 96 }, { "kind": "textbox", "id": "prefix", "label": "图号前缀", "commitAction": "minerva.options.prefix", "flex": true, "minWidth": 160 }] }] }
                ] },
                { "case": "SolidWorks .SLDASM → 整体打包（零件采购：BOM + 图纸）", "type": "stack", "gap": "tight", "children": [
                  { "type": "panel", "id": "sw-package-actions", "rows": [{ "mode": "even", "widgets": [{ "kind": "button", "action": "minerva.conversion.probe", "text": "解析装配体" }, { "kind": "button", "action": "minerva.conversion.run", "text": "打包" }, { "kind": "button", "action": "minerva.conversion.cancel", "text": "取消" }] }] },
                  { "type": "table", "id": "sw-package-parts", "dataSource": { "command": "minerva.ui.data", "args": { "view": "parts" } }, "columns": [{ "key": "drawing", "title": "图号 / 规格", "width": "170" }, { "key": "name", "title": "名称", "width": "*" }, { "key": "quantity", "title": "数量", "width": "60" }, { "key": "category", "title": "件别", "width": "90", "cellAction": "minerva.cell.kind" }, { "key": "brand", "title": "品牌", "width": "110" }, { "key": "hasdrawing", "title": "工程图", "width": "70" }, { "key": "status", "title": "状态", "width": "80" }, { "key": "detail", "title": "结果", "width": "2*" }] },
                  { "type": "panel", "id": "sw-package-options", "text": "打包选项", "rows": [{ "mode": "even", "widgets": [{ "kind": "textbox", "id": "batch-kind", "label": "", "mode": "select", "options": ["件别", "机加件", "外购件", "排除"], "commitAction": "minerva.property.kind" }, { "kind": "switch", "id": "package-brand-ai", "label": "AI 查品牌与名称（DeepSeek 联网搜索，按次计费）", "value": "__PKG_BRANDAI__", "action": "minerva.options.brandai" }] }, { "mode": "even", "widgets": [{ "kind": "switch", "id": "package-machined-bom", "label": "机加单", "value": "__PKG_MACHINEDBOM__", "action": "minerva.options.machinedbom" }, { "kind": "switch", "id": "package-purchased-bom", "label": "外购单", "value": "__PKG_PURCHASEDBOM__", "action": "minerva.options.purchasedbom" }, { "kind": "switch", "id": "package-step", "label": "STEP", "value": "__PKG_STEP__", "action": "minerva.options.step" }, { "kind": "switch", "id": "package-dwg", "label": "DWG", "value": "__PKG_DWG__", "action": "minerva.options.dwg" }, { "kind": "switch", "id": "package-pdf", "label": "PDF", "value": "__PKG_PDF__", "action": "minerva.options.pdf" }] }] }
                ] }
              ] }
            ] }
          }]
        }
        """;

    private const string ActionsJson = """
        {
          "schemaVersion": 1,
          "owner": "HistoryMinerva",
          "actions": [
            { "id": "minerva.source.set", "title": "设置来源", "command": "minerva.ui.source", "args": { "path": "{value}", "content": "{selection.minerva.content.value}" }, "summary": "按当前转换内容设置装配体文件或零件文件夹" },
            { "id": "minerva.content.set", "title": "设置转换内容", "command": "minerva.ui.content", "args": { "content": "{value}" }, "summary": "把页面当前转换内容写进模块" },
            { "id": "minerva.conversion.probe", "title": "解析装配体", "command": "minerva.conversion.probe", "args": { "content": "{selection.minerva.content.value}" }, "summary": "解析当前装配体来源" },
            { "id": "minerva.conversion.run", "title": "开始转换", "command": "minerva.conversion.run", "args": { "content": "{selection.minerva.content.value}" }, "summary": "执行当前转换；批量改名下只改文件名（前缀为空则保留原图号；改成外购件的同级件移入「外购件」文件夹）" },
            { "id": "minerva.conversion.cancel", "title": "取消", "command": "minerva.conversion.cancel", "summary": "取消当前操作" },
            { "id": "minerva.options.recognize", "title": "更新识别选项", "command": "minerva.ui.options", "args": { "option": "recognize", "value": "{value}" }, "summary": "开启或关闭特征与草图识别" },
            { "id": "minerva.options.continue", "title": "更新失败策略", "command": "minerva.ui.options", "args": { "option": "continue", "value": "{value}" }, "summary": "设置零件失败时是否继续" },
            { "id": "minerva.options.mates", "title": "更新装配关系", "command": "minerva.ui.options", "args": { "option": "mates", "value": "{value}" }, "summary": "设置是否重建装配关系" },
            { "id": "minerva.options.prefix", "title": "更新图号前缀", "command": "minerva.ui.options", "args": { "option": "prefix", "value": "{value}" }, "summary": "设置批量改名图号前缀" },
            { "id": "minerva.options.brandai", "title": "AI 查品牌", "command": "minerva.ui.options", "args": { "option": "brandai", "value": "{value}" }, "summary": "整体打包时是否联网查外购件品牌、并给文件名里没有名称的外购件补名称（DeepSeek 联网搜索，按次计费）；关着时品牌列与外购件清单 H 列留空" },
            { "id": "minerva.options.machinedbom", "title": "机加单", "command": "minerva.ui.options", "args": { "option": "machinedbom", "value": "{value}" }, "summary": "整体打包时是否生成机加件清单与截图；关着时不写也不删上一轮的" },
            { "id": "minerva.options.purchasedbom", "title": "外购单", "command": "minerva.ui.options", "args": { "option": "purchasedbom", "value": "{value}" }, "summary": "整体打包时是否生成外购件清单与截图；关着时不写也不删上一轮的" },
            { "id": "minerva.options.step", "title": "导出 STEP", "command": "minerva.ui.options", "args": { "option": "step", "value": "{value}" }, "summary": "整体打包时是否导出机加件 STEP" },
            { "id": "minerva.options.dwg", "title": "导出 DWG", "command": "minerva.ui.options", "args": { "option": "dwg", "value": "{value}" }, "summary": "整体打包时是否把工程图导成 DWG" },
            { "id": "minerva.options.pdf", "title": "导出 PDF", "command": "minerva.ui.options", "args": { "option": "pdf", "value": "{value}" }, "summary": "整体打包时是否把工程图导成 PDF" },
            { "id": "minerva.property.kind", "title": "统一设置件别", "command": "minerva.ui.property", "args": { "field": "kind", "value": "{value}" }, "summary": "把表里每一行的件别统一为 机加件 / 外购件 / 排除；「参考部件」目录下的件与子文件夹里的装配体改不成的保持原样" },
            { "id": "minerva.cell.kind", "title": "切换件别", "command": "minerva.ui.cell", "args": { "field": "kind", "id": "{id}" }, "summary": "点一下切换这一行的件别：机加件 → 外购件 → 排除" },
            { "id": "minerva.cell.name", "title": "修改名称", "command": "minerva.ui.cell", "args": { "field": "name", "id": "{id}" }, "summary": "修改这一行的名称；文件名是图号加空格加名称" }
          ]
        }
        """;
}
