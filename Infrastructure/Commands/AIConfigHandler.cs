using System.Text;
using System.Text.Json;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $ai cfg / $ai perm 动作的处理器：AI 相关配置的统一读写入口。
/// <para>覆盖范围（其余配置请走“第二路径”$app cfg）：</para>
/// <list type="bullet">
/// <item><c>EnableAI</c>、<c>DefaultAIConfig</c></item>
/// <item><c>AIConfigs.&lt;配置名&gt;.&lt;字段&gt;</c>（支持简写 <c>&lt;配置名&gt;.&lt;字段&gt;</c>）</item>
/// <item><c>AgentPermissions.*</c>（列表用 $ai perm 管理）</item>
/// </list>
/// </summary>
internal static class AIConfigHandler
{
    /// <summary>AI 配置范围的根路径。</summary>
    private static readonly string[] AiRoots = { "EnableAI", "DefaultAIConfig", "AIConfigs", "AgentPermissions" };

    /// <summary>列表类路径的专用管理提示。</summary>
    private const string ListHint = "列表类配置请用 $ai perm add|rm|ask|noask 管理";

    // ---------- cfg ----------

    /// <summary>
    /// 处理 <c>$ai cfg get|getall|set|rm</c>。
    /// </summary>
    /// <param name="rest">cfg 之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储与 AI 服务。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult HandleConfig(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Fail(output, Usage());

        var action = parts[0].ToLowerInvariant();
        var rawPath = parts.Length > 1 ? parts[1] : null;
        var value = parts.Length > 2 ? parts[2] : null;

        var config = args.ConfigStore.LoadConfig();

        return action switch
        {
            "get" => rawPath == null ? Overview(config, output) : GetValue(config, rawPath, output),
            "getall" => GetAll(config, output),
            "set" => SetValue(config, rawPath, value, args, output),
            "rm" or "remove" => RemoveValue(config, rawPath, args, output),
            _ => Fail(output, $"未知子动作 '{parts[0]}'，可用: get、getall、set、rm")
        };
    }

    /// <summary>
    /// 输出 AI 配置总览（实例、默认配置、密钥来源、提示词来源与授权列表）。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Overview(AppConfig config, IOutputWriter? output)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AI 配置总览:");
        sb.AppendLine($"  EnableAI: {config.EnableAI}");
        sb.AppendLine($"  默认配置: {(string.IsNullOrWhiteSpace(config.DefaultAIConfig) ? "(未设置)" : config.DefaultAIConfig)}");
        sb.AppendLine($"  实例 ({config.AIConfigs.Count}):");

        if (config.AIConfigs.Count == 0)
            sb.AppendLine("    （无，可用 $ai add <配置名> <Url> <Model> [ApiKeyEnv] 添加）");

        foreach (var kv in config.AIConfigs)
        {
            var c = kv.Value;
            sb.AppendLine($"    {kv.Key}: Model={c.Model}  Url={c.Url}");
            sb.AppendLine($"      chat={(c.EnableChat ? "on" : "off")}  agent={(c.EnableAgent ? "on" : "off")}  " +
                          $"MaxIterations={c.MaxIterations}  MaxContextTokens={c.MaxContextTokens}");
            sb.AppendLine($"      密钥: {KeyState(c)}");
            sb.AppendLine($"      提示词: ChatPrompt={PromptState(c.ChatPrompt, new AIConfig().ChatPrompt)}  " +
                          $"AgentPrompt={PromptState(c.AgentPrompt, new AIConfig().AgentPrompt)}  " +
                          $"ContextCompressPrompt={PromptState(c.ContextCompressPrompt, AIConfig.DefaultCompressPrompt)}");
        }

        sb.AppendLine("  代理授权:");
        sb.AppendLine($"    AllowList: {JoinOrNone(config.AgentPermissions.AllowList)}");
        sb.AppendLine($"    AlwaysAskCommands: {JoinOrNone(config.AgentPermissions.AlwaysAskCommands)}");

        var msg = sb.ToString();
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 输出 AI 相关配置的 JSON（密钥只给出是否已设置，不输出明文）。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult GetAll(AppConfig config, IOutputWriter? output)
    {
        var projection = new
        {
            config.EnableAI,
            config.DefaultAIConfig,
            AIConfigs = config.AIConfigs.ToDictionary(kv => kv.Key, kv => new
            {
                kv.Value.Url,
                kv.Value.Model,
                kv.Value.UseApiKeyEnv,
                kv.Value.ApiKeyEnv,
                ApiKeySet = !string.IsNullOrEmpty(kv.Value.ApiKey),
                kv.Value.EnableChat,
                kv.Value.EnableAgent,
                kv.Value.MaxIterations,
                kv.Value.MaxContextTokens,
                ChatPrompt = PromptState(kv.Value.ChatPrompt, new AIConfig().ChatPrompt),
                AgentPrompt = PromptState(kv.Value.AgentPrompt, new AIConfig().AgentPrompt),
                ContextCompressPrompt = PromptState(kv.Value.ContextCompressPrompt, AIConfig.DefaultCompressPrompt)
            }),
            AgentPermissions = new
            {
                config.AgentPermissions.AllowList,
                config.AgentPermissions.AlwaysAskCommands
            }
        };

        var json = JsonSerializer.Serialize(projection, new JsonSerializerOptions { WriteIndented = true });
        output?.Write("Command", LogLevel.Success, json);
        return new CommandResult(1, json);
    }

    /// <summary>
    /// 读取单个 AI 配置项。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <param name="rawPath">用户输入的路径。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult GetValue(AppConfig config, string rawPath, IOutputWriter? output)
    {
        if (!TryResolvePath(config, rawPath, out var path, out var error)) return Fail(output, error!);

        try
        {
            var value = AppConfigPath.GetValueByPath(config, path);
            var text = $"{path} : {AppConfigPath.FormatValue(value)}";
            output?.Write("Command", LogLevel.Success, text);
            return new CommandResult(1, text);
        }
        catch (Exception ex)
        {
            return Fail(output, $"读取失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 写入单个 AI 配置项并热更新到运行中的 AI 服务。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <param name="rawPath">用户输入的路径。</param>
    /// <param name="value">要写入的值。</param>
    /// <param name="args">命令参数，用于保存配置与热更新。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult SetValue(AppConfig config, string? rawPath, string? value, CommandArgs args, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || value == null)
            return Fail(output, "用法: $ai cfg set <路径> <值>，例如 $ai cfg set ds.MaxIterations 32");

        if (!TryResolvePath(config, rawPath, out var path, out var error)) return Fail(output, error!);
        if (IsListPath(path)) return Fail(output, $"'{path}' 是列表，{ListHint}");

        // 默认配置必须指向已存在的实例，避免把 $ai 打到一个不存在的名字上
        if (path.Equals("DefaultAIConfig", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(value) &&
            !config.AIConfigs.ContainsKey(value.Trim()))
        {
            return Fail(output, $"AI 配置 '{value.Trim()}' 不存在。已配置: {JoinOrNone(config.AIConfigs.Keys)}");
        }

        try
        {
            AppConfigPath.SetValueByPath(config, path, value);
            args.ConfigStore.SaveConfig(config);
            args.AgentService?.ReloadConfig(config);

            var msg = $"AI 配置已更新: {path} = {value}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            return Fail(output, $"设置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除一个 AI 配置项；删掉的若是默认实例，会一并清空 DefaultAIConfig。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <param name="rawPath">用户输入的路径。</param>
    /// <param name="args">命令参数，用于保存配置与热更新。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult RemoveValue(AppConfig config, string? rawPath, CommandArgs args, IOutputWriter? output)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return Fail(output, "用法: $ai cfg rm <路径>，例如 $ai cfg rm ds");

        if (!TryResolvePath(config, rawPath, out var path, out var error)) return Fail(output, error!);
        if (IsListPath(path)) return Fail(output, $"'{path}' 是列表，{ListHint}");

        // 记录被删除的实例名，便于同步清理默认配置
        var removedName = path.StartsWith("AIConfigs.", StringComparison.OrdinalIgnoreCase) &&
                          path.Count(c => c == '.') == 1
            ? path["AIConfigs.".Length..]
            : null;

        try
        {
            AppConfigPath.RemoveByPath(config, path);

            var notes = new List<string>();
            if (removedName != null && string.Equals(config.DefaultAIConfig, removedName, StringComparison.Ordinal))
            {
                config.DefaultAIConfig = string.Empty;
                notes.Add("它同时是默认 AI 配置，已一并清空；请用 $ai default <配置名> 重新指定。");
            }

            args.ConfigStore.SaveConfig(config);
            args.AgentService?.ReloadConfig(config);

            var msg = $"AI 配置已删除: {path}";
            foreach (var note in notes) msg += "\n" + note;
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            return Fail(output, $"删除失败: {ex.Message}");
        }
    }

    // ---------- perm ----------

    /// <summary>
    /// 处理 <c>$ai perm [get|add|rm|ask|noask] [范围]</c>：管理代理命令授权配置。
    /// </summary>
    /// <param name="rest">perm 之后的参数文本。</param>
    /// <param name="args">命令参数，包含配置存储。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult HandlePerm(string rest, CommandArgs args, IOutputWriter? output)
    {
        var config = args.ConfigStore.LoadConfig();
        var perms = config.AgentPermissions;

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("get", StringComparison.OrdinalIgnoreCase))
            return ShowPerm(perms, output);

        var action = parts[0].ToLowerInvariant();
        var raw = parts.Length > 1 ? parts[1].Trim().Trim('"') : string.Empty;
        if (raw.Length == 0)
            return Fail(output, $"用法: $ai perm {action} <授权范围>，例如 $ai perm add \"$server ck op\"");

        var scope = AgentPermissions.Normalize(raw);
        string msg;

        switch (action)
        {
            case "add":
                if (perms.IsAlwaysAsk(scope, scope))
                    return Fail(output, $"'{scope}' 属于强制逐次询问的底线命令，不能加入允许列表");
                if (!perms.TryAddToAllowList(scope))
                    return Fail(output, $"'{scope}' 已在允许列表中");
                msg = $"已加入允许列表: {scope}";
                break;

            case "rm":
            case "remove":
                var removedAllow = perms.AllowList.RemoveAll(
                    p => string.Equals(AgentPermissions.Normalize(p), scope, StringComparison.OrdinalIgnoreCase));
                if (removedAllow == 0) return Fail(output, $"允许列表中没有 '{scope}'");
                msg = $"已从允许列表移除: {scope}";
                break;

            case "ask":
                if (perms.AlwaysAskCommands.Any(c => string.Equals(AgentPermissions.Normalize(c), scope, StringComparison.OrdinalIgnoreCase)))
                    return Fail(output, $"'{scope}' 已在强制询问列表中");
                perms.AlwaysAskCommands.Add(scope);
                msg = $"已加入强制逐次询问: {scope}";
                break;

            case "noask":
                if (AgentPermissions.ForcedAlwaysAsk.Any(f => string.Equals(f, scope, StringComparison.OrdinalIgnoreCase)))
                    return Fail(output, $"'{scope}' 是内置底线（{string.Join("、", AgentPermissions.ForcedAlwaysAsk)}），无法移出强制询问列表");
                var removedAsk = perms.AlwaysAskCommands.RemoveAll(
                    c => string.Equals(AgentPermissions.Normalize(c), scope, StringComparison.OrdinalIgnoreCase));
                if (removedAsk == 0) return Fail(output, $"强制询问列表中没有 '{scope}'");
                msg = $"已移出强制询问列表: {scope}";
                break;

            default:
                return Fail(output, $"未知子动作 '{parts[0]}'，可用: get、add、rm、ask、noask");
        }

        args.ConfigStore.SaveConfig(config);
        msg += $"\nAllowList: {JoinOrNone(perms.AllowList)}\nAlwaysAskCommands: {JoinOrNone(perms.AlwaysAskCommands)}";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 展示当前的代理授权配置。
    /// </summary>
    /// <param name="perms">授权配置。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult ShowPerm(AgentPermissions perms, IOutputWriter? output)
    {
        var sb = new StringBuilder();
        sb.AppendLine("代理命令授权:");
        sb.AppendLine($"  AllowList（命中即不再询问）: {JoinOrNone(perms.AllowList)}");
        sb.AppendLine($"  AlwaysAskCommands（每次都必须确认）: {JoinOrNone(perms.AlwaysAskCommands)}");
        sb.AppendLine($"  内置底线（无法移除）: {string.Join("、", AgentPermissions.ForcedAlwaysAsk)}");
        sb.AppendLine("  用法: $ai perm add|rm <范围>（允许列表）、$ai perm ask|noask <范围>（强制询问）");

        var msg = sb.ToString();
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    // ---------- 路径与展示辅助 ----------

    /// <summary>
    /// 把用户输入的路径解析为 AppConfig 的完整点号路径，并校验它落在 AI 配置范围内。
    /// 支持简写：<c>&lt;配置名&gt;.&lt;字段&gt;</c> 与 <c>&lt;配置名&gt;</c> 会自动补上 AIConfigs 前缀。
    /// </summary>
    /// <param name="config">当前配置，用于识别已存在的实例名。</param>
    /// <param name="input">用户输入的路径。</param>
    /// <param name="path">解析后的完整路径。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>解析成功返回 true。</returns>
    private static bool TryResolvePath(AppConfig config, string input, out string path, out string? error)
    {
        path = string.Empty;
        error = null;

        var trimmed = input.Trim().Trim('"');
        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "路径不能为空";
            return false;
        }

        // 已经是 AI 根路径：把 AIConfigs 的键名按实际大小写归一化
        var root = AiRoots.FirstOrDefault(r => r.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
        if (root != null)
        {
            if (root.Equals("AIConfigs", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            {
                var realKey = config.AIConfigs.Keys.FirstOrDefault(k => k.Equals(segments[1], StringComparison.OrdinalIgnoreCase));
                if (realKey != null) segments[1] = realKey;
            }

            path = string.Join(".", segments);
            return true;
        }

        // 简写：首段命中已存在的实例名
        var name = config.AIConfigs.Keys.FirstOrDefault(k => k.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
        if (name != null)
        {
            path = "AIConfigs." + name + (segments.Length > 1 ? "." + string.Join(".", segments.Skip(1)) : string.Empty);
            return true;
        }

        error = $"路径 '{trimmed}' 不属于 AI 配置范围。可用: EnableAI、DefaultAIConfig、" +
                "AIConfigs.<配置名>.<字段>（可简写为 <配置名>.<字段>）、AgentPermissions.*；" +
                "其他配置请用 $app cfg get|set|rm。";
        return false;
    }

    /// <summary>
    /// 判断路径是否指向字符串列表（这类配置需要专用命令维护）。
    /// </summary>
    /// <param name="path">完整点号路径。</param>
    /// <returns>是列表时返回 true。</returns>
    private static bool IsListPath(string path)
        => path.EndsWith("AllowList", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("AlwaysAskCommands", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 描述密钥来源与是否已就绪。
    /// </summary>
    /// <param name="config">AI 配置。</param>
    /// <returns>可读的密钥状态。</returns>
    private static string KeyState(AIConfig config)
    {
        if (!config.UseApiKeyEnv)
            return string.IsNullOrEmpty(config.ApiKey) ? "直接填写（未设置）" : "直接填写（已设置）";

        if (string.IsNullOrWhiteSpace(config.ApiKeyEnv)) return "环境变量名未配置";
        return $"环境变量 {config.ApiKeyEnv}（{(string.IsNullOrEmpty(config.ApiKey) ? "当前为空" : "已读取")}）";
    }

    /// <summary>
    /// 判断提示词字段使用的是内置默认还是自定义内容。
    /// </summary>
    /// <param name="value">配置值。</param>
    /// <param name="builtin">内置默认值。</param>
    /// <returns>“(内置默认)”或“(已自定义)”。</returns>
    private static string PromptState(string value, string builtin)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, builtin, StringComparison.Ordinal)
            ? "(内置默认)"
            : "(已自定义)";

    /// <summary>
    /// 以逗号连接列表，空列表给出提示。
    /// </summary>
    /// <param name="items">列表内容。</param>
    /// <returns>可读文本。</returns>
    private static string JoinOrNone(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "(空)" : string.Join(", ", list);
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
    /// 用法说明。
    /// </summary>
    /// <returns>用法文本。</returns>
    private static string Usage() =>
        "用法: $ai cfg get|getall|set|rm ...\n" +
        "  cfg get [路径]                 省略路径输出总览；给路径输出单个值\n" +
        "  cfg getall                     输出 AI 相关配置 JSON（密钥打码）\n" +
        "  cfg set <路径> <值>            写入，如 $ai cfg set ds.MaxIterations 32\n" +
        "  cfg rm <路径>                  删除，如 $ai cfg rm ds\n" +
        "路径范围: EnableAI、DefaultAIConfig、AIConfigs.<配置名>.<字段>（可简写 <配置名>.<字段>）、AgentPermissions.*\n" +
        ListHint + "；其他配置用 $app cfg。";
}
