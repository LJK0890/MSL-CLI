using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server buf 动作的处理器：读取服务器输出缓冲区。
/// 用法：<c>buf read &lt;服务器名&gt;</c>（不清空）、<c>buf update &lt;服务器名&gt;</c>（读取并清空）。
/// </summary>
internal static class ServerBufferHandler
{
    /// <summary>
    /// 处理 buf 动作。
    /// </summary>
    /// <param name="rest">buf 之后的参数文本。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, "用法: $server buf read|update <服务器名>");

        var action = parts[0].ToLowerInvariant();
        var clear = action switch
        {
            "read" => false,
            "update" => true,
            _ => (bool?)null
        };

        if (clear == null)
            return Fail(output, $"未知缓冲区动作 '{parts[0]}'，可用: read、update");

        if (parts.Length < 2)
            return Fail(output, "用法: $server buf read|update <服务器名>");

        var serverName = parts[1];
        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null) return Fail(output, $"未找到服务器 '{serverName}'");

        var content = clear.Value ? server.GetAndClearBufferContent() : server.GetBufferContent();
        var suffix = clear.Value ? "（已清空）" : string.Empty;

        if (string.IsNullOrEmpty(content))
        {
            var empty = $"服务器 '{serverName}' 的缓冲区为空{suffix}";
            output?.Write("Command", LogLevel.Success, empty);
            return new CommandResult(1, empty);
        }

        var msg = $"服务器 '{serverName}' 的缓冲区内容{suffix}:\n{content}";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 输出错误信息并构造失败结果。
    /// </summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}
