using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;
using System.Diagnostics;
using System.Text;

namespace MSL_CLI.Infrastructure.Commands
{
    /// <summary>
    /// 统一服务器管理命令：server
    /// </summary>
    public class ServerCommand : ICommand
    {
        /// <summary>
        /// 命令名称：$server。
        /// </summary>
        public string Name => "$server";
        /// <summary>
        /// 命令描述：统一服务器管理命令。
        /// </summary>
        public string Description => "统一服务器管理命令，用法：server <子命令> [参数]";

        /// <summary>
        /// 执行 $server 命令，解析子命令并分发到对应的处理器。
        /// </summary>
        /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
        /// <param name="output">输出写入器，用于输出命令结果，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        {
            var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return Help(output);
            }
            // 首个单词为子命令，其余部分为子命令参数
            var subCmd = parts[0].ToLowerInvariant();
            var subArgs = string.Join(" ", parts.Skip(1));

            // 根据子命令分发到对应的处理函数
            return subCmd switch
            {
                "run" => await HandleRunAsync(subArgs, args, output),
                "stop" => await HandleStopAsync(subArgs, args, output),
                "send" => await HandleSendAsync(subArgs, args, output),
                "buffer" => await HandleBufferAsync(subArgs, args, output),
                "config" => await HandleConfigAsync(subArgs, args, output),
                "query" => await HandleQueryAsync(subArgs, args, output),
                "status" => await HandleStatusAsync(subArgs, args, output),
                _ => Help(output)
            };
        }

        /// <summary>
        /// 输出 $server 的帮助信息。
        /// </summary>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private CommandResult Help(IOutputWriter? output)
        {
            var helpText =
                "用法: server <子命令> [参数]\n" +
                "子命令:\n" +
                "  run <ServerName>                    启动服务器\n" +
                "  stop <ServerName|all> [-f]          停止服务器（-f 强制）\n" +
                "  send <ServerName|all> <命令>        向服务器发送 Minecraft 命令\n" +
                "  buffer read|update <ServerName>     读取/清空服务器缓冲区\n" +
                "  config get|set <ServerName> ...     查看/修改 server.properties\n" +
                "  query <ServerName|all>              查询服务器 Query 信息\n" +
                "  status [ServerName]                 查看服务器进程状态（默认全部）";
            output?.Write("Command", LogLevel.Error, helpText);
            return new CommandResult(0, helpText);
        }

        // ---------- 子命令处理 ----------

        /// <summary>
        /// 处理 run 子命令：启动指定服务器。
        /// </summary>
        /// <param name="subArgs">子命令参数，即服务器名。</param>
        /// <param name="args">命令参数，包含运行环境依赖。</param>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private async Task<CommandResult> HandleRunAsync(string subArgs, CommandArgs args, IOutputWriter? output)
        {
            if (string.IsNullOrWhiteSpace(subArgs))
            {
                var msg = "用法: server run <ServerName>";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            // 委托给 ServerRunCommand 执行
            var cmd = new ServerRunCommand();
            var newArgs = new CommandArgs(subArgs.Trim(), args.ServerRegistry, args.AgentService, args.ConfigStore);
            return await cmd.ExecuteAsync(newArgs, output);
        }

        /// <summary>
        /// 处理 stop 子命令：停止指定服务器（支持 all 与 -f 强制停止）。
        /// </summary>
        /// <param name="subArgs">子命令参数，即服务器名（可带 -f）。</param>
        /// <param name="args">命令参数，包含运行环境依赖。</param>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private async Task<CommandResult> HandleStopAsync(string subArgs, CommandArgs args, IOutputWriter? output)
        {
            if (string.IsNullOrWhiteSpace(subArgs))
            {
                var msg = "用法: server stop <ServerName|all> [-f]";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var parts = subArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                var msg = "缺少服务器名";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            // 检查是否针对 all
            if (parts[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                // 忽略 -f（原 StopAllCommand 无强制选项，如有需要可扩展）
                var cmd = new ServerStopAllCommand();
                // StopAllCommand 的 ExecuteAsync 不需要 Raw，但为避免空，可传 null 或空
                var newArgs = new CommandArgs(string.Empty, args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
            else
            {
                // 单个服务器，原样传递给 StopCommand
                var cmd = new ServerStopCommand();
                var newArgs = new CommandArgs(subArgs, args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
        }

        /// <summary>
        /// 处理 send 子命令：向服务器发送 Minecraft 命令（支持 all 广播）。
        /// </summary>
        /// <param name="subArgs">子命令参数，即目标服务器与命令内容。</param>
        /// <param name="args">命令参数，包含运行环境依赖。</param>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private async Task<CommandResult> HandleSendAsync(string subArgs, CommandArgs args, IOutputWriter? output)
        {
            if (string.IsNullOrWhiteSpace(subArgs))
            {
                var msg = "用法: server send <ServerName|all> <命令>";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var parts = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                var msg = "缺少命令内容";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var target = parts[0];
            var command = parts[1].Trim();

            // 目标为 all 时广播，否则发送给指定服务器
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new ServerSendAllCommand();
                var newArgs = new CommandArgs(command, args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
            else
            {
                var cmd = new ServerSendCommand();
                var newArgs = new CommandArgs($"{target} {command}", args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
        }

        /// <summary>
        /// 处理 buffer 子命令：读取或清空服务器缓冲区。
        /// </summary>
        /// <param name="subArgs">子命令参数，即操作类型（read/update）与服务器名。</param>
        /// <param name="args">命令参数，包含运行环境依赖。</param>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private async Task<CommandResult> HandleBufferAsync(string subArgs, CommandArgs args, IOutputWriter? output)
        {
            if (string.IsNullOrWhiteSpace(subArgs))
            {
                var msg = "用法: server buffer read|update <ServerName>";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var parts = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                var msg = "缺少操作类型或服务器名";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var action = parts[0].ToLowerInvariant();
            var serverName = parts[1].Trim();

            // 根据 read/update 选择对应的缓冲区处理命令
            if (action == "read")
            {
                var cmd = new ServerBufferReadCommand();
                var newArgs = new CommandArgs(serverName, args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
            else if (action == "update")
            {
                var cmd = new ServerBufferUpdateCommand();
                var newArgs = new CommandArgs(serverName, args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
            else
            {
                var msg = "无效操作，请使用 read 或 update";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
        }

        /// <summary>
        /// 处理 config 子命令：查看或修改 server.properties。
        /// </summary>
        /// <param name="subArgs">子命令参数，即操作类型（get/set）与服务器名。</param>
        /// <param name="args">命令参数，包含运行环境依赖。</param>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private async Task<CommandResult> HandleConfigAsync(string subArgs, CommandArgs args, IOutputWriter? output)
        {
            if (string.IsNullOrWhiteSpace(subArgs))
            {
                var msg = "用法: server config get|set <ServerName> ...";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var parts = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                var msg = "缺少操作类型或参数";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var action = parts[0].ToLowerInvariant();
            var rest = parts[1].Trim();

            // 根据 get/set 选择对应的配置处理命令
            if (action == "get")
            {
                var cmd = new ServerConfigGetCommand();
                var newArgs = new CommandArgs(rest, args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
            else if (action == "set")
            {
                var cmd = new ServerConfigSetCommand();
                var newArgs = new CommandArgs(rest, args.ServerRegistry, args.AgentService, args.ConfigStore);
                return await cmd.ExecuteAsync(newArgs, output);
            }
            else
            {
                var msg = "无效操作，请使用 get 或 set";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
        }

        /// <summary>
        /// 处理 query 子命令：查询服务器 Query 信息。
        /// </summary>
        /// <param name="subArgs">子命令参数，即目标服务器名。</param>
        /// <param name="args">命令参数，包含运行环境依赖。</param>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private async Task<CommandResult> HandleQueryAsync(string subArgs, CommandArgs args, IOutputWriter? output)
        {
            // 将 all 归一化为空字符串，表示查询全部服务器
            var raw = subArgs.Trim();
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
                raw = "";

            var cmd = new ServerQueryCommand();
            var newArgs = new CommandArgs(raw, args.ServerRegistry, args.AgentService, args.ConfigStore);
            return await cmd.ExecuteAsync(newArgs, output);
        }

        /// <summary>
        /// 处理 status 子命令：查看服务器进程状态。
        /// </summary>
        /// <param name="subArgs">子命令参数，即目标服务器名。</param>
        /// <param name="args">命令参数，包含运行环境依赖。</param>
        /// <param name="output">输出写入器，可为 null。</param>
        /// <returns>命令执行结果。</returns>
        private async Task<CommandResult> HandleStatusAsync(string subArgs, CommandArgs args, IOutputWriter? output)
        {
            // 同样处理 all
            var raw = subArgs.Trim();
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
                raw = "";

            var cmd = new ServerStatusCommand();
            var newArgs = new CommandArgs(raw, args.ServerRegistry, args.AgentService, args.ConfigStore);
            return await cmd.ExecuteAsync(newArgs, output);
        }
    }
}

/// <summary>
/// 查看或修改服务器启动参数的命令（$serverargument）。
/// </summary>
public class ServerArgumentCommand : ICommand
{
    /// <summary>
    /// 命令名称：$serverargument。
    /// </summary>
    public string Name => "$serverargument";
    /// <summary>
    /// 命令描述：查看或修改服务器的启动参数。
    /// </summary>
    public string Description => "查看或修改服务器的启动参数。用法：$serverargument get <服务器名|all> <参数> | $serverargument set <服务器名> <参数> <值>";

    /// <summary>
    /// 执行 $serverargument 命令，根据 get/set 子命令分发处理。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $serverargument get <服务器名|all> <参数> 或 $serverargument set <服务器名> <参数> <值>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            var msg = "参数不足，请查看帮助";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        string subCmd = parts[0].ToLowerInvariant();
        // 根据子命令分发到 get/set 处理
        if (subCmd == "get")
            return await HandleGet(parts.Skip(1).ToArray(), args, output);
        else if (subCmd == "set")
            return await HandleSet(parts.Skip(1).ToArray(), args, output);
        else
        {
            var msg = $"未知子命令 '{subCmd}'，请使用 get 或 set";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }

    /// <summary>
    /// 处理 get 子命令：读取指定服务器的启动参数。
    /// </summary>
    /// <param name="parts">子命令参数，依次为服务器名与参数名。</param>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private async Task<CommandResult> HandleGet(string[] parts, CommandArgs args, IOutputWriter? output)
    {
        if (parts.Length != 2)
        {
            var msg = "用法: $serverargument get <服务器名|all> <参数>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        string serverTarget = parts[0];
        string param = parts[1].ToLowerInvariant();

        var validParams = new[] { "javapath", "jvmargs", "jarargs", "append", "javaargs", "all" };
        // 校验参数名是否支持读取
        if (!validParams.Contains(param))
        {
            var msg = $"无效参数 '{param}'，允许: {string.Join(", ", validParams)}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 目标为 all 时遍历所有服务器，否则只查询指定服务器
        List<IServer> servers;
        if (serverTarget == "all")
            servers = args.ServerRegistry.All.Values.ToList();
        else
        {
            var server = args.ServerRegistry.GetServer(serverTarget);
            if (server == null)
            {
                var msg = $"未找到服务器 '{serverTarget}'";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
            servers = new List<IServer> { server };
        }

        var sb = new StringBuilder();
        foreach (var svr in servers)
        {
            var arg = svr.Argument;
            // 按参数名读取对应的启动参数值
            string value = param switch
            {
                "javapath" => arg.GetJavaPath(),
                "jvmargs" => arg.GetJvmArgs(),
                "jarargs" => arg.GetJarArgs(),
                "append" => arg.GetAppendArgs(),
                "javaargs" => arg.GetJavaArgs(),
                "all" => arg.GetStartArguments(),
                _ => ""
            };
            sb.AppendLine($"{svr.Name}: {param} = {value}");
        }

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }

    /// <summary>
    /// 处理 set 子命令：修改指定服务器的启动参数。
    /// </summary>
    /// <param name="parts">子命令参数，依次为服务器名、参数名与参数值。</param>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private async Task<CommandResult> HandleSet(string[] parts, CommandArgs args, IOutputWriter? output)
    {
        if (parts.Length < 3)
        {
            var msg = "用法: $serverargument set <服务器名> <参数> <值>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        string serverName = parts[0];
        string param = parts[1].ToLowerInvariant();
        string value = string.Join(" ", parts.Skip(2));

        var validSetParams = new[] { "javapath", "jvmargs", "jarargs", "append" };
        // 校验参数名是否允许设置
        if (!validSetParams.Contains(param))
        {
            var msg = $"参数 '{param}' 不允许设置，允许: {string.Join(", ", validSetParams)}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"未找到服务器 '{serverName}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var arg = server.Argument;
        // 按参数名写入对应的启动参数
        switch (param)
        {
            case "javapath": arg.SetJavaPath(value); break;
            case "jvmargs": arg.SetJvmArgs(value); break;
            case "jarargs": arg.SetJarArgs(value); break;
            case "append": arg.SetAppendArgs(value); break;
        }

        var message = $"服务器 '{serverName}' 的 {param} 已更新为 '{value}'（修改不会持久化到启动脚本，重启服务器后生效）";
        output?.Write("Command", LogLevel.Success, message);
        return new CommandResult(1, message);
    }
}

/// <summary>
/// 读取指定服务器缓冲区内容的命令（$bufferread），读取后不清空缓冲区。
/// </summary>
public class ServerBufferReadCommand : ICommand
{
    /// <summary>
    /// 命令名称：$bufferread。
    /// </summary>
    public string Name => "$bufferread";
    /// <summary>
    /// 命令描述：读取指定服务器的缓冲区内容（不清空）。
    /// </summary>
    public string Description => "读取指定服务器的缓冲区内容（不清空），用法: $bufferread <服务器名>";

    /// <summary>
    /// 执行 $bufferread 命令，读取并输出指定服务器的缓冲区内容。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $bufferread <服务器名>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1)
        {
            var msg = "参数错误，用法: $bufferread <服务器名>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var serverName = parts[0];
        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"未找到服务器 '{serverName}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var content = server.GetBufferContent(); // 需扩展方法
        if (string.IsNullOrEmpty(content))
        {
            var msg = $"服务器 '{serverName}' 的缓冲区为空";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        else
        {
            var msg = $"服务器 '{serverName}' 的缓冲区内容:\n{content}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
    }
}

/// <summary>
/// $bufferread 的简写命令（$bufr）。
/// </summary>
public class ServerBufferReadAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称：$bufr。
    /// </summary>
    public string Name => "$bufr";
    /// <summary>
    /// 命令描述：$bufferread 的简写。
    /// </summary>
    public string Description => "$bufferread 的简写";

    /// <summary>
    /// 内部封装的 ServerBufferReadCommand 实例，实际处理逻辑由其完成。
    /// </summary>
    private readonly ServerBufferReadCommand _inner = new();

    /// <summary>
    /// 执行命令，转发给内部封装的 ServerBufferReadCommand。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}

/// <summary>
/// 读取并清空指定服务器缓冲区内容的命令（$bufferupdate）。
/// </summary>
public class ServerBufferUpdateCommand : ICommand
{
    /// <summary>
    /// 命令名称：$bufferupdate。
    /// </summary>
    public string Name => "$bufferupdate";
    /// <summary>
    /// 命令描述：读取并清空指定服务器的缓冲区。
    /// </summary>
    public string Description => "读取并清空指定服务器的缓冲区，用法: $bufferupdate <服务器名>";

    /// <summary>
    /// 执行 $bufferupdate 命令，读取并清空指定服务器的缓冲区内容。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $bufferupdate <服务器名>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1)
        {
            var msg = "参数错误，用法: $bufferupdate <服务器名>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var serverName = parts[0];
        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"未找到服务器 '{serverName}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var content = server.GetAndClearBufferContent(); // 需实现
        if (string.IsNullOrEmpty(content))
        {
            var resultMsg = $"服务器 '{serverName}' 的缓冲区为空（已清空）";
            output?.Write("Command", LogLevel.Success, resultMsg);
            return new CommandResult(1, resultMsg);
        }
        else
        {
            var resultMsg = $"服务器 '{serverName}' 的缓冲区内容（已清空）:\n{content}";
            output?.Write("Command", LogLevel.Success, resultMsg);
            return new CommandResult(1, resultMsg);
        }
    }
}

/// <summary>
/// $bufferupdate 的简写命令（$bufu）。
/// </summary>
public class ServerBufferUpdateAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称：$bufu。
    /// </summary>
    public string Name => "$bufu";
    /// <summary>
    /// 命令描述：$bufferupdate 的简写。
    /// </summary>
    public string Description => "$bufferupdate 的简写";

    /// <summary>
    /// 内部封装的 ServerBufferUpdateCommand 实例，实际处理逻辑由其完成。
    /// </summary>
    private readonly ServerBufferUpdateCommand _inner = new();

    /// <summary>
    /// 执行命令，转发给内部封装的 ServerBufferUpdateCommand。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}

/// <summary>
/// 查看服务器 server.properties 配置的辅助类，支持服务器名/键名与高亮服务器解析。
/// </summary>
public static class ServerConfigGetHelper
{
    /// <summary>
    /// 执行配置读取：解析目标服务器与键名，并输出配置内容。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="useHighlight">是否允许使用高亮服务器作为默认目标。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> ExecuteAsync(CommandArgs args, bool useHighlight, IOutputWriter? output)
    {
        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? serverName = null;
        string? key = null;

        // ---------- 解析参数 ----------
        if (parts.Length == 0)
        {
            // 无参数：必须使用高亮服务器（useHighlight 为 true 或 false 都尝试高亮）
            var hl = args.ServerRegistry.GetHighlightedServer();
            if (hl == null)
            {
                var msg = "未设置高亮服务器，请先使用 $highlight/$hl 设置";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
            serverName = hl.Name;
            // key 保持 null，表示输出全部配置
        }
        else if (parts.Length == 1)
        {
            // 尝试作为服务器名
            if (args.ServerRegistry.GetServer(parts[0]) != null)
            {
                serverName = parts[0];
                // key 为 null，输出该服务器全部配置
            }
            else
            {
                // 不是服务器名，视为键名：必须使用高亮服务器
                var hl = args.ServerRegistry.GetHighlightedServer();
                if (hl == null)
                {
                    var msg = "未设置高亮服务器，且参数不是服务器名";
                    output?.Write("Command", LogLevel.Error, msg);
                    return new CommandResult(0, msg);
                }
                serverName = hl.Name;
                key = parts[0];
            }
        }
        else if (parts.Length == 2)
        {
            // 两个参数：第一个是服务器名，第二个是键名
            if (args.ServerRegistry.GetServer(parts[0]) != null)
            {
                serverName = parts[0];
                key = parts[1];
            }
            else
            {
                // 如果第一个不是服务器名，则尝试使用高亮（忽略 useHighlight 参数，统一按此规则）
                // 但旧版逻辑中，如果第一个参数不是服务器名，则视为键，使用高亮服务器。
                var hl = args.ServerRegistry.GetHighlightedServer();
                if (hl == null)
                {
                    var msg = "第一个参数不是服务器名，且未设置高亮服务器";
                    output?.Write("Command", LogLevel.Error, msg);
                    return new CommandResult(0, msg);
                }
                serverName = hl.Name;
                key = parts[0];  // 第一个作为键
                // 第二个参数被忽略？旧版会报错，但通常不会出现这种情况，我们仍处理。
                // 实际旧版只允许最多两个参数，且第二个必须是键名。
            }
        }
        else
        {
            var msg = "参数过多，用法: $serverconfigget [服务器名] [键名] 或 $serverconfigget [键名] (使用高亮服务器)";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // ---------- 获取服务器 ----------
        if (string.IsNullOrEmpty(serverName))
        {
            var msg = "无法确定服务器，请检查参数或设置高亮";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"服务器 '{serverName}' 不存在";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // ---------- 执行输出 ----------
        if (key == null)
        {
            // 输出全部配置
            var entries = server.Properties.Entries;
            if (entries.Count == 0)
            {
                var msg = $"服务器 '{serverName}' 的配置为空";
                output?.Write("Command", LogLevel.Success, msg);
                return new CommandResult(1, msg);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"服务器 '{serverName}' 的全部配置 ({entries.Count} 项):");
            foreach (var kv in entries)
                sb.AppendLine($"  {kv.Key} = {kv.Value}");
            output?.Write("Command", LogLevel.Success, sb.ToString());
            return new CommandResult(1, sb.ToString());
        }
        else
        {
            // 输出单个键值
            var value = server.Properties.GetValue(key);
            if (value == null)
            {
                var msg = $"键 '{key}' 不存在或未设置";
                output?.Write("Command", LogLevel.Success, msg);
                return new CommandResult(1, msg);
            }
            else
            {
                var msg = $"{serverName}.{key} = {value}";
                output?.Write("Command", LogLevel.Success, msg);
                return new CommandResult(1, msg);
            }
        }
    }
}

/// <summary>
/// 修改服务器 server.properties 配置的辅助类，支持服务器名或高亮服务器解析。
/// </summary>
internal static class ServerConfigSetHelper
{
    /// <summary>
    /// 执行配置修改：解析服务器名、键与值，并写入配置。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="useHighlight">是否允许使用高亮服务器作为默认目标。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> ExecuteAsync(CommandArgs args, bool useHighlight, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $serverconfigset <服务器名> <键> <值>  或  $serverconfigset <键> <值> (使用高亮服务器)";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? serverName = null;
        string key;
        string value;

        if (parts.Length == 2)
        {
            if (useHighlight)
            {
                var hl = args.ServerRegistry.GetHighlightedServer();
                if (hl == null)
                {
                    var msg = "未设置高亮服务器，请先使用 $highlight/$hl 设置或指定服务器名";
                    output?.Write("Command", LogLevel.Error, msg);
                    return new CommandResult(0, msg);
                }
                serverName = hl.Name;
                key = parts[0];
                value = parts[1];
            }
            else
            {
                var msg = "参数不足，需要键和值，或使用 $serverconfigsetnow/$scsn 直接操作高亮服务器";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
        }
        else if (parts.Length >= 3)
        {
            // 检查第一个参数是否为服务器名
            if (args.ServerRegistry.GetServer(parts[0]) != null)
            {
                serverName = parts[0];
                key = parts[1];
                value = string.Join(" ", parts.Skip(2));
            }
            else
            {
                // 如果第一个不是服务器名，则使用高亮
                if (useHighlight || args.ServerRegistry.HighlightedServerName != null)
                {
                    serverName = args.ServerRegistry.HighlightedServerName;
                    if (string.IsNullOrEmpty(serverName))
                    {
                        var msg = "未设置高亮服务器，且第一个参数不是服务器名";
                        output?.Write("Command", LogLevel.Error, msg);
                        return new CommandResult(0, msg);
                    }
                    key = parts[0];
                    value = string.Join(" ", parts.Skip(1));
                }
                else
                {
                    var msg = "无法识别服务器名或高亮服务器未设置";
                    output?.Write("Command", LogLevel.Error, msg);
                    return new CommandResult(0, msg);
                }
            }
        }
        else
        {
            var msg = "参数不足，用法: $serverconfigset <服务器名> <键> <值>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"未找到服务器 '{serverName}' 或无法获取配置";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        server.Properties.SetValue(key, value);
        var successMsg = $"{serverName}.{key} = {value} 已更新";
        output?.Write("Command", LogLevel.Success, successMsg);
        return new CommandResult(1, successMsg);
    }
}

/// <summary>
/// 获取服务器配置的命令（$serverconfigget）。
/// </summary>
public class ServerConfigGetCommand : ICommand
{
    /// <summary>
    /// 命令名称：$serverconfigget。
    /// </summary>
    public string Name => "$serverconfigget";
    /// <summary>
    /// 命令描述：获取服务器配置。
    /// </summary>
    public string Description => "获取服务器配置";

    /// <summary>
    /// 执行 $serverconfigget 命令，读取服务器配置。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        return await ServerConfigGetHelper.ExecuteAsync(args, false, output);
    }
}

/// <summary>
/// $serverconfigget 的简写命令（$scg）。
/// </summary>
public class ServerConfigGetAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称：$scg。
    /// </summary>
    public string Name => "$scg";
    /// <summary>
    /// 命令描述：$serverconfigget 的简写。
    /// </summary>
    public string Description => "$serverconfigget 的简写";
    /// <summary>
    /// 内部封装的 ServerConfigGetCommand 实例。
    /// </summary>
    private readonly ServerConfigGetCommand _inner = new();

    /// <summary>
    /// 执行命令，转发给内部封装的 ServerConfigGetCommand。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}

/// <summary>
/// 设置服务器 server.properties 配置的命令（$serverconfigset）。
/// </summary>
public class ServerConfigSetCommand : ICommand
{
    /// <summary>
    /// 命令名称：$serverconfigset。
    /// </summary>
    public string Name => "$serverconfigset";
    /// <summary>
    /// 命令描述：设置服务器 server.properties 配置。
    /// </summary>
    public string Description => "设置服务器 server.properties 配置，用法: $serverconfigset <服务器名> <键> <值> 或 $serverconfigset <键> <值> (使用高亮服务器)";

    /// <summary>
    /// 执行 $serverconfigset 命令，修改服务器配置。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        return await ServerConfigSetHelper.ExecuteAsync(args, false, output);
    }
}

/// <summary>
/// $serverconfigset 的简写命令（$scs）。
/// </summary>
public class ServerConfigSetAliasCommand : ICommand
{
    /// <summary>
    /// 命令名称：$scs。
    /// </summary>
    public string Name => "$scs";
    /// <summary>
    /// 命令描述：$serverconfigset 的简写。
    /// </summary>
    public string Description => "$serverconfigset 的简写";

    /// <summary>
    /// 内部封装的 ServerConfigSetCommand 实例。
    /// </summary>
    private readonly ServerConfigSetCommand _inner = new();

    /// <summary>
    /// 执行命令，转发给内部封装的 ServerConfigSetCommand。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
        => await _inner.ExecuteAsync(args, output);
}

/// <summary>
/// 启动指定服务器的命令（$run）。
/// </summary>
public class ServerRunCommand : ICommand
{
    /// <summary>
    /// 命令名称：$run。
    /// </summary>
    public string Name => "$run";
    /// <summary>
    /// 命令描述：启动指定服务器。
    /// </summary>
    public string Description => "启动指定服务器，用法: $run <服务器名>";

    /// <summary>
    /// 执行 $run 命令，启动指定服务器；若未设置高亮服务器则自动切换。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $run <服务器名>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var name = args.Raw.Trim();
        var server = args.ServerRegistry.GetServer(name);
        if (server == null)
        {
            var msg = $"未找到服务器 '{name}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        await server.StartAsync();

        // 若未设置高亮，则自动切换
        if (args.ServerRegistry.HighlightedServerName == null)
            args.ServerRegistry.SwitchHighlight(name);

        var message = $"服务器 '{name}' 已启动";
        output?.Write("Command", LogLevel.Success, message);
        return new CommandResult(1, message);
    }
}

/// <summary>
/// 向指定服务器发送 Minecraft 命令的命令（$send）。
/// </summary>
public class ServerSendCommand : ICommand
{
    /// <summary>
    /// 命令名称：$send。
    /// </summary>
    public string Name => "$send";
    /// <summary>
    /// 命令描述：向指定服务器发送 Minecraft 命令。
    /// </summary>
    public string Description => "向指定服务器发送 Minecraft 命令，用法: $send <服务器名> <命令>";

    /// <summary>
    /// 执行 $send 命令，向指定服务器发送命令（服务器必须处于运行状态）。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            var msg = "用法: $send <服务器名> <命令>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var name = parts[0];
        var command = parts[1].Trim();

        var server = args.ServerRegistry.GetServer(name);
        if (server == null)
        {
            var msg = $"未找到服务器 '{name}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        if (server.Status != ServerStatus.Running)
        {
            var msg = $"服务器 '{name}' 未运行，无法发送命令";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        await server.SendCommandAsync(command);

        var message = $"已向服务器 '{name}' 发送命令: {command}";
        output?.Write("Command", LogLevel.Success, message);
        return new CommandResult(1, message);
    }
}

/// <summary>
/// 向所有正在运行的服务器发送命令的命令（$sendall）。
/// </summary>
public class ServerSendAllCommand : ICommand
{
    /// <summary>
    /// 命令名称：$sendall。
    /// </summary>
    public string Name => "$sendall";
    /// <summary>
    /// 命令描述：向所有正在运行的服务器发送命令。
    /// </summary>
    public string Description => "向所有正在运行的服务器发送命令，用法: $sendall <命令>";

    /// <summary>
    /// 执行 $sendall 命令，向所有正在运行的服务器广播命令并统计结果。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $sendall <命令>";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var command = args.Raw.Trim();
        var running = args.ServerRegistry.GetRunningServers();
        if (running.Count == 0)
        {
            var msg = "没有正在运行的服务器";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        int success = 0, fail = 0;
        // 逐台发送命令并统计成功/失败数量
        foreach (var sm in running)
        {
            try
            {
                await sm.SendCommandAsync(command);
                success++;
            }
            catch (Exception ex)
            {
                fail++;
                output?.Write("Command", LogLevel.Error, $"向 '{sm.Name}' 发送命令失败: {ex.Message}");
            }
        }
        var message = $"发送完成: 成功 {success}, 失败 {fail}";
        output?.Write("Command", LogLevel.Success, message);
        return new CommandResult(1, message);
    }
}

/// <summary>
/// 查看服务器进程状态的命令（$status）。
/// </summary>
public class ServerStatusCommand : ICommand
{
    /// <summary>
    /// 命令名称：$status。
    /// </summary>
    public string Name => "$status";
    /// <summary>
    /// 命令描述：查看服务器状态。
    /// </summary>
    public string Description => "查看服务器状态";

    /// <summary>
    /// 执行 $status 命令，输出指定（或全部）服务器的进程运行状态。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var registry = args.ServerRegistry;
        List<IServer> targets;

        if (string.IsNullOrWhiteSpace(args.Raw))
            targets = registry.All.Values.ToList();
        else
        {
            var name = args.Raw.Trim();
            var server = registry.GetServer(name);
            if (server == null)
            {
                var msg = $"未找到服务器 '{name}'";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
            targets = new List<IServer> { server };
        }

        var sb = new StringBuilder();
        bool anyRunning = false;
        foreach (var svr in targets)
        {
            var procObj = svr.GetProcess();
            var proc = procObj as Process;
            // 进程不存在或已退出则视为未运行
            if (proc == null || proc.HasExited)
            {
                sb.AppendLine($"服务器 '{svr.Name}' 未运行");
                continue;
            }
            anyRunning = true;
            try
            {
                // 刷新进程信息，统计内存与 CPU 占用
                proc.Refresh();
                long memoryMB = proc.WorkingSet64 / (1024 * 1024);
                double cpuTime = proc.TotalProcessorTime.TotalSeconds;
                sb.AppendLine($"服务器 '{svr.Name}' - PID: {proc.Id}, 内存: {memoryMB} MB, CPU时间: {cpuTime:F2}s");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"获取 '{svr.Name}' 状态失败: {ex.Message}");
            }
        }
        if (!anyRunning)
            sb.AppendLine("没有正在运行的服务器");

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }
}

/// <summary>
/// 停止指定服务器的命令（$stop），支持 -f 强制停止。
/// </summary>
public class ServerStopCommand : ICommand
{
    /// <summary>
    /// 命令名称：$stop。
    /// </summary>
    public string Name => "$stop";
    /// <summary>
    /// 命令描述：停止指定服务器。
    /// </summary>
    public string Description => "停止指定服务器，用法: $stop <服务器名> [-f]";

    /// <summary>
    /// 执行 $stop 命令，停止指定服务器（可选强制停止）。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            var msg = "用法: $stop <服务器名> [-f]";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var name = parts[0];
        var force = parts.Length > 1 && parts[1].Equals("-f", StringComparison.OrdinalIgnoreCase);

        var server = args.ServerRegistry.GetServer(name);
        if (server == null)
        {
            var msg = $"未找到服务器 '{name}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        await server.StopAsync(force);

        var message = $"服务器 '{name}' 已停止";
        output?.Write("Command", LogLevel.Success, message);
        return new CommandResult(1, message);
    }
}

/// <summary>
/// 停止所有正在运行的服务器的命令（$stopall）。
/// </summary>
public class ServerStopAllCommand : ICommand
{
    /// <summary>
    /// 命令名称：$stopall。
    /// </summary>
    public string Name => "$stopall";
    /// <summary>
    /// 命令描述：停止所有正在运行的服务器。
    /// </summary>
    public string Description => "停止所有正在运行的服务器";

    /// <summary>
    /// 执行 $stopall 命令，停止所有正在运行的服务器。
    /// </summary>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        try
        {
            await args.ServerRegistry.StopAllAsync();
            var msg = "所有服务器已停止";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            var msg = $"停止所有服务器失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }
}

/// <summary>
/// 查询服务器 Query 信息的命令（$query）。
/// </summary>
public class ServerQueryCommand : ICommand
{
    /// <summary>
    /// 命令名称：$query。
    /// </summary>
    public string Name => "$query";
    /// <summary>
    /// 命令描述：查询服务器 Query 信息。
    /// </summary>
    public string Description => "查询服务器 Query 信息，用法: $query <服务器名|all>";

    /// <summary>
    /// 执行 $query 命令，查询指定（或全部）服务器的 Query 信息。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var registry = args.ServerRegistry;
        List<IServer> targets;

        if (string.IsNullOrWhiteSpace(args.Raw))
            targets = registry.All.Values.ToList();
        else
        {
            var name = args.Raw.Trim();
            var server = registry.GetServer(name);
            if (server == null)
            {
                var msg = $"未找到服务器 '{name}'";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }
            targets = new List<IServer> { server };
        }

        var sb = new StringBuilder();
        bool anyQueried = false;
        foreach (var svr in targets)
        {
            var info = await svr.GetQueryInfoAsync();
            if (info == null)
            {
                sb.AppendLine($"服务器 '{svr.Name}' 无法查询（未启用 Query 或服务器未运行）");
                continue;
            }
            anyQueried = true;
            sb.AppendLine($"服务器 '{svr.Name}':");
            foreach (var kv in info)
                sb.AppendLine($"  {kv.Key} = {kv.Value}");
        }
        if (!anyQueried)
            sb.AppendLine("没有可查询的服务器");

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }
}

/// <summary>
/// 列出所有服务器及其状态的命令（$list）。
/// </summary>
public class ServerListCommand : ICommand
{
    /// <summary>
    /// 命令名称：$list。
    /// </summary>
    public string Name => "$list";
    /// <summary>
    /// 命令描述：列出所有服务器及其状态。
    /// </summary>
    public string Description => "列出所有服务器及其状态";

    /// <summary>
    /// 执行 $list 命令，列出所有已配置的服务器及其状态。
    /// </summary>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var registry = args.ServerRegistry;
        var sb = new StringBuilder();

        if (registry.All.Count == 0)
        {
            sb.AppendLine("没有配置任何服务器");
        }
        else
        {
            sb.AppendLine("已配置的服务器:");
            foreach (var kv in registry.All)
                sb.AppendLine($"  {kv.Key} : {kv.Value.Status}");
        }

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }
}
