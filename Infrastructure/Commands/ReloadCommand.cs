using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $reload 命令实现，重新加载配置文件并完全重建服务器列表（运行中的服务器不受影响）。
/// </summary>
public class ReloadCommand : ICommand
{
    /// <summary>
    /// 命令名称 "$reload"。
    /// </summary>
    public string Name => "$reload";

    /// <summary>
    /// 命令用途说明，用于帮助信息展示。
    /// </summary>
    public string Description => "重新加载配置文件，完全重建服务器列表（跳过运行中的服务器）";

    /// <summary>
    /// 执行 $reload 命令：重新加载配置并通知服务器注册表重建，失败时捕获异常并返回错误信息。
    /// </summary>
    /// <param name="args">命令参数，包含配置存储与服务器注册表。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果，成功时退出码为 1，失败时为 0。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        try
        {
            // 1. 重新加载配置
            var newConfig = args.ConfigStore.LoadConfig();
            // 2. 通知 ServerRegistry 进行重建
            args.ServerRegistry.Reload(newConfig);
            output?.Write("Command", LogLevel.Success, "配置已重新加载，服务器列表已更新（运行中的服务器未受影响）");
            return new CommandResult(1, "配置已重新加载");
        }
        catch (Exception ex)
        {
            var msg = $"重新加载失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }
}
