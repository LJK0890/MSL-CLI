using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $exit 命令实现，退出程序前先停止所有正在运行的服务器。
/// </summary>
public class ExitCommand : ICommand
{
    /// <summary>
    /// 命令名称 "$exit"。
    /// </summary>
    public string Name => "$exit";

    /// <summary>
    /// 命令用途说明，用于帮助信息展示。
    /// </summary>
    public string Description => "退出程序（会先停止所有服务器）";

    /// <summary>
    /// 执行 $exit 命令：停止所有服务器，并通过带退出标记的结果通知程序退出。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果，其 ExitRequested 标记为 true，表示请求程序退出。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        output?.Write("Command", LogLevel.Info, "正在退出程序，停止所有服务器...");

        // 停止所有服务器
        await args.ServerRegistry.StopAllAsync();

        output?.Write("Command", LogLevel.Info, "所有服务器已关闭，正在退出程序...");

        // 通知程序退出（可通过 CommandResult 的特殊标记，或直接设置全局标志）
        output?.Write("Command", LogLevel.Success, "程序退出");
        return new CommandResult(1, "程序退出", exitRequested: true);
    }
}
