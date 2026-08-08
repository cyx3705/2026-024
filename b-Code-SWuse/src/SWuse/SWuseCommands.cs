namespace SWuse;

/// <summary>SWuse 独立窗口控制指令。</summary>
public sealed class SWuseCommands
{
    /// <summary>显示 SWuse 独立 C# 建模窗口。</summary>
    public string Show() => SWuseWindowHost.Show();

    /// <summary>隐藏 SWuse 独立窗口，不停止其中的 Worker。</summary>
    public string Hide() => SWuseWindowHost.Hide();

    /// <summary>读取 SWuse 窗口与 Worker 状态。</summary>
    public string Status() => SWuseWindowHost.Status();
}
