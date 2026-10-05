using MSL_CLI.Core.Ports;

namespace MSL_CLI.Core.Domain;

/// <summary>
/// 一次命令调用的校验结果。
/// </summary>
/// <param name="IsValid">命令名与参数是否合法。</param>
/// <param name="CommandName">从原始命令中取出的命令名。</param>
/// <param name="PermissionScope">用于授权匹配的“命令 + 子动作”范围。</param>
/// <param name="Message">可直接回传给模型的说明文本。</param>
public readonly record struct CommandValidation(bool IsValid, string CommandName, string PermissionScope, string Message);

/// <summary>
/// 代理命令调用校验器：在申请授权/执行之前先确认这条命令确实存在。
/// 这样可以避免为一条根本不存在的命令浪费时间征求操作员同意，
/// 也能防止把无效命令写进授权白名单。
/// </summary>
public static class CommandInvocationValidator
{
    /// <summary>
    /// 从原始命令文本中取出命令名（首个空白之前的内容）。
    /// </summary>
    /// <param name="command">原始命令文本。</param>
    /// <returns>命令名；输入为空时返回空字符串。</returns>
    public static string ExtractCommandName(string? command)
    {
        var normalized = AgentPermissions.Normalize(command);
        if (normalized.Length == 0) return string.Empty;

        var space = normalized.IndexOf(' ');
        return space < 0 ? normalized : normalized[..space];
    }

    /// <summary>
    /// 校验命令是否存在、参数是否合法。命令解析器不可用（例如未注入）时视为通过，交由后续流程处理。
    /// </summary>
    /// <param name="parser">命令解析器。</param>
    /// <param name="command">原始命令文本。</param>
    /// <param name="context">命令参数上下文，提供注册表等依赖以便命令自行校验参数。</param>
    /// <returns>校验结果；不通过时 <see cref="CommandValidation.Message"/> 会给出可用命令与建议。</returns>
    public static CommandValidation Validate(ICommandParser? parser, string? command, CommandArgs? context = null)
    {
        var name = ExtractCommandName(command);

        if (name.Length == 0)
            return new CommandValidation(false, string.Empty, string.Empty, "命令为空");

        // 没有解析器时不做判断，避免误拦
        if (parser == null)
            return new CommandValidation(true, name, name, string.Empty);

        var descriptions = parser.GetCommandDescriptions();

        if (!descriptions.ContainsKey(name))
            return new CommandValidation(false, name, name, BuildUnknownMessage(descriptions, name));

        var target = parser.GetCommand(name);
        var rawArgs = ExtractRawArgs(command);

        // 命令存在：交给命令自己的参数校验钩子检查子命令/动作是否拼错
        if (target is IArgValidatingCommand validating)
        {
            if (!validating.TryValidateArgs(rawArgs, context, out var argError))
                return new CommandValidation(false, name, name, argError);

            var scope = validating.GetPermissionScope(rawArgs, context);
            return new CommandValidation(true, name, string.IsNullOrWhiteSpace(scope) ? name : scope, string.Empty);
        }

        return new CommandValidation(true, name, name, string.Empty);
    }

    /// <summary>
    /// 取出命令名之后的参数文本（已规范化空白）。
    /// </summary>
    /// <param name="command">原始命令文本。</param>
    /// <returns>参数文本；没有参数时返回空字符串。</returns>
    public static string ExtractRawArgs(string? command)
    {
        var normalized = AgentPermissions.Normalize(command);
        if (normalized.Length == 0) return string.Empty;

        var space = normalized.IndexOf(' ');
        return space < 0 ? string.Empty : normalized[(space + 1)..].Trim();
    }

    /// <summary>
    /// 取出用于授权匹配的“命令 + 子动作”范围。
    /// 命令实现 <see cref="IArgValidatingCommand"/> 时由其自行判定；
    /// 否则回退为仅命令名（配合 <see cref="AgentPermissions.PermissionKey"/> 使用）。
    /// </summary>
    /// <param name="parser">命令解析器，可为 null。</param>
    /// <param name="command">原始命令文本。</param>
    /// <param name="context">命令参数上下文，可为 null。</param>
    /// <returns>授权范围字符串。</returns>
    public static string ResolvePermissionScope(ICommandParser? parser, string? command, CommandArgs? context = null)
    {
        var name = AgentPermissions.GetCommandName(command);
        if (name.Length == 0) return string.Empty;

        var target = parser?.GetCommand(name);
        if (target is not IArgValidatingCommand validating) return name;

        var scope = validating.GetPermissionScope(ExtractRawArgs(command), context);
        return string.IsNullOrWhiteSpace(scope) ? name : scope;
    }

    /// <summary>
    /// 判断给定范围是否命中已有条目：先按范围比较，范围未命中时再退化为命令名比较，
    /// 使旧配置里"整条命令"形式的条目在子动作变化后依然有效。
    /// </summary>
    /// <param name="pattern">白名单中的条目。</param>
    /// <param name="scope">本次调用的授权范围（命令 + 子动作）。</param>
    /// <param name="command">本次调用的完整命令文本。</param>
    /// <param name="normalizedCommand">规范化后的完整命令文本。</param>
    /// <returns>命中时返回 true。</returns>
    public static bool MatchesScope(string pattern, string scope, string command, string normalizedCommand)
    {
        var p = AgentPermissions.Normalize(pattern);
        if (p.Length == 0) return false;

        // 历史条目一：以 * 结尾 → 整串前缀匹配
        if (p.EndsWith('*'))
            return normalizedCommand.StartsWith(p[..^1].TrimEnd(), StringComparison.OrdinalIgnoreCase);

        var s = AgentPermissions.Normalize(scope);

        // 按“命令 + 子动作”范围匹配
        if (s.Length > 0 && string.Equals(p, s, StringComparison.OrdinalIgnoreCase))
            return true;

        // 按命令名匹配（含旧配置中带完整参数的条目）
        var name = AgentPermissions.GetCommandName(command);
        if (p.Contains(' '))
            return string.Equals(p, normalizedCommand, StringComparison.OrdinalIgnoreCase);

        return string.Equals(p.TrimStart('$'), name.TrimStart('$'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构造“命令不存在”的说明文本，包含可用命令列表与就近建议。
    /// </summary>
    /// <param name="descriptions">已注册命令的名称与描述。</param>
    /// <param name="name">被请求但未注册的命令名。</param>
    /// <returns>说明文本。</returns>
    private static string BuildUnknownMessage(Dictionary<string, string> descriptions, string name)
    {
        var available = string.Join(" ", descriptions.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var suggestions = FindSuggestions(descriptions, name);

        var hint = suggestions.Count > 0
            ? $"\n可能你想要的是: {string.Join(" 或 ", suggestions)}"
            : "\n可先用 $help 查看命令总览，用 $list 只看命令名。";

        return $"命令 '{name}' 不存在，未申请授权也未执行。可用命令: {available}{hint}\n" +
               "提示：命令名必须是 $ 开头且完全匹配（例如 $server、$app、$help），" +
               "子命令请先用 $help <命令名> 查看用法。";
    }

    /// <summary>
    /// 根据被拒绝的命令名给出可用命令建议：优先前缀匹配，其次子串匹配，最后看描述里提到的命令形式。
    /// </summary>
    /// <param name="descriptions">已注册命令的名称与描述。</param>
    /// <param name="name">被请求但未注册的命令名。</param>
    /// <returns>建议列表（最多 3 条）。</returns>
    private static List<string> FindSuggestions(Dictionary<string, string> descriptions, string name)
    {
        var key = name.TrimStart('$').ToLowerInvariant();
        if (key.Length == 0) return new List<string>();

        // 1. 前缀 / 子串匹配命令名
        var byName = descriptions.Keys
            .Where(k => k.TrimStart('$').ToLowerInvariant().StartsWith(key, StringComparison.OrdinalIgnoreCase))
            .Concat(descriptions.Keys
                .Where(k => k.TrimStart('$').ToLowerInvariant().Contains(key, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

        if (byName.Count > 0)
            return byName.Take(3).ToList();

        // 2. 在命令描述里找含该关键字的命令形式（例如 $server 的描述里写了 $server ck op）
        var byDescription = new List<string>();
        foreach (var kv in descriptions)
        {
            var forms = ExtractCommandForms(kv.Value)
                .Where(f => f.Contains(key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (forms.Count > 0)
                byDescription.AddRange(forms);
        }

        return byDescription.Distinct().Take(3).ToList();
    }

    /// <summary>
    /// 从描述文本里提取形如 <c>$xxx</c> 的命令形式。
    /// </summary>
    /// <param name="description">命令描述文本。</param>
    /// <returns>提取到的命令形式列表。</returns>
    private static IEnumerable<string> ExtractCommandForms(string description)
    {
        if (string.IsNullOrEmpty(description)) yield break;

        var tokens = description.Split(
            new[] { ' ', '|', ',', '，', '、', '（', '）', '(', ')', ':', '：', '\n', '\r', '；', ';' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            // 只取以 $ 开头且后续为字母/数字/下划线的片段，避免把标点带进来
            var trimmed = token.TrimEnd('.', '。', '!', '！', '?', '？');
            if (trimmed.Length > 1 && trimmed[0] == '$')
            {
                var end = 1;
                while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] == '_'))
                    end++;
                if (end > 1) yield return trimmed[..end];
            }
        }
    }
}
