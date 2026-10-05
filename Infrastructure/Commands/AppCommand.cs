using System.Text;
using System.Text.Json;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $app 命令：应用程序级操作的唯一入口。
/// 动作：<c>cfg get|getall|set|rm</c>（应用配置）、<c>exit</c>（退出）、<c>reload</c>（重载配置）、<c>ptcfg</c>（打印配置）。
/// </summary>
public class AppCommand : ICommand, IArgValidatingCommand
{
    /// <summary>命令名称：$app。</summary>
    public string Name => "$app";

    /// <summary>命令描述。</summary>
    public string Description =>
        "应用级操作。用法: $app cfg get|getall|set|rm ... | $app exit | $app reload | $app ptcfg；详见 $help $app";

    /// <summary>各动作对应的子动作（无子动作的为 null）。</summary>
    private static readonly Dictionary<string, string[]?> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cfg"] = new[] { "get", "getall", "set", "rm", "remove" },
        ["exit"] = null,
        ["reload"] = null,
        ["ptcfg"] = null
    };

    /// <summary>
    /// 参数校验钩子：检查动作是否有效，以及 cfg 的子动作是否合法。
    /// </summary>
    /// <param name="rawArgs">$app 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令校验不需要，可为 null）。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>参数合法时返回 true。</returns>
    public bool TryValidateArgs(string rawArgs, CommandArgs? args, out string error)
    {
        error = string.Empty;

        var parts = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = $"$app 缺少动作。可用动作: {string.Join("、", Actions.Keys)}";
            return false;
        }

        if (!Actions.TryGetValue(parts[0], out var subActions))
        {
            error = $"$app 未知动作 '{parts[0]}'，可用: {string.Join("、", Actions.Keys)}。" +
                    "可先用 $help $app 查看用法。";
            return false;
        }

        if (subActions == null) return true;

        if (parts.Length < 2)
        {
            error = $"$app {parts[0]} 缺少子动作，可用: {string.Join("、", subActions)}。";
            return false;
        }

        if (!subActions.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
        {
            error = $"$app {parts[0]} 未知子动作 '{parts[1]}'，可用: {string.Join("、", subActions)}。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取出授权范围：命令 + 动作 + （存在时的）子动作，
    /// 例如 <c>$app cfg get</c>、<c>$app exit</c>。
    /// </summary>
    /// <param name="rawArgs">$app 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令判定不需要，可为 null）。</param>
    /// <returns>授权范围。</returns>
    public string GetPermissionScope(string rawArgs, CommandArgs? args)
    {
        var parts = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Name;

        var action = parts[0].ToLowerInvariant();
        if (!Actions.TryGetValue(action, out var subActions))
            return Name;

        var scope = $"{Name} {action}";
        if (subActions == null) return scope;
        if (parts.Length < 2) return scope;

        return subActions.Contains(parts[1], StringComparer.OrdinalIgnoreCase)
            ? $"{scope} {parts[1].ToLowerInvariant()}"
            : scope;
    }

    /// <summary>
    /// 执行 $app 命令，按动作分发。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入、配置存储与服务器注册表。</param>
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
            "cfg" => Task.FromResult(HandleConfig(rest, args, output)),
            "exit" => Task.FromResult(HandleExit(output)),
            "reload" => Task.FromResult(HandleReload(args, output)),
            "ptcfg" => Task.FromResult(HandlePrintConfig(args, output)),
            _ => Task.FromResult(Fail(output, $"未知动作 '{parts[0]}'。{Help()}"))
        };
    }

    // ---------- cfg ----------

    /// <summary>
    /// 处理 cfg 动作：应用配置的读取、写入与删除。
    /// </summary>
    /// <param name="rest">cfg 之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult HandleConfig(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, "用法: $app cfg get|getall|set|rm [路径] [值]");

        var action = parts[0].ToLowerInvariant();
        var path = parts.Length > 1 ? parts[1] : null;
        var value = parts.Length > 2 ? parts[2] : null;

        return action switch
        {
            "get" => ConfigGet(args, path, output),
            "getall" => ConfigGet(args, null, output),
            "set" => ConfigSet(args, path, value, output),
            "rm" or "remove" => ConfigRemove(args, path, output),
            _ => Fail(output, $"未知配置动作 '{parts[0]}'，可用: get、getall、set、rm")
        };
    }

    /// <summary>
    /// 读取应用配置：无路径时输出完整 JSON，否则按点号路径读取单个值。
    /// </summary>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="path">点号路径，可为 null。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult ConfigGet(CommandArgs args, string? path, IOutputWriter? output)
    {
        try
        {
            var config = args.ConfigStore.LoadConfig();

            if (string.IsNullOrWhiteSpace(path))
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                output?.Write("Command", LogLevel.Success, json);
                return new CommandResult(1, json);
            }

            var value = AppConfigPath.GetValueByPath(config, path);
            var text = $"{path} : {value?.ToString() ?? "(null)"}";
            output?.Write("Command", LogLevel.Success, text);
            return new CommandResult(1, text);
        }
        catch (Exception ex)
        {
            return Fail(output, $"获取失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 写入应用配置：路径中间的字典键不存在时自动创建。
    /// </summary>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="path">点号路径。</param>
    /// <param name="value">要写入的值。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult ConfigSet(CommandArgs args, string? path, string? value, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(path) || value == null)
            return Fail(output, "用法: $app cfg set <路径> <值>");

        try
        {
            var config = args.ConfigStore.LoadConfig();
            AppConfigPath.SetValueByPath(config, path, value);
            args.ConfigStore.SaveConfig(config);

            var msg = $"配置已更新: {path} = {value}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            return Fail(output, $"设置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除应用配置中的字典条目并落盘。
    /// </summary>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="path">点号路径，最后一段为要删除的字典键。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult ConfigRemove(CommandArgs args, string? path, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail(output, "用法: $app cfg rm <路径>，例如 $app cfg rm ServerPaths.tga");

        try
        {
            var config = args.ConfigStore.LoadConfig();
            AppConfigPath.RemoveByPath(config, path);
            args.ConfigStore.SaveConfig(config);

            var msg = $"配置已删除: {path}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            return Fail(output, $"删除失败: {ex.Message}");
        }
    }

    // ---------- exit / reload / ptcfg ----------

    /// <summary>
    /// 处理 exit 动作：先停止所有服务器，再请求程序退出。
    /// </summary>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>带退出标记的命令结果。</returns>
    private static CommandResult HandleExit(IOutputWriter? output)
    {
        // 实际停止由 Program 的退出流程统一负责，这里只发出退出请求
        var msg = "收到退出指令，正在关闭...";
        output?.Write("Command", LogLevel.Info, msg);
        return new CommandResult(1, msg, exitRequested: true);
    }

    /// <summary>
    /// 处理 reload 动作：重新加载配置文件并重建服务器列表。
    /// </summary>
    /// <param name="args">命令参数，包含配置存储与服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult HandleReload(CommandArgs args, IOutputWriter? output)
    {
        try
        {
            var newConfig = args.ConfigStore.LoadConfig();
            args.ServerRegistry.Reload(newConfig);

            var msg = "配置已重新加载，服务器列表已更新（运行中的服务器未受影响）";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            return Fail(output, $"重新加载失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理 ptcfg 动作：重新加载配置并打印关键字段。
    /// </summary>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult HandlePrintConfig(CommandArgs args, IOutputWriter? output)
    {
        var config = args.ConfigStore.LoadConfig();
        var sb = new StringBuilder();

        sb.AppendLine("当前配置:");
        sb.AppendLine($"EnableAI: {config.EnableAI}");
        sb.AppendLine($"DefaultAIConfig: {config.DefaultAIConfig}");
        sb.AppendLine("AIConfigs:");
        foreach (var kv in config.AIConfigs)
        {
            sb.AppendLine($"  {kv.Key}:");
            sb.AppendLine($"    Url: {kv.Value.Url}");
            sb.AppendLine($"    Model: {kv.Value.Model}");
            sb.AppendLine($"    ApiKey: {(string.IsNullOrEmpty(kv.Value.ApiKey) ? "(empty)" : "****")}");
            sb.AppendLine($"    UseApiKeyEnv: {kv.Value.UseApiKeyEnv}");
            sb.AppendLine($"    ApiKeyEnv: {kv.Value.ApiKeyEnv ?? "(null)"}");
            sb.AppendLine($"    EnableChat: {kv.Value.EnableChat}");
            sb.AppendLine($"    EnableAgent: {kv.Value.EnableAgent}");
            sb.AppendLine($"    MaxIterations: {kv.Value.MaxIterations}");
        }

        sb.AppendLine("ServerPaths:");
        foreach (var kv in config.ServerPaths)
            sb.AppendLine($"  {kv.Key}: {kv.Value}");

        sb.AppendLine("AgentPermissions:");
        sb.AppendLine($"  AllowList: {string.Join(", ", config.AgentPermissions.AllowList)}");
        sb.AppendLine($"  AlwaysAskCommands: {string.Join(", ", config.AgentPermissions.AlwaysAskCommands)}");

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
    /// 生成 $app 用法文本。
    /// </summary>
    /// <returns>用法说明。</returns>
    private static string Help() =>
        "用法: $app <动作> ...\n" +
        "  cfg get [路径]              读取应用配置；省略路径输出全部\n" +
        "  cfg getall                  输出全部配置\n" +
        "  cfg set <路径> <值>         写入配置（路径中间的字典键会自动创建）\n" +
        "  cfg rm <路径>               删除字典条目，如 ServerPaths.tga\n" +
        "  exit                        退出程序（先停止所有服务器）\n" +
        "  reload                      重新加载配置文件并重建服务器列表\n" +
        "  ptcfg                       打印当前配置（调试用）\n" +
        "用 $help $app 查看详细说明。";
}
