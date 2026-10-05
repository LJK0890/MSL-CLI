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
    /// 初始化锁，保证白名单目录列表只初始化一次。
    /// </summary>
    private static readonly object _initLock = new();
    /// <summary>
    /// 允许访问的根目录列表（应用数据目录 + 各服务器目录）。
    /// </summary>
    private static List<string> _allowedDirs = new();
    /// <summary>
    /// 服务器名到服务器目录路径的映射，用于 %&lt;服务器名&gt;% 占位符替换。
    /// </summary>
    private static Dictionary<string, string> _serverPathMap = new(StringComparer.Ordinal);
    /// <summary>
    /// 应用数据目录路径（%appdata% 占位符的解析结果）。
    /// </summary>
    private static string _appDataPath = string.Empty;
    /// <summary>
    /// 是否已完成初始化。
    /// </summary>
    private static bool _initialized = false;

    /// <summary>
    /// 初始化允许目录列表（幂等且线程安全）：收集应用数据目录与所有服务器目录。
    /// </summary>
    /// <param name="registry">服务器注册表，用于获取各服务器目录。</param>
    private void EnsureInitialized(IServerRegistry registry)
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            _appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appDir = Path.Combine(_appDataPath, "MSL_CLI");
            _allowedDirs.Add(appDir);
            foreach (var kv in registry.All)
            {
                string path = Path.GetFullPath(kv.Value.Path);
                _allowedDirs.Add(path);
                _serverPathMap[kv.Key] = path;
            }
            _allowedDirs = _allowedDirs.Distinct().ToList();
            _initialized = true;
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
    /// </summary>
    /// <param name="fullPath">待校验的完整路径。</param>
    /// <returns>位于允许目录内返回 true，否则返回 false。</returns>
    private bool IsPathAllowed(string fullPath)
    {
        // 确保路径已规范化
        string normalized = Path.GetFullPath(fullPath);
        foreach (var dir in _allowedDirs)
        {
            string normalizedDir = Path.GetFullPath(dir);
            if (normalized.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 执行 $file 命令，按子命令执行读取、写入、列目录或删除操作。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $file <子命令> <路径> [内容]";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var parts = args.Raw.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            var msg = "子命令和路径必须指定";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 解析子命令、路径与可选的内容参数
        string subCmd = parts[0].ToLowerInvariant();
        string rawPath = parts[1];
        string? content = parts.Length > 2 ? parts[2] : null;

        string resolved = ResolvePath(rawPath, args.ServerRegistry);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolved);
        }
        catch
        {
            var msg = $"无效路径: {resolved}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 越权校验：路径必须位于允许目录内
        if (!IsPathAllowed(fullPath))
        {
            var msg = $"路径 '{fullPath}' 不在允许的目录中";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        try
        {
            switch (subCmd)
            {
                case "read":
                    if (!File.Exists(fullPath))
                    {
                        var msg = $"文件不存在: {fullPath}";
                        output?.Write("Command", LogLevel.Error, msg);
                        return new CommandResult(0, msg);
                    }
                    string content2 = File.ReadAllText(fullPath, Encoding.UTF8);
                    output?.Write("Command", LogLevel.Success, $"文件内容 ({fullPath}):\n{content2}");
                    return new CommandResult(1, content2);

                case "write":
                    if (content == null)
                    {
                        var msg = "写入内容不能为空";
                        output?.Write("Command", LogLevel.Error, msg);
                        return new CommandResult(0, msg);
                    }
                    // 自动创建不存在的父目录
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(fullPath, content, Encoding.UTF8);
                    output?.Write("Command", LogLevel.Success, $"已写入文件: {fullPath}");
                    return new CommandResult(1, $"已写入 {fullPath}");

                case "list":
                    if (!Directory.Exists(fullPath))
                    {
                        var msg = $"目录不存在: {fullPath}";
                        output?.Write("Command", LogLevel.Error, msg);
                        return new CommandResult(0, msg);
                    }
                    var entries = Directory.GetFileSystemEntries(fullPath);
                    var sb = new StringBuilder();
                    sb.AppendLine($"目录内容 ({fullPath}):");
                    foreach (var entry in entries)
                    {
                        // 区分目录与文件条目
                        string type = Directory.Exists(entry) ? "[DIR]" : "[FILE]";
                        sb.AppendLine($"  {type} {Path.GetFileName(entry)}");
                    }
                    output?.Write("Command", LogLevel.Success, sb.ToString());
                    return new CommandResult(1, sb.ToString());

                case "delete":
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                    {
                        var msg = $"路径不存在: {fullPath}";
                        output?.Write("Command", LogLevel.Error, msg);
                        return new CommandResult(0, msg);
                    }
                    // 仅允许删除空目录，防止误删数据
                    if (Directory.Exists(fullPath))
                    {
                        if (Directory.GetFileSystemEntries(fullPath).Length > 0)
                        {
                            var msg = "目录非空，拒绝删除";
                            output?.Write("Command", LogLevel.Error, msg);
                            return new CommandResult(0, msg);
                        }
                        Directory.Delete(fullPath);
                    }
                    else
                        File.Delete(fullPath);
                    output?.Write("Command", LogLevel.Success, $"已删除: {fullPath}");
                    return new CommandResult(1, $"已删除 {fullPath}");

                default:
                    var msgDefault = $"未知子命令: {subCmd}，支持: read, write, list, delete";
                    output?.Write("Command", LogLevel.Error, msgDefault);
                    return new CommandResult(0, msgDefault);
            }
        }
        catch (Exception ex)
        {
            var msg = $"文件操作失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }
}
