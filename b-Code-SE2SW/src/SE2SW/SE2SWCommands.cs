namespace SE2SW;

public sealed class SE2SWCommands
{
    /// <summary>说明如何显示 SE2SW 内嵌工具窗口。</summary>
    public string show()
    {
        return "win.show name=se2sw；窗口内可选择“零件转换”“装配转换”或“OHS 兼容”模式";
    }

    /// <summary>说明如何隐藏 SE2SW 内嵌工具窗口。</summary>
    public string hide()
    {
        return "win.hide name=se2sw";
    }
}
