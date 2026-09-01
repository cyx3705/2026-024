using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
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
        _runtimePaths = MappingRuntimePaths.CreateHistoryVulcanDefault();

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
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.strip",
                CommandClass = "conversion",
                Summary = "通过命令总线按空格洗掉当前装配体的图号",
                Example = "minerva.conversion.strip",
                RequiresUiThread = true,
                Parameters = PageContentParameters,
                Handler = StripCurrentAsync,
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
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(DescribeJson)),
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
                Summary = "一键刷满属性整备的某一个属性槽",
                RequiresUiThread = true,
                AllowUnspecifiedParameters = true,
                HiddenReason = "Aurora 属性整备内部协议，不对远程消费面暴露",
                Handler = SetPropertyAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".ui.cell",
                CommandClass = "ui",
                Summary = "修改零件表里某一行的材料、表面处理或热处理",
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
            _viewModel = new AssemblyViewModel(_runtimePaths);
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
        // 解析出来的零件行、以及零件上读回来的材料/表面处理/热处理，要立刻出现在表里。
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
                viewModel.IsRenameMode ? viewModel.RenameBlockedReason : viewModel.StatusText);
        }

        var result = await ConversionCommandHandlers.ConvertAsync(viewModel, command).ConfigureAwait(true);
        // 写入之后索引已经挪到新文件名上（见 AssemblyViewModel.ReindexAfterRename）。
        // 不刷这一下，表里显示的还是一批已经不存在的旧文件。
        await RefreshPropertyPrepTableAsync(command).ConfigureAwait(true);
        return result;
    }

    private async Task<CommandResult> StripCurrentAsync(CommandContext command)
    {
        var contentError = ApplyPageContent(command);
        if (contentError is not null)
            return contentError;

        var viewModel = GetViewModel();
        if (!viewModel.CanExecuteStrip)
            return CommandResult.Fail(viewModel.StripBlockedReason);

        var result = await ConversionCommandHandlers.StripAsync(viewModel, command).ConfigureAwait(true);
        await RefreshPropertyPrepTableAsync(command).ConfigureAwait(true);
        return result;
    }

    /// <summary>
    /// Aurora 转换内容下拉默认只改选择通道。选来源、解析、改名必须先套用页面当前内容，
    /// 否则属性整备仍按默认零件文件夹去选目录。
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
        return viewModel.Cancel()
            ? CommandResult.Ok("已请求取消 Minerva 当前操作。")
            : CommandResult.Fail("Minerva 当前没有可取消的操作。");
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
                default:
                    return CommandResult.Fail("未知转换选项");
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

    /// <summary>零件表里那张表的节点 id。改属性以后要按节点刷它，否则界面停在旧值上。</summary>
    private const string PartsTableNode = "sw-property-parts";

    /// <summary>
    /// 一键刷满一个属性槽：设计、日期、材料、表面处理、热处理。
    ///
    /// 返回改了几行而不是空口说成功。「刷了 0 行」与「刷完了」必须能分开——
    /// 没解析装配体时一行都没有，用户点下去要能看出是自己少了一步。
    /// </summary>
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
            case "designer":
                model.Designer = value;
                message = value.Length == 0
                    ? "已清空「设计」属性的待写值。"
                    : $"「设计」将写入：{value}";
                break;
            case "date":
                message = $"「日期」将写入：{model.SetDateToday()}";
                break;
            case "material":
            case "surface":
            case "heat":
                var target = ParsePartPropertyField(field);
                // 计划还没建起来时一行都刷不到。这时报「刷了 0 个零件」是句真话但没用——
                // 用户看不出是自己少了「解析装配体」或「填图号前缀」这一步。
                if (!model.PropertyEditsReady)
                    return CommandResult.Ok($"请先解析装配体并填写图号前缀，再刷「{DescribeField(target)}」。");
                if (!TryResolveSelection(target, value, out var resolved, out var database, out var reason))
                    return CommandResult.Fail(reason);
                var changed = model.SetAllPartProperties(target, resolved, database);
                message = resolved.Length == 0
                    ? $"已清空 {changed} 个零件的「{DescribeField(target)}」。"
                    : $"已把「{DescribeField(target)}」刷到 {changed} 个零件：{resolved}";
                break;
            default:
                return CommandResult.Fail("未知属性槽；支持 designer、date、material、surface、heat");
        }

        await RefreshPartsTableAsync(command).ConfigureAwait(true);
        return CommandResult.Ok(message);
    }

    /// <summary>
    /// 点单元格改一行的材料 / 表面处理 / 热处理。
    ///
    /// 弹窗走 <c>aurora.ui.dialog</c> 而不是自己 <c>new Window</c>：前端独立之后顶层窗口
    /// 拿不到 <c>Aurora.*</c> 令牌，自建窗口会静默退化成系统外观。
    /// 用户点取消时命令仍然成功——失败指令会让宿主抢控制台并重排停靠（REQ-002）。
    /// </summary>
    private async Task<CommandResult> EditCellAsync(CommandContext command)
    {
        var fieldText = command.GetString("field")?.Trim().ToLowerInvariant();
        var rowId = command.GetString("id")?.Trim();
        if (string.IsNullOrWhiteSpace(fieldText) || string.IsNullOrWhiteSpace(rowId))
            return CommandResult.Fail("需要 field 和 id");
        if (fieldText is not ("material" or "surface" or "heat"))
            return CommandResult.Fail("未知属性槽；单元格只支持 material、surface、heat");

        var bus = _context?.Bus;
        if (bus is null)
            return CommandResult.Fail("Minerva 尚未接入命令总线。");

        var field = ParsePartPropertyField(fieldText);
        var label = DescribeField(field);
        var model = GetViewModel();
        if (!model.WritesProperties(rowId))
            return CommandResult.Ok($"这一行不写属性（装配体或未编号内部件），「{label}」未修改。");
        var options = SelectionOptions(field);
        if (options.Count <= 1)
            return CommandResult.Ok(EmptyOptionsReason(field));

        var dialog = await bus.ExecuteAsync(
            BuildChoiceCommand(label, options),
            command.Source,
            command.Cancellation).ConfigureAwait(true);
        if (!dialog.Success)
            return CommandResult.Ok($"未修改「{label}」。");

        if (!TryResolveSelection(field, DialogValue(dialog), out var next, out var database, out var reason))
            return CommandResult.Fail(reason);
        if (!model.SetPartProperty(rowId, field, next, database))
            return CommandResult.Ok($"这一行不写属性（装配体或未编号内部件），「{label}」未修改。");

        await RefreshPartsTableAsync(command).ConfigureAwait(true);
        return CommandResult.Ok(next.Length == 0 ? $"已清空「{label}」。" : $"「{label}」已改为 {next}");
    }

    /// <summary>
    /// 三个属性槽在 SolidWorks 里都不是自由文本，界面上也就只让选不让填。
    ///
    /// 首项恒为 <see cref="PartPropertyNames.NoWriteOption"/>：选项框只能在候选之间轮换，
    /// 没有「清空」这个动作，少了这一项用户点错一次就再也退不回不写了。
    /// </summary>
    private static IReadOnlyList<string> SelectionOptions(PartPropertyField field)
    {
        var values = field switch
        {
            PartPropertyField.Material => SolidWorksPropertyOptions.Materials().Select(item => item.Label),
            PartPropertyField.SurfaceTreatment => SolidWorksPropertyOptions.SurfaceTreatments().AsEnumerable(),
            PartPropertyField.HeatTreatment => SolidWorksPropertyOptions.HeatTreatments().AsEnumerable(),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
        return new[] { PartPropertyNames.NoWriteOption }.Concat(values).ToArray();
    }

    /// <summary>只剩「（不写）」一项时的说明。材料是唯一会空的一栏，另两栏有模板兜底。</summary>
    private static string EmptyOptionsReason(PartPropertyField field)
        => field == PartPropertyField.Material
            ? "没有可选材质：请先在 SolidWorks 的材料对话框里把常用材质加入收藏，再回到这里。"
            : $"没有可选的「{DescribeField(field)}」候选，请检查 SolidWorks 属性标签模板。";

    /// <summary>
    /// 把下拉里选中的那一项折成模型层的值。
    ///
    /// 「（不写）」折成空串——空串是 <see cref="PartPropertyWrite"/> 里「本轮不动这一槽」的约定。
    /// 材料还要多一步：下拉显示的是材质名（重名时带库名），而应用材质需要材质名**加**材料库，
    /// 所以要按候选表反查回去。查不到就报错，不能当成空串放过——那会变成静默的「选了但没生效」。
    /// </summary>
    private static bool TryResolveSelection(
        PartPropertyField field,
        string selection,
        out string value,
        out string database,
        out string error)
    {
        value = string.Empty;
        database = string.Empty;
        error = string.Empty;
        var text = selection.Trim();
        if (text.Length == 0 || string.Equals(text, PartPropertyNames.NoWriteOption, StringComparison.Ordinal))
            return true;

        if (field != PartPropertyField.Material)
        {
            value = text;
            return true;
        }

        var match = SolidWorksPropertyOptions.Materials().FirstOrDefault(
            item => string.Equals(item.Label, text, StringComparison.Ordinal)
                    || string.Equals(item.Name, text, StringComparison.Ordinal));
        if (match.Database.Length == 0)
        {
            error = $"收藏材料里没有「{text}」。请在 SolidWorks 材料对话框里收藏它，或改选别的材质。";
            return false;
        }

        value = match.Name;
        database = match.Database;
        return true;
    }

    private static string BuildChoiceCommand(string label, IReadOnlyList<string> options)
        => "aurora.ui.dialog kind=choice"
            + " title=" + CommandParser.QuoteArg(label)
            + " body=" + CommandParser.QuoteArg($"选择要写进零件属性的「{label}」")
            + " cancel=取消"
            + " options=" + CommandParser.QuoteArg(JsonSerializer.Serialize(
                options.Select(option => new { label = option, value = option })));

    /// <summary>
    /// 单元格动作是 Aurora 直接打到总线的，表格不会自己重取。不显式刷这一格，
    /// 用户看到的还是改之前的值——而旧值与新值长得一模一样地都是「一个字符串」。
    /// </summary>
    private async Task RefreshPartsTableAsync(CommandContext command)
    {
        var bus = _context?.Bus;
        if (bus is null)
            return;
        _ = await bus.ExecuteAsync(
            "aurora.ui.refreshdata node=" + PartsTableNode,
            command.Source,
            command.Cancellation).ConfigureAwait(true);
    }

    /// <summary>
    /// 只在属性整备下刷零件表。别的三种转换内容各有自己的表节点，
    /// 在这里连它们一起刷是越权，而且刷的还是一个当下并不存在的节点。
    /// </summary>
    private async Task RefreshPropertyPrepTableAsync(CommandContext command)
    {
        if (_viewModel is { IsRenameMode: true })
            await RefreshPartsTableAsync(command).ConfigureAwait(true);
    }

    private static PartPropertyField ParsePartPropertyField(string field)
        => field switch
        {
            "material" => PartPropertyField.Material,
            "surface" => PartPropertyField.SurfaceTreatment,
            "heat" => PartPropertyField.HeatTreatment,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

    private static string DescribeField(PartPropertyField field)
        => field switch
        {
            PartPropertyField.Material => PartPropertyNames.Material,
            PartPropertyField.SurfaceTreatment => PartPropertyNames.SurfaceTreatment,
            PartPropertyField.HeatTreatment => PartPropertyNames.HeatTreatment,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

    private static string DialogValue(CommandResult result) => result.Data switch
    {
        string value => value.Trim(),
        _ => result.Message.Trim(),
    };

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
            // 三个下拉的候选走 optionsSource 实时取，而不是写死在页面描述里：
            // 用户在 SolidWorks 里收藏一种新材质、或改一次属性标签模板，下次展开面板就该看见。
            "materials" => CommandResult.Ok("Minerva 材料候选", OptionRows(PartPropertyField.Material)),
            "surfaces" => CommandResult.Ok("Minerva 表面处理候选", OptionRows(PartPropertyField.SurfaceTreatment)),
            "heats" => CommandResult.Ok("Minerva 热处理候选", OptionRows(PartPropertyField.HeatTreatment)),
            _ => CommandResult.Fail("未知 view；支持 parts、content、materials、surfaces、heats"),
        };
    }

    /// <summary>候选取数的行格式：Aurora 的 <c>optionsSource</c> 固定读 <c>value</c> 列。</summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string>> OptionRows(PartPropertyField field)
        => SelectionOptions(field)
            .Select(option => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["value"] = option,
            })
            .ToList();

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> PartRows(AssemblyViewModel? viewModel)
    {
        if (viewModel is not { } model)
            return [];

        return model.Parts.Select(row => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            // id 是隐藏字段：单元格动作按被点那一行的同名字段取值，不读当前选中行。
            ["id"] = row.Id,
            // 属性整备下 DisplayName 给的是**改完之后**的文件名，不再另开一列「改名后预览」：
            // 同一个东西占两列，而改名成功之后「文件」那一列显示的还是一个已经不存在的文件。
            ["file"] = row.DisplayName,
            ["status"] = row.Status,
            ["detail"] = row.Detail,
            ["features"] = row.FeatureText,
            ["sketches"] = row.SketchText,
            ["material"] = PropertyCell(row, PartPropertyField.Material),
            ["surface"] = PropertyCell(row, PartPropertyField.SurfaceTreatment),
            ["heat"] = PropertyCell(row, PartPropertyField.HeatTreatment),
        }).ToList();
    }

    /// <summary>
    /// 三个可点列的显示值。
    ///
    /// **空格子不能真的留空**：Aurora 的 <c>cellAction</c> 明确不触发空单元格，
    /// 而第一次填材料要点的恰恰是空格子——留空就等于这三列永远点不开。
    /// 因此会写属性的零件行在空值时渲染成占位符 <c>—</c>；不写属性的装配体行留真空，
    /// 让它保持不可点，用户也就不会往一个不会落盘的地方填字。
    /// </summary>
    private static string PropertyCell(ConversionFileRow row, PartPropertyField field)
    {
        if (!row.WritesProperties)
            return string.Empty;
        var value = field switch
        {
            PartPropertyField.Material => row.Material,
            PartPropertyField.SurfaceTreatment => row.SurfaceTreatment,
            PartPropertyField.HeatTreatment => row.HeatTreatment,
            _ => string.Empty,
        };
        return value.Length > 0 ? value : EmptyPropertyCell;
    }

    /// <summary>会写属性但还没填时的占位符。见 <see cref="PropertyCell"/> 里为什么不能留空。</summary>
    internal const string EmptyPropertyCell = "—";

    private const string DescribeJson = """
        {
          "schemaVersion": 1,
          "owner": "HistoryMinerva",
          "pages": [{
            "id": "mapping", "title": "Minerva",
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
                { "case": "SolidWorks .SLDASM → 属性整备（改名）", "type": "stack", "gap": "tight", "children": [
                  { "type": "panel", "id": "sw-property-actions", "rows": [{ "mode": "even", "widgets": [{ "kind": "button", "action": "minerva.conversion.probe", "text": "解析装配体" }, { "kind": "button", "action": "minerva.property.today", "text": "一键设置日期" }, { "kind": "button", "action": "minerva.conversion.run", "text": "写入" }, { "kind": "button", "action": "minerva.conversion.strip", "text": "按空格洗图号" }, { "kind": "button", "action": "minerva.conversion.cancel", "text": "取消" }] }] },
                  { "type": "table", "id": "sw-property-parts", "dataSource": { "command": "minerva.ui.data", "args": { "view": "parts" } }, "columns": [{ "key": "file", "title": "文件", "width": "*" }, { "key": "status", "title": "状态", "width": "80" }, { "key": "material", "title": "材料", "width": "110", "cellAction": "minerva.cell.material" }, { "key": "surface", "title": "表面处理", "width": "110", "cellAction": "minerva.cell.surface" }, { "key": "heat", "title": "热处理", "width": "110", "cellAction": "minerva.cell.heat" }] },
                  { "type": "panel", "id": "sw-property-options", "text": "图号前缀", "rows": [{ "mode": "flex", "widgets": [{ "kind": "textbox", "id": "prefix", "label": "图号前缀", "commitAction": "minerva.options.prefix", "flex": true, "minWidth": 160 }, { "kind": "textbox", "id": "designer", "label": "设计", "commitAction": "minerva.property.designer", "flex": true, "minWidth": 120 }] }, { "mode": "even", "widgets": [{ "kind": "textbox", "id": "batch-material", "label": "材料", "mode": "select", "optionsSource": { "command": "minerva.ui.data", "args": { "view": "materials" } }, "commitAction": "minerva.property.material" }, { "kind": "textbox", "id": "batch-surface", "label": "表面处理", "mode": "select", "optionsSource": { "command": "minerva.ui.data", "args": { "view": "surfaces" } }, "commitAction": "minerva.property.surface" }, { "kind": "textbox", "id": "batch-heat", "label": "热处理", "mode": "select", "optionsSource": { "command": "minerva.ui.data", "args": { "view": "heats" } }, "commitAction": "minerva.property.heat" }] }] }
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
            { "id": "minerva.conversion.run", "title": "开始转换", "command": "minerva.conversion.run", "args": { "content": "{selection.minerva.content.value}" }, "summary": "执行当前转换；属性整备下是改名并写入零件属性" },
            { "id": "minerva.conversion.strip", "title": "按空格洗图号", "command": "minerva.conversion.strip", "args": { "content": "{selection.minerva.content.value}" }, "summary": "按文件名第一个空格洗掉图号" },
            { "id": "minerva.conversion.cancel", "title": "取消", "command": "minerva.conversion.cancel", "summary": "取消当前操作" },
            { "id": "minerva.options.recognize", "title": "更新识别选项", "command": "minerva.ui.options", "args": { "option": "recognize", "value": "{value}" }, "summary": "开启或关闭特征与草图识别" },
            { "id": "minerva.options.continue", "title": "更新失败策略", "command": "minerva.ui.options", "args": { "option": "continue", "value": "{value}" }, "summary": "设置零件失败时是否继续" },
            { "id": "minerva.options.mates", "title": "更新装配关系", "command": "minerva.ui.options", "args": { "option": "mates", "value": "{value}" }, "summary": "设置是否重建装配关系" },
            { "id": "minerva.options.prefix", "title": "更新图号前缀", "command": "minerva.ui.options", "args": { "option": "prefix", "value": "{value}" }, "summary": "设置属性整备图号前缀" },
            { "id": "minerva.property.designer", "title": "设置设计", "command": "minerva.ui.property", "args": { "field": "designer", "value": "{value}" }, "summary": "把「设计」属性一次刷满全部零件" },
            { "id": "minerva.property.today", "title": "一键设置日期", "command": "minerva.ui.property", "args": { "field": "date" }, "summary": "把「日期」属性设为系统当日" },
            { "id": "minerva.property.material", "title": "一键设置材料", "command": "minerva.ui.property", "args": { "field": "material", "value": "{value}" }, "summary": "把「材料」一次刷满全部零件" },
            { "id": "minerva.property.surface", "title": "一键设置表面处理", "command": "minerva.ui.property", "args": { "field": "surface", "value": "{value}" }, "summary": "把「表面处理」一次刷满全部零件" },
            { "id": "minerva.property.heat", "title": "一键设置热处理", "command": "minerva.ui.property", "args": { "field": "heat", "value": "{value}" }, "summary": "把「热处理」一次刷满全部零件" },
            { "id": "minerva.cell.material", "title": "修改材料", "command": "minerva.ui.cell", "args": { "field": "material", "id": "{id}" }, "summary": "修改这一行零件的「材料」" },
            { "id": "minerva.cell.surface", "title": "修改表面处理", "command": "minerva.ui.cell", "args": { "field": "surface", "id": "{id}" }, "summary": "修改这一行零件的「表面处理」" },
            { "id": "minerva.cell.heat", "title": "修改热处理", "command": "minerva.ui.cell", "args": { "field": "heat", "id": "{id}" }, "summary": "修改这一行零件的「热处理」" }
          ]
        }
        """;
}
