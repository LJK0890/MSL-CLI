using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server cfg 动作的处理器：读写服务器的 server.properties。
/// 用法：<c>cfg get [服务器名] [键] | cfg getall [服务器名] | cfg set [服务器名] &lt;键&gt; &lt;值&gt; | cfg rm [服务器名] &lt;键&gt;</c>。
/// 省略服务器名时使用高亮服务器。
/// </summary>
internal static class ServerConfigHandler
{
    /// <summary>动作别名到标准动作的映射。</summary>
    private static readonly Dictionary<string, string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["get"] = "get",
        ["getall"] = "getall",
        ["set"] = "set",
        ["rm"] = "rm",
        ["remove"] = "rm"
    };

    /// <summary>
    /// 处理 cfg 动作。
    /// </summary>
    /// <param name="rest">cfg 之后的参数文本。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, "用法: $server cfg get|getall|set|rm [服务器名] [键] [值]");

        if (!Actions.TryGetValue(parts[0], out var action))
            return Fail(output, $"未知配置动作 '{parts[0]}'，可用: get、getall、set、rm");

        var tail = parts.Length > 1 ? parts[1] : string.Empty;
        return action switch
        {
            "get" or "getall" => Get(args, tail, output),
            "set" => Set(args, tail, output),
            "rm" => Remove(args, tail, output),
            _ => Fail(output, "用法: $server cfg get|getall|set|rm [服务器名] [键] [值]")
        };
    }

    /// <summary>
    /// 读取配置：getall 或不带键时输出全部；指定服务器名时读取该服务器，否则使用高亮服务器。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="tail">动作之后的参数：可选的服务器名与键名。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Get(CommandArgs args, string tail, IOutputWriter? output)
    {
        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? serverName;
        string? key = null;

        if (parts.Length == 0)
        {
            serverName = ResolveServer(args, null, out var error);
            if (serverName == null) return Fail(output, error!);
        }
        else if (parts.Length == 1)
        {
            if (args.ServerRegistry.GetServer(parts[0]) != null)
            {
                serverName = parts[0];
            }
            else
            {
                // 单个参数不是服务器名 → 视为键名，使用高亮服务器
                serverName = ResolveServer(args, null, out var error);
                if (serverName == null) return Fail(output, error!);
                key = parts[0];
            }
        }
        else
        {
            if (args.ServerRegistry.GetServer(parts[0]) != null)
            {
                serverName = parts[0];
                key = parts[1];
            }
            else
            {
                serverName = ResolveServer(args, null, out var error);
                if (serverName == null) return Fail(output, error!);
                key = parts[0];
            }
        }

        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null) return Fail(output, $"服务器 '{serverName}' 不存在");

        if (key == null)
        {
            var entries = server.Properties.Entries;
            if (entries.Count == 0)
            {
                var empty = $"服务器 '{serverName}' 的配置为空";
                output?.Write("Command", LogLevel.Success, empty);
                return new CommandResult(1, empty);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"服务器 '{serverName}' 的全部配置 ({entries.Count} 项):");
            foreach (var kv in entries)
                sb.AppendLine($"  {kv.Key} = {kv.Value}");
            output?.Write("Command", LogLevel.Success, sb.ToString());
            return new CommandResult(1, sb.ToString());
        }

        var value = server.Properties.GetValue(key);
        if (value == null)
        {
            var missing = $"键 '{key}' 不存在或未设置";
            output?.Write("Command", LogLevel.Success, missing);
            return new CommandResult(1, missing);
        }

        var msg = $"{serverName}.{key} = {value}";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 写入配置：键不存在时新增，值可包含空格。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="tail">动作之后的参数：可选的服务器名、键与值。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Set(CommandArgs args, string tail, IOutputWriter? output)
    {
        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!ResolveKeyValue(args, parts, output, out var serverName, out var key, out var value))
            return new CommandResult(0, "参数不足");

        var server = args.ServerRegistry.GetServer(serverName!);
        if (server == null) return Fail(output, $"未找到服务器 '{serverName}'");

        server.Properties.SetValue(key!, value!);
        var msg = $"{serverName}.{key} = {value} 已更新";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 删除配置项并落盘；不存在的键会被明确提示。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="tail">动作之后的参数：可选的服务器名与键。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Remove(CommandArgs args, string tail, IOutputWriter? output)
    {
        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, "用法: $server cfg rm [服务器名] <键>");

        string? serverName;
        string key;
        if (parts.Length == 1)
        {
            serverName = ResolveServer(args, null, out var error);
            if (serverName == null) return Fail(output, error!);
            key = parts[0];
        }
        else if (args.ServerRegistry.GetServer(parts[0]) != null)
        {
            serverName = parts[0];
            key = parts[1];
        }
        else
        {
            serverName = ResolveServer(args, null, out var error);
            if (serverName == null) return Fail(output, error!);
            key = parts[0];
        }

        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null) return Fail(output, $"未找到服务器 '{serverName}'");

        if (!server.Properties.TryRemoveValue(key))
            return Fail(output, $"{serverName}.{key} 不存在，无需删除");

        var msg = $"{serverName}.{key} 已删除";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 从参数中解析出服务器名、键与值（供 set 使用）。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="parts">动作之后的参数。</param>
    /// <param name="output">输出写入器，用于输出用法。</param>
    /// <param name="serverName">解析出的服务器名。</param>
    /// <param name="key">解析出的键。</param>
    /// <param name="value">解析出的值。</param>
    /// <returns>解析成功返回 true。</returns>
    private static bool ResolveKeyValue(
        CommandArgs args,
        string[] parts,
        IOutputWriter? output,
        out string? serverName,
        out string? key,
        out string? value)
    {
        serverName = null;
        key = null;
        value = null;

        if (parts.Length >= 3 && args.ServerRegistry.GetServer(parts[0]) != null)
        {
            serverName = parts[0];
            key = parts[1];
            value = string.Join(" ", parts.Skip(2));
            return true;
        }

        // 省略服务器名：使用高亮服务器，首个参数为键
        if (parts.Length < 2)
        {
            Fail(output, "用法: $server cfg set [服务器名] <键> <值>");
            return false;
        }

        serverName = ResolveServer(args, null, out var error);
        if (serverName == null)
        {
            Fail(output, error!);
            return false;
        }

        key = parts[0];
        value = string.Join(" ", parts.Skip(1));
        return true;
    }

    /// <summary>
    /// 解析服务器名：给定名称时校验存在性，未给定时回退到高亮服务器。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="name">显式服务器名，可为 null。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>服务器名；失败时为 null。</returns>
    private static string? ResolveServer(CommandArgs args, string? name, out string? error)
    {
        error = null;

        if (!string.IsNullOrEmpty(name))
        {
            if (args.ServerRegistry.GetServer(name) == null)
            {
                error = $"未找到服务器 '{name}'";
                return null;
            }
            return name;
        }

        var highlighted = args.ServerRegistry.HighlightedServerName;
        if (string.IsNullOrEmpty(highlighted))
        {
            error = "未设置高亮服务器，请指定服务器名或先用 $hl <服务器名> 设置高亮";
            return null;
        }

        return highlighted;
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
