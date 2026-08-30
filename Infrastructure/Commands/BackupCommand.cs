using System.IO.Compression;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// 备份服务器世界文件的命令（$backup）。
/// </summary>
public class BackupCommand : ICommand
{
    /// <summary>
    /// 命令名称：$backup。
    /// </summary>
    public string Name => "$backup";
    /// <summary>
    /// 命令描述：备份指定服务器的世界文件到 backups 目录。
    /// </summary>
    public string Description => "备份指定服务器的世界文件到 backups 目录，用法: $backup <服务器名> [备注]";

    /// <summary>
    /// 执行 $backup 命令，将服务器的世界目录打包为 zip 备份到 backups 目录。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $backup <服务器名> [备注]";
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
        {
            output?.Write("Command", LogLevel.Warning, "服务器正在运行，备份可能不一致，建议先停止。继续执行备份...");
        }

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
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupName = string.IsNullOrEmpty(remark) ? timestamp : $"{timestamp}_{remark}";
        string zipPath = Path.Combine(backupsDir, backupName + ".zip");

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
}
