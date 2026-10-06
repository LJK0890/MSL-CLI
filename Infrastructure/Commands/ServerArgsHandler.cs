using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server arg 动作的处理器：读写服务器的启动参数。
/// 模型：javaPath(String)、jvmArgs(List)、jarArgs(String)、appendArgs(List)；
/// 权限：javaPath 可读写不可删，jvmArgs 可读写删，jarArgs 可读写不可删，appendArgs 可读写删。
/// 用法：<c>arg get|set|rm &lt;服务器名&gt; &lt;参数&gt; [值...]</c>；省略服务器名时使用高亮服务器。
/// </summary>
internal static class ServerArgsHandler
{
    /// <summary>动作别名到标准动作的映射。</summary>
    private static readonly Dictionary<string, string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["get"] = "get",
        ["set"] = "set",
        ["rm"] = "rm",
        ["remove"] = "rm"
    };

    /// <summary>可读取的参数名。</summary>
    private static readonly string[] ReadableParams =
        { "javaPath", "jvmArgs", "jarArgs", "appendArgs", "javaArgs", "all" };

    /// <summary>可整体写入的参数名。</summary>
    private static readonly string[] WritableParams =
        { "javaPath", "jvmArgs", "jarArgs", "appendArgs" };

    /// <summary>可删除的参数名。javaPath 与 jarArgs 不可删除。</summary>
    private static readonly string[] DeletableParams =
        { "jvmArgs", "appendArgs" };

    /// <summary>
    /// 处理 arg 动作。
    /// </summary>
    /// <param name="rest">arg 之后的参数文本。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, Usage());

        if (!Actions.TryGetValue(parts[0], out var action))
            return Fail(output, $"未知启动参数动作 '{parts[0]}'，可用: get、set、rm");

        var tail = parts.Length > 1 ? parts[1] : string.Empty;
        return action switch
        {
            "get" => Get(args, tail, output),
            "set" => Set(args, tail, output),
            "rm" => Remove(args, tail, output),
            _ => Fail(output, Usage())
        };
    }

    /// <summary>
    /// 读取启动参数；element 子关键字用于读取列表中的单个元素。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="tail">动作之后的参数。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Get(CommandArgs args, string tail, IOutputWriter? output)
    {
        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, "用法: $server arg get <服务器名|all> <参数> [element <索引>]");

        // 目标可以是 all（列出全部服务器），否则解析单个服务器
        string? target;
        int paramIndex;
        if (parts[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            target = "all";
            paramIndex = 1;
        }
        else if (ServerTargetResolver.IsServer(args, parts[0]) || parts.Length == 1)
        {
            // 首个参数是服务器名，或只给了一个参数（按参数名处理，使用高亮）
            if (ServerTargetResolver.IsServer(args, parts[0]))
            {
                target = parts[0];
                paramIndex = 1;
            }
            else
            {
                target = ServerTargetResolver.Resolve(args, null, out var err);
                if (target == null) return Fail(output, err!);
                paramIndex = 0;
            }
        }
        else
        {
            target = ServerTargetResolver.Resolve(args, null, out var err);
            if (target == null) return Fail(output, err!);
            paramIndex = 0;
        }

        if (paramIndex >= parts.Length)
            return Fail(output, "用法: $server arg get <服务器名|all> <参数> [element <索引>]");

        if (!TryMatch(parts[paramIndex], ReadableParams, out var param))
            return Fail(output, $"无效参数 '{parts[paramIndex]}'，允许: {string.Join(", ", ReadableParams)}");

        // 解析可选的 element <索引>
        int? elementIndex = null;
        if (parts.Length > paramIndex + 1)
        {
            if (!parts[paramIndex + 1].Equals("element", StringComparison.OrdinalIgnoreCase) || parts.Length < paramIndex + 3)
                return Fail(output, "用法: $server arg get <服务器名|all> <参数> [element <索引>]");

            if (!int.TryParse(parts[paramIndex + 2], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var idx) || idx < 0)
                return Fail(output, $"无效索引 '{parts[paramIndex + 2]}'，应为从 0 开始的整数");
            elementIndex = idx;
        }

        if (elementIndex.HasValue && !IsListParam(param))
            return Fail(output, $"参数 '{param}' 不是列表，不支持 element 读取（仅 jvmArgs / appendArgs）");

        var servers = target.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? args.ServerRegistry.All.Values.ToList()
            : new List<IServer> { args.ServerRegistry.GetServer(target)! };

        var sb = new System.Text.StringBuilder();
        foreach (var svr in servers)
        {
            var model = svr.Argument;
            if (elementIndex.HasValue)
            {
                var list = param.Equals("jvmArgs", StringComparison.OrdinalIgnoreCase) ? model.JvmArgs : model.AppendArgs;
                sb.AppendLine(elementIndex.Value < list.Count
                    ? $"{svr.Name}: {param}[{elementIndex.Value}] = {list[elementIndex.Value]}"
                    : $"{svr.Name}: {param}[{elementIndex.Value}] 索引越界");
            }
            else
            {
                sb.AppendLine($"{svr.Name}: {param} = {FormatParam(model, param)}");
            }
        }

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }

    /// <summary>
    /// 写入启动参数：javaPath / jarArgs 为单值；jvmArgs / appendArgs 整体替换整个列表。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="tail">动作之后的参数。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Set(CommandArgs args, string tail, IOutputWriter? output)
    {
        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return Fail(output, "用法: $server arg set <服务器名> <参数> <值...>");

        if (!ServerTargetResolver.IsServer(args, parts[0]))
            return Fail(output, $"未找到服务器 '{parts[0]}'");

        if (!TryMatch(parts[1], WritableParams, out var param))
            return Fail(output, $"参数 '{parts[1]}' 不允许设置，允许: {string.Join(", ", WritableParams)}");

        var values = parts.Skip(2).ToArray();
        var model = args.ServerRegistry.GetServer(parts[0])!.Argument;

        try
        {
            switch (param)
            {
                case "javaPath": model.SetJavaPath(string.Join(" ", values)); break;
                case "jarArgs": model.SetJarArgs(string.Join(" ", values)); break;
                case "jvmArgs": model.SetJvmArgs(values); break;
                case "appendArgs": model.SetAppendArgs(values); break;
            }
        }
        catch (Exception ex)
        {
            return Fail(output, $"设置失败: {ex.Message}");
        }

        var msg = $"{parts[0]}: {param} 已更新为 {FormatParam(model, param)}";
        output?.Write("Command", LogLevel.Success, msg);
        return new CommandResult(1, msg);
    }

    /// <summary>
    /// 删除启动参数：仅 jvmArgs / appendArgs 可删除；
    /// 不带值或 all 时清空整个列表，否则按值删除单个或多个元素。
    /// </summary>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="tail">动作之后的参数。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static CommandResult Remove(CommandArgs args, string tail, IOutputWriter? output)
    {
        var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return Fail(output, "用法: $server arg rm <服务器名> <参数> [<值>|all]");

        if (!ServerTargetResolver.IsServer(args, parts[0]))
            return Fail(output, $"未找到服务器 '{parts[0]}'");

        // 删除时必须精确匹配参数名：放宽成“前缀匹配”会让
        // "$server arg rm yz jvmArgs-Xmx4G"（漏空格）被当成清空整个 jvmArgs 列表
        if (!TryMatch(parts[1], ReadableParams, out var param, allowGlued: false))
            return Fail(output, $"无效参数 '{parts[1]}'，允许: {string.Join(", ", ReadableParams)}");

        if (!DeletableParams.Contains(param, StringComparer.OrdinalIgnoreCase))
            return Fail(output, $"{param} 不可删除（仅 jvmArgs / appendArgs 可删除）");

        var model = args.ServerRegistry.GetServer(parts[0])!.Argument;
        var values = parts.Skip(2).ToArray();

        // 不带值、或仅一个 all：清空整个列表
        if (values.Length == 0 || (values.Length == 1 && values[0].Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            if (param.Equals("jvmArgs", StringComparison.OrdinalIgnoreCase))
                model.SetJvmArgs(Array.Empty<string>());
            else
                model.SetAppendArgs(Array.Empty<string>());

            var cleared = $"{parts[0]}: {param} 已全部清空";
            output?.Write("Command", LogLevel.Success, cleared);
            return new CommandResult(1, cleared);
        }

        var removed = new List<string>();
        var missing = new List<string>();
        foreach (var value in values)
        {
            var ok = param.Equals("jvmArgs", StringComparison.OrdinalIgnoreCase)
                ? model.RemoveJvmArg(value)
                : model.RemoveAppendArg(value);
            (ok ? removed : missing).Add(value);
        }

        if (removed.Count == 0)
            return Fail(output, $"未找到要删除的 {param} 元素: {string.Join(", ", missing)}");

        var sb = new System.Text.StringBuilder();
        sb.Append($"{parts[0]}: {param} 已删除 {string.Join(", ", removed)}");
        if (missing.Count > 0) sb.Append($"; 未找到: {string.Join(", ", missing)}");
        sb.Append($"; 当前为: {FormatParam(model, param)}");

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }

    // ---------- 辅助 ----------

    /// <summary>判断参数名是否对应列表模型。</summary>
    /// <param name="param">标准化后的参数名。</param>
    /// <returns>是列表返回 true。</returns>
    private static bool IsListParam(string param) =>
        param.Equals("jvmArgs", StringComparison.OrdinalIgnoreCase) ||
        param.Equals("appendArgs", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取整个参数的展示字符串。</summary>
    /// <param name="model">启动参数模型。</param>
    /// <param name="param">标准化后的参数名。</param>
    /// <returns>用于展示的值。</returns>
    private static string FormatParam(ServerArgument model, string param) => param switch
    {
        "javaPath" => model.JavaPath,
        "jvmArgs" => FormatList(model.JvmArgs),
        "jarArgs" => model.JarArgs,
        "appendArgs" => FormatList(model.AppendArgs),
        "javaArgs" => model.GetJavaArgs(),
        "all" => model.GetStartArguments(),
        _ => string.Empty
    };

    /// <summary>
    /// 优先按“整个参数名”匹配，其次（可选的）按参数名前缀匹配。
    /// </summary>
    /// <param name="token">用户输入的参数名。</param>
    /// <param name="allowed">允许的参数名。</param>
    /// <param name="matched">匹配到的标准参数名。</param>
    /// <param name="allowGlued">
    /// 是否允许“粘连”写法（如 <c>jvmArgs-Xmx4G</c>）；删除类操作必须传 false。
    /// </param>
    /// <returns>匹配成功返回 true。</returns>
    private static bool TryMatch(string token, string[] allowed, out string matched, bool allowGlued = true)
    {
        var exact = allowed.FirstOrDefault(a => a.Equals(token, StringComparison.OrdinalIgnoreCase));
        if (exact != null) { matched = exact; return true; }

        if (!allowGlued)
        {
            matched = string.Empty;
            return false;
        }

        var glued = allowed
            .Where(a => token.StartsWith(a, StringComparison.OrdinalIgnoreCase) && token.Length > a.Length)
            .OrderByDescending(a => a.Length)
            .FirstOrDefault();
        if (glued != null) { matched = glued; return true; }

        matched = string.Empty;
        return false;
    }

    /// <summary>以带索引的形式格式化列表。</summary>
    /// <param name="items">列表内容。</param>
    /// <returns>形如 "[0] -Xms2G [1] nogui" 的文本；空列表返回 "(空)"。</returns>
    private static string FormatList(IReadOnlyList<string> items)
        => items.Count == 0 ? "(空)" : string.Join(" ", items.Select((v, i) => $"[{i}] {v}"));

    /// <summary>用法说明。</summary>
    /// <returns>用法文本。</returns>
    private static string Usage() =>
        "用法: $server arg get|set|rm <服务器名> <参数> [值...]\n" +
        "  arg get <服务器名> jvmArgs [element <索引>]   # 读取（列表带索引）\n" +
        "  arg set <服务器名> jvmArgs <值...>            # 整体替换列表\n" +
        "  arg rm  <服务器名> jvmArgs <值>|all           # 删除单个 / 清空\n" +
        "参数权限: javaPath 可读写不可删、jvmArgs 可读写删、jarArgs 可读写不可删、appendArgs 可读写删";

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
