using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server query 动作的实现：查询服务器的 Query 信息。
/// </summary>
internal static class ServerQueryHandler
{
    /// <summary>
    /// 处理 query 动作。
    /// </summary>
    /// <param name="rest">query 之后的参数文本：服务器名或 all。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> RunAsync(string rest, CommandArgs args, IOutputWriter? output)
    {
        var registry = args.ServerRegistry;
        List<IServer> targets;

        if (string.IsNullOrWhiteSpace(rest) || rest.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets = registry.All.Values.ToList();
        }
        else
        {
            var server = registry.GetServer(rest);
            if (server == null)
                return Fail(output, $"未找到服务器 '{rest}'");
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

    /// <summary>输出错误信息并构造失败结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}

/// <summary>
/// $server status 动作的实现：查看服务器进程状态。
/// </summary>
internal static class ServerStatusHandler
{
    /// <summary>
    /// 处理 status 动作。
    /// </summary>
    /// <param name="rest">status 之后的参数文本：服务器名或 all。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Run(string rest, CommandArgs args, IOutputWriter? output)
    {
        var registry = args.ServerRegistry;
        List<IServer> targets;

        if (string.IsNullOrWhiteSpace(rest) || rest.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets = registry.All.Values.ToList();
        }
        else
        {
            var server = registry.GetServer(rest);
            if (server == null)
                return Fail(output, $"未找到服务器 '{rest}'");
            targets = new List<IServer> { server };
        }

        var sb = new StringBuilder();
        bool anyRunning = false;
        foreach (var svr in targets)
        {
            var proc = svr.GetProcess() as System.Diagnostics.Process;
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

    /// <summary>输出错误信息并构造失败结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}

/// <summary>
/// $server run 动作的实现：启动指定服务器，必要时自动切换高亮。
/// </summary>
internal static class ServerRunHandler
{
    /// <summary>
    /// 处理 run 动作。
    /// </summary>
    /// <param name="rest">run 之后的参数文本：服务器名。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> RunAsync(string rest, CommandArgs args, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return Fail(output, "用法: $server run <服务器名>");

        var name = rest.Trim();
        var server = args.ServerRegistry.GetServer(name);
        if (server == null)
            return Fail(output, $"未找到服务器 '{name}'");

        await server.StartAsync();

        // 若未设置高亮，则自动切换到刚启动的服务器
        if (args.ServerRegistry.HighlightedServerName == null)
            args.ServerRegistry.SwitchHighlight(name);

        var msg = $"服务器 '{name}' 已启动";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>输出错误信息并构造失败结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}

/// <summary>
/// $server send 动作的实现：向指定服务器发送 Minecraft 命令。
/// </summary>
internal static class ServerSendHandler
{
    /// <summary>
    /// 处理 send 动作（单个服务器）。
    /// </summary>
    /// <param name="rest">send 之后的参数文本：服务器名与命令。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> RunAsync(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return Fail(output, "用法: $server send <服务器名> <命令>");

        var name = parts[0];
        var command = parts[1].Trim();

        // 目标为 all 时走广播，而不是当作服务器名查找
        if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
            return await ServerSendAllHandler.RunAsync(command, args, output);

        var server = args.ServerRegistry.GetServer(name);
        if (server == null)
            return Fail(output, $"未找到服务器 '{name}'");

        if (server.Status != ServerStatus.Running)
            return Fail(output, $"服务器 '{name}' 未运行，无法发送命令");

        await server.SendCommandAsync(command);

        var msg = $"已向服务器 '{name}' 发送命令: {command}";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>输出错误信息并构造失败结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}

/// <summary>
/// $server send all 的实现：向所有正在运行的服务器广播命令。
/// </summary>
internal static class ServerSendAllHandler
{
    /// <summary>
    /// 处理广播发送。
    /// </summary>
    /// <param name="command">要发送的命令。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> RunAsync(string command, CommandArgs args, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Fail(output, "用法: $server send all <命令>");

        var running = args.ServerRegistry.GetRunningServers();
        if (running.Count == 0)
            return Fail(output, "没有正在运行的服务器");

        int success = 0, fail = 0;
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

        var msg = $"发送完成: 成功 {success}, 失败 {fail}";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>输出错误信息并构造失败结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}

/// <summary>
/// $server stop 动作的实现：停止指定服务器或全部服务器。
/// </summary>
internal static class ServerStopHandler
{
    /// <summary>
    /// 处理 stop 动作。
    /// </summary>
    /// <param name="rest">stop 之后的参数文本：服务器名（可带 -f）或 all。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> RunAsync(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, "用法: $server stop <服务器名|all> [-f]");

        // all：停止全部
        if (parts[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            return await ServerStopAllHandler.RunAsync(args, output);

        var name = parts[0];
        var force = parts.Length > 1 && parts[1].Equals("-f", StringComparison.OrdinalIgnoreCase);

        var server = args.ServerRegistry.GetServer(name);
        if (server == null)
            return Fail(output, $"未找到服务器 '{name}'");

        await server.StopAsync(force);

        var msg = $"服务器 '{name}' 已停止";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>输出错误信息并构造失败结果。</summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <param name="message">错误信息。</param>
    /// <returns>退出码为 0 的失败结果。</returns>
    private static CommandResult Fail(IOutputWriter? output, string message)
    {
        output?.Write("Command", LogLevel.Error, message);
        return new CommandResult(0, message);
    }
}

/// <summary>
/// $server stop all 的实现：停止所有正在运行的服务器。
/// </summary>
internal static class ServerStopAllHandler
{
    /// <summary>
    /// 停止所有服务器。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> RunAsync(CommandArgs args, IOutputWriter? output)
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
