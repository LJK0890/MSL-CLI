using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $list 命令：只列出全部命令名，不显示帮助说明。
/// 用法：<c>$list</c>。
/// </summary>
public class ListCommand : ICommand
{
    /// <summary>命令名称：$list。</summary>
    public string Name => "$list";

    /// <summary>命令描述。</summary>
    public string Description => "只列出全部命令名（不含说明），用法: $list";

    /// <summary>
    /// 执行 $list 命令，输出全部命令名称。
    /// </summary>
    /// <param name="args">命令参数，需包含已注入的命令解析器（Parser）。</param>
    /// <param name="output">输出写入器，可为 null。</param>
    /// <returns>命令执行结果。</returns>
    public Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        var parser = args.Parser;
        if (parser == null)
        {
            var msg = "命令解析器未注入，无法列出命令";
            output?.Write("Command", LogLevel.Error, msg);
            return Task.FromResult(new CommandResult(0, msg));
        }

        var names = parser.GetCommandDescriptions().Keys.OrderBy(k => k, StringComparer.Ordinal);
        var text = string.Join(" ", names);

        output?.Write("Command", LogLevel.Success, text);
        return Task.FromResult(new CommandResult(1, text));
    }
}
