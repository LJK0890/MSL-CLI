namespace MSL_CLI.Core.Domain;

/// <summary>
/// 命令执行结果，包含退出码、输出文本与是否请求退出程序的标记。
/// </summary>
public class CommandResult
{
    /// <summary>
    /// 命令执行退出码，1 表示成功，0 表示失败，-1 表示未知命令。
    /// </summary>
    public int ExitCode { get; }
    /// <summary>
    /// 命令执行的输出文本。
    /// </summary>
    public string Output { get; }
    /// <summary>
    /// 是否请求退出程序。
    /// </summary>
    public bool ExitRequested { get; }

    /// <summary>
    /// 初始化命令执行结果。
    /// </summary>
    /// <param name="exitCode">命令执行退出码。</param>
    /// <param name="output">命令执行的输出文本。</param>
    /// <param name="exitRequested">是否请求退出程序。</param>
    public CommandResult(int exitCode, string output, bool exitRequested = false)
    {
        ExitCode = exitCode;
        Output = output;
        ExitRequested = exitRequested;
    }

    /// <summary>
    /// 命令是否执行成功（退出码为 1）。
    /// </summary>
    public bool IsSuccess => ExitCode == 1;
    /// <summary>
    /// 命令是否为未知命令（退出码为 -1）。
    /// </summary>
    public bool IsUnknown => ExitCode == -1;
}
