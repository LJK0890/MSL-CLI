using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 文件操作命令（$file），仅允许在白名单目录内执行读写操作。
/// </summary>
public class FileCommand : ICommand, IArgValidatingCommand
{
    /// <summary>
    /// 命令名称：$file。
    /// </summary>
    public string Name => "$file";
    /// <summary>
    /// 命令描述：文件操作（仅限白名单目录）。
    /// </summary>
    public string Description => "文件操作（仅限白名单目录），子命令: read, write, list, delete。支持占位符：%appdata%, %<服务器名>%";

    /// <summary>允许的子命令。</summary>
    private static readonly string[] SubCommands = { "read", "write", "list", "delete" };

    /// <summary>
    /// 参数校验钩子：检查子命令是否有效、是否给了路径。
    /// </summary>
    /// <param name="rawArgs">$file 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令校验不需要，可为 null）。</param>
    /// <param name="error">不合法时的说明文本。</param>
    /// <returns>参数合法时返回 true。</returns>
    public bool TryValidateArgs(string rawArgs, CommandArgs? args, out string error)
    {
        error = string.Empty;

        var parts = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = $"$file 缺少子命令，可用: {string.Join("、", SubCommands)}";
            return false;
        }

        if (!SubCommands.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            error = $"$file 未知子命令 '{parts[0]}'，可用: {string.Join("、", SubCommands)}。";
            return false;
        }

        if (parts.Length < 2)
        {
            error = "用法: $file read|write|list|delete <路径> [内容]";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取出授权范围：命令 + 子命令（如 <c>$file write</c>）。
    /// </summary>
    /// <param name="rawArgs">$file 之后的参数文本。</param>
    /// <param name="args">命令参数上下文（本命令判定不需要，可为 null）。</param>
    /// <returns>授权范围。</returns>
    public string GetPermissionScope(string rawArgs, CommandArgs? args)
    {
        var parts = rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Name;

        var sub = parts[0].ToLowerInvariant();
        return SubCommands.Contains(sub, StringComparer.OrdinalIgnoreCase) ? $"{Name} {sub}" : Name;
    }

    /// <summary>
    /// 初始化锁，保证白名单目录列表的重建是线程安全的。
    /// </summary>
    private static readonly object _initLock = new();
    /// <summary>
    /// 允许访问的根目录列表（应用数据目录 + 各服务器目录）。
    /// </summary>
    private static List<string> _allowedDirs = new();
    /// <summary>
    /// 服务器名到服务器目录路径的映射，用于 %&lt;服务器名&gt;% 占位符替换。
    /// </summary>
    private static Dictionary<string, string> _serverPathMap = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 应用数据目录路径（%appdata% 占位符的解析结果）。
    /// </summary>
    private static string _appDataPath = string.Empty;

    /// <summary>
    /// 重建允许目录列表（线程安全）：收集应用数据目录与当前所有服务器目录。
    /// 每次执行都会重建，使 <c>$server add</c>/<c>rm</c> 与 <c>$app reload</c> 立即生效，
    /// 避免已移除的服务器目录在会话剩余时间内仍然可写。
    /// </summary>
    /// <param name="registry">服务器注册表，用于获取各服务器目录。</param>
    private void EnsureInitialized(IServerRegistry registry)
    {
        lock (_initLock)
        {
            _appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appDir = Path.Combine(_appDataPath, "MSL_CLI");

            var dirs = new List<string> { appDir };
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in registry.All)
            {
                string path = Path.GetFullPath(kv.Value.Path);
                dirs.Add(path);
                map[kv.Key] = path;
            }

            _allowedDirs = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _serverPathMap = map;
        }
    }

    /// <summary>
    /// 解析路径中的 %appdata% 与 %&lt;服务器名&gt;% 占位符为实际目录。
    /// </summary>
    /// <param name="inputPath">包含占位符的原始路径。</param>
    /// <param name="registry">服务器注册表，用于初始化占位符映射。</param>
    /// <returns>占位符替换后的完整路径。</returns>
    private string ResolvePath(string inputPath, IServerRegistry registry)
    {
        EnsureInitialized(registry);
        string result = inputPath;
        // 替换 %appdata%
        int idx = result.IndexOf("%appdata%", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            result = result.Remove(idx, 9).Insert(idx, _appDataPath);
        // 替换 %<服务器名>%
        foreach (var kv in _serverPathMap)
        {
            string placeholder = "%" + kv.Key + "%";
            if (result.Contains(placeholder, StringComparison.Ordinal))
                result = result.Replace(placeholder, kv.Value);
        }
        return result;
    }

    /// <summary>
    /// 判断规范化后的完整路径是否位于任一允许目录之内。
    /// 必须按目录分隔符比较，否则 <c>…\MSL_CLI_x</c> 会被误判为位于 <c>…\MSL_CLI</c> 之内。
    /// </summary>
    /// <param name="fullPath">待校验的完整路径。</param>
    /// <returns>位于允许目录内返回 true，否则返回 false。</returns>
    private bool IsPathAllowed(string fullPath)
    {
        // 确保路径已规范化
        string normalized = Path.GetFullPath(fullPath);
        foreach (var dir in _allowedDirs)
        {
            if (IsInside(normalized, Path.GetFullPath(dir)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 判断路径是否等于根目录本身或位于其子目录中（按目录分隔符边界比较）。
    /// </summary>
    /// <param name="normalizedPath">已规范化的绝对路径。</param>
    /// <param name="normalizedRoot">已规范化的根目录绝对路径。</param>
    /// <returns>位于根目录内时返回 true。</returns>
    private static bool IsInside(string normalizedPath, string normalizedRoot)
    {
        var root = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0) root = normalizedRoot; // 驱动器根（如 "C:\"）不能被完全裁剪

        return string.Equals(normalizedPath.TrimEnd(Path.DirectorySeparatorChar), root,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 执行 $file 命令，按子命令执行读取、写入、列目录或删除操作。
    /// 路径与内容都支持用双引号包裹，因此可以处理含空格的路径。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
            return Task.FromResult(Fail(output, "用法: $file <子命令> <路径> [内容]"));

        // 逐段读取：子命令 → 路径 → （可选）内容，引号内的空白不会被当作分隔符
        if (!TryReadToken(args.Raw, out var subCmdRaw, out var afterSub))
            return Task.FromResult(Fail(output, "用法: $file <子命令> <路径> [内容]"));

        if (!TryReadToken(afterSub, out var rawPath, out var afterPath))
            return Task.FromResult(Fail(output, "子命令和路径必须指定"));

        // 解析子命令、路径与可选的内容参数
        string subCmd = subCmdRaw.ToLowerInvariant();
        string? content = afterPath.Length == 0 ? null : Unquote(afterPath);

        string resolved = ResolvePath(rawPath, args.ServerRegistry);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolved);
        }
        catch
        {
            return Task.FromResult(Fail(output, $"无效路径: {resolved}"));
        }

        // 越权校验：路径必须位于允许目录内
        if (!IsPathAllowed(fullPath))
            return Task.FromResult(Fail(output, $"路径 '{fullPath}' 不在允许的目录中"));

        try
        {
            return Task.FromResult(FileOperations.Execute(subCmd, fullPath, content, output));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(output, $"文件操作失败: {ex.Message}"));
        }
    }

    /// <summary>
    /// 从原始文本开头读取一个参数：跳过前导空白，遇到双引号时读取到配对的收尾引号，
    /// 否则读取到下一个空白为止；引号本身会被去掉。
    /// </summary>
    /// <param name="raw">原始文本。</param>
    /// <param name="token">读出的参数值（已去引号）。</param>
    /// <param name="rest">参数之后的剩余文本（已去首尾空白）。</param>
    /// <returns>成功读出参数时返回 true。</returns>
    private static bool TryReadToken(string raw, out string token, out string rest)
    {
        token = string.Empty;
        rest = string.Empty;

        var i = 0;
        while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
        if (i >= raw.Length) return false;

        var sb = new StringBuilder();
        if (raw[i] == '"')
        {
            i++;
            while (i < raw.Length && raw[i] != '"') sb.Append(raw[i++]);
            if (i < raw.Length) i++; // 跳过收尾引号
        }
        else
        {
            while (i < raw.Length && !char.IsWhiteSpace(raw[i])) sb.Append(raw[i++]);
        }

        token = sb.ToString();
        rest = raw[i..].Trim();
        return true;
    }

    /// <summary>
    /// 去掉文本两端成对的双引号（仅一层），其余原样返回。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>去引号后的文本。</returns>
    private static string Unquote(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];
        return trimmed;
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
