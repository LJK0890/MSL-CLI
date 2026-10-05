using System.Text;
using System.Text.Json;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $server ck 动作的处理器：检查服务器的名单文件（白名单、OP、封禁玩家、封禁 IP）。
/// 用法：<c>ck wl|op|bp|bip &lt;服务器名|all&gt; [名称]</c>；省略服务器名时使用高亮服务器。
/// </summary>
internal static class ServerCheckHandler
{
    /// <summary>子命令与其对应名单文件的映射。</summary>
    private static readonly Dictionary<string, (string FileName, string ListType)> Lists = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wl"] = ("whitelist.json", "白名单"),
        ["whitelist"] = ("whitelist.json", "白名单"),
        ["op"] = ("ops.json", "OP"),
        ["bp"] = ("banned-players.json", "封禁玩家"),
        ["banplayer"] = ("banned-players.json", "封禁玩家"),
        ["bip"] = ("banned-ips.json", "封禁IP"),
        ["banip"] = ("banned-ips.json", "封禁IP")
    };

    /// <summary>
    /// 处理 ck 动作。
    /// </summary>
    /// <param name="rest">ck 之后的参数文本。</param>
    /// <param name="args">命令参数，包含服务器注册表。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public static CommandResult Handle(string rest, CommandArgs args, IOutputWriter? output)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Fail(output, "用法: $server ck wl|op|bp|bip <服务器名|all> [名称]");

        if (!Lists.TryGetValue(parts[0], out var list))
            return Fail(output, $"未知名单 '{parts[0]}'，可用: wl、op、bp、bip");

        // 解析目标服务器与可选名称
        string target;
        string? name = null;
        if (parts.Length == 1)
        {
            target = ServerTargetResolver.Resolve(args, null, out var error) ?? string.Empty;
            if (target.Length == 0) return Fail(output, error!);
        }
        else if (parts[0 + 1].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            target = "all";
            name = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : null;
        }
        else if (ServerTargetResolver.IsServer(args, parts[1]))
        {
            target = parts[1];
            name = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : null;
        }
        else
        {
            // 第二个参数不是服务器名 → 视为要检查的名称，使用高亮服务器
            target = ServerTargetResolver.Resolve(args, null, out var error) ?? string.Empty;
            if (target.Length == 0) return Fail(output, error!);
            name = string.Join(" ", parts.Skip(1));
        }

        var servers = target.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? args.ServerRegistry.All.Values.ToList()
            : new List<IServer> { args.ServerRegistry.GetServer(target)! };

        var sb = new StringBuilder();
        var anyFound = false;
        var allOutput = new List<string>();

        foreach (var server in servers)
        {
            var result = CheckList(server, name, list.FileName, list.ListType, out var isFound, out var text);
            if (result != null) return result;
            if (isFound) anyFound = true;
            allOutput.Add(text!);
        }

        var finalText = string.Join("\n", allOutput);
        output?.Write("Command", LogLevel.Success, finalText);

        // 单服务器且有明确结论时，未命中以非零退出码表示
        if (servers.Count == 1 && !string.IsNullOrEmpty(name))
            return new CommandResult(anyFound ? 1 : 0, finalText);

        return new CommandResult(1, finalText);
    }

    /// <summary>
    /// 读取指定服务器的名单文件并判断目标是否存在。
    /// </summary>
    /// <param name="server">目标服务器。</param>
    /// <param name="name">要检查的名称，为 null 时输出完整列表。</param>
    /// <param name="fileName">名单文件名。</param>
    /// <param name="listType">名单类型的中文名称。</param>
    /// <param name="found">目标是否命中。</param>
    /// <param name="text">输出文本。</param>
    /// <returns>出错时返回失败结果；正常时返回 null。</returns>
    private static CommandResult? CheckList(
        IServer server,
        string? name,
        string fileName,
        string listType,
        out bool found,
        out string? text)
    {
        found = false;
        text = null;

        var filePath = Path.Combine(server.Path, fileName);
        if (!File.Exists(filePath))
        {
            text = $"服务器 '{server.Name}' 的 {fileName} 不存在，可能未启用对应功能";
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                text = $"{fileName} 格式无效（不是数组），视为空";
                return null;
            }

            // 玩家名优先取 name，封禁 IP 取 ip
            var entries = new List<string>();
            foreach (var elem in root.EnumerateArray())
            {
                string? entry = null;
                if (elem.TryGetProperty("name", out var nameElem))
                    entry = nameElem.GetString();
                else if (elem.TryGetProperty("ip", out var ipElem))
                    entry = ipElem.GetString();
                if (!string.IsNullOrEmpty(entry)) entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                text = $"服务器 '{server.Name}' 的 {listType} 列表为空";
                return null;
            }

            if (string.IsNullOrEmpty(name))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"服务器 '{server.Name}' 的 {listType} 列表 ({entries.Count} 个):");
                foreach (var entry in entries)
                    sb.AppendLine($"  {entry}");
                text = sb.ToString().TrimEnd();
                return null;
            }

            found = entries.Contains(name, StringComparer.OrdinalIgnoreCase);
            text = $"'{name}' {(found ? "在" : "不在")} 服务器 '{server.Name}' 的 {listType} 列表中";
            return null;
        }
        catch (Exception ex)
        {
            return Fail(null, $"读取 {fileName} 失败: {ex.Message}");
        }
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
