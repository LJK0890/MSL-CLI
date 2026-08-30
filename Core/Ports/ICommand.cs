using MSL_CLI.Core.Domain;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 命令接口，定义单个可执行命令的元数据与执行行为。
/// </summary>
public interface ICommand
{
    /// <summary>
    /// 命令名称，用于在命令解析器中匹配命令。
    /// </summary>
    string Name { get; }
    /// <summary>
    /// 命令的功能描述文本，用于帮助信息展示。
    /// </summary>
    string Description { get; }
    /// <summary>
    /// 异步执行命令。
    /// </summary>
    /// <param name="args">命令参数，包含原始输入及所需的各服务引用。</param>
    /// <param name="output">可选的输出写入器；为 null 时使用默认输出。</param>
    /// <returns>命令执行结果，包含退出码与输出内容。</returns>
    Task<CommandResult> ExecuteAsync(CommandArgs args, IOutputWriter? output = null);
}
