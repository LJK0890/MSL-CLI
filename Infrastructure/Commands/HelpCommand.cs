using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $help 命令：不带参数时列出所有命令及其说明；带参数时输出单个命令的详细用法。
/// 用法：<c>$help [命令名]</c>。
/// </summary>
public class HelpCommand : ICommand
{
    /// <summary>命令名称：$help。</summary>
    public string Name => "$help";

    /// <summary>命令描述。</summary>
    public string Description => "查看命令帮助，用法: $help [命令名]；不带参数列出全部命令与说明";

    /// <summary>
    /// 执行 $help 命令。
    /// </summary>
    /// <param name="args">命令参数，需包含已注入的命令解析器（Parser）。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var parser = args.Parser;
        if (parser == null)
        {
            var msg = "命令解析器未注入，无法提供帮助";
            output?.Write("Command", LogLevel.Error, msg);
            return Task.FromResult(new CommandResult(0, msg));
        }

        var descriptions = parser.GetCommandDescriptions();
        var target = args.Raw.Trim();

        // 不带参数：列出全部命令与说明
        if (string.IsNullOrEmpty(target))
        {
            var sb = new StringBuilder();
            sb.AppendLine("可用命令：");
            foreach (var kv in descriptions.OrderBy(k => k.Key))
                sb.AppendLine($"  {kv.Key,-14} {kv.Value}");
            sb.AppendLine();
            sb.AppendLine("用 $help <命令名> 查看单个命令的详细用法。");

            output?.Write("Command", LogLevel.Success, sb.ToString());
            return Task.FromResult(new CommandResult(1, sb.ToString()));
        }

        // 带参数：输出单个命令的详细用法
        var name = target.StartsWith('$') ? target : "$" + target;
        var command = parser.GetCommand(name);
        if (command == null)
        {
            var available = string.Join(" ", descriptions.Keys.OrderBy(k => k));
            var msg = $"未找到命令 '{target}'。可用命令: {available}";
            output?.Write("Command", LogLevel.Error, msg);
            return Task.FromResult(new CommandResult(0, msg));
        }

        var detail = new StringBuilder();
        detail.AppendLine($"{command.Name}");
        detail.AppendLine($"  说明: {command.Description}");
        detail.AppendLine($"  详细用法: {HelpText.For(command.Name)}");

        output?.Write("Command", LogLevel.Success, detail.ToString());
        return Task.FromResult(new CommandResult(1, detail.ToString()));
    }
}

/// <summary>
/// 各命令的详细用法文本表，供 $help &lt;命令名&gt; 使用。
/// </summary>
internal static class HelpText
{
    /// <summary>命令名到详细用法的映射。</summary>
    private static readonly Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$ai"] = "$ai chat|agent [配置名] <消息>   # 对话 / 代理执行；省略配置名时用 DefaultAIConfig",
        ["$app"] = "$app cfg get [路径] | $app cfg getall | $app cfg set <路径> <值> | $app cfg rm <路径>\n" +
                   "         $app exit | $app reload | $app ptcfg",
        ["$exec"] = "$exec <命令/脚本路径> [参数...]   # 执行系统命令；由 AI 代理调用时需操作员确认",
        ["$file"] = "$file read|write|list|delete <路径> [内容]   # 仅限白名单目录；支持 %appdata%、%<服务器名>%",
        ["$help"] = "$help [命令名]",
        ["$list"] = "$list   # 只列出全部命令名（不含说明）",
        ["$hl"] = "$hl <服务器名>   # 切换高亮服务器；不带参数显示当前高亮",
        ["$server"] = "$server cfg get|getall|set|rm [服务器名] [键] [值]\n" +
                      "         $server arg get|set|rm <服务器名> <参数> [值...]\n" +
                      "         $server ck wl|op|bp|bip <服务器名|all> [名称]\n" +
                      "         $server buf read|update <服务器名>\n" +
                      "         $server ls\n" +
                      "         $server bp <服务器名> [备注]\n" +
                      "         $server query <服务器名|all>\n" +
                      "         $server status [服务器名|all]\n" +
                      "         $server stop <服务器名|all>\n" +
                      "         $server run <服务器名>\n" +
                      "         $server send <服务器名|all> <命令>"
    };

    /// <summary>
    /// 获取命令的详细用法。
    /// </summary>
    /// <param name="commandName">命令名称。</param>
    /// <returns>详细用法文本；未登记时返回通用提示。</returns>
    public static string For(string commandName)
        => Texts.TryGetValue(commandName, out var text) ? text : "（暂无详细说明）";
}
