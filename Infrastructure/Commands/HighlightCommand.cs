using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $hl 命令：切换或查看当前高亮的服务器。不带参数时显示当前高亮。
/// </summary>
public class HighlightCommand : ICommand
{
    /// <summary>命令名称：$hl。</summary>
    public string Name => "$hl";

    /// <summary>命令描述。</summary>
    public string Description => "切换高亮服务器，用法: $hl <服务器名>；不带参数显示当前高亮";

    /// <summary>
    /// 执行 $hl 命令。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表与原始输入。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var registry = args.ServerRegistry;

        // 无参数：显示当前高亮
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var current = registry.HighlightedServerName;
            var msg = current == null ? "当前没有高亮服务器" : $"当前高亮服务器: {current}";
            output?.Write("Command", LogLevel.Success, msg);
            return Task.FromResult(new CommandResult(1, msg));
        }

        // 带参数：切换高亮
        var name = args.Raw.Trim();
        if (registry.SwitchHighlight(name))
        {
            var msg = $"已切换到服务器 '{name}'";
            output?.Write("Command", LogLevel.Success, msg);
            return Task.FromResult(new CommandResult(1, msg));
        }

        var failMsg = $"未找到服务器 '{name}'";
        output?.Write("Command", LogLevel.Error, failMsg);
        return Task.FromResult(new CommandResult(0, failMsg));
    }
}
