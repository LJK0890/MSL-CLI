using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server 命令：服务器管理的唯一入口，按动作分发到各处理器。
/// 动作：cfg / arg / ck / buf / ls / bp / query / status / stop / run / send。
/// </summary>
public class ServerCommand : ICommand
{
    /// <summary>命令名称：$server。</summary>
    public string Name => "$server";

    /// <summary>命令描述。</summary>
    public string Description =>
        "服务器管理。用法: $server <动作> ...（cfg/arg/ck/buf/ls/bp/query/status/stop/run/send），详见 $help $server";

    /// <summary>
    /// 执行 $server 命令：解析动作并把剩余参数交给对应处理器。
    /// 简单动作复用已有的功能类；cfg/arg/ck/buf 交给各自独立的处理器。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入、服务器注册表与命令解析器。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Task.FromResult(Fail(output, Help()));

        var action = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        return action switch
        {
            // 配置 / 启动参数 / 名单 / 缓冲区：各自独立的处理器
            "cfg" => Task.FromResult(ServerConfigHandler.Handle(rest, args, output)),
            "arg" => Task.FromResult(ServerArgsHandler.Handle(rest, args, output)),
            "ck" => Task.FromResult(ServerCheckHandler.Handle(rest, args, output)),
            "buf" => Task.FromResult(ServerBufferHandler.Handle(rest, args, output)),
            "bp" => ServerBackupCommand.Make(rest, args, output),
            // 多服务器：目标可为 all
            "ls" => Task.FromResult(ListServers(args, output)),
            "query" => ServerQueryHandler.RunAsync(rest, args, output),
            "status" => Task.FromResult(ServerStatusHandler.Run(rest, args, output)),
            "stop" => ServerStopHandler.RunAsync(rest, args, output),
            // 单服务器
            "run" => ServerRunHandler.RunAsync(rest, args, output),
            "send" => ServerSendHandler.RunAsync(rest, args, output),
            _ => Task.FromResult(Fail(output, Help()))
        };
    }

    /// <summary>
    /// 用新的参数文本复制一份命令参数，保留解析器等依赖。
    /// </summary>
    /// <param name="args">原始命令参数。</param>
    /// <param name="raw">新的参数文本。</param>
    /// <returns>新的命令参数实例。</returns>
    private static CommandArgs ReTarget(CommandArgs args, string raw)
        => new(raw, args.ServerRegistry, args.AgentService, args.ConfigStore) { Parser = args.Parser };

    /// <summary>
    /// 处理 ls 动作：列出所有已配置的服务器及其状态。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult ListServers(CommandArgs args, IOutputWriter? output)
    {
        var registry = args.ServerRegistry;
        var sb = new System.Text.StringBuilder();

        if (registry.All.Count == 0)
        {
            sb.AppendLine("没有配置任何服务器");
        }
        else
        {
            sb.AppendLine($"已配置的服务器 ({registry.All.Count} 台):");
            foreach (var kv in registry.All)
                sb.AppendLine($"  {kv.Key,-16} {kv.Value.Status}");
        }

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
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

    /// <summary>
    /// 生成 $server 用法文本。
    /// </summary>
    /// <returns>用法说明。</returns>
    private static string Help() =>
        "用法: $server <动作> ...\n" +
        "  cfg get|getall|set|rm <服务器名> [键] [值]   服务器配置（server.properties）\n" +
        "  arg get|set|rm <服务器名> <参数> [值...]     启动参数（javaPath/jvmArgs/jarArgs/appendArgs）\n" +
        "  ck wl|op|bp|bip <服务器名> [名称]            名单检查（白名单/OP/封禁玩家/封禁IP）\n" +
        "  buf read|update <服务器名>                   读取 / 读取并清空输出缓冲区\n" +
        "  ls                                          列出所有服务器及状态\n" +
        "  bp <服务器名> [备注]                         备份世界目录\n" +
        "  query <服务器名|all>                         查询 Query 信息\n" +
        "  status [服务器名|all]                        查看进程状态\n" +
        "  stop <服务器名|all>                          停止服务器\n" +
        "  run <服务器名>                               启动服务器\n" +
        "  send <服务器名|all> <命令>                   发送 Minecraft 命令\n" +
        "用 $help $server 查看详细说明。";
}
