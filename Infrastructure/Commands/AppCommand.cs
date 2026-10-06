using System.Text;
using System.Text.Json;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $app 命令：高危 / 大权限操作的统一入口，也是访问任意配置的“第二路径”。
/// 动作：<c>cfg get|getall|set|rm</c>（任意应用配置）、<c>exec</c>（执行系统命令）、
/// <c>exit</c>（退出）、<c>reload</c>（重载配置）、<c>ptcfg</c>（打印配置）。
/// 日常的 AI 配置请用 <c>$ai cfg</c>、服务器配置请用 <c>$server</c>；
/// 这里放的是它们覆盖不到、或权限更大的操作。
/// </summary>
public class AppCommand : ICommand, IArgValidatingCommand
{
    /// <summary>命令名称：$app。</summary>
    public string Name => "$app";

    /// <summary>命令描述。</summary>
    public string Description =>
        "高危/大权限操作。用法: $app cfg get|getall|set|rm ... | $app exec <命令> [参数...] | $app exit | $app reload | $app ptcfg";

    /// <summary>各动作对应的子动作（无子动作的为 null）。</summary>
    private static readonly Dictionary<string, string[]?> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cfg"] = new[] { "get", "getall", "set", "rm", "remove" },
        ["exec"] = null,
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

        if (subActions == null)
        {
            // exec 需要命令/脚本路径
            if (parts[0].Equals("exec", StringComparison.OrdinalIgnoreCase) && parts.Length < 2)
            {
                error = "用法: $app exec <命令/脚本路径> [参数...]";
                return false;
            }
            return true;
        }

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

        if (!subActions.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
            return scope;

        // 别名归一化：$app cfg remove 与 $app cfg rm 共用同一个授权范围
        var subAction = parts[1].ToLowerInvariant();
        return subAction == "remove" ? $"{scope} rm" : $"{scope} {subAction}";
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
            "exec" => AppExecHandler.RunAsync(rest, output),
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
            var text = $"{path} : {AppConfigPath.FormatValue(value)}";
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
            // AI 实例集合同样热更新，避免改了 AIConfigs 却要重启才生效
            args.AgentService?.ReloadConfig(newConfig);

            var msg = "配置已重新加载，服务器列表与 AI 实例已更新（运行中的服务器未受影响）";
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
            sb.AppendLine($"    MaxContextTokens: {kv.Value.MaxContextTokens}");
            sb.AppendLine($"    ChatPrompt: {PromptState(kv.Value.ChatPrompt, new AIConfig().ChatPrompt)}");
            sb.AppendLine($"    AgentPrompt: {PromptState(kv.Value.AgentPrompt, new AIConfig().AgentPrompt)}");
            sb.AppendLine($"    ContextCompressPrompt: {PromptState(kv.Value.ContextCompressPrompt, AIConfig.DefaultCompressPrompt)}");
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
    /// 判断提示词字段当前使用的是内置默认值还是自定义内容，用于 $app cfg 的紧凑展示。
    /// </summary>
    /// <param name="value">配置中的提示词值。</param>
    /// <param name="builtin">对应的内置默认提示词。</param>
    /// <returns>“(内置默认)”或“(已自定义)”。</returns>
    private static string PromptState(string value, string builtin)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, builtin, StringComparison.Ordinal)
            ? "(内置默认)"
            : "(已自定义)";

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
        "用法: $app <动作> ...（高危 / 大权限操作，也是访问任意配置的第二路径）\n" +
        "  exec <命令/脚本路径> [参数...]   执行系统命令或脚本（代理调用时每次都需确认）\n" +
        "  cfg get [路径]              读取任意应用配置；省略路径输出全部 JSON\n" +
        "  cfg getall                  输出全部配置\n" +
        "  cfg set <路径> <值>         写入任意配置（路径中间的字典键会自动创建）\n" +
        "  cfg rm <路径>               删除字典条目，如 ServerPaths.tga\n" +
        "  exit                        退出程序（先停止所有服务器）\n" +
        "  reload                      重新加载配置文件并重建服务器列表与 AI 实例\n" +
        "  ptcfg                       打印当前配置（调试用）\n" +
        "日常配置请优先用 $ai（AI 相关）与 $server（服务器相关）；用 $help $app 查看详细说明。";
}
