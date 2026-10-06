using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// <c>$server file &lt;选项&gt; &lt;参数...&gt;</c> 的处理器（也可写成 <c>$server &lt;服务器名&gt; file ...</c>）。
/// 选项：<c>-r</c> 读取、<c>-w</c> 覆盖写入、<c>-a</c> 追加、<c>-rm</c> 删除、
/// <c>-cp</c> 复制、<c>-mv</c> 移动/重命名、<c>-ls</c> 列目录。
/// 所有路径都相对该服务器目录解析并做越界校验，因此不可能读写到服务器目录之外。
/// 省略服务器名时使用高亮服务器（与 cfg/arg 等动作一致）。
/// </summary>
internal static class ServerFileHandler
{
    /// <summary>选项到“授权范围用名”的映射。</summary>
    private static readonly Dictionary<string, string> FlagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-r"] = "read",
        ["-w"] = "write",
        ["-a"] = "append",
        ["-rm"] = "delete",
        ["-cp"] = "copy",
        ["-mv"] = "move",
        ["-ls"] = "list"
    };

    /// <summary>受支持的选项列表（用于校验与提示）。</summary>
    public static readonly string[] Flags = { "-r", "-w", "-a", "-rm", "-cp", "-mv", "-ls" };

    /// <summary>每个选项所需的参数个数（含选项本身）。</summary>
    private static readonly Dictionary<string, int> RequiredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-r"] = 2,
        ["-rm"] = 2,
        ["-w"] = 3,
        ["-a"] = 3,
        ["-cp"] = 3,
        ["-mv"] = 3,
        ["-ls"] = 1
    };

    /// <summary>
    /// 选项对应的授权范围用名（如 -r → read）。
    /// </summary>
    /// <param name="flag">选项文本。</param>
    /// <returns>范围用名；未识别时返回 null。</returns>
    public static string? ScopeName(string flag) => FlagNames.GetValueOrDefault(flag);

    /// <summary>
    /// 校验 <c>file</c> 选项及其参数数量。
    /// </summary>
    /// <param name="tokens">参数 token 列表。</param>
    /// <param name="flagIndex">选项所在下标。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>合法时返回 true。</returns>
    public static bool Validate(string[] tokens, int flagIndex, out string error)
    {
        error = string.Empty;

        if (tokens.Length <= flagIndex)
        {
            error = Usage();
            return false;
        }

        var flag = tokens[flagIndex].ToLowerInvariant();
        if (!RequiredTokens.TryGetValue(flag, out var required))
        {
            error = $"未知文件选项 '{tokens[flagIndex]}'，可用: {string.Join("、", Flags)}";
            return false;
        }

        if (tokens.Length - flagIndex < required)
        {
            error = Usage();
            return false;
        }

        return true;
    }

    /// <summary>
    /// 处理“$server file ...”形式：服务器名省略时使用高亮服务器。
    /// </summary>
    /// <param name="rest">file 之后的参数文本（选项与参数）。</param>
    /// <param name="args">命令参数，包含注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult HandleForHighlighted(string rest, CommandArgs args, IOutputWriter? output)
    {
        var highlighted = args.ServerRegistry.HighlightedServerName;
        if (string.IsNullOrEmpty(highlighted))
            return Fail(output, "未设置高亮服务器，请写成 $server <服务器名> file ... 或先用 $server hl <服务器名> 设置高亮");

        return Handle(highlighted, rest, args, output);
    }

    /// <summary>
    /// 处理“$server &lt;服务器名&gt; file ...”形式。
    /// </summary>
    /// <param name="serverName">服务器名（已确认存在）。</param>
    /// <param name="rest">file 之后的参数文本：选项 + 参数。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string serverName, string rest, CommandArgs args, IOutputWriter? output)
    {
        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null) return Fail(output, $"未找到服务器 '{serverName}'");

        var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Fail(output, Usage());

        // 运行时同样校验选项与参数数量（控制台路径不经过 TryValidateArgs）
        if (!Validate(tokens, 0, out var validationError))
            return Fail(output, validationError);

        var flag = tokens[0].ToLowerInvariant();
        if (!RequiredTokens.ContainsKey(flag))
            return Fail(output, $"未知文件选项 '{tokens[0]}'，可用: {string.Join("、", Flags)}");

        var root = Path.GetFullPath(server.Path);

        // content 取剩余全部 token（允许内容里带空格）
        string? content = null;
        string[] operands;

        switch (flag)
        {
            case "-w":
            case "-a":
                content = string.Join(" ", tokens.Skip(2));
                operands = new[] { tokens[1] };
                break;

            default:
                operands = tokens.Skip(1).ToArray();
                break;
        }

        if (flag == "-ls" && operands.Length == 0) operands = new[] { "." };

        if (operands.Length == 0) return Fail(output, Usage());

        // 全部路径统一解析并做越界校验
        var resolved = new List<string>();
        foreach (var operand in operands)
        {
            if (!TryResolveInside(root, operand, out var fullPath, out var error))
                return Fail(output, error!);
            resolved.Add(fullPath);
        }

        return flag switch
        {
            "-r" => FileOperations.Execute("read", resolved[0], null, output),
            "-w" => FileOperations.Execute("write", resolved[0], content, output),
            "-a" => FileOperations.Append(resolved[0], content, output),
            "-rm" => FileOperations.Execute("delete", resolved[0], null, output),
            "-cp" => FileOperations.Copy(resolved[0], resolved[1], output),
            "-mv" => FileOperations.Move(resolved[0], resolved[1], output),
            "-ls" => FileOperations.Execute("list", resolved[0], null, output),
            _ => Fail(output, $"未知文件选项 '{flag}'，可用: {string.Join("、", Flags)}")
        };
    }

    /// <summary>
    /// 把操作数解析为服务器目录内的绝对路径，并拒绝越界。
    /// </summary>
    /// <param name="root">服务器目录（绝对路径）。</param>
    /// <param name="operand">用户给出的路径（相对服务器目录，或目录内的绝对路径）。</param>
    /// <param name="fullPath">解析后的绝对路径。</param>
    /// <param name="error">拒绝原因。</param>
    /// <returns>解析成功返回 true。</returns>
    private static bool TryResolveInside(string root, string operand, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        var raw = operand.Trim().Trim('"');

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, raw));
        }
        catch (Exception ex)
        {
            error = $"无效路径: {raw}（{ex.Message}）";
            return false;
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var inside = string.Equals(normalizedTarget, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                     || normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (!inside)
        {
            error = $"路径越界：'{raw}' 解析为 {fullPath}，不在服务器目录 {root} 内";
            return false;
        }

        // 词法越界校验挡不住“服务器目录内的符号链接/junction 指向别处”：
        // 逐级检查已存在的路径分量，遇到重解析点就拒绝，保证读写不会落到服务器目录之外
        if (TryFindReparsePoint(normalizedRoot, normalizedTarget, out var linkPath))
        {
            error = $"路径越界：'{raw}' 经过符号链接/联接点 {linkPath}，无法确认其仍位于服务器目录内";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 在 <paramref name="target"/> 相对于 <paramref name="root"/> 的各层分量中查找重解析点
    /// （符号链接 / junction）。只检查已存在的分量，不存在的分量不可能被解析到别处。
    /// </summary>
    /// <param name="root">服务器目录（已去除末尾分隔符）。</param>
    /// <param name="target">目标路径（已去除末尾分隔符）。</param>
    /// <param name="linkPath">找到的重解析点路径。</param>
    /// <returns>存在重解析点时返回 true。</returns>
    private static bool TryFindReparsePoint(string root, string target, out string linkPath)
    {
        linkPath = string.Empty;

        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        var current = root;
        foreach (var segment in target[(root.Length + 1)..]
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            try
            {
                if (!File.Exists(current) && !Directory.Exists(current))
                    return false; // 之后的层级都不存在

                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    linkPath = current;
                    return true;
                }
            }
            catch
            {
                // 读取属性失败时不阻断（例如权限不足），交由后续文件操作给出真实错误
            }
        }

        return false;
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

    /// <summary>用法说明。</summary>
    /// <returns>用法文本。</returns>
    private static string Usage() =>
        "用法: $server [服务器名] file <选项> <参数...>（省略服务器名时用高亮服务器）\n" +
        "  -r  <路径>            读取文件内容\n" +
        "  -w  <路径> <内容>     写入（覆盖，自动创建目录）\n" +
        "  -a  <路径> <内容>     追加一行（文件不存在时创建）\n" +
        "  -rm <路径>            删除文件或空目录\n" +
        "  -cp <源> <目标>       复制文件/目录（目标已存在时拒绝）\n" +
        "  -mv <源> <目标>       移动 / 重命名（目标已存在时拒绝）\n" +
        "  -ls [路径]            列出目录（默认服务器根目录）\n" +
        "路径相对服务器目录，且不允许越界（如 ..\\..\\x、C:\\Windows\\x 都会被拒绝）。";
}
