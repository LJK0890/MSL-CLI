using System.IO.Compression;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server bp 动作的实现：把服务器世界目录打包为 zip 备份到 backups 目录。
/// 通过 <see cref="Make"/> 接收动作之后的参数文本。
/// </summary>
internal static class ServerBackupCommand
{
    /// <summary>
    /// 处理 bp 动作：解析服务器名与备注并执行备份。
    /// </summary>
    /// <param name="rest">bp 之后的参数文本，形如 "yz 备注"。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static Task<CommandResult> Make(string rest, CommandArgs args, IOutputWriter? output)
    {
        var forwarded = new CommandArgs(rest, args.ServerRegistry, args.AgentService, args.ConfigStore)
        {
            Parser = args.Parser
        };
        return ExecuteAsync(forwarded, output);
    }

    /// <summary>
    /// 执行备份：将服务器的世界目录打包为 zip 备份到 backups 目录。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    private static async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $server bp <服务器名> [备注]";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var parts = args.Raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string serverName = parts[0];
        string? remark = parts.Length > 1 ? parts[1] : null;

        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"未找到服务器 '{serverName}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 服务器运行中备份可能不一致，仅提示警告但不中断
        if (server.Status == ServerStatus.Running)
            output?.Write("Command", LogLevel.Warning, "服务器正在运行，备份可能不一致，建议先停止。继续执行备份...");

        string serverPath = server.Path;
        // 获取 level-name
        string levelName = server.Properties.GetValue("level-name", "world");
        string worldDir = Path.Combine(serverPath, levelName);
        if (!Directory.Exists(worldDir))
        {
            var msg = $"世界目录不存在: {worldDir}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        string backupsDir = Path.Combine(serverPath, "backups");
        Directory.CreateDirectory(backupsDir);

        // 以时间戳命名备份文件，可选附加备注
        // 时间戳用不变文化：某些区域设置使用非公历日历，ToString("yyyyMMdd") 会给出错误年份
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string backupName = string.IsNullOrEmpty(remark) ? timestamp : $"{timestamp}_{SanitizeRemark(remark)}";
        string zipPath = Path.Combine(backupsDir, backupName + ".zip");

        // 同一秒内重复备份（或备注被清理后重名）时自动加序号，避免覆盖/失败
        var suffix = 1;
        while (File.Exists(zipPath))
            zipPath = Path.Combine(backupsDir, $"{backupName}({suffix++}).zip");

        try
        {
            output?.Write("Command", LogLevel.Info, $"正在备份 '{worldDir}' 到 '{zipPath}' ...");
            // 将世界目录整体打包为 zip
            ZipFile.CreateFromDirectory(worldDir, zipPath);
            var msg = $"备份完成: {zipPath}";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }
        catch (Exception ex)
        {
            var msg = $"备份失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }

    /// <summary>
    /// 清理备注文本，使其可以安全地作为文件名片段：
    /// 非法文件名字符替换为下划线，并去掉首尾的空白与点号（Windows 不允许结尾的点号）。
    /// </summary>
    /// <param name="remark">用户输入的备注。</param>
    /// <returns>可安全用于文件名的备注；清理后为空时返回 "note"。</returns>
    private static string SanitizeRemark(string remark)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(remark.Length);
        foreach (var c in remark)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var cleaned = sb.ToString().Trim().Trim('.', ' ');
        return cleaned.Length == 0 ? "note" : cleaned;
    }
}
