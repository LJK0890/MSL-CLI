using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 文件读写核心操作，供 <c>$file</c>（白名单目录）与 <c>$server &lt;服务器名&gt; read|write|list|delete</c>
/// （限定在服务器目录内）共用，保证两条入口的行为与提示完全一致。
/// </summary>
internal static class FileOperations
{
    /// <summary>支持的文件子命令。</summary>
    private static readonly string[] SubCommands = { "read", "write", "list", "delete" };

    /// <summary>
    /// 按子命令对已解析的绝对路径执行读取、写入、列目录或删除。
    /// </summary>
    /// <param name="subCommand">子命令（read/write/list/delete）。</param>
    /// <param name="fullPath">已通过越权校验的绝对路径。</param>
    /// <param name="content">写入内容（仅 write 使用）。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Execute(string subCommand, string fullPath, string? content, IOutputWriter? output)
    {
        try
        {
            switch (subCommand.ToLowerInvariant())
            {
                case "read":
                    if (!File.Exists(fullPath))
                        return Fail(output, $"文件不存在: {fullPath}");

                    var text = File.ReadAllText(fullPath, Encoding.UTF8);
                    output?.Write("Command", LogLevel.Success, $"文件内容 ({fullPath}):\n{text}");
                    return new CommandResult(1, text);

                case "write":
                    if (content == null)
                        return Fail(output, "写入内容不能为空");

                    // 自动创建不存在的父目录
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllText(fullPath, content, Encoding.UTF8);
                    output?.Write("Command", LogLevel.Success, $"已写入文件: {fullPath}");
                    return new CommandResult(1, $"已写入 {fullPath}");

                case "list":
                    if (!Directory.Exists(fullPath))
                        return Fail(output, $"目录不存在: {fullPath}");

                    var sb = new StringBuilder();
                    sb.AppendLine($"目录内容 ({fullPath}):");
                    foreach (var entry in Directory.GetFileSystemEntries(fullPath))
                        sb.AppendLine($"  {(Directory.Exists(entry) ? "[DIR]" : "[FILE]")} {Path.GetFileName(entry)}");

                    output?.Write("Command", LogLevel.Success, sb.ToString());
                    return new CommandResult(1, sb.ToString());

                case "delete":
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                        return Fail(output, $"路径不存在: {fullPath}");

                    // 仅允许删除空目录，防止误删数据
                    if (Directory.Exists(fullPath))
                    {
                        if (Directory.GetFileSystemEntries(fullPath).Length > 0)
                            return Fail(output, "目录非空，拒绝删除");

                        Directory.Delete(fullPath);
                    }
                    else
                    {
                        File.Delete(fullPath);
                    }

                    output?.Write("Command", LogLevel.Success, $"已删除: {fullPath}");
                    return new CommandResult(1, $"已删除 {fullPath}");

                default:
                    return Fail(output, $"未知文件子命令: {subCommand}，支持: {string.Join(", ", SubCommands)}");
            }
        }
        catch (Exception ex)
        {
            return Fail(output, $"文件操作失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 追加一行内容到文件末尾（文件不存在时创建；已有内容且末尾无换行时先补一个换行）。
    /// </summary>
    /// <param name="fullPath">已通过越权校验的绝对路径。</param>
    /// <param name="content">要追加的内容。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Append(string fullPath, string? content, IOutputWriter? output)
    {
        if (content == null) return Fail(output, "追加内容不能为空");

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 末尾缺少换行时先补一个，保证每次追加独占一行
            var prefix = string.Empty;
            if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n') prefix = Environment.NewLine;
            }

            File.AppendAllText(fullPath, prefix + content + Environment.NewLine, Encoding.UTF8);
            output?.Write("Command", LogLevel.Success, $"已追加到文件: {fullPath}");
            return new CommandResult(1, $"已追加 {fullPath}");
        }
        catch (Exception ex)
        {
            return Fail(output, $"文件操作失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 复制文件或目录（目录递归复制）。目标已存在时拒绝，避免意外覆盖。
    /// </summary>
    /// <param name="source">源路径。</param>
    /// <param name="target">目标路径；是已存在目录时复制到该目录内。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Copy(string source, string target, IOutputWriter? output)
    {
        try
        {
            if (File.Exists(source))
            {
                if (Directory.Exists(target)) target = Path.Combine(target, Path.GetFileName(source));
                if (File.Exists(target)) return Fail(output, $"目标已存在，拒绝覆盖: {target}");

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.Copy(source, target);
                output?.Write("Command", LogLevel.Success, $"已复制: {source} -> {target}");
                return new CommandResult(1, $"已复制 {target}");
            }

            if (Directory.Exists(source))
            {
                var sourceRoot = Path.GetFullPath(source)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(target)) target = Path.Combine(target, Path.GetFileName(sourceRoot));
                if (Directory.Exists(target)) return Fail(output, $"目标已存在，拒绝覆盖: {target}");

                // 目标位于源目录之内时会递归复制自身（target\target\target…）直到路径超长，
                // 因此必须在开始复制前拒绝，而不是等到抛 IOException 时已留下大量垃圾目录
                var targetFull = Path.GetFullPath(target);
                if (string.Equals(targetFull, sourceRoot, StringComparison.OrdinalIgnoreCase) ||
                    targetFull.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(output, $"目标位于源目录之内，拒绝复制（会无限递归）: {target}");
                }

                CopyDirectory(source, target);
                output?.Write("Command", LogLevel.Success, $"已复制目录: {source} -> {target}");
                return new CommandResult(1, $"已复制目录 {target}");
            }

            return Fail(output, $"路径不存在: {source}");
        }
        catch (Exception ex)
        {
            return Fail(output, $"复制失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 移动（重命名）文件或目录。目标已存在时拒绝。
    /// </summary>
    /// <param name="source">源路径。</param>
    /// <param name="target">目标路径；是已存在目录时移动到该目录内。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Move(string source, string target, IOutputWriter? output)
    {
        try
        {
            var sourceIsFile = File.Exists(source);
            if (!sourceIsFile && !Directory.Exists(source)) return Fail(output, $"路径不存在: {source}");

            if (Directory.Exists(target))
                target = Path.Combine(target, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

            if (File.Exists(target) || Directory.Exists(target))
                return Fail(output, $"目标已存在，拒绝覆盖: {target}");

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (sourceIsFile) File.Move(source, target);
            else Directory.Move(source, target);

            output?.Write("Command", LogLevel.Success, $"已移动: {source} -> {target}");
            return new CommandResult(1, $"已移动 {target}");
        }
        catch (Exception ex)
        {
            return Fail(output, $"移动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 递归复制目录内容。
    /// </summary>
    /// <param name="source">源目录。</param>
    /// <param name="target">目标目录。</param>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
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
}
