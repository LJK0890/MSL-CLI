using MSL_CLI.Core.Domain;

namespace MSL_CLI.Core.Ports;

/// <summary>
/// 命令执行器接口，定义将用户输入解析并执行得到命令结果的能力。
/// </summary>
public interface ICommandExecutor
{
    /// <summary>
    /// 异步执行用户输入的命令。
    /// </summary>
    /// <param name="input">用户输入的原始命令文本。</param>
    /// <param name="output">可选的输出写入器，用于在执行过程中输出信息；为 null 时使用默认输出。</param>
    /// <returns>命令执行结果，包含退出码与输出内容。</returns>
    Task<CommandResult> ExecuteAsync(string input, IOutputWriter? output = null);
}
