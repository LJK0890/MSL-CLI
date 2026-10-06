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
    /// 无论白名单如何配置都必须逐次询问的命令，不区分大小写。
    /// 条目既可以是命令名（如 <c>$app</c>，覆盖该命令的全部动作），
    /// 也可以是“命令 + 动作”的授权范围（如 <c>$app exec</c>、<c>$server del</c>，只覆盖该动作）。
    /// 默认（见 <see cref="ForcedAlwaysAsk"/>）为执行系统命令与删除服务器目录两类不可逆操作。
    /// </summary>
    public List<string> AlwaysAskCommands { get; set; } = new(ForcedAlwaysAsk);

    /// <summary>
    /// 无论配置如何都必须逐次询问、且加载时会被自动补回的底线条目。
    /// <c>$app exec</c> 执行任意系统命令，<c>$server del</c> 删除整个服务器目录，二者都不可逆。
    /// </summary>
    public static readonly string[] ForcedAlwaysAsk = { "$app exec", "$server del" };

    /// <summary>
    /// 判断某个命令是否属于“必须每次询问”的类别。
    /// </summary>
    /// <param name="command">待判断的完整命令文本（如 "$app exec rm -rf /"）。</param>
    /// <param name="scope">
    /// 该命令的授权范围（命令 + 动作，如 <c>$server del</c>）；为 null 时只按命令名匹配。
    /// </param>
    /// <returns>属于强制询问命令时返回 true。</returns>
    public bool IsAlwaysAsk(string command, string? scope = null)
    {
        var name = GetCommandName(command);
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? null : Normalize(scope);

        foreach (var forced in AlwaysAskCommands)
        {
            var entry = Normalize(forced);
            if (entry.Length == 0) continue;

            // 条目是命令名：覆盖该命令的全部动作
            if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
                return true;

            // 条目是“命令 + 动作”范围：只覆盖该动作
            if (normalizedScope != null &&
                string.Equals(entry, normalizedScope, StringComparison.OrdinalIgnoreCase))
                return true;

            // 条目是某个范围的上级（如 "$server cfg" 覆盖 "$server cfg set"）：
            // 与白名单的向上覆盖规则保持一致，否则 AlwaysAskCommands 里的范围条目永远命不中带子动作的调用
            if (normalizedScope != null &&
                normalizedScope.StartsWith(entry + " ", StringComparison.OrdinalIgnoreCase))
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
        if (IsAlwaysAsk(command, scope)) return false;

        var name = GetCommandName(command);
        if (name.Length == 0) return false;

        var normalizedCommand = Normalize(command);
        var effectiveScope = string.IsNullOrWhiteSpace(scope) ? name : Normalize(scope);

        foreach (var pattern in AllowList)
        {
            var p = Normalize(pattern);
            if (p.Length == 0) continue;

            // 1. 历史条目：以 * 结尾 → 整串前缀匹配（按单词边界，避免 "$server" 匹配到 "$servers"）
            if (p.EndsWith('*'))
            {
                var prefix = p[..^1].TrimEnd();
                if (prefix.Length == 0) return true;                 // 仅 "*"：匹配全部命令
                if (string.Equals(normalizedCommand, prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (normalizedCommand.Length > prefix.Length &&
                    normalizedCommand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    char.IsWhiteSpace(normalizedCommand[prefix.Length]))
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
        if (IsAlwaysAsk(command ?? pattern, pattern) || IsAlwaysAsk(pattern)) return false;
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
