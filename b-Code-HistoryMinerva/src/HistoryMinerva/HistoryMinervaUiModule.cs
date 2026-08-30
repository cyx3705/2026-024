using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryMinerva.Contracts;
using System.IO;

namespace HistoryMinerva;

public sealed class HistoryMinervaUiModule : IModuleContextAware, IDisposable
{
    private IModuleContext? _context;
    private MappingRuntimePaths? _runtimePaths;
    private AssemblyViewModel? _viewModel;

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
                Handler = ProbeCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.run",
                CommandClass = "conversion",
                Summary = "通过命令总线执行当前选择的 Minerva 转换",
                Example = "minerva.conversion.run",
                RequiresUiThread = true,
                Handler = ConvertCurrentAsync,
            });
            registry.Register(new CommandDescriptor
            {
                Name = HistoryMinervaIdentity.CommandRoot + ".conversion.strip",
                CommandClass = "conversion",
                Summary = "通过命令总线按空格洗掉当前装配体的图号",
                Example = "minerva.conversion.strip",
                RequiresUiThread = true,
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
                Handler = CommandDescriptor.Sync(SetOption),
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

    private Task<CommandResult> ProbeCurrentAsync(CommandContext command)
    {
        var viewModel = GetViewModel();
        if (!viewModel.CanProbe)
            return Task.FromResult(CommandResult.Fail(viewModel.StatusText));

        return ConversionCommandHandlers.ProbeAsync(viewModel, command);
    }

    private Task<CommandResult> ConvertCurrentAsync(CommandContext command)
    {
        var viewModel = GetViewModel();
        if (!viewModel.CanConvert)
            return Task.FromResult(CommandResult.Fail(viewModel.StatusText));

        return ConversionCommandHandlers.ConvertAsync(viewModel, command);
    }

    private Task<CommandResult> StripCurrentAsync(CommandContext command)
    {
        var viewModel = GetViewModel();
        if (!viewModel.CanStrip)
            return Task.FromResult(CommandResult.Fail(viewModel.StatusText));

        return ConversionCommandHandlers.StripAsync(viewModel, command);
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

    private CommandResult SetSource(CommandContext command)
    {
        var path = command.GetString("path")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return CommandResult.Fail("缺少 path");

        try
        {
            var viewModel = GetViewModel();
            if (Directory.Exists(path))
                viewModel.SetPartDirectory(path);
            else
                viewModel.SetSourceFile(path);
            return CommandResult.Ok("已设置 Minerva 转换来源。");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            return CommandResult.Fail(ex.Message);
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
        if (string.IsNullOrWhiteSpace(option) || string.IsNullOrWhiteSpace(value))
            return CommandResult.Fail("需要 option 和 value");

        var model = GetViewModel();
        try
        {
            switch (option)
            {
                case "recognize":
                    model.RecognizeFeatures = ParseSwitch(value);
                    break;
                case "continue":
                    model.ContinueWhenPartFails = ParseSwitch(value);
                    break;
                case "mates":
                    model.RebuildMates = ParseSwitch(value);
                    break;
                case "prefix":
                    model.DrawingPrefix = value;
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

    private static bool ParseSwitch(string value)
        => value.Equals("开启", StringComparison.OrdinalIgnoreCase)
            || value.Equals("开", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase);

    private CommandResult Data(CommandContext command)
    {
        var view = command.GetString("view")?.Trim().ToLowerInvariant();
        var model = _viewModel;
        return view switch
        {
            "status" => CommandResult.Ok("Minerva 状态", StatusRows(model)),
            "parts" => CommandResult.Ok("Minerva 零件", PartRows(model)),
            "content" => CommandResult.Ok("Minerva 转换内容", MappingContentOption.Available
                .Select((item, index) => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["value"] = item.DisplayName,
                    ["index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }).ToList()),
            _ => CommandResult.Fail("未知 view；支持 status、parts、content"),
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> StatusRows(AssemblyViewModel? model)
    {
        if (model is null)
            return [new Dictionary<string, string> { ["key"] = "状态", ["value"] = "请选择转换来源" }];

        return [
            new Dictionary<string, string> { ["key"] = "来源", ["value"] = model.SourcePath },
            new Dictionary<string, string> { ["key"] = "内容", ["value"] = model.SelectedMappingContent.DisplayName },
            new Dictionary<string, string> { ["key"] = "状态", ["value"] = model.StatusText },
            new Dictionary<string, string> { ["key"] = "警告", ["value"] = model.WarningSummary },
        ];
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> PartRows(AssemblyViewModel? model)
        => model?.Parts.Select(row => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["file"] = row.FileName,
            ["status"] = row.Status,
            ["detail"] = row.Detail,
            ["features"] = row.FeatureText,
            ["sketches"] = row.SketchText,
        }).ToList() ?? [];

    private const string DescribeJson = """
        {
          "schemaVersion": 1,
          "owner": "HistoryMinerva",
          "pages": [
            {
              "id": "mapping",
              "title": "Minerva",
              "placement": { "side": "center", "ratio": 0.75, "visible": true, "singleton": true },
              "content": {
                "type": "stack",
                "gap": "tight",
                "children": [
                  {
                    "type": "panel",
                    "id": "mapping-controls",
                    "rows": [
                      { "widgets": [
                        { "kind": "textbox", "id": "source", "label": "转换来源", "flex": true },
                        { "kind": "button", "action": "minerva.source.set", "text": "设置来源" }
                      ] },
                      { "widgets": [
                        { "kind": "textbox", "id": "content", "label": "转换内容", "mode": "select", "channel": "minerva.content", "optionsSource": { "command": "minerva.ui.data", "args": { "view": "content" } }, "flex": true },
                        { "kind": "button", "action": "minerva.content.set", "text": "应用内容" },
                        { "kind": "button", "action": "minerva.conversion.probe", "text": "解析装配体" },
                        { "kind": "button", "action": "minerva.conversion.run", "text": "开始转换" },
                        { "kind": "button", "action": "minerva.conversion.strip", "text": "洗图号" },
                        { "kind": "button", "action": "minerva.conversion.cancel", "text": "取消" }
                      ] }
                    ]
                  },
                  {
                    "type": "panel",
                    "id": "conversion-options",
                    "text": "转换选项",
                    "rows": [
                      { "widgets": [
                        { "kind": "textbox", "id": "recognize", "label": "识别特征与草图", "mode": "select", "options": [ "关闭", "开启" ], "value": "关闭", "flex": true },
                        { "kind": "button", "action": "minerva.options.recognize", "text": "应用" },
                        { "kind": "textbox", "id": "continue", "label": "失败继续", "mode": "select", "options": [ "关闭", "开启" ], "value": "开启", "flex": true },
                        { "kind": "button", "action": "minerva.options.continue", "text": "应用" }
                      ] },
                      { "widgets": [
                        { "kind": "textbox", "id": "mates", "label": "重建装配关系", "mode": "select", "options": [ "关闭", "开启" ], "value": "关闭", "flex": true },
                        { "kind": "button", "action": "minerva.options.mates", "text": "应用" },
                        { "kind": "textbox", "id": "prefix", "label": "图号前缀", "flex": true },
                        { "kind": "button", "action": "minerva.options.prefix", "text": "应用" }
                      ] }
                    ]
                  },
                  { "type": "table", "id": "parts", "dataSource": { "command": "minerva.ui.data", "args": { "view": "parts" } }, "columns": [ { "key": "file", "title": "文件", "width": "220" }, { "key": "status", "title": "状态", "width": "90" }, { "key": "detail", "title": "详情", "width": "*" }, { "key": "features", "title": "特征", "width": "70" }, { "key": "sketches", "title": "草图", "width": "70" } ] }
                ]
              }
            }
          ]
        }
        """;

    private const string ActionsJson = """
        {
          "schemaVersion": 1,
          "owner": "HistoryMinerva",
          "actions": [
            { "id": "minerva.source.set", "title": "设置来源", "command": "minerva.ui.source", "args": { "path": "{source}" }, "summary": "设置文件或文件夹作为转换来源" },
            { "id": "minerva.content.set", "title": "应用内容", "command": "minerva.ui.content", "args": { "content": "{content}" }, "summary": "切换转换内容" },
            { "id": "minerva.conversion.probe", "title": "解析装配体", "command": "minerva.conversion.probe", "summary": "解析当前装配体来源" },
            { "id": "minerva.conversion.run", "title": "开始转换", "command": "minerva.conversion.run", "summary": "执行当前转换" },
            { "id": "minerva.conversion.strip", "title": "洗图号", "command": "minerva.conversion.strip", "summary": "按空格清理图号" },
            { "id": "minerva.conversion.cancel", "title": "取消", "command": "minerva.conversion.cancel", "summary": "取消当前操作" },
            { "id": "minerva.options.recognize", "title": "应用识别选项", "command": "minerva.ui.options", "args": { "option": "recognize", "value": "{recognize}" }, "summary": "开启或关闭特征与草图识别" },
            { "id": "minerva.options.continue", "title": "应用失败策略", "command": "minerva.ui.options", "args": { "option": "continue", "value": "{continue}" }, "summary": "设置零件失败时是否继续" },
            { "id": "minerva.options.mates", "title": "应用装配关系", "command": "minerva.ui.options", "args": { "option": "mates", "value": "{mates}" }, "summary": "设置是否重建装配关系" },
            { "id": "minerva.options.prefix", "title": "应用图号前缀", "command": "minerva.ui.options", "args": { "option": "prefix", "value": "{prefix}" }, "summary": "设置属性整备图号前缀" }
          ]
        }
        """;
}
