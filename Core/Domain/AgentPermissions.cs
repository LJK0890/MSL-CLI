namespace MSL_CLI.Core.Domain;

/// <summary>
/// 代理命令授权配置，保存“总是允许”的命令白名单等持久化策略。
/// </summary>
public class AgentPermissions
{
    /// <summary>
    /// 总是允许执行的命令模式列表，支持以 * 结尾的前缀通配。
    /// 该列表永不放行 <see cref="AlwaysAskCommands"/> 中的命令。
    /// </summary>
    public List<string> AllowList { get; set; } = new();

    /// <summary>
    /// 无论白名单如何配置都必须逐次询问的命令名（不区分大小写）。
    /// 默认为 <c>$exec</c>：执行任意系统命令的风险最高，必须每次人工确认。
    /// </summary>
    public List<string> AlwaysAskCommands { get; set; } = new() { "$exec" };

    /// <summary>
    /// 判断某个命令是否属于“必须每次询问”的类别。
    /// </summary>
    /// <param name="command">待判断的完整命令文本（如 "$exec rm -rf /"）。</param>
    /// <returns>属于强制询问命令时返回 true。</returns>
    public bool IsAlwaysAsk(string command)
    {
        var name = GetCommandName(command);
        foreach (var forced in AlwaysAskCommands)
        {
            if (string.IsNullOrWhiteSpace(forced)) continue;
            if (string.Equals(forced.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 判断某个命令是否已被“总是允许”白名单覆盖。
    /// </summary>
    /// <param name="command">待判断的完整命令文本。</param>
    /// <returns>命中白名单时返回 true；强制询问命令始终返回 false。</returns>
    public bool IsAlwaysAllowed(string command)
    {
        // 强制询问命令优先于白名单，防止历史配置绕过限制
        if (IsAlwaysAsk(command)) return false;
        if (string.IsNullOrWhiteSpace(command)) return false;

        foreach (var pattern in AllowList)
        {
            if (Matches(command, pattern)) return true;
        }
        return false;
    }

    /// <summary>
    /// 将命令加入白名单并持久化；已存在或属于强制询问命令时不重复添加。
    /// </summary>
    /// <param name="command">要加入白名单的完整命令文本。</param>
    /// <returns>本次是否实际新增了白名单项。</returns>
    public bool TryAddToAllowList(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var pattern = command.Trim();

        // 强制询问命令不允许持久化放行
        if (IsAlwaysAsk(pattern)) return false;
        if (AllowList.Any(p => string.Equals(p.Trim(), pattern, StringComparison.OrdinalIgnoreCase)))
            return false;

        AllowList.Add(pattern);
        return true;
    }

    /// <summary>
    /// 从命令文本中取出命令名（首个空格前的内容，统一小写）。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>小写的命令名；输入为空时返回空字符串。</returns>
    public static string GetCommandName(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        var trimmed = command.Trim();
        var space = trimmed.IndexOf(' ');
        return (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
    }

    /// <summary>
    /// 判断命令是否匹配给定模式：支持整串忽略大小写比较，以及以 * 结尾的前缀匹配。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <param name="pattern">白名单模式。</param>
    /// <returns>匹配时返回 true。</returns>
    private static bool Matches(string command, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var p = pattern.Trim();
        var c = command.Trim();

        if (p.EndsWith('*'))
        {
            var prefix = p[..^1];
            return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(c, p, StringComparison.OrdinalIgnoreCase);
    }
}
