using MSL_CLI.Services;

namespace MSL_CLI.Commands;

/// <summary>
/// 命令执行时的上下文参数。
/// </summary>
public class CommandArgs
{
    /// <summary>原始参数字符串（不含命令名）</summary>
    public string Raw { get; }

    /// <summary>全局管理器，可访问配置和服务器实例</summary>
    public GlobalManager GlobalManager { get; }

    public CommandArgs(string raw, GlobalManager globalManager)
    {
        Raw = raw;
        GlobalManager = globalManager;
    }
}