using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;
using System.Text;
using System.Text.Json;

/// <summary>
/// 检查服务器 JSON 列表文件（白名单、封禁玩家、封禁 IP）的辅助类。
/// </summary>
internal static class CheckListHelper
{
    /// <summary>
    /// 读取指定列表文件并执行检查：输出完整列表，或检索目标名称是否在列表中。
    /// </summary>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="serverName">目标服务器名称。</param>
    /// <param name="targetName">要检索的目标名称（玩家名或 IP），可为 null 表示输出完整列表。</param>
    /// <param name="fileName">列表文件名，如 whitelist.json。</param>
    /// <param name="listType">列表类型的中文名称，用于提示信息。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> ExecuteAsync(
        CommandArgs args,
        string serverName,
        string? targetName,
        string fileName,
        string listType,
        IOutputWriter? output)
    {
        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"未找到服务器 '{serverName}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 拼接列表文件路径（位于服务器目录下）
        string filePath = Path.Combine(server.Path, fileName);
        if (!File.Exists(filePath))
        {
            var msg = $"服务器 '{serverName}' 的 {fileName} 不存在，可能未启用对应功能";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }

        try
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 根元素必须是数组，否则视为文件格式无效
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                var msg = $"{fileName} 格式无效（不是数组），视为空";
                output?.Write("Command", LogLevel.Error, msg);
                return new CommandResult(0, msg);
            }

            var entries = new List<string>();
            // 从数组元素中提取名称字段（优先 name，其次 ip）
            foreach (var elem in root.EnumerateArray())
            {
                string? name = null;
                if (elem.TryGetProperty("name", out var nameElem))
                    name = nameElem.GetString();
                else if (elem.TryGetProperty("ip", out var ipElem))
                    name = ipElem.GetString();
                if (!string.IsNullOrEmpty(name))
                    entries.Add(name);
            }

            if (entries.Count == 0)
            {
                var msg = $"服务器 '{serverName}' 的 {listType} 列表为空";
                output?.Write("Command", LogLevel.Success, msg);
                return new CommandResult(1, msg);
            }

            // 未指定目标名称时输出完整列表，否则检索目标是否存在
            if (string.IsNullOrEmpty(targetName))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"服务器 '{serverName}' 的 {listType} 列表 ({entries.Count} 个):");
                foreach (var entry in entries)
                    sb.AppendLine($"  {entry}");
                output?.Write("Command", LogLevel.Success, sb.ToString());
                return new CommandResult(1, sb.ToString());
            }
            else
            {
                bool found = entries.Contains(targetName, StringComparer.OrdinalIgnoreCase);
                var msg = $"'{targetName}' {(found ? "在" : "不在")} 服务器 '{serverName}' 的 {listType} 列表中";
                output?.Write("Command", found ? LogLevel.Success : LogLevel.Error, msg);
                return new CommandResult(found ? 1 : 0, msg);
            }
        }
        catch (Exception ex)
        {
            var msg = $"读取 {fileName} 失败: {ex.Message}";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }
    }
}

/// <summary>
/// 检查服务器 OP 列表的辅助类。
/// </summary>
internal static class CheckOpHelper
{
    /// <summary>
    /// 读取服务器的 OP 列表并执行检查：输出完整列表，或判断指定玩家是否为 OP。
    /// </summary>
    /// <param name="args">命令参数，包含运行环境依赖。</param>
    /// <param name="serverName">目标服务器名称。</param>
    /// <param name="playerName">要检索的玩家名，可为 null 表示输出完整列表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static async Task<CommandResult> ExecuteAsync(
        CommandArgs args,
        string serverName,
        string? playerName,
        IOutputWriter? output)
    {
        var server = args.ServerRegistry.GetServer(serverName);
        if (server == null)
        {
            var msg = $"未找到服务器 '{serverName}'";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var ops = server.GetOps();
        if (ops.Count == 0)
        {
            var msg = $"服务器 '{serverName}' 没有 OP 或 ops.json 为空";
            output?.Write("Command", LogLevel.Success, msg);
            return new CommandResult(1, msg);
        }

        // 未指定玩家名时输出完整 OP 列表，否则判断玩家是否为 OP
        if (string.IsNullOrEmpty(playerName))
        {
            var sb = new StringBuilder();
            sb.AppendLine($"服务器 '{serverName}' 的 OP 列表 ({ops.Count} 个):");
            foreach (var name in ops)
                sb.AppendLine($"  {name}");
            output?.Write("Command", LogLevel.Success, sb.ToString());
            return new CommandResult(1, sb.ToString());
        }
        else
        {
            var isOp = server.IsOp(playerName);
            var msg = $"玩家 '{playerName}' {(isOp ? "是" : "不是")} 服务器 '{serverName}' 的 OP";
            output?.Write("Command", isOp ? LogLevel.Success : LogLevel.Error, msg);
            return new CommandResult(isOp ? 1 : 0, msg);
        }
    }
}

/// <summary>
/// 检查服务器列表的命令（$check），支持白名单、OP、封禁玩家与封禁 IP。
/// </summary>
public class CheckCommand : ICommand
{
    /// <summary>
    /// 命令名称：$check。
    /// </summary>
    public string Name => "$check";
    /// <summary>
    /// 命令描述：检查服务器列表。
    /// </summary>
    public string Description => "检查服务器列表，子命令: whitelist/wl, op, banplayer/bp, banip/bip。用法: $check <子命令> <服务器名> [名称]";

    /// <summary>
    /// 执行 $check 命令，解析子命令与服务器名并分发到对应的检查逻辑。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及运行环境依赖。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        if (string.IsNullOrWhiteSpace(args.Raw))
        {
            var msg = "用法: $check <子命令> <服务器名> [名称]\n子命令: whitelist/wl, op, banplayer/bp, banip/bip";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        var parts = args.Raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            var msg = "缺少子命令和服务器名";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        string subCommand = parts[0].ToLowerInvariant();
        string rest = string.Join(" ", parts.Skip(1));
        // 解析服务器名与可选的目标名称
        var (serverName, target) = ParseServerAndTarget(rest);
        if (string.IsNullOrEmpty(serverName))
        {
            var msg = "必须指定服务器名";
            output?.Write("Command", LogLevel.Error, msg);
            return new CommandResult(0, msg);
        }

        // 按子命令分发到对应的检查逻辑
        switch (subCommand)
        {
            case "whitelist":
            case "wl":
                return await CheckListHelper.ExecuteAsync(args, serverName, target, "whitelist.json", "白名单", output);
            case "banplayer":
            case "bp":
                return await CheckListHelper.ExecuteAsync(args, serverName, target, "banned-players.json", "封禁玩家", output);
            case "banip":
            case "bip":
                return await CheckListHelper.ExecuteAsync(args, serverName, target, "banned-ips.json", "封禁IP", output);
            case "op":
                return await CheckOpHelper.ExecuteAsync(args, serverName, target, output);
            default:
                var defaultMsg = $"未知子命令 '{subCommand}'，可用: whitelist/wl, op, banplayer/bp, banip/bip";
                output?.Write("Command", LogLevel.Error, defaultMsg);
                return new CommandResult(0, defaultMsg);
        }
    }

    /// <summary>
    /// 将剩余参数解析为服务器名与目标名称。
    /// </summary>
    /// <param name="input">剩余参数串。</param>
    /// <returns>包含服务器名与目标名称的元组，服务器名为 null 表示参数为空。</returns>
    private (string? serverName, string? target) ParseServerAndTarget(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return (null, null);
        if (parts.Length == 1)
            return (parts[0], null);
        return (parts[0], string.Join(" ", parts.Skip(1)));
    }
}
