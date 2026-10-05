namespace MSL_CLI.Core.Domain;

/// <summary>
/// 代理命令授权配置，保存“总是允许”的命令白名单等持久化策略。
/// </summary>
public class AgentPermissions
{
    /// <summary>
    /// 总是允许执行的白名单，**按“命令 + 子动作”匹配，不区分参数**。
    /// 例如写入 <c>$server cfg</c> 后，<c>$server cfg get/set/rm</c> 的任意服务器名与键值都不再询问；
    /// 写入 <c>$server</c>（命令名）则覆盖该命令的全部动作。
    /// 兼容历史配置：含 <c>*</c> 的条目按整串前缀匹配，含空格的完整命令条目按整串精确匹配。
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
    /// <param name="scope">
    /// 该命令的授权范围（命令 + 子动作），由命令自身的钩子给出；为 null 时退化为仅按命令名匹配。
    /// </param>
    /// <returns>命中白名单时返回 true；强制询问命令始终返回 false。</returns>
    public bool IsAlwaysAllowed(string command, string? scope = null)
    {
        // 强制询问命令优先于白名单，防止历史配置绕过限制
        if (IsAlwaysAsk(command)) return false;

        var name = GetCommandName(command);
        if (name.Length == 0) return false;

        var normalizedCommand = Normalize(command);
        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? name : Normalize(scope);

        foreach (var pattern in AllowList)
        {
            var p = Normalize(pattern);
            if (p.Length == 0) continue;

            // 1. 历史条目：以 * 结尾 → 整串前缀匹配
            if (p.EndsWith('*'))
            {
                if (normalizedCommand.StartsWith(p[..^1].TrimEnd(), StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            // 2. 条目就是本次的授权范围（命令 + 子动作）→ 命中
            if (string.Equals(p, effectiveScope, StringComparison.OrdinalIgnoreCase))
                return true;

            // 3. 条目是范围的上级（只有命令名，如 "$server"）→ 覆盖该命令的全部动作。
            //    注意必须用“范围 + 空格”做前缀判断，否则 "$server ck" 会被误当成 "$server" 使用。
            if (effectiveScope.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                return true;

            // 4. 历史条目：带参数的完整命令（如 "$server cfg get"）→ 仅当它是本次调用的前缀时命中
            if (p.Contains(' ') &&
                normalizedCommand.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 将命令的授权范围加入白名单；已存在或属于强制询问命令时不重复添加。
    /// 保存的是“命令 + 子动作”（如 <c>$server cfg</c>），不含参数。
    /// </summary>
    /// <param name="scope">授权范围；为空时回退为命令名。</param>
    /// <param name="command">完整命令文本，用于在范围为空的兜底与强制询问判定。</param>
    /// <returns>本次是否实际新增了白名单项。</returns>
    public bool TryAddToAllowList(string scope, string? command = null)
    {
        // 只记录“命令 + 子动作”，不记录参数：同一范围的不同参数共享一次授权
        var pattern = Normalize(scope);
        if (pattern.Length == 0)
            pattern = GetCommandName(command);
        if (pattern.Length == 0) return false;

        // 强制询问命令不允许持久化放行
        if (IsAlwaysAsk(command ?? pattern) || IsAlwaysAsk(pattern)) return false;
        if (AllowList.Any(p => string.Equals(Normalize(p), pattern, StringComparison.OrdinalIgnoreCase)))
            return false;

        AllowList.Add(pattern);
        return true;
    }

    /// <summary>
    /// 从命令文本中取出命令名（首个空格前的内容，统一小写）。
    /// </summary>
    /// <param name="command">完整命令文本。</param>
    /// <returns>小写的命令名；输入为空时返回空字符串。</returns>
    public static string GetCommandName(string? command)
    {
        var normalized = Normalize(command);
        if (normalized.Length == 0) return string.Empty;

        var space = normalized.IndexOf(' ');
        return (space < 0 ? normalized : normalized[..space]).ToLowerInvariant();
    }

    /// <summary>
    /// 规范化命令文本：去除首尾空白，并把内部连续空白合并为单个空格。
    /// 这样 " $server   buf  read yz " 与 "$server buf read yz" 视为同一条命令，
    /// 避免因多余空格导致白名单明明写入了却匹配不上。
    /// </summary>
    /// <param name="command">原始命令文本。</param>
    /// <returns>规范化后的命令文本。</returns>
    public static string Normalize(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;

        var parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }
}
