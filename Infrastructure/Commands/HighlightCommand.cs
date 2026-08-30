using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $highlight 命令实现，用于切换或查看当前高亮的服务器。
/// </summary>
public class HighlightCommand : ICommand
{
    /// <summary>
    /// 命令名称 "$highlight"。
    /// </summary>
    public string Name => "$highlight";

    /// <summary>
    /// 命令用途说明，用于帮助信息展示。
    /// </summary>
    public string Description => "切换高亮服务器，用法: $highlight <服务器名> 或 $highlight 显示当前高亮";

    /// <summary>
    /// 执行 $highlight 命令：无参数时显示当前高亮服务器，带参数时切换高亮目标。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表与原始输入。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果，切换成功时退出码为 1，未找到目标服务器时为 0。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var registry = args.ServerRegistry;

        // 无参数时显示当前高亮状态
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            // 显示当前高亮
            var current = registry.HighlightedServerName;
            var msg = current == null ? "当前没有高亮服务器" : $"当前高亮服务器: {current}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }

        // 带参数时按服务器名切换高亮，根据是否找到目标服务器返回不同结果
        var name = args.Raw.Trim();
        if (registry.SwitchHighlight(name))
        {
            var msg = $"已切换到服务器 '{name}'";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        else
        {
            var msg = $"未找到服务器 '{name}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }
}

/// <summary>
/// $hl 命令实现，$highlight 的别名，复用 HighlightCommand 的全部逻辑。
/// </summary>
public class HighlightAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称 "$hl"。
    /// </summary>
    public string Name => "$hl";

    /// <summary>
    /// 命令用途说明，用于帮助信息展示。
    /// </summary>
    public string Description => "切换高亮服务器（$highlight 的别名），用法: $hl <服务器名>";

    /// <summary>
    /// 内部复用的 HighlightCommand 实例。
    /// </summary>
    // 直接复用 HighlightCommand 的逻辑
    private readonly HighlightCommand _inner = new();

    /// <summary>
    /// 执行 $hl 命令：直接委托给 HighlightCommand 处理。
    /// </summary>
    /// <param name="args">命令参数。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}
