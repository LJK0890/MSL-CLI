using System.Text;
using MSL_CLI.Core.Domain;
using MSL_CLI.Core.Ports;

namespace MSL_CLI.Infrastructure.Commands;

/// <summary>
/// $help 命令实现，列出所有可用命令的名称与用途说明。
/// </summary>
public class HelpCommand : ICommand
{
    /// <summary>
    /// 命令名称 "$help"。
    /// </summary>
    public string Name => "$help";

    /// <summary>
    /// 命令用途说明，用于帮助信息展示。
    /// </summary>
    public string Description => "显示所有可用命令的帮助信息";

    /// <summary>
    /// 执行 $help 命令：从命令解析器获取全部命令描述并格式化为列表输出。
    /// </summary>
    /// <param name="args">命令参数，需包含已注入的命令解析器（Parser）。</param>
    /// <param name="output">可选的输出写入器。</param>
    /// <returns>命令执行结果，输出为格式化后的命令列表文本。</returns>
    public async Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null)
    {
        // 通过 args 获取命令解析器（需在 CommandArgs 中添加 Parser 属性）
        // 假设 CommandArgs 有 ICommandParser Parser 属性
        var parser = args.Parser ?? throw new InvalidOperationException("命令解析器未注入");
        var descriptions = parser.GetCommandDescriptions();

        var sb = new StringBuilder();

        // 不存在没有可用命令的情况，因为 $help 本身就是一个命令
        sb.AppendLine("可用命令列表：");
        foreach (var kv in descriptions.OrderBy(k => k.Key))
            sb.AppendLine($"  {kv.Key}: {kv.Value}");

        output?.Write("Command", LogLevel.Success, sb.ToString());
        return new CommandResult(1, sb.ToString());
    }
}
